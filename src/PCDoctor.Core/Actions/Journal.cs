using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PCDoctor.Core.Actions;

public sealed record JournalEvent(long Id, DateTime Time, long OperationId, string Kind, string ActionId, string Data, string Hash);

/// <summary>Una operación del historial (inicio + resultado + eventual deshacer).</summary>
public sealed record JournalOperation(
    long OperationId, DateTime Started, string ActionId, string Title,
    Dictionary<string, string> Parameters, Dictionary<string, string?> Snapshot,
    string? ResultStatus, string? ResultMessage, bool Undone, bool Reversible);

/// <summary>
/// Diario de reparaciones: SQLite, solo agregar, eventos encadenados por hash (si alguien edita una fila,
/// <see cref="VerifyChain"/> lo detecta). Vive en el pendrive, una base por PC.
/// </summary>
public sealed class Journal : IDisposable
{
    private readonly SqliteConnection _db;

    public Journal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode=DELETE;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS events(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              ts TEXT NOT NULL,
              op INTEGER NOT NULL,
              kind TEXT NOT NULL,
              action TEXT NOT NULL,
              data TEXT NOT NULL,
              prev_hash TEXT NOT NULL,
              hash TEXT NOT NULL);
            """);
    }

    public long Begin(ActionDef action, IReadOnlyDictionary<string, string> parameters, Dictionary<string, string?> snapshot)
    {
        return Append(null, "inicio", action.Id, new
        {
            titulo = action.Title,
            parametros = parameters,
            snapshot,
            reversible = action.Reversible,
        });
    }

    public void Result(long op, string actionId, OutcomeStatus status, string message) =>
        Append(op, "resultado", actionId, new { estado = status.ToString(), mensaje = message });

    public void Undone(long op, string actionId, string message) =>
        Append(op, "deshacer", actionId, new { mensaje = message });

    public IReadOnlyList<JournalOperation> Operations()
    {
        var events = Events();
        var ops = new List<JournalOperation>();
        foreach (var g in events.GroupBy(e => e.OperationId))
        {
            var start = g.FirstOrDefault(e => e.Kind == "inicio");
            if (start is null) continue;
            using var doc = JsonDocument.Parse(start.Data);
            var root = doc.RootElement;
            var result = g.LastOrDefault(e => e.Kind == "resultado");
            string? status = null, message = null;
            if (result is not null)
            {
                using var rd = JsonDocument.Parse(result.Data);
                status = rd.RootElement.GetProperty("estado").GetString();
                message = rd.RootElement.GetProperty("mensaje").GetString();
            }
            ops.Add(new JournalOperation(
                g.Key, start.Time, start.ActionId,
                root.GetProperty("titulo").GetString() ?? start.ActionId,
                JsonSerializer.Deserialize<Dictionary<string, string>>(root.GetProperty("parametros").GetRawText()) ?? [],
                JsonSerializer.Deserialize<Dictionary<string, string?>>(root.GetProperty("snapshot").GetRawText()) ?? [],
                status, message,
                g.Any(e => e.Kind == "deshacer"),
                root.GetProperty("reversible").GetBoolean()));
        }
        return ops.OrderByDescending(o => o.OperationId).ToList();
    }

    public IReadOnlyList<JournalEvent> Events()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, ts, op, kind, action, data, hash FROM events ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<JournalEvent>();
        while (r.Read())
            list.Add(new JournalEvent(r.GetInt64(0), DateTime.Parse(r.GetString(1), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        return list;
    }

    /// <summary>Recalcula la cadena de hashes. Devuelve false si algún evento fue alterado o borrado.</summary>
    public bool VerifyChain()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT ts, op, kind, action, data, prev_hash, hash FROM events ORDER BY id";
        using var r = cmd.ExecuteReader();
        var prev = "";
        while (r.Read())
        {
            if (r.GetString(5) != prev) return false;
            var expected = Hash(prev, r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4));
            if (expected != r.GetString(6)) return false;
            prev = expected;
        }
        return true;
    }

    /// <summary>Agrega un evento. Con op = null crea una operación nueva; el número se calcula dentro de la misma
    /// transacción para que la interfaz y el agente no puedan tomar el mismo número.</summary>
    private long Append(long? opOrNew, string kind, string actionId, object data)
    {
        var ts = DateTime.Now.ToString("o");
        var json = JsonSerializer.Serialize(data);
        using var tx = _db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        long op;
        if (opOrNew is { } existing) op = existing;
        else
        {
            using var next = _db.CreateCommand();
            next.Transaction = tx;
            next.CommandText = "SELECT COALESCE(MAX(op), 0) + 1 FROM events";
            op = (long)next.ExecuteScalar()!;
        }
        var prev = LastHash(tx);
        var hash = Hash(prev, ts, op, kind, actionId, json);
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events(ts, op, kind, action, data, prev_hash, hash) VALUES ($ts, $op, $kind, $action, $data, $prev, $hash)";
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$op", op);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$action", actionId);
        cmd.Parameters.AddWithValue("$data", json);
        cmd.Parameters.AddWithValue("$prev", prev);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return op;
    }

    private string LastHash(SqliteTransaction tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT hash FROM events ORDER BY id DESC LIMIT 1";
        return cmd.ExecuteScalar() as string ?? "";
    }

    private static string Hash(string prev, string ts, long op, string kind, string action, string data) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{prev}|{ts}|{op}|{kind}|{action}|{data}")));

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}

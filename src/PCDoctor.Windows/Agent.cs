using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PCDoctor.Core.Actions;

namespace PCDoctor.Windows;

public sealed class AgentRequest
{
    public string Token { get; set; } = "";
    /// <summary>"accion", "deshacer" o "diagnosticar".</summary>
    public string Op { get; set; } = "";
    public string? Accion { get; set; }
    public Dictionary<string, string> Parametros { get; set; } = new();
    public long? Operacion { get; set; }
    public List<string>? Sondas { get; set; }
}

public sealed class AgentResponse
{
    public bool Ok { get; set; }
    public string Estado { get; set; } = "";
    public string Mensaje { get; set; } = "";
    public long? Operacion { get; set; }
    public string? Json { get; set; }
}

/// <summary>
/// Agente elevado: un proceso aparte que se abre con permiso de administrador UNA vez por sesión y atiende
/// las reparaciones que lo necesitan. Seguridad:
///   · canal local (named pipe) con nombre aleatorio y permiso solo para el usuario que abrió PC Doctor;
///   · cada pedido lleva un código secreto de 256 bits generado por la interfaz;
///   · solo ejecuta acciones del catálogo (la misma política, respaldo, verificación y diario);
///   · se cierra cuando la interfaz se cierra o se desconecta.
/// </summary>
public static class AgentServer
{
    public static async Task<int> RunAsync(DoctorHost host, string pipeName, string token, string clientSid, int parentPid, CancellationToken ct = default)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(clientSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // FirstPipeInstance: si alguien creó antes un canal con este nombre, falla en vez de compartirlo.
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = WatchParentAsync(parentPid, lifetime);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connectTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            await pipe.WaitForConnectionAsync(connectTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            return 1;
        }

        // Solo atiende a la ventana de PC Doctor que lo abrió.
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != (uint)parentPid)
            return 2;

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var expected = Encoding.UTF8.GetBytes(token);
        while (!lifetime.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(lifetime.Token); }
            catch (Exception e) when (e is IOException or OperationCanceledException) { break; }
            if (line is null) break; // la interfaz se desconectó
            AgentResponse response;
            try
            {
                var req = JsonSerializer.Deserialize<AgentRequest>(line) ?? throw new InvalidDataException("pedido vacío");
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(req.Token), expected))
                    break; // código incorrecto: se corta la conexión sin ejecutar nada
                response = await HandleAsync(host, req, lifetime.Token);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                response = new AgentResponse { Ok = false, Estado = nameof(OutcomeStatus.Failed), Mensaje = e.Message };
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        }
        return 0;
    }

    private static async Task<AgentResponse> HandleAsync(DoctorHost host, AgentRequest req, CancellationToken ct)
    {
        switch (req.Op)
        {
            case "accion":
            {
                var def = host.Actions.Get(req.Accion ?? "") ?? throw new InvalidOperationException("acción inexistente");
                // El agente solo hace lo que de verdad necesita permiso; lo demás corre sin elevar en la interfaz.
                if (!def.RequiresAdmin) throw new InvalidOperationException("esta acción no necesita permiso de administrador");
                var outcome = await host.Runner.RunAsync(def, host.ActionContext(Sanitize(req.Parametros)), host.Journal, ct);
                return From(outcome);
            }
            case "deshacer":
            {
                var op = host.Journal.Operations().FirstOrDefault(o => o.OperationId == req.Operacion) ?? throw new InvalidOperationException("operación inexistente");
                if (host.Actions.Get(op.ActionId) is not { RequiresAdmin: true }) throw new InvalidOperationException("ese cambio se deshace sin permiso de administrador");
                return From(await host.Runner.UndoAsync(op, host.Actions, host.ActionContext(), host.Journal, ct));
            }
            case "diagnosticar":
            {
                // Solo las sondas pedidas (las que necesitan permiso); las del usuario se miden en la interfaz.
                var wanted = (req.Sondas ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var probes = host.Probes.Where(p => wanted.Contains(p.Id)).ToList();
                var report = await host.Engine.RunAsync(probes, host.ProbeContext(), ct: ct);
                return new AgentResponse { Ok = true, Estado = "ok", Json = ProbeExchange.Serialize(report) };
            }
            default:
                throw new InvalidOperationException("operación desconocida");
        }
    }

    /// <summary>Parámetros que solo existen para el laboratorio (redirigir a claves o carpetas de prueba): nunca llegan elevados.</summary>
    internal static readonly string[] LabOnlyParameters = ["carpeta", "clave", "raiz"];

    internal static Dictionary<string, string> Sanitize(Dictionary<string, string> p) =>
        p.Where(kv => !LabOnlyParameters.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
         .ToDictionary(kv => kv.Key, kv => kv.Value);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    private static AgentResponse From(ActionOutcome o) =>
        new() { Ok = o.Ok, Estado = o.Status.ToString(), Mensaje = o.Message, Operacion = o.OperationId };

    private static async Task WatchParentAsync(int pid, CancellationTokenSource lifetime)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            await parent.WaitForExitAsync(lifetime.Token);
        }
        catch (ArgumentException) { /* ya no existe */ }
        catch (OperationCanceledException) { return; }
        lifetime.Cancel();
    }
}

/// <summary>Lado de la interfaz: abre el agente (un solo aviso de Windows) y le envía pedidos.</summary>
public sealed class AgentClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly string _token;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AgentClient(NamedPipeClientStream pipe, string token)
    {
        _pipe = pipe;
        _token = token;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    public bool Connected => _pipe.IsConnected;

    /// <summary>
    /// Lanza el agente con permiso de administrador. Devuelve null si la persona eligió «No» en el aviso de Windows.
    /// <paramref name="launch"/> permite al laboratorio iniciar el agente sin aviso (en el mismo proceso).
    /// </summary>
    public static async Task<AgentClient?> StartAsync(string cliPath, Func<string, string, string, int, Task>? launch = null, CancellationToken ct = default)
    {
        var pipeName = "pcdoctor-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var me = WindowsIdentity.GetCurrent();
        var sid = me.User!.Value;
        var pid = Environment.ProcessId;
        if (launch is not null)
        {
            _ = launch(pipeName, token, sid, pid);
        }
        else
        {
            var psi = new ProcessStartInfo(cliPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            psi.ArgumentList.Add("agente");
            psi.ArgumentList.Add(pipeName);
            psi.ArgumentList.Add(token);
            psi.ArgumentList.Add(sid);
            psi.ArgumentList.Add(pid.ToString());
            try { Process.Start(psi)?.Dispose(); }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return null; } // eligió «No»
        }
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await pipe.ConnectAsync(timeout.Token);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw new InvalidOperationException("El ayudante con permiso no respondió.");
        }
        return new AgentClient(pipe, token);
    }

    public async Task<AgentResponse> SendAsync(AgentRequest req, CancellationToken ct = default)
    {
        req.Token = _token;
        await _gate.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(req).AsMemory(), ct);
            var line = await _reader.ReadLineAsync(ct) ?? throw new IOException("El ayudante con permiso se cerró.");
            return JsonSerializer.Deserialize<AgentResponse>(line) ?? throw new InvalidDataException("respuesta vacía");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActionOutcome> RunActionAsync(string actionId, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var r = await SendAsync(new AgentRequest { Op = "accion", Accion = actionId, Parametros = new(parameters) }, ct);
        return new ActionOutcome(Enum.TryParse<OutcomeStatus>(r.Estado, out var s) ? s : OutcomeStatus.Failed, r.Mensaje, r.Operacion);
    }

    public async Task<ActionOutcome> UndoAsync(long operationId, CancellationToken ct = default)
    {
        var r = await SendAsync(new AgentRequest { Op = "deshacer", Operacion = operationId }, ct);
        return new ActionOutcome(Enum.TryParse<OutcomeStatus>(r.Estado, out var s) ? s : OutcomeStatus.Failed, r.Mensaje, r.Operacion);
    }

    public async Task<string> DiagnoseAsync(IEnumerable<string> probeIds, CancellationToken ct = default)
    {
        var r = await SendAsync(new AgentRequest { Op = "diagnosticar", Sondas = probeIds.ToList() }, ct);
        return r.Ok && r.Json is { } json ? json : throw new InvalidOperationException(r.Mensaje);
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync();
        _gate.Dispose();
    }
}

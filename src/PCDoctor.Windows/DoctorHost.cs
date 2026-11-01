using System.Text.Json;
using PCDoctor.Core;
using PCDoctor.Core.Actions;
using PCDoctor.Core.Diagnostics;
using PCDoctor.Core.Knowledge;

namespace PCDoctor.Windows;

/// <summary>Arma todo lo que necesita una sesión de PC Doctor (interfaz o línea de comandos).</summary>
public sealed class DoctorHost : IDisposable
{
    public ContentPaths Paths { get; }
    public KnowledgeBase Knowledge { get; }
    public IReadOnlyList<IProbe> Probes { get; }
    public ActionCatalog Actions { get; }
    public DiagnosticEngine Engine { get; }
    public SymptomMatcher Matcher { get; }
    public ActionRunner Runner { get; }
    public bool IsElevated { get; }
    public string MachineId { get; }
    public string MachineDir { get; }

    private Journal? _journal;
    public Journal Journal => _journal ??= new Journal(Path.Combine(MachineDir, "historial.db"));

    public DoctorHost(string? baseDir = null)
    {
        Paths = ContentPaths.Discover(baseDir);
        Knowledge = KnowledgeBase.Load(Paths.ProblemsDir);
        Probes = WindowsPlatform.Probes();
        Actions = WindowsPlatform.Actions();
        Engine = new DiagnosticEngine(Knowledge, Probes);
        Matcher = new SymptomMatcher(Knowledge);
        // Versión 0.x: se habilitan reparaciones T1 (con verificación y etiqueta "En prueba"). En 1.0 pasa a T2.
        Runner = new ActionRunner(ActionPolicy.Prototype);
        IsElevated = WindowsEnvironment.IsElevated();
        MachineId = WindowsEnvironment.MachineId();
        MachineDir = Paths.MachineDir(MachineId);
    }

    public ProbeContext ProbeContext() => new() { IsElevated = IsElevated, Paths = Paths };

    public ActionContext ActionContext(IReadOnlyDictionary<string, string>? parameters = null) =>
        new() { IsElevated = IsElevated, Parameters = parameters ?? new Dictionary<string, string>() };

    public string ReportsDir
    {
        get
        {
            var d = Path.Combine(MachineDir, "informes");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public void Dispose() => _journal?.Dispose();
}

/// <summary>Resultado de un análisis serializable: lo usa el proceso elevado para devolverle los datos a la interfaz.</summary>
public sealed class ProbeExchange
{
    public Dictionary<string, JsonElement> Facts { get; set; } = new();
    public List<ProbeDto> Probes { get; set; } = new();

    public sealed class ProbeDto
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public List<DetailItem> Details { get; set; } = new();
        public List<string> Unverified { get; set; } = new();
        public string? Error { get; set; }
        public double Seconds { get; set; }
    }

    public static string Serialize(DiagnosticReport report)
    {
        var dto = new ProbeExchange
        {
            Facts = report.Facts.All.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
            Probes = report.Probes.Select(p => new ProbeDto
            {
                Id = p.ProbeId, Title = p.Title, Details = p.Details, Unverified = p.Unverified, Error = p.Error, Seconds = p.Duration.TotalSeconds,
            }).ToList(),
        };
        return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reconstruye los resultados de las sondas a partir del JSON del proceso elevado.</summary>
    public static List<ProbeResult> Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<ProbeExchange>(json) ?? throw new InvalidDataException("respuesta vacía");
        var results = new List<ProbeResult>();
        foreach (var p in dto.Probes)
        {
            var r = new ProbeResult { ProbeId = p.Id, Title = p.Title, Error = p.Error, Duration = TimeSpan.FromSeconds(p.Seconds) };
            r.Details.AddRange(p.Details);
            r.Unverified.AddRange(p.Unverified);
            results.Add(r);
        }
        // Los hechos van todos a la primera sonda: al combinar, solo importa el conjunto.
        if (results.Count > 0)
            foreach (var (k, v) in dto.Facts)
                results[0].Facts.Set(k, v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => v.GetDouble(),
                    JsonValueKind.String => v.GetString(),
                    _ => null,
                });
        return results;
    }
}

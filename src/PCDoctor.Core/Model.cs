using System.Globalization;

namespace PCDoctor.Core;

/// <summary>Los cinco estados que puede tener cualquier resultado. El orden es de menor a mayor gravedad.</summary>
public enum HealthStatus
{
    Ok = 0,
    Unverified = 1,
    Warning = 2,
    NeedsPhysicalCheck = 3,
    Problem = 4,
}

public enum FindingKind { Issue, Suggestion }

public enum Risk { Green, Yellow, Red }

/// <summary>Madurez de una reparación: T0 documentada, T1 lógica probada, T2 probada en laboratorio Win10+Win11, T3 además en hardware real.</summary>
public enum Maturity { T0 = 0, T1 = 1, T2 = 2, T3 = 3 }

public static class Labels
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-AR");

    public static string Status(HealthStatus s) => s switch
    {
        HealthStatus.Ok => "Correcto",
        HealthStatus.Warning => "Advertencia",
        HealthStatus.Problem => "Problema detectado",
        HealthStatus.NeedsPhysicalCheck => "Requiere revisión física",
        HealthStatus.Unverified => "No se pudo comprobar",
        _ => s.ToString(),
    };

    public static string Risk(Core.Risk r) => r switch
    {
        Core.Risk.Green => "Sin riesgo",
        Core.Risk.Yellow => "Cambio moderado (te pregunto antes)",
        Core.Risk.Red => "Cambio importante",
        _ => r.ToString(),
    };

    public static HealthStatus ParseStatus(string s) => s.Trim().ToLowerInvariant() switch
    {
        "correcto" => HealthStatus.Ok,
        "advertencia" => HealthStatus.Warning,
        "problema" => HealthStatus.Problem,
        "revision_fisica" => HealthStatus.NeedsPhysicalCheck,
        "no_comprobado" => HealthStatus.Unverified,
        _ => throw new FormatException($"Estado desconocido: '{s}'"),
    };

    public static string Number(double d) =>
        Math.Abs(d - Math.Round(d)) < 1e-9 ? ((long)Math.Round(d)).ToString(Culture) : d.ToString("0.#", Culture);
}

/// <summary>Hechos medidos en la PC. Un hecho ausente significa "no se sabe" (no "falso").</summary>
public sealed class FactStore
{
    private readonly Dictionary<string, object> _facts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, object> All => _facts;

    public void Set(string key, object? value)
    {
        var normalized = Normalize(value);
        if (normalized is null) _facts.Remove(key);
        else _facts[key] = normalized;
    }

    public bool TryGet(string key, out object value) => _facts.TryGetValue(key, out value!);

    public bool Has(string key) => _facts.ContainsKey(key);

    public void Merge(FactStore other)
    {
        foreach (var (k, v) in other._facts) _facts[k] = v;
    }

    public static object? Normalize(object? value) => value switch
    {
        null => null,
        bool b => b,
        string s => s,
        double d => double.IsNaN(d) ? null : d,
        float f => (double)f,
        int i => (double)i,
        long l => (double)l,
        uint u => (double)u,
        ulong ul => (double)ul,
        short sh => (double)sh,
        ushort us => (double)us,
        byte by => (double)by,
        decimal m => (double)m,
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IEnumerable<string> list => string.Join(", ", list),
        _ => value.ToString(),
    };

    public static string Format(object value) => value switch
    {
        bool b => b ? "sí" : "no",
        double d => Labels.Number(d),
        _ => value.ToString() ?? "",
    };
}

public sealed record DetailItem(string Label, string Value);

public sealed class ProbeResult
{
    public required string ProbeId { get; init; }
    public required string Title { get; init; }
    public FactStore Facts { get; } = new();
    public List<DetailItem> Details { get; } = new();
    /// <summary>Comprobaciones que no se pudieron hacer y por qué (por ejemplo: requiere administrador).</summary>
    public List<string> Unverified { get; } = new();
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }

    public void Detail(string label, object? value)
    {
        if (value is null) return;
        var v = FactStore.Normalize(value);
        if (v is null) return;
        Details.Add(new DetailItem(label, FactStore.Format(v)));
    }
}

public sealed class ProbeContext
{
    public bool IsElevated { get; init; }
    public required ContentPaths Paths { get; init; }
}

public interface IProbe
{
    string Id { get; }
    string Title { get; }
    /// <summary>Categorías de la pantalla principal en las que participa esta sonda.</summary>
    IReadOnlyList<string> Areas { get; }
    /// <summary>Todos los hechos que esta sonda puede producir (se usa para validar la biblioteca).</summary>
    IReadOnlyList<string> FactKeys { get; }
    /// <summary>Las sondas que miden uso (CPU) corren solas antes que el resto, para no medir el propio análisis.</summary>
    bool RunsAlone => false;
    Task RunAsync(ProbeContext ctx, ProbeResult result, CancellationToken ct);
}

public static class Areas
{
    public const string Hardware = "hardware";
    public const string Windows = "windows";
    public const string Network = "red";
    public const string Security = "seguridad";
    public const string Performance = "rendimiento";

    public static readonly IReadOnlyList<string> All = [Hardware, Windows, Network, Security, Performance];

    public static string Title(string area) => area switch
    {
        Hardware => "Revisar hardware",
        Windows => "Revisar Windows",
        Network => "Internet y redes",
        Security => "Seguridad y virus",
        Performance => "Rendimiento",
        _ => area,
    };
}

using System.Text.RegularExpressions;
using PCDoctor.Core.Actions;
using PCDoctor.Core.Rules;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PCDoctor.Core.Knowledge;

public sealed class TextPair
{
    public string Sencillo { get; set; } = "";
    public string Tecnico { get; set; } = "";
    public string For(bool technical) => technical && Tecnico.Length > 0 ? Tecnico : Sencillo;
}

public sealed class DetectionRule
{
    public string Si { get; set; } = "";
    public string Estado { get; set; } = "";
    public string Mensaje { get; set; } = "";
    public string? MensajeTecnico { get; set; }

    [YamlIgnore] public Expr? Compiled { get; internal set; }
    [YamlIgnore] public HealthStatus Status { get; internal set; }
}

public sealed class GuidedStep
{
    public string Texto { get; set; } = "";
    /// <summary>Configuración de Windows (ms-settings:) o página oficial (https) que el paso permite abrir.</summary>
    public string? Abrir { get; set; }
}

/// <summary>Una ficha de la biblioteca. Se escribe en YAML (una por archivo) y la valida el compilador.</summary>
public sealed class ProblemDef
{
    public string Id { get; set; } = "";
    public string Area { get; set; } = "";
    public string Tipo { get; set; } = "problema";
    public TextPair Titulo { get; set; } = new();
    public TextPair Explicacion { get; set; } = new();
    public List<string> Frases { get; set; } = [];
    public List<string> Sondas { get; set; } = [];
    public List<DetectionRule> Deteccion { get; set; } = [];
    public string? SiNoSePuede { get; set; }
    public List<string> Acciones { get; set; } = [];
    public List<GuidedStep> Pasos { get; set; } = [];
    public string? Derivar { get; set; }
    public List<string> Fuentes { get; set; } = [];
    public string Madurez { get; set; } = "T0";

    [YamlIgnore] public string SourceFile { get; internal set; } = "";
    [YamlIgnore] public FindingKind Kind => Tipo == "sugerencia" ? FindingKind.Suggestion : FindingKind.Issue;
    [YamlIgnore] public bool SymptomOnly => Deteccion.Count == 0;
}

public sealed class KnowledgeBase
{
    public static readonly IReadOnlySet<string> KnownAreas = new HashSet<string>
    {
        "hardware", "windows", "red", "seguridad", "rendimiento", "programas",
        "perifericos", "archivos", "audio", "basico", "juegos",
    };

    /// <summary>Únicos destinos que un paso guiado puede abrir.</summary>
    public static readonly IReadOnlyList<string> AllowedUrlPrefixes =
    [
        "ms-settings:",
        "https://support.microsoft.com/",
        "https://learn.microsoft.com/",
        "https://account.microsoft.com/",
        "https://www.microsoft.com/",
        "https://www.nomoreransom.org/",
        // Ayuda oficial de otros fabricantes, solo para lo que no es de Microsoft (Chrome, Android, iPhone, WhatsApp).
        "https://support.google.com/",
        "https://support.apple.com/",
        "https://faq.whatsapp.com/",
    ];

    private static readonly Regex IdPattern = new(@"^[A-Z]{2,6}(-[A-Z0-9]{1,6})+$", RegexOptions.Compiled);

    public IReadOnlyList<ProblemDef> Problems { get; }
    public IReadOnlyList<string> Errors { get; }

    private readonly Dictionary<string, ProblemDef> _byId;

    private KnowledgeBase(List<ProblemDef> problems, List<string> errors)
    {
        Problems = problems;
        Errors = errors;
        _byId = problems.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
    }

    public ProblemDef? Get(string id) => _byId.GetValueOrDefault(id);

    public static KnowledgeBase Load(string problemsDir)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var problems = new List<ProblemDef>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(problemsDir, "*.yaml", SearchOption.AllDirectories).Order())
        {
            var name = Path.GetFileName(file);
            List<ProblemDef> docs;
            try
            {
                docs = ReadDocuments(deserializer, File.ReadAllText(file));
            }
            catch (Exception e)
            {
                errors.Add($"{name}: no se pudo leer ({e.Message})");
                continue;
            }
            foreach (var p in docs)
            {
                p.SourceFile = name;
                var problemErrors = Validate(p);
                if (!seen.Add(p.Id)) problemErrors.Add("ID repetido");
                if (problemErrors.Count > 0)
                {
                    errors.AddRange(problemErrors.Select(e => $"{name} [{p.Id}]: {e}"));
                    continue;
                }
                problems.Add(p);
            }
        }
        return new KnowledgeBase(problems, errors);
    }

    /// <summary>Un archivo puede tener varias fichas separadas por "---".</summary>
    private static List<ProblemDef> ReadDocuments(IDeserializer deserializer, string yaml)
    {
        var parser = new YamlDotNet.Core.Parser(new StringReader(yaml));
        parser.Consume<YamlDotNet.Core.Events.StreamStart>();
        var list = new List<ProblemDef>();
        while (parser.Accept<YamlDotNet.Core.Events.DocumentStart>(out _))
            if (deserializer.Deserialize<ProblemDef?>(parser) is { } p) list.Add(p); // un «---» suelto no rompe nada
        return list;
    }

    private static List<string> Validate(ProblemDef p)
    {
        var errors = new List<string>();
        if (!IdPattern.IsMatch(p.Id)) errors.Add($"ID con formato inválido: '{p.Id}'");
        if (!KnownAreas.Contains(p.Area)) errors.Add($"área desconocida: '{p.Area}'");
        if (p.Tipo is not ("problema" or "sugerencia")) errors.Add($"tipo inválido: '{p.Tipo}'");
        if (string.IsNullOrWhiteSpace(p.Titulo.Sencillo)) errors.Add("falta titulo.sencillo");
        if (string.IsNullOrWhiteSpace(p.Explicacion.Sencillo)) errors.Add("falta explicacion.sencillo");
        if (p.Fuentes.Count == 0) errors.Add("falta al menos una fuente verificable");
        if (!Enum.TryParse<Maturity>(p.Madurez, out _)) errors.Add($"madurez inválida: '{p.Madurez}'");
        if (p.SymptomOnly && p.Frases.Count < 3) errors.Add("una ficha sin detección automática necesita al menos 3 frases");
        if (!p.SymptomOnly && p.Sondas.Count == 0) errors.Add("tiene detección pero no declara sondas");
        if (p.Acciones.Count == 0 && p.Pasos.Count == 0 && string.IsNullOrWhiteSpace(p.Derivar))
            errors.Add("no ofrece ninguna salida (acciones, pasos o derivación)");

        foreach (var r in p.Deteccion)
        {
            try
            {
                r.Compiled = Expr.Parse(r.Si);
                r.Status = Labels.ParseStatus(r.Estado);
            }
            catch (Exception e) when (e is RuleSyntaxException or FormatException)
            {
                errors.Add(e.Message);
            }
            if (string.IsNullOrWhiteSpace(r.Mensaje)) errors.Add($"la regla '{r.Si}' no tiene mensaje");
        }
        foreach (var step in p.Pasos)
        {
            if (string.IsNullOrWhiteSpace(step.Texto)) errors.Add("paso guiado vacío");
            if (step.Abrir is { } url && !AllowedUrlPrefixes.Any(prefix => url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"destino no permitido en un paso: '{url}'");
        }
        foreach (var url in p.Fuentes)
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) errors.Add($"fuente no verificable: '{url}'");
        return errors;
    }

    /// <summary>Valida la biblioteca contra las sondas y acciones reales: ningún hecho, sonda o acción inexistente.</summary>
    public List<string> ValidateAgainst(IEnumerable<IProbe> probes, ActionCatalog catalog)
    {
        var probeMap = probes.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>(Errors);
        foreach (var p in Problems)
        {
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in p.Sondas)
            {
                if (probeMap.TryGetValue(s, out var probe)) available.UnionWith(probe.FactKeys);
                else errors.Add($"{p.SourceFile} [{p.Id}]: sonda inexistente '{s}'");
            }
            var refs = p.Deteccion.SelectMany(r =>
                    (r.Compiled?.References() ?? []).Concat(Template.References(r.Mensaje))
                        .Concat(Template.References(r.MensajeTecnico ?? "")))
                .Concat(Template.References(p.SiNoSePuede ?? ""));
            foreach (var fact in refs.Distinct())
                if (!available.Contains(fact))
                    errors.Add($"{p.SourceFile} [{p.Id}]: usa el hecho '{fact}' que ninguna de sus sondas produce");
            foreach (var a in p.Acciones)
                if (catalog.Get(a) is null) errors.Add($"{p.SourceFile} [{p.Id}]: acción inexistente '{a}'");
        }
        return errors;
    }
}

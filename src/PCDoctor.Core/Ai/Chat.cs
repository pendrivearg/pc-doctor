using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PCDoctor.Core.Diagnostics;
using PCDoctor.Core.Knowledge;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PCDoctor.Core.Ai;

public sealed class ModelInfo
{
    public string Id { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Archivo { get; set; } = "";
    public double RamGb { get; set; }
    public string Licencia { get; set; } = "";
    public string Origen { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class EngineInfo
{
    public string Carpeta { get; set; } = "";
    public string Ejecutable { get; set; } = "";
    public string Version { get; set; } = "";
    public string Licencia { get; set; } = "";
    public bool Verificado { get; set; }
}

public sealed class ModelManifest
{
    public EngineInfo Motor { get; set; } = new();
    public List<ModelInfo> Modelos { get; set; } = [];

    public static ModelManifest Load(string dataDir) =>
        new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
            .Deserialize<ModelManifest>(File.ReadAllText(Path.Combine(dataDir, "modelos.yaml")));
}

/// <summary>Resultado de elegir el modelo para esta PC: cuál, o por qué ninguno.</summary>
public sealed record ModelChoice(ModelInfo? Model, string Reason);

/// <summary>
/// Elige el modelo según la memoria REALMENTE libre (no la instalada), dejando un margen para que la PC
/// no se ponga lenta: margen = el mayor entre 1 GB y el 15 % de la RAM total.
/// </summary>
public static class ModelSelector
{
    public static ModelChoice Choose(IEnumerable<ModelInfo> installed, double availableGb, double totalGb, bool avx2)
    {
        var list = installed.OrderBy(m => m.RamGb).ToList();
        if (list.Count == 0) return new(null, "no hay modelos de IA en el pendrive");
        var budget = availableGb - Math.Max(1.0, totalGb * 0.15);
        // Sin AVX2 la IA es mucho más lenta: no se usan modelos grandes.
        if (!avx2) list = list.Where(m => m.RamGb <= 2.0).ToList();
        var fit = list.LastOrDefault(m => m.RamGb <= budget);
        return fit is null
            ? new(null, $"la PC tiene {Labels.Number(Math.Round(availableGb, 1))} GB de memoria libre y la IA más chica necesita {Labels.Number(list[0].RamGb + Math.Max(1.0, totalGb * 0.15))} GB")
            : new(fit, $"memoria libre {Labels.Number(Math.Round(availableGb, 1))} GB");
    }
}

public sealed record ChatMessage(string Role, string Content);

/// <summary>Un modelo de lenguaje que devuelve JSON que cumple un esquema (lo impone el motor con una gramática).</summary>
public interface IChatModel
{
    string Name { get; }
    Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, JsonObject schema, CancellationToken ct);
}

/// <summary>Lo que el asistente le dice a la persona en un turno, más lo que el sistema comprobó.</summary>
public sealed class AssistantTurn
{
    public required string Text { get; init; }
    public string? Question { get; init; }
    public IReadOnlyList<ProblemDef> Problems { get; init; } = [];
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    public IReadOnlyList<string> CheckedProbes { get; init; } = [];
    public bool UsedAi { get; init; }
    /// <summary>Si el modelo dijo algo que no correspondía, se avisa en modo técnico.</summary>
    public IReadOnlyList<string> GuardNotes { get; init; } = [];
}

/// <summary>
/// Conversación con PC Doctor. El modelo solo interpreta y explica: elige fichas y revisiones que existen
/// (enumeradas en el esquema), nunca ejecuta nada y no puede anunciar arreglos. Si no hay modelo,
/// responde con el intérprete de reglas.
/// </summary>
public sealed class ChatSession(KnowledgeBase kb, SymptomMatcher matcher, DiagnosticEngine engine, Func<ProbeContext> probeContext, IChatModel? model, bool hybrid = true)
{
    /// <summary>Puntaje del intérprete de reglas a partir del cual su elección se considera segura.</summary>
    public const double ConfidentScore = 0.55;

    private readonly List<ChatMessage> _history = [];
    private readonly HashSet<string> _ranProbes = new(StringComparer.OrdinalIgnoreCase);
    private readonly FactStore _facts = new();
    private readonly List<ProblemDef> _context = [];

    public IChatModel? Model => model;

    private static readonly Regex ClaimsFixed = new(
        @"[^.!?\n]*\b(solucion(é|e|ado|ada|amos)|arregl(é|e|ado|ada|amos)|repar(é|e|ado|ada|amos)|ya (está|esta|quedó|quedo) (bien|listo|resuelto)|listo,? ya)\b[^.!?\n]*[.!?]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<AssistantTurn> SendAsync(string text, CancellationToken ct = default)
    {
        var scored = matcher.Match(text, 5);
        var matches = scored.Select(m => m.Problem).ToList();
        foreach (var m in matches.Where(m => !_context.Contains(m))) _context.Add(m);

        if (model is null) return await RulesTurnAsync(matches, ct);

        AssistantTurn turn;
        try
        {
            turn = await AiTurnAsync(text, matches, scored.Where(s => s.Score >= ConfidentScore).Select(s => s.Problem).Take(1).ToList(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Error("asistente IA", e);
            var fallback = await RulesTurnAsync(matches, ct);
            return new AssistantTurn
            {
                Text = "(El asistente con IA no respondió a tiempo; te contesto con lo que sé.) " + fallback.Text,
                Problems = fallback.Problems, Findings = fallback.Findings, CheckedProbes = fallback.CheckedProbes,
            };
        }
        _history.Add(new ChatMessage("user", text));
        _history.Add(new ChatMessage("assistant", turn.Text + (turn.Question is { } q ? " " + q : "")));
        return turn;
    }

    private async Task<AssistantTurn> RulesTurnAsync(IReadOnlyList<ProblemDef> matches, CancellationToken ct)
    {
        if (matches.Count == 0)
            return new AssistantTurn
            {
                Text = "No estoy seguro de haber entendido.",
                Question = "¿Me lo contás con otras palabras? Por ejemplo: «no tengo sonido», «no me anda internet» o «la compu está lenta».",
            };
        var findings = await CheckAsync(matches.SelectMany(m => m.Sondas), matches, ct);
        return new AssistantTurn
        {
            Text = findings.Any(f => !f.FromSymptom)
                ? "Revisé la computadora. Esto es lo que encontré relacionado con lo que me contaste:"
                : "Revisé lo relacionado con lo que me contaste. Te dejo los pasos para solucionarlo:",
            Problems = matches,
            Findings = findings,
            CheckedProbes = matches.SelectMany(m => m.Sondas).Distinct().ToList(),
        };
    }

    private async Task<AssistantTurn> AiTurnAsync(string text, IReadOnlyList<ProblemDef> matches, IReadOnlyList<ProblemDef> confident, CancellationToken ct)
    {
        var probes = engine.Probes;
        var schema = Schema(kb.Problems.Select(p => p.Id), probes.Select(p => p.Id));
        var messages = new List<ChatMessage> { new("system", SystemPrompt) };
        messages.AddRange(_history.TakeLast(6));
        messages.Add(new ChatMessage("user", BuildUserMessage(text, matches)));
        var json = await model!.CompleteJsonAsync(messages, schema, ct);

        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("respuesta sin JSON");
        var notes = new List<string>();
        var answer = (root["respuesta"]?.GetValue<string>() ?? "").Trim();
        var question = root["pregunta"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(question)) question = null;
        var chosen = (root["fichas"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>()
            .Select(kb.Get).OfType<ProblemDef>().Distinct().Take(4).ToList();
        var toCheck = (root["revisar"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>()
            .Where(id => probes.Any(p => p.Id == id)).ToList();

        // Guarda: el modelo no puede anunciar arreglos (eso lo dice el sistema tras verificar).
        var cleaned = ClaimsFixed.Replace(answer, "").Trim();
        if (cleaned != answer) notes.Add("se quitó una frase que daba algo por solucionado sin verificación");
        answer = cleaned.Length > 0 ? cleaned : "Te muestro lo que encontré.";
        if (answer.Length > 700) answer = answer[..700].TrimEnd() + "…";

        // Modo combinado: si las reglas están seguras, su ficha va primero y no hace falta repreguntar.
        if (hybrid && confident.Count > 0)
        {
            foreach (var c in confident.Where(c => !chosen.Contains(c))) chosen.Insert(0, c);
            question = null;
        }
        // Si el modelo no eligió fichas pero el intérprete de reglas sí, se usan esas (nunca se queda sin ayuda).
        if (chosen.Count == 0 && question is null) chosen = matches.Take(2).ToList();
        var findings = await CheckAsync(chosen.SelectMany(p => p.Sondas).Concat(toCheck), chosen, ct);
        return new AssistantTurn
        {
            Text = answer,
            Question = question,
            Problems = chosen,
            Findings = findings,
            CheckedProbes = chosen.SelectMany(p => p.Sondas).Concat(toCheck).Distinct().ToList(),
            UsedAi = true,
            GuardNotes = notes,
        };
    }

    /// <summary>Corre las sondas que falten y devuelve los hallazgos de las fichas pedidas (y los graves de lo revisado).</summary>
    private async Task<List<Finding>> CheckAsync(IEnumerable<string> probeIds, IReadOnlyList<ProblemDef> problems, CancellationToken ct)
    {
        var missing = probeIds.Distinct(StringComparer.OrdinalIgnoreCase).Where(id => !_ranProbes.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            var selected = engine.Probes.Where(p => missing.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            var report = await engine.RunAsync(selected, probeContext(), ct: ct);
            _facts.Merge(report.Facts);
            foreach (var p in report.Probes.Where(p => p.Error is null)) _ranProbes.Add(p.ProbeId);
        }
        var all = DiagnosticEngine.Evaluate(kb, _facts, _ranProbes, problems);
        var wanted = problems.Select(p => p.Id).ToHashSet();
        var areas = problems.SelectMany(p => p.Sondas).ToHashSet();
        return all.Where(f => wanted.Contains(f.Problem.Id)
                              || (f.Status >= HealthStatus.Warning && f.Problem.Sondas.Any(areas.Contains)))
                  .Take(6).ToList();
    }

    internal const string SystemPrompt = """
        Sos PC Doctor, un asistente que ayuda a personas sin conocimientos de computación a resolver problemas de su PC con Windows.
        Hablás en español rioplatense, con frases cortas, cálidas y sin tecnicismos.
        Reglas obligatorias:
        1. Usá solo la información de DATOS DE LA PC y FICHAS POSIBLES. Si no sabés algo, decilo. Nunca inventes datos.
        2. Nunca digas que algo quedó solucionado o arreglado: eso lo informa el sistema después de comprobarlo.
        3. No des comandos para escribir en consola ni instrucciones peligrosas. Los pasos y botones los muestra el sistema.
        4. En "fichas" poné los id de las FICHAS POSIBLES que correspondan a lo que cuenta la persona (de 0 a 3).
        5. En "revisar" poné los id de SONDAS que haga falta revisar en la PC, si las hay.
        6. Si una de las FICHAS POSIBLES coincide con lo que cuenta, elegila sin preguntar. Solo si de verdad no queda claro qué le pasa, hacé UNA pregunta simple en "pregunta" y dejá "fichas" vacío.
        7. "respuesta" es lo que le decís a la persona: máximo 3 oraciones.
        """;

    private string BuildUserMessage(string text, IReadOnlyList<ProblemDef> matches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FICHAS POSIBLES:");
        foreach (var p in matches.Concat(_context).Distinct().Take(8))
            sb.AppendLine($"- {p.Id}: {p.Titulo.Sencillo}. {Short(p.Explicacion.Sencillo, 140)} Ejemplos: {string.Join(" / ", p.Frases.Take(2).Select(f => $"«{f}»"))}");
        if (matches.Count == 0 && _context.Count == 0) sb.AppendLine("- (ninguna coincide claramente)");
        sb.AppendLine("SONDAS:");
        foreach (var p in engine.Probes) sb.AppendLine($"- {p.Id}: {p.Title}");
        sb.AppendLine("DATOS DE LA PC:");
        var findings = DiagnosticEngine.Evaluate(kb, _facts, _ranProbes);
        if (_ranProbes.Count == 0) sb.AppendLine("- todavía no se revisó nada");
        else if (findings.Count == 0) sb.AppendLine($"- se revisó ({string.Join(", ", _ranProbes)}) y no se encontraron problemas");
        foreach (var f in findings.Take(8)) sb.AppendLine($"- {f.Problem.Id} [{Labels.Status(f.Status)}]: {f.Message}");
        sb.AppendLine("MENSAJE DE LA PERSONA:");
        sb.AppendLine(text);
        return sb.ToString();
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    internal static JsonObject Schema(IEnumerable<string> problemIds, IEnumerable<string> probeIds)
    {
        JsonArray Enum(IEnumerable<string> ids) => new(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["respuesta"] = new JsonObject { ["type"] = "string" },
                ["pregunta"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                ["fichas"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["enum"] = Enum(problemIds) }, ["maxItems"] = 3 },
                ["revisar"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["enum"] = Enum(probeIds) }, ["maxItems"] = 4 },
            },
            ["required"] = new JsonArray("respuesta", "pregunta", "fichas", "revisar"),
            ["additionalProperties"] = false,
        };
    }
}

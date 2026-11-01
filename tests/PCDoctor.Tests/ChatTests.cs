using System.Text.Json.Nodes;
using PCDoctor.Core;
using PCDoctor.Core.Ai;
using PCDoctor.Core.Diagnostics;
using PCDoctor.Windows;

namespace PCDoctor.Tests;

/// <summary>Modelo de mentira: devuelve lo que la prueba le indica, para comprobar las guardas sin depender de la IA real.</summary>
internal sealed class FakeModel(Func<IReadOnlyList<ChatMessage>, string> answer) : IChatModel
{
    public string Name => "modelo de prueba";
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, JsonObject schema, CancellationToken ct)
    {
        Calls.Add(messages);
        return Task.FromResult(answer(messages));
    }
}

/// <summary>Una sonda de mentira para no medir la PC real en estas pruebas.</summary>
internal sealed class FakeAudioProbe : IProbe
{
    public string Id => "servicios";
    public string Title => "Servicios";
    public IReadOnlyList<string> Areas => ["windows"];
    public IReadOnlyList<string> FactKeys => ["servicios.audio_ejecutando", "servicios.audio_deshabilitado"];
    public Task RunAsync(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        r.Facts.Set("servicios.audio_ejecutando", false);
        r.Facts.Set("servicios.audio_deshabilitado", false);
        return Task.CompletedTask;
    }
}

public class ChatTests
{
    private static ChatSession Session(IChatModel? model, params IProbe[] probes)
    {
        var kb = Repo.Kb;
        var engine = new DiagnosticEngine(kb, probes.Length > 0 ? probes : [new FakeAudioProbe()]);
        return new ChatSession(kb, new Core.Knowledge.SymptomMatcher(kb), engine,
            () => new ProbeContext { Paths = ContentPaths.Discover(Repo.Root) }, model);
    }

    [Fact]
    public async Task WithoutAiTheChatStillFindsTheProblemAndChecksThePc()
    {
        var turn = await Session(null).SendAsync("no tengo sonido");
        Assert.False(turn.UsedAi);
        Assert.Contains(turn.Problems, p => p.Id == "AUD-001");
        Assert.Contains(turn.Findings, f => f.Problem.Id == "AUD-002"); // el servicio de audio apagado, medido
    }

    [Fact]
    public async Task WithoutAiUnknownTextGetsAQuestionNotAnInvention()
    {
        var turn = await Session(null).SendAsync("xyzzy");
        Assert.NotNull(turn.Question);
        Assert.Empty(turn.Problems);
        Assert.Empty(turn.Findings);
    }

    [Fact]
    public async Task AiChoicesAreUsedAndTheFindingsComeFromRealChecks()
    {
        var model = new FakeModel(_ => """{"respuesta":"Puede ser el servicio de sonido.","pregunta":null,"fichas":["AUD-001","AUD-002"],"revisar":["servicios"]}""");
        var turn = await Session(model).SendAsync("no se escucha nada");
        Assert.True(turn.UsedAi);
        Assert.Equal(["AUD-001", "AUD-002"], turn.Problems.Select(p => p.Id));
        Assert.Contains(turn.Findings, f => f.Problem.Id == "AUD-002" && f.Status == HealthStatus.Problem);
        // El modelo recibió las fichas posibles y las reglas.
        Assert.Contains(model.Calls[0], m => m.Role == "system" && m.Content.Contains("Nunca inventes"));
        Assert.Contains(model.Calls[0], m => m.Role == "user" && m.Content.Contains("AUD-001"));
    }

    [Fact]
    public async Task AiCannotClaimItFixedAnything()
    {
        var model = new FakeModel(_ => """{"respuesta":"Ya solucioné el problema del sonido. Probá ahora.","pregunta":null,"fichas":["AUD-001"],"revisar":[]}""");
        var turn = await Session(model).SendAsync("no tengo sonido");
        Assert.DoesNotContain("solucion", turn.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(turn.GuardNotes);
    }

    [Fact]
    public async Task UnknownIdsFromTheModelAreIgnored()
    {
        var model = new FakeModel(_ => """{"respuesta":"Revisemos.","pregunta":null,"fichas":["NO-EXISTE-1"],"revisar":["borrar_todo"]}""");
        var turn = await Session(model).SendAsync("no tengo sonido");
        Assert.DoesNotContain(turn.Problems, p => p.Id == "NO-EXISTE-1");
        Assert.DoesNotContain("borrar_todo", turn.CheckedProbes);
    }

    [Fact]
    public async Task BrokenModelOutputFallsBackToRulesHonestly()
    {
        var model = new FakeModel(_ => "esto no es JSON");
        var turn = await Session(model).SendAsync("no tengo sonido");
        Assert.False(turn.UsedAi);
        Assert.Contains("no respondió", turn.Text);
        Assert.Contains(turn.Problems, p => p.Id == "AUD-001");
    }

    [Fact]
    public async Task ClarifyingQuestionIsPassedThrough()
    {
        var model = new FakeModel(_ => """{"respuesta":"Quiero entender bien.","pregunta":"¿No suena nada o solo un programa?","fichas":[],"revisar":[]}""");
        var turn = await Session(model).SendAsync("tengo una duda");
        Assert.Equal("¿No suena nada o solo un programa?", turn.Question);
        Assert.Empty(turn.Problems);
    }

    [Fact]
    public async Task WhenRulesAreConfidentTheirChoiceWinsOverAQuestion()
    {
        var model = new FakeModel(_ => """{"respuesta":"Quiero entender bien.","pregunta":"¿Qué pasa exactamente?","fichas":[],"revisar":[]}""");
        var turn = await Session(model).SendAsync("no tengo sonido");
        Assert.Null(turn.Question);
        Assert.Contains(turn.Problems[0].Id, new[] { "AUD-001", "AUD-002" });
    }

    [Fact]
    public void SchemaOnlyAllowsRealIds()
    {
        var schema = ChatSession.Schema(["AUD-001"], ["red"]);
        var json = schema.ToJsonString();
        Assert.Contains("\"enum\":[\"AUD-001\"]", json);
        Assert.Contains("\"enum\":[\"red\"]", json);
        Assert.Contains("\"additionalProperties\":false", json);
    }

    [Theory]
    [InlineData(1.5, 4, true, null)]           // PC de 4 GB con poca memoria libre: sin IA, sigue con reglas
    [InlineData(2.2, 4, true, "qwen3.5-0.8b")]
    [InlineData(3.5, 8, true, "qwen3.5-2b")]
    [InlineData(10, 16, true, "qwen3.5-4b")]
    [InlineData(10, 16, false, "qwen3.5-2b")]  // sin AVX2: no se usan modelos grandes
    public void ModelChoiceFollowsRealFreeMemory(double freeGb, double totalGb, bool avx2, string? expected)
    {
        var manifest = ModelManifest.Load(Path.Combine(Repo.Root, "data"));
        var choice = ModelSelector.Choose(manifest.Modelos, freeGb, totalGb, avx2);
        Assert.Equal(expected, choice.Model?.Id);
        Assert.False(string.IsNullOrWhiteSpace(choice.Reason));
    }
}

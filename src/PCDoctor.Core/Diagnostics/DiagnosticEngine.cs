using System.Diagnostics;
using PCDoctor.Core.Knowledge;
using PCDoctor.Core.Rules;

namespace PCDoctor.Core.Diagnostics;

/// <summary>Un hallazgo SIEMPRE sale de una regla de la biblioteca evaluada sobre hechos medidos (o de un síntoma que contó la persona).</summary>
public sealed record Finding(
    ProblemDef Problem,
    HealthStatus Status,
    string Message,
    string TechnicalMessage,
    IReadOnlyList<DetailItem> Evidence,
    bool FromSymptom)
{
    public FindingKind Kind => Problem.Kind;
}

public sealed class DiagnosticReport
{
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public string MachineName { get; init; } = Environment.MachineName;
    public bool Elevated { get; init; }
    public string? Symptom { get; init; }
    public required FactStore Facts { get; init; }
    public required IReadOnlyList<ProbeResult> Probes { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }

    public IEnumerable<Finding> Issues => Findings.Where(f => f.Kind == FindingKind.Issue);
    public IEnumerable<Finding> Suggestions => Findings.Where(f => f.Kind == FindingKind.Suggestion);

    public HealthStatus Overall =>
        Issues.Select(f => f.Status).Where(s => s != HealthStatus.Unverified).DefaultIfEmpty(HealthStatus.Ok).Max();

    /// <summary>Estado por componente (sonda): el peor de los hallazgos que dependen de ella.</summary>
    public HealthStatus ComponentStatus(ProbeResult probe)
    {
        if (probe.Error is not null) return HealthStatus.Unverified;
        var related = Issues.Where(f => f.Problem.Sondas.FirstOrDefault() == probe.ProbeId).Select(f => f.Status).ToList();
        return related.Count == 0 ? HealthStatus.Ok : related.Max();
    }
}

public sealed class DiagnosticEngine(KnowledgeBase kb, IReadOnlyList<IProbe> probes)
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(25);

    public IReadOnlyList<IProbe> Probes => probes;
    public KnowledgeBase Knowledge => kb;

    public IReadOnlyList<IProbe> ProbesFor(IEnumerable<string>? areas, IEnumerable<ProblemDef>? symptomMatches = null)
    {
        if (areas is null && symptomMatches is null) return probes;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var areaSet = areas?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var p in probes)
            if (p.Areas.Any(areaSet.Contains)) wanted.Add(p.Id);
        foreach (var m in symptomMatches ?? [])
            wanted.UnionWith(m.Sondas);
        return probes.Where(p => wanted.Contains(p.Id)).ToList();
    }

    public async Task<DiagnosticReport> RunAsync(
        IReadOnlyList<IProbe> selected,
        ProbeContext ctx,
        string? symptom = null,
        IReadOnlyList<ProblemDef>? symptomMatches = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var first = new List<ProbeResult>();
        foreach (var p in selected.Where(p => p.RunsAlone))
            first.Add(await RunProbeAsync(p, ctx, progress, ct));
        var rest = await Task.WhenAll(selected.Where(p => !p.RunsAlone).Select(p => RunProbeAsync(p, ctx, progress, ct)));
        var results = first.Concat(rest).ToList();
        var facts = new FactStore();
        foreach (var r in results.Where(r => r.Error is null)) facts.Merge(r.Facts);
        var ran = results.Where(r => r.Error is null).Select(r => r.ProbeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new DiagnosticReport
        {
            Elevated = ctx.IsElevated,
            Symptom = symptom,
            Facts = facts,
            Probes = results,
            Findings = Evaluate(kb, facts, ran, symptomMatches),
        };
    }

    private static async Task<ProbeResult> RunProbeAsync(IProbe probe, ProbeContext ctx, IProgress<string>? progress, CancellationToken ct)
    {
        // La sonda escribe en un resultado propio. Si se pasa de tiempo, se descarta entero: así una sonda colgada
        // no puede seguir escribiendo sobre lo que ya se muestra en pantalla.
        var working = new ProbeResult { ProbeId = probe.Id, Title = probe.Title };
        var sw = Stopwatch.StartNew();
        progress?.Report($"Revisando: {probe.Title}…");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        string? error = null;
        try
        {
            // Cada sonda corre en su propio hilo: muchas APIs de Windows (WMI, registro) son bloqueantes.
            var work = Task.Run(() => probe.RunAsync(ctx, working, timeout.Token), timeout.Token);
            var finished = await Task.WhenAny(work, Task.Delay(ProbeTimeout, ct));
            if (finished != work)
            {
                error = "La comprobación tardó demasiado y se canceló.";
                _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // se observa el error tardío
            }
            else await work;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = "La comprobación tardó demasiado y se canceló.";
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            error = $"La comprobación falló: {e.Message}";
        }
        if (error is not null)
            return new ProbeResult { ProbeId = probe.Id, Title = probe.Title, Error = error, Duration = sw.Elapsed };
        working.Duration = sw.Elapsed;
        return working;
    }

    /// <summary>
    /// Evalúa la biblioteca. Una ficha se evalúa solo si corrieron todas sus sondas: así un análisis parcial
    /// no inventa conclusiones. La primera regla verdadera decide el estado.
    /// </summary>
    public static List<Finding> Evaluate(KnowledgeBase kb, FactStore facts, ISet<string> ranProbes, IReadOnlyList<ProblemDef>? symptomMatches = null)
    {
        var findings = new List<Finding>();
        var matchedIds = (symptomMatches ?? []).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in kb.Problems)
        {
            if (p.SymptomOnly)
            {
                if (matchedIds.Contains(p.Id))
                    findings.Add(new Finding(p, HealthStatus.Warning, p.Explicacion.Sencillo, p.Explicacion.For(true), [], FromSymptom: true));
                continue;
            }
            if (!p.Sondas.All(ranProbes.Contains)) continue;

            var anyUnknown = false;
            Finding? hit = null;
            foreach (var rule in p.Deteccion)
            {
                Tri tri;
                try
                {
                    tri = Evaluator.Evaluate(rule.Compiled!, facts);
                }
                catch (RuleSyntaxException e)
                {
                    // Un dato con tipo inesperado no debe tumbar todo el análisis: esa ficha queda «sin comprobar».
                    AppLog.Error($"regla {p.Id}", e);
                    anyUnknown = true;
                    continue;
                }
                if (tri == Tri.Unknown) { anyUnknown = true; continue; }
                if (tri != Tri.True) continue;
                var refs = rule.Compiled!.References().Concat(Template.References(rule.Mensaje)).Distinct();
                var evidence = refs.Where(facts.Has).Select(r => new DetailItem(r, FactStore.Format(facts.All[r]))).ToList();
                var msg = Template.Render(rule.Mensaje, facts);
                var tech = rule.MensajeTecnico is null ? msg : Template.Render(rule.MensajeTecnico, facts);
                hit = new Finding(p, rule.Status, msg, tech, evidence, matchedIds.Contains(p.Id));
                break;
            }
            if (hit is not null)
            {
                if (hit.Status != HealthStatus.Ok) findings.Add(hit);
            }
            else if (anyUnknown && p.SiNoSePuede is { } why)
            {
                var msg = Template.Render(why, facts);
                findings.Add(new Finding(p, HealthStatus.Unverified, msg, msg, [], matchedIds.Contains(p.Id)));
            }
        }
        return findings
            .OrderByDescending(f => f.FromSymptom)
            .ThenByDescending(f => f.Status)
            .ThenBy(f => f.Problem.Id)
            .ToList();
    }
}

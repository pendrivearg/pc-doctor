using PCDoctor.Core;
using PCDoctor.Core.Actions;
using PCDoctor.Core.Rules;

namespace PCDoctor.Tests;

public class ExpressionTests
{
    private static FactStore Facts(params (string k, object v)[] items)
    {
        var f = new FactStore();
        foreach (var (k, v) in items) f.Set(k, v);
        return f;
    }

    [Theory]
    [InlineData("a.x == 3", Tri.True)]
    [InlineData("a.x > 3", Tri.False)]
    [InlineData("a.x >= 3 and a.ok", Tri.True)]
    [InlineData("not a.ok or a.x < 1", Tri.False)]
    [InlineData("(a.x == 1 or a.x == 3) and a.nombre == 'NVIDIA'", Tri.True)]
    [InlineData("contiene(a.nombre, 'nvi')", Tri.True)]
    [InlineData("existe(a.falta)", Tri.False)]
    [InlineData("a.x == -2", Tri.False)]
    public void Evaluates(string expr, Tri expected)
    {
        var facts = Facts(("a.x", 3), ("a.ok", true), ("a.nombre", "NVIDIA"));
        Assert.Equal(expected, Evaluator.Evaluate(Expr.Parse(expr), facts));
    }

    [Theory]
    [InlineData("a.falta == 1", Tri.Unknown)]
    [InlineData("a.falta == 1 and a.ok", Tri.Unknown)]
    [InlineData("a.falta == 1 and not a.ok", Tri.False)]   // falso gana aunque falte un dato
    [InlineData("a.falta == 1 or a.ok", Tri.True)]         // verdadero gana aunque falte un dato
    [InlineData("not a.falta", Tri.Unknown)]
    public void MissingFactsAreUnknownNotFalse(string expr, Tri expected)
    {
        var facts = Facts(("a.ok", true));
        Assert.Equal(expected, Evaluator.Evaluate(Expr.Parse(expr), facts));
    }

    [Theory]
    [InlineData("a.x ==")]
    [InlineData("(a.x == 1")]
    [InlineData("a.x = 1")]
    [InlineData("a.x == 1 a.y")]
    [InlineData("borrar('C:')")]
    public void RejectsInvalidRules(string expr)
    {
        Assert.ThrowsAny<RuleSyntaxException>(() => Evaluator.Evaluate(Expr.Parse(expr), new FactStore()));
    }

    [Fact]
    public void ListsReferencedFacts()
    {
        var refs = Expr.Parse("red.dns_ok and (ram.uso_pct > 3 or existe(disco.x))").References().ToList();
        Assert.Equal(["red.dns_ok", "ram.uso_pct", "disco.x"], refs);
    }

    [Fact]
    public void TemplatesUseMeasuredValuesWithLocalFormat()
    {
        var facts = Facts(("d.libre", 43.75), ("d.nombre", "C:"));
        Assert.Equal("Quedan 43,8 GB en C:", Template.Render("Quedan {d.libre:0.0} GB en {d.nombre}", facts));
        Assert.Contains("dato no disponible", Template.Render("{d.falta}", facts));
    }
}

public class JournalAndRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PCDoctorTests", Guid.NewGuid().ToString("N"));
    private Journal NewJournal() => new(Path.Combine(_dir, "historial.db"));

    private sealed class FakeAction : IActionImpl
    {
        public bool VerifyOk = true;
        public bool Throw;
        public string State = "original";
        public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext c, CancellationToken t) => Task.FromResult(new Dictionary<string, string?> { ["estado"] = State });
        public Task<string> ExecuteAsync(ActionContext c, CancellationToken t)
        {
            State = "cambiado";
            if (Throw) throw new InvalidOperationException("falla simulada");
            return Task.FromResult("Cambiado.");
        }
        public Task<VerifyResult> VerifyAsync(ActionContext c, CancellationToken t) => Task.FromResult(new VerifyResult(VerifyOk, VerifyOk ? "ok" : "no quedó bien"));
        public Task RollbackAsync(ActionContext c, Dictionary<string, string?> s, CancellationToken t) { State = s["estado"]!; return Task.CompletedTask; }
    }

    private static ActionDef Def(FakeAction impl, Maturity m = Maturity.T2, bool admin = false) => new()
    {
        Id = "prueba.fake", Title = "Prueba", WhatChanges = "x", HowToUndo = "y", Risk = Risk.Yellow,
        Maturity = m, Reversible = true, RequiresAdmin = admin, Impl = impl,
    };

    [Fact]
    public async Task SuccessIsJournaledAndCanBeUndone()
    {
        using var j = NewJournal();
        var impl = new FakeAction();
        var runner = new ActionRunner(new ActionPolicy());
        var outcome = await runner.RunAsync(Def(impl), new ActionContext(), j);
        Assert.Equal(OutcomeStatus.Succeeded, outcome.Status);
        Assert.Equal("cambiado", impl.State);

        var catalog = new ActionCatalog();
        catalog.Register(Def(impl));
        var op = j.Operations().Single();
        Assert.Equal(nameof(OutcomeStatus.Succeeded), op.ResultStatus);
        var undo = await runner.UndoAsync(op, catalog, new ActionContext(), j);
        Assert.True(undo.Ok);
        Assert.Equal("original", impl.State);
        Assert.True(j.Operations().Single().Undone);
        var again = await runner.UndoAsync(j.Operations().Single(), catalog, new ActionContext(), j);
        Assert.False(again.Ok); // no se deshace dos veces
        Assert.True(j.VerifyChain());
    }

    [Fact]
    public async Task FailedVerificationRollsBackAutomatically()
    {
        using var j = NewJournal();
        var impl = new FakeAction { VerifyOk = false };
        var outcome = await new ActionRunner(new ActionPolicy()).RunAsync(Def(impl), new ActionContext(), j);
        Assert.Equal(OutcomeStatus.VerifyFailedRolledBack, outcome.Status);
        Assert.Equal("original", impl.State);
        Assert.Contains("no quedó bien", outcome.Message);
    }

    [Fact]
    public async Task ExceptionRollsBackAndIsReportedAsFailure()
    {
        using var j = NewJournal();
        var impl = new FakeAction { Throw = true };
        var outcome = await new ActionRunner(new ActionPolicy()).RunAsync(Def(impl), new ActionContext(), j);
        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
        Assert.Equal("original", impl.State);
    }

    [Fact]
    public async Task PolicyBlocksImmatureActionsAndMissingElevation()
    {
        using var j = NewJournal();
        var runner = new ActionRunner(new ActionPolicy()); // 1.0: exige T2
        Assert.Equal(OutcomeStatus.NotAllowed, (await runner.RunAsync(Def(new FakeAction(), Maturity.T1), new ActionContext(), j)).Status);
        Assert.Equal(OutcomeStatus.NeedsElevation, (await runner.RunAsync(Def(new FakeAction(), admin: true), new ActionContext { IsElevated = false }, j)).Status);
        Assert.Empty(j.Operations()); // nada se ejecutó ni se registró
    }

    [Fact]
    public async Task TamperingWithTheJournalIsDetected()
    {
        var path = Path.Combine(_dir, "historial.db");
        using (var j = new Journal(path))
            await new ActionRunner(new ActionPolicy()).RunAsync(Def(new FakeAction()), new ActionContext(), j);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE events SET data = replace(data, 'Cambiado', 'Otra cosa')";
            cmd.ExecuteNonQuery();
        }
        using var reopened = new Journal(path);
        Assert.False(reopened.VerifyChain());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

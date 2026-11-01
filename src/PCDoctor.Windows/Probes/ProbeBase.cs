using PCDoctor.Core;

namespace PCDoctor.Windows.Probes;

internal abstract class ProbeBase : IProbe
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract IReadOnlyList<string> Areas { get; }
    public abstract IReadOnlyList<string> FactKeys { get; }
    public virtual bool RunsAlone => false;

    public Task RunAsync(ProbeContext ctx, ProbeResult result, CancellationToken ct) => Run(ctx, result, ct);

    protected abstract Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct);

    /// <summary>Ejecuta una parte de la sonda; si falla, lo deja anotado como "no se pudo comprobar" en vez de abortar todo.</summary>
    protected static void Part(ProbeResult r, string what, Action action)
    {
        try
        {
            action();
        }
        catch (AccessDeniedException)
        {
            r.Unverified.Add($"{what} (requiere permiso de administrador)");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            r.Unverified.Add($"{what} ({e.Message})");
        }
    }

    protected static async Task PartAsync(ProbeResult r, string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (AccessDeniedException)
        {
            r.Unverified.Add($"{what} (requiere permiso de administrador)");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            r.Unverified.Add($"{what} ({e.Message})");
        }
    }

    protected static double Gb(double bytes) => Math.Round(bytes / 1024 / 1024 / 1024, 1);
}

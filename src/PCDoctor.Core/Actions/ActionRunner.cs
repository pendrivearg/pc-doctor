namespace PCDoctor.Core.Actions;

/// <summary>
/// Ejecuta acciones del catálogo con el ciclo completo: política → respaldo (al diario) → ejecutar →
/// verificar → si la verificación falla, revertir automáticamente → registrar el resultado real.
/// </summary>
public sealed class ActionRunner(ActionPolicy policy)
{
    public ActionPolicy Policy => policy;

    public async Task<ActionOutcome> RunAsync(ActionDef action, ActionContext ctx, Journal? journal, CancellationToken ct = default)
    {
        if (!policy.Allows(action))
            return new(OutcomeStatus.NotAllowed, "Esta reparación todavía no pasó las pruebas necesarias para hacerse automáticamente. Seguí los pasos guiados.");
        if (action.RequiresAdmin && !ctx.IsElevated)
            return new(OutcomeStatus.NeedsElevation, "Esta reparación necesita permiso de administrador.");
        if (action.Journaled && journal is null)
            throw new InvalidOperationException("Las acciones que modifican el sistema necesitan el diario.");

        Dictionary<string, string?> snapshot;
        try
        {
            snapshot = await action.Impl.SnapshotAsync(ctx, ct);
        }
        catch (Exception e)
        {
            return new(OutcomeStatus.Failed, $"No se pudo guardar el respaldo previo, así que no se cambió nada. ({e.Message})");
        }

        long? op = action.Journaled ? journal!.Begin(action, ctx.Parameters, snapshot) : null;

        ActionOutcome Finish(OutcomeStatus s, string msg)
        {
            if (op is { } id) journal!.Result(id, action.Id, s, msg);
            return new(s, msg, op);
        }

        string summary;
        try
        {
            summary = await action.Impl.ExecuteAsync(ctx, ct);
        }
        catch (Exception e)
        {
            var rolled = await TryRollbackAsync(action, ctx, snapshot, ct);
            return Finish(OutcomeStatus.Failed, $"La reparación falló ({e.Message}). {rolled}");
        }

        VerifyResult verify;
        try
        {
            verify = await action.Impl.VerifyAsync(ctx, ct);
        }
        catch (Exception e)
        {
            verify = new VerifyResult(false, $"No se pudo comprobar el resultado ({e.Message}).");
        }

        if (verify.Ok) return Finish(OutcomeStatus.Succeeded, $"{summary} Comprobado: {verify.Message}");

        if (action.Reversible)
        {
            var rolled = await TryRollbackAsync(action, ctx, snapshot, ct);
            return Finish(OutcomeStatus.VerifyFailedRolledBack, $"{summary} Pero la comprobación no dio bien: {verify.Message} {rolled}");
        }
        return Finish(OutcomeStatus.VerifyFailedNotReversible, $"{summary} Pero el problema sigue: {verify.Message}");
    }

    public async Task<ActionOutcome> UndoAsync(JournalOperation op, ActionCatalog catalog, ActionContext ctx, Journal journal, CancellationToken ct = default)
    {
        var action = catalog.Get(op.ActionId);
        if (action is null) return new(OutcomeStatus.Failed, "Esta versión de PC Doctor ya no tiene esa acción.");
        if (!op.Reversible) return new(OutcomeStatus.Failed, "Este cambio no se puede deshacer (no modificó configuraciones).");
        if (op.Undone) return new(OutcomeStatus.Failed, "Este cambio ya fue deshecho.");
        if (op.ResultStatus != nameof(OutcomeStatus.Succeeded)) return new(OutcomeStatus.Failed, "Ese cambio no quedó aplicado, no hay nada que deshacer.");
        if (action.RequiresAdmin && !ctx.IsElevated) return new(OutcomeStatus.NeedsElevation, "Deshacer este cambio necesita permiso de administrador.");
        // Si después hubo otro cambio sobre lo mismo, deshacer este pisaría el más nuevo.
        var newer = journal.Operations().FirstOrDefault(o => o.OperationId > op.OperationId && o.ActionId == op.ActionId && !o.Undone
            && o.ResultStatus == nameof(OutcomeStatus.Succeeded) && SameParameters(o.Parameters, op.Parameters));
        if (newer is not null)
            return new(OutcomeStatus.Failed, $"Después de este hubo otro cambio sobre lo mismo (n.º {newer.OperationId}). Deshacé primero ese.");
        var undoCtx = new ActionContext { IsElevated = ctx.IsElevated, Parameters = op.Parameters };
        try
        {
            await action.Impl.RollbackAsync(undoCtx, op.Snapshot, CancellationToken.None);
        }
        catch (Exception e)
        {
            return new(OutcomeStatus.Failed, $"No se pudo deshacer: {e.Message}");
        }
        // Se comprueba que el estado quedó igual al respaldo guardado antes del cambio.
        Dictionary<string, string?> now;
        try { now = await action.Impl.SnapshotAsync(undoCtx, CancellationToken.None); }
        catch (Exception e) { return new(OutcomeStatus.Failed, $"Se intentó deshacer, pero no se pudo comprobar el resultado: {e.Message}"); }
        if (!SameSnapshot(now, op.Snapshot))
            return new(OutcomeStatus.Failed, "Se intentó deshacer, pero la configuración no quedó como estaba antes. El respaldo sigue guardado en el historial.");
        journal.Undone(op.OperationId, action.Id, "Deshecho a pedido del usuario y comprobado.");
        return new(OutcomeStatus.Succeeded, "Listo y comprobado: se restauró la configuración anterior.", op.OperationId);
    }

    private static bool SameParameters(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static bool SameSnapshot(IReadOnlyDictionary<string, string?> a, IReadOnlyDictionary<string, string?> b) =>
        a.Keys.Union(b.Keys).All(k => a.GetValueOrDefault(k) == b.GetValueOrDefault(k));

    private static async Task<string> TryRollbackAsync(ActionDef action, ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct)
    {
        if (!action.Reversible) return "";
        try
        {
            // Volver atrás no se cancela a mitad de camino.
            await action.Impl.RollbackAsync(ctx, snapshot, CancellationToken.None);
            return "Se restauró la configuración anterior.";
        }
        catch (Exception e)
        {
            return $"ATENCIÓN: no se pudo restaurar la configuración anterior ({e.Message}). El respaldo quedó guardado en el historial.";
        }
    }
}

namespace PCDoctor.Core.Actions;

public sealed record VerifyResult(bool Ok, string Message);

public sealed class ActionContext
{
    public bool IsElevated { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public string Param(string key, string fallback = "") => Parameters.GetValueOrDefault(key, fallback);
}

/// <summary>Implementación de una acción del catálogo. La IA nunca escribe comandos: solo puede pedir acciones de aquí.</summary>
public interface IActionImpl
{
    /// <summary>Guarda todo lo necesario para deshacer. Se persiste en el diario ANTES de ejecutar.</summary>
    Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct);
    /// <summary>Ejecuta el cambio y devuelve un resumen legible de lo que hizo.</summary>
    Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct);
    /// <summary>Comprueba de verdad que el cambio tuvo el efecto buscado.</summary>
    Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct);
    Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct);
}

public sealed class ActionDef
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>Qué cambia, en lenguaje sencillo (se muestra antes de pedir permiso).</summary>
    public required string WhatChanges { get; init; }
    /// <summary>Cómo se deshace (o por qué no hace falta).</summary>
    public required string HowToUndo { get; init; }
    public required Risk Risk { get; init; }
    public required Maturity Maturity { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool RequiresReboot { get; init; }
    public bool Reversible { get; init; }
    /// <summary>Si tarda, cuánto (se avisa antes de empezar). Null = segundos.</summary>
    public string? Duration { get; init; }
    /// <summary>Las acciones que no cambian nada del sistema (por ejemplo abrir una configuración) no van al diario.</summary>
    public bool Journaled { get; init; } = true;
    public required IActionImpl Impl { get; init; }
}

public sealed class ActionCatalog
{
    private readonly Dictionary<string, ActionDef> _actions = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ActionDef def) => _actions.Add(def.Id, def);
    public ActionDef? Get(string id) => _actions.GetValueOrDefault(id);
    public IEnumerable<ActionDef> All => _actions.Values;
}

/// <summary>Política de ejecución. En la versión 1.0 la madurez mínima para reparar automáticamente es T2.</summary>
public sealed class ActionPolicy
{
    public Maturity MinimumMaturity { get; init; } = Maturity.T2;

    /// <summary>Etiqueta visible cuando la acción está habilitada por debajo de T2 (solo en versiones de prueba).</summary>
    public string? MaturityLabel(ActionDef a) => a.Maturity < Maturity.T2 ? "En prueba" : null;

    public bool Allows(ActionDef a) => a.Maturity >= MinimumMaturity;

    /// <summary>Versión de prueba (0.x): habilita acciones T1, siempre con verificación posterior y la etiqueta "En prueba".</summary>
    public static ActionPolicy Prototype => new() { MinimumMaturity = Maturity.T1 };
}

public enum OutcomeStatus { Succeeded, VerifyFailedRolledBack, VerifyFailedNotReversible, Failed, NeedsElevation, NotAllowed }

public sealed record ActionOutcome(OutcomeStatus Status, string Message, long? OperationId = null)
{
    public bool Ok => Status == OutcomeStatus.Succeeded;
}

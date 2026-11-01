using System.ServiceProcess;
using PCDoctor.Core;

namespace PCDoctor.Windows.Probes;

/// <summary>Servicios de Windows de los que dependen la impresión, el sonido y las actualizaciones.</summary>
internal sealed class ServicesProbe : ProbeBase
{
    internal static readonly (string service, string key, string label)[] Watched =
    [
        ("Spooler", "impresion", "Servicio de impresión"),
        ("Audiosrv", "audio", "Servicio de audio"),
        ("wuauserv", "update", "Windows Update"),
        ("BITS", "bits", "Transferencia inteligente (BITS)"),
    ];

    public override string Id => "servicios";
    public override string Title => "Servicios de Windows (impresión, sonido, actualizaciones)";
    public override IReadOnlyList<string> Areas => [Core.Areas.Windows];
    public override IReadOnlyList<string> FactKeys =>
        Watched.SelectMany(w => new[] { $"servicios.{w.key}_ejecutando", $"servicios.{w.key}_deshabilitado" }).ToList();

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        foreach (var (service, key, label) in Watched)
        {
            Part(r, label, () =>
            {
                using var sc = new ServiceController(service);
                var running = sc.Status == ServiceControllerStatus.Running;
                var disabled = sc.StartType == ServiceStartMode.Disabled;
                r.Facts.Set($"servicios.{key}_ejecutando", running);
                r.Facts.Set($"servicios.{key}_deshabilitado", disabled);
                r.Detail(label, $"{(running ? "funcionando" : "detenido")}{(disabled ? " · DESHABILITADO" : "")}");
            });
        }
        return Task.CompletedTask;
    }
}

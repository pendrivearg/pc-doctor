using PCDoctor.Core;
using PCDoctor.Core.Actions;
using PCDoctor.Windows.Actions;
using PCDoctor.Windows.Probes;

namespace PCDoctor.Windows;

/// <summary>Registro de todas las sondas y acciones de Windows.</summary>
public static class WindowsPlatform
{
    public const string OpenAction = "abrir.configuracion";

    public static IReadOnlyList<IProbe> Probes() =>
    [
        new SystemProbe(), new LicenseProbe(), new PlatformProbe(), new CpuProbe(), new MemoryProbe(), new DiskProbe(),
        new GpuProbe(), new BatteryProbe(), new EventsProbe(), new DevicesProbe(), new NetworkProbe(), new TimeProbe(),
        new SecurityProbe(), new StartupProbe(), new ServicesProbe(),
    ];

    /// <summary>
    /// Madurez de las reparaciones que requieren administrador. Quedan en T0 (ocultas) hasta que pasen las
    /// pruebas con permiso de administrador (tests marcados "ConAdmin"); recién ahí se suben a T1.
    /// </summary>
    internal static Maturity AdminMaturity = Maturity.T0;

    public static ActionCatalog Actions()
    {
        var c = new ActionCatalog();
        RegisterAdminActions(c);
        c.Register(new ActionDef
        {
            Id = StartupCatalog.DisableUserAction,
            Title = "Que no arranque solo con Windows",
            WhatChanges = "El programa deja de abrirse solo al prender la PC. No se desinstala ni se borra: lo podés seguir abriendo cuando quieras.",
            HowToUndo = "Desde el Historial de reparaciones, con «Deshacer», vuelve a arrancar con Windows.",
            Risk = Risk.Yellow,
            Maturity = Maturity.T1,
            Reversible = true,
            Impl = new StartupDisableAction(machine: false),
        });
        c.Register(new ActionDef
        {
            Id = "net.dns.flush",
            Title = "Vaciar la caché de nombres de Internet (DNS)",
            WhatChanges = "Windows olvida las direcciones de páginas que tenía guardadas y las vuelve a pedir. No cambia ninguna configuración.",
            HowToUndo = "No hace falta deshacerlo.",
            Risk = Risk.Green,
            Maturity = Maturity.T1,
            Reversible = false,
            Impl = new FlushDnsAction(),
        });
        c.Register(new ActionDef
        {
            Id = "net.proxy.disable",
            Title = "Desactivar el proxy",
            WhatChanges = "Se apaga el proxy configurado para tu usuario, para que Internet vaya directo. Si lo usás en el trabajo a propósito, no lo hagas.",
            HowToUndo = "Desde el Historial de reparaciones, con el botón Deshacer, vuelve exactamente la configuración anterior.",
            Risk = Risk.Yellow,
            Maturity = Maturity.T1,
            Reversible = true,
            Impl = new DisableProxyAction(),
        });
        c.Register(new ActionDef
        {
            Id = "clean.temp",
            Title = "Borrar archivos temporales viejos",
            WhatChanges = "Se borran archivos temporales de tu usuario con más de 2 días. No se tocan documentos, fotos ni archivos en uso.",
            HowToUndo = "Son archivos descartables que los programas vuelven a crear si los necesitan; no se recuperan.",
            Risk = Risk.Yellow,
            Maturity = Maturity.T1,
            Reversible = false,
            Impl = new CleanTempAction(),
        });
        c.Register(new ActionDef
        {
            Id = OpenAction,
            Title = "Abrir la configuración",
            WhatChanges = "Solo se abre la pantalla de Configuración de Windows indicada.",
            HowToUndo = "No cambia nada.",
            Risk = Risk.Green,
            Maturity = Maturity.T1,
            Reversible = false,
            Journaled = false,
            Impl = new OpenSettingsAction(),
        });
        return c;
    }

    private static void RegisterAdminActions(ActionCatalog c)
    {
        void Admin(string id, string title, string what, string undo, Risk risk, bool reversible, IActionImpl impl, string? duration = null) =>
            c.Register(new ActionDef
            {
                Id = id, Title = title, WhatChanges = what, HowToUndo = undo, Risk = risk, Reversible = reversible,
                RequiresAdmin = true, Maturity = AdminMaturity, Duration = duration, Impl = impl,
            });

        Admin(StartupCatalog.DisableMachineAction, "Que no arranque solo con Windows",
            "El programa deja de abrirse solo al prender la PC (para todos los usuarios). No se desinstala ni se borra.",
            "Desde el Historial de reparaciones, con «Deshacer», vuelve a arrancar con Windows.",
            Risk.Yellow, true, new StartupDisableAction(machine: true));
        Admin("print.spooler.repair", "Reparar el servicio de impresión",
            "Se vuelve a poner en marcha el servicio de Windows que maneja las impresoras (si estaba deshabilitado, se habilita).",
            "Si cambió el tipo de inicio, «Deshacer» lo deja como estaba.",
            Risk.Yellow, true, new ServiceRepairAction("Spooler", System.ServiceProcess.ServiceStartMode.Automatic, restartIfRunning: true));
        Admin("print.queue.clear", "Vaciar la cola de impresión trabada",
            "Se borran los documentos que están esperando para imprimirse (los vas a tener que mandar a imprimir de nuevo) y se reinicia el servicio de impresión. Tus archivos no se tocan.",
            "No se puede deshacer: los trabajos de impresión pendientes se pierden, pero tus documentos siguen intactos.",
            Risk.Yellow, false, new ClearPrintQueueAction());
        Admin("audio.service.repair", "Reparar el servicio de sonido",
            "Se reinicia el servicio de audio de Windows (si estaba deshabilitado, se habilita). El sonido se corta un segundo.",
            "Si cambió el tipo de inicio, «Deshacer» lo deja como estaba.",
            Risk.Yellow, true, new ServiceRepairAction("Audiosrv", System.ServiceProcess.ServiceStartMode.Automatic, restartIfRunning: true));
        Admin("wu.service.repair", "Volver a habilitar Windows Update",
            "Se habilita el servicio de Windows Update como viene de fábrica, para que la PC vuelva a recibir arreglos de seguridad.",
            "«Deshacer» lo vuelve a dejar deshabilitado.",
            Risk.Yellow, true, new ServiceRepairAction("wuauserv", System.ServiceProcess.ServiceStartMode.Manual, restartIfRunning: false));
        Admin("win.dism.scan", "Revisar a fondo los archivos de Windows",
            "Windows revisa sus propios archivos con su herramienta oficial (DISM). Solo mira, no cambia nada.",
            "No cambia nada.",
            Risk.Green, false, new DismScanAction(), "Puede tardar entre 5 y 20 minutos.");
        Admin("win.dism.restore", "Reparar los archivos de Windows",
            "Windows reemplaza sus archivos dañados por copias sanas que descarga de Windows Update (necesita Internet). Tus archivos y programas no se tocan.",
            "No hace falta deshacerlo: solo restaura archivos originales de Windows.",
            Risk.Yellow, false, new DismRestoreAction(), "Puede tardar entre 10 y 40 minutos.");
        Admin("defender.update", "Actualizar el antivirus (Microsoft Defender)",
            "Microsoft Defender descarga las firmas más nuevas para reconocer los virus recientes (necesita Internet).",
            "No hace falta deshacerlo.",
            Risk.Green, false, new DefenderUpdateAction(), "Puede tardar unos minutos.");
        Admin("defender.quickscan", "Buscar virus (análisis rápido de Microsoft Defender)",
            "Microsoft Defender revisa los lugares donde suelen esconderse los virus. Si encuentra algo, lo maneja Defender.",
            "No cambia configuraciones.",
            Risk.Green, false, new DefenderQuickScanAction(), "Puede tardar entre 2 y 15 minutos.");
        Admin("defender.offline", "Análisis profundo sin conexión (Microsoft Defender)",
            "La PC se reinicia enseguida y Microsoft Defender la revisa ANTES de que arranque Windows, cuando los virus no se pueden esconder. "
            + "Guardá lo que estés haciendo. Tarda unos 15 minutos y vuelve sola a Windows. Si el disco tiene BitLocker, puede pedir la clave de recuperación.",
            "No cambia configuraciones. Lo que encuentre lo maneja Defender: se ve en Seguridad de Windows > Historial de protección.",
            Risk.Yellow, false, new DefenderOfflineScanAction(), "Unos 15 minutos; la PC se reinicia sola.");
    }
}

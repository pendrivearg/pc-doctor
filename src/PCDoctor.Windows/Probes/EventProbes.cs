using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;
using PCDoctor.Core;

namespace PCDoctor.Windows.Probes;

/// <summary>Registros de eventos de los últimos 30 días: apagados inesperados, pantallas azules, errores de hardware, de disco, de video y de programas.</summary>
internal sealed class EventsProbe : ProbeBase
{
    private const long ThirtyDaysMs = 30L * 24 * 3600 * 1000;

    public override string Id => "eventos";
    public override string Title => "Historial de errores (últimos 30 días)";
    public override IReadOnlyList<string> Areas => [Core.Areas.Windows, Core.Areas.Hardware];
    public override IReadOnlyList<string> FactKeys =>
    [
        "eventos.apagados_inesperados_30d", "eventos.pantallas_azules_30d", "eventos.codigos_bsod", "eventos.whea_fatales_30d",
        "eventos.whea_corregidos_30d", "eventos.errores_disco_30d", "eventos.reinicios_video_30d", "eventos.programa_mas_fallado",
        "eventos.programa_mas_fallado_veces", "eventos.cuelgues_30d",
    ];

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        Part(r, "Apagados inesperados", () =>
        {
            var n = Count("System", "Microsoft-Windows-Kernel-Power", "EventID=41");
            r.Facts.Set("eventos.apagados_inesperados_30d", n);
            r.Detail("Apagados o reinicios inesperados", n);
        });
        Part(r, "Pantallas azules", () =>
        {
            var codes = new List<string>();
            foreach (var e in Read("System", "Microsoft-Windows-WER-SystemErrorReporting", "EventID=1001", 50))
            {
                var raw = e.Properties.Count > 0 ? e.Properties[0].Value?.ToString() ?? "" : "";
                var m = Regex.Match(raw, @"0x[0-9a-fA-F]+");
                if (m.Success) codes.Add(BugCheckName(m.Value));
            }
            r.Facts.Set("eventos.pantallas_azules_30d", codes.Count);
            if (codes.Count > 0)
                r.Facts.Set("eventos.codigos_bsod", string.Join(", ", codes.GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Count() > 1 ? $"{g.Key} (×{g.Count()})" : g.Key)));
            r.Detail("Pantallas azules", codes.Count);
        });
        Part(r, "Errores de hardware (WHEA)", () =>
        {
            var fatal = Count("System", "Microsoft-Windows-WHEA-Logger", "(EventID=1 or EventID=18 or EventID=20 or EventID=46)");
            var corrected = Count("System", "Microsoft-Windows-WHEA-Logger", "(EventID=17 or EventID=19 or EventID=47)");
            r.Facts.Set("eventos.whea_fatales_30d", fatal);
            r.Facts.Set("eventos.whea_corregidos_30d", corrected);
            r.Detail("Errores de hardware graves / corregidos", $"{fatal} / {corrected}");
        });
        Part(r, "Errores de disco", () =>
        {
            var n = Count("System", "disk", "(EventID=7 or EventID=51 or EventID=153)")
                    + Count("System", "Microsoft-Windows-Ntfs", "EventID=55");
            r.Facts.Set("eventos.errores_disco_30d", n);
            r.Detail("Errores de disco o sistema de archivos", n);
        });
        Part(r, "Reinicios del controlador de video", () =>
        {
            var n = Count("System", "Display", "EventID=4101");
            r.Facts.Set("eventos.reinicios_video_30d", n);
            r.Detail("Reinicios del controlador de video", n);
        });
        Part(r, "Programas que se cerraron con error", () =>
        {
            var apps = new List<string>();
            foreach (var e in Read("Application", "Application Error", "EventID=1000", 500))
                if (e.Properties.Count > 0 && e.Properties[0].Value?.ToString() is { Length: > 0 } app) apps.Add(app);
            var hangs = Count("Application", "Application Hang", "EventID=1002");
            r.Facts.Set("eventos.cuelgues_30d", hangs);
            var top = apps.GroupBy(a => a, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (top is not null)
            {
                r.Facts.Set("eventos.programa_mas_fallado", top.Key);
                r.Facts.Set("eventos.programa_mas_fallado_veces", top.Count());
            }
            else
            {
                r.Facts.Set("eventos.programa_mas_fallado_veces", 0);
            }
            r.Detail("Cierres con error de programas", apps.Count);
            r.Detail("Programas que dejaron de responder", hangs);
            foreach (var g in apps.GroupBy(a => a, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(3))
                r.Detail($"  · {g.Key}", $"{g.Count()} veces");
        });
        return Task.CompletedTask;
    }

    private static string Query(string provider, string condition) =>
        $"*[System[Provider[@Name='{provider}'] and {condition} and TimeCreated[timediff(@SystemTime) <= {ThirtyDaysMs}]]]";

    private static IEnumerable<EventRecord> Read(string log, string provider, string condition, int max)
    {
        var q = new EventLogQuery(log, PathType.LogName, Query(provider, condition)) { ReverseDirection = true };
        EventLogReader reader;
        try { reader = new EventLogReader(q); }
        catch (UnauthorizedAccessException) { throw new AccessDeniedException(log); }
        using (reader)
        {
            for (var i = 0; i < max; i++)
            {
                var e = reader.ReadEvent();
                if (e is null) yield break;
                using (e) yield return e;
            }
        }
    }

    private static int Count(string log, string provider, string condition) => Read(log, provider, condition, 1000).Count();

    internal static string BugCheckName(string hex)
    {
        var code = Convert.ToUInt32(hex[2..], 16);
        var name = code switch
        {
            0x0A => "IRQL_NOT_LESS_OR_EQUAL",
            0x19 => "BAD_POOL_HEADER",
            0x1A => "MEMORY_MANAGEMENT",
            0x1E => "KMODE_EXCEPTION_NOT_HANDLED",
            0x3B => "SYSTEM_SERVICE_EXCEPTION",
            0x50 => "PAGE_FAULT_IN_NONPAGED_AREA",
            0x7A => "KERNEL_DATA_INPAGE_ERROR",
            0x7B => "INACCESSIBLE_BOOT_DEVICE",
            0x7E => "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
            0x9F => "DRIVER_POWER_STATE_FAILURE",
            0xC2 => "BAD_POOL_CALLER",
            0xD1 => "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
            0xEF => "CRITICAL_PROCESS_DIED",
            0xF4 => "CRITICAL_OBJECT_TERMINATION",
            0x101 => "CLOCK_WATCHDOG_TIMEOUT",
            0x10D => "WDF_VIOLATION",
            0x116 => "VIDEO_TDR_FAILURE",
            0x117 => "VIDEO_TDR_TIMEOUT_DETECTED",
            0x124 => "WHEA_UNCORRECTABLE_ERROR",
            0x133 => "DPC_WATCHDOG_VIOLATION",
            0x139 => "KERNEL_SECURITY_CHECK_FAILURE",
            0x154 => "UNEXPECTED_STORE_EXCEPTION",
            0x1000007E => "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M",
            0x1000008E => "KERNEL_MODE_EXCEPTION_NOT_HANDLED_M",
            0xC000021A => "STATUS_SYSTEM_PROCESS_TERMINATED",
            _ => null,
        };
        var shortHex = "0x" + code.ToString("X", CultureInfo.InvariantCulture);
        return name is null ? shortHex : $"{name} ({shortHex})";
    }
}

/// <summary>Dispositivos con error en el Administrador de dispositivos, con el significado del código.</summary>
internal sealed class DevicesProbe : ProbeBase
{
    public override string Id => "dispositivos";
    public override string Title => "Dispositivos y controladores";
    public override IReadOnlyList<string> Areas => [Core.Areas.Windows, Core.Areas.Hardware];
    public override IReadOnlyList<string> FactKeys => ["dispositivos.con_error", "dispositivos.deshabilitados", "dispositivos.lista"];

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        var rows = Wmi.Cimv2("SELECT Name, ConfigManagerErrorCode, PNPClass FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        var errors = new List<string>();
        var disabled = 0;
        foreach (var d in rows)
        {
            var code = d.Get<uint>("ConfigManagerErrorCode");
            var name = d.Str("Name");
            if (name.Length == 0) name = "Dispositivo desconocido";
            if (code == 22) { disabled++; r.Detail(name, "deshabilitado"); continue; }
            // No son fallas: desconectado (45), deshabilitado en la BIOS (29), listo para quitar (47),
            // apagado por una aplicación (44), quitándose (21) o esperando un reinicio (14, ya lo cubre otra ficha).
            if (code is 45 or 29 or 47 or 44 or 21 or 14) continue;
            var text = $"{name}: {CodeText(code)} (código {code})";
            errors.Add(text);
            r.Detail(name, $"{CodeText(code)} (código {code})");
        }
        r.Facts.Set("dispositivos.con_error", errors.Count);
        r.Facts.Set("dispositivos.deshabilitados", disabled);
        if (errors.Count > 0) r.Facts.Set("dispositivos.lista", string.Join("; ", errors));
        if (errors.Count == 0 && disabled == 0) r.Detail("Dispositivos con problemas", "ninguno");
        return Task.CompletedTask;
    }

    internal static string CodeText(uint code) => code switch
    {
        1 => "no está bien configurado",
        3 => "el controlador está dañado o falta memoria",
        10 => "no puede iniciarse",
        12 => "no encuentra recursos libres",
        14 => "necesita reiniciar la PC",
        18 => "hay que reinstalar el controlador",
        19 => "configuración del registro dañada",
        21 => "Windows lo está quitando",
        24 => "no está presente o le falta controlador",
        28 => "no tiene controlador instalado",
        29 => "deshabilitado desde la BIOS",
        31 => "no funciona correctamente (controlador)",
        32 => "el servicio del controlador está deshabilitado",
        37 => "el controlador devolvió un error",
        38 => "el controlador anterior sigue en memoria (reiniciar)",
        39 => "controlador dañado o faltante",
        40 => "información del servicio dañada",
        41 => "el controlador cargó pero no encuentra el dispositivo",
        43 => "Windows lo detuvo porque informó problemas",
        44 => "una aplicación o servicio lo apagó",
        47 => "preparado para quitar en forma segura",
        48 => "controlador bloqueado por problemas conocidos",
        52 => "no se puede verificar la firma digital del controlador",
        _ => "tiene un problema",
    };
}

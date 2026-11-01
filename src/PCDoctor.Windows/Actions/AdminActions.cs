using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using PCDoctor.Core.Actions;

namespace PCDoctor.Windows.Actions;

/// <summary>
/// Repara un servicio de Windows: si está deshabilitado lo vuelve a su inicio por defecto de Microsoft
/// y lo arranca (o lo reinicia si ya estaba funcionando). Deshacer restaura el tipo de inicio anterior.
/// </summary>
internal sealed class ServiceRepairAction(string service, ServiceStartMode defaultStart, bool restartIfRunning) : IActionImpl
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(40);

    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct)
    {
        using var sc = new ServiceController(service);
        return Task.FromResult(new Dictionary<string, string?> { ["inicio"] = sc.StartType.ToString() });
    }

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        using var sc = new ServiceController(service);
        var notes = new List<string>();
        if (sc.StartType == ServiceStartMode.Disabled)
        {
            SetStartMode(service, defaultStart);
            notes.Add("se volvió a habilitar");
        }
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running && restartIfRunning)
        {
            Services.Restart(sc, Wait);
            notes.Add("se reinició");
        }
        sc.Refresh();
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, Wait);
            if (!notes.Contains("se reinició")) notes.Add("se puso en marcha");
        }
        return Task.FromResult($"El servicio {sc.DisplayName} {string.Join(" y ", notes.DefaultIfEmpty("ya estaba bien"))}.");
    }

    public Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct)
    {
        using var sc = new ServiceController(service);
        var ok = sc.StartType != ServiceStartMode.Disabled && sc.Status == ServiceControllerStatus.Running;
        return Task.FromResult(ok
            ? new VerifyResult(true, "el servicio está funcionando.")
            : new VerifyResult(false, $"el servicio quedó {(sc.StartType == ServiceStartMode.Disabled ? "deshabilitado" : "detenido")}."));
    }

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct)
    {
        if (snapshot.GetValueOrDefault("inicio") is { } previous && Enum.TryParse<ServiceStartMode>(previous, out var mode))
        {
            using var sc = new ServiceController(service);
            if (sc.StartType != mode) SetStartMode(service, mode);
        }
        return Task.CompletedTask;
    }

    /// <summary>Usa sc.exe (herramienta oficial vigente) para que el Administrador de servicios aplique el cambio en el momento.</summary>
    internal static void SetStartMode(string service, ServiceStartMode mode)
    {
        var arg = mode switch
        {
            ServiceStartMode.Automatic => "auto",
            ServiceStartMode.Manual => "demand",
            ServiceStartMode.Disabled => "disabled",
            _ => throw new InvalidOperationException($"tipo de inicio no soportado: {mode}"),
        };
        var exit = Tools.Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["config", service, "start=", arg], TimeSpan.FromSeconds(30));
        if (exit != 0) throw new InvalidOperationException($"sc.exe devolvió {exit}");
    }
}

/// <summary>Vacía la cola de impresión trabada: detiene el servicio, borra los trabajos pendientes y lo vuelve a iniciar.</summary>
internal sealed class ClearPrintQueueAction : IActionImpl
{
    private static readonly string[] SpoolPatterns = ["*.SHD", "*.SPL", "*.TMP"];

    private static string Folder(ActionContext ctx)
    {
        var custom = ctx.Param("carpeta");
        if (custom.Length == 0) return Path.Combine(Environment.SystemDirectory, "spool", "PRINTERS");
        var full = Path.GetFullPath(custom);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("carpeta no permitida");
        return full;
    }

    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct) =>
        Task.FromResult(new Dictionary<string, string?> { ["carpeta"] = Folder(ctx) });

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        var folder = Folder(ctx);
        using var sc = new ServiceController("Spooler");
        var deleted = 0;
        var dependents = Services.Stop(sc, TimeSpan.FromSeconds(40));
        try
        {
            foreach (var pattern in SpoolPatterns)
                foreach (var f in Directory.EnumerateFiles(folder, pattern))
                {
                    File.Delete(f);
                    deleted++;
                }
        }
        finally
        {
            // Pase lo que pase, la impresión no queda apagada.
            Services.Start(sc, dependents, TimeSpan.FromSeconds(40));
        }
        return Task.FromResult(deleted == 0
            ? "La cola de impresión estaba vacía; se reinició el servicio de impresión."
            : $"Se borraron {deleted} archivos de trabajos trabados y se reinició el servicio de impresión.");
    }

    public Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct)
    {
        using var sc = new ServiceController("Spooler");
        var left = SpoolPatterns.Sum(p => Directory.EnumerateFiles(Folder(ctx), p).Count());
        var ok = sc.Status == ServiceControllerStatus.Running && left == 0;
        return Task.FromResult(ok
            ? new VerifyResult(true, "la cola está vacía y el servicio de impresión funciona.")
            : new VerifyResult(false, left > 0 ? $"quedaron {left} trabajos en la cola." : "el servicio de impresión no arrancó."));
    }

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Acceso a la API oficial de DISM (dismapi.dll). Da el estado como número, sin depender del idioma de Windows.</summary>
internal static class Dism
{
    private const string OnlineImage = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";
    private const int AlreadyInitialized = unchecked((int)0xC0040002); // DISMAPI_E_DISMAPI_ALREADY_INITIALIZED (además, se inicializa una sola vez por proceso)
    private static readonly object Gate = new();
    private static bool _initialized;

    public enum Health { Healthy = 0, Repairable = 1, NonRepairable = 2 }

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    private static extern int DismInitialize(int logLevel, string? logFilePath, string? scratchDirectory);

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    private static extern int DismOpenSession(string imagePath, string? windowsDirectory, string? systemDrive, out uint session);

    [DllImport("dismapi.dll")]
    private static extern int DismCloseSession(uint session);

    [DllImport("dismapi.dll")]
    private static extern int DismCheckImageHealth(uint session, bool scanImage, IntPtr cancelEvent, IntPtr progress, IntPtr userData, out int health);

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    private static extern int DismRestoreImageHealth(uint session, string[]? sourcePaths, uint sourcePathCount, bool limitAccess, IntPtr cancelEvent, IntPtr progress, IntPtr userData);

    private static T WithSession<T>(Func<uint, T> work)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                var hr = DismInitialize(0, null, null);
                if (hr != 0 && hr != AlreadyInitialized) Marshal.ThrowExceptionForHR(hr);
                _initialized = true;
            }
            Check(DismOpenSession(OnlineImage, null, null, out var session));
            try { return work(session); }
            finally { DismCloseSession(session); }
        }
    }

    /// <param name="fullScan">true = análisis completo (varios minutos); false = lectura rápida del estado registrado.</param>
    public static Health CheckHealth(bool fullScan) => WithSession(s =>
    {
        Check(DismCheckImageHealth(s, fullScan, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var h));
        return (Health)h;
    });

    public static void RestoreHealth() => WithSession(s =>
    {
        Check(DismRestoreImageHealth(s, null, 0, false, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        return 0;
    });

    private static void Check(int hr)
    {
        if (hr == unchecked((int)0x800702E4)) throw new AccessDeniedException("DISM");
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }
}

/// <summary>Revisa a fondo los archivos de Windows (DISM ScanHealth). No cambia nada.</summary>
internal sealed class DismScanAction : IActionImpl
{
    private Dism.Health? _result;

    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct) => Task.FromResult(new Dictionary<string, string?>());

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        _result = Dism.CheckHealth(fullScan: true);
        return Task.FromResult(_result switch
        {
            Dism.Health.Healthy => "Los archivos de Windows están sanos.",
            Dism.Health.Repairable => "Hay archivos de Windows dañados que se pueden reparar: usá «Reparar los archivos de Windows».",
            _ => "Hay archivos de Windows dañados que no se pueden reparar desde Windows: hace falta una reparación con el instalador oficial.",
        });
    }

    public Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct) =>
        Task.FromResult(new VerifyResult(_result is not null, "el análisis terminó."));

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Repara los archivos de Windows con DISM RestoreHealth (descarga las piezas sanas de Windows Update).</summary>
internal sealed class DismRestoreAction : IActionImpl
{
    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct) => Task.FromResult(new Dictionary<string, string?>());

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        Dism.RestoreHealth();
        return Task.FromResult("Windows terminó de reparar sus archivos.");
    }

    public Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct)
    {
        var h = Dism.CheckHealth(fullScan: false);
        return Task.FromResult(h == Dism.Health.Healthy
            ? new VerifyResult(true, "los archivos de Windows quedaron sanos.")
            : new VerifyResult(false, "Windows sigue informando archivos dañados; hace falta una reparación con el instalador oficial."));
    }

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Acciones de Microsoft Defender mediante su herramienta oficial MpCmdRun.</summary>
internal static class Defender
{
    private const string Ns = @"root\Microsoft\Windows\Defender";

    public static string MpCmdRun()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        return File.Exists(path) ? path : throw new InvalidOperationException("Microsoft Defender no está instalado en esta PC");
    }

    public static ManagementBaseObject Status()
    {
        var rows = Wmi.Query(Ns, "SELECT * FROM MSFT_MpComputerStatus");
        return rows.Count > 0 ? rows[0] : throw new InvalidOperationException("Defender no informa su estado");
    }

    public static void RequireActive()
    {
        var s = Status();
        var mode = s.Str("AMRunningMode");
        if (!s.Get<bool>("RealTimeProtectionEnabled") || (mode.Length > 0 && !mode.Equals("Normal", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Microsoft Defender no es el antivirus activo de esta PC; usá el antivirus que tengas instalado");
    }

    public static DateTime? Time(ManagementBaseObject o, string prop)
    {
        var raw = o.Str(prop);
        if (raw.Length < 14) return null;
        try { return ManagementDateTimeConverter.ToDateTime(raw); } catch (ArgumentOutOfRangeException) { return null; }
    }

    public static int ThreatsSince(DateTime since) =>
        Wmi.Query(Ns, "SELECT InitialDetectionTime FROM MSFT_MpThreatDetection")
            .Count(t => Time(t, "InitialDetectionTime") is { } d && d >= since);
}

internal sealed class DefenderUpdateAction : IActionImpl
{
    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct) => Task.FromResult(new Dictionary<string, string?>());

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        Defender.RequireActive();
        var exit = Tools.Run(Defender.MpCmdRun(), ["-SignatureUpdate"], TimeSpan.FromMinutes(10));
        if (exit != 0) throw new InvalidOperationException("no se pudieron descargar las firmas (¿hay conexión a Internet?)");
        return Task.FromResult("Se actualizaron las firmas de Microsoft Defender.");
    }

    public Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct)
    {
        var age = Defender.Status().Get<uint>("AntivirusSignatureAge");
        return Task.FromResult(age <= 1
            ? new VerifyResult(true, "las firmas están al día.")
            : new VerifyResult(false, $"las firmas siguen teniendo {age} días."));
    }

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class DefenderQuickScanAction : IActionImpl
{
    private DateTime _start;
    private int _threats;

    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct) => Task.FromResult(new Dictionary<string, string?>());

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        Defender.RequireActive();
        _start = DateTime.Now.AddSeconds(-5);
        Tools.Run(Defender.MpCmdRun(), ["-Scan", "-ScanType", "1"], TimeSpan.FromMinutes(60));
        _threats = Defender.ThreatsSince(_start);
        return Task.FromResult(_threats == 0
            ? "Microsoft Defender terminó el análisis rápido y no encontró amenazas (con las firmas actuales)."
            : $"Microsoft Defender encontró {_threats} amenaza(s). Abrí Seguridad de Windows > Historial de protección para ver qué hizo con cada una.");
    }

    public Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct)
    {
        var end = Defender.Time(Defender.Status(), "QuickScanEndTime");
        return Task.FromResult(end is { } e && e >= _start
            ? new VerifyResult(true, "el análisis se completó.")
            : new VerifyResult(false, "Defender no registró el análisis como terminado."));
    }

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Análisis sin conexión de Microsoft Defender (clase WMI oficial MSFT_MpWDOScan): reinicia la PC y revisa el disco desde un
/// entorno de confianza, antes de que arranque Windows. Necesita el entorno de recuperación (WinRE) activado; si no, Windows no
/// hace nada y no avisa, por eso se comprueba antes.
/// </summary>
internal sealed class DefenderOfflineScanAction : IActionImpl
{
    private DateTime _start;

    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct) => Task.FromResult(new Dictionary<string, string?>());

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        Defender.RequireActive();
        var reagent = Tools.Capture(Path.Combine(Environment.SystemDirectory, "reagentc.exe"), ["/info"], TimeSpan.FromMinutes(1));
        if (System.Text.RegularExpressions.Regex.IsMatch(reagent, @"\b(Disabled|Deshabilitado)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidOperationException("la recuperación de Windows (WinRE) está apagada, y sin ella el análisis sin conexión no arranca");
        _start = DateTime.Now.AddSeconds(-5);
        using var cls = new ManagementClass(@"root\Microsoft\Windows\Defender", "MSFT_MpWDOScan", null);
        var result = cls.InvokeMethod("Start", null);
        if (result is not null && Convert.ToInt64(result) != 0) throw new InvalidOperationException($"Defender no pudo programar el análisis (código {result})");
        return Task.FromResult("Defender programó el análisis sin conexión: la PC se va a reiniciar en menos de un minuto. "
            + "Al volver a Windows, el resultado está en Seguridad de Windows > Historial de protección.");
    }

    public Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct)
    {
        // Evento 2030 de Defender: «configuró el análisis sin conexión para el próximo reinicio» (documentado por Microsoft).
        var query = new System.Diagnostics.Eventing.Reader.EventLogQuery("Microsoft-Windows-Windows Defender/Operational",
            System.Diagnostics.Eventing.Reader.PathType.LogName, "*[System[(EventID=2030)]]") { ReverseDirection = true };
        using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
        using var ev = reader.ReadEvent();
        return Task.FromResult(ev?.TimeCreated is { } t && t >= _start
            ? new VerifyResult(true, "Defender dejó listo el análisis para el reinicio.")
            : new VerifyResult(false, "Defender no registró el análisis programado."));
    }

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Detener/arrancar servicios sin dejar apagados a los que dependen de ellos.</summary>
internal static class Services
{
    /// <summary>Detiene el servicio y devuelve los servicios dependientes que estaban funcionando (Windows los detiene junto con él).</summary>
    public static List<string> Stop(ServiceController sc, TimeSpan wait)
    {
        var dependents = sc.DependentServices.Where(d => d.Status == ServiceControllerStatus.Running).Select(d => d.ServiceName).ToList();
        if (sc.Status != ServiceControllerStatus.Stopped)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, wait);
        }
        return dependents;
    }

    public static void Start(ServiceController sc, IEnumerable<string> dependents, TimeSpan wait)
    {
        sc.Refresh();
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, wait);
        }
        foreach (var name in dependents)
        {
            try
            {
                using var d = new ServiceController(name);
                if (d.Status != ServiceControllerStatus.Running) { d.Start(); d.WaitForStatus(ServiceControllerStatus.Running, wait); }
            }
            catch (InvalidOperationException) { /* un dependiente que no arranca no deshace la reparación principal */ }
        }
    }

    public static void Restart(ServiceController sc, TimeSpan wait)
    {
        var dependents = Stop(sc, wait);
        Start(sc, dependents, wait);
    }
}

internal static class Tools
{
    /// <summary>Como <see cref="Run"/>, pero devuelve lo que la herramienta escribió.</summary>
    public static string Capture(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"no se pudo iniciar {Path.GetFileName(exe)}");
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            throw new System.TimeoutException($"{Path.GetFileName(exe)} tardó demasiado");
        }
        return output.Result;
    }

    /// <summary>Ejecuta una herramienta oficial de Windows con argumentos fijos (nunca texto libre) y devuelve el código de salida.</summary>
    public static int Run(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"no se pudo iniciar {Path.GetFileName(exe)}");
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            throw new System.TimeoutException($"{Path.GetFileName(exe)} tardó demasiado");
        }
        return p.ExitCode;
    }
}

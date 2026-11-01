using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32;
using PCDoctor.Core;
using PCDoctor.Core.Actions;
using PCDoctor.Core.Knowledge;
using PCDoctor.Windows.Probes;

namespace PCDoctor.Windows.Actions;

/// <summary>Vacía la caché de nombres (DNS) de Windows. No cambia ninguna configuración.</summary>
internal sealed class FlushDnsAction : IActionImpl
{
    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct) => Task.FromResult(new Dictionary<string, string?>());

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        if (!Native.DnsFlushResolverCache()) throw new InvalidOperationException("Windows no aceptó vaciar la caché DNS");
        return Task.FromResult("Se vació la caché de nombres de Internet.");
    }

    public async Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct) =>
        await NetworkProbe.ResolveAnyAsync(["www.microsoft.com", "www.google.com"], ct)
            ? new VerifyResult(true, "los nombres de páginas web se resuelven correctamente.")
            : new VerifyResult(false, "los nombres de páginas siguen sin resolverse; el problema está en el router o en el proveedor.");

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Desactiva el proxy del usuario (servidor y script automático), guardando la configuración exacta para poder deshacerlo.</summary>
internal sealed class DisableProxyAction : IActionImpl
{
    internal const string RealKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    internal const string TestRoot = @"Software\PCDoctorTests\";
    private static readonly string[] Values = ["ProxyEnable", "ProxyServer", "AutoConfigURL", "ProxyOverride"];

    private static string KeyPath(ActionContext ctx)
    {
        var custom = ctx.Param("clave");
        if (custom.Length == 0) return RealKey;
        // Solo el laboratorio puede redirigir la acción, y únicamente a una clave de prueba.
        if (!custom.StartsWith(TestRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("clave no permitida");
        return custom;
    }

    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath(ctx));
        var snap = new Dictionary<string, string?>();
        foreach (var v in Values)
        {
            var value = key.GetValue(v);
            snap[v] = value is null ? null : $"{key.GetValueKind(v)}|{value}";
        }
        return Task.FromResult(snap);
    }

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        var path = KeyPath(ctx);
        using (var key = Registry.CurrentUser.CreateSubKey(path))
        {
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        }
        if (path == RealKey) NotifyWinInet();
        return Task.FromResult("Se desactivó el proxy.");
    }

    public Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct)
    {
        var path = KeyPath(ctx);
        using var key = Registry.CurrentUser.OpenSubKey(path);
        var enabled = key?.GetValue("ProxyEnable") is int i && i == 1;
        var pac = key?.GetValue("AutoConfigURL") as string;
        if (enabled || !string.IsNullOrEmpty(pac))
            return Task.FromResult(new VerifyResult(false, "el proxy sigue configurado."));
        if (path == RealKey)
        {
            // Lo que importa es lo que Windows usa de verdad, no solo lo que se escribió.
            if (PolicyForcesProxy()) return Task.FromResult(new VerifyResult(false, "el proxy lo impone una política de la organización; hay que consultar con quien administra la PC."));
            if (EffectiveProxy() is { } effective) return Task.FromResult(new VerifyResult(false, $"Windows sigue usando un proxy ({effective})."));
        }
        return Task.FromResult(new VerifyResult(true, "Windows ya no usa proxy."));
    }

    private static bool PolicyForcesProxy() =>
        Reg.Int(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings", "ProxySettingsPerUser") == 0
        || Reg.Int(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Internet Explorer\Control Panel", "Proxy") == 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINHTTP_CURRENT_USER_IE_PROXY_CONFIG
    {
        public bool fAutoDetect;
        public IntPtr lpszAutoConfigUrl;
        public IntPtr lpszProxy;
        public IntPtr lpszProxyBypass;
    }

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(ref WINHTTP_CURRENT_USER_IE_PROXY_CONFIG config);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr mem);

    /// <summary>Proxy efectivo del usuario según WinHTTP (o null si va directo).</summary>
    internal static string? EffectiveProxy()
    {
        var cfg = new WINHTTP_CURRENT_USER_IE_PROXY_CONFIG();
        if (!WinHttpGetIEProxyConfigForCurrentUser(ref cfg)) return null;
        try
        {
            var proxy = Marshal.PtrToStringUni(cfg.lpszProxy);
            var pac = Marshal.PtrToStringUni(cfg.lpszAutoConfigUrl);
            return !string.IsNullOrEmpty(proxy) ? proxy : !string.IsNullOrEmpty(pac) ? pac : null;
        }
        finally
        {
            foreach (var p in new[] { cfg.lpszAutoConfigUrl, cfg.lpszProxy, cfg.lpszProxyBypass })
                if (p != IntPtr.Zero) GlobalFree(p);
        }
    }

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct)
    {
        var path = KeyPath(ctx);
        using (var key = Registry.CurrentUser.CreateSubKey(path))
        {
            foreach (var v in Values)
            {
                if (!snapshot.TryGetValue(v, out var stored) || stored is null)
                {
                    key.DeleteValue(v, throwOnMissingValue: false);
                    continue;
                }
                var sep = stored.IndexOf('|');
                var kind = Enum.Parse<RegistryValueKind>(stored[..sep]);
                var raw = stored[(sep + 1)..];
                object value = kind == RegistryValueKind.DWord ? int.Parse(raw) : raw;
                key.SetValue(v, value, kind);
            }
        }
        if (path == RealKey) NotifyWinInet();
        return Task.CompletedTask;
    }

    private static void NotifyWinInet()
    {
        Native.InternetSetOption(IntPtr.Zero, Native.INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        Native.InternetSetOption(IntPtr.Zero, Native.INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }
}

/// <summary>Borra archivos temporales del usuario con más de 2 días. Nunca toca documentos ni archivos en uso.</summary>
internal sealed class CleanTempAction : IActionImpl
{
    internal static readonly TimeSpan MinAge = TimeSpan.FromDays(2);
    private static readonly string[] Protected = [".net", "PCDoctor"]; // extracción de esta misma app
    private int _deleted, _skipped;
    private long _bytes;

    /// <summary>
    /// Solo se limpia la carpeta de temporales estándar del usuario (%LOCALAPPDATA%\Temp). Si TEMP apunta a otro
    /// lado (por error o a propósito: el perfil, un disco entero), no se toca nada.
    /// </summary>
    internal static string SafeTempRoot()
    {
        var expected = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")).TrimEnd('\\');
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        if (!temp.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("la carpeta de temporales de esta PC no es la estándar de Windows; por seguridad no se toca");
        RejectReparsePoint(temp);
        return temp;
    }

    private static string Folder(ActionContext ctx)
    {
        var temp = SafeTempRoot();
        var custom = ctx.Param("carpeta");
        if (custom.Length == 0) return temp;
        // Solo el laboratorio usa otra carpeta, y únicamente dentro de los temporales.
        var full = Path.GetFullPath(custom).TrimEnd('\\');
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("carpeta no permitida");
        for (var d = new DirectoryInfo(full); d is not null && d.FullName.Length > temp.Length; d = d.Parent) RejectReparsePoint(d.FullName);
        return full;
    }

    /// <summary>Un enlace (junction) disfrazado de carpeta podría llevar el borrado a otro lugar: se rechaza.</summary>
    private static void RejectReparsePoint(string dir)
    {
        if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("la carpeta es un enlace a otro lugar; no se toca");
    }

    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct) =>
        Task.FromResult(new Dictionary<string, string?> { ["carpeta"] = Folder(ctx) });

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        var root = new DirectoryInfo(Folder(ctx));
        var cutoff = DateTime.Now - MinAge;
        _deleted = 0; _skipped = 0; _bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in root.EnumerateFiles("*", options)) TryDelete(f, cutoff);
        foreach (var d in root.EnumerateDirectories("*", options))
        {
            if (Protected.Any(p => d.Name.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
            ct.ThrowIfCancellationRequested();
            CleanDirectory(d, cutoff, options);
        }
        return Task.FromResult($"Se borraron {_deleted} archivos temporales ({Labels.Number(Math.Round(_bytes / 1024.0 / 1024, 1))} MB). {(_skipped > 0 ? $"{_skipped} estaban en uso o eran recientes y se dejaron." : "")}".Trim());
    }

    private void CleanDirectory(DirectoryInfo dir, DateTime cutoff, EnumerationOptions options)
    {
        foreach (var f in dir.EnumerateFiles("*", options)) TryDelete(f, cutoff);
        foreach (var sub in dir.EnumerateDirectories("*", options)) CleanDirectory(sub, cutoff, options);
        try
        {
            if (!dir.EnumerateFileSystemInfos().Any() && dir.LastWriteTime < cutoff) dir.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void TryDelete(FileInfo f, DateTime cutoff)
    {
        try
        {
            if (f.LastWriteTime > cutoff) { _skipped++; return; }
            var len = f.Length;
            f.Delete();
            _deleted++;
            _bytes += len;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _skipped++;
        }
    }

    public Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct)
    {
        var cutoff = DateTime.Now - MinAge;
        var remainingOld = new DirectoryInfo(Folder(ctx))
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true })
            .Count(f => f.LastWriteTime < cutoff);
        return Task.FromResult(new VerifyResult(true, remainingOld == 0
            ? "no quedan temporales viejos que se puedan borrar."
            : $"quedan {remainingOld} archivos viejos que están en uso por programas abiertos (se pueden borrar después de reiniciar)."));
    }

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Abre una página de Configuración de Windows o una página oficial de ayuda. No cambia nada.</summary>
internal sealed class OpenSettingsAction : IActionImpl
{
    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct) => Task.FromResult(new Dictionary<string, string?>());

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        var uri = ctx.Param("uri");
        if (!KnowledgeBase.AllowedUrlPrefixes.Any(p => uri.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("destino no permitido");
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        return Task.FromResult("Se abrió la configuración.");
    }

    public Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct) => Task.FromResult(new VerifyResult(true, "abierta."));

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct) => Task.CompletedTask;
}

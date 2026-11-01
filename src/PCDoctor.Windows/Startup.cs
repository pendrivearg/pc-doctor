using Microsoft.Win32;
using PCDoctor.Core;
using PCDoctor.Core.Actions;
using PCDoctor.Windows.Probes;

namespace PCDoctor.Windows;

public enum StartupOrigin { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

/// <summary>Un programa que arranca con Windows, tal como lo muestra el Administrador de tareas.</summary>
public sealed record StartupItem(string DisplayName, string ValueName, StartupOrigin Origin, bool Enabled)
{
    /// <summary>Los de todos los usuarios (HKLM y carpeta común) necesitan permiso de administrador para desactivarse.</summary>
    public bool NeedsAdmin => Origin is StartupOrigin.MachineRun or StartupOrigin.MachineRun32 or StartupOrigin.CommonFolder;
    public string ActionId => NeedsAdmin ? StartupCatalog.DisableMachineAction : StartupCatalog.DisableUserAction;
}

public static class StartupCatalog
{
    public const string DisableUserAction = "startup.disable.user";
    public const string DisableMachineAction = "startup.disable.machine";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Programas que nunca se ofrecen para desactivar: son de seguridad o de Windows.</summary>
    internal static readonly string[] Protected = ["SecurityHealth", "WindowsDefender", "Windows Defender"];

    public static IReadOnlyList<StartupItem> Items()
    {
        var items = new List<StartupItem>();
        void FromRun(RegistryHive hive, RegistryView view, StartupOrigin origin)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(RunKey);
            if (key is null) return;
            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
                items.Add(new StartupItem(name, name, origin, IsEnabled(origin, name)));
        }
        Try(() => FromRun(RegistryHive.CurrentUser, RegistryView.Default, StartupOrigin.UserRun));
        Try(() => FromRun(RegistryHive.LocalMachine, RegistryView.Registry64, StartupOrigin.MachineRun));
        Try(() => FromRun(RegistryHive.LocalMachine, RegistryView.Registry32, StartupOrigin.MachineRun32));
        foreach (var (folder, origin) in new[] { (Environment.SpecialFolder.Startup, StartupOrigin.UserFolder), (Environment.SpecialFolder.CommonStartup, StartupOrigin.CommonFolder) })
        {
            Try(() =>
            {
                var dir = Environment.GetFolderPath(folder);
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                    items.Add(new StartupItem(Path.GetFileNameWithoutExtension(f), Path.GetFileName(f), origin, IsEnabled(origin, Path.GetFileName(f))));
            });
        }
        return items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsProtected(StartupItem item) => Protected.Any(p => item.ValueName.Equals(p, StringComparison.OrdinalIgnoreCase));

    internal static (RegistryHive hive, RegistryView view, string sub) ApprovedLocation(StartupOrigin origin) => origin switch
    {
        StartupOrigin.UserRun => (RegistryHive.CurrentUser, RegistryView.Default, "Run"),
        StartupOrigin.MachineRun => (RegistryHive.LocalMachine, RegistryView.Registry64, "Run"),
        StartupOrigin.MachineRun32 => (RegistryHive.LocalMachine, RegistryView.Registry64, "Run32"),
        StartupOrigin.UserFolder => (RegistryHive.CurrentUser, RegistryView.Default, "StartupFolder"),
        StartupOrigin.CommonFolder => (RegistryHive.LocalMachine, RegistryView.Registry64, "StartupFolder"),
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    /// <summary>El Administrador de tareas guarda en StartupApproved un binario: primer byte impar = deshabilitado.</summary>
    private static bool IsEnabled(StartupOrigin origin, string valueName)
    {
        var (hive, view, sub) = ApprovedLocation(origin);
        var v = Reg.Value(hive, $@"{ApprovedKey}\{sub}", valueName, view);
        return v is not byte[] { Length: > 0 } bytes || (bytes[0] & 1) == 0;
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }
}

/// <summary>Programas que arrancan con Windows.</summary>
internal sealed class StartupProbe : ProbeBase
{
    public override string Id => "inicio";
    public override string Title => "Programas que arrancan con Windows";
    public override IReadOnlyList<string> Areas => [Core.Areas.Performance, Core.Areas.Security];
    public override IReadOnlyList<string> FactKeys => ["inicio.activos", "inicio.lista"];

    protected override Task Run(ProbeContext ctx, ProbeResult r, CancellationToken ct)
    {
        var enabled = StartupCatalog.Items().Where(i => i.Enabled).Select(i => i.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        r.Facts.Set("inicio.activos", enabled.Count);
        if (enabled.Count > 0) r.Facts.Set("inicio.lista", string.Join(", ", enabled));
        r.Detail("Programas activos al inicio", enabled.Count);
        foreach (var i in enabled) r.Detail("Arranca con Windows", i);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Desactiva un programa de inicio exactamente como lo hace el Administrador de tareas (sin borrar nada):
/// escribe el valor «deshabilitado» en StartupApproved. Deshacer restaura el valor anterior.
/// Parámetros: nombre (valor o archivo), origen (StartupOrigin). El laboratorio puede redirigir a «raiz».
/// </summary>
internal sealed class StartupDisableAction(bool machine) : IActionImpl
{
    internal const string TestRoot = @"Software\PCDoctorTests\";

    private static (RegistryKey baseKey, string path) Target(ActionContext ctx, bool machine)
    {
        var name = ctx.Param("nombre");
        if (name.Length == 0) throw new InvalidOperationException("falta el nombre del programa");
        if (StartupCatalog.Protected.Any(p => name.Equals(p, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("ese programa es de seguridad de Windows y no se desactiva");
        // Solo nombres de origen válidos: un número como «99» no puede colarse a la rama de HKLM.
        if (!Enum.TryParse<StartupOrigin>(ctx.Param("origen"), out var origin) || !Enum.IsDefined(origin)
            || int.TryParse(ctx.Param("origen"), out _))
            throw new InvalidOperationException("origen inválido");
        if (name.IndexOfAny(['\\', '/', '\0']) >= 0) throw new InvalidOperationException("nombre inválido");
        var forMachine = origin is StartupOrigin.MachineRun or StartupOrigin.MachineRun32 or StartupOrigin.CommonFolder;
        if (forMachine != machine) throw new InvalidOperationException("origen no corresponde a esta acción");
        var (hive, view, sub) = StartupCatalog.ApprovedLocation(origin);
        var custom = ctx.Param("raiz");
        if (custom.Length > 0)
        {
            if (!custom.StartsWith(TestRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("clave no permitida");
            return (RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default), $@"{custom}\{sub}");
        }
        return (RegistryKey.OpenBaseKey(hive, view), $@"{StartupCatalog.ApprovedKey}\{sub}");
    }

    public Task<Dictionary<string, string?>> SnapshotAsync(ActionContext ctx, CancellationToken ct)
    {
        var (root, path) = Target(ctx, machine);
        using (root)
        using (var key = root.OpenSubKey(path))
        {
            var v = key?.GetValue(ctx.Param("nombre")) as byte[];
            return Task.FromResult(new Dictionary<string, string?> { ["valor"] = v is null ? null : Convert.ToBase64String(v) });
        }
    }

    public Task<string> ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        var (root, path) = Target(ctx, machine);
        using (root)
        using (var key = root.CreateSubKey(path))
        {
            var data = new byte[12];
            data[0] = 0x03; // deshabilitado (mismo formato que el Administrador de tareas)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            key.SetValue(ctx.Param("nombre"), data, RegistryValueKind.Binary);
        }
        return Task.FromResult($"«{ctx.Param("nombre")}» ya no arranca solo con Windows (no se desinstaló: lo podés abrir cuando quieras).");
    }

    public Task<VerifyResult> VerifyAsync(ActionContext ctx, CancellationToken ct)
    {
        var (root, path) = Target(ctx, machine);
        using (root)
        using (var key = root.OpenSubKey(path))
        {
            var ok = key?.GetValue(ctx.Param("nombre")) is byte[] { Length: > 0 } b && (b[0] & 1) == 1;
            return Task.FromResult(new VerifyResult(ok, ok ? "quedó desactivado del inicio." : "Windows no guardó el cambio."));
        }
    }

    public Task RollbackAsync(ActionContext ctx, Dictionary<string, string?> snapshot, CancellationToken ct)
    {
        var (root, path) = Target(ctx, machine);
        using (root)
        using (var key = root.CreateSubKey(path))
        {
            if (snapshot.GetValueOrDefault("valor") is { } b64) key.SetValue(ctx.Param("nombre"), Convert.FromBase64String(b64), RegistryValueKind.Binary);
            else key.DeleteValue(ctx.Param("nombre"), throwOnMissingValue: false);
        }
        return Task.CompletedTask;
    }
}

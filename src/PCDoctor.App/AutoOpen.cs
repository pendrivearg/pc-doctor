using System.Diagnostics;
using Microsoft.Win32;
using PCDoctor.Core;
using PCDoctor.Core.Updates;

namespace PCDoctor.App;

/// <summary>
/// «Que se abra solo al enchufar el pendrive», en esta PC. Solo si la persona lo pide: copia el abridor (PCDoctor-Abrir.exe,
/// unos KB) a su carpeta de usuario y lo anota para que arranque con Windows. Sin permiso de administrador.
/// Se quita con un botón: borra la copia y la anotación, y la PC queda como estaba.
/// </summary>
internal sealed class AutoOpen(string installDir, string runKeyPath = AutoOpen.RunKey)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "PCDoctorAbrirAlEnchufar";
    public const string ExeName = "PCDoctor-Abrir.exe";

    public static AutoOpen ForThisUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCDoctor", "abrir-al-enchufar"));

    public string ExePath => Path.Combine(installDir, ExeName);

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath);
            return key?.GetValue(ValueName) is string && File.Exists(ExePath);
        }
    }

    /// <param name="sourceExe">El abridor que viene en el pendrive (pcdoctor\PCDoctor-Abrir.exe).</param>
    public void Enable(string sourceExe, bool start = true)
    {
        StopRunning();
        Directory.CreateDirectory(installDir);
        File.Copy(sourceExe, ExePath, overwrite: true);
        using (var key = Registry.CurrentUser.CreateSubKey(runKeyPath))
            key.SetValue(ValueName, $"\"{ExePath}\"");
        AppLog.Info("Abrir al enchufar: activado en esta PC");
        if (start) Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = installDir })?.Dispose();
    }

    public void Disable()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true))
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        StopRunning();
        for (var i = 0; i < 10 && Directory.Exists(installDir); i++)
        {
            try { Directory.Delete(installDir, recursive: true); }
            catch (IOException) { Thread.Sleep(300); } // el abridor tarda un instante en soltar su archivo
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
        AppLog.Info("Abrir al enchufar: quitado de esta PC");
    }

    /// <summary>Si ya está activado y el pendrive trae un abridor más nuevo, lo reemplaza (la PC queda al día sin preguntar de nuevo).</summary>
    public bool Refresh(string sourceExe, bool start = true)
    {
        if (!IsEnabled || !File.Exists(sourceExe)) return false;
        if (ProgramSeal.Sha256Hex(sourceExe) == ProgramSeal.Sha256Hex(ExePath)) return false;
        Enable(sourceExe, start);
        return true;
    }

    private void StopRunning()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            using (p)
                try
                {
                    if (string.Equals(p.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}

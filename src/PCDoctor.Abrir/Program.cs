using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using PCDoctor.Core.Updates;

namespace PCDoctor.Abrir;

/// <summary>
/// Abre PC Doctor apenas se enchufa el pendrive. Windows no deja que un pendrive abra nada solo (lo bloquea contra los virus),
/// así que esto queda en la PC, solo si la persona lo pide desde PC Doctor, y se quita desde ahí mismo.
/// No hace nada más: espera el aviso de Windows de «se conectó una unidad», sin revisar cada tanto, y solo abre
/// un programa con el sello firmado de PC Doctor (<see cref="ProgramSeal"/>).
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using (var single = new Mutex(true, @"Local\PCDoctorAI-Abrir", out var first))
        {
            if (!first) return;
            using (var watcher = new DriveWatcher()) Application.Run();
        }
    }
}

/// <summary>Ventana invisible que recibe el aviso WM_DEVICECHANGE de Windows cuando aparece una unidad nueva.</summary>
internal sealed class DriveWatcher : NativeWindow, IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDevTypVolume = 2;
    private readonly Dictionary<char, DateTime> _recent = new Dictionary<char, DateTime>();
    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "registro.txt");

    public DriveWatcher() => CreateHandle(new CreateParams { Caption = "PC Doctor - abrir al enchufar" });

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDeviceChange && m.WParam.ToInt64() == DbtDeviceArrival && m.LParam != IntPtr.Zero
            && Marshal.ReadInt32(m.LParam, 4) == DbtDevTypVolume)
        {
            var mask = Marshal.ReadInt32(m.LParam, 12);
            for (var i = 0; i < 26; i++)
                if ((mask & (1 << i)) != 0) Arrived((char)('A' + i));
        }
        base.WndProc(ref m);
    }

    private void Arrived(char letter)
    {
        lock (_recent)
        {
            // Windows a veces avisa dos veces la misma unidad: se abre una sola vez.
            if (_recent.TryGetValue(letter, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(20)) return;
            _recent[letter] = DateTime.UtcNow;
        }
        ThreadPool.QueueUserWorkItem(_ => Check(letter + ":\\"));
    }

    private static void Check(string root)
    {
        try
        {
            var program = Path.Combine(root, ProgramSeal.ProgramFile);
            // La unidad tarda unos segundos en estar lista.
            for (var i = 0; i < 20 && !Directory.Exists(root); i++) Thread.Sleep(500);
            if (!File.Exists(program)) return;
            if (!ProgramSeal.Verify(root, UpdateKeys.PublicKeyPem))
            {
                Log($"{root}: tiene «{ProgramSeal.ProgramFile}» pero sin el sello de PC Doctor: no se abre.");
                return;
            }
            Log($"{root}: pendrive de PC Doctor, se abre.");
            Process.Start(new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = root })?.Dispose();
        }
        catch (Exception e) { Log($"{root}: {e.Message}"); }
    }

    private static void Log(string text)
    {
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 64 * 1024) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:dd/MM/yyyy HH:mm:ss} {text}\r\n", System.Text.Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => DestroyHandle();
}

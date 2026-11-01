using System.Text;

namespace PCDoctor.Rescue;

/// <summary>
/// PC Doctor — modo rescate. Arranca solo desde el pendrive (sin Windows) y ofrece, con letra grande y en castellano:
/// rescatar fotos y documentos, revisar virus y señales de hackeo con Windows apagado, ver la salud del disco y
/// qué hacer si Windows no arranca. Nunca escribe en el disco de la PC salvo para la cuarentena, y solo si el
/// usuario lo acepta y Windows no quedó hibernado.
/// </summary>
public static class Program
{
    private sealed record WindowsVolume(string Device, string Uuid, string MountPoint, string Label);

    private static readonly List<WindowsVolume> Windows = new();
    private static readonly List<BlockDevice> Locked = new();
    private static string? _dataRoot;     // parte de datos del pendrive, montada con escritura
    private static string? _sessionDir;   // RESCATE/<fecha> dentro del pendrive
    private static readonly StringBuilder Report = new();
    private static string _network = "Buscando conexión a Internet…";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("Este programa es el modo rescate de PC Doctor: corre al arrancar la PC desde el pendrive.");
            return 1;
        }
        LinuxSystem.Run("setfont", "/usr/share/consolefonts/Uni2-TerminusBold32x16.psf.gz", 5000);
        Header("Preparando el modo rescate…");
        Say("Buscando los discos de esta computadora. No se cambia nada en ellos.");
        PrepareDisks();
        _ = Task.Run(PrepareNetwork);
        Log($"Modo rescate iniciado: {DateTime.Now:dd/MM/yyyy HH:mm}");

        while (true)
        {
            Header("PC DOCTOR — MODO RESCATE");
            if (Windows.Count == 0 && Locked.Count == 0) Warn("No encontré Windows en esta computadora (o el disco no se puede leer).");
            foreach (var w in Windows) Say($"Windows encontrado en {w.Device} {(w.Label.Length > 0 ? "(" + w.Label + ")" : "")}");
            foreach (var l in Locked) Warn($"Disco cifrado con BitLocker en {l.Path}: hace falta la clave de recuperación (opción 8).");
            Say(_dataRoot is null ? "⚠ No encontré la parte de datos del pendrive: no se pueden guardar archivos." : "Lo que rescates se guarda en el pendrive, carpeta RESCATE.");
            Say(_network);
            Console.WriteLine();
            Say("1. Rescatar mis fotos y documentos");
            Say("2. Revisar si la PC tiene virus o está hackeada");
            Say("3. Ver si el disco está fallando");
            Say("4. Windows no arranca: qué hacer");
            Say("5. Archivos en cuarentena (devolverlos)");
            if (Locked.Count > 0) Say("8. Abrir un disco cifrado (BitLocker)");
            Say("6. Apagar la computadora      7. Reiniciar");
            Say("T. Consola para técnicos");
            Console.WriteLine();
            Console.Write("Elegí un número: ");
            var key = char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar);
            try
            {
                switch (key)
                {
                    case '1': RescueFiles(); break;
                    case '2': SecurityCheck(); break;
                    case '3': DiskHealth(); break;
                    case '4': WindowsWontStart(); break;
                    case '5': QuarantineMenu(); break;
                    case '8' when Locked.Count > 0: UnlockMenu(); break;
                    case '6': SaveReport(); LinuxSystem.Run("systemctl", "poweroff"); return 0;
                    case '7': SaveReport(); LinuxSystem.Run("systemctl", "reboot"); return 0;
                    case 'T': TechConsole(); break;
                }
            }
            catch (Exception ex)
            {
                Warn("Algo salió mal: " + ex.Message);
                Log("ERROR: " + ex);
                Pause();
            }
        }
    }

    // ---------- Discos ----------

    private static void PrepareDisks()
    {
        var devices = LinuxSystem.Devices();
        var bootDisk = LinuxSystem.BootDisk(devices);
        var data = devices.FirstOrDefault(d => d.Parent == bootDisk && d.Type == "part" && d.MountPoint != LinuxSystem.MediumMount
                                               && d.FsType is "exfat" or "vfat" or "ntfs");
        if (data is not null)
        {
            var fs = data.FsType == "ntfs" ? "ntfs3" : data.FsType!;
            if (LinuxSystem.Mount(data.Path, "/mnt/pcdoctor", fs, readOnly: false, out _))
            {
                _dataRoot = "/mnt/pcdoctor";
                _sessionDir = Path.Combine(_dataRoot, "RESCATE", DateTime.Now.ToString("yyyy-MM-dd_HH-mm"));
            }
        }
        foreach (var d in devices.Where(d => d.Parent != bootDisk && d.Name != bootDisk))
        {
            if (d.FsType == "BitLocker") { Locked.Add(d); continue; }
            if (d.FsType != "ntfs") continue;
            TryAddWindows(d.Path, d.Uuid ?? d.Name, d.Label ?? "");
        }
    }

    private static void TryAddWindows(string device, string uuid, string label)
    {
        var mp = "/mnt/win-" + Path.GetFileName(device);
        if (!LinuxSystem.Mount(device, mp, "ntfs3", readOnly: true, out var err)) { Log($"No se pudo abrir {device}: {err}"); return; }
        if (OfflineInspector.LooksLikeWindows(mp)) Windows.Add(new(device, uuid, mp, label));
        else LinuxSystem.Unmount(mp);
    }

    private static void UnlockMenu()
    {
        foreach (var disk in Locked.ToList())
        {
            Header("Disco cifrado con BitLocker");
            Say("Este disco está protegido. Para leerlo hace falta la «clave de recuperación de BitLocker»: 48 números.");
            Say("Si la PC usa una cuenta de Microsoft, la clave está en https://aka.ms/myrecoverykey (entrá desde un celular).");
            Say("PC Doctor no guarda la clave: solo la usa ahora para abrir el disco en modo lectura.");
            Console.Write("\nEscribí la clave (o Enter para volver): ");
            var keyText = Console.ReadLine() ?? "";
            if (keyText.Trim().Length == 0) return;
            if (!LinuxSystem.Has("cryptsetup")) { Warn("Falta la herramienta para abrir BitLocker en este pendrive."); Pause(); return; }
            var opened = LinuxSystem.UnlockBitLocker(disk.Path, keyText, out var err);
            if (opened is null) { Warn("No se pudo abrir: " + (err.Length > 0 ? err : "la clave no es correcta.")); Pause(); continue; }
            Locked.Remove(disk);
            TryAddWindows(opened, disk.Uuid ?? disk.Name, disk.Label ?? "");
            Say("Listo: el disco quedó abierto en modo lectura.");
            Pause();
        }
    }

    // ---------- 1. Rescate de archivos ----------

    private static void RescueFiles()
    {
        Header("Rescatar fotos y documentos");
        if (!RequireWindows() || !RequireDataRoot()) return;
        foreach (var w in Windows)
        {
            var plan = FileRescue.Plan(new OfflineInspector(w.MountPoint));
            var free = new DriveInfo(_dataRoot!).AvailableFreeSpace;
            Say($"Encontré {plan.FileCount:N0} archivos personales ({Gb(plan.TotalBytes)}) de: {string.Join(", ", plan.Items.Select(i => i.User).Distinct())}.");
            Say($"Espacio libre en el pendrive: {Gb(free)}.");
            if (plan.TotalBytes > free)
            {
                Warn("No entra todo en el pendrive. Conectá un disco externo más grande, o se copiará solo lo que entre (primero Escritorio y Documentos).");
            }
            Console.Write("\n¿Empiezo a copiar? (S/N) ");
            if (!Yes()) return;
            var dest = Path.Combine(_sessionDir!, "Archivos");
            var last = DateTime.MinValue;
            var result = FileRescue.Copy(plan, dest, (done, total, current) =>
            {
                if ((DateTime.Now - last).TotalMilliseconds < 500) return;
                last = DateTime.Now;
                var pct = total == 0 ? 100 : (int)(done * 100 / total);
                Console.Write($"\r{pct,3} %  {Gb(done)} de {Gb(total)}   ");
            });
            Console.WriteLine();
            Say($"Listo: {result.Copied:N0} copiados, {result.Skipped:N0} ya estaban, {result.Failed:N0} no se pudieron leer.");
            if (result.Failed > 0) Warn("Los que no se pudieron leer suelen ser zonas dañadas del disco. La lista está en el informe.");
            Say($"Quedaron en el pendrive: RESCATE/{Path.GetFileName(_sessionDir)}/Archivos");
            Log($"Rescate de {w.Device}: {result.Copied} copiados, {result.Skipped} existentes, {result.Failed} con error ({Gb(result.Bytes)}).");
            foreach (var e in result.Errors) Log("  no se pudo leer: " + e);
        }
        SaveReport();
        Pause();
    }

    // ---------- 2. Revisión de seguridad ----------

    private static void SecurityCheck()
    {
        Header("Revisión de virus y hackeo (con Windows apagado)");
        if (!RequireWindows()) return;
        Say("Con Windows apagado, los virus no se pueden esconder ni defender. Se revisa sin cambiar nada.");
        foreach (var w in Windows)
        {
            var inspector = new OfflineInspector(w.MountPoint);
            Say("\nRevisando arranque automático, tareas, servicios, antivirus y programas de control remoto…");
            var findings = inspector.Inspect();

            var hits = new List<VirusHit>();
            var sigs = LinuxSystem.SignatureDir();
            if (sigs is null || !LinuxSystem.Has("clamscan"))
                Warn("No hay firmas de virus en este pendrive. Conectá un cable de red y volvé a entrar para descargarlas.");
            else
            {
                Say($"Buscando virus con ClamAV (firmas del {LinuxSystem.SignatureDate(sigs)}). Puede tardar entre 10 y 40 minutos…");
                var targets = ScanTargets(inspector, findings);
                var count = 0;
                hits = LinuxSystem.Scan(targets, sigs, f => Console.Write($"\rVirus encontrados: {++count}   "), CancellationToken.None);
                Console.WriteLine();
            }

            var suspicious = findings.Where(f => f.Severity == Severity.Suspicious).ToList();
            var warnings = findings.Where(f => f.Severity == Severity.Warning).ToList();
            Header("Resultado de la revisión");
            if (hits.Count == 0 && suspicious.Count == 0 && warnings.Count == 0)
                Good("No encontré virus ni señales de hackeo.");
            foreach (var h in hits.Take(10)) Bad($"VIRUS: {Short(h.File, w.MountPoint)}  ({h.Signature})");
            if (hits.Count > 10) Bad($"… y {hits.Count - 10} más (están en el informe).");
            foreach (var f in suspicious.Take(8)) { Bad($"SOSPECHOSO — {f.Where}: {f.What}"); Say("   " + f.Why); }
            foreach (var f in warnings.Take(6)) { Warn($"ATENCIÓN — {f.Where}: {f.What}"); Say("   " + f.Why); }
            Say($"\n({findings.Count(f => f.Severity == Severity.Info)} elementos normales que arrancan con Windows están en el informe.)");

            Log($"Revisión de seguridad de {w.Device}:");
            foreach (var h in hits) Log($"  VIRUS {h.Signature}: {h.File}");
            foreach (var f in findings) Log($"  [{f.Severity}] {f.Where}: {f.What} — {f.Why}");

            if (hits.Count > 0) OfferQuarantine(w, hits);
            if (hits.Count > 0 || suspicious.Count > 0)
            {
                Say("\nQué hacer después:");
                Say("• Cambiá las claves del correo y del banco desde OTRO dispositivo (un celular).");
                Say("• Al volver a Windows, abrí PC Doctor y hacé «Análisis sin conexión de Microsoft Defender».");
                Say("• Si alguien te llamó «de soporte» y le diste acceso, avisá a tu banco.");
            }
        }
        SaveReport();
        Pause();
    }

    private static IEnumerable<string> ScanTargets(OfflineInspector inspector, List<Finding> findings)
    {
        var root = inspector.Root;
        foreach (var f in findings.Where(f => f.FilePath is not null)) yield return f.FilePath!;
        foreach (var rel in new[] { @"Windows\Temp", @"ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp", "ProgramData" })
            if (PathCi.Find(root, rel) is { } p) yield return p;
        foreach (var (_, profile) in inspector.UserProfiles())
            foreach (var rel in new[] { "Downloads", "Desktop", @"AppData\Roaming", @"AppData\Local\Temp", @"AppData\Local\Programs" })
                if (PathCi.Find(profile, rel) is { } p) yield return p;
    }

    private static void OfferQuarantine(WindowsVolume w, List<VirusHit> hits)
    {
        if (_dataRoot is null) return;
        if (LinuxSystem.IsHibernated(w.MountPoint))
        {
            Warn("\nWindows quedó en «inicio rápido» o hibernado: mover archivos ahora podría dañarlo.");
            Say("Prendé Windows, apagalo con Mayúscula apretada + Apagar, y volvé a arrancar el rescate para poner los virus en cuarentena.");
            return;
        }
        Console.Write($"\n¿Pongo los {hits.Count} virus en cuarentena? Se guardan en el pendrive y se pueden devolver. (S/N) ");
        if (!Yes()) return;
        LinuxSystem.Unmount(w.MountPoint);
        if (!LinuxSystem.Mount(w.Device, w.MountPoint, "ntfs3", readOnly: false, out var err))
        {
            Warn("No se pudo abrir el disco para escribir: " + err);
            LinuxSystem.Mount(w.Device, w.MountPoint, "ntfs3", readOnly: true, out _);
            return;
        }
        var q = new Quarantine(Path.Combine(_dataRoot, "RESCATE", "cuarentena"));
        int ok = 0, failed = 0;
        foreach (var h in hits)
        {
            try { q.Isolate(h.File, w.MountPoint, w.Uuid, h.Signature); ok++; Log($"  cuarentena: {h.File}"); }
            catch (Exception ex) { failed++; Log($"  no se pudo poner en cuarentena {h.File}: {ex.Message}"); }
        }
        LinuxSystem.Unmount(w.MountPoint);
        LinuxSystem.Mount(w.Device, w.MountPoint, "ntfs3", readOnly: true, out _);
        Good($"{ok} archivos en cuarentena.{(failed > 0 ? $" {failed} no se pudieron mover." : "")}");
    }

    private static void QuarantineMenu()
    {
        Header("Archivos en cuarentena");
        if (!RequireDataRoot()) return;
        var q = new Quarantine(Path.Combine(_dataRoot!, "RESCATE", "cuarentena"));
        var items = q.List();
        if (items.Count == 0) { Say("No hay archivos en cuarentena."); Pause(); return; }
        for (var i = 0; i < items.Count && i < 9; i++) Say($"{i + 1}. {items[i].RelativePath}  ({items[i].Reason}, {items[i].WhenUtc.ToLocalTime():dd/MM HH:mm})");
        Console.Write("\nNúmero del archivo a devolver a su lugar (o Enter para volver): ");
        var k = Console.ReadKey(true).KeyChar;
        if (!char.IsDigit(k) || k == '0' || k - '1' >= items.Count) return;
        var entry = items[k - '1'];
        var w = Windows.FirstOrDefault(x => x.Uuid == entry.VolumeId);
        if (w is null) { Warn("No encuentro el disco de donde salió ese archivo."); Pause(); return; }
        if (LinuxSystem.IsHibernated(w.MountPoint)) { Warn("Windows quedó hibernado: no se puede escribir en ese disco ahora."); Pause(); return; }
        LinuxSystem.Unmount(w.MountPoint);
        if (LinuxSystem.Mount(w.Device, w.MountPoint, "ntfs3", readOnly: false, out var err))
        {
            q.Restore(entry, w.MountPoint);
            Good("Listo: el archivo volvió a su lugar.");
            Log($"Devuelto de cuarentena: {entry.RelativePath}");
        }
        else Warn("No se pudo abrir el disco para escribir: " + err);
        LinuxSystem.Unmount(w.MountPoint);
        LinuxSystem.Mount(w.Device, w.MountPoint, "ntfs3", readOnly: true, out _);
        Pause();
    }

    // ---------- 3. Salud del disco ----------

    private static void DiskHealth()
    {
        Header("Salud de los discos");
        var devices = LinuxSystem.Devices();
        var boot = LinuxSystem.BootDisk(devices);
        var disks = devices.Where(d => d.Type == "disk" && d.Name != boot && d.Size > 0).ToList();
        if (disks.Count == 0) { Warn("No encontré discos."); Pause(); return; }
        if (!LinuxSystem.Has("smartctl")) { Warn("Falta la herramienta de salud de discos."); Pause(); return; }
        foreach (var d in disks)
        {
            var h = LinuxSystem.Health(d);
            Say($"\n{h.Model} ({Gb(d.Size)}):");
            if (h.Passed is false) Bad(h.Summary); else if (h.Passed is true) Good(h.Summary); else Warn(h.Summary);
            foreach (var line in h.Details) Say("   " + line);
            Log($"Disco {d.Path} {h.Model}: {h.Summary} {string.Join("; ", h.Details)}");
        }
        SaveReport();
        Pause();
    }

    // ---------- 4. Windows no arranca ----------

    private static void WindowsWontStart()
    {
        Header("Windows no arranca: qué hacer");
        Say("1) Primero rescatá tus fotos y documentos (opción 1). Así, pase lo que pase, no se pierden.");
        Say("2) Revisá el disco (opción 3). Si está fallando, ninguna reparación de Windows sirve: hay que cambiarlo.");
        Say("3) Sacá el pendrive y prendé la PC. Si Windows falla dos veces seguidas, se abre solo «Reparación automática».");
        Say("   Si no aparece: prendé, y cuando veas el logo mantené apretado el botón hasta que se apague. Repetilo 2 veces.");
        Say("4) En esa pantalla: «Opciones avanzadas» > «Solucionar problemas» > «Opciones avanzadas», y probá en este orden:");
        Say("   • «Reparación de inicio».");
        Say("   • «Restaurar sistema» (vuelve a una fecha anterior sin borrar tus archivos).");
        Say("   • «Desinstalar actualizaciones» (si empezó después de una actualización).");
        Say("   • Último recurso: «Restablecer este PC» > «Mantener mis archivos».");
        Say("5) Si pide la clave de BitLocker: está en https://aka.ms/myrecoverykey (desde el celular).");
        Say("\nFuente: Microsoft, «Windows Recovery Environment». PC Doctor no reemplaza a un técnico si el problema es de hardware.");
        Pause();
    }

    // ---------- Red y firmas ----------

    private static void PrepareNetwork()
    {
        if (!LinuxSystem.TryConnect())
        {
            var sigs = LinuxSystem.SignatureDir();
            _network = sigs is null ? "Sin Internet. Conectá un cable de red para descargar las firmas de virus."
                                    : $"Sin Internet: se usan las firmas de virus del pendrive (del {LinuxSystem.SignatureDate(sigs)}).";
            return;
        }
        _network = "Conectado a Internet. Actualizando las firmas de virus…";
        _network = LinuxSystem.UpdateSignatures(out var msg) ? "Conectado a Internet. " + msg : msg;
    }

    // ---------- Consola ----------

    private static void TechConsole()
    {
        Header("Consola para técnicos");
        Say("Escribí «exit» para volver al menú. Windows está montado (solo lectura) en /mnt/win-*.");
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/bash", "-l") { UseShellExecute = false });
        p?.WaitForExit();
    }

    // ---------- Pantalla e informe ----------

    private static bool RequireWindows()
    {
        if (Windows.Count > 0) return true;
        Warn(Locked.Count > 0 ? "El disco está cifrado con BitLocker: primero abrilo con la opción 8." : "No encontré Windows en esta computadora.");
        Pause();
        return false;
    }

    private static bool RequireDataRoot()
    {
        if (_dataRoot is not null) return true;
        Warn("No encontré la parte de datos del pendrive para guardar.");
        Pause();
        return false;
    }

    private static void SaveReport()
    {
        if (_sessionDir is null) return;
        try
        {
            Directory.CreateDirectory(_sessionDir);
            File.WriteAllText(Path.Combine(_sessionDir, "informe.txt"), Report.ToString());
            LinuxSystem.Run("sync", "", 30_000);
        }
        catch { /* el pendrive se pudo haber desconectado */ }
    }

    private static void Log(string line) => Report.AppendLine(line);

    private static string Short(string path, string root) => path.StartsWith(root) ? "C:" + path[root.Length..].Replace('/', '\\') : path;
    private static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1 << 20):0} MB";
    private static bool Yes() { var k = char.ToUpperInvariant(Console.ReadKey(true).KeyChar); Console.WriteLine(k); return k == 'S'; }

    private static void Header(string title)
    {
        Console.ResetColor(); Console.Clear();
        Console.BackgroundColor = ConsoleColor.DarkBlue; Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine((" " + title).PadRight(Math.Max(Console.WindowWidth - 1, title.Length + 2)));
        Console.ResetColor(); Console.WriteLine();
    }

    private static void Say(string text) => Wrap(text, ConsoleColor.Gray);
    private static void Good(string text) => Wrap(text, ConsoleColor.Green);
    private static void Warn(string text) => Wrap(text, ConsoleColor.Yellow);
    private static void Bad(string text) => Wrap(text, ConsoleColor.Red);

    private static void Wrap(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        var width = Math.Max(Console.WindowWidth - 1, 40);
        foreach (var paragraph in text.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                if (line.Length + word.Length + 1 > width) { Console.WriteLine(line.ToString()); line.Clear().Append("   "); }
                line.Append(line.Length > 0 && line.ToString() != "   " ? " " : "").Append(word);
            }
            Console.WriteLine(line.ToString());
        }
        Console.ResetColor();
    }

    private static void Pause()
    {
        Console.Write("\nApretá cualquier tecla para volver al menú…");
        Console.ReadKey(true);
    }
}

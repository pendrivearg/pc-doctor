using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PCDoctor.Rescue;

public enum Severity { Info, Warning, Suspicious }

/// <summary>Un hallazgo de la revisión sin conexión: qué es, dónde está y por qué llama la atención.</summary>
public sealed record Finding(Severity Severity, string Where, string What, string Why, string? FilePath = null);

/// <summary>
/// Revisa un Windows apagado (montado de solo lectura en <see cref="Root"/>) buscando lo que usan los virus y los
/// estafadores para quedarse en la PC: arranque automático, tareas programadas, servicios, exclusiones del antivirus,
/// Defender apagado por política, programas de control remoto, archivo hosts y notas de rescate.
/// Solo lee. Las rutas de Windows se resuelven sin distinguir mayúsculas (el disco se monta en Linux).
/// </summary>
public sealed class OfflineInspector
{
    public string Root { get; }

    public OfflineInspector(string windowsRoot) { Root = windowsRoot; }

    /// <summary>¿Este directorio es un Windows instalado?</summary>
    public static bool LooksLikeWindows(string root) =>
        PathCi.Find(root, @"Windows\System32\config\SOFTWARE") is not null;

    public static readonly string[] RemoteAccessTools =
    [
        "AnyDesk", "TeamViewer", "UltraViewer", "RustDesk", "ScreenConnect", "ConnectWise Control", "Supremo", "AeroAdmin",
        "Ammyy", "LogMeIn", "Splashtop", "RemotePC", "Atera", "NetSupport", "Remote Utilities", "DWAgent", "ToDesk", "HopToDesk",
    ];

    public List<Finding> Inspect()
    {
        var findings = new List<Finding>();
        var software = OpenHive(@"Windows\System32\config\SOFTWARE");
        var system = OpenHive(@"Windows\System32\config\SYSTEM");

        if (software is not null)
        {
            foreach (var path in new[] { @"Microsoft\Windows\CurrentVersion\Run", @"Microsoft\Windows\CurrentVersion\RunOnce",
                                         @"WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce" })
                CheckRunKey(software.Open(path), "Arranque automático (todos los usuarios)", findings);
            CheckWinlogon(software, findings);
            CheckDebuggers(software, findings);
            CheckDefender(software, findings);
        }
        else findings.Add(new(Severity.Info, "Registro de Windows", "No se pudo leer el registro del sistema", "El archivo puede estar dañado o en uso."));

        if (system is not null) CheckServices(system, findings);

        foreach (var (user, profile) in UserProfiles())
        {
            var ntuser = PathCi.Find(profile, "NTUSER.DAT") is { } nt ? RegistryHive.TryOpen(nt) : null;
            if (ntuser is not null)
                foreach (var path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
                    CheckRunKey(ntuser.Open(path), $"Arranque automático de {user}", findings);
            CheckStartupFolder(PathCi.Find(profile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup"), $"Carpeta Inicio de {user}", findings);
            CheckRansomNotes(profile, user, findings);
        }
        CheckStartupFolder(PathCi.Find(Root, @"ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp"), "Carpeta Inicio (todos los usuarios)", findings);
        CheckScheduledTasks(findings);
        CheckRemoteAccess(findings);
        CheckHosts(findings);
        return findings;
    }

    public IEnumerable<(string User, string Profile)> UserProfiles()
    {
        var users = PathCi.Find(Root, "Users");
        if (users is null) yield break;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Public", "Default", "Default User", "All Users", "defaultuser0", "WDAGUtilityAccount" };
        foreach (var dir in SafeDirs(users))
        {
            var name = Path.GetFileName(dir);
            if (!skip.Contains(name)) yield return (name, dir);
        }
    }

    private RegistryHive? OpenHive(string relative) => PathCi.Find(Root, relative) is { } p ? RegistryHive.TryOpen(p) : null;

    // ---------- Clasificación de comandos que arrancan solos ----------

    private static readonly (Regex Pattern, string Why)[] SuspiciousCommand =
    [
        (Rx(@"powershell.*(-e(nc|ncodedcommand)?\s+[a-z0-9+/=]{20,}|frombase64string)"), "Ejecuta un comando de PowerShell escondido (codificado)."),
        (Rx(@"powershell.*(-w(indowstyle)?\s+h(idden)?).*(http|iex|invoke-expression|downloadstring)"), "Ejecuta PowerShell oculto que descarga algo de Internet."),
        (Rx(@"\b(mshta)(\.exe)?\b"), "Usa mshta, un truco común para ejecutar código de páginas web."),
        (Rx(@"\b(wscript|cscript)(\.exe)?\b.*\.(js|jse|vbs|vbe|wsf)\b"), "Ejecuta un script oculto al iniciar."),
        (Rx(@"regsvr32.*(/i:\s*https?|scrobj)"), "Usa regsvr32 para ejecutar código de Internet."),
        (Rx(@"\\appdata\\local\\temp\\|\\windows\\temp\\|\\users\\public\\|\\\$recycle\.bin\\"), "Arranca un programa guardado en una carpeta temporal o pública, donde los programas legítimos no se instalan."),
        (Rx(@"rundll32(\.exe)?\s+[^,]*\\(appdata|programdata|temp)\\"), "Carga una biblioteca desde una carpeta de usuario o temporal."),
        (Rx(@"\.(pdf|doc|docx|jpg|jpeg|png|txt|xls|xlsx|mp3|mp4)\.(exe|scr|com|bat|cmd|pif)\b"), "Tiene doble extensión (se hace pasar por un documento)."),
        (Rx(@"https?://"), "Abre o descarga una dirección de Internet al iniciar."),
    ];

    private static Regex Rx(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Si el comando tiene un patrón típico de virus devuelve el motivo; si no, null.</summary>
    public static string? WhySuspicious(string command)
    {
        foreach (var (pattern, why) in SuspiciousCommand)
            if (pattern.IsMatch(command)) return why;
        return null;
    }

    private void CheckRunKey(RegistryKeyView? key, string where, List<Finding> findings)
    {
        if (key is null) return;
        foreach (var (name, value) in key.Values())
        {
            if (value is not string cmd || string.IsNullOrWhiteSpace(cmd)) continue;
            var why = WhySuspicious(cmd);
            if (why is not null) findings.Add(new(Severity.Suspicious, where, $"«{name}»: {cmd}", why, ExecutableOf(cmd)));
            else if (RemoteAccessTools.Any(t => cmd.Contains(t.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) || name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                findings.Add(new(Severity.Warning, where, $"«{name}»: {cmd}", "Es un programa para manejar la PC a distancia. Si no lo instalaste vos o alguien de confianza, puede ser un estafador."));
            else findings.Add(new(Severity.Info, where, $"«{name}»: {cmd}", "Arranca solo con Windows."));
        }
    }

    private static void CheckWinlogon(RegistryHive software, List<Finding> findings)
    {
        var k = software.Open(@"Microsoft\Windows NT\CurrentVersion\Winlogon");
        if (k is null) return;
        var shell = k.GetString("Shell")?.Trim();
        if (shell is not null && !string.Equals(shell, "explorer.exe", StringComparison.OrdinalIgnoreCase))
            findings.Add(new(Severity.Suspicious, "Inicio de sesión de Windows", $"Shell = {shell}", "Lo normal es «explorer.exe». Los virus cambian este valor para arrancar en lugar del escritorio."));
        var userinit = k.GetString("Userinit")?.Trim().TrimEnd(',');
        if (userinit is not null && !userinit.EndsWith(@"\system32\userinit.exe", StringComparison.OrdinalIgnoreCase))
            findings.Add(new(Severity.Suspicious, "Inicio de sesión de Windows", $"Userinit = {userinit}", "Lo normal es solo userinit.exe de Windows. Algo más agregado arranca con cada inicio de sesión."));
    }

    private static void CheckDebuggers(RegistryHive software, List<Finding> findings)
    {
        var ifeo = software.Open(@"Microsoft\Windows NT\CurrentVersion\Image File Execution Options");
        if (ifeo is null) return;
        foreach (var k in ifeo.SubKeys())
        {
            var dbg = k.GetString("Debugger");
            if (!string.IsNullOrWhiteSpace(dbg))
                findings.Add(new(Severity.Suspicious, "Opciones de ejecución de programas", $"{k.Name} → {dbg}", "Cuando se abre ese programa, Windows ejecuta otro en su lugar. Los virus lo usan para bloquear el antivirus o el Administrador de tareas."));
        }
    }

    private static void CheckDefender(RegistryHive software, List<Finding> findings)
    {
        var policy = software.Open(@"Policies\Microsoft\Windows Defender");
        if (policy?.GetInt("DisableAntiSpyware") == 1 || policy?.GetInt("DisableAntiVirus") == 1)
            findings.Add(new(Severity.Suspicious, "Microsoft Defender", "Defender está apagado por una política", "En una PC de casa nadie apaga el antivirus así; suele hacerlo un virus para que no lo detecten."));
        var rtp = software.Open(@"Policies\Microsoft\Windows Defender\Real-Time Protection");
        if (rtp?.GetInt("DisableRealtimeMonitoring") == 1)
            findings.Add(new(Severity.Suspicious, "Microsoft Defender", "La protección en tiempo real está apagada por una política", "Deja la PC sin protección mientras se usa."));

        foreach (var (sub, what) in new[] { ("Paths", "carpeta"), ("Processes", "programa"), ("Extensions", "tipo de archivo") })
        {
            var k = software.Open($@"Microsoft\Windows Defender\Exclusions\{sub}");
            if (k is null) continue;
            foreach (var (name, _) in k.Values())
            {
                var broad = Regex.IsMatch(name, @"^[a-z]:\\?$", RegexOptions.IgnoreCase) || name is "*" or ".exe" or "exe" || WhySuspicious(name) is not null
                            || name.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) || name.Contains(@"\Temp", StringComparison.OrdinalIgnoreCase);
                findings.Add(new(broad ? Severity.Suspicious : Severity.Warning, "Exclusiones de Microsoft Defender", $"No se revisa este {what}: {name}",
                    broad ? "Es una exclusión muy amplia o en una carpeta típica de virus: el antivirus no mira ahí."
                          : "El antivirus no revisa esto. Si no lo pidió un programa de confianza, conviene sacarlo."));
            }
        }
    }

    private void CheckServices(RegistryHive system, List<Finding> findings)
    {
        var current = system.Open("Select")?.GetInt("Current") ?? 1;
        var services = system.Open($@"ControlSet{current:000}\Services");
        if (services is null) return;
        foreach (var svc in services.SubKeys())
        {
            var image = svc.GetString("ImagePath");
            if (string.IsNullOrWhiteSpace(image)) continue;
            var lower = image.ToLowerInvariant();
            string? why = WhySuspicious(image);
            if (why is null && (lower.Contains(@"\appdata\") || Regex.IsMatch(lower, @"\\programdata\\[^\\]+\.(exe|dll)")))
                why = "Es un servicio (arranca antes de iniciar sesión, con permisos totales) pero está guardado en una carpeta de usuario.";
            if (why is not null)
                findings.Add(new(Severity.Suspicious, "Servicios de Windows", $"{svc.Name}: {image}", why, ExecutableOf(image)));
            else if (RemoteAccessTools.Any(t => lower.Contains(t.Replace(" ", "").ToLowerInvariant()) || svc.Name.Contains(t.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)))
                findings.Add(new(Severity.Warning, "Servicios de Windows", $"{svc.Name}: {image}", "Programa de control remoto que queda funcionando siempre. Si no lo instalaste vos, desinstalalo."));
        }
    }

    private static readonly string[] ScriptExtensions = [".exe", ".scr", ".com", ".pif", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".hta", ".ps1"];

    private static void CheckStartupFolder(string? dir, string where, List<Finding> findings)
    {
        if (dir is null) return;
        foreach (var file in SafeFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ScriptExtensions.Contains(ext))
                findings.Add(new(Severity.Suspicious, where, name, "Hay un programa o script suelto en la carpeta de inicio: arranca solo y los programas legítimos casi nunca se ponen así.", file));
            else findings.Add(new(Severity.Info, where, name, "Acceso directo que arranca con Windows."));
        }
    }

    private void CheckScheduledTasks(List<Finding> findings)
    {
        var tasks = PathCi.Find(Root, @"Windows\System32\Tasks");
        if (tasks is null) return;
        foreach (var file in SafeFiles(tasks, recursive: true))
        {
            string? command = null, args = null;
            try
            {
                var doc = XDocument.Load(file);
                var exec = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Exec");
                command = exec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value;
                args = exec?.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value;
            }
            catch { continue; } // no es una tarea legible
            if (string.IsNullOrWhiteSpace(command)) continue;
            var full = $"{command} {args}".Trim();
            var why = WhySuspicious(full);
            var taskName = Path.GetRelativePath(tasks, file);
            if (why is not null)
                findings.Add(new(Severity.Suspicious, "Tareas programadas", $"{taskName}: {full}", why, ExecutableOf(command)));
            else if (RemoteAccessTools.Any(t => full.Contains(t.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)))
                findings.Add(new(Severity.Warning, "Tareas programadas", $"{taskName}: {full}", "Arranca un programa de control remoto."));
        }
    }

    private void CheckRemoteAccess(List<Finding> findings)
    {
        var places = new List<string?> { PathCi.Find(Root, "Program Files"), PathCi.Find(Root, "Program Files (x86)"), PathCi.Find(Root, "ProgramData") };
        foreach (var (_, profile) in UserProfiles())
        {
            places.Add(PathCi.Find(profile, @"AppData\Roaming"));
            places.Add(PathCi.Find(profile, @"AppData\Local"));
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var place in places.OfType<string>())
            foreach (var dir in SafeDirs(place))
            {
                var name = Path.GetFileName(dir);
                var tool = RemoteAccessTools.FirstOrDefault(t => name.Replace(" ", "").Contains(t.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                if (tool is not null && seen.Add(tool))
                    findings.Add(new(Severity.Warning, "Programas de control remoto", $"{tool} ({dir[Root.Length..].TrimStart('/', '\\')})",
                        "Permite que otra persona maneje esta PC. Si alguien te llamó «de soporte» y te hizo instalarlo, es una estafa: desinstalalo y cambiá tus claves desde otro dispositivo."));
            }
    }

    private void CheckHosts(List<Finding> findings)
    {
        var hosts = PathCi.Find(Root, @"Windows\System32\drivers\etc\hosts");
        if (hosts is null) return;
        string[] lines;
        try { lines = File.ReadAllLines(hosts); } catch { return; }
        var important = Rx(@"(microsoft|windowsupdate|google|bing|facebook|banco|bank|paypal|mercadopago|mercadolibre|afip|anses|defender|avast|kaspersky|eset|norton|mcafee|malwarebytes)");
        foreach (var raw in lines)
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] is "localhost") continue;
            var target = parts[1];
            if (important.IsMatch(target))
                findings.Add(new(Severity.Suspicious, "Archivo hosts", line, "Desvía o bloquea una página conocida (bancos, antivirus o Microsoft). Los virus lo hacen para robar datos o impedir que el antivirus se actualice."));
            else findings.Add(new(Severity.Info, "Archivo hosts", line, "Redirección agregada a mano o por un programa."));
        }
    }

    private static readonly Regex RansomName = Rx(@"(decrypt|recover|restore|how.?to|readme|rescate|instrucciones).*\.(txt|html?|hta)$");
    private static readonly Regex RansomText = Rx(@"(bitcoin|btc|monero|decrypt|descifrar|tor browser|\.onion|your files (have been|are) encrypted|archivos.*(cifrados|encriptados))");

    private static void CheckRansomNotes(string profile, string user, List<Finding> findings)
    {
        foreach (var folder in new[] { "Desktop", "Documents", @"OneDrive\Desktop", @"OneDrive\Documents" })
        {
            var dir = PathCi.Find(profile, folder);
            if (dir is null) continue;
            foreach (var file in SafeFiles(dir).Where(f => RansomName.IsMatch(Path.GetFileName(f))).Take(20))
            {
                string head;
                try { using var r = new StreamReader(file); var buf = new char[8192]; head = new string(buf, 0, r.Read(buf, 0, buf.Length)); }
                catch { continue; }
                if (RansomText.IsMatch(head))
                {
                    findings.Add(new(Severity.Suspicious, $"Archivos de {user}", Path.GetFileName(file),
                        "Parece una nota de rescate (ransomware): piden plata para devolver los archivos. No pagues. Rescatá lo que se pueda y mirá www.nomoreransom.org desde otra computadora.", file));
                    break;
                }
            }
        }
    }

    // ---------- Utilidades ----------

    /// <summary>Extrae el ejecutable de una línea de comando de Windows y lo traduce a la ruta montada.</summary>
    public string? ExecutableOf(string command)
    {
        // Las variables de Windows no existen en el rescate: se traducen las de rutas fijas.
        var cmd = command.Trim();
        cmd = Regex.Replace(cmd, "%(systemroot|windir)%", @"C:\Windows", RegexOptions.IgnoreCase);
        cmd = Regex.Replace(cmd, "%programfiles\\(x86\\)%", @"C:\Program Files (x86)", RegexOptions.IgnoreCase);
        cmd = Regex.Replace(cmd, "%programfiles%", @"C:\Program Files", RegexOptions.IgnoreCase);
        cmd = Regex.Replace(cmd, "%(programdata|allusersprofile)%", @"C:\ProgramData", RegexOptions.IgnoreCase);
        cmd = Regex.Replace(cmd, @"^\\SystemRoot\\", @"C:\Windows\", RegexOptions.IgnoreCase);
        string exe;
        if (cmd.StartsWith('"')) { var end = cmd.IndexOf('"', 1); exe = end > 0 ? cmd[1..end] : cmd.Trim('"'); }
        else
        {
            var m = Regex.Match(cmd, @"^.+?\.(exe|scr|com|bat|cmd|dll|vbs|js|ps1|hta)\b", RegexOptions.IgnoreCase);
            exe = m.Success ? m.Value : cmd.Split(' ')[0];
        }
        exe = Regex.Replace(exe, @"^\\\?\?\\", "");
        var drive = Regex.Match(exe, @"^[a-z]:\\", RegexOptions.IgnoreCase);
        if (!drive.Success) return null; // ruta relativa (system32): no se puede ubicar con certeza
        return PathCi.Find(Root, exe[3..]);
    }

    internal static IEnumerable<string> SafeFiles(string dir, bool recursive = false)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = recursive, AttributesToSkip = 0, MaxRecursionDepth = 8 };
        try { return Directory.EnumerateFiles(dir, "*", opts).ToList(); } catch { return []; }
    }

    internal static IEnumerable<string> SafeDirs(string dir)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        try { return Directory.EnumerateDirectories(dir, "*", opts).ToList(); } catch { return []; }
    }
}

/// <summary>Resuelve rutas de Windows («Windows\System32\config») sobre un disco montado en Linux, sin distinguir mayúsculas.</summary>
public static class PathCi
{
    public static string? Find(string root, string relative)
    {
        var current = root;
        foreach (var part in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var direct = Path.Combine(current, part);
            if (Directory.Exists(direct) || File.Exists(direct)) { current = direct; continue; }
            string? match = null;
            try
            {
                match = Directory.EnumerateFileSystemEntries(current, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
            if (match is null) return null;
            current = match;
        }
        return current;
    }
}

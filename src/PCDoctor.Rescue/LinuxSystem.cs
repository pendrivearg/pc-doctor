using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCDoctor.Rescue;

public sealed record BlockDevice(string Name, string Path, string Type, string? FsType, string? Label, string? Uuid, long Size,
                                 bool Removable, string? Transport, string? Parent, string? MountPoint, string? Model);

public sealed record DiskHealth(string Device, string Model, bool? Passed, string Summary, IReadOnlyList<string> Details);

public sealed record VirusHit(string File, string Signature);

/// <summary>Todo lo que toca el sistema de rescate (Linux): discos, montaje, BitLocker, salud, antivirus y red.</summary>
public static class LinuxSystem
{
    public const string MediumMount = "/run/live/medium";

    public static (int Exit, string Out, string Err) Run(string file, string args, int timeoutMs = 120_000, string? stdin = null)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, UseShellExecute = false };
        psi.Environment["LANG"] = "C.UTF-8";
        try
        {
            using var p = Process.Start(psi)!;
            if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, o.IsCompleted ? o.Result : "", "tiempo agotado"); }
            return (p.ExitCode, o.Result, e.Result);
        }
        catch (Exception ex) { return (-2, "", ex.Message); }
    }

    public static bool Has(string tool) => Run("sh", $"-c \"command -v {tool}\"", 5000).Exit == 0;

    public static List<BlockDevice> Devices()
    {
        var (exit, json, _) = Run("lsblk", "-J -l -b -o NAME,PATH,TYPE,FSTYPE,LABEL,UUID,SIZE,RM,TRAN,PKNAME,MOUNTPOINT,MODEL", 20_000);
        var list = new List<BlockDevice>();
        if (exit != 0) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var d in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            string? S(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long size = d.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
            bool rm = d.TryGetProperty("rm", out var r) && (r.ValueKind == JsonValueKind.True || (r.ValueKind == JsonValueKind.String && r.GetString() == "1"));
            list.Add(new(S("name") ?? "", S("path") ?? "", S("type") ?? "", S("fstype"), S("label"), S("uuid"), size, rm, S("tran"), S("pkname"), S("mountpoint"), S("model")?.Trim()));
        }
        return list;
    }

    /// <summary>El disco del pendrive (del que arrancó el rescate), para no confundirlo con la PC.</summary>
    public static string? BootDisk(List<BlockDevice> devices) =>
        devices.FirstOrDefault(d => d.MountPoint == MediumMount)?.Parent;

    public static bool Mount(string device, string target, string fsType, bool readOnly, out string error)
    {
        Directory.CreateDirectory(target);
        var opts = readOnly ? "ro" : "rw";
        var (exit, _, err) = Run("mount", $"-t {fsType} -o {opts} {device} {target}", 60_000);
        if (exit != 0 && fsType == "ntfs3" && readOnly)
            (exit, _, err) = Run("mount", $"-t ntfs3 -o ro,force {device} {target}", 60_000);
        error = err.Trim();
        return exit == 0;
    }

    public static void Unmount(string target) => Run("umount", target, 60_000);

    /// <summary>Abre un disco cifrado con BitLocker usando la clave de recuperación (48 números). Devuelve el dispositivo descifrado.</summary>
    public static string? UnlockBitLocker(string device, string recoveryKey, out string error)
    {
        var name = "pcd-" + Path.GetFileName(device);
        var key = Regex.Replace(recoveryKey, @"[^0-9]", "");
        if (key.Length != 48) { error = "La clave de recuperación tiene 48 números (8 grupos de 6)."; return null; }
        var formatted = string.Join("-", Enumerable.Range(0, 8).Select(i => key.Substring(i * 6, 6)));
        var (exit, _, err) = Run("cryptsetup", $"bitlkOpen --readonly {device} {name}", 60_000, stdin: formatted + "\n");
        error = err.Trim();
        return exit == 0 ? $"/dev/mapper/{name}" : null;
    }

    /// <summary>Windows con «inicio rápido» o hibernado: escribir en ese disco podría dañarlo.</summary>
    public static bool IsHibernated(string windowsRoot)
    {
        var hib = PathCi.Find(windowsRoot, "hiberfil.sys");
        if (hib is null) return false;
        try
        {
            using var f = File.OpenRead(hib);
            var head = new byte[4];
            if (f.Read(head, 0, 4) < 4) return false;
            var sig = System.Text.Encoding.ASCII.GetString(head).ToLowerInvariant();
            return sig is "hibr" or "wake" or "rstr";
        }
        catch { return true; } // ante la duda, no escribir
    }

    public static DiskHealth Health(BlockDevice disk)
    {
        var (exit, json, _) = Run("smartctl", $"--json -a {disk.Path}", 60_000);
        var details = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return new(disk.Path, disk.Model ?? "", null, "No se pudo leer la salud de este disco.", details);
        bool? passed = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("smart_status", out var st) && st.TryGetProperty("passed", out var p)) passed = p.GetBoolean();
            if (root.TryGetProperty("temperature", out var t) && t.TryGetProperty("current", out var tc)) details.Add($"Temperatura: {tc.GetInt32()} °C");
            if (root.TryGetProperty("nvme_smart_health_information_log", out var nv))
            {
                if (nv.TryGetProperty("percentage_used", out var pu)) details.Add($"Desgaste: {pu.GetInt32()} % de la vida útil");
                if (nv.TryGetProperty("media_errors", out var me) && me.GetInt64() > 0) details.Add($"Errores de lectura: {me.GetInt64()}");
                if (nv.TryGetProperty("critical_warning", out var cw) && cw.GetInt32() != 0) { details.Add("El disco informa una advertencia crítica."); passed = false; }
            }
            if (root.TryGetProperty("ata_smart_attributes", out var at) && at.TryGetProperty("table", out var table))
                foreach (var a in table.EnumerateArray())
                {
                    var id = a.GetProperty("id").GetInt32();
                    var raw = a.TryGetProperty("raw", out var rw) && rw.TryGetProperty("value", out var rv) ? rv.GetInt64() : 0;
                    var label = id switch { 5 => "Sectores reemplazados", 187 => "Errores no corregibles", 197 => "Sectores pendientes", 198 => "Sectores ilegibles", _ => null };
                    if (label is not null && raw > 0) details.Add($"{label}: {raw}");
                }
        }
        catch (JsonException) { return new(disk.Path, disk.Model ?? "", null, "No se pudo leer la salud de este disco.", details); }
        var bad = details.Any(d => d.StartsWith("Sectores") || d.StartsWith("Errores") || d.Contains("crítica"));
        var summary = passed is false ? "El disco está FALLANDO. Rescatá tus archivos ya y reemplazalo."
                    : bad ? "El disco muestra señales de desgaste. Rescatá tus archivos y planeá cambiarlo."
                    : passed is true ? "El disco está sano." : "El disco no informa su estado de salud.";
        return new(disk.Path, disk.Model ?? "", passed is false ? false : bad ? false : passed, summary, details);
    }

    /// <summary>Carpeta de firmas de virus más nueva disponible (la actualizada por Internet o la del pendrive).</summary>
    public static string? SignatureDir()
    {
        foreach (var dir in new[] { "/var/lib/clamav", Path.Combine(MediumMount, "pcdoctor", "clamav-db") })
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any(f => Path.GetFileName(f).StartsWith("daily.")))
                return dir;
        return null;
    }

    public static List<VirusHit> Scan(IEnumerable<string> paths, string signatures, Action<string>? progress, CancellationToken ct)
    {
        var list = Path.Combine(Path.GetTempPath(), "pcd-scan-list.txt");
        File.WriteAllLines(list, paths.Distinct());
        var psi = new ProcessStartInfo("clamscan", $"--database={signatures} --recursive --infected --no-summary --max-filesize=200M --max-scansize=400M --file-list={list}")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        var hits = new List<VirusHit>();
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        p.ErrorDataReceived += (_, e) => { };
        p.BeginErrorReadLine();
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            var m = Regex.Match(line, @"^(.*): (.+) FOUND$");
            if (m.Success) { hits.Add(new(m.Groups[1].Value, m.Groups[2].Value)); progress?.Invoke(m.Groups[1].Value); }
        }
        p.WaitForExit();
        return hits;
    }

    public static bool HasInternet() => Run("sh", "-c \"ip route | grep -q '^default'\"", 5000).Exit == 0;

    /// <summary>Intenta conectarse por cable (DHCP). El Wi-Fi necesita la clave y no se configura solo.</summary>
    public static bool TryConnect()
    {
        if (HasInternet()) return true;
        if (Has("dhcpcd")) Run("dhcpcd", "-b", 30_000);
        for (var i = 0; i < 20 && !HasInternet(); i++) Thread.Sleep(1000);
        return HasInternet();
    }

    public static bool UpdateSignatures(out string message)
    {
        var (exit, o, e) = Run("freshclam", "--stdout --datadir=/var/lib/clamav", 15 * 60_000);
        message = exit == 0 ? "Firmas de virus actualizadas por Internet." : "No se pudieron actualizar las firmas: " + (e + o).Trim().Split('\n').LastOrDefault();
        return exit == 0;
    }

    public static string SignatureDate(string dir)
    {
        var f = Directory.EnumerateFiles(dir).Where(p => Path.GetFileName(p).StartsWith("daily.")).Select(p => new FileInfo(p)).OrderByDescending(i => i.LastWriteTimeUtc).FirstOrDefault();
        return f is null ? "desconocida" : f.LastWriteTime.ToString("dd/MM/yyyy");
    }
}

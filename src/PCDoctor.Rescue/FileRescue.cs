using System.Security.Cryptography;
using System.Text.Json;

namespace PCDoctor.Rescue;

/// <summary>Qué carpetas personales se rescatan de cada usuario y cuánto ocupan.</summary>
public sealed record RescuePlan(IReadOnlyList<(string User, string Folder, string Source)> Items, long TotalBytes, int FileCount);

public sealed record RescueResult(int Copied, int Skipped, int Failed, long Bytes, IReadOnlyList<string> Errors);

/// <summary>
/// Copia fotos y documentos de un Windows apagado a otro disco (normalmente la parte de datos del pendrive).
/// Nunca escribe en el disco de origen. Si se corta, al volver a empezar saltea lo que ya está copiado igual.
/// </summary>
public static class FileRescue
{
    public static readonly string[] PersonalFolders =
    [
        "Desktop", "Documents", "Pictures", "Videos", "Music", "Downloads",
        @"OneDrive\Desktop", @"OneDrive\Documents", @"OneDrive\Pictures", @"OneDrive\Escritorio", @"OneDrive\Documentos", @"OneDrive\Imágenes",
    ];

    public static RescuePlan Plan(OfflineInspector windows)
    {
        var items = new List<(string, string, string)>();
        long total = 0; var count = 0;
        foreach (var (user, profile) in windows.UserProfiles())
            foreach (var folder in PersonalFolders)
            {
                var src = PathCi.Find(profile, folder);
                if (src is null || !Directory.Exists(src)) continue;
                items.Add((user, folder.Replace('\\', '/'), src));
                foreach (var f in OfflineInspector.SafeFiles(src, recursive: true))
                {
                    try { total += new FileInfo(f).Length; count++; } catch { /* archivo ilegible: se informa al copiar */ }
                }
            }
        return new RescuePlan(items, total, count);
    }

    public static RescueResult Copy(RescuePlan plan, string destination, Action<long, long, string>? progress = null, CancellationToken ct = default)
    {
        int copied = 0, skipped = 0, failed = 0; long done = 0;
        var errors = new List<string>();
        var buffer = new byte[1 << 20];
        foreach (var (user, folder, src) in plan.Items)
            foreach (var file in OfflineInspector.SafeFiles(src, recursive: true))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(src, file);
                var target = Path.Combine(destination, Sanitize(user), Sanitize(folder), SanitizePath(rel));
                try
                {
                    var info = new FileInfo(file);
                    var existing = new FileInfo(target);
                    if (existing.Exists && existing.Length == info.Length) { skipped++; done += info.Length; continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                    using (var output = new FileStream(target + ".parcial", FileMode.Create, FileAccess.Write))
                    {
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, n);
                            done += n;
                            progress?.Invoke(done, plan.TotalBytes, rel);
                        }
                    }
                    File.Move(target + ".parcial", target, overwrite: true);
                    try { File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc); } catch { /* exFAT a veces no acepta la fecha */ }
                    copied++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    if (errors.Count < 200) errors.Add($"{user}/{folder}/{rel}: {ex.Message}");
                    try { File.Delete(target + ".parcial"); } catch { }
                }
            }
        return new RescueResult(copied, skipped, failed, done, errors);
    }

    // exFAT y FAT no aceptan algunos caracteres que NTFS sí.
    private static string Sanitize(string name)
    {
        var bad = new[] { '<', '>', ':', '"', '|', '?', '*' };
        var s = new string(name.Select(c => bad.Contains(c) || c < 32 ? '_' : c).ToArray()).TrimEnd(' ', '.');
        return s.Length == 0 ? "_" : s;
    }

    private static string SanitizePath(string rel) =>
        string.Join(Path.DirectorySeparatorChar, rel.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Select(Sanitize));
}

/// <summary>Un archivo puesto en cuarentena: se puede devolver a su lugar.</summary>
public sealed record QuarantineEntry(string Id, string VolumeId, string RelativePath, string Sha256, string Reason, DateTime WhenUtc);

/// <summary>
/// Cuarentena reversible: el archivo se guarda codificado (no se puede abrir ni ejecutar por accidente) en el
/// pendrive, con un registro para devolverlo. Recién después de guardarlo y comprobarlo se borra el original.
/// </summary>
public sealed class Quarantine
{
    private const byte Key = 0x5A;
    private readonly string _dir;

    public Quarantine(string directory) { _dir = directory; Directory.CreateDirectory(_dir); }

    public QuarantineEntry Isolate(string file, string windowsRoot, string volumeId, string reason)
    {
        var bytes = File.ReadAllBytes(file);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var id = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{sha[..12]}";
        var encoded = (byte[])bytes.Clone();
        for (var i = 0; i < encoded.Length; i++) encoded[i] ^= Key;
        var data = Path.Combine(_dir, id + ".cuarentena");
        File.WriteAllBytes(data, encoded);
        if (!ReadBack(data).SequenceEqual(bytes)) { File.Delete(data); throw new IOException("No se pudo guardar una copia exacta: el archivo original no se tocó."); }
        var entry = new QuarantineEntry(id, volumeId, Path.GetRelativePath(windowsRoot, file), sha, reason, DateTime.UtcNow);
        File.WriteAllText(Path.Combine(_dir, id + ".json"), JsonSerializer.Serialize(entry));
        File.Delete(file);
        return entry;
    }

    public void Restore(QuarantineEntry entry, string windowsRoot)
    {
        var bytes = ReadBack(Path.Combine(_dir, entry.Id + ".cuarentena"));
        if (Convert.ToHexString(SHA256.HashData(bytes)) != entry.Sha256) throw new IOException("La copia guardada no coincide con el original.");
        var original = Path.Combine(windowsRoot, entry.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllBytes(original, bytes);
        File.Delete(Path.Combine(_dir, entry.Id + ".cuarentena"));
        File.Delete(Path.Combine(_dir, entry.Id + ".json"));
    }

    public IReadOnlyList<QuarantineEntry> List() =>
        Directory.EnumerateFiles(_dir, "*.json")
            .Select(f => { try { return JsonSerializer.Deserialize<QuarantineEntry>(File.ReadAllText(f)); } catch { return null; } })
            .OfType<QuarantineEntry>().OrderBy(e => e.WhenUtc).ToList();

    private static byte[] ReadBack(string path)
    {
        var b = File.ReadAllBytes(path);
        for (var i = 0; i < b.Length; i++) b[i] ^= Key;
        return b;
    }
}

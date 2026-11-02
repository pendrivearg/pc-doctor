using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCDoctor.Core.Updates;

/// <summary>
/// Un paquete de la versión: un ZIP con la estructura del pendrive y la lista exacta de lo que reemplaza.
/// «datos» es la parte «PC DOCTOR» del pendrive; «rescate» es la parte «PCD-RESCATE» (el modo rescate).
/// </summary>
public sealed record UpdatePackage(string Name, string Target, string File, string Sha256, long Size, IReadOnlyList<string> Replace);

/// <summary>Lo que describe una versión publicada. Va firmado: si alguien lo cambia, la firma no coincide y no se instala.</summary>
public sealed record UpdateManifest(string Product, string Version, DateTime Published, string Notes, IReadOnlyList<UpdatePackage> Packages)
{
    public const string ProductName = "pcdoctor";
    public const string ManifestAsset = "manifiesto.json";
    public const string SignatureAsset = "manifiesto.json.firma";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);
    public static UpdateManifest Parse(byte[] bytes) =>
        JsonSerializer.Deserialize<UpdateManifest>(bytes, Json) ?? throw new InvalidDataException("El manifiesto de la versión está vacío.");
}

/// <summary>Firma digital de las versiones (ECDSA P-256 con SHA-256, incluida en .NET: sin costo ni servicios externos).</summary>
public static class UpdateSignature
{
    public static (string PrivatePem, string PublicPem) CreateKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    public static byte[] Sign(byte[] data, string privatePem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        return key.SignData(data, HashAlgorithmName.SHA256);
    }

    public static bool Verify(byte[] data, byte[] signature, string publicPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicPem);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static string PublicFromPrivate(string privatePem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        return key.ExportSubjectPublicKeyInfoPem();
    }
}

public static class UpdateVersion
{
    /// <summary>Compara «0.2.0», «v0.3.1», etc. Una versión que no se entiende nunca se considera más nueva.</summary>
    public static bool IsNewer(string candidate, string current) =>
        TryParse(candidate) is { } c && TryParse(current) is { } cur && c > cur;

    public static Version? TryParse(string text) =>
        Version.TryParse(text.Trim().TrimStart('v', 'V').Split('-', '+')[0], out var v) ? v : null;
}

/// <summary>Rutas que una actualización nunca puede tocar: el historial, los informes, los modelos de IA y lo rescatado.</summary>
public static class UpdateRules
{
    private static readonly string[] Protected = ["pcdoctor/state", "pcdoctor/models", "pcdoctor/engines", "RESCATE", "System Volume Information"];

    public static string Normalize(string relative)
    {
        var p = relative.Replace('\\', '/').Trim('/');
        if (p.Length == 0 || Path.IsPathRooted(relative) || p.Split('/').Any(s => s is ".." or "." || s.Length == 0) || p.Contains(':'))
            throw new InvalidDataException($"Ruta no permitida en la actualización: «{relative}»");
        if (Protected.Any(x => p.Equals(x, StringComparison.OrdinalIgnoreCase) || p.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"La actualización no puede tocar «{relative}»");
        return p;
    }
}

/// <summary>Arma una versión: los ZIP, el manifiesto y su firma, listos para subir a GitHub Releases.</summary>
public static class UpdatePackager
{
    public static UpdateManifest Build(string version, string notes, IEnumerable<(string Name, string Target, string SourceRoot, IReadOnlyList<string> Replace)> parts,
                                       string outputDir, string privatePem)
    {
        Directory.CreateDirectory(outputDir);
        var packages = new List<UpdatePackage>();
        foreach (var (name, target, root, replace) in parts)
        {
            var normalized = replace.Select(UpdateRules.Normalize).ToList();
            var zipName = $"{name}-{version}.zip";
            var zipPath = Path.Combine(outputDir, zipName);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                foreach (var rel in normalized)
                {
                    var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full)) zip.CreateEntryFromFile(full, rel, CompressionLevel.Optimal);
                    else if (Directory.Exists(full))
                        foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                            zip.CreateEntryFromFile(f, rel + "/" + Path.GetRelativePath(full, f).Replace('\\', '/'), CompressionLevel.Optimal);
                    else throw new FileNotFoundException($"Falta «{rel}» en {root}");
                }
            packages.Add(new UpdatePackage(name, target, zipName, Sha256Of(zipPath), new FileInfo(zipPath).Length, normalized));
        }
        var manifest = new UpdateManifest(UpdateManifest.ProductName, version, DateTime.UtcNow, notes, packages);
        var bytes = manifest.ToBytes();
        File.WriteAllBytes(Path.Combine(outputDir, UpdateManifest.ManifestAsset), bytes);
        File.WriteAllText(Path.Combine(outputDir, UpdateManifest.SignatureAsset), Convert.ToBase64String(UpdateSignature.Sign(bytes, privatePem)));
        return manifest;
    }

    public static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }
}

/// <summary>Una versión disponible en GitHub, ya con la firma comprobada.</summary>
public sealed record AvailableUpdate(UpdateManifest Manifest, byte[] ManifestBytes, byte[] Signature, IReadOnlyDictionary<string, string> AssetUrls)
{
    public long DownloadSize => Manifest.Packages.Sum(p => p.Size);
}

/// <summary>
/// Busca versiones en GitHub Releases (gratis, sin cuenta para descargar) y las descarga a una carpeta de preparación.
/// Solo se conecta cuando la persona lo pide, y solo envía lo que cualquier navegador envía para bajar un archivo.
/// </summary>
public sealed class UpdateClient
{
    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly string _publicKey;
    private readonly string _apiBase;

    public UpdateClient(string repository, string publicKeyPem, HttpMessageHandler? handler = null, string apiBase = "https://api.github.com")
    {
        _repository = repository;
        _publicKey = publicKeyPem;
        _apiBase = apiBase.TrimEnd('/');
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCDoctor", "1"));
    }

    /// <summary>Devuelve la versión publicada si es más nueva que la actual y su firma es válida; null si ya está al día.</summary>
    public async Task<AvailableUpdate?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiBase}/repos/{_repository}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // todavía no hay versiones publicadas
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            assets[a.GetProperty("name").GetString()!] = a.GetProperty("browser_download_url").GetString()!;
        if (!assets.TryGetValue(UpdateManifest.ManifestAsset, out var mUrl) || !assets.TryGetValue(UpdateManifest.SignatureAsset, out var sUrl))
            throw new InvalidDataException("La versión publicada no tiene manifiesto firmado: no se instala.");

        var manifestBytes = await _http.GetByteArrayAsync(mUrl, ct);
        var signature = Convert.FromBase64String((await _http.GetStringAsync(sUrl, ct)).Trim());
        if (!UpdateSignature.Verify(manifestBytes, signature, _publicKey))
            throw new InvalidDataException("La firma de la versión publicada no es válida: alguien pudo haberla modificado. No se instala.");
        var manifest = UpdateManifest.Parse(manifestBytes);
        if (manifest.Product != UpdateManifest.ProductName) throw new InvalidDataException("La versión publicada no es de PC Doctor.");
        foreach (var p in manifest.Packages)
        {
            if (!assets.ContainsKey(p.File)) throw new InvalidDataException($"Falta el archivo «{p.File}» en la versión publicada.");
            foreach (var r in p.Replace) UpdateRules.Normalize(r);
        }
        return UpdateVersion.IsNewer(manifest.Version, currentVersion) ? new AvailableUpdate(manifest, manifestBytes, signature, assets) : null;
    }

    /// <summary>
    /// Descarga y comprueba cada paquete (SHA-256 del manifiesto firmado) y lo descomprime en «carpeta/paquete».
    /// Solo cuando todo está comprobado deja la marca «lista»: una descarga cortada nunca se instala.
    /// </summary>
    public async Task DownloadAsync(AvailableUpdate update, string stagingDir, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
        Directory.CreateDirectory(stagingDir);
        long done = 0, total = update.DownloadSize;
        foreach (var p in update.Manifest.Packages)
        {
            var zipPath = Path.Combine(stagingDir, p.File);
            using (var resp = await _http.GetAsync(update.AssetUrls[p.File], HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var input = await resp.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(zipPath);
                var buffer = new byte[1 << 16];
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    progress?.Report((done, total));
                }
            }
            if (!UpdatePackager.Sha256Of(zipPath).Equals(p.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"«{p.File}» llegó distinto de lo publicado (descarga dañada o modificada). No se instala.");
            UpdateStaging.Extract(zipPath, Path.Combine(stagingDir, p.Name), p.Replace);
            File.Delete(zipPath);
        }
        UpdateStaging.Finish(stagingDir, update.ManifestBytes, update.Signature, update.Manifest.Version);
    }
}

/// <summary>
/// Toda una versión en un solo archivo («pcdoctor-actualizacion-X.Y.Z.zip»): para una PC sin Internet, o hackeada,
/// se baja en otro lado (otra PC, el celular) y se instala desde el archivo. Lleva el mismo manifiesto firmado:
/// se comprueba igual que la descarga de Internet.
/// </summary>
public static class UpdateBundle
{
    public const string Prefix = "pcdoctor-actualizacion-";

    /// <summary>Junta en un solo archivo todo lo de la versión (los paquetes ya vienen comprimidos: se guardan tal cual).</summary>
    public static string Create(string releaseDir, string version, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, $"{Prefix}{version}.zip");
        if (File.Exists(path)) File.Delete(path);
        var files = Directory.EnumerateFiles(releaseDir).Where(f => !Path.GetFileName(f).StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var f in files) zip.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.NoCompression);
        return path;
    }

    /// <summary>Lee y comprueba la firma del archivo, sin descomprimir los paquetes. Si algo no cierra, lanza <see cref="InvalidDataException"/>.</summary>
    public static UpdateManifest Read(string bundlePath, string publicKeyPem)
    {
        using var zip = OpenZip(bundlePath);
        return ReadVerified(zip, publicKeyPem).Manifest;
    }

    /// <summary>Comprueba firma y huellas, y deja la versión lista para instalar en <paramref name="stagingDir"/>.</summary>
    public static UpdateManifest Stage(string bundlePath, string stagingDir, string publicKeyPem, string currentVersion, IProgress<(long Done, long Total)>? progress = null)
    {
        using var zip = OpenZip(bundlePath);
        var (manifest, bytes, signature) = ReadVerified(zip, publicKeyPem);
        if (!UpdateVersion.IsNewer(manifest.Version, currentVersion))
            throw new InvalidDataException($"Ese archivo es la versión {manifest.Version}, y este pendrive ya tiene la {currentVersion}.");
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
        Directory.CreateDirectory(stagingDir);
        long done = 0, total = manifest.Packages.Sum(p => p.Size);
        foreach (var p in manifest.Packages)
        {
            var entry = zip.GetEntry(p.File) ?? throw new InvalidDataException($"Al archivo le falta «{p.File}»: no se instala.");
            var zipPath = Path.Combine(stagingDir, p.File);
            entry.ExtractToFile(zipPath, overwrite: true);
            done += p.Size;
            progress?.Report((done, total));
            if (!UpdatePackager.Sha256Of(zipPath).Equals(p.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"«{p.File}» está dañado o lo cambió alguien. No se instala.");
            UpdateStaging.Extract(zipPath, Path.Combine(stagingDir, p.Name), p.Replace);
            File.Delete(zipPath);
        }
        UpdateStaging.Finish(stagingDir, bytes, signature, manifest.Version);
        return manifest;
    }

    /// <summary>El archivo de actualización más nuevo en estas carpetas (por ejemplo Descargas), si hay alguno.</summary>
    public static string? FindNewest(IEnumerable<string> folders) =>
        folders.Where(Directory.Exists)
            .SelectMany(d => { try { return Directory.EnumerateFiles(d, Prefix + "*.zip"); } catch (IOException) { return []; } catch (UnauthorizedAccessException) { return []; } })
            .Select(f => (File: f, Version: UpdateVersion.TryParse(Path.GetFileNameWithoutExtension(f)[Prefix.Length..])))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.File)
            .FirstOrDefault();

    private static ZipArchive OpenZip(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new InvalidDataException("Ese archivo no es una actualización de PC Doctor (o se bajó incompleto)."); }
    }

    private static (UpdateManifest Manifest, byte[] Bytes, byte[] Signature) ReadVerified(ZipArchive zip, string publicKeyPem)
    {
        var m = zip.GetEntry(UpdateManifest.ManifestAsset);
        var s = zip.GetEntry(UpdateManifest.SignatureAsset);
        if (m is null || s is null || m.Length > 1_000_000 || s.Length > 10_000)
            throw new InvalidDataException("Ese archivo no es una actualización de PC Doctor (no tiene manifiesto firmado).");
        byte[] bytes, signature;
        using (var ms = new MemoryStream()) { using (var st = m.Open()) st.CopyTo(ms); bytes = ms.ToArray(); }
        using (var sr = new StreamReader(s.Open()))
            try { signature = Convert.FromBase64String(sr.ReadToEnd().Trim()); }
            catch (FormatException) { throw new InvalidDataException("La firma de ese archivo está dañada: no se instala."); }
        if (!UpdateSignature.Verify(bytes, signature, publicKeyPem))
            throw new InvalidDataException("La firma de ese archivo no es válida: alguien pudo haberlo modificado. No se instala.");
        var manifest = UpdateManifest.Parse(bytes);
        if (manifest.Product != UpdateManifest.ProductName) throw new InvalidDataException("Ese archivo no es de PC Doctor.");
        foreach (var p in manifest.Packages) foreach (var r in p.Replace) UpdateRules.Normalize(r);
        return (manifest, bytes, signature);
    }
}

/// <summary>Pone el sello firmado del programa en un pendrive publicado (lo hace el dueño, con la clave privada).</summary>
public static class ProgramSealer
{
    public static void Seal(string pendriveRoot, string privatePem)
    {
        var text = Encoding.UTF8.GetBytes(ProgramSeal.SealText(ProgramSeal.Sha256Hex(Path.Combine(pendriveRoot, ProgramSeal.ProgramFile))));
        File.WriteAllBytes(Path.Combine(pendriveRoot, ProgramSeal.SealFile), text);
        File.WriteAllText(Path.Combine(pendriveRoot, ProgramSeal.SignatureFile), Convert.ToBase64String(UpdateSignature.Sign(text, privatePem)));
    }
}

/// <summary>La carpeta de preparación que después instala <see cref="UpdateApplier"/> (venga de Internet o de un archivo).</summary>
internal static class UpdateStaging
{
    /// <summary>Solo cuando todo está comprobado se deja la marca «lista»: una preparación cortada nunca se instala.</summary>
    public static void Finish(string stagingDir, byte[] manifestBytes, byte[] signature, string version)
    {
        File.WriteAllBytes(Path.Combine(stagingDir, UpdateManifest.ManifestAsset), manifestBytes);
        File.WriteAllText(Path.Combine(stagingDir, UpdateManifest.SignatureAsset), Convert.ToBase64String(signature));
        File.WriteAllText(Path.Combine(stagingDir, "lista"), version);
    }

    public static void Extract(string zipPath, string dest, IReadOnlyList<string> allowed)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var rel = UpdateRules.Normalize(entry.FullName);
            if (!allowed.Any(a => rel.Equals(a, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"El paquete trae «{rel}», que no está en su lista: no se instala.");
            var target = Path.GetFullPath(Path.Combine(dest, rel));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ruta no permitida en el paquete.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}

/// <summary>
/// Instala una versión ya descargada y comprobada. Por cada ruta de la lista, guarda la anterior en «anterior/» y pone la nueva;
/// si algo falla a mitad de camino, devuelve todo a como estaba. Nunca toca lo protegido (<see cref="UpdateRules"/>).
/// </summary>
public static class UpdateApplier
{
    public sealed record Result(bool Ok, string Message, string? Version);

    /// <param name="targets">Raíz de cada parte del pendrive: «datos» → H:\, «rescate» → la parte PCD-RESCATE (si no está, ese paquete se saltea).</param>
    public static Result Apply(string stagingDir, IReadOnlyDictionary<string, string> targets, string publicKeyPem, string currentVersion, Action<string>? failAt = null)
    {
        var marker = Path.Combine(stagingDir, "lista");
        if (!File.Exists(marker)) return new(false, "La descarga no terminó bien: no se instala nada.", null);
        var bytes = File.ReadAllBytes(Path.Combine(stagingDir, UpdateManifest.ManifestAsset));
        var sig = Convert.FromBase64String(File.ReadAllText(Path.Combine(stagingDir, UpdateManifest.SignatureAsset)).Trim());
        if (!UpdateSignature.Verify(bytes, sig, publicKeyPem)) return new(false, "La firma no es válida: no se instala nada.", null);
        var manifest = UpdateManifest.Parse(bytes);
        if (!UpdateVersion.IsNewer(manifest.Version, currentVersion)) return new(false, $"La versión {manifest.Version} no es más nueva que la instalada ({currentVersion}).", null);

        var done = new List<(string Target, string Backup)>();
        var created = new List<string>();
        try
        {
            foreach (var p in manifest.Packages)
            {
                if (!targets.TryGetValue(p.Target, out var root) || !Directory.Exists(root)) continue;
                var backupRoot = Path.Combine(stagingDir, "anterior", p.Name);
                foreach (var rel in p.Replace.Select(UpdateRules.Normalize))
                {
                    var source = Path.Combine(stagingDir, p.Name, rel.Replace('/', Path.DirectorySeparatorChar));
                    var target = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                    var backup = Path.Combine(backupRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    failAt?.Invoke(rel);
                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        MoveAny(target, backup);
                        done.Add((target, backup));
                    }
                    else created.Add(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    CopyAny(source, target);
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var t in created) DeleteAny(t);
            foreach (var (target, backup) in Enumerable.Reverse(done))
            {
                DeleteAny(target);
                MoveAny(backup, target);
            }
            return new(false, $"No se pudo instalar ({ex.Message}). Se dejó todo como estaba.", null);
        }
        return new(true, $"PC Doctor se actualizó a la versión {manifest.Version}.", manifest.Version);
    }

    private static void MoveAny(string from, string to)
    {
        if (Directory.Exists(from)) CopyDir(from, to, deleteSource: true);
        else { File.Copy(from, to, overwrite: true); File.Delete(from); } // entre unidades distintas no se puede mover directo
    }

    private static void CopyAny(string from, string to)
    {
        if (Directory.Exists(from)) CopyDir(from, to, deleteSource: false);
        else File.Copy(from, to, overwrite: true);
    }

    private static void CopyDir(string from, string to, bool deleteSource)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, overwrite: true);
        }
        if (deleteSource) Directory.Delete(from, recursive: true);
    }

    private static void DeleteAny(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Dónde busca versiones este pendrive (data/actualizaciones.yaml). Sin repositorio configurado, el botón no aparece.</summary>
public sealed class UpdateSettings
{
    public string Repositorio { get; set; } = "";

    public bool Enabled => System.Text.RegularExpressions.Regex.IsMatch(Repositorio, @"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$");

    public static UpdateSettings Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "actualizaciones.yaml");
        if (!File.Exists(path)) return new UpdateSettings();
        return new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build()
            .Deserialize<UpdateSettings>(File.ReadAllText(path)) ?? new UpdateSettings();
    }
}

/// <summary>Las partes del pendrive que reemplaza cada versión (todo lo demás no se toca).</summary>
public static class UpdateLayout
{
    public static readonly string[] Datos =
    [
        "ABRIR PC DOCTOR.exe", "LEEME.txt", "pcdoctor/pcdoctor-cli.exe", "pcdoctor/e_sqlite3.dll",
        "pcdoctor/knowledge", "pcdoctor/data", "pcdoctor/licencias",
        "pcdoctor/PCDoctor-Abrir.exe", "pcdoctor/pcdoctor.ico", "autorun.inf", "pcdoctor/firma-programa.txt", "pcdoctor/firma-programa.txt.firma",
    ];

    public static readonly string[] Rescate = ["pcdoctor/pcdoctor-rescate", "live/config-hooks"];
}

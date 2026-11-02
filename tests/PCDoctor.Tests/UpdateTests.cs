using System.IO.Compression;
using System.Net;
using System.Text;
using PCDoctor.Core.Updates;

namespace PCDoctor.Tests;

/// <summary>Responde como GitHub Releases, sirviendo los archivos de una carpeta (sin Internet).</summary>
internal sealed class FakeGitHub(string releaseDir, string repo = "dueno/pcdoctor") : HttpMessageHandler
{
    public bool NoRelease;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == $"/repos/{repo}/releases/latest")
        {
            if (NoRelease) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var assets = string.Join(",", Directory.EnumerateFiles(releaseDir).Select(f =>
                $"{{\"name\":\"{Path.GetFileName(f)}\",\"browser_download_url\":\"https://fake/descargas/{Path.GetFileName(f)}\"}}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"tag_name\":\"v0.3.0\",\"assets\":[{assets}]}}") });
        }
        if (path.StartsWith("/descargas/"))
        {
            var file = Path.Combine(releaseDir, Path.GetFileName(path));
            return Task.FromResult(File.Exists(file)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(file)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public class UpdateTests
{
    private static readonly (string Private, string Public) Keys = UpdateSignature.CreateKeys();

    private static string NewDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PCDoctorActualizar", Guid.NewGuid().ToString("N"))).FullName;

    private static void Put(string root, string rel, string content)
    {
        var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Un pendrive con la versión instalada (0.2.0) y lo que no se debe tocar nunca.</summary>
    private static (string Datos, string Rescate) OldPendrive()
    {
        var datos = NewDir();
        foreach (var f in UpdateLayout.Datos.Where(f => f.Contains('.')))
            Put(datos, f, "viejo " + f);
        Put(datos, "pcdoctor/knowledge/problems/a.yaml", "ficha a vieja");
        Put(datos, "pcdoctor/knowledge/problems/borrada.yaml", "ficha que ya no existe");
        Put(datos, "pcdoctor/data/actualizaciones.yaml", "repositorio: dueno/pcdoctor");
        Put(datos, "pcdoctor/licencias/GPL-3.0.txt", "licencia");
        Put(datos, "pcdoctor/state/machines/x/historial.db", "HISTORIAL");
        Put(datos, "pcdoctor/models/modelo.gguf", "MODELO");
        Put(datos, "RESCATE/2026-11-01/Archivos/Juan/foto.jpg", "FOTO");
        var rescate = NewDir();
        Put(rescate, "pcdoctor/pcdoctor-rescate", "rescate viejo");
        Put(rescate, "live/config-hooks/9990-pcdoctor", "hook viejo");
        Put(rescate, "live/filesystem.squashfs", "SISTEMA");
        return (datos, rescate);
    }

    /// <summary>Publica la versión 0.3.0 (programa + rescate) con la clave de prueba.</summary>
    private static string PublishNewVersion(string version = "0.3.0", string? privateKey = null)
    {
        var datos = NewDir();
        foreach (var f in UpdateLayout.Datos.Where(f => f.Contains('.')))
            Put(datos, f, "nuevo " + f);
        Put(datos, "pcdoctor/knowledge/problems/a.yaml", "ficha a nueva");
        Put(datos, "pcdoctor/knowledge/problems/b.yaml", "ficha b nueva");
        Put(datos, "pcdoctor/data/actualizaciones.yaml", "repositorio: dueno/pcdoctor");
        Put(datos, "pcdoctor/licencias/GPL-3.0.txt", "licencia");
        var rescate = NewDir();
        Put(rescate, "pcdoctor/pcdoctor-rescate", "rescate nuevo");
        Put(rescate, "live/config-hooks/9990-pcdoctor", "hook nuevo");
        var release = NewDir();
        UpdatePackager.Build(version, "Arreglos y fichas nuevas.",
            [("programa", "datos", datos, UpdateLayout.Datos), ("rescate", "rescate", rescate, UpdateLayout.Rescate)], release, privateKey ?? Keys.Private);
        return release;
    }

    [Fact]
    public void SignatureAcceptsOnlyTheOwnersUntouchedManifest()
    {
        var data = Encoding.UTF8.GetBytes("{\"version\":\"0.3.0\"}");
        var sig = UpdateSignature.Sign(data, Keys.Private);
        Assert.True(UpdateSignature.Verify(data, sig, Keys.Public));
        Assert.False(UpdateSignature.Verify(Encoding.UTF8.GetBytes("{\"version\":\"9.9.9\"}"), sig, Keys.Public));
        Assert.False(UpdateSignature.Verify(data, sig, UpdateSignature.CreateKeys().PublicPem));
        Assert.False(UpdateSignature.Verify(data, [1, 2, 3], Keys.Public));
        Assert.Equal(Keys.Public.Trim(), UpdateSignature.PublicFromPrivate(Keys.Private).Trim());
    }

    [Fact]
    public void TheProjectKeyShippedInsideTheProgramIsValid()
    {
        Assert.Contains("BEGIN PUBLIC KEY", UpdateKeys.PublicKeyPem);
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(UpdateKeys.PublicKeyPem); // si la clave estuviera rota, ningún pendrive podría actualizarse
        Assert.Equal(256, key.KeySize);
    }

    [Theory]
    [InlineData("0.3.0", "0.2.0", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.1.9", "0.2.0", false)]
    [InlineData("basura", "0.2.0", false)]
    public void ComparesVersions(string candidate, string current, bool newer) => Assert.Equal(newer, UpdateVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData("../Windows/System32/x.dll")]
    [InlineData("C:/Windows/x.dll")]
    [InlineData("pcdoctor/state/machines/x/historial.db")]
    [InlineData("pcdoctor/models")]
    [InlineData("RESCATE/fotos")]
    [InlineData("pcdoctor/../pcdoctor/state")]
    public void RefusesPathsOutsideTheProgramOrIntoProtectedFolders(string path) =>
        Assert.Throws<InvalidDataException>(() => UpdateRules.Normalize(path));

    [Fact]
    public async Task FullCycleDownloadsVerifiesAndInstallsWithoutTouchingUserData()
    {
        var (datos, rescate) = OldPendrive();
        var client = new UpdateClient("dueno/pcdoctor", Keys.Public, new FakeGitHub(PublishNewVersion()), "https://fake");

        var update = await client.CheckAsync("0.2.0");
        Assert.NotNull(update);
        Assert.Equal("0.3.0", update!.Manifest.Version);
        Assert.Equal("Arreglos y fichas nuevas.", update.Manifest.Notes);

        var staging = Path.Combine(datos, "pcdoctor", "actualizacion");
        var reported = new List<long>();
        await client.DownloadAsync(update, staging, new SyncProgress(p => reported.Add(p.Done)));
        Assert.NotEmpty(reported);
        Assert.True(File.Exists(Path.Combine(staging, "lista")));

        var result = UpdateApplier.Apply(staging, new Dictionary<string, string> { ["datos"] = datos, ["rescate"] = rescate }, Keys.Public, "0.2.0");
        Assert.True(result.Ok, result.Message);
        Assert.Equal("0.3.0", result.Version);

        Assert.Equal("nuevo ABRIR PC DOCTOR.exe", Read(datos, "ABRIR PC DOCTOR.exe"));
        Assert.Equal("nuevo pcdoctor/pcdoctor-cli.exe", Read(datos, "pcdoctor/pcdoctor-cli.exe"));
        Assert.Equal("ficha a nueva", Read(datos, "pcdoctor/knowledge/problems/a.yaml"));
        Assert.Equal("ficha b nueva", Read(datos, "pcdoctor/knowledge/problems/b.yaml"));
        Assert.False(File.Exists(Path.Combine(datos, "pcdoctor", "knowledge", "problems", "borrada.yaml"))); // la biblioteca queda igual a la publicada
        Assert.Equal("rescate nuevo", Read(rescate, "pcdoctor/pcdoctor-rescate"));
        Assert.Equal("hook nuevo", Read(rescate, "live/config-hooks/9990-pcdoctor"));

        // Lo de la persona y lo pesado no se toca.
        Assert.Equal("HISTORIAL", Read(datos, "pcdoctor/state/machines/x/historial.db"));
        Assert.Equal("MODELO", Read(datos, "pcdoctor/models/modelo.gguf"));
        Assert.Equal("FOTO", Read(datos, "RESCATE/2026-11-01/Archivos/Juan/foto.jpg"));
        Assert.Equal("SISTEMA", Read(rescate, "live/filesystem.squashfs"));
        // La versión anterior queda guardada.
        Assert.Equal("viejo ABRIR PC DOCTOR.exe", Read(staging, "anterior/programa/ABRIR PC DOCTOR.exe"));
    }

    [Fact]
    public async Task SaysUpToDateWhenThereIsNothingNewer()
    {
        var release = PublishNewVersion("0.3.0");
        Assert.Null(await new UpdateClient("dueno/pcdoctor", Keys.Public, new FakeGitHub(release), "https://fake").CheckAsync("0.3.0"));
        Assert.Null(await new UpdateClient("dueno/pcdoctor", Keys.Public, new FakeGitHub(release), "https://fake").CheckAsync("0.4.0"));
        Assert.Null(await new UpdateClient("dueno/pcdoctor", Keys.Public, new FakeGitHub(release) { NoRelease = true }, "https://fake").CheckAsync("0.2.0"));
    }

    [Fact]
    public async Task RejectsAVersionSignedBySomeoneElse()
    {
        var forged = PublishNewVersion(privateKey: UpdateSignature.CreateKeys().PrivatePem);
        var client = new UpdateClient("dueno/pcdoctor", Keys.Public, new FakeGitHub(forged), "https://fake");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync("0.2.0"));
        Assert.Contains("firma", ex.Message);
    }

    [Fact]
    public async Task RejectsAManifestChangedAfterSigning()
    {
        var release = PublishNewVersion();
        var path = Path.Combine(release, UpdateManifest.ManifestAsset);
        File.WriteAllText(path, File.ReadAllText(path).Replace("0.3.0", "0.3.1"));
        var client = new UpdateClient("dueno/pcdoctor", Keys.Public, new FakeGitHub(release), "https://fake");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync("0.2.0"));
    }

    [Fact]
    public async Task RejectsAPackageModifiedAfterPublishingAndInstallsNothing()
    {
        var (datos, _) = OldPendrive();
        var release = PublishNewVersion();
        var zip = Directory.EnumerateFiles(release, "programa-*.zip").Single();
        var bytes = File.ReadAllBytes(zip);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);
        var client = new UpdateClient("dueno/pcdoctor", Keys.Public, new FakeGitHub(release), "https://fake");
        var update = await client.CheckAsync("0.2.0");
        var staging = Path.Combine(datos, "pcdoctor", "actualizacion");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(update!, staging));
        Assert.False(File.Exists(Path.Combine(staging, "lista")));
        var result = UpdateApplier.Apply(staging, new Dictionary<string, string> { ["datos"] = datos }, Keys.Public, "0.2.0");
        Assert.False(result.Ok);
        Assert.Equal("viejo ABRIR PC DOCTOR.exe", Read(datos, "ABRIR PC DOCTOR.exe"));
    }

    [Fact]
    public async Task IfInstallingFailsHalfwayEverythingGoesBackAsItWas()
    {
        var (datos, rescate) = OldPendrive();
        var client = new UpdateClient("dueno/pcdoctor", Keys.Public, new FakeGitHub(PublishNewVersion()), "https://fake");
        var staging = Path.Combine(datos, "pcdoctor", "actualizacion");
        await client.DownloadAsync((await client.CheckAsync("0.2.0"))!, staging);

        var result = UpdateApplier.Apply(staging, new Dictionary<string, string> { ["datos"] = datos, ["rescate"] = rescate }, Keys.Public, "0.2.0",
            failAt: rel => { if (rel == "pcdoctor/data") throw new IOException("archivo en uso"); });
        Assert.False(result.Ok);
        Assert.Contains("como estaba", result.Message);
        Assert.Equal("viejo ABRIR PC DOCTOR.exe", Read(datos, "ABRIR PC DOCTOR.exe"));
        Assert.Equal("viejo pcdoctor/pcdoctor-cli.exe", Read(datos, "pcdoctor/pcdoctor-cli.exe"));
        Assert.Equal("ficha a vieja", Read(datos, "pcdoctor/knowledge/problems/a.yaml"));
        Assert.True(File.Exists(Path.Combine(datos, "pcdoctor", "knowledge", "problems", "borrada.yaml")));
        Assert.False(File.Exists(Path.Combine(datos, "pcdoctor", "knowledge", "problems", "b.yaml")));
        Assert.Equal("rescate viejo", Read(rescate, "pcdoctor/pcdoctor-rescate"));
        Assert.Equal("HISTORIAL", Read(datos, "pcdoctor/state/machines/x/historial.db"));
    }

    [Fact]
    public async Task RefusesToInstallAnOlderVersionEvenIfItIsSigned()
    {
        var (datos, _) = OldPendrive();
        var client = new UpdateClient("dueno/pcdoctor", Keys.Public, new FakeGitHub(PublishNewVersion()), "https://fake");
        var staging = Path.Combine(datos, "pcdoctor", "actualizacion");
        await client.DownloadAsync((await client.CheckAsync("0.2.0"))!, staging);
        var result = UpdateApplier.Apply(staging, new Dictionary<string, string> { ["datos"] = datos }, Keys.Public, "0.5.0");
        Assert.False(result.Ok);
        Assert.Equal("viejo ABRIR PC DOCTOR.exe", Read(datos, "ABRIR PC DOCTOR.exe"));
    }

    [Fact]
    public async Task RejectsAPackageThatSneaksInFilesOutsideItsList()
    {
        var release = PublishNewVersion();
        var zipPath = Directory.EnumerateFiles(release, "programa-*.zip").Single();
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        {
            var e = zip.CreateEntry("pcdoctor/state/machines/x/historial.db");
            using var w = new StreamWriter(e.Open());
            w.Write("borrado");
        }
        // Se vuelve a firmar con la clave correcta: aun así, el contenido no permitido se rechaza.
        var manifestPath = Path.Combine(release, UpdateManifest.ManifestAsset);
        var m = UpdateManifest.Parse(File.ReadAllBytes(manifestPath));
        var fixedPackages = m.Packages.Select(p => p.Name == "programa" ? p with { Sha256 = UpdatePackager.Sha256Of(zipPath), Size = new FileInfo(zipPath).Length } : p).ToList();
        var bytes = (m with { Packages = fixedPackages }).ToBytes();
        File.WriteAllBytes(manifestPath, bytes);
        File.WriteAllText(Path.Combine(release, UpdateManifest.SignatureAsset), Convert.ToBase64String(UpdateSignature.Sign(bytes, Keys.Private)));

        var (datos, _) = OldPendrive();
        var client = new UpdateClient("dueno/pcdoctor", Keys.Public, new FakeGitHub(release), "https://fake");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await client.DownloadAsync((await client.CheckAsync("0.2.0"))!, Path.Combine(datos, "pcdoctor", "actualizacion")));
        Assert.Equal("HISTORIAL", Read(datos, "pcdoctor/state/machines/x/historial.db"));
    }

    [Fact]
    public void TheUpdateButtonOnlyAppearsWithAValidRepository()
    {
        Assert.False(new UpdateSettings().Enabled);
        Assert.False(new UpdateSettings { Repositorio = "no es un repo" }.Enabled);
        Assert.True(new UpdateSettings { Repositorio = "dueno/pc-doctor" }.Enabled);
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "actualizaciones.yaml"), "repositorio: \"dueno/pc-doctor\"\n");
        Assert.Equal("dueno/pc-doctor", UpdateSettings.Load(dir).Repositorio);
        Assert.False(UpdateSettings.Load(NewDir()).Enabled); // sin archivo: apagado
    }

    [Fact]
    public void InstallsFromASingleFileWithoutInternetAndKeepsUserData()
    {
        var (datos, rescate) = OldPendrive();
        var bundle = UpdateBundle.Create(PublishNewVersion(), "0.3.0", NewDir());
        Assert.Equal("pcdoctor-actualizacion-0.3.0.zip", Path.GetFileName(bundle));
        Assert.Equal("0.3.0", UpdateBundle.Read(bundle, Keys.Public).Version);

        var staging = Path.Combine(datos, "pcdoctor", "actualizacion");
        var manifest = UpdateBundle.Stage(bundle, staging, Keys.Public, "0.2.0");
        Assert.Equal("0.3.0", manifest.Version);
        var result = UpdateApplier.Apply(staging, new Dictionary<string, string> { ["datos"] = datos, ["rescate"] = rescate }, Keys.Public, "0.2.0");
        Assert.True(result.Ok, result.Message);
        Assert.Equal("nuevo ABRIR PC DOCTOR.exe", Read(datos, "ABRIR PC DOCTOR.exe"));
        Assert.Equal("nuevo pcdoctor/PCDoctor-Abrir.exe", Read(datos, "pcdoctor/PCDoctor-Abrir.exe"));
        Assert.Equal("rescate nuevo", Read(rescate, "pcdoctor/pcdoctor-rescate"));
        Assert.Equal("HISTORIAL", Read(datos, "pcdoctor/state/machines/x/historial.db"));
        Assert.Equal("FOTO", Read(datos, "RESCATE/2026-11-01/Archivos/Juan/foto.jpg"));
    }

    [Fact]
    public void RejectsForgedTamperedOldOrBogusUpdateFiles()
    {
        var staging = Path.Combine(NewDir(), "actualizacion");
        // Firmada por otro.
        var forged = UpdateBundle.Create(PublishNewVersion(privateKey: UpdateSignature.CreateKeys().PrivatePem), "0.3.0", NewDir());
        Assert.Contains("firma", Assert.Throws<InvalidDataException>(() => UpdateBundle.Stage(forged, staging, Keys.Public, "0.2.0")).Message);
        // Un paquete cambiado después de firmar.
        var release = PublishNewVersion();
        var zip = Directory.EnumerateFiles(release, "programa-*.zip").Single();
        var bytes = File.ReadAllBytes(zip);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);
        var tampered = UpdateBundle.Create(release, "0.3.0", NewDir());
        Assert.Throws<InvalidDataException>(() => UpdateBundle.Stage(tampered, staging, Keys.Public, "0.2.0"));
        Assert.False(File.Exists(Path.Combine(staging, "lista"))); // nada queda listo para instalar
        // Una versión que no es más nueva.
        var same = UpdateBundle.Create(PublishNewVersion(), "0.3.0", NewDir());
        Assert.Throws<InvalidDataException>(() => UpdateBundle.Stage(same, staging, Keys.Public, "0.3.0"));
        // Cualquier otro archivo.
        var bogus = Path.Combine(NewDir(), "pcdoctor-actualizacion-9.9.9.zip");
        File.WriteAllText(bogus, "no es un zip");
        Assert.Throws<InvalidDataException>(() => UpdateBundle.Read(bogus, Keys.Public));
        var emptyZip = Path.Combine(NewDir(), "vacio.zip");
        ZipFile.Open(emptyZip, ZipArchiveMode.Create).Dispose();
        Assert.Throws<InvalidDataException>(() => UpdateBundle.Read(emptyZip, Keys.Public));
    }

    [Fact]
    public void FindsTheNewestUpdateFileInDownloads()
    {
        var a = NewDir();
        var b = NewDir();
        foreach (var (dir, name) in new[] { (a, "pcdoctor-actualizacion-0.3.1.zip"), (a, "pcdoctor-actualizacion-0.10.0.zip"), (b, "pcdoctor-actualizacion-0.9.0.zip"), (b, "pcdoctor-actualizacion-basura.zip"), (b, "otra-cosa-9.9.9.zip") })
            File.WriteAllText(Path.Combine(dir, name), "x");
        Assert.Equal("pcdoctor-actualizacion-0.10.0.zip", Path.GetFileName(UpdateBundle.FindNewest([a, b, Path.Combine(a, "no-existe")])));
        Assert.Null(UpdateBundle.FindNewest([NewDir()]));
    }

    [Fact]
    public void TheAutoOpenerOnlyOpensTheSealedProgram()
    {
        var root = NewDir();
        Put(root, "ABRIR PC DOCTOR.exe", "programa original");
        Directory.CreateDirectory(Path.Combine(root, "pcdoctor"));
        Assert.False(ProgramSeal.Verify(root, Keys.Public)); // sin sello no se abre
        ProgramSealer.Seal(root, Keys.Private);
        Assert.True(ProgramSeal.Verify(root, Keys.Public));
        Assert.False(ProgramSeal.Verify(root, UpdateSignature.CreateKeys().PublicPem)); // sellado por otro
        Assert.False(ProgramSeal.Verify(root, UpdateKeys.PublicKeyPem)); // la clave real del proyecto se lee bien y no coincide con la de prueba

        Put(root, "ABRIR PC DOCTOR.exe", "programa cambiado por un virus");
        Assert.False(ProgramSeal.Verify(root, Keys.Public));
        Put(root, "ABRIR PC DOCTOR.exe", "programa original");
        Assert.True(ProgramSeal.Verify(root, Keys.Public));
        // Alguien que cambia el sello para que coincida con su programa: la firma ya no vale.
        Put(root, "ABRIR PC DOCTOR.exe", "otro programa");
        File.WriteAllText(Path.Combine(root, ProgramSeal.SealFile), ProgramSeal.SealText(ProgramSeal.Sha256Hex(Path.Combine(root, ProgramSeal.ProgramFile))));
        Assert.False(ProgramSeal.Verify(root, Keys.Public));
        File.WriteAllText(Path.Combine(root, ProgramSeal.SignatureFile), "basura que no es base64 ###");
        Assert.False(ProgramSeal.Verify(root, Keys.Public));
    }

    private sealed class SyncProgress(Action<(long Done, long Total)> report) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => report(value);
    }
}

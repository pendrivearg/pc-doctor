using Microsoft.Win32;
using PCDoctor.Core;
using PCDoctor.Core.Actions;
using PCDoctor.Windows;
using PCDoctor.Windows.Probes;

namespace PCDoctor.Tests;

[Collection("Variables de entorno")]
public class WindowsActionTests
{
    private static readonly ActionCatalog Catalog = WindowsPlatform.Actions();
    private static readonly ActionRunner Runner = new(ActionPolicy.Prototype);

    private static Journal TempJournal(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "PCDoctorTests", Guid.NewGuid().ToString("N"));
        return new Journal(Path.Combine(dir, "historial.db"));
    }

    [Fact]
    public async Task DisableProxyFullCycleOnTestKey()
    {
        var keyPath = $@"Software\PCDoctorTests\proxy-{Guid.NewGuid():N}";
        using (var k = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            k.SetValue("ProxyServer", "10.1.2.3:8080", RegistryValueKind.String);
            k.SetValue("AutoConfigURL", "http://malo.example/proxy.pac", RegistryValueKind.String);
        }
        try
        {
            using var j = TempJournal(out _);
            var ctx = new ActionContext { Parameters = new Dictionary<string, string> { ["clave"] = keyPath } };
            var outcome = await Runner.RunAsync(Catalog.Get("net.proxy.disable")!, ctx, j);
            Assert.Equal(OutcomeStatus.Succeeded, outcome.Status);
            using (var k = Registry.CurrentUser.OpenSubKey(keyPath)!)
            {
                Assert.Equal(0, k.GetValue("ProxyEnable"));
                Assert.Null(k.GetValue("AutoConfigURL"));
            }

            var undo = await Runner.UndoAsync(j.Operations().Single(), Catalog, new ActionContext(), j);
            Assert.True(undo.Ok, undo.Message);
            using (var k = Registry.CurrentUser.OpenSubKey(keyPath)!)
            {
                Assert.Equal(1, k.GetValue("ProxyEnable"));
                Assert.Equal("10.1.2.3:8080", k.GetValue("ProxyServer"));
                Assert.Equal("http://malo.example/proxy.pac", k.GetValue("AutoConfigURL"));
                Assert.Null(k.GetValue("ProxyOverride")); // lo que no existía, sigue sin existir
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task ProxyActionRefusesKeysOutsideTheLab()
    {
        using var j = TempJournal(out _);
        var ctx = new ActionContext { Parameters = new Dictionary<string, string> { ["clave"] = @"Software\Microsoft\Windows\CurrentVersion\Run" } };
        var outcome = await Runner.RunAsync(Catalog.Get("net.proxy.disable")!, ctx, j);
        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
        Assert.Empty(j.Operations());
    }

    [Fact]
    public async Task CleanTempDeletesOnlyOldUnprotectedFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PCDoctorTests", "temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "viejo", "sub"));
        Directory.CreateDirectory(Path.Combine(dir, ".net"));
        var old = DateTime.Now.AddDays(-5);
        string Make(string rel, DateTime when)
        {
            var p = Path.Combine(dir, rel);
            File.WriteAllText(p, new string('x', 1000));
            File.SetLastWriteTime(p, when);
            return p;
        }
        var oldFile = Make("viejo.tmp", old);
        var nested = Make(Path.Combine("viejo", "sub", "a.tmp"), old);
        var recent = Make("reciente.tmp", DateTime.Now);
        var ownRuntime = Make(Path.Combine(".net", "propio.dll"), old);
        Directory.SetLastWriteTime(Path.Combine(dir, "viejo", "sub"), old);
        try
        {
            using var j = TempJournal(out _);
            var ctx = new ActionContext { Parameters = new Dictionary<string, string> { ["carpeta"] = dir } };
            var outcome = await Runner.RunAsync(Catalog.Get("clean.temp")!, ctx, j);
            Assert.Equal(OutcomeStatus.Succeeded, outcome.Status);
            Assert.False(File.Exists(oldFile));
            Assert.False(File.Exists(nested));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(ownRuntime));
            Assert.Contains("2 archivos", outcome.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CleanTempRefusesFoldersOutsideTemp()
    {
        using var j = TempJournal(out _);
        var ctx = new ActionContext { Parameters = new Dictionary<string, string> { ["carpeta"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) } };
        var outcome = await Runner.RunAsync(Catalog.Get("clean.temp")!, ctx, j);
        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task CleanTempRefusesWhenTempPointsSomewhereElse()
    {
        var original = Environment.GetEnvironmentVariable("TMP");
        try
        {
            // Si TEMP/TMP apuntan al perfil (por error o a propósito), no se borra nada.
            Environment.SetEnvironmentVariable("TMP", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            using var j = TempJournal(out _);
            var outcome = await Runner.RunAsync(Catalog.Get("clean.temp")!, new ActionContext(), j);
            Assert.Equal(OutcomeStatus.Failed, outcome.Status);
            Assert.Contains("no es la estándar", outcome.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", original);
        }
    }

    [Fact]
    public void AgentNeverReceivesLabParameters()
    {
        var clean = AgentServer.Sanitize(new Dictionary<string, string> { ["carpeta"] = "C:\\", ["clave"] = "x", ["raiz"] = "y", ["nombre"] = "ok" });
        Assert.Equal(["nombre"], clean.Keys);
    }

    [Fact]
    public async Task OpenSettingsRefusesAnythingNotWhitelisted()
    {
        var ctx = new ActionContext { Parameters = new Dictionary<string, string> { ["uri"] = @"file:///C:/Windows/System32/cmd.exe" } };
        var outcome = await Runner.RunAsync(Catalog.Get(WindowsPlatform.OpenAction)!, ctx, null);
        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
    }

    [Fact]
    [Trait("Tipo", "EnVivo")]
    public async Task FlushDnsWorksOnThisPc()
    {
        using var j = TempJournal(out _);
        var outcome = await Runner.RunAsync(Catalog.Get("net.dns.flush")!, new ActionContext(), j);
        Assert.True(outcome.Status is OutcomeStatus.Succeeded or OutcomeStatus.VerifyFailedNotReversible, outcome.Message);
    }

    [Fact]
    public void EveryCatalogActionHasHonestMetadata()
    {
        foreach (var a in Catalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.WhatChanges), a.Id);
            Assert.False(string.IsNullOrWhiteSpace(a.HowToUndo), a.Id);
            // Lo que no tiene pruebas (T0) puede estar registrado, pero la política nunca lo deja aparecer ni ejecutarse.
            if (a.Maturity < Maturity.T1) Assert.False(ActionPolicy.Prototype.Allows(a), a.Id);
        }
    }
}

public class WindowsProbeTests
{
    [Fact]
    [Trait("Tipo", "EnVivo")]
    public async Task EveryProbeRunsOnThisPcAndOnlyProducesDeclaredFacts()
    {
        var ctx = new ProbeContext { IsElevated = WindowsEnvironment.IsElevated(), Paths = Core.ContentPaths.Discover(Repo.Root) };
        foreach (var probe in WindowsPlatform.Probes())
        {
            var result = new ProbeResult { ProbeId = probe.Id, Title = probe.Title };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await probe.RunAsync(ctx, result, cts.Token);
            Assert.Null(result.Error);
            var undeclared = result.Facts.All.Keys.Except(probe.FactKeys, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.True(undeclared.Count == 0, $"{probe.Id} produce hechos no declarados: {string.Join(", ", undeclared)}");
            Assert.NotEmpty(result.Facts.All);
        }
    }

    [Fact]
    public void LifecycleDataParsesKnownDates()
    {
        var data = LifecycleData.Load(Path.Combine(Repo.Root, "data"));
        var v24h2 = data.Versiones.Single(v => v.Build == 26100);
        Assert.Equal(new DateTime(2026, 10, 13), v24h2.Consumo);
        Assert.Equal(new DateTime(2027, 10, 12), v24h2.Empresa);
        Assert.Equal(new DateTime(2025, 10, 14), data.Versiones.Single(v => v.Build == 19045).Consumo);
    }

    [Theory]
    [InlineData("0x0000009f (0x3, 0x1)", "DRIVER_POWER_STATE_FAILURE (0x9F)")]
    [InlineData("0x00000124", "WHEA_UNCORRECTABLE_ERROR (0x124)")]
    [InlineData("0x0000dead", "0xDEAD")]
    public void BugCheckNames(string raw, string expected) =>
        Assert.Equal(expected, EventsProbe.BugCheckName(System.Text.RegularExpressions.Regex.Match(raw, "0x[0-9a-fA-F]+").Value));

    [Theory]
    [InlineData(0x61100u, true, true)]    // Defender activo y al día
    [InlineData(0x60100u, false, true)]   // desactivado
    [InlineData(0x61110u, true, false)]   // activo, firmas viejas
    [InlineData(0x41000u, true, true)]
    public void DecodesSecurityCenterState(uint state, bool enabled, bool upToDate) =>
        Assert.Equal((enabled, upToDate), SecurityProbe.DecodeProductState(state));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070", "PCI\\VEN_10DE&DEV_2F04", true)]
    [InlineData("AMD Radeon RX 6600", "PCI\\VEN_1002&DEV_73FF", true)]
    [InlineData("AMD Radeon(TM) Graphics", "PCI\\VEN_1002&DEV_1638", false)]
    [InlineData("Intel(R) UHD Graphics 620", "PCI\\VEN_8086&DEV_5917", false)]
    [InlineData("Intel(R) Arc(TM) A750 Graphics", "PCI\\VEN_8086&DEV_56A1", true)]
    [InlineData("Intel(R) Arc(TM) Graphics", "PCI\\VEN_8086&DEV_7D55", false)]
    public void ClassifiesIntegratedVsDedicatedGpus(string name, string pnp, bool dedicated) =>
        Assert.Equal(dedicated, GpuProbe.IsDedicated(name, pnp));
}

[CollectionDefinition("Variables de entorno", DisableParallelization = true)]
public class EnvironmentCollection;
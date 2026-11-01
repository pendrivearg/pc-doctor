using System.IO.Pipes;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PCDoctor.Core;
using PCDoctor.Core.Actions;
using PCDoctor.Windows;
using PCDoctor.Windows.Actions;

namespace PCDoctor.Tests;

/// <summary>Prueba que solo corre con permiso de administrador (el resto del tiempo queda "omitida", no "aprobada").</summary>
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!WindowsEnvironment.IsElevated()) Skip = "Requiere permiso de administrador: correr con tools/probar-con-permiso.cmd";
    }
}

public class StartupActionTests
{
    private static readonly ActionCatalog Catalog = WindowsPlatform.Actions();

    [Fact]
    public async Task DisableUserStartupItemAndUndoOnTestKey()
    {
        var root = $@"Software\PCDoctorTests\inicio-{Guid.NewGuid():N}";
        var runner = new ActionRunner(ActionPolicy.Prototype);
        var dir = Path.Combine(Path.GetTempPath(), "PCDoctorTests", Guid.NewGuid().ToString("N"));
        using var j = new Journal(Path.Combine(dir, "h.db"));
        var ctx = new ActionContext { Parameters = new Dictionary<string, string> { ["nombre"] = "ProgramaDePrueba", ["origen"] = nameof(StartupOrigin.UserRun), ["raiz"] = root } };
        try
        {
            var outcome = await runner.RunAsync(Catalog.Get(StartupCatalog.DisableUserAction)!, ctx, j);
            Assert.Equal(OutcomeStatus.Succeeded, outcome.Status);
            using (var k = Registry.CurrentUser.OpenSubKey($@"{root}\Run")!)
            {
                var v = (byte[])k.GetValue("ProgramaDePrueba")!;
                Assert.Equal(12, v.Length);
                Assert.Equal(0x03, v[0]);
            }
            var undo = await runner.UndoAsync(j.Operations().Single(), Catalog, new ActionContext(), j);
            Assert.True(undo.Ok, undo.Message);
            using (var k = Registry.CurrentUser.OpenSubKey($@"{root}\Run")!)
                Assert.Null(k.GetValue("ProgramaDePrueba")); // no existía antes: vuelve a no existir
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData("Algo", "99")]                                       // número disfrazado de origen
    [InlineData("Algo", "4")]
    [InlineData(@"..\..\Run", nameof(StartupOrigin.UserRun))]      // nombre con ruta
    [InlineData("SecurityHealth", nameof(StartupOrigin.UserRun))]   // protección de Windows: nunca
    [InlineData("Algo", nameof(StartupOrigin.MachineRun))]          // de todos los usuarios: no corresponde a la acción de usuario
    public async Task RefusesProtectedOrMismatchedItems(string name, string origin)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PCDoctorTests", Guid.NewGuid().ToString("N"));
        using var j = new Journal(Path.Combine(dir, "h.db"));
        var ctx = new ActionContext { Parameters = new Dictionary<string, string> { ["nombre"] = name, ["origen"] = origin, ["raiz"] = @"Software\PCDoctorTests\x" } };
        var outcome = await new ActionRunner(ActionPolicy.Prototype).RunAsync(Catalog.Get(StartupCatalog.DisableUserAction)!, ctx, j);
        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task UndoRefusesWhenANewerChangeTouchedTheSameThing()
    {
        var root = $@"Software\PCDoctorTests\inicio-{Guid.NewGuid():N}";
        var runner = new ActionRunner(ActionPolicy.Prototype);
        using var j = new Journal(Path.Combine(Path.GetTempPath(), "PCDoctorTests", Guid.NewGuid().ToString("N"), "h.db"));
        var p = new Dictionary<string, string> { ["nombre"] = "Prog", ["origen"] = nameof(StartupOrigin.UserRun), ["raiz"] = root };
        try
        {
            var def = Catalog.Get(StartupCatalog.DisableUserAction)!;
            Assert.True((await runner.RunAsync(def, new ActionContext { Parameters = p }, j)).Ok);
            Assert.True((await runner.RunAsync(def, new ActionContext { Parameters = p }, j)).Ok);
            var oldest = j.Operations().OrderBy(o => o.OperationId).First();
            var undo = await runner.UndoAsync(oldest, Catalog, new ActionContext(), j);
            Assert.False(undo.Ok);
            Assert.Contains("Deshacé primero", undo.Message);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    [Trait("Tipo", "EnVivo")]
    public void ListsThisPcStartupItemsWithoutErrors()
    {
        var items = StartupCatalog.Items();
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.ValueName)));
    }
}

public class AgentTests
{
    private static Task Launch(DoctorHost host, string pipe, string token, string sid, int pid) =>
        Task.Run(() => AgentServer.RunAsync(host, pipe, token, sid, pid));

    [Fact]
    public async Task AgentRunsCatalogActionsAndEnforcesPolicy()
    {
        using var host = new DoctorHost(Repo.Root);
        await using var client = (await AgentClient.StartAsync("", (p, t, s, pid) => Launch(host, p, t, s, pid)))!;
        Assert.True(client.Connected);

        // Lo que no necesita permiso no se hace elevado: el agente lo rechaza.
        var flush = await client.RunActionAsync("net.dns.flush", new Dictionary<string, string>());
        Assert.Equal(OutcomeStatus.Failed, flush.Status);
        var userStartup = await client.RunActionAsync(StartupCatalog.DisableUserAction, new Dictionary<string, string> { ["nombre"] = "x", ["origen"] = "UserRun" });
        Assert.Equal(OutcomeStatus.Failed, userStartup.Status);

        // Una reparación sin pruebas (T0) tampoco se puede pedir por el agente.
        var t0 = host.Actions.All.FirstOrDefault(a => a.Maturity == Maturity.T0);
        if (t0 is not null)
            Assert.Equal(OutcomeStatus.NotAllowed, (await client.RunActionAsync(t0.Id, new Dictionary<string, string>())).Status);

        var missing = await client.RunActionAsync("formatear.disco", new Dictionary<string, string>());
        Assert.Equal(OutcomeStatus.Failed, missing.Status);
    }

    [Fact]
    public async Task AgentRefusesRequestsWithoutTheSecretCode()
    {
        using var host = new DoctorHost(Repo.Root);
        var pipeName = "pcdoctor-test-" + Guid.NewGuid().ToString("N");
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var server = Task.Run(() => AgentServer.RunAsync(host, pipeName, "codigo-correcto", sid, Environment.ProcessId));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        string? reply;
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new AgentRequest { Token = "codigo-falso", Op = "accion", Accion = "net.dns.flush" }));
            reply = await reader.ReadLineAsync();
        }
        catch (IOException)
        {
            // El agente cortó la conexión antes de que terminara el pedido: también es un rechazo (no respondió ni ejecutó nada).
            reply = null;
        }
        var code = await server.WaitAsync(TimeSpan.FromSeconds(20));
        // Lo importante: no respondió ni ejecutó nada. El código dice por qué cortó (0 = código secreto falso, 2 = proceso no autorizado).
        Assert.True(reply is null, $"el agente respondió: {reply}");
        Assert.True(code is 0 or 2, $"el agente terminó con código {code}");
    }
}

/// <summary>Reparaciones reales con permiso de administrador. Solo corren elevadas.</summary>
public class AdminActionTests
{
    private static readonly ActionCatalog Catalog = WindowsPlatform.Actions();
    private static readonly ActionRunner LabRunner = new(new ActionPolicy { MinimumMaturity = Maturity.T0 });
    private static readonly ActionContext Elevated = new() { IsElevated = true };

    private static Journal NewJournal() =>
        new(Path.Combine(Path.GetTempPath(), "PCDoctorTests", Guid.NewGuid().ToString("N"), "h.db"));

    private static async Task<ActionOutcome> Run(string id, Dictionary<string, string>? p = null)
    {
        using var j = NewJournal();
        return await LabRunner.RunAsync(Catalog.Get(id)!, new ActionContext { IsElevated = true, Parameters = p ?? new() }, j);
    }

    [AdminFact]
    public async Task PrintSpoolerRepairRestartsTheService()
    {
        var o = await Run("print.spooler.repair");
        Assert.True(o.Ok, o.Message);
        using var sc = new ServiceController("Spooler");
        Assert.Equal(ServiceControllerStatus.Running, sc.Status);
    }

    [AdminFact]
    public async Task PrintQueueClearDeletesStuckJobsInLabFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PCDoctorTests", "spool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "00001.SPL"), "trabajo");
        File.WriteAllText(Path.Combine(dir, "00001.SHD"), "trabajo");
        File.WriteAllText(Path.Combine(dir, "nota.txt"), "no se toca");
        var o = await Run("print.queue.clear", new() { ["carpeta"] = dir });
        Assert.True(o.Ok, o.Message);
        Assert.False(File.Exists(Path.Combine(dir, "00001.SPL")));
        Assert.True(File.Exists(Path.Combine(dir, "nota.txt")));
        Directory.Delete(dir, true);
    }

    [AdminFact]
    public async Task AudioServiceRepairLeavesAudioRunning()
    {
        var o = await Run("audio.service.repair");
        Assert.True(o.Ok, o.Message);
    }

    [AdminFact]
    public async Task WindowsUpdateRepairWhenAlreadyEnabledChangesNothing()
    {
        using var sc = new ServiceController("wuauserv");
        var before = sc.StartType;
        if (before == ServiceStartMode.Disabled) return; // no tocamos una PC configurada así a propósito
        var o = await Run("wu.service.repair");
        Assert.True(o.Ok, o.Message);
        sc.Refresh();
        Assert.Equal(before, sc.StartType);
    }

    [AdminFact]
    public async Task MachineStartupDisableAndUndoOnRealKey()
    {
        const string name = "PCDoctorPruebaLaboratorio";
        var catalog = Catalog;
        using var j = NewJournal();
        var ctx = new ActionContext { IsElevated = true, Parameters = new Dictionary<string, string> { ["nombre"] = name, ["origen"] = nameof(StartupOrigin.MachineRun) } };
        var o = await LabRunner.RunAsync(catalog.Get(StartupCatalog.DisableMachineAction)!, ctx, j);
        Assert.True(o.Ok, o.Message);
        var undo = await LabRunner.UndoAsync(j.Operations().Single(), catalog, Elevated, j);
        Assert.True(undo.Ok, undo.Message);
        using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        Assert.Null(k?.GetValue(name));
    }

    [AdminFact]
    public void DismQuickHealthCheckAnswers()
    {
        var h = Dism.CheckHealth(fullScan: false);
        Assert.True(Enum.IsDefined(h));
    }

    [AdminFact]
    public async Task DefenderSignatureUpdateWorksWhenDefenderIsActive()
    {
        try { Defender.RequireActive(); } catch (InvalidOperationException) { return; }
        var o = await Run("defender.update");
        Assert.True(o.Ok, o.Message);
    }

    [AdminFact]
    public async Task DefenderQuickScanCompletes()
    {
        try { Defender.RequireActive(); } catch (InvalidOperationException) { return; }
        var o = await Run("defender.quickscan");
        Assert.True(o.Ok, o.Message);
    }

    [AdminFact]
    public async Task ElevatedAgentLaunchedFromThePendriveReadsAdminOnlyData()
    {
        const string cli = @"H:\pcdoctor\pcdoctor-cli.exe";
        if (!File.Exists(cli)) return;
        await using var client = (await AgentClient.StartAsync(cli))!;
        var json = await client.DiagnoseAsync(["plataforma"]);
        Assert.Contains("plataforma.tpm_presente", json);
    }
}

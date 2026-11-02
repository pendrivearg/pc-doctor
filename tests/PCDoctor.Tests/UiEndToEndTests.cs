using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PCDoctor.App;
using PCDoctor.Core;
using PCDoctor.Core.Actions;

namespace PCDoctor.Tests;

public static class HeadlessUi
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>
/// Prueba de punta a punta de la interfaz real (sin pantalla): recorre todas las pantallas y caminos
/// y comprueba que ninguno termina en la pantalla de error.
/// </summary>
[Trait("Tipo", "EnVivo")]
public class UiEndToEndTests
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
    {
        Environment.SetEnvironmentVariable("PCDOCTOR_ROOT", Repo.Root);
        return HeadlessUnitTestSession.StartNew(typeof(HeadlessUi));
    });

    private static Task Ui(Func<Task> work) => Session.Value.Dispatch(async () => { await work(); return 0; }, CancellationToken.None);

    private static void AssertNoError(MainWindow w, string where)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.LastError is null, $"{where}: terminó en error → {w.LastError}");
    }

    private static List<Button> Buttons(MainWindow w) =>
        w.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).ToList();

    private static string Label(Button b) => b.Content switch
    {
        TextBlock t => t.Text ?? "",
        string s => s,
        _ => "",
    };

    [Fact]
    public Task EveryScreenAndPathWorks() => Ui(async () =>
    {
        var w = new MainWindow { Width = 1100, Height = 900 };
        w.Show();
        AssertNoError(w, "inicio");
        var home = Buttons(w).Select(Label).ToList();
        Assert.Contains("ANALIZAR Y SOLUCIONAR MI COMPUTADORA", home);
        Assert.Contains("Historial de reparaciones", home);
        foreach (var area in Areas.All) Assert.Contains(Areas.Title(area), home);

        // Cada categoría de la pantalla principal.
        foreach (var area in Areas.All)
        {
            await w.SnapAreaAsync(area);
            AssertNoError(w, $"categoría {area}");
            Assert.NotNull(w.Report);
            Assert.NotEmpty(w.Report!.Probes);
            Assert.All(w.Report.Probes, p => Assert.Null(p.Error));
        }

        // Análisis completo, en modo sencillo y técnico, con letra chica y grande.
        await w.SnapFullAsync();
        AssertNoError(w, "análisis completo");
        foreach (var tech in new[] { false, true })
            foreach (var scale in new[] { 0.85, 1.75, 1.0 })
            {
                w.SnapTechnical(tech);
                w.SnapScale(scale);
                AssertNoError(w, $"resultados técnico={tech} escala={scale}");
            }
        w.SnapTechnical(false);

        // Los botones visibles en los resultados tienen texto (ningún botón vacío o sin sentido).
        Assert.All(Buttons(w), b => Assert.True(b.Content is not TextBlock tb || !string.IsNullOrWhiteSpace(tb.Text), "botón sin texto"));

        // Problemas contados con palabras.
        foreach (var text in new[]
                 {
                     "mi computadora está muy lenta y no puedo abrir Word", "no tengo sonido", "no me anda internet",
                     "no me sale la @", "la impresora no imprime", "creo que tengo un virus", "se me apaga sola",
                     "las letras son muy chiquitas", "el juego anda lento", "windows anda raro",
                 })
        {
            await w.SnapAskAsync(text);
            AssertNoError(w, $"«{text}»");
            Assert.NotNull(w.Report);
            Assert.Equal(text, w.Report!.Symptom);
            Assert.Contains(w.Report.Findings, f => f.FromSymptom);
        }

        // Texto que no coincide con nada: vuelve al inicio con un aviso, sin error.
        await w.SnapAskAsync("xyzzy plugh");
        AssertNoError(w, "texto sin coincidencias");

        // Una reparación real desde la interfaz (sin riesgo: vaciar la caché DNS).
        var outcome = await w.SnapExecuteAsync("net.dns.flush", new Dictionary<string, string>());
        Assert.NotNull(outcome);
        Assert.True(outcome!.Status is OutcomeStatus.Succeeded or OutcomeStatus.VerifyFailedNotReversible, outcome.Message);

        // Conversación (sin IA en desarrollo: responde el intérprete de reglas y revisa la PC).
        await w.SnapChatAsync("no tengo sonido");
        AssertNoError(w, "conversación");
        await w.SnapChatAsync("y además la compu está lenta");
        AssertNoError(w, "conversación, segundo mensaje");

        // Historial y regreso al inicio.
        w.SnapHistory();
        AssertNoError(w, "historial");
        w.SnapHome();
        AssertNoError(w, "volver al inicio");
        w.Close();
    });

    [Fact]
    public Task RescueRebootExplainsStepsAndOnlyRestartsWhenAccepted() => Ui(() =>
    {
        var restarts = 0;
        var originalRestart = MainWindow.RestartToBootMenu;
        var originalAvailable = MainWindow.RescueAvailable;
        MainWindow.RestartToBootMenu = () => restarts++; // nunca reinicia esta PC
        try
        {
            var w = new MainWindow { Width = 1100, Height = 900 };
            foreach (var (label, expected) in new[] { ("No, ahora no", 0), ("Sí, reiniciar ahora", 1) })
            {
                var accepted = 0;
                var dialog = w.BuildRescueRebootDialog(() => accepted++);
                dialog.Show();
                Dispatcher.UIThread.RunJobs();
                var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
                Assert.Contains(texts, t => t.Contains("«Usar un dispositivo»"));
                Assert.Contains(texts, t => t.Contains("BitLocker"));
                dialog.GetVisualDescendants().OfType<Button>().Single(b => Label(b) == label)
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(expected, accepted);
            }
            Assert.Equal(0, restarts); // armar la ventana nunca reinicia por sí solo

            // El botón solo aparece con la parte de rescate del pendrive enchufada.
            foreach (var available in new[] { true, false })
            {
                MainWindow.RescueAvailable = () => available;
                var home = new MainWindow { Width = 1100, Height = 900 };
                home.Show();
                AssertNoError(home, "inicio");
                Assert.Equal(available, Buttons(home).Select(Label).Contains("Revisar con Windows apagado"));
                home.Close();
            }
        }
        finally
        {
            MainWindow.RestartToBootMenu = originalRestart;
            MainWindow.RescueAvailable = originalAvailable;
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task FromThePendriveOffersAutoOpenAndUpdates() => Ui(async () =>
    {
        var pendrive = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PCDoctorPendrive", Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(pendrive, "pcdoctor"));
        File.WriteAllText(Path.Combine(pendrive, "pcdoctor", AutoOpen.ExeName), "abridor");
        File.WriteAllText(Path.Combine(pendrive, "autorun.inf"), "[autorun]");
        var install = Path.Combine(pendrive, "instalado");
        var runKey = @"Software\PCDoctorPruebas\Run-" + Guid.NewGuid().ToString("N");
        MainWindow.PendriveRootOverride = () => pendrive;
        MainWindow.SealValid = _ => true;
        MainWindow.AutoOpenFactory = () => new AutoOpen(install, runKey);
        string? declined = null;
        try
        {
            var w = new MainWindow { Width = 1100, Height = 900 };
            w.Show();
            await w.SealCheck!;
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
            AssertNoError(w, "inicio desde el pendrive");
            var home = Buttons(w).Select(Label).ToList();
            Assert.Contains("Actualizar el pendrive", home);
            Assert.Contains("Sí, que se abra solo", home);
            Assert.True(File.GetAttributes(Path.Combine(pendrive, "pcdoctor")).HasFlag(FileAttributes.Hidden)); // al abrir el pendrive solo se ve el programa
            Assert.True(File.GetAttributes(Path.Combine(pendrive, "autorun.inf")).HasFlag(FileAttributes.Hidden));

            // «No, gracias»: no se vuelve a ofrecer en esta PC y no se instala nada.
            Buttons(w).Single(b => Label(b) == "No, gracias").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            declined = Path.Combine(w.MachineDirForTests, "no-abrir-solo.txt");
            Assert.True(File.Exists(declined));
            Assert.DoesNotContain("Sí, que se abra solo", Buttons(w).Select(Label));
            Assert.False(Directory.Exists(install));

            // Pantalla de actualizar: por Internet y desde un archivo; un archivo falso se rechaza sin romper nada.
            Buttons(w).Single(b => Label(b) == "Actualizar el pendrive").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            AssertNoError(w, "actualizar el pendrive");
            var update = Buttons(w).Select(Label).ToList();
            Assert.Contains("Buscar la versión nueva por Internet", update);
            Assert.Contains("Instalar la actualización desde un archivo", update);
            var fake = Path.Combine(pendrive, "pcdoctor-actualizacion-9.9.9.zip");
            File.WriteAllText(fake, "no es una actualización");
            w.SnapHome();
            Dispatcher.UIThread.RunJobs();
            Buttons(w).Single(b => Label(b) == "Actualizar el pendrive").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Instalar esta actualización", Buttons(w).Select(Label)); // la encontró sola en el pendrive
            await w.StageBundleAsync(fake);
            AssertNoError(w, "archivo de actualización falso");
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains(texts, t => t.Contains("no es una actualización de PC Doctor") && t.Contains("No se cambió nada"));
            Assert.DoesNotContain("Instalar ahora", Buttons(w).Select(Label));
            w.Close();
        }
        finally
        {
            MainWindow.PendriveRootOverride = null;
            MainWindow.SealValid = root => PCDoctor.Core.Updates.ProgramSeal.Verify(root, PCDoctor.Core.Updates.UpdateKeys.PublicKeyPem);
            MainWindow.AutoOpenFactory = AutoOpen.ForThisUser;
            if (declined is not null) File.Delete(declined);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\PCDoctorPruebas", throwOnMissingSubKey: false);
            foreach (var f in Directory.EnumerateFileSystemEntries(pendrive, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(pendrive, recursive: true);
        }
    });

    [Fact]
    public void AutoOpenInstallsOnlyForThisUserAndRemovesCleanly()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PCDoctorAbrir", Guid.NewGuid().ToString("N"))).FullName;
        var runKey = @"Software\PCDoctorPruebas\Run-" + Guid.NewGuid().ToString("N");
        try
        {
            var source = Path.Combine(dir, "pendrive-abridor.exe");
            File.WriteAllText(source, "abridor v1");
            var opener = new AutoOpen(Path.Combine(dir, "instalado"), runKey);
            Assert.False(opener.IsEnabled);
            opener.Enable(source, start: false);
            Assert.True(opener.IsEnabled);
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey))
                Assert.Equal($"\"{opener.ExePath}\"", key!.GetValue(AutoOpen.ValueName));
            Assert.False(opener.Refresh(source, start: false)); // igual: no hace nada
            File.WriteAllText(source, "abridor v2");
            Assert.True(opener.Refresh(source, start: false)); // el pendrive se actualizó: la PC queda al día
            Assert.Equal("abridor v2", File.ReadAllText(opener.ExePath));
            opener.Disable();
            Assert.False(opener.IsEnabled);
            Assert.False(Directory.Exists(Path.Combine(dir, "instalado")));
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey))
                Assert.Null(key?.GetValue(AutoOpen.ValueName));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\PCDoctorPruebas", throwOnMissingSubKey: false);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public Task PermissionNoticeIsClearAndHasBothChoices() => Ui(() =>
    {
        var w = new MainWindow();
        var dialog = w.BuildPermissionDialog();
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains(texts, t => t.Contains("Tocá el botón «Sí»"));
        Assert.Contains("Entendido, seguir", texts);
        Assert.Contains("Ahora no", texts);
        dialog.Close();
        w.Close();
        return Task.CompletedTask;
    });
}

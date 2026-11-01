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

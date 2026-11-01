using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using PCDoctor.App;

if (args is ["icono", var icoPath, ..])
{
    using var iconSession = HeadlessUnitTestSession.StartNew(typeof(SnapEntry));
    await iconSession.Dispatch(() => { IconMaker.Write(icoPath); return 0; }, CancellationToken.None);
    Console.WriteLine(icoPath);
    return;
}

var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "pcdoctor-snaps");
Directory.CreateDirectory(outDir);

using var session = HeadlessUnitTestSession.StartNew(typeof(SnapEntry));
await session.Dispatch(async () =>
{
    void Save(MainWindow w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = w.CaptureRenderedFrame();
        frame!.Save(Path.Combine(outDir, name));
        Console.WriteLine(Path.Combine(outDir, name));
    }

    var w = new MainWindow { Width = 1100, Height = 900 };
    w.Show();
    Save(w, "1-inicio.png");

    w.Height = 2600;
    await w.SnapFullAsync();
    Save(w, "2-analisis-completo.png");

    w.Height = 1600;
    await w.SnapAskAsync("no me sale la @ y las letras son muy chiquitas");
    Save(w, "3-sintoma.png");

    w.Height = 2600;
    w.SnapTechnical(true);
    await w.SnapAskAsync("mi computadora está muy lenta y no puedo abrir Word");
    Save(w, "4-sintoma-tecnico.png");
    w.SnapTechnical(false);

    var dialog = w.BuildPermissionDialog();
    dialog.Show();
    Dispatcher.UIThread.RunJobs();
    using (var frame = dialog.CaptureRenderedFrame()) { frame!.Save(Path.Combine(outDir, "6-aviso-permiso.png")); }
    dialog.Close();

    w.Height = 1800;
    await w.SnapChatAsync("no tengo sonido y la compu está muy lenta");
    Save(w, "8-conversacion.png");

    w.Height = 900;
    w.SnapAbout();
    Save(w, "7-acerca-de.png");

    w.SnapHistory();
    Save(w, "5-historial.png");
    w.Close();
    return 0;
}, CancellationToken.None);

public static class SnapEntry
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

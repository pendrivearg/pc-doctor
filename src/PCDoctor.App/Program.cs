using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using PCDoctor.Core;

namespace PCDoctor.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Una sola ventana: si alguien hace doble clic dos veces, la segunda no abre otra copia.
        using var single = new Mutex(true, @"Local\PCDoctorAI-Interfaz", out var first);
        if (!first) return 0;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) AppLog.Error("sin capturar", ex); };
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Error("tarea", e.Exception); e.SetObserved(); };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // En PCs viejas o sin aceleración por GPU, Avalonia cae a dibujo por software.
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software] })
            .LogToTrace();
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

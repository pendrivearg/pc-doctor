using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PCDoctor.Core;
using PCDoctor.Core.Actions;
using PCDoctor.Core.Ai;
using PCDoctor.Core.Diagnostics;
using PCDoctor.Core.Knowledge;
using PCDoctor.Core.Reports;
using PCDoctor.Core.Updates;
using PCDoctor.Windows;

namespace PCDoctor.App;

/// <summary>
/// Ventana principal. Toda la interfaz se arma en código (sin XAML) para que sea liviana y fácil de seguir.
/// Regla de la interfaz: solo se muestran botones que hacen algo real y probado.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly IBrush Ink = Brush("#1F1F1F");
    private static readonly IBrush Muted = Brush("#5F6368");
    private static readonly IBrush CardBg = Brushes.White;
    private static readonly IBrush Line = Brush("#E3E3E3");

    private readonly DoctorHost? _host;
    private readonly string? _startupError;
    private readonly ContentControl _content = new();
    private readonly ToggleSwitch _technicalSwitch;
    private double _scale = 1.0;
    private Action? _currentView;

    private DiagnosticReport? _report;
    private IReadOnlyList<string>? _lastAreas;
    private string? _lastSymptom;
    private IReadOnlyList<ProblemDef>? _lastMatches;
    private readonly Dictionary<string, (bool ok, string message)> _cardResults = new();
    private bool _busy;
    private AgentClient? _agent;

    private bool Technical => _technicalSwitch.IsChecked == true;

    public MainWindow()
    {
        Title = "PC Doctor AI";
        Width = 1100;
        Height = 820;
        MinWidth = 640;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#F3F6FA");
        FontFamily = new FontFamily("Segoe UI, Arial");

        try
        {
            _host = new DoctorHost();
            AppLog.Configure(_host.Paths.StateRoot);
            AppLog.Info($"Inicio {Version} · {Environment.OSVersion} · administrador={_host.IsElevated}");
        }
        catch (Exception e) { _startupError = e.Message; }
        Icon = LoadIcon();
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Error("interfaz", e.Exception);
            e.Handled = true;
            _busy = false;
            Show(() => ErrorView("Ocurrió un error inesperado. Quedó anotado en el registro para el técnico. Detalle: " + e.Exception.Message));
        };

        _technicalSwitch = new ToggleSwitch { OnContent = "Modo técnico", OffContent = "Modo sencillo", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        _technicalSwitch.IsCheckedChanged += (_, _) => _currentView?.Invoke();

        var shell = new DockPanel();
        var top = TopBar();
        DockPanel.SetDock(top, Dock.Top);
        shell.Children.Add(top);
        shell.Children.Add(_content);
        Content = shell;

        if (_host is not null) StartPendriveChecks();
        if (_host is null) Show(() => ErrorView(_startupError ?? "Error desconocido"));
        else Show(HomeView);
        Closing += (_, _) =>
        {
            Voice.Stop();
            _agent?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); // al cerrar el canal, el agente elevado termina solo
            _llm?.Dispose(); // apaga el motor de IA y libera su memoria
            _host?.Dispose();
        };
    }

    // ───────────────────────────── utilidades de interfaz ─────────────────────────────

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static IBrush StatusBrush(HealthStatus s) => s switch
    {
        HealthStatus.Problem => Brush("#B3261E"),
        HealthStatus.NeedsPhysicalCheck => Brush("#6A3FB5"),
        HealthStatus.Warning => Brush("#B26A00"),
        HealthStatus.Unverified => Brush("#5F6368"),
        _ => Brush("#1B7F3B"),
    };

    private double S(double size) => Math.Round(size * _scale);

    private TextBlock Text(string text, double size = 18, FontWeight weight = FontWeight.Normal, IBrush? color = null, bool italic = false) => new()
    {
        Text = text,
        FontSize = S(size),
        FontWeight = weight,
        Foreground = color ?? Ink,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        TextWrapping = TextWrapping.Wrap,
    };

    private Button Btn(string text, Func<Task> onClick, bool primary = false, double size = 18)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = S(size), FontWeight = primary ? FontWeight.Bold : FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            Padding = new Thickness(S(16), S(10)),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(8),
        };
        if (primary) b.Classes.Add("accent");
        b.Click += async (_, _) =>
        {
            if (_busy) return;
            try { await onClick(); }
            catch (Exception e)
            {
                AppLog.Error($"botón «{text}»", e);
                _busy = false;
                Show(() => ErrorView(e.Message));
            }
        };
        return b;
    }

    private Button Btn(string text, Action onClick, bool primary = false, double size = 18) =>
        Btn(text, () => { onClick(); return Task.CompletedTask; }, primary, size);

    private Border Card(Control child, IBrush? stripe = null) => new()
    {
        Background = CardBg,
        BorderBrush = stripe ?? Line,
        BorderThickness = stripe is null ? new Thickness(1) : new Thickness(6, 1, 1, 1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(S(18), S(14)),
        Child = child,
    };

    private static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    private void Show(Action view)
    {
        _currentView = view;
        view();
    }

    private void SetPage(Control page)
    {
        _content.Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(S(20)), Child = new Panel { MaxWidth = 1000, Children = { page } } },
        };
    }

    private Control TopBar()
    {
        var title = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "PC DOCTOR AI", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
                new TextBlock { Text = "Diagnóstico y reparación de tu computadora", FontSize = 14, Foreground = Brush("#D6E4FF") },
            },
        };
        var smaller = new Button { Content = "A−", FontSize = 18, Padding = new Thickness(12, 4), Foreground = Brushes.White, Background = Brush("#2C5BB5") };
        var bigger = new Button { Content = "A+", FontSize = 18, Padding = new Thickness(12, 4), Foreground = Brushes.White, Background = Brush("#2C5BB5") };
        ToolTip.SetTip(smaller, "Letra más chica");
        ToolTip.SetTip(bigger, "Letra más grande");
        smaller.Click += (_, _) => { _scale = Math.Max(0.85, _scale - 0.15); _currentView?.Invoke(); };
        bigger.Click += (_, _) => { _scale = Math.Min(1.75, _scale + 0.15); _currentView?.Invoke(); };
        var controls = Row(10, _technicalSwitch, smaller, bigger);
        controls.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(title);
        Grid.SetColumn(controls, 1);
        grid.Children.Add(controls);
        return new Border { Background = Brush("#0B3D91"), Padding = new Thickness(20, 12), Child = grid };
    }

    // ───────────────────────────── pantalla principal ─────────────────────────────

    private void HomeView() => HomeView(null);

    private void HomeView(string? notice)
    {
        var host = _host!;
        var page = new StackPanel { Spacing = S(16) };
        page.Children.Add(Text("¿Qué problema tiene tu computadora?", 30, FontWeight.Bold));
        page.Children.Add(Text("Contalo con tus palabras, o tocá una de las opciones de abajo:", 20));
        var box = new TextBox
        {
            Watermark = "Por ejemplo: «no tengo sonido», «la compu está muy lenta», «no me sale la @»",
            FontSize = S(20),
            Padding = new Thickness(S(12)),
            MinHeight = S(52),
        };
        var ask = Btn("Preguntarle a PC Doctor", () => StartChatAsync(box.Text), primary: true, size: 20);
        box.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || _busy) return;
            try { await StartChatAsync(box.Text); }
            catch (Exception ex) { AppLog.Error("consulta", ex); _busy = false; Show(() => ErrorView(ex.Message)); }
        };
        var askRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        askRow.Children.Add(box);
        Grid.SetColumn(ask, 1);
        askRow.Children.Add(ask);
        page.Children.Add(askRow);
        if (notice is not null) page.Children.Add(Card(Text(notice, 18), StatusBrush(HealthStatus.Warning)));

        var big = Btn("ANALIZAR Y SOLUCIONAR MI COMPUTADORA", () => RunDiagnosisAsync(null, null, null), primary: true, size: 26);
        big.HorizontalAlignment = HorizontalAlignment.Stretch;
        big.Padding = new Thickness(S(20), S(26));
        page.Children.Add(big);

        page.Children.Add(Text("O tocá lo que te pasa:", 21, FontWeight.SemiBold));
        var grid = new UniformGrid { Columns = Bounds.Width is > 0 and < 820 ? 2 : 3 };
        foreach (var area in Areas.All)
        {
            var b = Btn(Areas.Title(area), () => RunDiagnosisAsync([area], null, null), size: 19);
            b.Margin = new Thickness(0, 0, 10, 10);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.MinHeight = S(70);
            grid.Children.Add(b);
        }
        var history = Btn("Historial de reparaciones", () => Show(HistoryView), size: 19);
        history.Margin = new Thickness(0, 0, 10, 10);
        history.HorizontalAlignment = HorizontalAlignment.Stretch;
        history.MinHeight = S(70);
        grid.Children.Add(history);
        page.Children.Add(grid);

        if (RescueAvailable())
        {
            var rescue = new StackPanel { Spacing = S(8) };
            rescue.Children.Add(Text("¿La PC puede estar hackeada o Windows anda muy mal?", 20, FontWeight.Bold));
            rescue.Children.Add(Text("Revisala con Windows apagado: la PC se reinicia desde este pendrive, donde los virus no se pueden esconder. También sirve para rescatar tus fotos y documentos.", 17));
            rescue.Children.Add(Btn("Revisar con Windows apagado", () => ConfirmRescueRebootAsync(), size: 19));
            page.Children.Add(Card(rescue, StatusBrush(HealthStatus.Warning)));
        }

        if (PendriveRoot() is not null && UpdateSettings.Load(host.Paths.DataDir).Enabled)
        {
            var update = Btn("Actualizar el pendrive", () => Show(UpdateView), size: 19);
            update.HorizontalAlignment = HorizontalAlignment.Left;
            page.Children.Add(Card(new StackPanel
            {
                Spacing = S(8),
                Children = { Text($"Este pendrive tiene la versión {Version}. Las versiones nuevas traen más problemas resueltos y arreglos.", 17), update },
            }));
        }

        // La oferta de abrirse solo aparece cuando termina de comprobarse el sello del pendrive (unos segundos, sin frenar la pantalla).
        _autoOpenSlot = new StackPanel();
        FillAutoOpenSlot(_autoOpenSlot);
        page.Children.Add(_autoOpenSlot);

        var footer = new StringBuilder($"{host.Knowledge.Problems.Count} problemas en la biblioteca · versión {Version} de prueba");
        if (!host.IsElevated) footer.Append(" · Abierto sin permiso de administrador: el análisis funciona igual y te va a pedir permiso solo si hace falta.");
        page.Children.Add(Text(footer.ToString(), 14, color: Muted));
        var about = Btn("Acerca de PC Doctor (licencia y privacidad)", () => Show(AboutView), size: 14);
        about.HorizontalAlignment = HorizontalAlignment.Left;
        page.Children.Add(about);
        SetPage(page);
        box.AttachedToVisualTree += (_, _) => box.Focus();
    }

    private async Task AskAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Show(() => HomeView("Escribí con tus palabras qué te pasa, por ejemplo «no me anda internet»."));
            return;
        }
        var matches = _host!.Matcher.Match(text).Select(m => m.Problem).ToList();
        if (matches.Count == 0)
        {
            var t = text;
            Show(() => HomeView($"No encontré en la biblioteca un problema que coincida con «{t}». Probá decirlo de otra forma, o tocá ANALIZAR Y SOLUCIONAR para revisar toda la computadora."));
            return;
        }
        await RunDiagnosisAsync(null, text, matches);
    }

    // ───────────────────────────── conversación ─────────────────────────────

    private sealed record ChatEntry(bool FromUser, string Text, AssistantTurn? Turn);

    private readonly List<ChatEntry> _chatLog = [];
    private ChatSession? _chat;
    private LlamaServer? _llm;
    private LocalAi.Setup? _aiSetup;
    private bool _thinking;

    private async Task StartChatAsync(string? firstMessage)
    {
        var host = _host!;
        if (_chat is null)
        {
            try { _aiSetup = LocalAi.Detect(host.Paths); }
            catch (Exception e) { AppLog.Error("detección de IA", e); _aiSetup = new LocalAi.Setup(null, null, "", "no se pudo leer la configuración de la IA"); }
            _llm = _aiSetup is { ServerExe: { } exe, Model: { } model } ? new LlamaServer(exe, _aiSetup.ModelPath, model) : null;
            _chat = new ChatSession(host.Knowledge, host.Matcher, host.Engine, host.ProbeContext, _llm);
            AppLog.Info(_llm is null ? $"Conversación sin IA: {_aiSetup.Reason}" : $"Conversación con IA: {_llm.Name} ({_aiSetup.Reason})");
        }
        Show(ChatView);
        if (!string.IsNullOrWhiteSpace(firstMessage)) await SendChatAsync(firstMessage);
    }

    private async Task SendChatAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _chat is null || _thinking) return;
        _chatLog.Add(new ChatEntry(true, text.Trim(), null));
        _thinking = true;
        Show(ChatView);
        try
        {
            var turn = await _chat.SendAsync(text.Trim());
            _chatLog.Add(new ChatEntry(false, turn.Text, turn));
        }
        finally
        {
            _thinking = false;
        }
        Show(ChatView);
    }

    private void ChatView()
    {
        var tech = Technical;
        var page = new StackPanel { Spacing = S(12) };
        var header = Row(12,
            Btn("Volver al inicio", () => Show(HomeView)),
            Btn("Nueva conversación", () => { _chat = null; _llm?.Dispose(); _llm = null; _chatLog.Clear(); _cardResults.Clear(); return StartChatAsync(null); }));
        page.Children.Add(header);
        page.Children.Add(Text("Contame qué te pasa con la computadora", 26, FontWeight.Bold));
        var status = _llm is not null
            ? $"Asistente con inteligencia artificial que funciona en esta PC, sin Internet ({_llm.Name})."
            : $"Modo sin inteligencia artificial: entiendo las frases más comunes ({_aiSetup?.Reason}).";
        page.Children.Add(Text(status, 15, color: Muted, italic: true));

        if (_chatLog.Count == 0)
            page.Children.Add(Card(Text("Escribí abajo, con tus palabras, qué problema tenés. Por ejemplo: «no tengo sonido», «la compu tarda mucho en prender» o «no me abre el Word».", 18)));

        foreach (var entry in _chatLog)
        {
            if (entry.FromUser)
            {
                page.Children.Add(new Border
                {
                    Background = Brush("#DCEBFF"),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(S(16), S(10)),
                    Margin = new Thickness(S(120), 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Child = Text(entry.Text, 18),
                });
                continue;
            }
            var turn = entry.Turn!;
            var bubble = new StackPanel { Spacing = S(8) };
            bubble.Children.Add(Text(turn.Text, 19));
            if (turn.Question is { } q) bubble.Children.Add(Text(q, 19, FontWeight.SemiBold));
            if (Voice.Available)
            {
                var speak = Btn("🔊 Leer", () => Voice.Speak(turn.Text + " " + (turn.Question ?? "")), size: 14);
                speak.HorizontalAlignment = HorizontalAlignment.Left;
                bubble.Children.Add(speak);
            }
            if (tech)
            {
                bubble.Children.Add(Text($"{(turn.UsedAi ? "Respondió la IA" : "Respondió el intérprete de reglas")} · revisado: {string.Join(", ", turn.CheckedProbes.DefaultIfEmpty("nada"))} · fichas: {string.Join(", ", turn.Problems.Select(p => p.Id).DefaultIfEmpty("ninguna"))}", 13, color: Muted));
                foreach (var n in turn.GuardNotes) bubble.Children.Add(Text("Control: " + n, 13, color: Muted));
            }
            page.Children.Add(new Border
            {
                Background = Brushes.White,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(S(16), S(12)),
                Margin = new Thickness(0, 0, S(60), 0),
                Child = bubble,
            });
            foreach (var f in turn.Findings) page.Children.Add(FindingCard(f, tech));
        }

        if (_thinking)
        {
            page.Children.Add(Text(_llm is { Running: false }
                ? "PC Doctor está preparando el asistente (la primera vez tarda unos segundos)…"
                : "PC Doctor está pensando y revisando la computadora…", 17, color: Muted, italic: true));
            page.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 8 });
        }

        var box = new TextBox { Watermark = "Escribí acá y tocá Enviar", FontSize = S(20), Padding = new Thickness(S(12)), MinHeight = S(52), IsEnabled = !_thinking };
        var send = Btn("Enviar", () => SendChatAsync(box.Text), primary: true, size: 20);
        send.IsEnabled = !_thinking;
        box.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            try { await SendChatAsync(box.Text); }
            catch (Exception ex) { AppLog.Error("conversación", ex); _thinking = false; Show(() => ErrorView(ex.Message)); }
        };
        var input = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, S(8), 0, 0) };
        input.Children.Add(box);
        Grid.SetColumn(send, 1);
        input.Children.Add(send);
        page.Children.Add(input);
        SetPage(page);
        if (!_thinking) box.AttachedToVisualTree += (_, _) => box.Focus();
        if (_content.Content is ScrollViewer sv) Avalonia.Threading.Dispatcher.UIThread.Post(() => sv.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
    }

    internal Task SnapChatAsync(string text) => StartChatAsync(text);

    // ───────────────────────────── diagnóstico ─────────────────────────────

    private async Task RunDiagnosisAsync(IReadOnlyList<string>? areas, string? symptom, IReadOnlyList<ProblemDef>? matches)
    {
        var host = _host!;
        _lastAreas = areas;
        _lastSymptom = symptom;
        _lastMatches = matches;
        _cardResults.Clear();
        var status = Text("Preparando…", 20);
        var page = new StackPanel
        {
            Spacing = S(18),
            Margin = new Thickness(0, S(60), 0, 0),
            Children =
            {
                Text(symptom is null ? "Revisando tu computadora…" : "Buscando la causa de lo que me contaste…", 26, FontWeight.Bold),
                new ProgressBar { IsIndeterminate = true, Height = 10 },
                status,
                Text("Esto tarda menos de un minuto. No hace falta que hagas nada; PC Doctor no cambia nada sin preguntarte.", 17, color: Muted),
            },
        };
        _currentView = null;
        SetPage(page);
        _busy = true;
        try
        {
            var probes = areas is null && matches is null ? host.Probes : host.Engine.ProbesFor(areas, matches);
            var progress = new Progress<string>(m => status.Text = m);
            _report = await host.Engine.RunAsync(probes, host.ProbeContext(), symptom, matches, progress);
        }
        finally
        {
            _busy = false;
        }
        Show(ResultsView);
    }

    private void ResultsView()
    {
        var host = _host!;
        var report = _report!;
        var tech = Technical;
        var page = new StackPanel { Spacing = S(14) };

        var issues = report.Issues.ToList();
        var overall = report.Overall;
        var headline = overall switch
        {
            HealthStatus.Problem => "Encontré problemas que conviene solucionar",
            HealthStatus.NeedsPhysicalCheck => "Hay algo que necesita una revisión física",
            HealthStatus.Warning => "Tu computadora funciona, pero hay cosas para mejorar",
            _ => issues.Any(f => f.Status == HealthStatus.Unverified)
                ? "No encontré problemas en lo que pude revisar"
                : "No encontré problemas",
        };
        var summary = new StackPanel { Spacing = S(8) };
        summary.Children.Add(Text(headline, 26, FontWeight.Bold, StatusBrush(overall)));
        summary.Children.Add(Text(
            string.Join(" · ",
                Count(issues.Count(f => f.Status == HealthStatus.Problem), "problema", "problemas"),
                Count(issues.Count(f => f.Status == HealthStatus.Warning), "advertencia", "advertencias"),
                Count(issues.Count(f => f.Status == HealthStatus.NeedsPhysicalCheck), "para revisión física", "para revisión física"),
                Count(issues.Count(f => f.Status == HealthStatus.Unverified), "sin poder comprobar", "sin poder comprobar"),
                Count(report.Suggestions.Count(), "mejora sugerida", "mejoras sugeridas")), 17, color: Muted));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        void AddAction(Button b) { b.Margin = new Thickness(0, S(6), 10, 0); actions.Children.Add(b); }
        AddAction(Btn("Volver al inicio", () => Show(HomeView)));
        AddAction(Btn("Volver a revisar", () => RunDiagnosisAsync(_lastAreas, _lastSymptom, _lastMatches)));
        AddAction(Btn("Guardar informe", () => SaveReport(false)));
        AddAction(Btn("Informe técnico", () => SaveReport(true)));
        if (Voice.Available) AddAction(Btn("🔊 Leer el resumen", () => Voice.Speak(SpokenSummary(report))));
        summary.Children.Add(actions);
        page.Children.Add(Card(summary, StatusBrush(overall)));

        // En modo sencillo no se ofrece: el permiso de administrador se pide solo cuando hay que reparar algo.
        var needsAdmin = tech && !report.Elevated && report.Probes.Any(p => p.Unverified.Any(u => u.Contains("administrador")));
        if (needsAdmin)
        {
            var adminBox = new StackPanel { Spacing = S(8) };
            adminBox.Children.Add(Text("Algunas comprobaciones necesitan permiso de administrador (por ejemplo, la temperatura del disco o el chip de seguridad).", 18));
            adminBox.Children.Add(Text("Windows te va a preguntar si permitís que PC Doctor haga cambios: en este paso solo lee información, no modifica nada.", 16, color: Muted));
            var b = Btn("Completar la revisión con permiso", ElevatedRerunAsync, primary: true);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            adminBox.Children.Add(b);
            page.Children.Add(Card(adminBox, StatusBrush(HealthStatus.Unverified)));
        }

        if (_lastSymptom is { } sym)
            page.Children.Add(Text($"Lo que me contaste: «{sym}». Primero están los resultados relacionados.", 18, color: Muted, italic: true));

        void Section(string title, IEnumerable<Finding> list)
        {
            var items = list.ToList();
            if (items.Count == 0) return;
            page.Children.Add(Text(title, 22, FontWeight.Bold));
            foreach (var f in items) page.Children.Add(FindingCard(f, tech));
        }
        Section("Problemas detectados", issues.Where(f => f.Status == HealthStatus.Problem));
        Section("Requiere revisión física", issues.Where(f => f.Status == HealthStatus.NeedsPhysicalCheck));
        Section("Advertencias", issues.Where(f => f.Status == HealthStatus.Warning));
        Section("No se pudo comprobar", issues.Where(f => f.Status == HealthStatus.Unverified));
        Section("Mejoras sugeridas", report.Suggestions);

        page.Children.Add(Text("Lo que se revisó", 22, FontWeight.Bold));
        foreach (var p in report.Probes) page.Children.Add(ComponentRow(report, p, tech));
        SetPage(page);
    }

    private Control FindingCard(Finding f, bool tech)
    {
        var host = _host!;
        var color = f.Kind == FindingKind.Suggestion ? Brush("#0B5FFF") : StatusBrush(f.Status);
        var box = new StackPanel { Spacing = S(8) };
        var tag = f.Kind == FindingKind.Suggestion ? "Sugerencia" : Labels.Status(f.Status);
        if (f.FromSymptom) tag += " · relacionado con lo que contaste";
        box.Children.Add(Text(tag.ToUpperInvariant(), 13, FontWeight.Bold, color));
        box.Children.Add(Text(f.Problem.Titulo.For(tech), 21, FontWeight.Bold));
        box.Children.Add(Text(tech ? f.TechnicalMessage : f.Message, 18));
        if (!f.Problem.SymptomOnly) // en las fichas por síntoma, el mensaje ya es la explicación
            box.Children.Add(Text(f.Problem.Explicacion.For(tech), 16, color: Muted));

        if (f.Problem.Pasos.Count > 0)
        {
            box.Children.Add(Text("Qué hacer:", 17, FontWeight.SemiBold));
            var n = 1;
            foreach (var step in f.Problem.Pasos)
            {
                var line = new StackPanel { Spacing = S(4) };
                line.Children.Add(Text($"{n++}. {step.Texto}", 17));
                if (step.Abrir is { } target)
                {
                    var open = Btn(target.StartsWith("ms-settings:") ? "Abrir esa configuración" : "Abrir la página oficial", () => OpenTargetAsync(target), size: 15);
                    open.HorizontalAlignment = HorizontalAlignment.Left;
                    open.Margin = new Thickness(S(20), 0, 0, 0);
                    line.Children.Add(open);
                }
                box.Children.Add(line);
            }
        }

        var actionRow = new WrapPanel();
        if (f.Problem.Acciones.Any(a => a.StartsWith("startup.disable", StringComparison.Ordinal)))
            box.Children.Add(StartupList(f));
        foreach (var id in f.Problem.Acciones.Where(a => !a.StartsWith("startup.disable", StringComparison.Ordinal)))
        {
            var def = host.Actions.Get(id);
            if (def is null || !host.Runner.Policy.Allows(def)) continue; // nunca un botón que no puede funcionar
            var label = (def.Risk == Risk.Green ? "" : "Solucionar: ") + def.Title;
            if (host.Runner.Policy.MaturityLabel(def) is { } m) label += $"  ({m})";
            var b = Btn(label, () => RunActionAsync(def, f), primary: true, size: 17);
            b.Margin = new Thickness(0, S(4), 10, 0);
            actionRow.Children.Add(b);
        }
        if (Voice.Available)
        {
            var speak = Btn("🔊 Leer", () => Voice.Speak(Spoken(f, tech)), size: 15);
            speak.Margin = new Thickness(0, S(4), 10, 0);
            actionRow.Children.Add(speak);
        }
        if (actionRow.Children.Count > 0) box.Children.Add(actionRow);

        if (_cardResults.TryGetValue(f.Problem.Id, out var result))
            box.Children.Add(Card(Text(result.message, 17, FontWeight.SemiBold, result.ok ? StatusBrush(HealthStatus.Ok) : StatusBrush(HealthStatus.Problem))));

        if (f.Problem.Derivar is { } d) box.Children.Add(Text("Si no se resuelve: " + d, 16, color: Muted, italic: true));
        if (tech)
        {
            box.Children.Add(Text($"{f.Problem.Id} · madurez {f.Problem.Madurez} · {f.Problem.SourceFile}", 13, color: Muted));
            foreach (var e in f.Evidence) box.Children.Add(Text($"{e.Label} = {e.Value}", 13, color: Muted));
            foreach (var src in f.Problem.Fuentes) box.Children.Add(Text("Fuente: " + src, 13, color: Muted));
        }
        return Card(box, color);
    }

    private Control ComponentRow(DiagnosticReport report, ProbeResult p, bool tech)
    {
        var st = report.ComponentStatus(p);
        var box = new StackPanel { Spacing = S(4) };
        box.Children.Add(Row(10, Text(Labels.Status(st), 15, FontWeight.Bold, StatusBrush(st)), Text(p.Title, 17, FontWeight.SemiBold)));
        if (p.Error is { } err) box.Children.Add(Text(err, 15, color: Muted));
        var details = tech ? p.Details : p.Details.Take(4).ToList();
        foreach (var d in details) box.Children.Add(Text($"{d.Label}: {d.Value}", 15, color: Muted));
        foreach (var u in p.Unverified) box.Children.Add(Text("No se pudo comprobar: " + u, 14, color: Muted, italic: true));
        if (tech) box.Children.Add(Text($"{p.Duration.TotalSeconds:0.0} s", 13, color: Muted));
        var card = Card(box, StatusBrush(st));
        card.Padding = new Thickness(S(14), S(10));
        return card;
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static string Spoken(Finding f, bool tech)
    {
        var sb = new StringBuilder();
        sb.Append(f.Problem.Titulo.For(tech)).Append(". ").Append(tech ? f.TechnicalMessage : f.Message).Append(' ');
        var n = 1;
        foreach (var s in f.Problem.Pasos) sb.Append($"Paso {n++}: {s.Texto} ");
        return sb.ToString();
    }

    private static string SpokenSummary(DiagnosticReport r)
    {
        var sb = new StringBuilder();
        var issues = r.Issues.Where(f => f.Status != HealthStatus.Unverified).ToList();
        if (issues.Count == 0) sb.Append("No encontré problemas en lo que pude revisar. ");
        else
        {
            sb.Append($"Encontré {issues.Count} cosas para revisar. ");
            foreach (var f in issues.Take(5)) sb.Append(f.Problem.Titulo.Sencillo).Append(". ");
        }
        return sb.ToString();
    }

    // ───────────────────────────── acciones ─────────────────────────────

    private Task RunActionAsync(ActionDef def, Finding f) => RunActionAsync(def, f, new Dictionary<string, string>());

    private async Task RunActionAsync(ActionDef def, Finding f, Dictionary<string, string> parameters)
    {
        var back = _currentView ?? ResultsView; // se vuelve a la pantalla desde donde se pidió (resultados o conversación)
        if (def.Risk != Risk.Green && !await ConfirmAsync(def)) return;
        if (await ExecuteActionAsync(def, parameters) is not { } outcome)
        {
            _cardResults[f.Problem.Id] = (false, "Windows no dio el permiso, así que no se cambió nada. Si querés, probá de nuevo y en el cartel de Windows tocá «Sí».");
            Show(back);
            return;
        }
        var prefix = outcome.Status switch
        {
            OutcomeStatus.Succeeded => "Listo y comprobado. ",
            OutcomeStatus.VerifyFailedRolledBack => "No funcionó, así que dejé todo como estaba. ",
            OutcomeStatus.VerifyFailedNotReversible => "Lo hice, pero el problema sigue. ",
            OutcomeStatus.NeedsElevation => "Hace falta permiso de administrador. ",
            OutcomeStatus.NotAllowed => "",
            _ => "No se pudo hacer. ",
        };
        var undo = outcome.Ok && def.Reversible ? " Si querés, lo podés deshacer desde el Historial de reparaciones." : "";
        _cardResults[f.Problem.Id] = (outcome.Ok, prefix + outcome.Message + undo + (outcome.Ok ? " Tocá «Volver a revisar» para confirmar que el aviso desapareció." : ""));
        Show(back);
    }

    /// <summary>
    /// Ejecuta una acción en este proceso o, si necesita administrador, en el agente elevado
    /// (un solo aviso de Windows por sesión). Devuelve null si la persona no dio el permiso.
    /// </summary>
    private async Task<ActionOutcome?> ExecuteActionAsync(ActionDef def, Dictionary<string, string> parameters)
    {
        var host = _host!;
        AgentClient? agent = null;
        if (def.RequiresAdmin && !host.IsElevated)
        {
            agent = await EnsureAgentAsync();
            if (agent is null) return null;
        }
        if (def.Duration is { } duration) ShowWorking(def.Title, duration);
        _busy = true;
        try
        {
            return agent is not null
                ? await agent.RunActionAsync(def.Id, parameters)
                : await host.Runner.RunAsync(def, host.ActionContext(parameters), def.Journaled ? host.Journal : null);
        }
        catch (Exception e) when (agent is not null && e is IOException or InvalidDataException or ObjectDisposedException)
        {
            _agent = null;
            return new ActionOutcome(OutcomeStatus.Failed, "Se cortó la comunicación con el ayudante con permiso: " + e.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<AgentClient?> EnsureAgentAsync()
    {
        if (_agent is { Connected: true }) return _agent;
        var cli = FindCli() ?? throw new InvalidOperationException("No encontré la herramienta pcdoctor-cli.exe junto a PC Doctor.");
        if (!await ExplainPermissionAsync()) return null;
        VerifyAgentFiles(cli); // justo antes de pedir permiso: nadie pudo cambiar los archivos que van a correr elevados
        _agent = await AgentClient.StartAsync(cli);
        return _agent;
    }

    /// <summary>
    /// Compara los archivos del agente con las huellas SHA-256 grabadas dentro de esta interfaz al publicarla.
    /// Si algo cambió en el pendrive (un virus, un archivo dañado), no se pide permiso de administrador.
    /// </summary>
    private static void VerifyAgentFiles(string cliPath)
    {
        var expected = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "AgentHashes")?.Value;
        if (string.IsNullOrEmpty(expected)) return; // compilación de desarrollo
        var dir = Path.GetDirectoryName(cliPath)!;
        foreach (var pair in expected.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var (file, hash) = (pair.Split('=')[0], pair.Split('=')[1]);
            var path = Path.Combine(dir, file);
            var actual = File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : "";
            if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Info($"Huella distinta en {file}: no se pide permiso de administrador.");
                throw new InvalidOperationException("Un archivo de PC Doctor en el pendrive fue modificado o está dañado, así que por seguridad no voy a pedir permiso de administrador. Volvé a copiar PC Doctor desde una copia confiable.");
            }
        }
    }

    private string? FindCli() =>
        new[] { AppContext.BaseDirectory, _host!.Paths.ContentRoot }
            .Select(d => Path.Combine(d, "pcdoctor-cli.exe"))
            .FirstOrDefault(File.Exists);

    /// <summary>Antes del aviso de Windows, se explica con letra grande qué va a aparecer y qué tocar.</summary>
    private async Task<bool> ExplainPermissionAsync() => await BuildPermissionDialog().ShowDialog<bool>(this);

    internal Window BuildPermissionDialog()
    {
        var dialog = new Window
        {
            Title = "Un paso más",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Brushes.White,
            FontFamily = FontFamily,
        };
        var go = new Button { Content = new TextBlock { Text = "Entendido, seguir", FontSize = S(20), FontWeight = FontWeight.Bold }, Padding = new Thickness(S(20), S(12)) };
        go.Classes.Add("accent");
        var cancel = new Button { Content = new TextBlock { Text = "Ahora no", FontSize = S(18) }, Padding = new Thickness(S(18), S(12)) };
        go.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        var sample = new Border
        {
            Background = Brush("#F3F3F3"),
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Text("Control de cuentas de usuario", 15, color: Muted),
                    Text("¿Quieres permitir que esta aplicación haga cambios en el dispositivo?", 18, FontWeight.SemiBold),
                    Row(10,
                        new Border { Background = Brush("#0067C0"), CornerRadius = new CornerRadius(4), Padding = new Thickness(26, 6), Child = new TextBlock { Text = "Sí", Foreground = Brushes.White, FontSize = S(16) } },
                        new Border { Background = Brush("#E0E0E0"), CornerRadius = new CornerRadius(4), Padding = new Thickness(26, 6), Child = new TextBlock { Text = "No", FontSize = S(16) } }),
                },
            },
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(26),
            Spacing = 14,
            Children =
            {
                Text("Para arreglar esto, Windows te va a pedir permiso", 24, FontWeight.Bold),
                Text("Va a aparecer un cartel parecido a este. Tocá el botón «Sí».", 20),
                sample,
                Text("Es un aviso normal de Windows. Aparece una sola vez mientras uses PC Doctor, y cada cambio igual te lo pregunto antes.", 17, color: Muted),
                Text("Si Windows te pide una contraseña, es porque esta computadora la administra otra persona: pedile ayuda a ella.", 17, color: Muted),
                Row(12, go, cancel),
            },
        };
        return dialog;
    }

    private void ShowWorking(string title, string duration)
    {
        _currentView = null;
        SetPage(new StackPanel
        {
            Spacing = S(18),
            Margin = new Thickness(0, S(60), 0, 0),
            Children =
            {
                Text("Trabajando: " + title, 26, FontWeight.Bold),
                new ProgressBar { IsIndeterminate = true, Height = 10 },
                Text(duration, 20),
                Text("No apagues la computadora mientras tanto. Cuando termine, te muestro el resultado.", 18, color: Muted),
            },
        });
    }

    /// <summary>Lista de programas de inicio, cada uno con su botón: desactivar es uno por uno y reversible.</summary>
    private Control StartupList(Finding f)
    {
        var host = _host!;
        var panel = new StackPanel { Spacing = S(6) };
        panel.Children.Add(Text("Elegí los que no necesitás apenas prendés la PC (no se desinstalan):", 17, FontWeight.SemiBold));
        foreach (var item in StartupCatalog.Items().Where(i => i.Enabled && !StartupCatalog.IsProtected(i)))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            row.Children.Add(Text(item.DisplayName + (item.NeedsAdmin ? "  · todos los usuarios" : ""), 17));
            var def = host.Actions.Get(item.ActionId);
            Button button = def is not null && host.Runner.Policy.Allows(def)
                ? Btn("Que no arranque solo", () => RunActionAsync(def, f, new Dictionary<string, string> { ["nombre"] = item.ValueName, ["origen"] = item.Origin.ToString() }), size: 15)
                : Btn("Cambiarlo en Configuración", () => OpenTargetAsync("ms-settings:startupapps"), size: 15);
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            panel.Children.Add(row);
        }
        return panel;
    }

    private async Task<bool> ConfirmAsync(ActionDef def)
    {
        var dialog = new Window
        {
            Title = "¿Hago este cambio?",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Brushes.White,
            FontFamily = FontFamily,
        };
        var yes = new Button { Content = new TextBlock { Text = "Sí, hacelo", FontSize = S(18), FontWeight = FontWeight.Bold }, Padding = new Thickness(S(18), S(10)) };
        yes.Classes.Add("accent");
        var no = new Button { Content = new TextBlock { Text = "No, mejor no", FontSize = S(18) }, Padding = new Thickness(S(18), S(10)) };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        var content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                Text(def.Title, 22, FontWeight.Bold),
                Text("Qué cambia: " + def.WhatChanges, 17),
                Text("Cómo se deshace: " + def.HowToUndo, 17),
                Text("Tipo de cambio: " + Labels.Risk(def.Risk) + (def.Maturity < Maturity.T2 ? " · reparación en prueba: se comprueba el resultado y, si no funciona, se vuelve atrás." : ""), 15, color: Muted),
                Row(12, yes, no),
            },
        };
        dialog.Content = content;
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task OpenTargetAsync(string target)
    {
        var host = _host!;
        var def = host.Actions.Get(WindowsPlatform.OpenAction)!;
        var ctx = host.ActionContext(new Dictionary<string, string> { ["uri"] = target });
        var outcome = await host.Runner.RunAsync(def, ctx, null);
        if (!outcome.Ok) Show(() => ErrorView(outcome.Message));
    }

    private async Task SaveReport(bool technical)
    {
        var host = _host!;
        var file = Path.Combine(host.ReportsDir, $"informe-{(technical ? "tecnico-" : "")}{DateTime.Now:yyyyMMdd-HHmmss}.html");
        await File.WriteAllTextAsync(file, HtmlReport.Render(_report!, technical), Encoding.UTF8);
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Repite el análisis dentro del agente con permiso (modo técnico): solo lee más datos, no cambia nada.</summary>
    private async Task ElevatedRerunAsync()
    {
        var host = _host!;
        var agent = await EnsureAgentAsync();
        if (agent is null) return;
        _busy = true;
        try
        {
            var current = _report!;
            // Solo se repiten con permiso las sondas que lo necesitaban. Lo del usuario (proxy, inicio, temporales)
            // se conserva: si el permiso lo da otra cuenta, sus datos no deben reemplazar a los de quien usa la PC.
            var needAdmin = current.Probes.Where(p => p.Unverified.Any(u => u.Contains("administrador"))).Select(p => p.ProbeId).ToList();
            var elevated = ProbeExchange.Deserialize(await agent.DiagnoseAsync(needAdmin))
                .ToDictionary(p => p.ProbeId, StringComparer.OrdinalIgnoreCase);
            var elevatedFacts = new FactStore();
            foreach (var r in elevated.Values) elevatedFacts.Merge(r.Facts);
            var facts = new FactStore();
            facts.Merge(current.Facts);
            foreach (var (k, v) in elevatedFacts.All) if (!facts.Has(k)) facts.Set(k, v); // solo se agregan datos que faltaban
            var probes = current.Probes.Select(p => elevated.TryGetValue(p.ProbeId, out var e) && e.Error is null ? e : p).ToList();
            var ran = probes.Where(r => r.Error is null).Select(r => r.ProbeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _report = new DiagnosticReport
            {
                Elevated = true,
                Symptom = _lastSymptom,
                Facts = facts,
                Probes = probes,
                Findings = DiagnosticEngine.Evaluate(host.Knowledge, facts, ran, _lastMatches),
            };
            _cardResults.Clear();
        }
        finally
        {
            _busy = false;
        }
        Show(ResultsView);
    }
    // ───────────────────────────── historial ─────────────────────────────

    private void HistoryView()
    {
        var host = _host!;
        var page = new StackPanel { Spacing = S(14) };
        page.Children.Add(Row(12, Btn("Volver al inicio", () => Show(HomeView)), Text("Historial de reparaciones", 26, FontWeight.Bold)));
        page.Children.Add(Text("Todo lo que PC Doctor cambió en esta computadora queda anotado acá. Lo que se puede deshacer tiene su botón.", 17, color: Muted));
        var ops = host.Journal.Operations();
        if (ops.Count == 0) page.Children.Add(Card(Text("Todavía no se hizo ninguna reparación en esta PC.", 18)));
        foreach (var op in ops)
        {
            var ok = op.ResultStatus == nameof(OutcomeStatus.Succeeded);
            var state = op.Undone ? "Deshecho" : op.ResultStatus switch
            {
                nameof(OutcomeStatus.Succeeded) => "Hecho y comprobado",
                nameof(OutcomeStatus.VerifyFailedRolledBack) => "No funcionó; se volvió atrás",
                nameof(OutcomeStatus.VerifyFailedNotReversible) => "Se hizo, pero el problema seguía",
                nameof(OutcomeStatus.Failed) => "Falló",
                null => "Sin terminar",
                _ => op.ResultStatus,
            };
            var box = new StackPanel { Spacing = S(6) };
            box.Children.Add(Text($"{op.Started:dd/MM/yyyy HH:mm} · {op.Title}", 19, FontWeight.SemiBold));
            box.Children.Add(Text(state, 16, FontWeight.Bold, ok && !op.Undone ? StatusBrush(HealthStatus.Ok) : Muted));
            if (op.ResultMessage is { } m) box.Children.Add(Text(m, 16, color: Muted));
            if (ok && op.Reversible && !op.Undone)
            {
                var opCopy = op;
                var undo = Btn("Deshacer este cambio", async () =>
                {
                    ActionOutcome outcome;
                    if (host.Actions.Get(opCopy.ActionId) is { RequiresAdmin: true } && !host.IsElevated)
                    {
                        if (await EnsureAgentAsync() is not { } agent) return;
                        outcome = await agent.UndoAsync(opCopy.OperationId);
                    }
                    else
                    {
                        outcome = await host.Runner.UndoAsync(opCopy, host.Actions, host.ActionContext(), host.Journal);
                    }
                    Show(HistoryView);
                    if (!outcome.Ok) Show(() => ErrorView(outcome.Message));
                });
                undo.HorizontalAlignment = HorizontalAlignment.Left;
                box.Children.Add(undo);
            }
            page.Children.Add(Card(box, ok && !op.Undone ? StatusBrush(HealthStatus.Ok) : Line));
        }
        page.Children.Add(Text(host.Journal.VerifyChain()
            ? "Integridad del historial: correcta."
            : "ATENCIÓN: el historial fue modificado fuera de PC Doctor.", 14, color: Muted));
        SetPage(page);
    }

    internal static string Version =>
        (typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0").Split('+')[0];

    private static WindowIcon? LoadIcon()
    {
        var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("PCDoctor.App.pcdoctor.ico");
        return stream is null ? null : new WindowIcon(stream);
    }

    private void AboutView()
    {
        var host = _host!;
        var page = new StackPanel { Spacing = S(14) };
        page.Children.Add(Row(12, Btn("Volver al inicio", () => Show(HomeView)), Text("Acerca de PC Doctor AI", 26, FontWeight.Bold)));
        page.Children.Add(Card(new StackPanel
        {
            Spacing = S(8),
            Children =
            {
                Text($"Versión {Version} (de prueba) · {host.Knowledge.Problems.Count} problemas en la biblioteca · {host.Actions.All.Count(a => a.Journaled && host.Runner.Policy.Allows(a))} reparaciones habilitadas", 18),
                Text("Software libre bajo la Licencia Pública General de GNU, versión 3 (GPL-3.0). Se entrega SIN NINGUNA GARANTÍA. Podés usarlo, copiarlo, estudiarlo, modificarlo y redistribuirlo según esa licencia.", 17),
            },
        }));
        page.Children.Add(Text("Tu privacidad", 22, FontWeight.Bold));
        page.Children.Add(Card(new StackPanel
        {
            Spacing = S(6),
            Children =
            {
                Text("• Funciona sin Internet. No envía datos a ningún lado y no tiene publicidad. Solo se conecta si tocás «Buscar la versión nueva por Internet», y únicamente para descargarla.", 17),
                Text("• No pide, no lee y no guarda contraseñas.", 17),
                Text("• No instala nada en la computadora y nunca cambia nada sin preguntarte. La única excepción es si pedís que se abra solo al enchufar el pendrive: "
                     + "ahí queda en esta PC un programa chiquito que se quita con el botón de abajo.", 17),
                Text("• El historial de reparaciones y los informes de cada PC quedan solo en el pendrive y se pueden borrar.", 17),
                Text("• PC Doctor nunca te va a llamar por teléfono ni pedir acceso remoto o pagos: si alguien lo hace en su nombre, es una estafa.", 17),
            },
        }));
        page.Children.Add(Text("Dónde se guarda la información de esta PC: " + host.MachineDir, 14, color: Muted));
        var actions = new WrapPanel();
        void Add(Button b) { b.Margin = new Thickness(0, 0, 10, 10); actions.Children.Add(b); }
        var license = Path.Combine(host.Paths.ContentRoot, "licencias", "GPL-3.0.txt");
        if (File.Exists(license)) Add(Btn("Ver la licencia completa", () => OpenLocal(license)));
        var notices = Path.Combine(host.Paths.ContentRoot, "licencias", "THIRD-PARTY-NOTICES.md");
        if (File.Exists(notices)) Add(Btn("Componentes de terceros", () => OpenLocal(notices)));
        Add(Btn("Abrir la carpeta de informes", () => OpenLocal(host.ReportsDir)));
        if (PendriveRoot() is { } root)
        {
            Add(Btn("Actualizar el pendrive", () => Show(UpdateView), primary: true));
            var opener = AutoOpenFactory();
            if (opener.IsEnabled)
                Add(Btn("Que no se abra solo en esta PC", () =>
                {
                    opener.Disable();
                    File.WriteAllText(Path.Combine(host.MachineDir, NoAutoOpenFile), DateTime.Now.ToString("dd/MM/yyyy"));
                    Show(AboutView);
                }));
            else if (File.Exists(Path.Combine(root, "pcdoctor", AutoOpen.ExeName)) && _sealCheck is { IsCompletedSuccessfully: true, Result: true })
                Add(Btn("Que se abra solo al enchufarlo en esta PC", () =>
                {
                    opener.Enable(Path.Combine(root, "pcdoctor", AutoOpen.ExeName));
                    File.Delete(Path.Combine(host.MachineDir, NoAutoOpenFile));
                    Show(AboutView);
                }));
        }
        page.Children.Add(actions);
        SetPage(page);
    }

    /// <summary>¿Está enchufada la parte de rescate del pendrive? Sin ella, el botón de revisar con Windows apagado no aparece.</summary>
    internal static Func<bool> RescueAvailable = () =>
        DriveInfo.GetDrives().Any(d => d.DriveType == DriveType.Removable && d.IsReady
                                       && d.VolumeLabel.Equals("PCD-RESCATE", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reinicia en el menú de arranque de Windows («Elegir una opción»), donde «Usar un dispositivo» arranca desde el pendrive
    /// sin saber la tecla de cada marca. shutdown /r /o: documentado por Microsoft; no necesita permiso de administrador.
    /// En las pruebas se reemplaza para que nunca reinicie la PC.
    /// </summary>
    internal static Action RestartToBootMenu = () =>
        Process.Start(new ProcessStartInfo("shutdown.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/r", "/o", "/t", "0" } })?.Dispose();

    private async Task ConfirmRescueRebootAsync()
    {
        var accepted = false;
        await BuildRescueRebootDialog(() => accepted = true).ShowDialog(this);
        if (!accepted) return;
        AppLog.Info("Reinicio al menú de arranque para el modo rescate");
        RestartToBootMenu();
    }

    /// <summary>Explica con letra grande qué va a aparecer después del reinicio y qué tocar.</summary>
    internal Window BuildRescueRebootDialog(Action onAccept)
    {
        var dialog = new Window
        {
            Title = "Revisar con Windows apagado",
            Width = 680,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Brushes.White,
            FontFamily = FontFamily,
        };
        var yes = new Button { Content = new TextBlock { Text = "Sí, reiniciar ahora", FontSize = S(18), FontWeight = FontWeight.Bold }, Padding = new Thickness(S(18), S(10)) };
        yes.Classes.Add("accent");
        var no = new Button { Content = new TextBlock { Text = "No, ahora no", FontSize = S(18) }, Padding = new Thickness(S(18), S(10)) };
        yes.Click += (_, _) => { onAccept(); dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                Text("La PC se va a reiniciar. Hacé esto:", 22, FontWeight.Bold),
                Text("1. Guardá lo que tengas abierto y dejá el pendrive puesto.", 18),
                Text("2. Aparece una pantalla azul que dice «Elegir una opción». Tocá «Usar un dispositivo».", 18),
                Text("3. Elegí el pendrive (suele decir «USB» o «UEFI»). Arranca «PC Doctor - Modo rescate».", 18),
                Text("4. Al terminar, elegí «Apagar» en el modo rescate, sacá el pendrive y prendé la PC normalmente.", 18),
                Text("Si no aparece «Usar un dispositivo»: apagá la PC y prendela apretando varias veces la tecla de arranque (según la marca: F12, F11, F9, F8 o Esc). Si el disco tiene BitLocker, tené a mano la clave de recuperación.", 15, color: Muted),
                Row(12, yes, no),
            },
        };
        return dialog;
    }

    // ───────────────────────────── abrirse solo al enchufar ─────────────────────────────

    private const string NoAutoOpenFile = "no-abrir-solo.txt";
    private Task<bool>? _sealCheck;
    private StackPanel? _autoOpenSlot;

    /// <summary>¿El programa de este pendrive tiene el sello firmado de PC Doctor? (En las pruebas se reemplaza.)</summary>
    internal static Func<string, bool> SealValid = root => ProgramSeal.Verify(root, UpdateKeys.PublicKeyPem);
    internal static Func<AutoOpen> AutoOpenFactory = AutoOpen.ForThisUser;
    /// <summary>Para las pruebas: hace de cuenta que PC Doctor corre desde un pendrive en esa carpeta.</summary>
    internal static Func<string?>? PendriveRootOverride;
    internal Task? SealCheck => _sealCheck;
    internal string MachineDirForTests => _host!.MachineDir;

    private void StartPendriveChecks()
    {
        if (PendriveRoot() is not { } root) return;
        HidePendriveInternals(root);
        var opener = Path.Combine(root, "pcdoctor", AutoOpen.ExeName);
        _sealCheck = Task.Run(() =>
        {
            var ok = SealValid(root);
            // Si esta PC ya lo tiene activado y el pendrive se actualizó, el abridor de la PC queda al día.
            if (ok)
                try { AutoOpenFactory().Refresh(opener); }
                catch (Exception e) { AppLog.Error("abridor", e); }
            return ok;
        });
        _sealCheck.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_autoOpenSlot is { } slot) FillAutoOpenSlot(slot); }));
    }

    /// <summary>
    /// Al abrir el pendrive se ve solo «ABRIR PC DOCTOR» y el LEEME. Se repite en cada inicio porque una actualización
    /// reemplaza archivos y les saca el atributo; así también quedan ordenados los pendrives armados antes.
    /// </summary>
    private static void HidePendriveInternals(string root)
    {
        foreach (var name in new[] { "pcdoctor", "PC-Doctor-AI-codigo", "autorun.inf" })
            try
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path) || Directory.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private bool ShouldOfferAutoOpen()
    {
        if (_sealCheck is not { IsCompletedSuccessfully: true, Result: true } || PendriveRoot() is not { } root) return false;
        if (!File.Exists(Path.Combine(root, "pcdoctor", AutoOpen.ExeName)) || File.Exists(Path.Combine(_host!.MachineDir, NoAutoOpenFile))) return false;
        return !AutoOpenFactory().IsEnabled;
    }

    private void FillAutoOpenSlot(StackPanel slot)
    {
        slot.Children.Clear();
        if (!ShouldOfferAutoOpen()) return;
        var offer = new StackPanel { Spacing = S(8) };
        offer.Children.Add(Text("¿Querés que PC Doctor se abra solo cada vez que enchufes este pendrive en esta computadora?", 20, FontWeight.Bold));
        offer.Children.Add(Text("Windows no deja que un pendrive se abra solo. Si decís que sí, queda en esta PC un programa chiquito que hace solo eso: "
                                + "abre PC Doctor al enchufar el pendrive, y únicamente si es un PC Doctor original. Lo podés quitar cuando quieras desde «Acerca de».", 17));
        offer.Children.Add(Row(10,
            Btn("Sí, que se abra solo", () =>
            {
                AutoOpenFactory().Enable(Path.Combine(PendriveRoot()!, "pcdoctor", AutoOpen.ExeName));
                Show(() => HomeView("Listo: la próxima vez que enchufes este pendrive en esta PC, PC Doctor se abre solo."));
            }, primary: true, size: 18),
            Btn("No, gracias", () =>
            {
                File.WriteAllText(Path.Combine(_host!.MachineDir, NoAutoOpenFile), DateTime.Now.ToString("dd/MM/yyyy"));
                FillAutoOpenSlot(slot);
            }, size: 18)));
        slot.Children.Add(Card(offer, StatusBrush(HealthStatus.Ok)));
    }

    /// <summary>Raíz del pendrive (donde está «ABRIR PC DOCTOR.exe»). En desarrollo no hay: ahí no se actualiza nada.</summary>
    private string? PendriveRoot()
    {
        if (PendriveRootOverride is not null) return PendriveRootOverride();
        var content = Path.GetFullPath(_host!.Paths.ContentRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.GetFileName(content).Equals("pcdoctor", StringComparison.OrdinalIgnoreCase)) return null;
        var root = Path.GetDirectoryName(content)!;
        return File.Exists(Path.Combine(root, "ABRIR PC DOCTOR.exe")) ? root : null;
    }

    // ───────────────────────────── actualizar el pendrive ─────────────────────────────

    /// <summary>Dónde se busca solo un archivo de actualización ya bajado: Descargas y la raíz del pendrive.</summary>
    private IEnumerable<string> BundleFolders()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (PendriveRoot() is { } root) yield return root;
    }

    private void UpdateView()
    {
        var host = _host!;
        var page = new StackPanel { Spacing = S(14) };
        page.Children.Add(Row(12, Btn("Volver al inicio", () => Show(HomeView)), Text("Actualizar el pendrive", 26, FontWeight.Bold)));
        var info = new StackPanel { Spacing = S(6) };
        info.Children.Add(Text($"Este pendrive tiene la versión {Version}.", 19, FontWeight.SemiBold));
        var last = Path.Combine(host.Paths.ContentRoot, "ultima-actualizacion.txt");
        if (File.Exists(last)) info.Children.Add(Text("Última actualización: " + File.ReadAllText(last).Trim(), 16, color: Muted));
        info.Children.Add(Text("Se reemplazan el programa y la biblioteca de problemas. Tu historial, tus informes y los archivos rescatados no se tocan. "
                               + "Cada versión viene firmada: si alguien la cambió, no se instala.", 17));
        page.Children.Add(Card(info));

        var found = UpdateBundle.FindNewest(BundleFolders());
        if (found is not null && UpdateVersion.IsNewer(Path.GetFileNameWithoutExtension(found)[UpdateBundle.Prefix.Length..], Version))
        {
            var where = string.Equals(Path.GetDirectoryName(found)!.TrimEnd('\\'), PendriveRoot()?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? "en el pendrive" : "en Descargas";
            page.Children.Add(Card(new StackPanel
            {
                Spacing = S(8),
                Children =
                {
                    Text($"Encontré la actualización «{Path.GetFileName(found)}» {where}.", 19, FontWeight.Bold),
                    Btn("Instalar esta actualización", () => StageBundleAsync(found), primary: true, size: 19),
                },
            }, StatusBrush(HealthStatus.Ok)));
        }

        var settings = UpdateSettings.Load(host.Paths.DataDir);
        var online = Btn("Buscar la versión nueva por Internet", () => CheckUpdatesAsync(settings), primary: found is null, size: 19);
        var file = Btn("Instalar la actualización desde un archivo", () => PickBundleAsync(), size: 19);
        foreach (var b in new[] { online, file }) b.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (settings.Enabled) page.Children.Add(online);
        page.Children.Add(file);
        var site = settings.Enabled ? $"github.com/{settings.Repositorio}/releases" : "la página del proyecto";
        page.Children.Add(Text($"Si esta PC no tiene Internet, o puede estar hackeada: en otra PC o en el celular entrá a {site}, bajá el archivo "
                               + $"«{UpdateBundle.Prefix}…zip», copialo a este pendrive y tocá «Instalar la actualización desde un archivo».", 16, color: Muted));
        SetPage(page);
    }

    private async Task PickBundleAsync()
    {
        var picker = StorageProvider;
        var files = await picker.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Elegí el archivo de actualización de PC Doctor",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Actualización de PC Doctor") { Patterns = [UpdateBundle.Prefix + "*.zip", "*.zip"] }],
            SuggestedStartLocation = await picker.TryGetWellKnownFolderAsync(Avalonia.Platform.Storage.WellKnownFolder.Downloads),
        });
        if (files.Count == 0) return;
        var path = Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(files[0]);
        if (path is null) { Show(() => UpdateResultView("Ese archivo no se puede abrir desde acá. Copialo al pendrive o a Descargas y probá de nuevo.")); return; }
        await StageBundleAsync(path);
    }

    private void UpdateResultView(string message)
    {
        var page = new StackPanel { Spacing = S(14) };
        page.Children.Add(Row(12, Btn("Volver", () => Show(UpdateView)), Text("Actualizar el pendrive", 26, FontWeight.Bold)));
        page.Children.Add(Card(Text(message, 18), StatusBrush(HealthStatus.Warning)));
        SetPage(page);
    }

    /// <summary>Comprueba la firma del archivo y lo deja listo; después la persona confirma y se instala igual que desde Internet.</summary>
    internal async Task StageBundleAsync(string bundlePath)
    {
        var host = _host!;
        var status = Text("Comprobando la firma de la actualización…", 18);
        var page = new StackPanel { Spacing = S(14) };
        page.Children.Add(Row(12, Btn("Volver", () => Show(UpdateView)), Text("Actualizar el pendrive", 26, FontWeight.Bold)));
        page.Children.Add(Card(status));
        SetPage(page);
        var staging = Path.Combine(host.Paths.ContentRoot, "actualizacion");
        UpdateManifest manifest;
        _busy = true;
        try
        {
            var progress = new Progress<(long Done, long Total)>(p => status.Text = $"Comprobando y preparando: {p.Done * 100 / Math.Max(p.Total, 1)} %");
            var current = Version;
            manifest = await Task.Run(() => UpdateBundle.Stage(bundlePath, staging, UpdateKeys.PublicKeyPem, current, progress));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            status.Text = e.Message + "\nNo se cambió nada.";
            AppLog.Info("Actualización desde archivo rechazada: " + e.Message);
            return;
        }
        finally { _busy = false; }
        status.Text = $"La actualización es auténtica: versión {manifest.Version} (tenés la {Version}).\n\n{manifest.Notes}".TrimEnd()
                      + "\n\nPC Doctor se cierra y se vuelve a abrir solo.";
        page.Children.Add(Btn("Instalar ahora", () => LaunchInstallerAsync(staging, manifest.Version, status), primary: true));
    }

    private async Task CheckUpdatesAsync(UpdateSettings settings)
    {
        var status = Text("Buscando una versión nueva…", 18);
        var page = new StackPanel { Spacing = S(14) };
        page.Children.Add(Row(12, Btn("Volver", () => Show(UpdateView)), Text("Actualizar el pendrive", 26, FontWeight.Bold)));
        page.Children.Add(Card(status));
        SetPage(page);
        var client = new UpdateClient(settings.Repositorio, UpdateKeys.PublicKeyPem);
        AvailableUpdate? update;
        try { update = await client.CheckAsync(Version); }
        catch (HttpRequestException) { status.Text = "No pude conectarme a Internet. Revisá la conexión y probá de nuevo."; return; }
        catch (TaskCanceledException) { status.Text = "Internet está muy lento o no responde. Probá de nuevo más tarde."; return; }
        catch (InvalidDataException e) { status.Text = e.Message; AppLog.Info("Actualización rechazada: " + e.Message); return; }
        if (update is null) { status.Text = $"Ya tenés la última versión ({Version})."; return; }
        status.Text = $"Hay una versión nueva: {update.Manifest.Version} (tenés la {Version}).\n\n{update.Manifest.Notes}".TrimEnd()
            + $"\n\nSe descargan {update.DownloadSize / 1048576.0:0.#} MB, con firma comprobada. Se reemplazan el programa y la biblioteca; "
            + "tu historial, tus informes y los archivos rescatados no se tocan. PC Doctor se cierra y se vuelve a abrir solo.";
        page.Children.Add(Btn("Actualizar ahora", () => InstallUpdateAsync(client, update, status), primary: true));
    }

    private async Task InstallUpdateAsync(UpdateClient client, AvailableUpdate update, TextBlock status)
    {
        var host = _host!;
        var staging = Path.Combine(host.Paths.ContentRoot, "actualizacion");
        _busy = true;
        try
        {
            var progress = new Progress<(long Done, long Total)>(p =>
                status.Text = $"Descargando la versión {update.Manifest.Version}: {p.Done * 100 / Math.Max(p.Total, 1)} %");
            await client.DownloadAsync(update, staging, progress);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            _busy = false;
            status.Text = "No se pudo descargar la versión nueva: " + e.Message + "\nNo se cambió nada.";
            return;
        }
        await LaunchInstallerAsync(staging, update.Manifest.Version, status);
    }

    /// <summary>Instala una versión ya comprobada en <paramref name="staging"/>: cierra PC Doctor, reemplaza y lo vuelve a abrir.</summary>
    private async Task LaunchInstallerAsync(string staging, string version, TextBlock status)
    {
        var root = PendriveRoot() ?? throw new InvalidOperationException("Solo se puede actualizar PC Doctor desde el pendrive.");
        var cli = FindCli() ?? throw new InvalidOperationException("No encontré la herramienta pcdoctor-cli.exe junto a PC Doctor.");
        _busy = true;
        // El instalador corre desde una copia temporal, para que todos los archivos del pendrive queden libres.
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pcdoctor-actualizar-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(cli)!, "*.*").Where(f => f.EndsWith(".exe") || f.EndsWith(".dll")))
            File.Copy(f, Path.Combine(temp, Path.GetFileName(f)), overwrite: true);
        var psi = new ProcessStartInfo(Path.Combine(temp, "pcdoctor-cli.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "aplicar-actualizacion", staging, "--datos", root, "--esperar", Environment.ProcessId.ToString(), "--actual", Version, "--abrir", Path.Combine(root, "ABRIR PC DOCTOR.exe") })
            psi.ArgumentList.Add(a);
        var rescue = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.VolumeLabel.Equals("PCD-RESCATE", StringComparison.OrdinalIgnoreCase));
        if (rescue is not null) { psi.ArgumentList.Add("--rescate"); psi.ArgumentList.Add(rescue.RootDirectory.FullName); }
        AppLog.Info($"Instalando la versión {version}");
        status.Text = "Instalando… PC Doctor se va a cerrar y se vuelve a abrir solo en unos segundos.";
        await Task.Delay(1500);
        Process.Start(psi)?.Dispose();
        Close();
    }

    /// <summary>Abre un archivo o carpeta propios de PC Doctor (licencias, informes) con el programa de Windows.</summary>
    private void OpenLocal(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(_host!.Paths.ContentRoot), StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(Path.GetFullPath(_host.Paths.StateRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Solo se pueden abrir archivos de PC Doctor.");
        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose();
    }

    internal void SnapAbout() => Show(AboutView);

    // Ganchos para las capturas automáticas de pantalla (herramienta tools/PCDoctor.Snap).
    internal Task SnapAskAsync(string text) => AskAsync(text);
    internal Task SnapFullAsync() => RunDiagnosisAsync(null, null, null);
    internal void SnapHistory() => Show(HistoryView);
    internal void SnapTechnical(bool on) => _technicalSwitch.IsChecked = on;
    internal Task SnapAreaAsync(string area) => RunDiagnosisAsync([area], null, null);
    internal void SnapScale(double scale) { _scale = scale; _currentView?.Invoke(); }
    internal void SnapHome() => Show(HomeView);
    internal Task<ActionOutcome?> SnapExecuteAsync(string actionId, Dictionary<string, string> parameters) =>
        ExecuteActionAsync(_host!.Actions.Get(actionId)!, parameters);
    internal DiagnosticReport? Report => _report;
    internal object? CurrentPage => _content.Content;
    /// <summary>Último mensaje de la pantalla de error (para las pruebas de punta a punta).</summary>
    internal string? LastError { get; private set; }

    private void ErrorView(string message)
    {
        LastError = message;
        var page = new StackPanel { Spacing = S(14), Margin = new Thickness(0, S(40), 0, 0) };
        page.Children.Add(Text("Algo no salió bien", 26, FontWeight.Bold, StatusBrush(HealthStatus.Problem)));
        page.Children.Add(Text(message, 18));
        if (_host is not null) page.Children.Add(Btn("Volver al inicio", () => Show(HomeView)));
        SetPage(page);
    }
}

using System.Text;
using PCDoctor.Core;
using PCDoctor.Core.Actions;
using PCDoctor.Core.Diagnostics;
using PCDoctor.Core.Reports;
using PCDoctor.Windows;

Console.OutputEncoding = Encoding.UTF8;

const string Usage = """
    PC Doctor AI — línea de comandos (técnicos y laboratorio)

      pcdoctor-cli diagnosticar [--areas hardware,red,...] [--sintoma "texto"] [--informe archivo.html] [--tecnico] [--json archivo.json]
      pcdoctor-cli reparar <acción> [--param clave=valor ...] [--si]
      pcdoctor-cli historial
      pcdoctor-cli deshacer <número de operación>
      pcdoctor-cli biblioteca            (valida la biblioteca contra sondas y acciones)
      pcdoctor-cli fuentes               (lista las fuentes citadas)
      pcdoctor-cli acciones              (catálogo de reparaciones)
      pcdoctor-cli buscar-actualizacion  (consulta si hay una versión nueva publicada)

    Para publicar versiones (dueño del proyecto):
      pcdoctor-cli crear-claves <carpeta>      (una sola vez: clave privada para firmar y clave pública para el programa)
      pcdoctor-cli preparar-version --version X.Y.Z --notas "texto" --datos <pendrive publicado> [--rescate <parte de rescate>] --clave <privada.pem> --salida <carpeta>

    Códigos de salida: 0 = bien, 1 = error de uso, 2 = se encontraron problemas, 3 = la reparación no se pudo verificar.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "ayuda")
{
    Console.WriteLine(Usage);
    return 0;
}

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
bool Flag(string name) => args.Contains(name);

// Comandos que no usan la biblioteca: el instalador de actualizaciones corre desde una copia temporal, lejos del pendrive.
switch (args[0])
{
    case "crear-claves" when args.Length >= 2:
    {
        var dir = args[1];
        var priv = Path.Combine(dir, "pcdoctor-clave-privada.pem");
        if (File.Exists(priv)) { Console.Error.WriteLine($"Ya existe {priv}: no se reemplaza (perderla impide actualizar los pendrives vendidos)."); return 1; }
        Directory.CreateDirectory(dir);
        var (privatePem, publicPem) = PCDoctor.Core.Updates.UpdateSignature.CreateKeys();
        File.WriteAllText(priv, privatePem);
        File.WriteAllText(Path.Combine(dir, "pcdoctor-clave-publica.pem"), publicPem);
        Console.WriteLine($"Claves creadas en {Path.GetFullPath(dir)}.\nGuardá una copia de la clave PRIVADA en otro lugar seguro y no la compartas.\n\nClave pública:\n{publicPem}");
        return 0;
    }
    case "preparar-version":
    {
        if (Opt("--version") is not { } version || Opt("--datos") is not { } datos || Opt("--clave") is not { } clave || Opt("--salida") is not { } salida)
        {
            Console.WriteLine(Usage);
            return 1;
        }
        var privatePem = File.ReadAllText(clave);
        if (PCDoctor.Core.Updates.UpdateSignature.PublicFromPrivate(privatePem).Trim() != PCDoctor.Core.Updates.UpdateKeys.PublicKeyPem.Trim())
        {
            Console.Error.WriteLine("Esa clave privada no corresponde a la clave pública que llevan los pendrives: no la aceptarían.");
            return 1;
        }
        var parts = new List<(string, string, string, IReadOnlyList<string>)> { ("programa", "datos", datos, PCDoctor.Core.Updates.UpdateLayout.Datos) };
        if (Opt("--rescate") is { } rescate) parts.Add(("rescate", "rescate", rescate, PCDoctor.Core.Updates.UpdateLayout.Rescate));
        var manifest = PCDoctor.Core.Updates.UpdatePackager.Build(version, Opt("--notas") ?? "", parts, salida, privatePem);
        Console.WriteLine($"Versión {manifest.Version} lista en {Path.GetFullPath(salida)}:");
        foreach (var f in Directory.EnumerateFiles(salida)) Console.WriteLine($"  {Path.GetFileName(f)}  ({new FileInfo(f).Length / 1024:N0} KB)");
        Console.WriteLine("Subí todos esos archivos a una versión (release) nueva de GitHub.");
        return 0;
    }
    case "aplicar-actualizacion" when args.Length >= 2:
    {
        // Uso interno: lo lanza la interfaz desde una copia temporal y se cierra; acá se espera a que termine de cerrarse.
        if (Opt("--esperar") is { } pidText && int.TryParse(pidText, out var pid))
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.WaitForExit(60_000); } catch (ArgumentException) { }
        var targets = new Dictionary<string, string>();
        if (Opt("--datos") is { } d) targets["datos"] = d;
        if (Opt("--rescate") is { } r) targets["rescate"] = r;
        var current = Opt("--actual") ?? "0";
        PCDoctor.Core.Updates.UpdateApplier.Result result = new(false, "No se pudo instalar.", null);
        for (var attempt = 0; attempt < 5; attempt++) // el programa puede tardar unos segundos en soltar sus archivos
        {
            result = PCDoctor.Core.Updates.UpdateApplier.Apply(args[1], targets, PCDoctor.Core.Updates.UpdateKeys.PublicKeyPem, current);
            if (result.Ok || !result.Message.Contains("Se dejó todo como estaba")) break;
            Thread.Sleep(2000);
        }
        if (targets.TryGetValue("datos", out var datosRoot))
            File.WriteAllText(Path.Combine(datosRoot, "pcdoctor", "ultima-actualizacion.txt"), $"{DateTime.Now:dd/MM/yyyy HH:mm} {result.Message}");
        Console.WriteLine(result.Message);
        if (Opt("--abrir") is { } exe && File.Exists(exe))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
        return result.Ok ? 0 : 3;
    }
}

using var host = new DoctorHost();

switch (args[0])
{
    case "diagnosticar":
    {
        var areas = Opt("--areas")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var symptom = Opt("--sintoma");
        var matches = symptom is null ? null : host.Matcher.Match(symptom).Select(m => m.Problem).ToList();
        var probes = areas is null && matches is null ? host.Probes : host.Engine.ProbesFor(areas, matches);
        var progress = Flag("--json") ? null : new Progress<string>(m => Console.Error.WriteLine(m));
        var report = await host.Engine.RunAsync(probes, host.ProbeContext(), symptom, matches, progress);
        if (Opt("--json") is { } jsonPath)
        {
            await File.WriteAllTextAsync(jsonPath, ProbeExchange.Serialize(report), Encoding.UTF8);
        }
        PrintReport(report, Flag("--tecnico"));
        if (Opt("--informe") is { } html)
        {
            await File.WriteAllTextAsync(html, HtmlReport.Render(report, Flag("--tecnico")), Encoding.UTF8);
            Console.WriteLine($"\nInforme guardado en {Path.GetFullPath(html)}");
        }
        return report.Issues.Any(f => f.Status is HealthStatus.Problem or HealthStatus.NeedsPhysicalCheck) ? 2 : 0;
    }
    case "reparar" when args.Length >= 2:
    {
        var action = host.Actions.Get(args[1]);
        if (action is null) { Console.Error.WriteLine($"No existe la acción '{args[1]}'. Usá 'acciones' para ver el catálogo."); return 1; }
        var parameters = new Dictionary<string, string>();
        for (var i = 2; i < args.Length - 1; i++)
            if (args[i] == "--param" && args[i + 1].Split('=', 2) is [var k, var v]) parameters[k] = v;
        Console.WriteLine($"{action.Title}\n  Qué cambia: {action.WhatChanges}\n  Cómo se deshace: {action.HowToUndo}\n  Riesgo: {Labels.Risk(action.Risk)}");
        if (action.Risk != Risk.Green && !Flag("--si"))
        {
            Console.WriteLine("\nNo se hizo nada. Para confirmar, repetí el comando agregando --si");
            return 1;
        }
        var outcome = await host.Runner.RunAsync(action, host.ActionContext(parameters), host.Journal);
        Console.WriteLine($"\n{outcome.Status}: {outcome.Message}");
        if (outcome.OperationId is { } op) Console.WriteLine($"Operación n.º {op} registrada en el historial.");
        return outcome.Ok ? 0 : 3;
    }
    case "agente" when args.Length >= 5 && int.TryParse(args[4], out var parentPid):
        // Uso interno: la interfaz lo abre con permiso de administrador (un solo aviso por sesión).
        return await AgentServer.RunAsync(host, args[1], args[2], args[3], parentPid);
    case "evaluar-reglas":
    {
        // Mide el intérprete sin IA en los tres conjuntos (rápido, sin modelo).
        foreach (var (name, cases) in new[] { ("conocidas", EvalSets.Known), ("nuevas", EvalSets.Novel), ("control", EvalSets.Control), ("control limpio", EvalSets.Clean), ("control limpio 2", EvalSets.Clean2) })
        {
            var hits = 0;
            var hits5 = 0; // la pantalla muestra hasta 5 opciones
            var misses = new List<string>();
            foreach (var (text, expected) in cases)
            {
                var top5 = host.Matcher.Match(text, 5).Select(m => m.Problem.Id).ToList();
                var top = top5.Take(3).ToList();
                if (top5.Any(expected.Contains)) hits5++;
                if (top.Any(expected.Contains)) hits++;
                else misses.Add($"«{text}» → [{string.Join(", ", top)}]");
            }
            Console.WriteLine($"{name}: {hits}/{cases.Length} entre las 3 primeras · {hits5}/{cases.Length} entre las 5 que ve la persona");
            foreach (var m in misses) Console.WriteLine("   falló " + m);
        }
        return 0;
    }
    case "evaluar-ia":
        return await EvaluateAiAsync(host, Opt("--modelo"));
    case "historial":
    {
        var ops = host.Journal.Operations();
        if (ops.Count == 0) Console.WriteLine("Todavía no se hizo ninguna reparación en esta PC.");
        foreach (var op in ops)
            Console.WriteLine($"#{op.OperationId}  {op.Started:dd/MM/yyyy HH:mm}  {op.Title}  →  {op.ResultStatus ?? "sin terminar"}{(op.Undone ? "  (deshecho)" : "")}\n     {op.ResultMessage}");
        Console.WriteLine(host.Journal.VerifyChain() ? "\nIntegridad del historial: correcta." : "\nATENCIÓN: el historial fue modificado fuera de PC Doctor.");
        return 0;
    }
    case "deshacer" when args.Length >= 2 && long.TryParse(args[1], out var opId):
    {
        var op = host.Journal.Operations().FirstOrDefault(o => o.OperationId == opId);
        if (op is null) { Console.Error.WriteLine("No existe esa operación."); return 1; }
        var outcome = await host.Runner.UndoAsync(op, host.Actions, host.ActionContext(), host.Journal);
        Console.WriteLine(outcome.Message);
        return outcome.Ok ? 0 : 3;
    }
    case "biblioteca":
    {
        var errors = host.Knowledge.ValidateAgainst(host.Probes, host.Actions);
        Console.WriteLine($"Fichas válidas: {host.Knowledge.Problems.Count}");
        foreach (var e in errors) Console.WriteLine($"  ERROR {e}");
        return errors.Count == 0 ? 0 : 1;
    }
    case "fuentes":
        foreach (var url in host.Knowledge.Problems.SelectMany(p => p.Fuentes.Concat(p.Pasos.Select(s => s.Abrir ?? "")))
                     .Where(u => u.StartsWith("https://")).Distinct().Order())
            Console.WriteLine(url);
        return 0;
    case "acciones":
        foreach (var a in host.Actions.All)
            Console.WriteLine($"{a.Id,-22} {Labels.Risk(a.Risk),-18} madurez {a.Maturity}  {a.Title}");
        return 0;
    case "buscar-actualizacion":
    {
        var settings = PCDoctor.Core.Updates.UpdateSettings.Load(host.Paths.DataDir);
        if (!settings.Enabled) { Console.WriteLine("Este pendrive no tiene configurado dónde buscar versiones (data/actualizaciones.yaml)."); return 1; }
        // --como-version permite comprobar una versión publicada haciéndose pasar por una anterior; --descargar la baja y la verifica sin instalarla.
        var current = Opt("--como-version") ?? (typeof(DoctorHost).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0").Split('+')[0];
        var client = new PCDoctor.Core.Updates.UpdateClient(settings.Repositorio, PCDoctor.Core.Updates.UpdateKeys.PublicKeyPem);
        var update = await client.CheckAsync(current);
        Console.WriteLine(update is null
            ? $"PC Doctor {current} está al día."
            : $"Hay una versión nueva: {update.Manifest.Version} ({update.DownloadSize / 1048576.0:0.0} MB). Firma comprobada.\n{update.Manifest.Notes}");
        if (update is not null && Opt("--descargar") is { } folder)
        {
            await client.DownloadAsync(update, folder);
            Console.WriteLine($"Descargada y comprobada en {Path.GetFullPath(folder)} (no se instaló nada).");
            foreach (var p in update.Manifest.Packages)
                Console.WriteLine($"  {p.Name}: {Directory.EnumerateFiles(Path.Combine(folder, p.Name), "*", SearchOption.AllDirectories).Count()} archivos, SHA-256 correcto");
        }
        return 0;
    }
    default:
        Console.WriteLine(Usage);
        return 1;
}

// Evaluación del asistente: quejas reales con la respuesta correcta conocida, en dos grupos:
//   «conocidas»: parecidas a las frases de la biblioteca;  «nuevas»: dichas de otra manera (lo que no se vio nunca).
// Compara tres modos: solo reglas, solo IA y combinado (el que usa el producto).
static async Task<int> EvaluateAiAsync(DoctorHost host, string? modelId)
{
    var known = EvalSets.Known;
    var novel = EvalSets.Novel;

    var setup = LocalAi.Detect(host.Paths, forEvaluation: true);
    var manifest = PCDoctor.Core.Ai.ModelManifest.Load(host.Paths.DataDir);
    var model = modelId is null ? setup.Model : manifest.Modelos.FirstOrDefault(m => m.Id == modelId);
    if (setup.ServerExe is null || model is null) { Console.WriteLine($"No se puede evaluar: {setup.Reason}"); return 1; }
    var modelPath = Path.Combine(LocalAi.Root(host.Paths), "models", model.Archivo);

    async Task<(int hits, int questions, double avgSec, List<string> misses)> Run((string text, string[] expected)[] cases, PCDoctor.Core.Ai.IChatModel? m, bool hybrid)
    {
        int hits = 0, questions = 0;
        var misses = new List<string>();
        var times = new List<double>();
        foreach (var (text, expected) in cases)
        {
            var chat = new PCDoctor.Core.Ai.ChatSession(host.Knowledge, host.Matcher, host.Engine, host.ProbeContext, m, hybrid);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var turn = await chat.SendAsync(text);
            times.Add(sw.Elapsed.TotalSeconds);
            if (turn.Problems.Any(p => expected.Contains(p.Id))) hits++;
            else if (turn.Question is not null) { questions++; misses.Add($"«{text}» → preguntó: {turn.Question}"); }
            else misses.Add($"«{text}» → [{string.Join(", ", turn.Problems.Select(p => p.Id))}]{(turn.UsedAi ? "" : " (sin IA)")}");
        }
        return (hits, questions, times.Skip(1).DefaultIfEmpty(times[0]).Average(), misses);
    }

    using var llm = new LlamaServer(setup.ServerExe, modelPath, model);
    Console.WriteLine($"Modelo: {model.Nombre}\n");
    foreach (var (name, cases) in new[] { ("CONOCIDAS", known), ("NUEVAS (dichas de otra manera)", novel) })
    {
        Console.WriteLine($"— {name}: {cases.Length} consultas");
        var rules = await Run(cases, null, true);
        Console.WriteLine($"  Solo reglas: {rules.hits}/{cases.Length} aciertos · {rules.avgSec:0.0} s");
        var ai = await Run(cases, llm, false);
        Console.WriteLine($"  Solo IA:     {ai.hits}/{cases.Length} aciertos · {ai.questions} repreguntas · {ai.avgSec:0.0} s");
        var mix = await Run(cases, llm, true);
        Console.WriteLine($"  Combinado:   {mix.hits}/{cases.Length} aciertos · {mix.questions} repreguntas · {mix.avgSec:0.0} s");
        foreach (var miss in rules.misses) Console.WriteLine("    reglas falló: " + miss);
        foreach (var miss in mix.misses) Console.WriteLine("    combinado falló: " + miss);
    }
    Console.WriteLine($"\nMemoria máxima del motor: {llm.PeakMemoryMb} MB");
    return 0;
}
static void PrintReport(DiagnosticReport report, bool technical)
{
    Console.WriteLine($"\n=== Estado general: {Labels.Status(report.Overall)} ===");
    if (!report.Elevated) Console.WriteLine("(sin permiso de administrador: algunas comprobaciones no se pudieron hacer)");
    foreach (var f in report.Findings)
    {
        var tag = f.Kind == FindingKind.Suggestion ? "Sugerencia" : Labels.Status(f.Status);
        Console.WriteLine($"\n[{tag}] {f.Problem.Titulo.For(technical)}{(technical ? $"  ({f.Problem.Id})" : "")}");
        Console.WriteLine($"  {(technical ? f.TechnicalMessage : f.Message)}");
    }
    Console.WriteLine("\n--- Componentes ---");
    foreach (var p in report.Probes)
    {
        Console.WriteLine($"{Labels.Status(report.ComponentStatus(p)),-26} {p.Title}{(technical ? $"  [{p.Duration.TotalSeconds:0.0} s]" : "")}");
        if (p.Error is not null) Console.WriteLine($"    {p.Error}");
        if (technical)
        {
            foreach (var d in p.Details) Console.WriteLine($"    {d.Label}: {d.Value}");
            foreach (var u in p.Unverified) Console.WriteLine($"    No se pudo comprobar: {u}");
        }
    }
}

/// <summary>Conjuntos de evaluación del intérprete y del asistente.</summary>
internal static class EvalSets
{
    public static readonly (string text, string[] expected)[] Known =
    [
        ("no tengo sonido", ["AUD-001", "AUD-002"]),
        ("no me anda internet", ["NET-001", "NET-002", "NET-003", "NET-004", "NET-DNS-001"]),
        ("la compu está re lenta", ["PERF-SLOW-001", "PERF-CPU-001", "PERF-RAM-002", "PERF-STA-001"]),
        ("no me sale la arroba", ["BAS-KB-001"]),
        ("se me apaga sola cuando juego", ["WIN-PWR-010"]),
        ("me salió una pantalla azul", ["WIN-BSOD-001"]),
        ("la impresora no imprime", ["PRN-001", "PRN-002"]),
        ("no puedo abrir el word", ["APP-OFF-001"]),
        ("me llamaron de microsoft y me hicieron instalar un programa", ["SCAM-002", "SCAM-001"]),
        ("creo que tengo virus", ["SEC-VIR-001"]),
        ("no me ven en zoom", ["PER-CAM-001"]),
        ("no me escuchan en las videollamadas", ["PER-MIC-001"]),
        ("las letras se ven muy chiquitas", ["BAS-TXT-001"]),
        ("el disco está lleno", ["FIL-SPC-001"]),
        ("la batería dura poquito", ["LAP-BAT-001"]),
        ("windows no se actualiza", ["WIN-UPD-001", "WIN-UPD-020"]),
        ("el juego anda a pocos fps", ["GAM-001"]),
        ("la hora de la compu está mal", ["WIN-TIME-001"]),
        ("whatsapp web anda pero google no carga", ["NET-DNS-001"]),
        ("tarda mucho en prender", ["PERF-STA-001", "PERF-SLOW-001"]),
        ("me dice que windows no está activado", ["WIN-ACT-001"]),
        ("una página dice que llame a un número porque tengo virus", ["SCAM-001"]),
        ("el monitor de 144 hz se ve como de 60", ["DSP-001"]),
        ("me pide la clave de bitlocker", ["BOOT-BL-002"]),
        ("windows anda raro, me salen errores", ["WIN-SYS-001"]),
    ];
    public static readonly (string text, string[] expected)[] Novel =
    [
        ("cuando pongo un video de youtube no se oye nada", ["AUD-001", "AUD-002"]),
        ("el navegador dice que no hay conexión", ["NET-001", "NET-002", "NET-003", "NET-004", "NET-DNS-001", "NET-CAP-001"]),
        ("todo va en cámara lenta desde hace una semana", ["PERF-SLOW-001", "PERF-CPU-001", "PERF-RAM-002", "PERF-STA-001"]),
        ("no encuentro cómo poner el símbolo de los correos electrónicos", ["BAS-KB-001"]),
        ("la pc se reinicia de golpe en medio de un partido del fifa", ["WIN-PWR-010", "GAM-001"]),
        ("apareció un cartel celeste con una carita triste", ["WIN-BSOD-001"]),
        ("mandé a imprimir y no sale ninguna hoja", ["PRN-001", "PRN-002"]),
        ("el programa de las cartas, el word, no arranca", ["APP-OFF-001"]),
        ("un señor me dijo por teléfono que era técnico y se metió en mi computadora", ["SCAM-002", "SCAM-001"]),
        ("me aparecen avisos de que estoy infectado", ["SEC-VIR-001", "SCAM-001"]),
        ("en la reunión de meet mis nietos no me pueden ver", ["PER-CAM-001"]),
        ("los demás no oyen lo que digo cuando hablo por skype", ["PER-MIC-001"]),
        ("no llego a leer lo que dice la pantalla de lo pequeño que es", ["BAS-TXT-001"]),
        ("me dice que no queda lugar para guardar fotos", ["FIL-SPC-001"]),
        ("la notebook se descarga enseguida aunque la cargué toda la noche", ["LAP-BAT-001"]),
        ("hace meses que la computadora no se pone al día", ["WIN-UPD-001", "WIN-UPD-020", "WIN-LIFE-001", "WIN-LIFE-002"]),
        ("los juegos van a los saltos", ["GAM-001"]),
        ("el reloj de abajo marca cualquier hora", ["WIN-TIME-001"]),
        ("puedo mandar mensajes pero las páginas no cargan", ["NET-DNS-001"]),
        ("desde que la prendo hasta que puedo usarla pasan como diez minutos", ["PERF-STA-001", "PERF-SLOW-001"]),
        ("abajo a la derecha aparece una marca de agua que dice activar", ["WIN-ACT-001"]),
        ("el navegador se puso en pantalla completa con una alarma y un número para llamar", ["SCAM-001"]),
        ("compré un monitor gamer y siento que va igual que el viejo", ["DSP-001"]),
        ("al prender me pide una clave larguísima de números", ["BOOT-BL-002"]),
        ("salen ventanitas de error de windows todo el tiempo", ["WIN-SYS-001", "WIN-APP-001"]),
    ];

    /// <summary>
    /// Control: escritas DESPUÉS de ver los fallos y ANTES de ajustar el vocabulario. No se usan para ajustar nada:
    /// miden si una mejora generaliza o solo «memorizó» las otras listas.
    /// </summary>
    public static readonly (string text, string[] expected)[] Control =
    [
        ("no se escucha el audio de las películas", ["AUD-001", "AUD-002"]),
        ("mi nieta dice que no me oye cuando la llamo desde la compu", ["PER-MIC-001"]),
        ("en las videollamadas me ven todo negro", ["PER-CAM-001"]),
        ("la compu se tilda cuando abro muchas cosas", ["PERF-SLOW-001", "PERF-RAM-002", "PERF-CPU-001", "PERF-RAM-001"]),
        ("el disco c está en rojo", ["FIL-SPC-001"]),
        ("sale una ventana azul y se reinicia sola", ["WIN-BSOD-001", "WIN-PWR-010"]),
        ("la impresora dice que hay documentos en espera", ["PRN-001", "PRN-002"]),
        ("no puedo escribir la eñe", ["BAS-KB-001"]),
        ("no me conecta a la red del vecino", ["NET-001", "NET-002", "NET-003", "NET-004", "NET-WIFI-001"]),
        ("me salió un mensaje de que mi windows no es original", ["WIN-ACT-001"]),
        ("quiero agrandar todo lo que se ve", ["BAS-TXT-001"]),
        ("la notebook no aguanta nada sin el cargador", ["LAP-BAT-001"]),
        ("el antivirus está apagado", ["SEC-AV-001"]),
        ("hay un programa que maneja mi computadora a distancia", ["SCAM-002"]),
        ("la fecha de la computadora vuelve atrás cada vez que la apago", ["WIN-TIME-001"]),
        ("no encuentro el archivo que borré", ["FIL-DEL-001", "HOW-013", "ARC-012"]),
        ("el juego me dice que active el arranque seguro", ["GAM-003", "WIN-SB-001"]),
        ("cómo hago para ver la compu en el televisor", ["TV-001"]),
        ("no se conectan mis auriculares inalámbricos", ["BT-001"]),
        ("el pendrive no aparece en mi pc", ["USB-001"]),
    ];

    /// <summary>Control limpio: escritas después del último ajuste y medidas una sola vez, sin ajustar nada a partir de ellas.</summary>
    public static readonly (string text, string[] expected)[] Clean =
    [
        ("los parlantes no suenan aunque subo el volumen", ["AUD-001", "AUD-002"]),
        ("en zoom aparece mi foto en vez de mi cara", ["PER-CAM-001"]),
        ("la máquina tarda una eternidad en abrir cualquier cosa", ["PERF-SLOW-001", "PERF-CPU-001", "PERF-RAM-002", "PERF-STA-001", "APP-GEN-001"]),
        ("me quedé sin internet desde ayer", ["NET-001", "NET-002", "NET-003", "NET-004", "NET-DNS-001", "NET-WIFI-001"]),
        ("se apagó de repente mientras miraba una película", ["WIN-PWR-010"]),
        ("la computadora me avisa que el disco está casi lleno", ["FIL-SPC-001"]),
        ("no puedo imprimir el documento del banco", ["PRN-001", "PRN-002"]),
        ("cuando escribo un mail no me sale el arroba", ["BAS-KB-001"]),
        ("el excel no quiere abrir", ["APP-OFF-001"]),
        ("me escribieron diciendo que eran de soporte y les di acceso a la compu", ["SCAM-002", "SCAM-001"]),
        ("quiero pasar los videos del teléfono a la computadora", ["PHN-001"]),
        ("no me acuerdo la contraseña para entrar a windows", ["ACC-PWD-001"]),
        ("se me borró una carpeta con documentos del trabajo", ["FIL-DEL-001"]),
        ("el wifi se desconecta solo cada cinco minutos", ["NET-WIFI-001"]),
        ("todos los archivos tienen una extensión rara y hay una nota pidiendo dinero", ["SEC-RAN-001"]),
    ];

    /// <summary>Control limpio 2 (1/11/2026): escrito antes de la tanda de fichas 115+; no se usa para ajustar.</summary>
    public static readonly (string text, string[] expected)[] Clean2 =
    [
        ("la impresora está prendida pero windows dice que está desconectada", ["PRN-003", "PRN-001"]),
        ("no encuentro la barra de inicio", ["BAS-TASK-001"]),
        ("se me llenó la nube de onedrive", ["FIL-ODR-002"]),
        ("prendo la pc y queda todo negro", ["BOOT-BLK-001"]),
        ("el mouse inalámbrico dejó de funcionar", ["PER-MOU-001"]),
        ("quiero que mis nietos no usen la compu después de las diez", ["FAM-001", "FAMX-001", "FAMX-002"]),
        ("necesito escanear unos papeles", ["PRN-004"]),
        ("cuando hablo en las reuniones nadie me escucha", ["PER-MIC-001", "VID-003", "VID-016"]),
        ("windows me muestra todo en inglés", ["BAS-LANG-001"]),
        ("la computadora lee en voz alta todo lo que hago", ["BAS-ACC-001"]),
        ("me llegó un mail que parece del banco pidiendo mis datos", ["SEC-PHI-001"]),
        ("no me deja borrar un programa", ["APP-GEN-004"]),
        ("el monitor nuevo no da imagen", ["DSP-003"]),
        ("la notebook se me descarga muy rápido", ["LAP-BAT-001", "LAP-BAT-002"]),
        ("quiero dejar la computadora como recién comprada", ["MIG-005"]),
    ];}

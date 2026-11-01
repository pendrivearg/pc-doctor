using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;
using PCDoctor.Core.Ai;

namespace PCDoctor.Windows;

/// <summary>Busca el motor y los modelos (en el pendrive) y elige el modelo adecuado para la memoria libre de esta PC.</summary>
public static class LocalAi
{
    /// <summary>Carpeta con engines/ y models/. En el pendrive es la misma que la biblioteca; en desarrollo se puede indicar con PCDOCTOR_AI_ROOT.</summary>
    public static string Root(ContentPaths paths) =>
        Environment.GetEnvironmentVariable("PCDOCTOR_AI_ROOT") is { Length: > 0 } env && !Environment.IsPrivilegedProcess ? env : paths.ContentRoot;

    public sealed record Setup(string? ServerExe, ModelInfo? Model, string ModelPath, string Reason);

    public static Setup Detect(ContentPaths paths) => Detect(paths, forEvaluation: false);

    /// <param name="forEvaluation">La evaluación (pcdoctor-cli evaluar-ia) usa el motor aunque todavía no esté marcado como verificado.</param>
    public static Setup Detect(ContentPaths paths, bool forEvaluation)
    {
        var manifest = ModelManifest.Load(paths.DataDir);
        var root = Root(paths);
        var exe = Path.Combine(root, manifest.Motor.Carpeta, manifest.Motor.Ejecutable);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return new(null, null, "", "la IA local todavía no está disponible para este tipo de procesador");
        if (!File.Exists(exe)) return new(null, null, "", "el motor de IA no está en el pendrive");
        if (!manifest.Motor.Verificado && !forEvaluation) return new(null, null, "", "el asistente con IA todavía está en prueba");
        var installed = manifest.Modelos.Where(m => File.Exists(Path.Combine(root, "models", m.Archivo))).ToList();
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref status);
        var choice = ModelSelector.Choose(installed, status.ullAvailPhys / 1073741824.0, status.ullTotalPhys / 1073741824.0, Avx2.IsSupported);
        return choice.Model is null
            ? new(exe, null, "", choice.Reason)
            : new(exe, choice.Model, Path.Combine(root, "models", choice.Model.Archivo), choice.Reason);
    }
}

/// <summary>
/// llama.cpp (llama-server) como proceso aparte:
///   · escucha solo en 127.0.0.1, en un puerto libre, con una clave aleatoria;
///   · dentro de un Job Object con tope de memoria y que lo cierra si PC Doctor se cierra;
///   · prioridad «por debajo de lo normal» y pocos hilos para no trabar la PC;
///   · se apaga solo tras unos minutos sin uso (libera la memoria) y se vuelve a cargar cuando hace falta.
/// </summary>
public sealed class LlamaServer : IChatModel, IDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private readonly string _exe;
    private readonly string _modelPath;
    private readonly ModelInfo _model;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private Process? _process;
    private JobObject? _job;
    private int _port;
    private string _key = "";
    private Timer? _idle;

    public LlamaServer(string exe, string modelPath, ModelInfo model)
    {
        _exe = exe;
        _modelPath = modelPath;
        _model = model;
    }

    public string Name => _model.Nombre;
    public bool Running => _process is { HasExited: false };

    /// <summary>Memoria máxima usada por el motor (para la evaluación).</summary>
    public double PeakMemoryMb
    {
        get
        {
            if (_process is not { HasExited: false } p) return 0;
            p.Refresh();
            return Math.Round(p.PeakWorkingSet64 / 1048576.0);
        }
    }

    public async Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, JsonObject schema, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureStartedAsync(ct);
            var body = new JsonObject
            {
                ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
                ["temperature"] = 0.2,
                ["max_tokens"] = 400,
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = "respuesta", ["strict"] = true, ["schema"] = schema.DeepClone() },
                },
                // Respuestas directas: sin el modo «pensando» de Qwen (más lento y no hace falta para esto).
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/v1/chat/completions")
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _key);
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new InvalidDataException("respuesta vacía del motor");
            return json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? throw new InvalidDataException("respuesta sin contenido");
        }
        finally
        {
            _idle?.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (Running) return;
        Stop();
        _port = FreePort();
        _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var threads = Math.Clamp(Environment.ProcessorCount / 2 - 1, 2, 8);
        var psi = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_exe)!,
        };
        foreach (var a in new[]
                 {
                     "-m", _modelPath, "--host", "127.0.0.1", "--port", _port.ToString(), "--api-key", _key,
                     // Lotes de 512: con el vocabulario grande de Qwen, el lote por defecto (2048) reserva ~2 GB solo para resultados.
                     "-c", "4096", "-b", "512", "-ub", "512", "-t", threads.ToString(), "-ngl", "0", "--parallel", "1", "--no-webui",
                     "--chat-template-kwargs", "{\"enable_thinking\":false}",
                 })
            psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("no se pudo iniciar el motor de IA");
        _process = process;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
        // Tope de memoria: el modelo más su contexto, con margen. Si lo supera, Windows lo cierra (no a PC Doctor).
        var limit = (ulong)(new FileInfo(_modelPath).Length * 1.4) + 900UL * 1024 * 1024;
        _job = JobObject.Create(limit);
        _job.Assign(process);
        _idle ??= new Timer(_ => { if (_gate.Wait(0)) { try { Stop(); } finally { _gate.Release(); } } });

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"el motor de IA se cerró al cargar (código {process.ExitCode})");
            try
            {
                using var health = await _http.GetAsync($"http://127.0.0.1:{_port}/health", ct);
                if (health.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(300, ct);
        }
        Stop();
        throw new TimeoutException("el motor de IA tardó demasiado en cargar el modelo");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Apaga el motor y libera la memoria.</summary>
    public void Stop()
    {
        try { if (_process is { HasExited: false } p) p.Kill(true); } catch (InvalidOperationException) { }
        _process?.Dispose();
        _process = null;
        _job?.Dispose();
        _job = null;
    }

    public void Dispose()
    {
        _idle?.Dispose();
        Stop();
        _http.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Job Object de Windows: tope de memoria por proceso y cierre automático si PC Doctor termina.</summary>
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x100;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _handle;

    public static JobObject Create(ulong processMemoryLimit)
    {
        var h = CreateJobObject(IntPtr.Zero, null);
        if (h == IntPtr.Zero) throw new InvalidOperationException("no se pudo crear el límite de memoria");
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
            ProcessMemoryLimit = (UIntPtr)processMemoryLimit,
        };
        if (!SetInformationJobObject(h, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            CloseHandle(h);
            throw new InvalidOperationException("no se pudo aplicar el límite de memoria");
        }
        return new JobObject { _handle = h };
    }

    public void Assign(Process p)
    {
        if (!AssignProcessToJobObject(_handle, p.Handle)) throw new InvalidOperationException("no se pudo aplicar el límite de memoria al motor de IA");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }
}

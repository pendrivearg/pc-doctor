namespace PCDoctor.Core;

/// <summary>
/// Ubica el contenido (biblioteca, datos) y el estado escribible (diario, informes).
/// En el pendrive: X:\pcdoctor\knowledge, X:\pcdoctor\data, X:\pcdoctor\state.
/// En desarrollo: la raíz del repositorio.
/// </summary>
public sealed class ContentPaths
{
    public required string ContentRoot { get; init; }
    public required string StateRoot { get; init; }

    public string KnowledgeDir => Path.Combine(ContentRoot, "knowledge");
    public string ProblemsDir => Path.Combine(KnowledgeDir, "problems");
    public string DataDir => Path.Combine(ContentRoot, "data");

    public string MachineDir(string machineId)
    {
        var dir = Path.Combine(StateRoot, "machines", machineId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static ContentPaths Discover(string? baseDir = null)
    {
        // PCDOCTOR_ROOT permite a desarrollo y laboratorio apuntar a una biblioteca concreta.
        // Con permiso de administrador se ignora: una variable de usuario no puede redirigir a un proceso elevado.
        baseDir ??= !Environment.IsPrivilegedProcess && Environment.GetEnvironmentVariable("PCDOCTOR_ROOT") is { Length: > 0 } env
            ? env
            : AppContext.BaseDirectory;
        var root = FindContentRoot(baseDir)
            ?? throw new DirectoryNotFoundException(
                $"No encontré la biblioteca de PC Doctor (carpeta 'knowledge/problems') cerca de {baseDir}.");
        return new ContentPaths { ContentRoot = root, StateRoot = ChooseStateRoot(root) };
    }

    private static string? FindContentRoot(string baseDir)
    {
        var candidates = new List<string> { Path.Combine(baseDir, "pcdoctor") };
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
        {
            candidates.Add(dir.FullName);
            candidates.Add(Path.Combine(dir.FullName, "pcdoctor"));
        }
        return candidates.FirstOrDefault(c => Directory.Exists(Path.Combine(c, "knowledge", "problems")));
    }

    private static string ChooseStateRoot(string contentRoot)
    {
        var preferred = Path.Combine(contentRoot, "state");
        if (IsWritable(preferred)) return preferred;
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCDoctor", "state");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".w{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

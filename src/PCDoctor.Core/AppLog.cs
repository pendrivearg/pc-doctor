using System.Text;

namespace PCDoctor.Core;

/// <summary>
/// Registro técnico de errores (no de datos personales): state\registro\pcdoctor-AAAAMMDD.log en el pendrive.
/// Sirve para que un técnico entienda una falla. Nunca bloquea ni hace fallar al programa.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static void Configure(string stateRoot)
    {
        _dir = Path.Combine(stateRoot, "registro");
    }

    public static void Error(string where, Exception e) => Write("ERROR", $"{where}: {e}");

    public static void Info(string message) => Write("INFO", message);

    private static void Write(string level, string message)
    {
        if (_dir is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(_dir);
                var file = Path.Combine(_dir, $"pcdoctor-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // El registro es auxiliar: si el pendrive está lleno o protegido, se ignora.
        }
    }
}

using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// Unified runtime log (%AppData%\JuniGrid\juni-grid.log).
/// Persists warnings and errors from all business layers to a file, making it easy to trace "which step failed, and why".
/// Thread-safe; rolls over to .old and starts a fresh file past ~1MB to avoid unbounded growth.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JuniGrid");
    private static readonly string FilePath = Path.Combine(Dir, "juni-grid.log");
    private const long MaxBytes = 1024 * 1024;   // rolls over at 1MB

    /// <summary>Logs a warning (WRN). Do not call it indiscriminately; callers should only call on actual failures/anomalies.</summary>
    public static void Warn(string source, string message)
        => Write("WRN", source, message);

    /// <summary>Logs an error (ERR).</summary>
    public static void Error(string source, string message)
        => Write("ERR", source, message);

    /// <summary>Logs an exception (ERR + stack trace). Best suited for catches where ex is available.</summary>
    public static void Error(string source, Exception ex)
        => Write("ERR", source, ex.ToString());

    private static void Write(string level, string source, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                RollIfNeeded();
                File.AppendAllText(FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{source}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app, even when it fails itself */ }
    }

    private static void RollIfNeeded()
    {
        if (!File.Exists(FilePath)) return;
        if (new FileInfo(FilePath).Length < MaxBytes) return;
        File.Copy(FilePath, FilePath + ".old", overwrite: true);
        File.Delete(FilePath);
    }
}
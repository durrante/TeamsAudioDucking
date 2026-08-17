using System.IO;
using System.Text;

namespace TeamsAudioDucking.Core;

/// <summary>
/// Minimal thread-safe file logger with size-based rotation. Never throws.
/// </summary>
public static class Logger
{
    private static readonly object Sync = new();
    private static string _logFile = "";
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Init(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _logFile = Path.Combine(directory, "TeamsAudioDucking.log");
            Info("---- session started ----");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        if (_logFile.Length == 0) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Sync)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_logFile, line, Encoding.UTF8);
            }
            catch
            {
                // Swallow: a failed log write must not affect audio handling.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        var fi = new FileInfo(_logFile);
        if (fi.Exists && fi.Length > MaxBytes)
        {
            var old = _logFile + ".old";
            File.Delete(old);
            File.Move(_logFile, old);
        }
    }
}

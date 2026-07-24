using System.IO;

namespace ScrcpyDeck.Services;

/// <summary>Minimal rolling file logger (no external dependencies) in %AppData%\PhoneLinkPro\logs.</summary>
public static class Log
{
    private static readonly object _sync = new();

    private static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneLinkPro", "logs");

    /// <summary>
    /// Test-only redirection of the log directory. Null in production, so the application
    /// always logs to %AppData%\PhoneLinkPro\logs exactly as before; the regression suite
    /// sets it so tests never write into the real user profile.
    /// </summary>
    internal static string? DirOverride;

    private static string Dir => DirOverride ?? DefaultDir;

    private static bool _cleaned;

    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);

    public static void Error(string msg, Exception? ex = null)
        => Write("ERR", ex == null ? msg : $"{msg}\n{ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Dir);
                if (!_cleaned) { CleanupOldLogs(); _cleaned = true; }
                var file = Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }

    /// <summary>Keep the last 7 daily log files.</summary>
    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.GetFiles(Dir, "app-*.log"))
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
        }
        catch { }
    }
}

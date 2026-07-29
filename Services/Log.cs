using System.IO;
using System.Runtime.InteropServices;

namespace ScrcpyDeck.Services;

/// <summary>
/// Minimal rolling file logger (no external dependencies) in %AppData%\PhoneLinkPro\logs.
/// <para>
/// Bounded two ways (LOG-02): files older than 7 days are removed, and a file that outgrows
/// <see cref="MaxLogBytes"/> is rolled aside once (".overflow.log", overwritten), so a hot
/// failure loop can never fill the disk. Each fresh file begins with an app/version header.
/// Writes are best-effort and never crash the app; a transient sharing violation (the brief
/// window where a second instance exists before the single-instance handoff) is retried
/// once and then dropped.
/// </para>
/// </summary>
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

    /// <summary>Where logs are currently written (used by the support-bundle export).</summary>
    internal static string Dir => DirOverride ?? DefaultDir;

    /// <summary>Size cap per daily file; the file is rolled aside once when exceeded.</summary>
    internal static long MaxLogBytes { get; set; } = 5 * 1024 * 1024;

    private static bool _cleaned;

    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);

    public static void Error(string msg, Exception? ex = null)
        => Write("ERR", ex == null ? msg : $"{msg}\n{ex}");

    private static void Write(string level, string msg)
    {
        // PRIV-01: the single authoritative pre-persistence boundary. Every Info/Warn/Error —
        // including future call sites and any exception text appended by <see cref="Error"/> —
        // funnels through here, so no raw device serial, user-profile path, remote transfer
        // path, wireless endpoint or pairing code can reach the file even when a caller forgets
        // to redact. <see cref="SupportBundle"/> sanitizes again on export (defence in depth).
        // Fails CLOSED: if redaction itself fails the entry is withheld rather than written raw.
        string safe;
        try { safe = LogSanitizer.Sanitize(msg); }
        catch { safe = "(entry withheld: redaction failed)"; }

        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Dir);
                if (!_cleaned) { CleanupOldLogs(); _cleaned = true; }

                var file = Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log");
                EnforceSizeCap(file);

                var text = (File.Exists(file) ? "" : HeaderLine())
                           + $"{DateTime.Now:HH:mm:ss.fff} [{level}] {safe}{Environment.NewLine}";
                Append(file, text);
            }
        }
        catch { /* logging must never crash the app */ }
    }

    /// <summary>First line of every fresh log file: version, PID, architecture, OS.</summary>
    private static string HeaderLine()
        => $"# PhoneLink Pro v{AppVersionInfo.ProductVersion} " +
           $"pid={Environment.ProcessId} arch={RuntimeInformation.ProcessArchitecture} " +
           $"os={Environment.OSVersion.Version} net={RuntimeInformation.FrameworkDescription}" +
           Environment.NewLine;

    /// <summary>Appends with one retry, tolerating a concurrent writer's transient lock.</summary>
    private static void Append(string file, string text)
    {
        try
        {
            File.AppendAllText(file, text);
        }
        catch (IOException)
        {
            Thread.Sleep(15);
            File.AppendAllText(file, text);   // second failure propagates to the outer catch
        }
    }

    /// <summary>Rolls an oversized file aside so today's log restarts small.</summary>
    private static void EnforceSizeCap(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length < MaxLogBytes) return;

            var overflow = Path.ChangeExtension(file, ".overflow.log");
            File.Move(file, overflow, overwrite: true);
        }
        catch { /* worst case the file keeps growing until the next successful roll */ }
    }

    /// <summary>Keep the last 7 days of daily and overflow files.</summary>
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

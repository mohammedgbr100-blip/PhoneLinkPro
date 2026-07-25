using System.Runtime.InteropServices;

namespace ScrcpyDeck.Services;

/// <summary>What the application should do with a global exception.</summary>
public enum ExceptionDisposition
{
    /// <summary>Classified recoverable, first occurrence: show ONE safe Arabic dialog, continue.</summary>
    ContinueWithDialog,
    /// <summary>Duplicate of an already-reported failure: log only, no dialog.</summary>
    ContinueSilently,
    /// <summary>Unknown or repeated failure: announce once, then shut down in an orderly way.</summary>
    FatalShutdown,
}

/// <summary>The policy's verdict for one exception.</summary>
public sealed record ExceptionDecision(
    ExceptionDisposition Disposition,
    string CorrelationId,
    string UserMessage,
    bool ShowDialog);

/// <summary>
/// Bounded global exception policy (LOG-01). The old behavior — mark every UI exception
/// handled and continue forever with a MessageBox each time — is gone. Rules:
///
/// <list type="bullet">
///   <item><description><b>Classified recoverable</b> exception types (I/O, access, COM,
///   timeouts, cancellations): one Arabic dialog on first occurrence per signature, silent
///   deduplicated logging afterwards, escalation to shutdown when the same failure repeats
///   beyond <see cref="RepeatShutdownThreshold"/> inside <see cref="Window"/>.</description></item>
///   <item><description><b>Unknown or fatal-category</b> exceptions: one controlled close
///   message, then orderly shutdown via <see cref="AppLifecycle"/>. Never an abrupt
///   process kill, never a dialog loop.</description></item>
///   <item><description><b>Background task exceptions</b> never terminate the app: logged
///   with the same dedup rules, always observed.</description></item>
/// </list>
///
/// Every event is logged with category, app version, correlation id, the current operation
/// (<see cref="AppDiagnostics.CurrentOperation"/>) and a sanitized stack trace — logs only,
/// never a user dialog.
/// </summary>
public static class GlobalExceptionPolicy
{
    /// <summary>Same-signature occurrences inside <see cref="Window"/> that force shutdown.</summary>
    internal static int RepeatShutdownThreshold { get; set; } = 3;

    /// <summary>Total UI exceptions inside <see cref="Window"/> that force shutdown.</summary>
    internal static int TotalShutdownThreshold { get; set; } = 6;

    /// <summary>Sliding window for the thresholds above.</summary>
    internal static TimeSpan Window { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Clock seam so threshold expiry is testable without waiting.</summary>
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private static readonly object _gate = new();
    private static readonly Dictionary<string, List<DateTimeOffset>> _bySignature = new();
    private static readonly List<DateTimeOffset> _all = new();
    private static bool _fatalAnnounced;

    private const string RecoverableMessage =
        "حدث خطأ غير متوقع وتمت معالجته. إذا تكررت المشكلة يُرجى إعادة تشغيل البرنامج.";

    private const string FatalMessage =
        "واجه البرنامج خللاً متكرراً ولا يمكنه المتابعة بأمان.\n" +
        "سيتم إغلاق البرنامج الآن للحفاظ على سلامة البيانات — يمكنك إعادة فتحه مباشرة.\n" +
        "إذا استمرت المشكلة، صدّر ملف التشخيص من نافذة «حول» وأرسله للدعم.";

    /// <summary>Exception types that are never safe to continue past.</summary>
    internal static bool IsFatalCategory(Exception ex) => ex
        is OutOfMemoryException
        or StackOverflowException
        or AccessViolationException
        or BadImageFormatException
        or InvalidProgramException
        or System.Windows.Markup.XamlParseException;   // broken install, not a user action

    /// <summary>
    /// Exception types classified as recoverable UI failures: transient environment issues
    /// where application state remains valid after the failed user action is abandoned.
    /// </summary>
    internal static bool IsRecoverableCategory(Exception ex) => ex
        is System.IO.IOException
        or UnauthorizedAccessException
        or ExternalException          // COMException/Win32Exception (clipboard, dialogs, shell)
        or TimeoutException
        or OperationCanceledException;

    /// <summary>Stable dedup key: type + failing member, never the (variable) message text.</summary>
    internal static string Signature(Exception ex)
        => ex.GetType().FullName + "|" + (ex.TargetSite?.Name ?? "?");

    /// <summary>Applies the policy to a UI-thread (Dispatcher) exception.</summary>
    public static ExceptionDecision RegisterUiException(Exception ex)
    {
        var correlationId = NewCorrelationId();
        var now = Clock();
        var signature = Signature(ex);

        ExceptionDecision decision;
        lock (_gate)
        {
            Purge(now);

            _all.Add(now);
            if (!_bySignature.TryGetValue(signature, out var hits))
                _bySignature[signature] = hits = new List<DateTimeOffset>();
            hits.Add(now);

            if (AppLifecycle.IsShuttingDown)
            {
                // Teardown noise: log only, never a dialog, never a second shutdown.
                decision = new ExceptionDecision(
                    ExceptionDisposition.ContinueSilently, correlationId, "", false);
            }
            else if (IsFatalCategory(ex)
                     || !IsRecoverableCategory(ex)
                     || hits.Count >= RepeatShutdownThreshold
                     || _all.Count >= TotalShutdownThreshold)
            {
                // Unknown, fatal-category, or repeated beyond the bound: controlled close.
                // The dialog is shown exactly once, however many more exceptions follow.
                var announce = !_fatalAnnounced;
                _fatalAnnounced = true;
                decision = new ExceptionDecision(
                    ExceptionDisposition.FatalShutdown, correlationId, FatalMessage, announce);
            }
            else
            {
                decision = new ExceptionDecision(
                    hits.Count == 1 ? ExceptionDisposition.ContinueWithDialog
                                    : ExceptionDisposition.ContinueSilently,
                    correlationId, RecoverableMessage, hits.Count == 1);
            }
        }

        LogException("ui", ex, decision);
        return decision;
    }

    /// <summary>
    /// Policy for unobserved background-task exceptions: never fatal (the task already
    /// died; the app is intact), logged with the same signature dedup so a hot loop cannot
    /// flood the log.
    /// </summary>
    public static ExceptionDecision RegisterBackgroundException(Exception ex)
    {
        var correlationId = NewCorrelationId();
        var now = Clock();
        var signature = Signature(ex);

        bool duplicate;
        lock (_gate)
        {
            Purge(now);
            if (!_bySignature.TryGetValue(signature, out var hits))
                _bySignature[signature] = hits = new List<DateTimeOffset>();
            duplicate = hits.Count > 0;
            hits.Add(now);
        }

        var decision = new ExceptionDecision(
            ExceptionDisposition.ContinueSilently, correlationId, "", false);
        if (!duplicate) LogException("task", ex, decision);
        return decision;
    }

    /// <summary>Test hook: pristine counters and production thresholds.</summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _bySignature.Clear();
            _all.Clear();
            _fatalAnnounced = false;
        }
        RepeatShutdownThreshold = 3;
        TotalShutdownThreshold = 6;
        Window = TimeSpan.FromMinutes(2);
        Clock = () => DateTimeOffset.UtcNow;
    }

    private static void Purge(DateTimeOffset now)
    {
        var cutoff = now - Window;
        _all.RemoveAll(t => t < cutoff);
        foreach (var hits in _bySignature.Values) hits.RemoveAll(t => t < cutoff);
    }

    private static string NewCorrelationId() => Guid.NewGuid().ToString("N")[..8];

    private static void LogException(string origin, Exception ex, ExceptionDecision decision)
    {
        var line = $"GlobalException[{decision.CorrelationId}] origin={origin} " +
                   $"category={ex.GetType().Name} app=v{AppVersionInfo.ProductVersion} " +
                   $"operation={AppDiagnostics.CurrentOperation ?? "-"} " +
                   $"disposition={decision.Disposition}\n" +
                   LogSanitizer.Sanitize(ex.ToString());
        Log.Error(line);

        AppDiagnostics.RecordFailure(
            ex.GetType().Name, decision.CorrelationId,
            $"{origin}:{decision.Disposition}");
    }
}

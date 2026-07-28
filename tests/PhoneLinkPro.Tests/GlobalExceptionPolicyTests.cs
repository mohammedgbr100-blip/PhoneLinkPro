using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// LOG-01 (Batch 1F) — the bounded global exception policy: classified recoverable errors
/// continue with ONE dialog, duplicates go silent, unknown/fatal/repeated errors end in one
/// controlled orderly shutdown, and nothing ever loops MessageBoxes or calls FailFast.
///
/// In the global collection: the policy, AppLifecycle and the log directory are process-wide.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class GlobalExceptionPolicyTests : IDisposable
{
    private readonly string _logDir;
    private readonly string? _previousLogOverride;
    private DateTimeOffset _now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    public GlobalExceptionPolicyTests()
    {
        GlobalExceptionPolicy.ResetForTests();
        AppLifecycle.ResetForTests();
        AppDiagnostics.ResetForTests();
        GlobalExceptionPolicy.Clock = () => _now;

        _logDir = Path.Combine(Path.GetTempPath(), "plp-tests-policy-" + Guid.NewGuid().ToString("N"));
        _previousLogOverride = Log.DirOverride;
        Log.DirOverride = _logDir;
    }

    public void Dispose()
    {
        GlobalExceptionPolicy.ResetForTests();
        AppLifecycle.ResetForTests();
        AppDiagnostics.ResetForTests();
        Log.DirOverride = _previousLogOverride;
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private string ReadLogs()
        => !Directory.Exists(_logDir)
            ? ""
            : string.Concat(Directory.GetFiles(_logDir, "*.log").Select(File.ReadAllText));

    /// <summary>A classified-recoverable exception with a realistic stack trace.</summary>
    private static Exception Recoverable()
    {
        try { throw new IOException("disk fell over"); }
        catch (IOException ex) { return ex; }
    }

    private static Exception Unknown()
    {
        try { throw new InvalidOperationException("state machine confused"); }
        catch (InvalidOperationException ex) { return ex; }
    }

    // ---------------------------------------------------------------- recoverable path

    [Fact] // first classified-recoverable failure: one dialog, keep running
    public void FirstRecoverable_ContinuesWithOneDialog()
    {
        var decision = GlobalExceptionPolicy.RegisterUiException(Recoverable());

        Assert.Equal(ExceptionDisposition.ContinueWithDialog, decision.Disposition);
        Assert.True(decision.ShowDialog);
        Assert.NotEmpty(decision.CorrelationId);
        Assert.DoesNotContain("Exception", decision.UserMessage);   // safe Arabic text only
    }

    [Fact] // the same failure again: logged, NO second dialog (dedup)
    public void RepeatedRecoverable_IsSilentlyDeduplicated()
    {
        GlobalExceptionPolicy.RegisterUiException(Recoverable());
        var second = GlobalExceptionPolicy.RegisterUiException(Recoverable());

        Assert.Equal(ExceptionDisposition.ContinueSilently, second.Disposition);
        Assert.False(second.ShowDialog);
    }

    [Fact] // the same failure keeps repeating: bounded — orderly shutdown, not forever
    public void RepeatedRecoverable_EscalatesToShutdownAtThreshold()
    {
        GlobalExceptionPolicy.RegisterUiException(Recoverable());
        GlobalExceptionPolicy.RegisterUiException(Recoverable());
        var third = GlobalExceptionPolicy.RegisterUiException(Recoverable());

        Assert.Equal(ExceptionDisposition.FatalShutdown, third.Disposition);
        Assert.True(third.ShowDialog);   // announced exactly once...

        var fourth = GlobalExceptionPolicy.RegisterUiException(Recoverable());
        Assert.Equal(ExceptionDisposition.FatalShutdown, fourth.Disposition);
        Assert.False(fourth.ShowDialog); // ...never a dialog loop
    }

    [Fact] // repetition outside the sliding window is NOT an escalation
    public void WindowExpiry_ResetsTheRepeatCounter()
    {
        GlobalExceptionPolicy.RegisterUiException(Recoverable());
        GlobalExceptionPolicy.RegisterUiException(Recoverable());

        _now += GlobalExceptionPolicy.Window + TimeSpan.FromSeconds(1);
        var later = GlobalExceptionPolicy.RegisterUiException(Recoverable());

        Assert.Equal(ExceptionDisposition.ContinueWithDialog, later.Disposition);
    }

    // ---------------------------------------------------------------- fatal path

    [Fact] // an unclassified exception type is not safe to continue past
    public void UnknownException_TriggersControlledShutdown()
    {
        var decision = GlobalExceptionPolicy.RegisterUiException(Unknown());

        Assert.Equal(ExceptionDisposition.FatalShutdown, decision.Disposition);
        Assert.True(decision.ShowDialog);
        Assert.Contains("إغلاق", decision.UserMessage);              // controlled close message
        Assert.DoesNotContain("FailFast", decision.UserMessage);
    }

    [Fact] // too many distinct UI failures in the window: the app is not healthy — close
    public void TotalThreshold_ForcesShutdownEvenAcrossDifferentSignatures()
    {
        // Distinct recoverable signatures (different target sites via local functions).
        static Exception Make(int i)
        {
            try
            {
                if (i == 0) throw new UnauthorizedAccessException("a");
                throw new TimeoutException("b" + i);
            }
            catch (Exception ex) { return ex; }
        }

        GlobalExceptionPolicy.TotalShutdownThreshold = 3;
        GlobalExceptionPolicy.RegisterUiException(Make(0));
        GlobalExceptionPolicy.RegisterUiException(Make(1));
        var third = GlobalExceptionPolicy.RegisterUiException(Make(2));

        Assert.Equal(ExceptionDisposition.FatalShutdown, third.Disposition);
    }

    [Fact] // once shutdown began, teardown noise is logged silently — no dialogs over a closing app
    public void ExceptionsDuringShutdown_AreSilent()
    {
        AppLifecycle.Shutdown();
        var decision = GlobalExceptionPolicy.RegisterUiException(Unknown());

        Assert.Equal(ExceptionDisposition.ContinueSilently, decision.Disposition);
        Assert.False(decision.ShowDialog);
    }

    // ---------------------------------------------------------------- background tasks

    [Fact] // a dead background task never takes the app down, and hot loops cannot flood logs
    public void BackgroundExceptions_NeverEscalateAndDeduplicate()
    {
        var first = GlobalExceptionPolicy.RegisterBackgroundException(Recoverable());
        var second = GlobalExceptionPolicy.RegisterBackgroundException(Recoverable());

        Assert.Equal(ExceptionDisposition.ContinueSilently, first.Disposition);
        Assert.Equal(ExceptionDisposition.ContinueSilently, second.Disposition);
        Assert.False(first.ShowDialog);

        var occurrences = ReadLogs().Split("origin=task").Length - 1;
        Assert.Equal(1, occurrences);   // second identical failure logged zero times
    }

    // ---------------------------------------------------------------- logging content

    [Fact] // logs carry category, version, correlation id, operation — with sanitized paths
    public void Logs_AreStructuredAndSanitized()
    {
        AppDiagnostics.CurrentOperation = "page:Files";
        Exception ex;
        try
        {
            throw new IOException(
                @"cannot write C:\Users\testuser\Documents\secret.txt for device R58M12ABCDE");
        }
        catch (IOException e) { ex = e; }

        var decision = GlobalExceptionPolicy.RegisterUiException(ex);
        var logs = ReadLogs();

        Assert.Contains("category=IOException", logs);
        Assert.Contains($"app=v{AppVersionInfo.ProductVersion}", logs);
        Assert.Contains(decision.CorrelationId, logs);
        Assert.Contains("operation=page:Files", logs);
        Assert.DoesNotContain(@"C:\Users\testuser", logs);     // profile path masked
        Assert.DoesNotContain("R58M12ABCDE", logs);         // serial masked
        Assert.Single(AppDiagnostics.RecentFailures, f => f.Category == "IOException");
    }
}

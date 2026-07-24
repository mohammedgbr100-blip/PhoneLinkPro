using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScrcpyDeck.Services;

/// <summary>How a process invocation ended. Replaces the old ambiguous -1 exit sentinel.</summary>
public enum ProcessOutcome
{
    /// <summary>The process started and exited on its own; <see cref="ProcessResult.ExitCode"/> is its real code.</summary>
    Exited,
    /// <summary>The process exceeded its timeout and its process tree was killed.</summary>
    TimedOut,
    /// <summary>The caller's cancellation token fired; the process tree was killed.</summary>
    Cancelled,
    /// <summary>The process could not be started (missing executable, access denied, ...).</summary>
    StartFailed,
    /// <summary>Input failed validation, so no process was ever launched.</summary>
    InvalidInput,
}

/// <summary>
/// The outcome of running an external tool.
/// <para>
/// <see cref="ExitCode"/> is non-null only when <see cref="Outcome"/> is
/// <see cref="ProcessOutcome.Exited"/>, so a genuine -1 exit can never be confused with a
/// start failure. Callers must branch on <see cref="Outcome"/>, never on message text.
/// </para>
/// </summary>
public record ProcessResult(
    ProcessOutcome Outcome,
    int? ExitCode,
    string StdOut,
    string StdErr,
    bool OutputTruncated = false,
    string? FailureDetail = null)
{
    /// <summary>True only when the process ran to completion with exit code 0.</summary>
    public bool Success => Outcome == ProcessOutcome.Exited && ExitCode == 0;

    /// <summary>A user-facing Arabic description of the outcome. Never contains stack traces.</summary>
    public string Message => Outcome switch
    {
        ProcessOutcome.Exited => string.IsNullOrWhiteSpace(StdOut) ? StdErr : StdOut,
        ProcessOutcome.TimedOut => "انتهت مهلة تنفيذ الأمر.",
        ProcessOutcome.Cancelled => "تم إلغاء العملية.",
        ProcessOutcome.StartFailed => "تعذّر تشغيل أداة الاتصال بالجهاز.",
        // The specific validation reason is carried in StdErr so the user is told exactly
        // what was wrong (invalid port, invalid address, ...) rather than a generic message.
        ProcessOutcome.InvalidInput => string.IsNullOrWhiteSpace(StdErr) ? "مدخل غير صالح." : StdErr,
        _ => "",
    };

    /// <summary>
    /// Text to show the user. For a completed process this is stdout (falling back to
    /// stderr), preserving the previous behaviour; otherwise a safe Arabic message.
    /// Kept as an alias of <see cref="Message"/> so existing view code keeps working.
    /// </summary>
    public string Output => Message;

    /// <summary>Builds an <see cref="ProcessOutcome.InvalidInput"/> result carrying an Arabic reason.</summary>
    public static ProcessResult Invalid(string arabicReason)
        => new(ProcessOutcome.InvalidInput, null, "", arabicReason, false, arabicReason);
}

/// <summary>
/// An external tool invocation. Arguments are kept as separate elements and handed to
/// <see cref="ProcessStartInfo.ArgumentList"/>, so no command line is ever assembled by
/// this application and no value can inject additional arguments.
/// </summary>
public sealed record ProcessCommand(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    int TimeoutMs = 20000,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? RedactedDescription = null)
{
    /// <summary>
    /// What may safely be written to the log. Falls back to the real argument list, so
    /// callers handling secrets (for example pairing codes) must set this explicitly.
    /// </summary>
    public string LogText => RedactedDescription
        ?? $"{Path.GetFileName(ExecutablePath)} {string.Join(' ', Arguments)}";
}

/// <summary>Runs external console tools (adb) hidden and captures their output.</summary>
public static class ProcessRunner
{
    /// <summary>
    /// Maximum characters captured per stream. Output beyond this is dropped and
    /// <see cref="ProcessResult.OutputTruncated"/> is set. Policy: the FIRST
    /// <see cref="MaxCapturedChars"/> characters are kept, because diagnostics from adb
    /// appear at the start of a stream; the tail is discarded deterministically.
    /// </summary>
    public const int MaxCapturedChars = 1_000_000;

    /// <summary>
    /// How long to wait for the stdout/stderr EOF callbacks after the process has exited.
    /// A detached grandchild can inherit the pipes and hold them open forever, so the wait
    /// is bounded and can never hang the UI.
    /// </summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>Run a tool to completion and capture stdout/stderr.</summary>
    public static Task<ProcessResult> RunAsync(
        string exePath, IReadOnlyList<string> arguments, int timeoutMs = 20000,
        CancellationToken ct = default)
        => RunAsync(new ProcessCommand(exePath, arguments, timeoutMs), ct);

    /// <summary>Run a tool to completion and capture stdout/stderr.</summary>
    public static async Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(command.ExecutablePath) ?? Environment.CurrentDirectory,
        };
        foreach (var a in command.Arguments) psi.ArgumentList.Add(a);
        if (command.Environment != null)
            foreach (var kv in command.Environment) psi.Environment[kv.Key] = kv.Value;

        using var proc = new Process { StartInfo = psi };

        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();
        var truncated = false;

        // EOF completion: DataReceived fires with Data == null exactly once per stream when
        // the pipe reaches end-of-file, which is the only reliable "all output delivered"
        // signal for the asynchronous read API.
        var outEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) outEof.TrySetResult();
            else Append(sbOut, e.Data, ref truncated);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) errEof.TrySetResult();
            else Append(sbErr, e.Data, ref truncated);
        };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            Log.Error($"Run failed to start: {command.LogText}", ex);
            return new ProcessResult(ProcessOutcome.StartFailed, null, "", "",
                FailureDetail: $"{ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(command.TimeoutMs);

            await proc.WaitForExitAsync(timeoutCts.Token);

            // The process is gone; now make sure every byte it wrote has been delivered.
            await DrainAsync(outEof.Task, errEof.Task);

            return new ProcessResult(ProcessOutcome.Exited, proc.ExitCode,
                sbOut.ToString().Trim(), sbErr.ToString().Trim(), truncated);
        }
        catch (OperationCanceledException)
        {
            var cancelledByCaller = ct.IsCancellationRequested;
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }

            // Collect whatever was produced before the kill, without hanging on it.
            await DrainAsync(outEof.Task, errEof.Task);

            if (cancelledByCaller)
            {
                Log.Warn($"Cancelled: {command.LogText}");
                return new ProcessResult(ProcessOutcome.Cancelled, null,
                    sbOut.ToString().Trim(), sbErr.ToString().Trim(), truncated);
            }

            Log.Warn($"Timeout ({command.TimeoutMs}ms): {command.LogText}");
            return new ProcessResult(ProcessOutcome.TimedOut, null,
                sbOut.ToString().Trim(), sbErr.ToString().Trim(), truncated);
        }
        catch (Exception ex)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            Log.Error($"Run failed: {command.LogText}", ex);
            return new ProcessResult(ProcessOutcome.StartFailed, null,
                sbOut.ToString().Trim(), sbErr.ToString().Trim(), truncated,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Waits for both streams to hit EOF, bounded by <see cref="DrainGrace"/>.</summary>
    private static async Task DrainAsync(Task outEof, Task errEof)
    {
        try { await Task.WhenAll(outEof, errEof).WaitAsync(DrainGrace); }
        catch (TimeoutException)
        {
            // A detached grandchild still holds a pipe. Return what we have rather than hang.
            Log.Warn("Output drain timed out; captured output may be incomplete.");
        }
        catch { }
    }

    /// <summary>Appends a line, enforcing the capture cap.</summary>
    private static void Append(StringBuilder sb, string line, ref bool truncated)
    {
        if (sb.Length >= MaxCapturedChars) { truncated = true; return; }

        var remaining = MaxCapturedChars - sb.Length;
        if (line.Length + System.Environment.NewLine.Length > remaining)
        {
            sb.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
            truncated = true;
            return;
        }
        sb.AppendLine(line);
    }

    /// <summary>Launch a detached GUI tool (the mirroring engine); returns the started Process.</summary>
    public static Process? Launch(string exePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? env = null, bool showConsole = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = !showConsole,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;
        return Process.Start(psi);
    }
}

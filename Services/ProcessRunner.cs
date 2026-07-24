using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScrcpyDeck.Services;

public record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
    public string Output => string.IsNullOrWhiteSpace(StdOut) ? StdErr : StdOut;
}

/// <summary>Runs external console tools (adb) hidden and captures their output.</summary>
public static class ProcessRunner
{
    /// <summary>Run a tool to completion and capture stdout/stderr.</summary>
    public static async Task<ProcessResult> RunAsync(
        string exePath, string arguments, int timeoutMs = 20000, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
        };

        using var proc = new Process { StartInfo = psi };
        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await proc.WaitForExitAsync(timeoutCts.Token);
            return new ProcessResult(proc.ExitCode, sbOut.ToString().Trim(), sbErr.ToString().Trim());
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            Log.Warn($"Timeout ({timeoutMs}ms): {Path.GetFileName(exePath)} {arguments}");
            return new ProcessResult(-1, sbOut.ToString().Trim(),
                "انتهت مهلة تنفيذ الأمر.\n" + sbErr);
        }
        catch (Exception ex)
        {
            Log.Error($"Run failed: {Path.GetFileName(exePath)} {arguments}", ex);
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    /// <summary>Launch a detached GUI tool (the mirroring engine); returns the started Process.</summary>
    public static Process? Launch(string exePath, string arguments,
        IReadOnlyDictionary<string, string>? env = null, bool showConsole = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = !showConsole,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
        };
        if (env != null)
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;
        return Process.Start(psi);
    }
}

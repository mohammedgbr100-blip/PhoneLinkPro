using System.Diagnostics;
using System.Text;

namespace ScrcpyDeck.Services;

/// <summary>
/// The slice of <see cref="Process"/> the session lifecycle actually needs.
/// <para>
/// This exists so the state machine can be tested deterministically (fast exits, exit/stop
/// races, disposal counting) without spawning real programs. Production always uses
/// <see cref="SystemProcessLauncher"/>, which wraps a real System.Diagnostics.Process.
/// </para>
/// </summary>
public interface IProcessHandle : IDisposable
{
    int Id { get; }

    /// <summary>True once the process has terminated. Must never throw.</summary>
    bool HasExited { get; }

    /// <summary>Exit code, or null when it is not (yet) knowable.</summary>
    int? TryGetExitCode();

    /// <summary>Raised once the process terminates, after <see cref="BeginWatching"/>.</summary>
    event EventHandler? Exited;

    /// <summary>Starts raising <see cref="Exited"/>. Safe to call once.</summary>
    void BeginWatching();

    /// <summary>Diagnostic output captured so far (bounded).</summary>
    string Diagnostics { get; }

    /// <summary>Terminates the process, and its children where supported. Must never throw.</summary>
    void Kill(bool entireTree);
}

/// <summary>Creates process handles. Narrow seam for tests; no DI container involved.</summary>
public interface IProcessLauncher
{
    /// <summary>Starts a process. Throws when it cannot be started.</summary>
    IProcessHandle Start(ProcessCommand command);
}

/// <summary>Real launcher, backed by System.Diagnostics.Process.</summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    public static readonly SystemProcessLauncher Instance = new();

    public IProcessHandle Start(ProcessCommand command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Captured so a failed launch can be explained; bounded by SystemProcessHandle.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = System.IO.Path.GetDirectoryName(command.ExecutablePath)
                               ?? Environment.CurrentDirectory,
        };
        foreach (var a in command.Arguments) psi.ArgumentList.Add(a);
        if (command.Environment != null)
            foreach (var kv in command.Environment) psi.Environment[kv.Key] = kv.Value;

        var proc = new Process { StartInfo = psi };
        var handle = new SystemProcessHandle(proc);
        handle.StartAndCapture();
        return handle;
    }
}

/// <summary>Wraps a real process, capturing bounded diagnostics and disposing exactly once.</summary>
internal sealed class SystemProcessHandle : IProcessHandle
{
    /// <summary>Engine diagnostics are for a log line, not a transcript.</summary>
    internal const int MaxDiagnosticChars = 8_000;

    private readonly Process _proc;
    private readonly StringBuilder _diag = new();
    private readonly object _diagGate = new();
    private int _disposed;
    private int _watching;

    internal SystemProcessHandle(Process proc) => _proc = proc;

    public event EventHandler? Exited;

    public int Id { get { try { return _proc.Id; } catch { return -1; } } }

    public bool HasExited
    {
        get { try { return _proc.HasExited; } catch { return true; } }
    }

    public string Diagnostics { get { lock (_diagGate) return _diag.ToString(); } }

    public int? TryGetExitCode()
    {
        try { return _proc.HasExited ? _proc.ExitCode : null; }
        catch { return null; }
    }

    internal void StartAndCapture()
    {
        _proc.OutputDataReceived += (_, e) => Append(e.Data);
        _proc.ErrorDataReceived += (_, e) => Append(e.Data);

        _proc.Start();
        try { _proc.BeginOutputReadLine(); } catch { }
        try { _proc.BeginErrorReadLine(); } catch { }
    }

    private void Append(string? line)
    {
        if (line == null) return;
        lock (_diagGate)
        {
            if (_diag.Length >= MaxDiagnosticChars) return;
            var remaining = MaxDiagnosticChars - _diag.Length;
            _diag.Append(line.Length > remaining ? line[..remaining] : line).Append('\n');
        }
    }

    public void BeginWatching()
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1) return;
        try
        {
            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // The process may already be gone; the caller's post-subscribe HasExited check
            // covers that race.
        }
    }

    public void Kill(bool entireTree)
    {
        try { if (!_proc.HasExited) _proc.Kill(entireTree); }
        catch { /* already exited, access denied, or invalid handle — all tolerated */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _proc.Dispose(); } catch { }
    }
}

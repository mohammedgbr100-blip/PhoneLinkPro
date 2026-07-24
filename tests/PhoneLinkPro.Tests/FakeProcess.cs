using ScrcpyDeck.Services;

namespace PhoneLinkPro.Tests;

/// <summary>
/// A controllable <see cref="IProcessHandle"/>. Lets the session tests drive exits, fast
/// crashes and exit/stop races deterministically, with no real program and no phone.
/// </summary>
internal sealed class FakeProcessHandle : IProcessHandle
{
    private readonly object _sync = new();
    private bool _exited;
    private int? _exitCode;

    public FakeProcessHandle(int id = 4242) => Id = id;

    public int Id { get; }
    public int DisposeCount { get; private set; }
    public int KillCount { get; private set; }
    public bool KilledWholeTree { get; private set; }
    public bool WatchingStarted { get; private set; }

    /// <summary>Set to throw from <see cref="Kill"/>, mimicking access-denied.</summary>
    public Exception? KillThrows { get; set; }

    /// <summary>Set to throw from <see cref="Dispose"/>, mimicking a disposed handle.</summary>
    public Exception? DisposeThrows { get; set; }

    public string Diagnostics { get; set; } = "";

    public event EventHandler? Exited;

    public bool HasExited { get { lock (_sync) return _exited; } }

    public int? TryGetExitCode() { lock (_sync) return _exitCode; }

    public void BeginWatching() => WatchingStarted = true;

    public void Kill(bool entireTree)
    {
        KillCount++;
        KilledWholeTree = entireTree;
        if (KillThrows != null) return;   // production Kill swallows; mirror that contract
        SignalExit(0);
    }

    public void Dispose()
    {
        DisposeCount++;
        if (DisposeThrows != null) throw DisposeThrows;
    }

    /// <summary>Simulates the process terminating with the given code.</summary>
    public void SignalExit(int code)
    {
        lock (_sync)
        {
            if (_exited) return;
            _exited = true;
            _exitCode = code;
        }
        Exited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks the process dead WITHOUT raising the event, to model the race where a
    /// process exits before the caller can subscribe.</summary>
    public void ExitSilentlyBeforeSubscription(int code)
    {
        lock (_sync) { _exited = true; _exitCode = code; }
    }
}

/// <summary>Launcher that hands out pre-built fakes, or throws to model a start failure.</summary>
internal sealed class FakeLauncher : IProcessLauncher
{
    private readonly Func<ProcessCommand, IProcessHandle> _factory;

    public FakeLauncher(Func<ProcessCommand, IProcessHandle> factory) => _factory = factory;

    /// <summary>Always hands out the same handle (any implementation).</summary>
    public FakeLauncher(IProcessHandle handle) : this(_ => handle) { }

    public List<ProcessCommand> Commands { get; } = new();
    public int StartCount => Commands.Count;

    public IProcessHandle Start(ProcessCommand command)
    {
        Commands.Add(command);
        return _factory(command);
    }
}

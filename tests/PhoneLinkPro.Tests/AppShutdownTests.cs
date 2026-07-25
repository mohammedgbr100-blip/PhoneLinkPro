using System.Diagnostics;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// PROC-01 — the application shutdown sequence must be bounded, must continue past a failing
/// step, and must leave nothing running.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class AppShutdownTests : IDisposable
{
    private const string SerialA = "R58M12ABCDE";
    private const string SerialB = "192.168.1.42:5555";

    private readonly string _logDir;
    private readonly string? _previousLogOverride;

    public AppShutdownTests()
    {
        ScrcpyService.ResetForTests();
        AppLifecycle.ResetForTests();
        ScrcpyService.EngineFileCheck = () => Array.Empty<string>();
        ScrcpyService.IntegrityCheck = () => true;   // engine files assumed verified here
        ScrcpyService.StartupWindow = TimeSpan.FromMilliseconds(100);

        _logDir = Path.Combine(Path.GetTempPath(), "plp-tests-shutdown-" + Guid.NewGuid().ToString("N"));
        _previousLogOverride = Log.DirOverride;
        Log.DirOverride = _logDir;
    }

    public void Dispose()
    {
        ScrcpyService.ResetForTests();
        AppLifecycle.ResetForTests();
        Log.DirOverride = _previousLogOverride;
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private static ScrcpyOptions Opts(string serial) => new() { Serial = serial };

    private static async Task WaitFor(Func<bool> predicate, string what, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(15);
        }
        Assert.Fail($"timed out waiting for: {what}");
    }

    // ---------------------------------------------------------------- core sequence

    [Fact]
    public async Task Shutdown_StopsEveryOwnedSession()
    {
        var a = new FakeProcessHandle(1);
        var b = new FakeProcessHandle(2);
        ScrcpyService.Launcher = new FakeLauncher(cmd => cmd.Arguments.Contains(SerialA) ? a : b);

        ScrcpyService.Launch(Opts(SerialA));
        ScrcpyService.Launch(Opts(SerialB));
        await WaitFor(() => ScrcpyService.ActiveCount == 2, "both sessions active");

        AppLifecycle.Shutdown();

        Assert.Equal(0, ScrcpyService.ActiveCount);
        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(1, b.DisposeCount);
        Assert.True(a.KilledWholeTree);
        Assert.True(b.KilledWholeTree);
    }

    [Fact]
    public void Shutdown_MarksShuttingDownAndRefusesNewLaunches()
    {
        var launcher = new FakeLauncher(_ => new FakeProcessHandle());
        ScrcpyService.Launcher = launcher;

        AppLifecycle.Shutdown();

        Assert.True(AppLifecycle.IsShuttingDown);
        var result = ScrcpyService.Launch(Opts(SerialA));
        Assert.Equal(MirrorLaunchStatus.ShuttingDown, result.Status);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public void Shutdown_StopsDeviceWatcherScheduling()
    {
        // Start() needs a Dispatcher; assert the Stop half, which is what shutdown uses.
        DeviceWatcher.Stop();

        Assert.False(DeviceWatcher.IsRunning);
        var ex = Record.Exception(DeviceWatcher.Stop);   // idempotent
        Assert.Null(ex);
    }

    [Fact] // Batch 1E: the shutdown sequence no longer contains a SecurityGuard step
    public void Shutdown_HasNoSecurityGuardStep()
    {
        AppLifecycle.Shutdown();

        var text = string.Concat(Directory.GetFiles(_logDir, "*.log").Select(File.ReadAllText));
        Assert.Contains("Shutdown: begin", text);
        Assert.Contains("Shutdown: complete", text);
        Assert.DoesNotContain("security guard", text);
    }

    [Fact]
    public void Shutdown_RunsOnlyOnce()
    {
        AppLifecycle.Shutdown();
        var ex = Record.Exception(() =>
        {
            AppLifecycle.Shutdown();
            AppLifecycle.Shutdown();
        });

        Assert.Null(ex);
        Assert.True(AppLifecycle.IsShuttingDown);
    }

    // ---------------------------------------------------------------- resilience

    [Fact] // one failing component must not abort the rest of the sequence
    public async Task Shutdown_ContinuesAfterAComponentThrows()
    {
        var exploding = new FakeProcessHandle(1)
        {
            KillThrows = new InvalidOperationException("boom"),
            DisposeThrows = new ObjectDisposedException("handle"),
        };
        var healthy = new FakeProcessHandle(2);
        ScrcpyService.Launcher = new FakeLauncher(cmd =>
            cmd.Arguments.Contains(SerialA) ? exploding : healthy);

        ScrcpyService.Launch(Opts(SerialA));
        ScrcpyService.Launch(Opts(SerialB));
        await WaitFor(() => ScrcpyService.ActiveCount == 2, "both active");

        var ex = Record.Exception(AppLifecycle.Shutdown);

        Assert.Null(ex);
        // The healthy session was still cleaned up.
        Assert.Equal(1, healthy.DisposeCount);
        Assert.Equal(0, ScrcpyService.ActiveCount);
    }

    [Fact] // a wedged step must not hang the exit
    public async Task Shutdown_CompletesWithinItsBudgetEvenWhenAStepHangs()
    {
        var previousStep = AppLifecycle.StepBudget;
        var previousTotal = AppLifecycle.ShutdownBudget;
        AppLifecycle.StepBudget = TimeSpan.FromMilliseconds(300);
        AppLifecycle.ShutdownBudget = TimeSpan.FromSeconds(2);
        try
        {
            // A handle whose Kill blocks far longer than the step budget.
            var slow = new SlowKillHandle(TimeSpan.FromSeconds(30));
            ScrcpyService.Launcher = new FakeLauncher(slow);
            ScrcpyService.Launch(Opts(SerialA));
            await WaitFor(() => ScrcpyService.ActiveCount == 1, "session active");

            var sw = Stopwatch.StartNew();
            AppLifecycle.Shutdown();
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
                $"shutdown took {sw.Elapsed}, which is not bounded");
            Assert.True(AppLifecycle.IsShuttingDown);
        }
        finally
        {
            AppLifecycle.StepBudget = previousStep;
            AppLifecycle.ShutdownBudget = previousTotal;
        }
    }

    [Fact] // shutdown with nothing running is still clean and quick
    public void Shutdown_WithNoSessions_IsFastAndSilent()
    {
        var sw = Stopwatch.StartNew();
        var ex = Record.Exception(AppLifecycle.Shutdown);
        sw.Stop();

        Assert.Null(ex);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact] // failures are logged, never surfaced as a chain of dialogs
    public async Task Shutdown_LogsRatherThanShowingDialogs()
    {
        var exploding = new FakeProcessHandle(1)
        {
            KillThrows = new InvalidOperationException("kill failed"),
        };
        ScrcpyService.Launcher = new FakeLauncher(exploding);
        ScrcpyService.Launch(Opts(SerialA));
        await WaitFor(() => ScrcpyService.ActiveCount == 1, "active");

        AppLifecycle.Shutdown();

        // Log written; the test would have hung on a modal MessageBox instead.
        Assert.True(Directory.Exists(_logDir));
        var text = string.Concat(Directory.GetFiles(_logDir, "*.log").Select(File.ReadAllText));
        Assert.Contains("Shutdown: begin", text);
        Assert.Contains("Shutdown: complete", text);
    }

    // ---------------------------------------------------------------- adb policy

    [Fact] // the policy decision is consulted; it must never throw during shutdown
    public void Shutdown_AppliesTheAdbOwnershipPolicyWithoutThrowing()
    {
        var ex = Record.Exception(AppLifecycle.StopOwnedAdbServer);

        Assert.Null(ex);
    }

    [Fact] // HW-INSTALL-001 — a process that never started engine services (rejected second
           // instance, PLP_SELFTEST, tool modes) must NOT stop the shared ADB server at
           // exit: the image-path ownership proof cannot tell WHICH process started it.
    public void Shutdown_WithoutEngineServices_LeavesTheAdbServerAlone()
    {
        AppLifecycle.Shutdown();

        var text = string.Concat(Directory.GetFiles(_logDir, "*.log").Select(File.ReadAllText));
        Assert.Contains("engine services never started in this process", text);
        Assert.DoesNotContain("ADB server is our bundled bridge", text);
        Assert.DoesNotContain("kill-server", text);
    }

    [Fact] // ...while the process that DID start the services still applies the 1C policy
    public void Shutdown_WithEngineServices_ConsultsTheOwnershipPolicy()
    {
        AppLifecycle.NoteEngineServicesStarted();

        AppLifecycle.Shutdown();

        var text = string.Concat(Directory.GetFiles(_logDir, "*.log").Select(File.ReadAllText));
        Assert.DoesNotContain("engine services never started in this process", text);
        // Whatever the machine state, exactly one of the four documented decisions logs.
        Assert.True(
            text.Contains("no ADB server running")
            || text.Contains("belongs to another tool")
            || text.Contains("ownership could not be proven")
            || text.Contains("bundled bridge; stopping"),
            "expected an ADB ownership decision in the shutdown log");
    }

    /// <summary>A handle whose Kill blocks, to prove the step budget is enforced.</summary>
    private sealed class SlowKillHandle : IProcessHandle
    {
        private readonly TimeSpan _delay;
        private bool _exited;

        public SlowKillHandle(TimeSpan delay) => _delay = delay;

        public int Id => 99;
        public bool HasExited => _exited;
        public string Diagnostics => "";
        public event EventHandler? Exited;

        public int? TryGetExitCode() => _exited ? 0 : null;
        public void BeginWatching() { }

        public void Kill(bool entireTree)
        {
            Thread.Sleep(_delay);
            _exited = true;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() { }
    }
}

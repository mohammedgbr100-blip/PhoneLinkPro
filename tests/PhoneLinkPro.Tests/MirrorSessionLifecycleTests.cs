using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// PROC-02 / UX-01 / SCR-01 — the mirror-session state machine.
///
/// Driven through <see cref="FakeProcessHandle"/>, so exits, fast crashes and exit/stop
/// races are deterministic and no phone or real engine is required. Production still uses
/// the real System.Diagnostics.Process via SystemProcessLauncher.
///
/// The whole class shares one collection because ScrcpyService is a process-wide singleton.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class MirrorSessionLifecycleTests : IDisposable
{
    private const string SerialA = "R58M12ABCDE";
    private const string SerialB = "192.168.1.42:5555";

    public MirrorSessionLifecycleTests()
    {
        ScrcpyService.ResetForTests();
        AppLifecycle.ResetForTests();
        // Tests run without the bundled engine in their output directory.
        ScrcpyService.EngineFileCheck = () => Array.Empty<string>();
        ScrcpyService.IntegrityCheck = () => true;   // engine files assumed verified here
        ScrcpyService.StartupWindow = TimeSpan.FromMilliseconds(150);
    }

    public void Dispose()
    {
        ScrcpyService.ResetForTests();
        AppLifecycle.ResetForTests();
    }

    private static ScrcpyOptions Opts(string? serial) => new() { Serial = serial };

    /// <summary>Waits until <paramref name="predicate"/> holds, or fails the test.</summary>
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

    // ---------------------------------------------------------------- Starting -> Running

    [Fact]
    public async Task SuccessfulLaunch_TransitionsStartingToRunning()
    {
        var proc = new FakeProcessHandle();
        ScrcpyService.Launcher = new FakeLauncher(proc);

        var result = ScrcpyService.Launch(Opts(SerialA));

        Assert.True(result.Started);
        Assert.Equal(MirrorSessionState.Starting, result.Session!.State);
        Assert.True(proc.WatchingStarted, "exit subscription must be armed at attach time");

        await WaitFor(() => ScrcpyService.GetSession(SerialA)?.State == MirrorSessionState.Running,
            "session to reach Running");

        var session = ScrcpyService.GetSession(SerialA)!;
        Assert.Equal(SerialA, session.Serial);
        Assert.True(session.IsActive);
        Assert.Null(session.ExitedAt);
        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.True(session.StartedAt <= DateTimeOffset.UtcNow);
    }

    // ---------------------------------------------------------------- Starting -> Failed

    [Fact]
    public async Task FastNonZeroExit_TransitionsStartingToFailedAndClearsTheDevice()
    {
        var proc = new FakeProcessHandle { Diagnostics = "ERROR: could not find device" };
        ScrcpyService.Launcher = new FakeLauncher(proc);

        var result = ScrcpyService.Launch(Opts(SerialA));
        Assert.True(result.Started);

        proc.SignalExit(1);   // dies inside the startup window

        await WaitFor(() => ScrcpyService.GetSession(SerialA) == null,
            "failed session to be removed from the registry");

        // The device is free again, so the UI can offer Start.
        Assert.False(ScrcpyService.IsActive(SerialA));
        Assert.Equal(0, ScrcpyService.ActiveCount);
        Assert.Equal(1, proc.DisposeCount);
    }

    [Fact]
    public async Task FastZeroExit_IsAnOrdinaryExitNotAFailure()
    {
        var proc = new FakeProcessHandle();
        ScrcpyService.Launcher = new FakeLauncher(proc);

        ScrcpyService.Launch(Opts(SerialA));
        proc.SignalExit(0);

        await WaitFor(() => ScrcpyService.GetSession(SerialA) == null, "session cleanup");

        Assert.False(ScrcpyService.IsActive(SerialA));
    }

    [Fact] // a process that dies before the subscription lands must not be missed
    public async Task ProcessThatExitsBeforeSubscription_IsStillObserved()
    {
        var proc = new FakeProcessHandle();
        proc.ExitSilentlyBeforeSubscription(2);      // no event will ever be raised
        ScrcpyService.Launcher = new FakeLauncher(proc);

        ScrcpyService.Launch(Opts(SerialA));

        // Attach checks HasExited after subscribing, so the exit is picked up anyway.
        await WaitFor(() => ScrcpyService.GetSession(SerialA) == null,
            "silently-exited session to be reaped");
        Assert.False(ScrcpyService.IsActive(SerialA));
    }

    // ---------------------------------------------------------------- Running -> Exited

    [Fact] // closing the engine window manually must update the app
    public async Task ManualCloseAfterRunning_TransitionsToExitedAndFreesTheDevice()
    {
        var proc = new FakeProcessHandle();
        ScrcpyService.Launcher = new FakeLauncher(proc);
        ScrcpyService.Launch(Opts(SerialA));

        await WaitFor(() => ScrcpyService.GetSession(SerialA)?.State == MirrorSessionState.Running,
            "Running");

        var notified = 0;
        void OnChanged() => Interlocked.Increment(ref notified);
        ScrcpyService.SessionsChanged += OnChanged;
        try
        {
            proc.SignalExit(0);      // user closed the mirror window

            await WaitFor(() => !ScrcpyService.IsActive(SerialA), "device to be released");
            await WaitFor(() => Volatile.Read(ref notified) > 0, "a change notification");
        }
        finally { ScrcpyService.SessionsChanged -= OnChanged; }
    }

    // ---------------------------------------------------------------- Running -> Stopping -> Exited

    [Fact]
    public async Task Stop_TerminatesTheTreeAndReleasesTheDevice()
    {
        var proc = new FakeProcessHandle();
        ScrcpyService.Launcher = new FakeLauncher(proc);
        ScrcpyService.Launch(Opts(SerialA));
        await WaitFor(() => ScrcpyService.GetSession(SerialA)?.State == MirrorSessionState.Running,
            "Running");

        ScrcpyService.Stop(SerialA);

        Assert.False(ScrcpyService.IsActive(SerialA));
        Assert.Null(ScrcpyService.GetSession(SerialA));
        Assert.Equal(1, proc.KillCount);
        Assert.True(proc.KilledWholeTree, "the whole process tree must be terminated");
        Assert.Equal(1, proc.DisposeCount);
    }

    [Fact]
    public async Task RepeatedStop_IsSafeAndDisposesExactlyOnce()
    {
        var proc = new FakeProcessHandle();
        ScrcpyService.Launcher = new FakeLauncher(proc);
        ScrcpyService.Launch(Opts(SerialA));
        await WaitFor(() => ScrcpyService.GetSession(SerialA)?.State == MirrorSessionState.Running,
            "Running");

        ScrcpyService.Stop(SerialA);
        ScrcpyService.Stop(SerialA);
        ScrcpyService.Stop(SerialA);

        Assert.Equal(1, proc.DisposeCount);
    }

    [Fact]
    public async Task StopAfterProcessAlreadyExited_IsSafe()
    {
        var proc = new FakeProcessHandle();
        ScrcpyService.Launcher = new FakeLauncher(proc);
        ScrcpyService.Launch(Opts(SerialA));
        await WaitFor(() => ScrcpyService.GetSession(SerialA)?.State == MirrorSessionState.Running,
            "Running");

        proc.SignalExit(0);
        await WaitFor(() => !ScrcpyService.IsActive(SerialA), "release");

        var ex = Record.Exception(() => ScrcpyService.Stop(SerialA));

        Assert.Null(ex);
        Assert.Equal(1, proc.DisposeCount);
    }

    [Fact] // access-denied / invalid-handle must never reach the UI
    public async Task StopToleratesKillAndDisposeFailures()
    {
        var proc = new FakeProcessHandle
        {
            KillThrows = new InvalidOperationException("access denied"),
            DisposeThrows = new ObjectDisposedException("handle"),
        };
        ScrcpyService.Launcher = new FakeLauncher(proc);
        ScrcpyService.Launch(Opts(SerialA));
        await WaitFor(() => ScrcpyService.GetSession(SerialA)?.State == MirrorSessionState.Running,
            "Running");

        var ex = Record.Exception(() => ScrcpyService.Stop(SerialA));

        Assert.Null(ex);   // no ObjectDisposedException escapes
        Assert.False(ScrcpyService.IsActive(SerialA));
    }

    [Fact] // exit racing with an explicit stop
    public async Task ExitRacingWithStop_DisposesExactlyOnce()
    {
        var proc = new FakeProcessHandle();
        ScrcpyService.Launcher = new FakeLauncher(proc);
        ScrcpyService.Launch(Opts(SerialA));
        await WaitFor(() => ScrcpyService.GetSession(SerialA)?.State == MirrorSessionState.Running,
            "Running");

        await Task.WhenAll(
            Task.Run(() => proc.SignalExit(0)),
            Task.Run(() => ScrcpyService.Stop(SerialA)));

        await WaitFor(() => !ScrcpyService.IsActive(SerialA), "release");
        Assert.Equal(1, proc.DisposeCount);
    }

    // ---------------------------------------------------------------- UX-01 duplicates

    [Fact] // UX-01 — a double-click must not start two engines
    public async Task DuplicateLaunchForOneSerial_IsRejected()
    {
        var proc = new FakeProcessHandle();
        var launcher = new FakeLauncher(proc);
        ScrcpyService.Launcher = launcher;

        var first = ScrcpyService.Launch(Opts(SerialA));
        var second = ScrcpyService.Launch(Opts(SerialA));

        Assert.True(first.Started);
        Assert.False(second.Started);
        Assert.Equal(MirrorLaunchStatus.AlreadyActive, second.Status);
        Assert.Equal(1, launcher.StartCount);

        await WaitFor(() => ScrcpyService.GetSession(SerialA)?.State == MirrorSessionState.Running,
            "Running");

        // Still refused once Running, not only while Starting.
        Assert.False(ScrcpyService.Launch(Opts(SerialA)).Started);
        Assert.Equal(1, launcher.StartCount);
    }

    [Fact] // UX-01 — concurrent requests collapse to one process
    public void SimultaneousLaunches_ForOneSerial_StartExactlyOneProcess()
    {
        var launcher = new FakeLauncher(_ => new FakeProcessHandle());
        ScrcpyService.Launcher = launcher;

        var results = new MirrorLaunchResult[16];
        Parallel.For(0, results.Length, i => results[i] = ScrcpyService.Launch(Opts(SerialA)));

        Assert.Equal(1, results.Count(r => r.Started));
        Assert.Equal(1, launcher.StartCount);
        Assert.All(results.Where(r => !r.Started),
            r => Assert.Equal(MirrorLaunchStatus.AlreadyActive, r.Status));
    }

    [Fact] // different devices are independent
    public async Task TwoDevices_RunSimultaneouslyAndIndependently()
    {
        var a = new FakeProcessHandle(1);
        var b = new FakeProcessHandle(2);
        ScrcpyService.Launcher = new FakeLauncher(cmd =>
            cmd.Arguments.Contains(SerialA) ? a : b);

        Assert.True(ScrcpyService.Launch(Opts(SerialA)).Started);
        Assert.True(ScrcpyService.Launch(Opts(SerialB)).Started);

        await WaitFor(() => ScrcpyService.ActiveCount == 2, "both sessions active");

        // Stopping one leaves the other untouched.
        ScrcpyService.Stop(SerialA);

        Assert.False(ScrcpyService.IsActive(SerialA));
        Assert.True(ScrcpyService.IsActive(SerialB));
        Assert.Equal(1, ScrcpyService.ActiveCount);
        Assert.Equal(1, a.KillCount);
        Assert.Equal(0, b.KillCount);
    }

    [Fact]
    public async Task StopAll_StopsEveryDevice()
    {
        var a = new FakeProcessHandle(1);
        var b = new FakeProcessHandle(2);
        ScrcpyService.Launcher = new FakeLauncher(cmd => cmd.Arguments.Contains(SerialA) ? a : b);

        ScrcpyService.Launch(Opts(SerialA));
        ScrcpyService.Launch(Opts(SerialB));
        await WaitFor(() => ScrcpyService.ActiveCount == 2, "both active");

        ScrcpyService.StopAll();

        Assert.Equal(0, ScrcpyService.ActiveCount);
        Assert.Empty(ScrcpyService.Snapshot());
        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(1, b.DisposeCount);
    }

    // ---------------------------------------------------------------- SCR-01 launch guards

    [Fact] // SCR-01 — missing engine files block the launch with a friendly message
    public void MissingEngineFiles_RejectTheLaunch()
    {
        ScrcpyService.EngineFileCheck = () => new[] { "PhoneLink.Engine.exe" };
        var launcher = new FakeLauncher(_ => new FakeProcessHandle());
        ScrcpyService.Launcher = launcher;

        var result = ScrcpyService.Launch(Opts(SerialA));

        Assert.Equal(MirrorLaunchStatus.EngineMissing, result.Status);
        Assert.Equal(0, launcher.StartCount);
        Assert.Contains("المحرّك", result.Message);
    }

    [Fact] // SCR-01 — a missing dependency (not just the exe) also blocks
    public void MissingEngineDependency_RejectsTheLaunch()
    {
        ScrcpyService.EngineFileCheck = () => new[] { "core.dat" };
        ScrcpyService.Launcher = new FakeLauncher(_ => new FakeProcessHandle());

        Assert.Equal(MirrorLaunchStatus.EngineMissing, ScrcpyService.Launch(Opts(SerialA)).Status);
    }

    [Fact] // SCR-01 — the Batch 1B serial validation is enforced on this path too
    public void InvalidSerial_RejectsTheLaunchWithoutStartingAnything()
    {
        var launcher = new FakeLauncher(_ => new FakeProcessHandle());
        ScrcpyService.Launcher = launcher;

        var result = ScrcpyService.Launch(Opts("abc kill-server"));

        Assert.Equal(MirrorLaunchStatus.InvalidDevice, result.Status);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact] // SCR-01 — a start failure is reported, not thrown, and frees the device
    public void ProcessStartFailure_IsReportedAndReleasesTheDevice()
    {
        ScrcpyService.Launcher = new FakeLauncher(
            _ => throw new System.ComponentModel.Win32Exception("The system cannot find the file"));

        var result = ScrcpyService.Launch(Opts(SerialA));

        Assert.Equal(MirrorLaunchStatus.StartFailed, result.Status);
        Assert.False(ScrcpyService.IsActive(SerialA));
        Assert.Null(ScrcpyService.GetSession(SerialA));

        // The user-facing text carries no exception detail and no path.
        Assert.DoesNotContain("Win32", result.Message);
        Assert.DoesNotContain(":\\", result.Message);
    }

    [Fact] // the engine is launched through the structured ArgumentList API
    public void LaunchUsesStructuredArgumentsAndEngineEnvironment()
    {
        var launcher = new FakeLauncher(_ => new FakeProcessHandle());
        ScrcpyService.Launcher = launcher;

        ScrcpyService.Launch(new ScrcpyOptions { Serial = SerialA, WindowTitle = "a\" --record \"b" });

        var cmd = Assert.Single(launcher.Commands);
        Assert.Contains("-s", cmd.Arguments);
        Assert.Contains(SerialA, cmd.Arguments);
        // The hostile title is one literal element, exactly as CMD-01 requires.
        Assert.Contains("a\" --record \"b", cmd.Arguments);
        Assert.NotNull(cmd.Environment);
        Assert.True(cmd.Environment!.ContainsKey("ADB"));
    }

    [Fact] // a launch with no serial still gets a session, keyed to the implicit device
    public async Task LaunchWithoutSerial_UsesTheDefaultDeviceKey()
    {
        ScrcpyService.Launcher = new FakeLauncher(_ => new FakeProcessHandle());

        Assert.True(ScrcpyService.Launch(Opts(null)).Started);

        await WaitFor(() => ScrcpyService.IsActive(null), "default-device session");
        Assert.Equal(ScrcpyService.DefaultDeviceKey, ScrcpyService.GetSession(null)!.Serial);
    }

    // ---------------------------------------------------------------- diagnostics

    [Fact] // SCR-01 — machine-specific paths are stripped before logging
    public void Diagnostics_AreSanitizedOfPersonalPaths()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var raw = $"ERROR at {profile}\\Desktop\\clip.mp4 while opening {ToolLocator.BaseDir}engine";

        var clean = ScrcpyService.Sanitize(raw);

        Assert.DoesNotContain(profile, clean);
        Assert.Contains("<user>", clean);
        Assert.Contains("ERROR", clean);
    }

    [Fact] // SCR-01 — Arabic and Unicode diagnostics survive sanitization
    public void Diagnostics_PreserveArabicAndUnicode()
    {
        var clean = ScrcpyService.Sanitize("خطأ في المحرّك — 日本語 — device offline");

        Assert.Contains("خطأ في المحرّك", clean);
        Assert.Contains("日本語", clean);
    }

    [Fact] // SCR-01 — diagnostics are capped
    public void Diagnostics_AreCapped()
    {
        var clean = ScrcpyService.Sanitize(new string('z', 50_000));

        Assert.True(clean.Length <= 8_000, $"diagnostics were {clean.Length} chars");
    }

    [Fact]
    public void Diagnostics_EmptyInputIsRepresentedExplicitly()
        => Assert.Equal("(none)", ScrcpyService.Sanitize("   "));

    // ---------------------------------------------------------------- shutdown gate

    [Fact] // PROC-01 — no new sessions once shutdown starts
    public void LaunchAfterShutdownBegins_IsRefused()
    {
        var launcher = new FakeLauncher(_ => new FakeProcessHandle());
        ScrcpyService.Launcher = launcher;

        ScrcpyService.BeginShutdown();
        var result = ScrcpyService.Launch(Opts(SerialA));

        Assert.Equal(MirrorLaunchStatus.ShuttingDown, result.Status);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact] // shutdown racing with a natural exit must not throw or double-dispose
    public async Task ShutdownRacingWithExit_IsSafe()
    {
        var proc = new FakeProcessHandle();
        ScrcpyService.Launcher = new FakeLauncher(proc);
        ScrcpyService.Launch(Opts(SerialA));
        await WaitFor(() => ScrcpyService.GetSession(SerialA)?.State == MirrorSessionState.Running,
            "Running");

        Exception? failure = null;
        await Task.WhenAll(
            Task.Run(() => { try { proc.SignalExit(0); } catch (Exception ex) { failure = ex; } }),
            Task.Run(() => { try { ScrcpyService.StopAll(); } catch (Exception ex) { failure = ex; } }));

        Assert.Null(failure);
        Assert.Equal(1, proc.DisposeCount);
        Assert.Equal(0, ScrcpyService.ActiveCount);
    }

    // ---------------------------------------------------------------- snapshot safety

    [Fact] // the snapshot must expose no live process object
    public async Task Snapshot_IsImmutableAndProcessFree()
    {
        var proc = new FakeProcessHandle();
        ScrcpyService.Launcher = new FakeLauncher(proc);
        ScrcpyService.Launch(Opts(SerialA));
        await WaitFor(() => ScrcpyService.GetSession(SerialA)?.State == MirrorSessionState.Running,
            "Running");

        var snapshot = Assert.Single(ScrcpyService.Snapshot());

        // Held across disposal; reading it afterwards must stay safe.
        ScrcpyService.Stop(SerialA);

        Assert.Equal(SerialA, snapshot.Serial);
        Assert.Equal(MirrorSessionState.Running, snapshot.State);   // a value, frozen in time
        var ex = Record.Exception(() => _ = snapshot.ToString());
        Assert.Null(ex);
    }
}

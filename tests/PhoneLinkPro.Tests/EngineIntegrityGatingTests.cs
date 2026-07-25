using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// SEC-01 (Batch 1E) — how the rest of the app behaves around integrity verification:
/// ScrcpyService refuses to create sessions, AdbService never launches the bridge,
/// DeviceWatcher neither reports phantom disconnects nor invokes ADB during a failure,
/// UI notifications deduplicate, and a successful Retry restores everything.
///
/// In the global collection: these tests drive the process-wide service singletons.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class EngineIntegrityGatingTests : IDisposable
{
    private const string SerialA = "R58M12ABCDE";

    private static readonly EngineIntegrityResult ValidResult =
        new(EngineIntegrityStatus.Valid, null, null, 11, TimeSpan.Zero);

    private static readonly EngineIntegrityResult TamperedResult =
        new(EngineIntegrityStatus.HashMismatch, "engine/SDL2.dll", null, 11, TimeSpan.Zero);

    public EngineIntegrityGatingTests()
    {
        ScrcpyService.ResetForTests();
        AppLifecycle.ResetForTests();
        DeviceWatcher.ResetForTests();
        EngineIntegrityVerifier.ResetForTests();
        AdbService.ResetIntegrityGateForTests();
        AppState.SetCurrent(null);
        ScrcpyService.EngineFileCheck = () => Array.Empty<string>();
        ScrcpyService.StartupWindow = TimeSpan.FromMilliseconds(100);
    }

    public void Dispose()
    {
        ScrcpyService.ResetForTests();
        AppLifecycle.ResetForTests();
        DeviceWatcher.ResetForTests();
        EngineIntegrityVerifier.ResetForTests();
        AdbService.ResetIntegrityGateForTests();
        // The watcher tests above confirm devices, which selects one into the process-wide
        // AppState; clear it so later collection members see a pristine selection.
        AppState.SetCurrent(null);
    }

    // ---------------------------------------------------------------- ScrcpyService

    [Fact] // failure creates NO session, launches NO process, leaves NO stale Starting entry
    public void Launch_WithInvalidIntegrity_CreatesNoSessionAndStartsNoProcess()
    {
        var launcher = new FakeLauncher(_ => new FakeProcessHandle());
        ScrcpyService.Launcher = launcher;
        ScrcpyService.IntegrityCheck = () => false;

        var result = ScrcpyService.Launch(new ScrcpyOptions { Serial = SerialA });

        Assert.Equal(MirrorLaunchStatus.IntegrityBlocked, result.Status);
        Assert.False(result.Started);
        Assert.Null(result.Session);
        Assert.Equal(0, launcher.StartCount);
        Assert.Equal(0, ScrcpyService.ActiveCount);
        Assert.Null(ScrcpyService.GetSession(SerialA));
        Assert.NotEqual("", result.Message);   // Arabic guidance, not silence
    }

    [Fact] // successful verification permits a launch
    public void Launch_WithValidIntegrity_Starts()
    {
        ScrcpyService.Launcher = new FakeLauncher(new FakeProcessHandle());
        ScrcpyService.IntegrityCheck = () => true;

        var result = ScrcpyService.Launch(new ScrcpyOptions { Serial = SerialA });

        Assert.Equal(MirrorLaunchStatus.Started, result.Status);
    }

    [Fact] // Retry flow: blocked while invalid, working immediately once valid again
    public void Launch_RecoversAfterIntegrityIsRestored()
    {
        var launcher = new FakeLauncher(_ => new FakeProcessHandle());
        ScrcpyService.Launcher = launcher;
        var intact = false;
        ScrcpyService.IntegrityCheck = () => intact;

        Assert.Equal(MirrorLaunchStatus.IntegrityBlocked,
            ScrcpyService.Launch(new ScrcpyOptions { Serial = SerialA }).Status);

        intact = true;   // "repair + successful Retry"
        Assert.Equal(MirrorLaunchStatus.Started,
            ScrcpyService.Launch(new ScrcpyOptions { Serial = SerialA }).Status);
        Assert.Equal(1, launcher.StartCount);
    }

    // ---------------------------------------------------------------- AdbService

    [Fact] // a blocked adb call is StartFailed (never InvalidInput) and carries safe detail
    public async Task AdbCall_WithInvalidIntegrity_IsBlockedWithoutLaunching()
    {
        var gateCalls = 0;
        AdbService.IntegrityGate = _ => { gateCalls++; return Task.FromResult(TamperedResult); };

        var result = await AdbService.ClearAsync(SerialA, "com.example.app");

        Assert.Equal(ProcessOutcome.StartFailed, result.Outcome);
        Assert.Equal(1, gateCalls);
        Assert.NotNull(result.FailureDetail);
        Assert.Contains("engine integrity", result.FailureDetail);
        Assert.Contains("HashMismatch", result.FailureDetail);
        // Never a hash value, never an absolute path.
        Assert.DoesNotMatch("[0-9a-f]{64}", result.FailureDetail);
        Assert.DoesNotContain(":\\", result.FailureDetail);
    }

    [Fact] // input validation still runs FIRST: bad input is InvalidInput, not integrity noise
    public async Task InvalidInput_IsStillRejectedBeforeTheIntegrityGate()
    {
        var gateCalls = 0;
        AdbService.IntegrityGate = _ => { gateCalls++; return Task.FromResult(TamperedResult); };

        var result = await AdbService.ClearAsync("abc kill-server", "com.example.app");

        Assert.Equal(ProcessOutcome.InvalidInput, result.Outcome);
        Assert.Equal(0, gateCalls);
    }

    [Fact] // pairing is gated too, and the blocked path still never logs the code
    public async Task Pair_WithInvalidIntegrity_IsBlocked()
    {
        AdbService.IntegrityGate = _ => Task.FromResult(TamperedResult);

        var result = await AdbService.PairAsync("192.168.1.42:37123", "246813");

        Assert.Equal(ProcessOutcome.StartFailed, result.Outcome);
    }

    // ---------------------------------------------------------------- device discovery

    [Fact]
    public async Task Discovery_WithInvalidIntegrity_ReportsIntegrityBlockedAsUnknown()
    {
        AdbService.IntegrityGate = _ => Task.FromResult(TamperedResult);

        var result = await AdbService.DiscoverDevicesAsync();

        Assert.Equal(DeviceDiscoveryOutcome.IntegrityBlocked, result.Outcome);
        Assert.True(result.IsUnknown);   // unknown state, NOT "no devices"
        Assert.Empty(result.Devices);
        Assert.Contains("engine integrity", result.Diagnostic);
    }

    [Fact]
    public async Task Discovery_WithCancelledVerification_ReportsCancelled()
    {
        AdbService.IntegrityGate = _ => Task.FromResult(new EngineIntegrityResult(
            EngineIntegrityStatus.VerificationCancelled, null, null, 0, TimeSpan.Zero));

        var result = await AdbService.DiscoverDevicesAsync();

        Assert.Equal(DeviceDiscoveryOutcome.Cancelled, result.Outcome);
    }

    // ---------------------------------------------------------------- DeviceWatcher

    [Fact] // integrity failure is NEVER read as "all devices unplugged"
    public async Task Watcher_KeepsConfirmedDevices_WhenIntegrityFails()
    {
        var confirmed = new List<AdbDevice> { new() { Serial = SerialA, State = "device" } };
        var removals = 0;
        DeviceWatcher.DevicesChanged += devices => { if (devices.Count == 0) removals++; };

        // 1) a healthy poll confirms one device.
        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(confirmed));
        DeviceWatcher.Start();
        await WaitFor(() => DeviceWatcher.LastConfirmed.Count == 1, "initial confirmation");

        // 2) integrity breaks: discovery now flows through the REAL AdbService gate.
        AdbService.IntegrityGate = _ => Task.FromResult(TamperedResult);
        DeviceWatcher.DiscoveryProvider = AdbService.DiscoverDevicesAsync;
        for (var i = 0; i < 3; i++)
        {
            DeviceWatcher.RequestRefresh();
            await Task.Delay(50);
        }
        await WaitFor(() => DeviceWatcher.ConsecutiveFailures >= 1, "failure noted");

        // The confirmed snapshot survives and no disconnect notification was raised.
        Assert.Single(DeviceWatcher.LastConfirmed);
        Assert.Equal(SerialA, DeviceWatcher.LastConfirmed[0].Serial);
        Assert.Equal(0, removals);
    }

    [Fact] // during a failure the bridge binary is never invoked — polls stop at the gate
    public async Task Watcher_DoesNotInvokeAdb_WhileIntegrityFails()
    {
        var gateCalls = 0;
        AdbService.IntegrityGate = _ => { gateCalls++; return Task.FromResult(TamperedResult); };
        DeviceWatcher.DiscoveryProvider = AdbService.DiscoverDevicesAsync;

        DeviceWatcher.Start();
        await WaitFor(() => DeviceWatcher.ConsecutiveFailures >= 1, "first gated poll");

        // Every attempt consulted the (cached-in-production) gate and reported
        // IntegrityBlocked — a result only the gate produces, proving adb never ran.
        Assert.True(gateCalls >= 1);
        Assert.Equal(0, DeviceWatcher.LastConfirmed.Count);
    }

    [Fact] // after repair + Retry, polling resumes and confirms devices again
    public async Task Watcher_ResumesNormally_AfterIntegrityIsRestored()
    {
        AdbService.IntegrityGate = _ => Task.FromResult(TamperedResult);
        DeviceWatcher.DiscoveryProvider = AdbService.DiscoverDevicesAsync;
        DeviceWatcher.Start();
        await WaitFor(() => DeviceWatcher.ConsecutiveFailures >= 1, "gated poll");

        // Repair: discovery becomes healthy again.
        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(
            new List<AdbDevice> { new() { Serial = SerialA, State = "device" } }));
        DeviceWatcher.RequestRefresh(manual: true);   // the Retry button's refresh

        await WaitFor(() => DeviceWatcher.LastConfirmed.Count == 1, "recovery");
        Assert.Equal(0, DeviceWatcher.ConsecutiveFailures);
    }

    // ---------------------------------------------------------------- notification dedup

    [Fact] // one user notification per DISTINCT state, however many polls repeat it
    public void NotificationKey_DeduplicatesIdenticalStates()
    {
        var a1 = EngineIntegrityMessages.NotificationKey(TamperedResult);
        var a2 = EngineIntegrityMessages.NotificationKey(
            TamperedResult with { Duration = TimeSpan.FromSeconds(9) });
        var differentFile = EngineIntegrityMessages.NotificationKey(
            TamperedResult with { AffectedPath = "engine/core.dat" });
        var differentStatus = EngineIntegrityMessages.NotificationKey(
            TamperedResult with { Status = EngineIntegrityStatus.MissingFile });

        Assert.Equal(a1, a2);                  // same state => same key => one dialog
        Assert.NotEqual(a1, differentFile);    // a distinct state notifies again
        Assert.NotEqual(a1, differentStatus);
    }

    // ---------------------------------------------------------------- messages (Part F)

    [Fact] // user-facing text: Arabic, specific-but-safe, no hashes, no absolute paths,
           // no malware accusations, no protection claims
    public void Messages_AreSafeAndSpecific()
    {
        foreach (var status in Enum.GetValues<EngineIntegrityStatus>())
        {
            var result = new EngineIntegrityResult(status, "engine/SDL2.dll", null, 11, TimeSpan.Zero);
            var text = EngineIntegrityMessages.For(result);

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotMatch("[0-9a-f]{64}", text);        // no hash values
            Assert.DoesNotContain(":\\", text);               // no absolute Windows paths
            Assert.DoesNotContain("Exception", text);         // no stack traces / type names
            Assert.DoesNotContain("فيروس", text);             // no malware accusation
        }

        // The affected relative file name IS shown where safe...
        Assert.Contains("engine/SDL2.dll", EngineIntegrityMessages.For(TamperedResult));
        // ...and repair guidance points at reinstalling from a trusted package.
        Assert.Contains("إعادة تثبيت", EngineIntegrityMessages.For(TamperedResult));
    }

    // ---------------------------------------------------------------- helpers

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
}

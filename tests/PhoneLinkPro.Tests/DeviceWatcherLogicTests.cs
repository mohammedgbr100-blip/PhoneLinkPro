using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// DEV-01 / DEV-02 — DeviceWatcher discovery, coalescing and reconciliation.
///
/// The watcher is a process-wide singleton, so this class joins the global-state collection
/// and fully resets the watcher around every test. The adb query is replaced by a
/// controllable provider, so no phone, no adb and no timer are involved.
///
/// The Batch 1A characterization tests that pinned "a failed poll looks like no devices"
/// are REPLACED by the regression tests below.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class DeviceWatcherLogicTests : IDisposable
{
    private static AdbDevice Dev(string serial, string state = "device", string model = "")
        => new() { Serial = serial, State = state, Model = model };

    public DeviceWatcherLogicTests()
    {
        DeviceWatcher.ResetForTests();
        AppState.SetCurrent(null);
    }

    public void Dispose()
    {
        DeviceWatcher.ResetForTests();
        AppState.SetCurrent(null);
    }

    private static async Task WaitFor(Func<bool> predicate, string what, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"timed out waiting for: {what}");
    }

    // ================================================================
    //  signature / diff (pure)
    // ================================================================

    [Fact]
    public void Signature_EncodesSerialAndStatePairs()
        => Assert.Equal("A:device|B:offline",
            DeviceWatcher.Signature([Dev("A"), Dev("B", "offline")]));

    [Fact]
    public void Signature_OfEmptyList_IsEmptyString()
        => Assert.Equal("", DeviceWatcher.Signature([]));

    [Fact]
    public void Signature_ChangesWhenAStateChanges()
        => Assert.NotEqual(DeviceWatcher.Signature([Dev("A", "unauthorized")]),
                           DeviceWatcher.Signature([Dev("A", "device")]));

    [Fact]
    public void Signature_IgnoresEverythingExceptSerialAndState()
        => Assert.Equal(DeviceWatcher.Signature([Dev("A")]),
                        DeviceWatcher.Signature([Dev("A", "device", "Galaxy A52")]));

    [Fact]
    public void Diff_DetectsAdditionsRemovalsAndStateChanges()
    {
        var before = new List<AdbDevice> { Dev("A"), Dev("B", "unauthorized") };
        var after = new List<AdbDevice> { Dev("B", "device"), Dev("C") };

        var delta = DeviceWatcher.Diff(before, after);

        Assert.Equal(["C"], delta.Added.Select(d => d.Serial));
        Assert.Equal(["A"], delta.Removed.Select(d => d.Serial));
        Assert.Equal(["B"], delta.StateChanged.Select(d => d.Serial));
    }

    // ================================================================
    //  DEV-01 — a failed poll is not "no devices"
    // ================================================================

    [Theory] // every failure outcome must be reported as UNKNOWN, never as empty
    [InlineData(DeviceDiscoveryOutcome.TimedOut)]
    [InlineData(DeviceDiscoveryOutcome.Cancelled)]
    [InlineData(DeviceDiscoveryOutcome.StartFailed)]
    [InlineData(DeviceDiscoveryOutcome.AdbError)]
    [InlineData(DeviceDiscoveryOutcome.Unusable)]
    public void DEV01_FailureOutcomesAreDistinguishableFromAnEmptySuccess(DeviceDiscoveryOutcome outcome)
    {
        var failure = DeviceDiscoveryResult.Failure(outcome, "diag");
        var emptySuccess = DeviceDiscoveryResult.Success([]);

        Assert.True(failure.IsUnknown);
        Assert.False(failure.IsSuccess);

        Assert.True(emptySuccess.IsSuccess);
        Assert.False(emptySuccess.IsUnknown);
        Assert.Empty(emptySuccess.Devices);
    }

    [Fact] // DEV-01 — the previously skipped test, now active
    public async Task DEV01_PollFailureIsNotTreatedAsDisconnection()
    {
        var current = Dev("A");
        AppState.SetCurrent(current);

        // First a success establishes the confirmed snapshot.
        var results = new Queue<DeviceDiscoveryResult>();
        results.Enqueue(DeviceDiscoveryResult.Success([Dev("A")]));
        results.Enqueue(DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.TimedOut, "timeout"));

        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(results.Dequeue());
        DeviceWatcher.Start();

        await WaitFor(() => DeviceWatcher.LastConfirmed.Count == 1, "first success applied");

        // Now the failing poll.
        DeviceWatcher.RequestRefresh();
        await WaitFor(() => DeviceWatcher.ConsecutiveFailures > 0, "failure recorded");

        // The device is still confirmed and still selected.
        Assert.Single(DeviceWatcher.LastConfirmed);
        Assert.Equal("A", DeviceWatcher.LastConfirmed[0].Serial);
        Assert.NotNull(AppState.CurrentDevice);
        Assert.Equal("A", AppState.CurrentSerial);
    }

    [Fact] // DEV-01 — a failure raises no DevicesChanged (so no disconnect UI)
    public async Task DEV01_FailureEmitsNoDevicesChanged()
    {
        var results = new Queue<DeviceDiscoveryResult>();
        results.Enqueue(DeviceDiscoveryResult.Success([Dev("A")]));
        results.Enqueue(DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.StartFailed, "no adb"));

        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(results.Dequeue());

        var changes = 0;
        DeviceWatcher.DevicesChanged += _ => Interlocked.Increment(ref changes);

        DeviceWatcher.Start();
        await WaitFor(() => Volatile.Read(ref changes) == 1, "first change");

        DeviceWatcher.RequestRefresh();
        await WaitFor(() => DeviceWatcher.ConsecutiveFailures > 0, "failure recorded");

        Assert.Equal(1, Volatile.Read(ref changes));   // no second event from the failure
    }

    [Fact] // DEV-01 — a genuine empty success DOES clear devices
    public async Task DEV01_EmptySuccessIsAppliedAsNoDevices()
    {
        var results = new Queue<DeviceDiscoveryResult>();
        results.Enqueue(DeviceDiscoveryResult.Success([Dev("A")]));
        results.Enqueue(DeviceDiscoveryResult.Success([]));

        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(results.Dequeue());
        DeviceWatcher.Start();
        await WaitFor(() => DeviceWatcher.LastConfirmed.Count == 1, "device confirmed");

        DeviceWatcher.RequestRefresh();
        await WaitFor(() => DeviceWatcher.LastConfirmed.Count == 0, "empty success applied");

        Assert.Empty(DeviceWatcher.LastConfirmed);
    }

    [Fact] // DEV-01 — a later success reconciles correctly after an outage
    public async Task DEV01_SuccessAfterFailureReconcilesState()
    {
        var results = new Queue<DeviceDiscoveryResult>();
        results.Enqueue(DeviceDiscoveryResult.Success([Dev("A")]));
        results.Enqueue(DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.TimedOut, null));
        results.Enqueue(DeviceDiscoveryResult.Success([Dev("A"), Dev("B")]));

        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(results.Dequeue());
        DeviceWatcher.Start();
        await WaitFor(() => DeviceWatcher.LastConfirmed.Count == 1, "first success");

        DeviceWatcher.RequestRefresh();
        await WaitFor(() => DeviceWatcher.ConsecutiveFailures > 0, "failure");

        DeviceWatcher.RequestRefresh();
        await WaitFor(() => DeviceWatcher.LastConfirmed.Count == 2, "recovery success");

        Assert.Equal(0, DeviceWatcher.ConsecutiveFailures);   // backoff reset
        Assert.Equal(["A", "B"], DeviceWatcher.LastConfirmed.Select(d => d.Serial));
    }

    // ================================================================
    //  DEV-02 — no refresh is ever lost
    // ================================================================

    [Fact] // DEV-02 — the previously skipped test, now active
    public async Task DEV02_RequestDuringAnInFlightPollTriggersExactlyOneFollowUp()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;

        DeviceWatcher.DiscoveryProvider = async _ =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1) await gate.Task;                 // hold the first query open
            return DeviceDiscoveryResult.Success([Dev("A")]);
        };

        DeviceWatcher.Start();
        await WaitFor(() => Volatile.Read(ref calls) == 1, "first query in flight");

        // Requests arriving while busy must be remembered, not dropped.
        DeviceWatcher.RequestRefresh();
        DeviceWatcher.RequestRefresh();
        DeviceWatcher.RequestRefresh();

        Assert.Equal(1, Volatile.Read(ref calls));       // still no overlap

        gate.SetResult();
        await WaitFor(() => Volatile.Read(ref calls) == 2, "one coalesced follow-up");

        await Task.Delay(150);
        Assert.Equal(2, Volatile.Read(ref calls));       // exactly one, not three
    }

    [Fact] // DEV-02 — queries never overlap
    public async Task DEV02_QueriesNeverOverlap()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        var calls = 0;

        DeviceWatcher.DiscoveryProvider = async _ =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref concurrent);
            Interlocked.Increment(ref calls);
            return DeviceDiscoveryResult.Success([Dev("A")]);
        };

        DeviceWatcher.Start();
        for (var i = 0; i < 50; i++) DeviceWatcher.RequestRefresh();

        await WaitFor(() => Volatile.Read(ref calls) >= 2, "several polls completed");
        await Task.Delay(200);

        Assert.Equal(1, Volatile.Read(ref maxConcurrent));
    }

    [Fact] // DEV-02 — the latest state still reaches the UI after a burst
    public async Task DEV02_LatestStateIsEventuallyDelivered()
    {
        var sequence = 0;
        DeviceWatcher.DiscoveryProvider = _ =>
        {
            var n = Interlocked.Increment(ref sequence);
            // Each poll reports a different device set.
            return Task.FromResult(DeviceDiscoveryResult.Success(
                Enumerable.Range(0, Math.Min(n, 3)).Select(i => Dev("DEV" + i)).ToList()));
        };

        DeviceWatcher.Start();
        for (var i = 0; i < 10; i++) DeviceWatcher.RequestRefresh();

        await WaitFor(() => DeviceWatcher.LastConfirmed.Count == 3, "latest snapshot delivered");
    }

    [Fact] // DEV-02 — the signature is committed only after the snapshot is applied
    public async Task DEV02_SignatureCommitsOnlyAfterSuccessfulApply()
    {
        DeviceWatcher.DiscoveryProvider = _ =>
            Task.FromResult(DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.AdbError, "boom"));

        DeviceWatcher.Start();
        await WaitFor(() => DeviceWatcher.ConsecutiveFailures > 0, "failure");

        // Nothing was confirmed, so a later success must still be treated as a change.
        Assert.Empty(DeviceWatcher.LastConfirmed);

        var changes = 0;
        DeviceWatcher.DevicesChanged += _ => Interlocked.Increment(ref changes);
        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success([Dev("A")]));

        DeviceWatcher.RequestRefresh();
        await WaitFor(() => Volatile.Read(ref changes) == 1, "change emitted after recovery");
    }

    [Fact] // DEV-02 — Stop cancels pending work and silences events
    public async Task DEV02_StopDiscardsPendingWorkAndEmitsNoFurtherEvents()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        DeviceWatcher.DiscoveryProvider = async _ =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1) await gate.Task;
            return DeviceDiscoveryResult.Success([Dev("A")]);
        };

        var changes = 0;
        DeviceWatcher.DevicesChanged += _ => Interlocked.Increment(ref changes);

        DeviceWatcher.Start();
        await WaitFor(() => Volatile.Read(ref calls) == 1, "in flight");

        DeviceWatcher.RequestRefresh();   // pending
        DeviceWatcher.Stop();             // shutdown begins
        gate.SetResult();

        await Task.Delay(250);

        Assert.Equal(1, Volatile.Read(ref calls));    // pending follow-up discarded
        Assert.Equal(0, Volatile.Read(ref changes));  // no event after Stop
    }

    [Fact] // DEV-02 — refresh requests after Stop are ignored
    public async Task DEV02_RefreshAfterStopIsIgnored()
    {
        var calls = 0;
        DeviceWatcher.DiscoveryProvider = _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(DeviceDiscoveryResult.Success([]));
        };

        DeviceWatcher.Start();
        await WaitFor(() => Volatile.Read(ref calls) >= 1, "initial poll");
        DeviceWatcher.Stop();

        var after = Volatile.Read(ref calls);
        for (var i = 0; i < 5; i++) DeviceWatcher.RequestRefresh();
        await Task.Delay(150);

        Assert.Equal(after, Volatile.Read(ref calls));
    }

    [Fact] // DEV-02 — Start/Stop are idempotent
    public void DEV02_RepeatedStartAndStopAreSafe()
    {
        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success([]));

        var ex = Record.Exception(() =>
        {
            DeviceWatcher.Start();
            DeviceWatcher.Start();
            DeviceWatcher.Start();
            DeviceWatcher.Stop();
            DeviceWatcher.Stop();
            DeviceWatcher.Stop();
        });

        Assert.Null(ex);
        Assert.False(DeviceWatcher.IsRunning);
    }

    // ================================================================
    //  notifications and selection
    // ================================================================

    [Fact]
    public void NoCurrentDevice_SelectsFirstAuthorized()
    {
        var (action, target) = DeviceWatcher.DecideCurrentDevice(
            null, [Dev("A", "unauthorized"), Dev("B"), Dev("C")]);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.SelectFirst, action);
        Assert.Equal("B", target!.Serial);
    }

    [Fact]
    public void NoCurrentDevice_AndNoneAuthorized_DoesNothing()
    {
        var (action, _) = DeviceWatcher.DecideCurrentDevice(
            null, [Dev("A", "unauthorized"), Dev("B", "offline")]);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.None, action);
    }

    [Fact] // selection is preserved while the serial is still present
    public void CurrentDeviceStillPresent_IsPreserved()
    {
        var (action, _) = DeviceWatcher.DecideCurrentDevice(Dev("A"), [Dev("A"), Dev("B")]);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.None, action);
    }

    [Fact] // a state transition updates without looking like a new device
    public void CurrentDeviceStateChanged_ReportsUpdateState()
    {
        var (action, target) = DeviceWatcher.DecideCurrentDevice(
            Dev("A", "unauthorized"), [Dev("A", "device")]);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.UpdateState, action);
        Assert.Equal("device", target!.State);
    }

    [Fact] // genuine removal falls back to another authorized device
    public void CurrentDeviceRemoved_FallsBackToAnotherAuthorizedDevice()
    {
        var (action, target) = DeviceWatcher.DecideCurrentDevice(Dev("A"), [Dev("B")]);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.SelectFirst, action);
        Assert.Equal("B", target!.Serial);
    }

    [Fact] // with nothing left, the selection is cleared
    public void CurrentDeviceRemoved_WithNoReplacement_ClearsSelection()
    {
        var (action, target) = DeviceWatcher.DecideCurrentDevice(Dev("A"), []);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.Disconnect, action);
        Assert.Null(target);
    }

    [Fact] // an unauthorized-only list is not a valid fallback
    public void CurrentDeviceRemoved_WithOnlyUnauthorizedLeft_ClearsSelection()
    {
        var (action, _) = DeviceWatcher.DecideCurrentDevice(Dev("A"), [Dev("B", "unauthorized")]);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.Disconnect, action);
    }

    [Fact] // DEV-03 knock-on: banners can no longer influence selection
    public void PhantomBannerDevicesCannotBeSelected()
    {
        var devices = AdbService.ParseDeviceList(
            "* daemon not running; starting now at tcp:5037\n" +
            "List of devices attached\n" +
            "R58M12ABCDE  device\n");

        var (action, target) = DeviceWatcher.DecideCurrentDevice(null, devices);

        Assert.Equal(DeviceWatcher.CurrentDeviceAction.SelectFirst, action);
        Assert.Equal("R58M12ABCDE", target!.Serial);
    }

    // ================================================================
    //  suspend / resume
    // ================================================================

    [Fact]
    public async Task Suspend_PausesPollingWithoutClearingDevices()
    {
        var calls = 0;
        DeviceWatcher.DiscoveryProvider = _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(DeviceDiscoveryResult.Success([Dev("A")]));
        };

        DeviceWatcher.Start();
        await WaitFor(() => DeviceWatcher.LastConfirmed.Count == 1, "device confirmed");

        DeviceWatcher.OnSuspended();
        var before = Volatile.Read(ref calls);

        for (var i = 0; i < 5; i++) DeviceWatcher.RequestRefresh();
        await Task.Delay(150);

        Assert.True(DeviceWatcher.IsSuspended);
        Assert.Equal(before, Volatile.Read(ref calls));       // no polling while asleep
        Assert.Single(DeviceWatcher.LastConfirmed);           // and no false disconnect
    }

    [Fact]
    public async Task Resume_TriggersAnImmediateRefresh()
    {
        var calls = 0;
        DeviceWatcher.DiscoveryProvider = _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(DeviceDiscoveryResult.Success([Dev("A")]));
        };
        DeviceWatcher.ResumeStabilization = TimeSpan.Zero;

        DeviceWatcher.Start();
        await WaitFor(() => Volatile.Read(ref calls) >= 1, "initial poll");

        DeviceWatcher.OnSuspended();
        var before = Volatile.Read(ref calls);

        DeviceWatcher.OnResumed();

        Assert.False(DeviceWatcher.IsSuspended);
        await WaitFor(() => Volatile.Read(ref calls) > before, "immediate refresh after resume");
    }

    [Fact]
    public void RepeatedSuspendAndResume_IsSafe()
    {
        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success([]));
        DeviceWatcher.Start();

        var ex = Record.Exception(() =>
        {
            DeviceWatcher.OnSuspended();
            DeviceWatcher.OnSuspended();
            DeviceWatcher.OnResumed();
            DeviceWatcher.OnResumed();
        });

        Assert.Null(ex);
        Assert.False(DeviceWatcher.IsSuspended);
    }

    [Fact] // resume clears a pre-sleep failure streak so recovery is immediate
    public async Task Resume_ResetsTheFailureBackoff()
    {
        var failing = true;
        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(
            failing ? DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.TimedOut, null)
                    : DeviceDiscoveryResult.Success([Dev("A")]));
        DeviceWatcher.ResumeStabilization = TimeSpan.Zero;

        DeviceWatcher.Start();
        await WaitFor(() => DeviceWatcher.ConsecutiveFailures > 0, "failure recorded");
        Assert.True(DeviceWatcher.CurrentBackoff > TimeSpan.Zero);

        // The device is back after the machine wakes, so the resume poll succeeds. Resume
        // clears the streak up front, which is what lets that poll run immediately instead
        // of being throttled by the pre-sleep backoff.
        failing = false;
        DeviceWatcher.OnSuspended();
        DeviceWatcher.OnResumed();

        await WaitFor(() => DeviceWatcher.LastConfirmed.Count == 1,
            "resume poll ran immediately and succeeded");
        Assert.Equal(0, DeviceWatcher.ConsecutiveFailures);
    }

    // ---------------------------------------------------------------- power subscription

    [Fact] // the subscription is taken once and released on Stop (no static handler leak)
    public void PowerEventSubscription_IsTakenOnceAndReleasedOnStop()
    {
        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success([]));

        DeviceWatcher.Start();
        Assert.True(DeviceWatcher.IsPowerHooked);

        DeviceWatcher.Start();                       // idempotent
        Assert.True(DeviceWatcher.IsPowerHooked);

        DeviceWatcher.Stop();
        Assert.False(DeviceWatcher.IsPowerHooked);

        DeviceWatcher.Stop();                        // idempotent
        Assert.False(DeviceWatcher.IsPowerHooked);
    }

    // ================================================================
    //  backoff
    // ================================================================

    [Fact]
    public void Backoff_GrowsWithConsecutiveFailuresUpToTheMaximum()
    {
        Assert.Equal(TimeSpan.Zero, DeviceWatcher.BackoffFor(0));
        Assert.Equal(TimeSpan.FromSeconds(4), DeviceWatcher.BackoffFor(1));
        Assert.Equal(TimeSpan.FromSeconds(8), DeviceWatcher.BackoffFor(2));
        Assert.Equal(TimeSpan.FromSeconds(16), DeviceWatcher.BackoffFor(3));
        Assert.Equal(TimeSpan.FromSeconds(32), DeviceWatcher.BackoffFor(4));
        Assert.Equal(DeviceWatcher.BackoffMax, DeviceWatcher.BackoffFor(5));
    }

    [Fact] // it never grows without bound
    public void Backoff_IsCapped()
    {
        foreach (var n in new[] { 6, 10, 100, 1000, int.MaxValue })
            Assert.Equal(DeviceWatcher.BackoffMax, DeviceWatcher.BackoffFor(n));
    }

    [Fact]
    public async Task Backoff_ResetsAfterASuccessfulPoll()
    {
        var fail = true;
        DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(
            fail ? DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.TimedOut, null)
                 : DeviceDiscoveryResult.Success([Dev("A")]));

        DeviceWatcher.Start();
        await WaitFor(() => DeviceWatcher.ConsecutiveFailures > 0, "failure");
        Assert.True(DeviceWatcher.CurrentBackoff > TimeSpan.Zero);

        fail = false;
        DeviceWatcher.RequestRefresh();               // manual bypasses the backoff
        await WaitFor(() => DeviceWatcher.ConsecutiveFailures == 0, "backoff reset");

        Assert.Equal(TimeSpan.Zero, DeviceWatcher.CurrentBackoff);
    }

    [Fact] // an automatic poll respects the backoff; a manual refresh bypasses it once
    public async Task Backoff_SuppressesAutomaticPollsButNotManualRefresh()
    {
        var calls = 0;
        DeviceWatcher.DiscoveryProvider = _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.AdbError, null));
        };

        DeviceWatcher.Start();
        await WaitFor(() => DeviceWatcher.ConsecutiveFailures >= 1, "first failure");

        var afterFirst = Volatile.Read(ref calls);

        // Automatic polls are throttled while backing off.
        for (var i = 0; i < 5; i++) DeviceWatcher.RequestRefresh(manual: false);
        await Task.Delay(120);
        Assert.Equal(afterFirst, Volatile.Read(ref calls));

        // A user-initiated refresh still goes through.
        DeviceWatcher.RequestRefresh(manual: true);
        await WaitFor(() => Volatile.Read(ref calls) > afterFirst, "manual refresh bypassed backoff");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current) return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}

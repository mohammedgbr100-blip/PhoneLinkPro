using ScrcpyDeck;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 3A — symmetrical shell event lifetime.
///
/// The services expose STATIC events, so a missed unsubscribe roots the window forever and
/// lets a handler run against a dead visual tree. These cover the pairing helper and the
/// dispatch guard; the end-to-end proof that a live MainWindow holds exactly one subscription
/// per event and releases them all on close runs in the STA PLP_SELFTEST harness, because
/// xUnit runs MTA and cannot construct a Window.
/// </summary>
public sealed class ShellSubscriptionsTests
{
    [Fact] // subscribing happens immediately, exactly once
    public void Add_AttachesOnceAndTracksTheSubscription()
    {
        var attached = 0;
        var subscriptions = new ShellSubscriptions();

        subscriptions.Add(() => attached++, () => { });

        Assert.Equal(1, attached);
        Assert.Equal(1, subscriptions.Count);
    }

    [Fact] // disposing releases everything and leaves nothing behind
    public void Dispose_DetachesEverySubscription()
    {
        var detached = 0;
        var subscriptions = new ShellSubscriptions();
        subscriptions.Add(() => { }, () => detached++);
        subscriptions.Add(() => { }, () => detached++);
        subscriptions.Add(() => { }, () => detached++);

        subscriptions.Dispose();

        Assert.Equal(3, detached);
        Assert.Equal(0, subscriptions.Count);
        Assert.True(subscriptions.IsDisposed);
    }

    [Fact] // newest first, so teardown unwinds in the reverse of setup
    public void Dispose_DetachesInReverseOrder()
    {
        var order = new List<int>();
        var subscriptions = new ShellSubscriptions();
        subscriptions.Add(() => { }, () => order.Add(1));
        subscriptions.Add(() => { }, () => order.Add(2));
        subscriptions.Add(() => { }, () => order.Add(3));

        subscriptions.Dispose();

        Assert.Equal(new[] { 3, 2, 1 }, order);
    }

    [Fact] // a second Close must not unsubscribe a handler that is no longer ours
    public void Dispose_IsIdempotent()
    {
        var detached = 0;
        var subscriptions = new ShellSubscriptions();
        subscriptions.Add(() => { }, () => detached++);

        subscriptions.Dispose();
        subscriptions.Dispose();
        subscriptions.Dispose();

        Assert.Equal(1, detached);
    }

    [Fact] // a late Loaded cannot resurrect a closed window's handlers
    public void Add_AfterDisposeIsRefused()
    {
        var attached = 0;
        var subscriptions = new ShellSubscriptions();
        subscriptions.Dispose();

        subscriptions.Add(() => attached++, () => { });

        Assert.Equal(0, attached);
        Assert.Equal(0, subscriptions.Count);
    }

    [Fact] // one failing detach must never strand the others
    public void Dispose_ContinuesAfterAFailingDetach()
    {
        var detached = 0;
        var subscriptions = new ShellSubscriptions();
        subscriptions.Add(() => { }, () => detached++);
        subscriptions.Add(() => { }, () => throw new InvalidOperationException("boom"));
        subscriptions.Add(() => { }, () => detached++);

        subscriptions.Dispose();

        Assert.Equal(2, detached);
        Assert.Equal(0, subscriptions.Count);
    }

    [Fact] // repeated create/dispose cycles never accumulate
    public void RepeatedCycles_DoNotAccumulate()
    {
        var live = 0;
        for (var cycle = 0; cycle < 5; cycle++)
        {
            var subscriptions = new ShellSubscriptions();
            subscriptions.Add(() => live++, () => live--);
            Assert.Equal(1, live);
            subscriptions.Dispose();
            Assert.Equal(0, live);
        }
    }

    [Fact] // null arguments are a programming error, surfaced immediately
    public void Add_RejectsNullDelegates()
    {
        var subscriptions = new ShellSubscriptions();
        Assert.Throws<ArgumentNullException>(() => subscriptions.Add(null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => subscriptions.Add(() => { }, null!));
    }

    // ---------------------------------------------------------------- dispatch guard

    [Fact] // the only state in which a handler may touch the visual tree
    public void CanDispatch_OnlyWhenTheWindowIsLiveAndNothingIsShuttingDown()
        => Assert.True(ShellSubscriptions.CanDispatch(false, false, false));

    [Theory] // a static event can still fire after close, during app shutdown, or after the
             // dispatcher begins shutting down — none of them may reach the visual tree
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void CanDispatch_RefusesEveryTerminalState(
        bool closed, bool appShuttingDown, bool dispatcherShuttingDown)
        => Assert.False(ShellSubscriptions.CanDispatch(closed, appShuttingDown, dispatcherShuttingDown));

    // ---------------------------------------------------------------- wiring guards
    // Source-level, and honestly so: they guard the symmetry the STA harness then proves.

    [Fact] // every shell subscription goes through the paired helper, not a bare +=
    public void MainWindow_RoutesEverySubscriptionThroughTheHelper()
    {
        var code = TestPaths.ReadRepoFile("MainWindow.xaml.cs");
        foreach (var evt in new[]
                 {
                     "AppState.CurrentDeviceChanged", "DeviceWatcher.DevicesChanged",
                     "ScrcpyService.SessionsChanged", "Toast.Requested",
                 })
        {
            Assert.Contains($"{evt} +=", code);
            Assert.Contains($"{evt} -=", code);
        }
        Assert.Contains("_subscriptions.Dispose()", code);
    }

    [Fact] // handlers are named methods, guarded, and released in one teardown point
    public void MainWindow_HasASingleGuardedTeardownPoint()
    {
        var code = TestPaths.ReadRepoFile("MainWindow.xaml.cs");
        Assert.Contains("private void OnShellClosed", code);
        Assert.Contains("CanUpdateShell", code);
        Assert.Contains("ShellSubscriptions.CanDispatch", code);
    }
}

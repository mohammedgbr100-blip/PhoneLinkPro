using System.Diagnostics;
using System.Windows.Threading;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Public-release audit — regression proof for the intermittent
/// RealLogDirectory_IsUnchangedAcrossACompleteSelfTestProcess failure.
///
/// <para>Root cause: the PLP_SELFTEST ordering harness waited for an async continuation with a
/// budget of 200 dispatcher pumps. The continuation first hops through the thread pool
/// (RunContinuationsAsynchronously), and while it is in that hop the dispatcher queue is empty,
/// so 200 pumps take only milliseconds; under CPU load the budget ran out before the
/// continuation was even queued. <see cref="DispatcherWait"/> replaces that budget with a
/// signal-driven nested frame bounded by wall-clock time.</para>
///
/// <para>Each test runs on its own STA thread with its own Dispatcher (no WPF Application is
/// created, so nothing process-wide is touched and the class can run in parallel).</para>
/// </summary>
public sealed class DispatcherWaitTests
{
    private static T OnDispatcherThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "dispatcher test thread hung");
        if (error != null) throw new Xunit.Sdk.XunitException("dispatcher thread failed: " + error);
        return result;
    }

    /// <summary>The old harness's bound, reproduced verbatim for the comparison test below.</summary>
    private static bool OldPumpCountWait(Func<bool> done, int maxIterations = 200)
    {
        for (var i = 0; i < maxIterations && !done(); i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        return done();
    }

    [Fact] // the failure mode itself: a signal delayed on the thread pool while the dispatcher is idle
    public void SignalDelayedOnTheThreadPool_IsStillObserved()
    {
        var (signaled, elapsed) = OnDispatcherThread(() =>
        {
            var sw = Stopwatch.StartNew();
            var ok = DispatcherWait.UntilSignaled(
                signal => Task.Run(async () => { await Task.Delay(300); signal(); }),
                TimeSpan.FromSeconds(10));
            return (ok, sw.Elapsed);
        });

        Assert.True(signaled);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(250), $"returned before the signal ({elapsed})");
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"did not return promptly after the signal ({elapsed})");
    }

    [Fact] // documents WHY the old bound was wrong: on an idle dispatcher 200 pumps cannot even
           // span a 2 s thread-pool delay (the new wait handles any delay up to its timeout)
    public void OldTwoHundredPumpBudget_GivesUpOnADelayedSignal()
    {
        var observed = OnDispatcherThread(() =>
        {
            var done = 0;
            _ = Task.Run(async () => { await Task.Delay(2000); Volatile.Write(ref done, 1); });
            return OldPumpCountWait(() => Volatile.Read(ref done) == 1);
        });

        Assert.False(observed);
    }

    [Fact] // a genuine hang still fails, bounded by the timeout rather than blocking forever
    public void SignalNeverRaised_ReturnsFalseAfterTheTimeout()
    {
        var (signaled, elapsed) = OnDispatcherThread(() =>
        {
            var sw = Stopwatch.StartNew();
            var ok = DispatcherWait.UntilSignaled(_ => { }, TimeSpan.FromMilliseconds(200));
            return (ok, sw.Elapsed);
        });

        Assert.False(signaled);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(150), $"timed out too early ({elapsed})");
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"timeout was not honored ({elapsed})");
    }

    [Fact] // a signal raised synchronously by the trigger needs no wait at all
    public void SignalRaisedSynchronously_ReturnsImmediately()
    {
        var (signaled, elapsed) = OnDispatcherThread(() =>
        {
            var sw = Stopwatch.StartNew();
            var ok = DispatcherWait.UntilSignaled(signal => signal(), TimeSpan.FromSeconds(10));
            return (ok, sw.Elapsed);
        });

        Assert.True(signaled);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"waited although already signaled ({elapsed})");
    }

    [Fact] // work posted to the dispatcher keeps running while the wait is parked
    public void DispatcherKeepsProcessingPostedWorkWhileWaiting()
    {
        var (signaled, ran) = OnDispatcherThread(() =>
        {
            var ranOnDispatcher = false;
            var dispatcher = Dispatcher.CurrentDispatcher;
            var ok = DispatcherWait.UntilSignaled(signal =>
                Task.Run(async () =>
                {
                    await Task.Delay(100);
                    // the continuation pattern under test: posted back to the dispatcher, which
                    // raises the signal from inside its own dispatcher operation
                    await dispatcher.InvokeAsync(() => { ranOnDispatcher = true; signal(); });
                }), TimeSpan.FromSeconds(10));
            return (ok, ranOnDispatcher);
        });

        Assert.True(signaled);
        Assert.True(ran);
    }

    [Fact] // UntilCompleted: a task faulted later on the thread pool is observed as completed
    public void UntilCompleted_ObservesALateFault()
    {
        var completed = OnDispatcherThread(() =>
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(async () => { await Task.Delay(300); tcs.SetException(new InvalidOperationException("late")); });
            var ok = DispatcherWait.UntilCompleted(tcs.Task, TimeSpan.FromSeconds(10));
            _ = tcs.Task.Exception; // observe
            return ok && tcs.Task.IsFaulted;
        });

        Assert.True(completed);
    }

    // ---------------------------------------------------------------- WaitBudget
    // Independent review of the audit fix: with a 10 s wall-clock bound per wait, a GENUINE hang
    // made every remaining wait of the run spend its full 10 s, so a hung PLP_SELFTEST outlasted
    // its caller's process timeout and lost its named-assertion report. WaitBudget keeps the full
    // timeout until the first wait times out, then gives every later wait only AfterHang.

    [Fact]
    public void WaitBudget_GivesTheFullTimeoutUntilAWaitTimesOut()
    {
        var budget = new WaitBudget(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), budget.Next);
        Assert.True(budget.Record(true));
        Assert.False(budget.HangObserved);
        Assert.Equal(TimeSpan.FromSeconds(10), budget.Next);
    }

    [Fact]
    public void WaitBudget_AfterOneTimeout_EveryLaterWaitIsShort()
    {
        var budget = new WaitBudget(TimeSpan.FromSeconds(10));
        Assert.False(budget.Record(false));
        Assert.True(budget.HangObserved);
        Assert.Equal(WaitBudget.AfterHang, budget.Next);
        Assert.True(budget.Record(true)); // a later success does not restore the full budget
        Assert.Equal(WaitBudget.AfterHang, budget.Next);
        Assert.True(WaitBudget.AfterHang <= TimeSpan.FromSeconds(1));
    }

    [Fact] // the point of it: a hung run ends in about one timeout, not one timeout per wait
    public void WaitBudget_FiveHungWaitsTakeAboutOneTimeoutNotFive()
    {
        var elapsed = OnDispatcherThread(() =>
        {
            var budget = new WaitBudget(TimeSpan.FromMilliseconds(1500));
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 5; i++)
                budget.Record(DispatcherWait.UntilSignaled(_ => { }, budget.Next));
            return sw.Elapsed;
        });

        // Without the budget: 5 x 1.5 s = 7.5 s. With it: 1.5 s + 4 x AfterHang.
        Assert.True(elapsed < TimeSpan.FromSeconds(6), $"took {elapsed.TotalSeconds:0.0}s");
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(1400), $"took only {elapsed.TotalMilliseconds:0}ms");
    }
}

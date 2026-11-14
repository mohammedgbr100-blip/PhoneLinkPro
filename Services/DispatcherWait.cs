using System.Windows.Threading;

namespace ScrcpyDeck.Services;

/// <summary>
/// Self-test synchronization primitive (public-release audit): runs the current thread's
/// dispatcher until a causal signal arrives, bounded by WALL-CLOCK time — never by a pump count.
///
/// <para>
/// Why not a pump count (the Batch 5A design this replaces): the operations the self-tests wait
/// for resume through a TaskCompletionSource created with RunContinuationsAsynchronously, so the
/// continuation first hops through a thread-pool work item and only then is posted to this
/// dispatcher. While that hop is pending the dispatcher queue is EMPTY, so each pump returns in
/// microseconds and a budget of N pumps is really a budget of a few milliseconds. Under CPU load
/// (the full xUnit suite runs test classes in parallel next to the PLP_SELFTEST child process)
/// the hop can take longer than that, the budget is exhausted before the continuation is even
/// queued, and a correct operation is reported as "never settled" — the intermittent
/// RealLogDirectory_IsUnchangedAcrossACompleteSelfTestProcess failure
/// (dashboard-ordering-stress 1091/1100, both scenarios failing in the same iteration).
/// </para>
///
/// <para>
/// This helper instead parks in one nested dispatcher frame — the dispatcher keeps processing
/// all posted work normally — and leaves it the moment the signal is raised (from any thread),
/// or when the timeout elapses so a genuine hang still fails instead of blocking forever.
/// </para>
/// </summary>
internal static class DispatcherWait
{
    /// <summary>Generous bound for one wait: only a genuine hang ever reaches it.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Calls <paramref name="start"/> with a thread-safe, idempotent signal callback, then runs
    /// the current thread's dispatcher until that callback has been invoked or
    /// <paramref name="timeout"/> elapses. Returns whether the signal was observed. Must be
    /// called on a thread that owns a dispatcher (the PLP_SELFTEST UI thread, or an STA test
    /// thread).
    /// </summary>
    internal static bool UntilSignaled(Action<Action> start, TimeSpan timeout)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var signaled = 0;

        void Signal()
        {
            if (Interlocked.Exchange(ref signaled, 1) == 1) return;
            // Callable from any thread; ends the frame once the current dispatcher operation
            // (typically the very finally block that raised the signal) has finished.
            dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false));
        }

        start(Signal);
        if (Volatile.Read(ref signaled) == 1) return true;   // signaled synchronously inside start

        var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = timeout };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }

        return Volatile.Read(ref signaled) == 1;
    }

    /// <summary>
    /// Runs the current thread's dispatcher until <paramref name="task"/> has completed (success
    /// or fault) or <paramref name="timeout"/> elapses. Returns <c>task.IsCompleted</c>.
    /// </summary>
    internal static bool UntilCompleted(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted) return true;
        UntilSignaled(signal => task.ContinueWith(_ => signal(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default), timeout);
        return task.IsCompleted;
    }
}

/// <summary>
/// Per-run wait budget for a self-test harness: every wait gets the full timeout until one of
/// them actually times out. That proves a genuine hang, and from then on every later wait of the
/// same run gets only <see cref="AfterHang"/>. A hung run therefore ends in about one timeout,
/// with its named failures reported, instead of spending the full timeout on each remaining wait
/// and outliving its caller's process deadline (independent review of the audit fix).
/// </summary>
internal sealed class WaitBudget
{
    internal static readonly TimeSpan AfterHang = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _normal;

    internal WaitBudget(TimeSpan normal) => _normal = normal;

    /// <summary>True once any wait recorded through this budget timed out.</summary>
    internal bool HangObserved { get; private set; }

    /// <summary>The timeout to use for the next wait.</summary>
    internal TimeSpan Next => HangObserved ? AfterHang : _normal;

    /// <summary>Records a wait's outcome and passes it through.</summary>
    internal bool Record(bool settled)
    {
        if (!settled) HangObserved = true;
        return settled;
    }
}

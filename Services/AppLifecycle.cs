using System.Diagnostics;

namespace ScrcpyDeck.Services;

/// <summary>
/// Bounded, deterministic application shutdown.
/// <para>
/// Every step is independent and time-boxed: one failing step never prevents the rest from
/// running, and no step can hang the exit. Failures are logged, never surfaced as a chain of
/// message boxes.
/// </para>
/// </summary>
public static class AppLifecycle
{
    /// <summary>Whole-sequence ceiling. Exit proceeds even if a step is still working.</summary>
    internal static TimeSpan ShutdownBudget { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>Per-step ceiling.</summary>
    internal static TimeSpan StepBudget { get; set; } = TimeSpan.FromSeconds(3);

    private static int _shutdownStarted;

    /// <summary>True once shutdown has begun; new work must not start.</summary>
    public static bool IsShuttingDown => Volatile.Read(ref _shutdownStarted) == 1;

    /// <summary>Test hook: return to the "running normally" state.</summary>
    internal static void ResetForTests() => Volatile.Write(ref _shutdownStarted, 0);

    /// <summary>
    /// Runs the shutdown sequence exactly once. Safe to call from a window Closing handler
    /// and from Application.Exit.
    /// </summary>
    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 1) return;

        var sw = Stopwatch.StartNew();
        Log.Info("Shutdown: begin");

        // 1) Refuse new sessions immediately, before anything else can race us.
        Step(sw, "block new sessions", ScrcpyService.BeginShutdown);

        // 2) Stop the poll timer. Polling semantics are untouched; only scheduling stops.
        Step(sw, "stop device watcher", DeviceWatcher.Stop);

        // 3) Terminate every mirror session we own.
        Step(sw, "stop mirror sessions", ScrcpyService.StopAll);

        // 4) Stop the ADB server only when we can prove it is ours.
        Step(sw, "adb server policy", StopOwnedAdbServer);

        // 5) Hand back the single-instance mutex. Deliberately NOT routed through Step():
        //    Step runs on a pool thread, but ReleaseMutex must run on the acquiring (UI)
        //    thread. Release() is instant, never throws, and if this process dies without
        //    reaching it the abandoned-mutex recovery in SingleInstance.Acquire covers it.
        SingleInstance.Release();

        sw.Stop();
        Log.Info($"Shutdown: complete in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Runs one cleanup step under a time bound, swallowing and logging any failure so the
    /// remaining steps still execute. Each step gets whichever is smaller: the per-step
    /// budget, or what is left of the whole-sequence budget.
    /// </summary>
    private static void Step(Stopwatch elapsed, string name, Action action)
    {
        var remaining = ShutdownBudget - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            Log.Warn($"Shutdown: skipped '{name}' (budget exhausted)");
            return;
        }

        var allowance = remaining < StepBudget ? remaining : StepBudget;

        try
        {
            var task = Task.Run(action);
            if (!task.Wait(allowance))
            {
                Log.Warn($"Shutdown: step '{name}' exceeded its budget; continuing");
                return;
            }
            if (task.IsFaulted)
                Log.Error($"Shutdown: step '{name}' failed", task.Exception?.GetBaseException());
        }
        catch (Exception ex)
        {
            Log.Error($"Shutdown: step '{name}' threw", ex);
        }
    }

    /// <summary>
    /// Applies the ADB ownership policy. We only ever ask OUR adb to kill its own server, and
    /// only when the running server is provably our bundled binary.
    /// </summary>
    internal static void StopOwnedAdbServer()
    {
        var decision = AdbServerOwnership.Evaluate();
        switch (decision)
        {
            case AdbOwnershipDecision.OwnedByUs:
                Log.Info("Shutdown: ADB server is our bundled bridge; stopping it.");
                try
                {
                    // Bounded: never let a wedged adb hold up the exit.
                    AdbService.KillServerAsync().Wait(StepBudget);
                }
                catch (Exception ex) { Log.Warn("Shutdown: kill-server failed: " + ex.Message); }
                break;

            case AdbOwnershipDecision.NotOurs:
                Log.Info("Shutdown: ADB server belongs to another tool; leaving it running.");
                break;

            case AdbOwnershipDecision.Ambiguous:
                Log.Warn("Shutdown: ADB server ownership could not be proven; leaving it running.");
                break;

            case AdbOwnershipDecision.NothingToStop:
                Log.Info("Shutdown: no ADB server running.");
                break;
        }
    }
}

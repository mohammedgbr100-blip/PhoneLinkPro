using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ScrcpyDeck.Models;
using ScrcpyDeck.Views;

namespace ScrcpyDeck.Services;

/// <summary>
/// Proves DashboardView never applies an older observed device snapshot after a newer one,
/// even when the older snapshot was left queued in _missedUpdate past the busy period that
/// queued it (Batch 3D — the ordering gap Batch 3C's review deferred: RefreshAsync's own busy
/// period never drained a snapshot queued behind it, so a later, unrelated caller's own
/// coalescing loop could dequeue and reapply that stale snapshot after something genuinely
/// newer had already been applied).
///
/// <para>
/// AdbService.DiscoverDevicesAsync has no injectable seam and must not gain one just for this
/// test, so DashboardView.DiscoverDevicesOverrideForTests stands in for it here (mirroring
/// Batch 3C's EnrichOverrideForTests): a TaskCompletionSource pauses RefreshAsync's discovery at
/// a controlled point, so the race is driven deterministically — no sleeps, no timing — and a
/// real adb/bridge process is never started. Scenarios C-F use ApplyDevicesAsync's existing
/// DeviceWatcher.DiscoveryProvider seam exclusively and never touch RefreshAsync (Loaded's own
/// implicit refresh is suppressed via SuppressRefreshForHarness for the whole run; Scenarios A/B
/// trigger RefreshAsync explicitly via the public OnShown(), which is not gated by that switch).
/// </para>
///
/// <para>
/// Batch 5A: every TaskCompletionSource used below (discoverTcs/holdTcs/tcs in Scenarios A-D)
/// is created with RunContinuationsAsynchronously, so its continuation — the rest of
/// ApplyDevicesCoreAsync/ApplyDevicesAsync after the paused await — is genuinely posted back
/// onto this thread rather than resumed synchronously inside the SetResult/SetException call
/// that unblocks it. A FIXED dispatcher-pump count (the previous design) cannot bound that
/// hand-off deterministically: how many pumps it takes for the posted continuation to actually
/// run is a real cross-thread race, which is exactly why Scenarios C and E were the two
/// occasionally-flaky ones (both call SetResult/SetException on a still-in-flight enrichment
/// TCS, immediately followed by an assertion on the result). DashboardView.OperationSettledForTests
/// (fired at the exact end of the finally block that releases _busy for a still-current
/// generation — i.e. only once any drain that operation owned has itself finished) replaces
/// the guess with a real causal signal: <see cref="RunAndWaitForSettle"/> pumps only as long as
/// it takes for that specific operation to actually settle, bounded so a genuine hang still
/// fails fast. Inspection of ApplyDevicesCoreAsync/ApplyDevicesAsync/DrainQueuedUpdatesAsync
/// found the production ownership/generation/sequence logic already correct — the entry guard
/// (`sequence &lt;= _lastAppliedSequence` is rejected), the per-generation drain, and the
/// single-slot coalesced queue together guarantee the newer snapshot always wins once the test
/// stops racing its own assertions against an unresolved continuation. This repair is therefore
/// harness-only; no production ownership/generation logic changed.
/// </para>
/// </summary>
internal static class DashboardMissedUpdateOrderingSelfTest
{
    internal sealed record Result(bool Passed, int Total, int Failed, string Report);

    internal static Result Run()
    {
        var lines = new StringBuilder();
        var failures = new List<string>();
        var total = 0;

        void Expect(string name, bool condition)
        {
            total++;
            if (!condition) failures.Add(name);
        }

        var wasSuppressed = DashboardView.SuppressRefreshForHarness;
        var wasEnrichOverride = DashboardView.EnrichOverrideForTests;
        var wasDiscoverOverride = DashboardView.DiscoverDevicesOverrideForTests;
        var appStateBefore = AppState.CurrentDevice;
        DashboardView.SuppressRefreshForHarness = true;
        try
        {
            // ---- A: confirmed defect — S observed while RefreshAsync is busy, T observed
            //         after RefreshAsync completes; T must win, S must never apply after it ----
            {
                DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;
                var discoverTcs = new TaskCompletionSource<DeviceDiscoveryResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.DiscoverDevicesOverrideForTests = () => discoverTcs.Task;

                var view = new DashboardView();
                Load(view);
                view.OnShown();   // triggers RefreshAsync, which pauses at discoverTcs.Task

                var s = Authorized("batch3d-queued-s");
                var startEx = QueueDeviceEvent(new List<AdbDevice> { s });
                Expect("A: no exception queuing S while refresh is busy", startEx == null);
                Expect("A: S is actually queued behind the busy refresh",
                    view.MissedUpdateForTests?.Devices.FirstOrDefault()?.Serial == s.Serial);

                var r = Authorized("batch3d-refresh-result");
                var settledR = RunAndWaitForSettle(
                    () => discoverTcs.SetResult(DeviceDiscoveryResult.Success(new List<AdbDevice> { r })));
                Expect("A: the refresh (R applied, then S drained) settled", settledR);

                var t = Authorized("batch3d-newer-t");
                var (laterEx, settledT) = DeliverAndWaitForSettle(new List<AdbDevice> { t });
                Expect("A: no exception delivering T", laterEx == null);
                Expect("A: T's delivery settled", settledT);

                // Batch 4A: a solo device becomes the HERO, excluded from DeviceList — see
                // DashboardView.HeroDeviceForTests' own remarks.
                Expect($"A: the final hero represents T, not S; expected={t.Serial} " +
                       $"actual={view.HeroDeviceForTests?.Serial ?? "<null>"}",
                    ReferenceEquals(view.HeroDeviceForTests, t));
                Expect("A: AppState.CurrentDevice represents T",
                    ReferenceEquals(AppState.CurrentDevice, t));

                lines.AppendLine($"  A: final device={view.HeroDeviceForTests?.Serial ?? "?"}, " +
                                 $"AppState={AppState.CurrentDevice?.Serial ?? "<null>"}");
                Unload(view);
                DashboardView.DiscoverDevicesOverrideForTests = null;
            }

            // ---- B: a single queued update must not be lost, even with nothing newer behind
            //         it ----
            {
                DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;
                var discoverTcs = new TaskCompletionSource<DeviceDiscoveryResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.DiscoverDevicesOverrideForTests = () => discoverTcs.Task;

                var view = new DashboardView();
                Load(view);
                view.OnShown();

                var s = Authorized("batch3d-lone-queued");
                var startEx = QueueDeviceEvent(new List<AdbDevice> { s });
                Expect("B: no exception queuing the lone update", startEx == null);

                var r = Authorized("batch3d-lone-refresh-result");
                var settled = RunAndWaitForSettle(
                    () => discoverTcs.SetResult(DeviceDiscoveryResult.Success(new List<AdbDevice> { r })));
                Expect("B: the refresh (R applied, then the lone update drained) settled", settled);

                Expect("B: the lone queued update eventually became the final result",
                    ReferenceEquals(view.HeroDeviceForTests, s));

                lines.AppendLine($"  B: final device={view.HeroDeviceForTests?.Serial ?? "?"}");
                Unload(view);
                DashboardView.DiscoverDevicesOverrideForTests = null;
            }

            // ---- C: multiple queued updates coalesce to the latest; earlier ones never
            //         apply ----
            {
                var (t, f, summary) = ScenarioC("batch3d");
                total += t;
                failures.AddRange(f);
                lines.AppendLine("  " + summary);
            }

            // ---- D: an old lifecycle's queued update cannot survive into a new one ----
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.EnrichOverrideForTests = _ => tcs.Task;
                var view = new DashboardView();
                Load(view);

                var holder = Authorized("batch3d-gen-holder");
                var startEx = RaiseDeviceEvent(new List<AdbDevice> { holder });
                Expect("D: no exception starting the busy-holding operation", startEx == null);

                var queued = Authorized("batch3d-gen-queued");
                var queueEx = QueueDeviceEvent(new List<AdbDevice> { queued });
                Expect("D: no exception queuing an update in the outgoing generation", queueEx == null);

                Unload(view);          // generation G -> G+1; _missedUpdate cleared, busy released
                // The abandoned holder's continuation resumes for a now-stale generation, so
                // OperationSettledForTests structurally never fires for it (ApplyDevicesAsync's
                // finally only invokes it when IsCurrent(generation) is still true) — there is
                // nothing to wait FOR here by design, only room to let the abandoned
                // continuation run its course and prove it changes nothing.
                tcs.SetResult();
                PumpWithoutWaiting();

                DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;
                Load(view);            // fresh lifecycle, generation G+1

                var current = Authorized("batch3d-gen-current");
                var (currentEx, settledCurrent) = DeliverAndWaitForSettle(new List<AdbDevice> { current });
                Expect("D: no exception delivering the current-generation update", currentEx == null);
                Expect("D: the current-generation delivery settled", settledCurrent);

                Expect("D: the old queued update was discarded; the current update won",
                    ReferenceEquals(view.HeroDeviceForTests, current));

                lines.AppendLine($"  D: final device={view.HeroDeviceForTests?.Serial ?? "?"}");
                Unload(view);
            }

            // ---- E: an obsolete queued snapshot cannot overwrite a newer update after the
            //         active operation fails ----
            {
                var (t, f, summary) = ScenarioE("batch3d");
                total += t;
                failures.AddRange(f);
                lines.AppendLine("  " + summary);
            }

            // ---- F: regression preservation — subscription balance and a single normal
            //         update still apply exactly once ----
            {
                var baseline = SubscriberCount();
                var view = new DashboardView();
                Load(view);
                Expect("F: subscriber count after Loaded", SubscriberCount() == baseline + 1);

                DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;
                var live = Authorized("batch3d-normal-live");
                var (ex, settled) = DeliverAndWaitForSettle(new List<AdbDevice> { live });
                Expect("F: no exception on a normal, current operation", ex == null);
                Expect("F: the normal operation settled", settled);

                var appliedOnce = ReferenceEquals(view.HeroDeviceForTests, live);
                Expect("F: a single normal watcher event still updates the hero exactly once", appliedOnce);

                Unload(view);
                Expect("F: subscriber count after Unloaded returns to baseline",
                    SubscriberCount() == baseline);

                lines.AppendLine($"  F: subscriber balance ok, normal update applied once={appliedOnce}");
            }

            // ---- G: repair batch 5B (§6) — a throwing OperationSettledForTests hook must not
            //         block busy release, and must not block a SUCCESSFUL operation's own drain
            //         of whatever coalesced behind it. Invokes ApplyDevicesAsync directly via
            //         reflection (bypassing RunAndWaitForSettle, which now itself fails loudly on
            //         ANY hook exception — appropriate for every other scenario, but this one
            //         deliberately installs a throwing hook and inspects the result). ----
            {
                var applyMethod = typeof(DashboardView).GetMethod(
                    "ApplyDevicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

                var enrichTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.EnrichOverrideForTests = _ => enrichTcs.Task;
                var view = new DashboardView();
                Load(view);

                var holder = Authorized("batch5b-hook-g-holder");
                var holderTask = (Task)applyMethod.Invoke(view, new object[] { new List<AdbDevice> { holder } })!;
                Expect("G: the holder operation owns the busy gate", view.IsBusyForTests);

                var queued = Authorized("batch5b-hook-g-queued");
                var queuedTask = (Task)applyMethod.Invoke(view, new object[] { new List<AdbDevice> { queued } })!;
                Expect("G: the second update coalesced behind the busy operation instead of erroring",
                    queuedTask.IsCompletedSuccessfully);
                Expect("G: the coalesced update is actually queued; queued=" + Serial(view.MissedUpdateForTests),
                    Serial(view.MissedUpdateForTests) == queued.Serial);

                var hookInvoked = false;
                DashboardView.OperationSettledForTests = () =>
                {
                    hookInvoked = true;
                    throw new InvalidOperationException("batch5b-hook-g-failure");
                };
                DashboardView.OperationSettledHookExceptionForTests = null;

                enrichTcs.SetResult();
                var settledG = PumpUntilComplete(holderTask);
                Expect("G: the holder operation completed despite the throwing hook (did not hang)", settledG);
                Expect("G: the holder operation completed SUCCESSFULLY (the hook's own failure did " +
                       "not turn a successful operation into a faulted one)",
                    holderTask.IsCompletedSuccessfully);
                Expect("G: the throwing hook was actually invoked and its own exception was captured, " +
                       "not silently lost",
                    hookInvoked && DashboardView.OperationSettledHookExceptionForTests?.Message
                        == "batch5b-hook-g-failure");
                Expect("G: busy was released despite the throwing hook; busy=" + view.IsBusyForTests,
                    !view.IsBusyForTests);
                Expect("G: the coalesced update was still drained and applied despite the throwing " +
                       $"hook; actual={view.HeroDeviceForTests?.Serial ?? "<null>"}",
                    ReferenceEquals(view.HeroDeviceForTests, queued));

                DashboardView.OperationSettledForTests = null;
                DashboardView.OperationSettledHookExceptionForTests = null;
                lines.AppendLine($"  G: hook invoked={hookInvoked}, busy released, " +
                                 $"queued update applied={ReferenceEquals(view.HeroDeviceForTests, queued)}");
                Unload(view);
            }

            // ---- H: repair batch 5B (§6) — a throwing OperationSettledForTests hook must never
            //         mask the real operation's OWN failure with the hook's own exception. ----
            {
                var applyMethod = typeof(DashboardView).GetMethod(
                    "ApplyDevicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

                var enrichTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.EnrichOverrideForTests = _ => enrichTcs.Task;
                var view = new DashboardView();
                Load(view);

                var failing = Authorized("batch5b-hook-h-failing");
                var failingTask = (Task)applyMethod.Invoke(view, new object[] { new List<AdbDevice> { failing } })!;
                Expect("H: the failing operation owns the busy gate", view.IsBusyForTests);

                var operationFailure = new InvalidOperationException("batch5b-real-operation-failure");
                var hookInvoked = false;
                DashboardView.OperationSettledForTests = () =>
                {
                    hookInvoked = true;
                    throw new InvalidOperationException("batch5b-hook-h-failure");
                };
                DashboardView.OperationSettledHookExceptionForTests = null;

                enrichTcs.SetException(operationFailure);
                var settledH = PumpUntilComplete(failingTask);
                Expect("H: the operation completed despite the throwing hook (did not hang)", settledH);

                var observedFault = failingTask.IsFaulted ? failingTask.Exception?.GetBaseException() : null;
                Expect("H: the ORIGINAL operation failure propagated, not the hook's own exception; " +
                       $"observed={observedFault?.GetType().Name ?? "<none>"}: {observedFault?.Message ?? ""}",
                    observedFault is InvalidOperationException oe && oe.Message == operationFailure.Message);
                Expect("H: the throwing hook was actually invoked and its own exception was captured, " +
                       "not silently lost",
                    hookInvoked && DashboardView.OperationSettledHookExceptionForTests?.Message
                        == "batch5b-hook-h-failure");
                Expect("H: busy was released despite the throwing hook; busy=" + view.IsBusyForTests,
                    !view.IsBusyForTests);

                DashboardView.OperationSettledForTests = null;
                DashboardView.OperationSettledHookExceptionForTests = null;
                lines.AppendLine($"  H: hook invoked={hookInvoked}, original failure preserved, busy released");
                Unload(view);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"unexpected failure: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DashboardView.SuppressRefreshForHarness = wasSuppressed;
            DashboardView.EnrichOverrideForTests = wasEnrichOverride;
            DashboardView.DiscoverDevicesOverrideForTests = wasDiscoverOverride;
            AppState.SetCurrent(appStateBefore);
            DeviceWatcher.ResetForTests();
        }

        var passed = failures.Count == 0;
        var report = $"dashboard-ordering: {total - failures.Count}/{total} ordering assertions passed" +
                     Environment.NewLine + lines.ToString().TrimEnd() +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));

        return new Result(passed, total, failures.Count, report);
    }

    /// <summary>
    /// Batch 5A ordering stress gate: repeats exactly Scenarios C and E — the two that raced a
    /// synchronous assertion against an asynchronous TaskCompletionSource continuation — many
    /// times with full per-iteration isolation (a fresh DashboardView and fresh
    /// TaskCompletionSources each time), using the same deterministic
    /// <see cref="RunAndWaitForSettle"/> synchronization as <see cref="Run"/>. Bounded (no real
    /// sleeps, no dependence on CPU speed beyond the per-operation dispatcher-pump cap), and
    /// every failure is reported with its scenario, iteration and the exact expected/actual/
    /// generation-owner state the failing assertion carries in its own name.
    /// </summary>
    internal static Result RunOrderingStressGate(int iterations = 50)
    {
        var failures = new List<string>();
        var total = 0;
        // Repair (independent review, §12): tracks how many iterations actually RAN to
        // completion, distinct from `iterations` (how many were requested) — an exception
        // partway through iteration i must never let the report claim "over {iterations}
        // iterations" when only some smaller number were actually attempted/completed.
        var completedIterations = 0;

        var wasSuppressed = DashboardView.SuppressRefreshForHarness;
        var wasEnrichOverride = DashboardView.EnrichOverrideForTests;
        var wasDiscoverOverride = DashboardView.DiscoverDevicesOverrideForTests;
        var appStateBefore = AppState.CurrentDevice;
        DashboardView.SuppressRefreshForHarness = true;
        try
        {
            for (var i = 0; i < iterations; i++)
            {
                var (tc, fc, _) = ScenarioC($"stress-c-iter{i}");
                total += tc;
                failures.AddRange(fc.Select(f => $"iteration {i}, scenario C: {f}"));

                var (te, fe, _) = ScenarioE($"stress-e-iter{i}");
                total += te;
                failures.AddRange(fe.Select(f => $"iteration {i}, scenario E: {f}"));

                completedIterations = i + 1;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"unexpected failure during iteration {completedIterations} of {iterations} " +
                         $"({completedIterations} iteration(s) fully completed before this): " +
                         $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DashboardView.SuppressRefreshForHarness = wasSuppressed;
            DashboardView.EnrichOverrideForTests = wasEnrichOverride;
            DashboardView.DiscoverDevicesOverrideForTests = wasDiscoverOverride;
            AppState.SetCurrent(appStateBefore);
            DeviceWatcher.ResetForTests();
        }

        // An incomplete run (cut short by an exception) is never silently reported as a clean
        // pass over the full requested count, even when zero ordering assertions themselves
        // failed before the exception hit.
        var passed = failures.Count == 0 && completedIterations == iterations;
        var report =
            $"dashboard-ordering-stress: {total - failures.Count}/{total} assertions passed " +
            $"over {completedIterations}/{iterations} attempted iterations of scenarios C and E" +
            (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));

        return new Result(passed, total, failures.Count, report);
    }

    // ---------------------------------------------------------------- scenario bodies (shared
    // by Run(), which executes each once, and RunOrderingStressGate(), which repeats them)

    /// <summary>
    /// Scenario C: multiple watcher snapshots queued behind a busy operation coalesce to the
    /// latest; only that latest snapshot is ever applied. Proves, in order: (1) the initial
    /// operation owns the busy gate; (2) each of S1/S2/S3 replaces the previous in the
    /// single-slot queue as it arrives; (3-5) releasing the busy operation causes it — and its
    /// own drain of the coalesced queue — to settle, observed via a real causal signal rather
    /// than a guessed pump count; (6) the final hero is S3; (7) nothing repaints afterward.
    /// </summary>
    private static (int Total, List<string> Failures, string Summary) ScenarioC(string tag)
    {
        var total = 0;
        var failures = new List<string>();
        void Expect(string name, bool ok) { total++; if (!ok) failures.Add(name); }

        var holdTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DashboardView.EnrichOverrideForTests = _ => holdTcs.Task;
        var view = new DashboardView();
        Load(view);

        var holder = Authorized($"{tag}-busy-holder");
        var startEx = RaiseDeviceEvent(new List<AdbDevice> { holder });
        Expect($"{tag} C: no exception starting the busy-holding operation", startEx == null);
        Expect($"{tag} C: the holder operation owns the busy gate; busy={view.IsBusyForTests}",
            view.IsBusyForTests);

        var s1 = Authorized($"{tag}-s1");
        var s2 = Authorized($"{tag}-s2");
        var s3 = Authorized($"{tag}-s3");
        var ex1 = QueueDeviceEvent(new List<AdbDevice> { s1 });
        Expect($"{tag} C: S1 coalesced into the queue; queued={Serial(view.MissedUpdateForTests)}",
            Serial(view.MissedUpdateForTests) == s1.Serial);
        var ex2 = QueueDeviceEvent(new List<AdbDevice> { s2 });
        Expect($"{tag} C: S2 replaced S1 in the queue; queued={Serial(view.MissedUpdateForTests)}",
            Serial(view.MissedUpdateForTests) == s2.Serial);
        var ex3 = QueueDeviceEvent(new List<AdbDevice> { s3 });
        Expect($"{tag} C: S3 replaced S2 in the queue; queued={Serial(view.MissedUpdateForTests)}",
            Serial(view.MissedUpdateForTests) == s3.Serial);
        Expect($"{tag} C: no exception queuing S1/S2/S3", ex1 == null && ex2 == null && ex3 == null);

        DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;
        var settled = RunAndWaitForSettle(holdTcs.SetResult);
        Expect($"{tag} C: the holder operation (and its drain of S3) settled", settled);

        Expect($"{tag} C: only S3 (the latest) was ever applied; expected={s3.Serial} " +
               $"actual={view.HeroDeviceForTests?.Serial ?? "<null>"} busy={view.IsBusyForTests} " +
               $"queued={Serial(view.MissedUpdateForTests)}",
            ReferenceEquals(view.HeroDeviceForTests, s3));
        Expect($"{tag} C: the queue is fully drained; queued={Serial(view.MissedUpdateForTests)}",
            view.MissedUpdateForTests == null);
        Expect($"{tag} C: busy was released; busy={view.IsBusyForTests}", !view.IsBusyForTests);

        // Extra guard against a delayed repaint: pump a further bounded, non-waiting window
        // (nothing should be pending, so no further settle is expected) and confirm S3 still
        // holds.
        PumpWithoutWaiting();
        Expect($"{tag} C: S3 still holds after further pumping (no obsolete repaint); " +
               $"actual={view.HeroDeviceForTests?.Serial ?? "<null>"}",
            ReferenceEquals(view.HeroDeviceForTests, s3));

        Unload(view);
        var summary = $"{tag} C: final device={view.HeroDeviceForTests?.Serial ?? "?"}";
        return (total, failures, summary);
    }

    /// <summary>
    /// Scenario E: an obsolete snapshot queued behind an operation that goes on to FAIL must
    /// never overwrite a newer snapshot delivered after that failure. Proves, in order: (1) the
    /// failing operation owns the busy gate; (2) the obsolete snapshot is actually queued behind
    /// it; (3-4) the failure continuation settles and busy ownership is observably released
    /// before the newer snapshot is ever delivered — the exact race the previous fixed-pump
    /// harness could lose; (5) the newer snapshot's own delivery (which also drains and rejects
    /// the still-obsolete queued entry via ApplyDevicesCoreAsync's existing sequence guard)
    /// settles; (6) the final hero is the newer snapshot; (7) nothing repaints afterward.
    /// </summary>
    private static (int Total, List<string> Failures, string Summary) ScenarioE(string tag)
    {
        var total = 0;
        var failures = new List<string>();
        void Expect(string name, bool ok) { total++; if (!ok) failures.Add(name); }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DashboardView.EnrichOverrideForTests = _ => tcs.Task;
        var view = new DashboardView();
        Load(view);

        var failing = Authorized($"{tag}-failing");
        var startEx = RaiseDeviceEvent(new List<AdbDevice> { failing });
        Expect($"{tag} E: no exception starting the operation that will fail", startEx == null);
        Expect($"{tag} E: the failing operation owns the busy gate; busy={view.IsBusyForTests}",
            view.IsBusyForTests);

        var queued = Authorized($"{tag}-queued-obsolete");
        var queueEx = QueueDeviceEvent(new List<AdbDevice> { queued });
        Expect($"{tag} E: no exception queuing an update behind the failing operation", queueEx == null);
        Expect($"{tag} E: the obsolete update is actually queued; queued={Serial(view.MissedUpdateForTests)}",
            Serial(view.MissedUpdateForTests) == queued.Serial);

        var settledFailure = RunAndWaitForSettle(
            () => tcs.SetException(new InvalidOperationException("batch3d-simulated-failure")));
        Expect($"{tag} E: the failure continuation settled (busy released)", settledFailure);
        Expect($"{tag} E: busy ownership was actually released after the failure; busy={view.IsBusyForTests}",
            !view.IsBusyForTests);

        DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;
        var newer = Authorized($"{tag}-newer-after-failure");
        var (newerEx, settledNewer) = DeliverAndWaitForSettle(new List<AdbDevice> { newer });
        Expect($"{tag} E: no exception on the operation after the failure (busy recovered)", newerEx == null);
        Expect($"{tag} E: the newer delivery (and its drain of the obsolete queue) settled", settledNewer);

        Expect($"{tag} E: the obsolete queued snapshot did not overwrite the newer update; " +
               $"expected={newer.Serial} actual={view.HeroDeviceForTests?.Serial ?? "<null>"} " +
               $"busy={view.IsBusyForTests} queued={Serial(view.MissedUpdateForTests)}",
            ReferenceEquals(view.HeroDeviceForTests, newer));
        Expect($"{tag} E: the obsolete snapshot was rejected, not left queued; " +
               $"queued={Serial(view.MissedUpdateForTests)}",
            view.MissedUpdateForTests == null);

        PumpWithoutWaiting();
        Expect($"{tag} E: newer still holds after further pumping (no stale repaint); " +
               $"actual={view.HeroDeviceForTests?.Serial ?? "<null>"}",
            ReferenceEquals(view.HeroDeviceForTests, newer));

        Unload(view);
        var summary = $"{tag} E: final device={view.HeroDeviceForTests?.Serial ?? "?"}";
        return (total, failures, summary);
    }

    private static string Serial((List<AdbDevice> Devices, int Sequence)? missed)
        => missed?.Devices.FirstOrDefault()?.Serial ?? "<none>";

    /// <summary>
    /// Authorized (IsAuthorized == true), so ApplyDevicesCoreAsync's enrichment step runs —
    /// always paired with DashboardView.EnrichOverrideForTests, which fully replaces the real
    /// AdbService.EnrichAsync call, so this never starts a real process.
    /// </summary>
    private static AdbDevice Authorized(string serial) => new() { Serial = serial, State = "device" };

    /// <summary>
    /// Raises a real DeviceWatcher.DevicesChanged event through DeviceWatcher's existing
    /// DiscoveryProvider seam — never touches real adb. Does not call ResetForTests (that
    /// would clear DevicesChanged and silently unsubscribe the DashboardView under test). Does
    /// NOT pump: DeviceWatcher's discovery provider here is always already-completed
    /// (Task.FromResult), so RequestRefresh runs end-to-end synchronously on this thread and any
    /// effect (queuing OR a full apply-and-settle) is already visible the instant this returns —
    /// callers that expect a real operation to result should wrap this in
    /// <see cref="RunAndWaitForSettle"/> rather than pump independently.
    /// </summary>
    private static Exception? RaiseDeviceEvent(List<AdbDevice> devices)
    {
        try
        {
            DeviceWatcher.Stop();
            DeviceWatcher.Invalidate();
            DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(devices));
            DeviceWatcher.Start();
            DeviceWatcher.RequestRefresh();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            DeviceWatcher.Stop();
        }
    }

    /// <summary>
    /// Raises a watcher event expected to be coalesced into _missedUpdate because busy is
    /// already claimed (Scenarios C/D/E's S1/S2/S3/obsolete arrivals). The coalescing itself
    /// happens synchronously inside RaiseDeviceEvent (before any await), so there is nothing to
    /// wait for; the bounded, non-waiting pump afterward is pure safety margin, not a
    /// correctness dependency.
    /// </summary>
    private static Exception? QueueDeviceEvent(List<AdbDevice> devices)
    {
        var error = RaiseDeviceEvent(devices);
        PumpWithoutWaiting(2);
        return error;
    }

    /// <summary>
    /// Raises a watcher event expected to actually apply (busy already false), deterministically
    /// waiting for DashboardView.OperationSettledForTests to confirm the resulting operation —
    /// including any queue it drains — has fully finished before returning.
    /// </summary>
    private static (Exception? Error, bool Settled) DeliverAndWaitForSettle(List<AdbDevice> devices)
    {
        Exception? error = null;
        var settled = RunAndWaitForSettle(() => error = RaiseDeviceEvent(devices));
        return (error, settled);
    }

    /// <summary>
    /// Deterministic replacement for a fixed dispatcher-pump count (Batch 5A). Installs
    /// DashboardView.OperationSettledForTests, runs <paramref name="trigger"/> — which starts or
    /// resolves the operation under test (a TaskCompletionSource.SetResult/SetException, or
    /// raising a watcher event) — then pumps the dispatcher only for as long as it actually
    /// takes for that exact operation's finally block to run (which is where the signal fires),
    /// never guessing how many pumps "should" be enough and never leaving real causal work
    /// unflushed. Bounded by <paramref name="maxIterations"/> so a genuine hang still fails fast
    /// instead of looping forever. Returns whether the signal was actually observed — false is
    /// hard proof of a stuck/never-settling operation, never silently treated as success.
    /// </summary>
    private static bool RunAndWaitForSettle(Action trigger, int maxIterations = 200)
    {
        var settled = false;
        var previous = DashboardView.OperationSettledForTests;
        DashboardView.OperationSettledForTests = () => settled = true;
        DashboardView.OperationSettledHookExceptionForTests = null;
        try
        {
            trigger();
            for (var i = 0; i < maxIterations && !settled; i++)
            {
                var frame = new DispatcherFrame();
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            // Repair batch 5B (§6): this harness's OWN hook never throws, so observing one here
            // would mean something else corrupted the shared static between calls — fail loudly
            // rather than silently ignore it.
            if (DashboardView.OperationSettledHookExceptionForTests is { } hookEx)
                throw new InvalidOperationException(
                    "OperationSettledForTests hook threw unexpectedly: " + hookEx, hookEx);
            return settled;
        }
        finally
        {
            DashboardView.OperationSettledForTests = previous;
            DashboardView.OperationSettledHookExceptionForTests = null;
        }
    }

    /// <summary>
    /// Pumps a small, bounded number of frames without waiting for any signal. Used only where
    /// the action just taken is known, by construction, never to raise
    /// OperationSettledForTests — a coalesced queue write (nothing async happens) or an
    /// abandoned/stale-generation continuation (Scenario D) — so there is nothing to wait for
    /// and RunAndWaitForSettle would just burn its whole bounded budget before giving up.
    /// </summary>
    /// <summary>
    /// Repair batch 5B (§6) helper: pumps the dispatcher until <paramref name="task"/> itself
    /// completes (success or fault), bounded so a genuine hang still fails fast. Used only by
    /// Scenarios G/H, which invoke ApplyDevicesAsync directly via reflection and need to observe
    /// the resulting Task's own completion/fault state — RunAndWaitForSettle's own signal
    /// (OperationSettledForTests) is deliberately NOT used here, since these two scenarios
    /// install their own throwing hook and must not trip RunAndWaitForSettle's unrelated
    /// "my own hook must never throw" guard.
    /// </summary>
    private static bool PumpUntilComplete(Task task, int maxIterations = 200)
    {
        for (var i = 0; i < maxIterations && !task.IsCompleted; i++)
        {
            var frame = new DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        return task.IsCompleted;
    }

    private static void PumpWithoutWaiting(int iterations = 6)
    {
        for (var i = 0; i < iterations; i++)
        {
            var frame = new DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static int SubscriberCount()
    {
        var field = typeof(DeviceWatcher).GetField("DevicesChanged", BindingFlags.NonPublic | BindingFlags.Static);
        return (field?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static void Load(DashboardView view)
        => view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

    private static void Unload(DashboardView view)
        => view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
}

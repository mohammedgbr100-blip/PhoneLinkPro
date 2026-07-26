using System.Text;
using System.Windows;
using System.Windows.Threading;
using ScrcpyDeck.Models;
using ScrcpyDeck.Views;

namespace ScrcpyDeck.Services;

/// <summary>
/// Proves DashboardView discards a stale async continuation instead of applying it after the
/// view has moved on (Batch 3C — the race deferred by Batch 3B's review: an already-running
/// ApplyDevicesAsync continuation resuming after Unloaded and still touching DeviceList or
/// AppState.CurrentDevice).
///
/// <para>
/// AdbService.EnrichAsync has no injectable seam and must not gain one just for this test, so
/// DashboardView.EnrichOverrideForTests stands in for it here: a TaskCompletionSource pauses an
/// "authorized device" enrichment at a controlled point, so the race is driven deterministically
/// — no sleeps, no timing — and a real adb/bridge process is never started (an unauthorized
/// fake device never reaches enrichment at all; an authorized one is always paired with this
/// override, which fully replaces the real call rather than wrapping it).
/// </para>
/// </summary>
internal static class DashboardStaleContinuationSelfTest
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
        var wasOverride = DashboardView.EnrichOverrideForTests;
        var appStateBefore = AppState.CurrentDevice;
        DashboardView.SuppressRefreshForHarness = true;
        try
        {
            // ---- A: an operation already past selection, paused mid-enrichment, must not
            //         mutate anything once Unloaded fires while it is still suspended ----
            //
            // The post-unload replacement device below deliberately shares the stale
            // operation's serial. ApplyDevicesCoreAsync's second AppState.SetCurrent(keep)
            // matches on serial alone ("AppState.CurrentSerial == keep.Serial"), so a same-
            // serial replacement is the only setup that actually exercises the guard: a
            // different-serial replacement would already make that condition false on its own,
            // and the assertions below would pass even with the guard deleted.
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.EnrichOverrideForTests = _ => tcs.Task;
                var view = new DashboardView();
                Load(view);

                var probe = Authorized("batch3c-inflight-probe");
                var startEx = TryRaiseDeviceEvent(new List<AdbDevice> { probe });
                Expect("A: no exception starting the in-flight operation", startEx == null);

                // The synchronous first half already ran (legitimate — the view WAS loaded);
                // only the post-enrichment half is still pending, paused on tcs.Task.
                var itemsAfterStart = view.DeviceList.ItemsSource;

                Unload(view);

                // A distinct instance, same serial as the stale operation's "keep" device.
                var replacement = Authorized(probe.Serial);
                AppState.SetCurrent(replacement);

                tcs.SetResult();
                Pump();

                Expect("A: DeviceList.ItemsSource unchanged by the stale resumption",
                    ReferenceEquals(view.DeviceList.ItemsSource, itemsAfterStart));
                Expect("A: AppState.CurrentDevice is still the post-unload replacement instance",
                    ReferenceEquals(AppState.CurrentDevice, replacement));
                Expect("A: the stale same-serial probe instance did not replace it",
                    !ReferenceEquals(AppState.CurrentDevice, probe));

                lines.AppendLine(
                    $"  A: items unchanged={ReferenceEquals(view.DeviceList.ItemsSource, itemsAfterStart)}, " +
                    $"AppState still replacement={ReferenceEquals(AppState.CurrentDevice, replacement)}, " +
                    $"not stale same-serial probe={!ReferenceEquals(AppState.CurrentDevice, probe)}");
            }

            // ---- B: an older result must not overwrite a newer one ----
            {
                var tcsA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.EnrichOverrideForTests = _ => tcsA.Task;
                var view = new DashboardView();
                Load(view);

                var deviceA = Authorized("batch3c-old-result");
                var exA = TryRaiseDeviceEvent(new List<AdbDevice> { deviceA });   // A starts, pauses
                Expect("B: no exception starting A", exA == null);

                Unload(view);
                Load(view);   // fresh lifecycle; A is now stale but still suspended

                DashboardView.EnrichOverrideForTests = null;   // B completes synchronously
                var deviceB = Unauthorized("batch3c-new-result");
                var exB = TryRaiseDeviceEvent(new List<AdbDevice> { deviceB });   // B runs to completion
                Expect("B: no exception completing B", exB == null);

                var itemsAfterB = (view.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
                Expect("B: B's result is applied before A resolves",
                    itemsAfterB != null && itemsAfterB.Count == 1 && ReferenceEquals(itemsAfterB[0], deviceB));

                tcsA.SetResult();   // the older, previously-paused operation finally resolves
                Pump();

                var itemsAfterA = (view.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
                Expect("B: A's late resolution did not overwrite B's result",
                    itemsAfterA != null && itemsAfterA.Count == 1 && ReferenceEquals(itemsAfterA[0], deviceB));

                lines.AppendLine(
                    $"  B: after B={(itemsAfterB is [{ } b] ? b.Serial : "?")}, " +
                    $"after A resolves late={(itemsAfterA is [{ } a] ? a.Serial : "?")}");
                Unload(view);
            }

            // ---- C: a fresh reload succeeds even though the busy flag was abandoned mid-flight ----
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.EnrichOverrideForTests = _ => tcs.Task;
                var view = new DashboardView();
                Load(view);

                var stale = Authorized("batch3c-abandoned");
                var startEx = TryRaiseDeviceEvent(new List<AdbDevice> { stale });
                Expect("C: no exception starting the abandoned operation", startEx == null);

                Unload(view);
                tcs.SetResult();   // the abandoned operation completes and must be discarded
                Pump();

                DashboardView.EnrichOverrideForTests = null;
                Load(view);   // fresh lifecycle

                var fresh = Unauthorized("batch3c-fresh-reload");
                var freshEx = TryRaiseDeviceEvent(new List<AdbDevice> { fresh });
                Expect("C: no exception on the fresh reload's refresh", freshEx == null);

                var itemsAfterFresh = (view.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
                var freshSucceeded = itemsAfterFresh != null && itemsAfterFresh.Count == 1
                                     && ReferenceEquals(itemsAfterFresh[0], fresh);
                Expect("C: the fresh reload's refresh was not blocked by the abandoned busy flag", freshSucceeded);

                lines.AppendLine($"  C: fresh reload after an abandoned operation succeeded = {freshSucceeded}");
                Unload(view);
            }

            // ---- D: a stale enrichment exception must not touch detached UI or AppState, and
            //         must not prevent the next lifecycle from working normally ----
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.EnrichOverrideForTests = _ => tcs.Task;
                var view = new DashboardView();
                Load(view);

                var probe = Authorized("batch3c-exception-probe");
                var startEx = TryRaiseDeviceEvent(new List<AdbDevice> { probe });
                Expect("D: no exception starting the operation that will later fault", startEx == null);

                var itemsAfterStart = view.DeviceList.ItemsSource;
                Unload(view);

                // Same-serial replacement, exactly as scenario A, so a faulted resumption is
                // held to the same bar: an exception must not reach the mutations either.
                var replacement = Authorized(probe.Serial);
                AppState.SetCurrent(replacement);

                tcs.SetException(new InvalidOperationException("batch3c-simulated-enrichment-failure"));
                Pump();

                Expect("D: DeviceList.ItemsSource unchanged after the stale enrichment fault",
                    ReferenceEquals(view.DeviceList.ItemsSource, itemsAfterStart));
                Expect("D: AppState.CurrentDevice still reflects the post-unload replacement",
                    ReferenceEquals(AppState.CurrentDevice, replacement));

                lines.AppendLine(
                    $"  D: items unchanged after fault={ReferenceEquals(view.DeviceList.ItemsSource, itemsAfterStart)}, " +
                    $"AppState not clobbered by fault={ReferenceEquals(AppState.CurrentDevice, replacement)}");

                // The faulted, abandoned generation must not wedge the next lifecycle's _busy.
                DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;
                Load(view);

                var fresh = Unauthorized("batch3c-post-fault-reload");
                var freshEx = TryRaiseDeviceEvent(new List<AdbDevice> { fresh });
                Expect("D: no exception on the reload started after the stale fault", freshEx == null);

                var itemsAfterFresh = (view.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
                var freshSucceeded = itemsAfterFresh != null && itemsAfterFresh.Count == 1
                                     && ReferenceEquals(itemsAfterFresh[0], fresh);
                Expect("D: the reload after a stale fault still updates the list", freshSucceeded);

                lines.AppendLine($"  D: reload after a stale fault succeeded = {freshSucceeded}");
                Unload(view);
            }

            // ---- E: one valid current operation still updates the list and current device once ----
            {
                DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;   // immediate, no real adb
                var view = new DashboardView();
                Load(view);

                var live = Authorized("batch3c-live-authorized");
                var ex = TryRaiseDeviceEvent(new List<AdbDevice> { live });
                Expect("E: no exception on a normal, current operation", ex == null);

                var items = (view.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
                Expect("E: DeviceList updated exactly once with the live device",
                    items != null && items.Count == 1 && ReferenceEquals(items[0], live));
                Expect("E: AppState.CurrentDevice updated to the live device",
                    ReferenceEquals(AppState.CurrentDevice, live));

                lines.AppendLine($"  E: list updated={items?.Count == 1}, " +
                                 $"AppState updated={ReferenceEquals(AppState.CurrentDevice, live)}");
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
            DashboardView.EnrichOverrideForTests = wasOverride;
            AppState.SetCurrent(appStateBefore);
            DeviceWatcher.ResetForTests();
        }

        var passed = failures.Count == 0;
        var report = $"dashboard-stale: {total - failures.Count}/{total} guard assertions passed" +
                     Environment.NewLine + lines.ToString().TrimEnd() +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));

        return new Result(passed, total, failures.Count, report);
    }

    /// <summary>Never authorized, so applying it can never reach enrichment at all.</summary>
    private static AdbDevice Unauthorized(string serial) => new() { Serial = serial, State = "unauthorized" };

    /// <summary>
    /// Authorized (IsAuthorized == true), so ApplyDevicesCoreAsync's enrichment step runs —
    /// always paired with DashboardView.EnrichOverrideForTests, which fully replaces the real
    /// AdbService.EnrichAsync call, so this never starts a real process.
    /// </summary>
    private static AdbDevice Authorized(string serial) => new() { Serial = serial, State = "device" };

    /// <summary>
    /// Raises a real DeviceWatcher.DevicesChanged event through DeviceWatcher's existing
    /// DiscoveryProvider seam — never touches real adb. Does not call ResetForTests (that
    /// would clear DevicesChanged and silently unsubscribe the DashboardView under test).
    /// </summary>
    private static Exception? TryRaiseDeviceEvent(List<AdbDevice> devices)
    {
        try
        {
            DeviceWatcher.Stop();
            DeviceWatcher.Invalidate();
            DeviceWatcher.DiscoveryProvider = _ => Task.FromResult(DeviceDiscoveryResult.Success(devices));
            DeviceWatcher.Start();
            DeviceWatcher.RequestRefresh();
            Pump();
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

    private static void Load(DashboardView view)
        => view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

    private static void Unload(DashboardView view)
        => view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
        {
            var frame = new DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}

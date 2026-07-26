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
                var startEx = TryRaiseDeviceEvent(new List<AdbDevice> { s });
                Expect("A: no exception queuing S while refresh is busy", startEx == null);

                var r = Authorized("batch3d-refresh-result");
                discoverTcs.SetResult(DeviceDiscoveryResult.Success(new List<AdbDevice> { r }));
                Pump();   // refresh applies R, then drains and applies S in order, then releases busy

                var t = Authorized("batch3d-newer-t");
                var laterEx = TryRaiseDeviceEvent(new List<AdbDevice> { t });
                Expect("A: no exception delivering T", laterEx == null);

                var items = (view.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
                Expect("A: final ItemsSource represents T, not S",
                    items != null && items.Count == 1 && ReferenceEquals(items[0], t));
                Expect("A: AppState.CurrentDevice represents T",
                    ReferenceEquals(AppState.CurrentDevice, t));

                lines.AppendLine($"  A: final device={(items is [{ } a] ? a.Serial : "?")}, " +
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
                var startEx = TryRaiseDeviceEvent(new List<AdbDevice> { s });
                Expect("B: no exception queuing the lone update", startEx == null);

                var r = Authorized("batch3d-lone-refresh-result");
                discoverTcs.SetResult(DeviceDiscoveryResult.Success(new List<AdbDevice> { r }));
                Pump();

                var items = (view.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
                Expect("B: the lone queued update eventually became the final result",
                    items != null && items.Count == 1 && ReferenceEquals(items[0], s));

                lines.AppendLine($"  B: final device={(items is [{ } b] ? b.Serial : "?")}");
                Unload(view);
                DashboardView.DiscoverDevicesOverrideForTests = null;
            }

            // ---- C: multiple queued updates coalesce to the latest; earlier ones never
            //         apply ----
            {
                var holdTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.EnrichOverrideForTests = _ => holdTcs.Task;
                var view = new DashboardView();
                Load(view);

                var holder = Authorized("batch3d-busy-holder");
                var startEx = TryRaiseDeviceEvent(new List<AdbDevice> { holder });
                Expect("C: no exception starting the busy-holding operation", startEx == null);

                var s1 = Authorized("batch3d-s1");
                var s2 = Authorized("batch3d-s2");
                var s3 = Authorized("batch3d-s3");
                var ex1 = TryRaiseDeviceEvent(new List<AdbDevice> { s1 });
                var ex2 = TryRaiseDeviceEvent(new List<AdbDevice> { s2 });
                var ex3 = TryRaiseDeviceEvent(new List<AdbDevice> { s3 });
                Expect("C: no exception queuing S1/S2/S3", ex1 == null && ex2 == null && ex3 == null);

                DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;
                holdTcs.SetResult();
                Pump();

                var items = (view.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
                Expect("C: only S3 (the latest) was ever applied",
                    items != null && items.Count == 1 && ReferenceEquals(items[0], s3));

                lines.AppendLine($"  C: final device={(items is [{ } c] ? c.Serial : "?")}");
                Unload(view);
            }

            // ---- D: an old lifecycle's queued update cannot survive into a new one ----
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.EnrichOverrideForTests = _ => tcs.Task;
                var view = new DashboardView();
                Load(view);

                var holder = Authorized("batch3d-gen-holder");
                var startEx = TryRaiseDeviceEvent(new List<AdbDevice> { holder });
                Expect("D: no exception starting the busy-holding operation", startEx == null);

                var queued = Authorized("batch3d-gen-queued");
                var queueEx = TryRaiseDeviceEvent(new List<AdbDevice> { queued });
                Expect("D: no exception queuing an update in the outgoing generation", queueEx == null);

                Unload(view);          // generation G -> G+1; _missedUpdate cleared, busy released
                tcs.SetResult();       // the abandoned holder resolves after unload; must change nothing
                Pump();

                DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;
                Load(view);            // fresh lifecycle, generation G+1

                var current = Authorized("batch3d-gen-current");
                var currentEx = TryRaiseDeviceEvent(new List<AdbDevice> { current });
                Expect("D: no exception delivering the current-generation update", currentEx == null);

                var items = (view.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
                Expect("D: the old queued update was discarded; the current update won",
                    items != null && items.Count == 1 && ReferenceEquals(items[0], current));

                lines.AppendLine($"  D: final device={(items is [{ } d] ? d.Serial : "?")}");
                Unload(view);
            }

            // ---- E: an obsolete queued snapshot cannot overwrite a newer update after the
            //         active operation fails ----
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DashboardView.EnrichOverrideForTests = _ => tcs.Task;
                var view = new DashboardView();
                Load(view);

                var failing = Authorized("batch3d-failing");
                var startEx = TryRaiseDeviceEvent(new List<AdbDevice> { failing });
                Expect("E: no exception starting the operation that will fail", startEx == null);

                var queued = Authorized("batch3d-queued-obsolete");
                var queueEx = TryRaiseDeviceEvent(new List<AdbDevice> { queued });
                Expect("E: no exception queuing an update behind the failing operation", queueEx == null);

                tcs.SetException(new InvalidOperationException("batch3d-simulated-failure"));
                Pump();

                DashboardView.EnrichOverrideForTests = _ => Task.CompletedTask;
                var newer = Authorized("batch3d-newer-after-failure");
                var newerEx = TryRaiseDeviceEvent(new List<AdbDevice> { newer });
                Expect("E: no exception on the operation after the failure (busy recovered)", newerEx == null);

                var items = (view.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
                Expect("E: the obsolete queued snapshot did not overwrite the newer update",
                    items != null && items.Count == 1 && ReferenceEquals(items[0], newer));

                lines.AppendLine($"  E: final device={(items is [{ } e] ? e.Serial : "?")}");
                Unload(view);
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
                var ex = TryRaiseDeviceEvent(new List<AdbDevice> { live });
                Expect("F: no exception on a normal, current operation", ex == null);

                var items = (view.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
                Expect("F: a single normal watcher event still updates the list exactly once",
                    items != null && items.Count == 1 && ReferenceEquals(items[0], live));

                Unload(view);
                Expect("F: subscriber count after Unloaded returns to baseline",
                    SubscriberCount() == baseline);

                lines.AppendLine($"  F: subscriber balance ok, normal update applied once=" +
                                 $"{items != null && items.Count == 1}");
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

    private static int SubscriberCount()
    {
        var field = typeof(DeviceWatcher).GetField("DevicesChanged", BindingFlags.NonPublic | BindingFlags.Static);
        return (field?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
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

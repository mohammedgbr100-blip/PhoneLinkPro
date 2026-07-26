using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ScrcpyDeck.Models;
using ScrcpyDeck.Views;

namespace ScrcpyDeck.Services;

/// <summary>
/// Proves DashboardView releases its DeviceWatcher.DevicesChanged subscription when unloaded
/// (Batch 3B). Constructs real DashboardView instances and raises the real Loaded/Unloaded
/// routed events — the same production mechanism MainWindow's cached-page navigation drives
/// when it swaps PageHost.Content — and reads the subscriber count straight off
/// DeviceWatcher's backing field, exactly as ShellLifecycleSelfTest does for the shell's own
/// events.
///
/// <para>
/// DashboardView.SuppressRefreshForHarness skips the ADB-touching RefreshAsync() call that
/// Loaded would otherwise trigger. This build's own output directory carries the real engine
/// binaries (CopyEngineToBuild copies them there for every build), so unlike the xUnit test
/// host — where no engine folder exists and ToolLocator.AdbAvailable is naturally false —
/// PLP_SELFTEST running from that same output would otherwise start a real
/// PhoneLink.Bridge.exe. Only the device query is skipped; the subscribe/unsubscribe wiring
/// itself runs exactly as in production.
/// </para>
///
/// <para>
/// DevicesChanged is raised through DeviceWatcher's existing DiscoveryProvider seam (the same
/// one DeviceWatcherLogicTests uses), never through real discovery — so the "one event while
/// loaded updates the list once" check is genuine production behaviour, not a simulation.
/// </para>
/// </summary>
internal static class DashboardLifecycleSelfTest
{
    internal sealed record Result(bool Passed, int Total, int Failed, string Report);

    internal static Result Run()
    {
        var lines = new StringBuilder();
        var failures = new List<string>();
        var total = 0;

        void Expect(string name, int expected, int actual)
        {
            total++;
            if (expected != actual) failures.Add($"{name}: expected {expected}, got {actual}");
        }

        var wasSuppressed = DashboardView.SuppressRefreshForHarness;
        DashboardView.SuppressRefreshForHarness = true;
        try
        {
            var baseline = SubscriberCount();
            lines.AppendLine($"  baseline DevicesChanged subscribers = {baseline}");

            // ---- one live instance: exactly one subscription while loaded ----
            var view = new DashboardView();
            Load(view);
            var afterLoad = SubscriberCount();
            lines.AppendLine($"  after Loaded             = {afterLoad}");
            Expect("subscriber count after Loaded", baseline + 1, afterLoad);

            // ---- a repeated Loaded without an intervening Unloaded adds nothing ----
            Load(view);
            var afterSecondLoad = SubscriberCount();
            lines.AppendLine($"  after repeated Loaded    = {afterSecondLoad}");
            Expect("subscriber count after a repeated Loaded", baseline + 1, afterSecondLoad);

            Unload(view);
            var afterUnload = SubscriberCount();
            lines.AppendLine($"  after Unloaded           = {afterUnload}");
            Expect("subscriber count after Unloaded", baseline, afterUnload);

            // ---- Loaded -> Unloaded -> Loaded -> Unloaded stays balanced on one instance ----
            Load(view);
            Unload(view);
            Load(view);
            Unload(view);
            Expect("subscriber count after two more Loaded/Unloaded cycles", baseline, SubscriberCount());

            // ---- three fresh create/load/unload cycles: no cumulative growth ----
            for (var cycle = 1; cycle <= 3; cycle++)
            {
                var cycled = new DashboardView();
                Load(cycled);
                Expect($"cycle {cycle}: subscriber count while loaded", baseline + 1, SubscriberCount());
                Unload(cycled);
                Expect($"cycle {cycle}: subscriber count after unload", baseline, SubscriberCount());
            }
            lines.AppendLine("  three create/load/unload cycles: no cumulative growth (see assertions above)");

            // ---- event after unload: no re-subscription, no visible update, no exception ----
            var unloadedView = new DashboardView();
            Load(unloadedView);
            Unload(unloadedView);
            var beforeItems = unloadedView.DeviceList.ItemsSource;
            var postUnloadEx = TryRaiseDeviceEvent(new List<AdbDevice> { Fake("batch3b-unloaded-probe") });
            var afterItems = unloadedView.DeviceList.ItemsSource;

            total++;
            if (postUnloadEx != null)
                failures.Add($"a DevicesChanged event after Unloaded threw: {postUnloadEx.GetType().Name}: {postUnloadEx.Message}");
            Expect("subscriber count stays at baseline after a post-unload event", baseline, SubscriberCount());
            total++;
            if (!ReferenceEquals(beforeItems, afterItems))
                failures.Add("a DevicesChanged event after Unloaded still touched DeviceList.ItemsSource");
            lines.AppendLine($"  post-unload event: no exception={postUnloadEx == null}, " +
                             $"ItemsSource unchanged={ReferenceEquals(beforeItems, afterItems)}");

            // ---- production behaviour preserved: one event while loaded updates the list once ----
            var liveView = new DashboardView();
            Load(liveView);
            var fakeDevices = new List<AdbDevice> { Fake("batch3b-live-probe") };
            var liveEx = TryRaiseDeviceEvent(fakeDevices);
            // DeviceWatcher.ApplySuccess wraps the payload in a fresh List<AdbDevice> (a
            // ".ToList()" copy) before dispatching, so the LIST container is never the same
            // object as fakeDevices — only the single AdbDevice element inside it is. "Exactly
            // once" is checked at the element level: the applied list holds exactly the one
            // fake device instance, no more, no fewer.
            var appliedList = (liveView.DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.ToList();
            var applied = liveEx == null && appliedList != null && appliedList.Count == 1
                          && ReferenceEquals(appliedList[0], fakeDevices[0]);

            total++;
            if (liveEx != null)
                failures.Add($"a DevicesChanged event while Loaded threw: {liveEx.GetType().Name}: {liveEx.Message}");
            else if (!applied)
                failures.Add("a DevicesChanged event while Loaded did not update DeviceList.ItemsSource exactly once");
            lines.AppendLine($"  live event while loaded: DeviceList.ItemsSource updated exactly once = {applied}");
            Unload(liveView);
        }
        catch (Exception ex)
        {
            failures.Add($"unexpected failure: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DashboardView.SuppressRefreshForHarness = wasSuppressed;
            DeviceWatcher.ResetForTests();
        }

        var passed = failures.Count == 0;
        var report = $"dashboard: {total - failures.Count}/{total} lifecycle assertions passed" +
                     Environment.NewLine + lines.ToString().TrimEnd() +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));

        return new Result(passed, total, failures.Count, report);
    }

    /// <summary>An unauthorized fake device never reaches AdbService.EnrichAsync (it early
    /// returns on !IsAuthorized), so applying it can never start a real process.</summary>
    private static AdbDevice Fake(string serial) => new() { Serial = serial, State = "unauthorized" };

    /// <summary>
    /// Raises a real DeviceWatcher.DevicesChanged event through the same fake-provider seam
    /// DeviceWatcherLogicTests uses (DiscoveryProvider + RequestRefresh) — never touches real
    /// adb. Deliberately does NOT call DeviceWatcher.ResetForTests(): that clears
    /// DevicesChanged to null, which would silently unsubscribe the very DashboardView this
    /// method exists to signal. Invalidate() forces the next poll to be treated as a change
    /// without touching the subscriber list. Returns the exception if raising it (or any
    /// subscriber) threw, so the caller can assert "no dispatcher exception" without letting a
    /// failure abort the whole self-test.
    /// </summary>
    private static Exception? TryRaiseDeviceEvent(List<AdbDevice> devices)
    {
        try
        {
            DeviceWatcher.Stop();   // idempotent; clears polling/timer state only
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

    private static int SubscriberCount()
    {
        var field = typeof(DeviceWatcher).GetField("DevicesChanged", BindingFlags.NonPublic | BindingFlags.Static);
        return (field?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }

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

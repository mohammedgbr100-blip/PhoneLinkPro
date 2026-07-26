using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ScrcpyDeck.Services;

/// <summary>
/// Proves the shell gives back every long-lived subscription when it closes.
///
/// <para>
/// The services expose STATIC events, so a missed unsubscribe roots the window for the life
/// of the process and lets a handler run against a dead visual tree. Subscriber counts are
/// read straight off each event's compiler-generated backing field by reflection: the
/// services themselves are not modified, and no test hook is added to them.
/// </para>
/// </summary>
internal static class ShellLifecycleSelfTest
{
    /// <summary>The static events the shell subscribes to, as (owner, field) pairs.</summary>
    private static readonly (string Label, Type Owner, string Field)[] Events =
    {
        ("AppState.CurrentDeviceChanged", typeof(AppState), "CurrentDeviceChanged"),
        ("DeviceWatcher.DevicesChanged", typeof(DeviceWatcher), "DevicesChanged"),
        ("ScrcpyService.SessionsChanged", typeof(ScrcpyService), "SessionsChanged"),
        ("Toast.Requested", typeof(Toast), "Requested"),
    };

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

        var baseline = Counts();
        lines.AppendLine("  baseline " + Describe(baseline));

        // ---- one live window holds exactly one subscription per event ----
        var window = new MainWindow();
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        Pump();
        var whileOpen = Counts();
        lines.AppendLine("  open     " + Describe(whileOpen));
        for (var i = 0; i < Events.Length; i++)
            Expect($"{Events[i].Label} while open", baseline[i] + 1, whileOpen[i]);

        // ---- closing releases all of them ----
        var weak = new WeakReference(window);
        window.Close();
        Pump();
        var afterClose = Counts();
        lines.AppendLine("  closed   " + Describe(afterClose));
        for (var i = 0; i < Events.Length; i++)
            Expect($"{Events[i].Label} after close", baseline[i], afterClose[i]);

        // ---- recreating does not accumulate duplicates ----
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var repeat = new MainWindow();
            repeat.Show();
            Pump();
            var open = Counts();
            for (var i = 0; i < Events.Length; i++)
                Expect($"{Events[i].Label} cycle {cycle} open", baseline[i] + 1, open[i]);
            repeat.Close();
            Pump();
        }
        var afterCycles = Counts();
        lines.AppendLine("  x3 cycle " + Describe(afterCycles));
        for (var i = 0; i < Events.Length; i++)
            Expect($"{Events[i].Label} after 3 cycles", baseline[i], afterCycles[i]);

        // ---- a closed window refuses to touch its visual tree ----
        total++;
        if (ShellSubscriptions.CanDispatch(windowClosed: true, false, false))
            failures.Add("CanDispatch: a closed window must not accept callbacks");

        // ---- nothing the SHELL owns still roots the closed window ----
        // WPF itself legitimately retains a recently-closed window for a while (focus scopes,
        // PresentationSource bookkeeping), so reachability alone is not a defect. What must
        // hold is that no shell subscription is among the roots — proven by the zero counts
        // above. The reachability figure is reported for information only.
        window = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        lines.AppendLine($"  first window still reachable after close+GC = {weak.IsAlive} " +
                         "(informational: shell subscriptions are already released)");

        var passed = failures.Count == 0;
        var report = $"lifecycle: {total - failures.Count}/{total} subscription assertions passed" +
                     Environment.NewLine + lines.ToString().TrimEnd() +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));

        return new Result(passed, total, failures.Count, report);
    }

    private static int[] Counts()
    {
        var counts = new int[Events.Length];
        for (var i = 0; i < Events.Length; i++)
        {
            var field = Events[i].Owner.GetField(
                Events[i].Field, BindingFlags.NonPublic | BindingFlags.Static);
            counts[i] = (field?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
        }
        return counts;
    }

    private static string Describe(int[] counts)
    {
        var parts = new List<string>();
        for (var i = 0; i < Events.Length; i++)
            parts.Add($"{Events[i].Label.Split('.')[^1]}={counts[i]}");
        return string.Join(" ", parts);
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

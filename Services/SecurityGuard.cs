using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace ScrcpyDeck.Services;

/// <summary>
/// Lightweight runtime anti-tamper / anti-debug guard. Active only in Release builds so
/// development is unaffected. This raises the reverse-engineering bar; it is NOT absolute —
/// gate any future paid features on server-side validation (see HANDOFF).
/// </summary>
public static class SecurityGuard
{
    [DllImport("kernel32.dll")]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll")]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref bool isPresent);

#if !DEBUG
    private static System.Windows.Threading.DispatcherTimer? _timer;
#endif

    public static void ArmIfRelease()
    {
#if !DEBUG
        if (IsBeingAnalyzed()) Bail();

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += (_, _) => { if (IsBeingAnalyzed()) Bail(); };
        _timer.Start();
#endif
    }

    /// <summary>
    /// Stops the periodic check so an orderly shutdown cannot be turned into a FailFast.
    /// This is a lifecycle hook only — the detection logic itself is unchanged and is
    /// scheduled for a later batch.
    /// </summary>
    public static void Disarm()
    {
        Volatile.Write(ref _disarmed, 1);
#if !DEBUG
        try { _timer?.Stop(); } catch { }
        _timer = null;
#endif
    }

    private static int _disarmed;

    /// <summary>True once <see cref="Disarm"/> has run.</summary>
    internal static bool IsDisarmed => Volatile.Read(ref _disarmed) == 1;

    private static bool IsBeingAnalyzed()
    {
        try
        {
            if (Debugger.IsAttached) return true;
            if (IsDebuggerPresent()) return true;
            bool remote = false;
            CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, ref remote);
            if (remote) return true;

            // Known analysis tooling running alongside the process.
            string[] bad = { "dnspy", "de4dot", "ilspy", "dotpeek", "megadumper",
                             "x64dbg", "ollydbg", "windbg", "cheatengine" };
            foreach (var pr in Process.GetProcesses())
            {
                string n;
                try { n = pr.ProcessName.ToLowerInvariant(); } catch { continue; }
                foreach (var b in bad) if (n.Contains(b)) return true;
            }
        }
        catch { }
        return false;
    }

    private static void Bail()
    {
        // Never FailFast during or after an orderly shutdown.
        if (IsDisarmed || AppLifecycle.IsShuttingDown) return;

        try
        {
            MessageBox.Show("تعذّر تشغيل البرنامج في هذه البيئة.",
                "PhoneLink Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        Environment.FailFast(null);
    }
}

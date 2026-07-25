using Microsoft.Win32;
using System.Windows.Threading;
using ScrcpyDeck.Models;

namespace ScrcpyDeck.Services;

/// <summary>
/// Polls the device list in the background so the UI reacts to plug/unplug/authorize
/// without a manual refresh.
///
/// <para><b>Single-flight with coalescing (DEV-02).</b> At most one adb query runs at a
/// time. A request arriving while one is in flight sets a pending flag instead of being
/// dropped; when the active query finishes, exactly one follow-up runs. Any number of
/// requests coalesce into that single follow-up, so a refresh is never lost and adb
/// processes never overlap.</para>
///
/// <para><b>Unknown is not empty (DEV-01).</b> Only a successful query updates the confirmed
/// snapshot, the dedup signature, and the UI. A timeout, cancellation, start failure, adb
/// error or unusable output leaves the last confirmed devices in place and raises no
/// disconnect notification.</para>
/// </summary>
public static class DeviceWatcher
{
    /// <summary>Raised (UI thread) whenever the confirmed set of devices changes.</summary>
    public static event Action<List<AdbDevice>>? DevicesChanged;

    // ---------------- tunables (documented in the Batch 1D report) ----------------

    /// <summary>Normal cadence. Short enough that plugging a cable feels immediate.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>First extra delay after a failure; doubles per consecutive failure.</summary>
    internal static TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Ceiling for the backoff, so a long outage never stops recovery entirely.</summary>
    internal static TimeSpan BackoffMax { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Settling time after the machine resumes before the first refresh.</summary>
    internal static TimeSpan ResumeStabilization { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Swappable query, so the loop can be tested without adb or a phone.</summary>
    internal static Func<CancellationToken, Task<DeviceDiscoveryResult>> DiscoveryProvider { get; set; }
        = AdbService.DiscoverDevicesAsync;

    /// <summary>Clock seam, so backoff can be tested without waiting.</summary>
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    // ---------------- state ----------------

    private static readonly object _gate = new();

    private static DispatcherTimer? _timer;
    private static bool _running;            // Start() called and not Stopped
    private static bool _polling;            // a query is in flight
    private static bool _pending;            // a request arrived while polling
    private static bool _pendingManual;      // that request wants to bypass backoff
    private static bool _suspended;          // machine is asleep
    private static bool _powerHooked;        // PowerModeChanged subscribed exactly once

    private static string? _lastSignature;               // committed only after a successful apply
    private static List<AdbDevice> _lastConfirmed = new();
    private static int _consecutiveFailures;
    private static DateTimeOffset _nextAllowedPoll = DateTimeOffset.MinValue;
    private static DeviceDiscoveryOutcome? _lastLoggedFailure;
    private static readonly HashSet<string> _unauthorizedNotified = new(StringComparer.Ordinal);

    private static CancellationTokenSource? _cts;

    // ---------------- lifecycle ----------------

    /// <summary>Starts polling. Idempotent.</summary>
    public static void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
        }

        HookPowerEvents();

        // The timer needs a Dispatcher; headless hosts (tests, PLP_SELFTEST) just skip it
        // and drive the loop directly.
        if (System.Windows.Application.Current != null)
        {
            _timer = new DispatcherTimer { Interval = PollInterval };
            _timer.Tick += (_, _) => RequestRefresh(manual: false);
            _timer.Start();
        }

        RequestRefresh(manual: true);
    }

    /// <summary>
    /// Stops polling and releases the power-event subscription. Idempotent, and safe to
    /// call during shutdown: pending work is discarded and no further events are raised.
    /// </summary>
    public static void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_running && _timer == null && !_powerHooked) return;
            _running = false;
            _pending = false;
            _pendingManual = false;
            cts = _cts;
            _cts = null;
        }

        var timer = _timer;
        _timer = null;
        try { timer?.Stop(); } catch { }

        UnhookPowerEvents();

        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    /// <summary>True when the watcher is accepting polls.</summary>
    internal static bool IsRunning { get { lock (_gate) return _running; } }

    /// <summary>Test hook: full reset to a pristine, stopped state.</summary>
    internal static void ResetForTests()
    {
        Stop();
        lock (_gate)
        {
            _polling = false;
            _pending = false;
            _pendingManual = false;
            _suspended = false;
            _lastSignature = null;
            _lastConfirmed = new List<AdbDevice>();
            _consecutiveFailures = 0;
            _nextAllowedPoll = DateTimeOffset.MinValue;
            _lastLoggedFailure = null;
            _unauthorizedNotified.Clear();
        }
        DevicesChanged = null;
        DiscoveryProvider = AdbService.DiscoverDevicesAsync;
        Clock = () => DateTimeOffset.UtcNow;
        PollInterval = TimeSpan.FromSeconds(4);
        BackoffBase = TimeSpan.FromSeconds(4);
        BackoffMax = TimeSpan.FromSeconds(60);
        ResumeStabilization = TimeSpan.FromSeconds(2);
    }

    /// <summary>Force the next successful poll to report even if nothing changed.</summary>
    public static void Invalidate()
    {
        lock (_gate) _lastSignature = null;
    }

    /// <summary>The last snapshot adb actually confirmed.</summary>
    internal static IReadOnlyList<AdbDevice> LastConfirmed
    {
        get { lock (_gate) return _lastConfirmed.ToList(); }
    }

    // ---------------- power events (Part E) ----------------

    private static void HookPowerEvents()
    {
        lock (_gate)
        {
            if (_powerHooked) return;
            _powerHooked = true;
        }
        try { SystemEvents.PowerModeChanged += OnPowerModeChanged; }
        catch { lock (_gate) _powerHooked = false; }
    }

    private static void UnhookPowerEvents()
    {
        bool hooked;
        lock (_gate) { hooked = _powerHooked; _powerHooked = false; }
        if (!hooked) return;
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
    }

    /// <summary>True while a power-event subscription is held (leak check).</summary>
    internal static bool IsPowerHooked { get { lock (_gate) return _powerHooked; } }

    private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend: OnSuspended(); break;
            case PowerModes.Resume: OnResumed(); break;
        }
    }

    /// <summary>
    /// Pauses polling. A sleeping machine must never be read as "every device unplugged",
    /// so the confirmed snapshot is left untouched.
    /// </summary>
    internal static void OnSuspended()
    {
        lock (_gate)
        {
            _suspended = true;
            _pending = false;
            _pendingManual = false;
        }
        Log.Info("DeviceWatcher: suspended");
    }

    /// <summary>
    /// Resumes polling and asks for a refresh immediately rather than waiting out the normal
    /// interval. USB re-enumeration takes a moment after wake, so the first attempt is
    /// delayed by a small stabilization window.
    /// </summary>
    internal static void OnResumed()
    {
        lock (_gate)
        {
            _suspended = false;
            _consecutiveFailures = 0;                 // a pre-sleep outage is not evidence
            _nextAllowedPoll = Clock() + ResumeStabilization;
        }
        Log.Info("DeviceWatcher: resumed");
        RequestRefresh(manual: true);
    }

    /// <summary>True while the machine is considered asleep.</summary>
    internal static bool IsSuspended { get { lock (_gate) return _suspended; } }

    // ---------------- poll loop (Part C) ----------------

    /// <summary>
    /// Asks for a device refresh. Never blocks and never overlaps: if a query is already
    /// running the request is remembered and serviced exactly once afterwards.
    /// </summary>
    /// <param name="manual">True for user-initiated refreshes, which bypass the backoff once.</param>
    public static void RequestRefresh(bool manual = true)
    {
        lock (_gate)
        {
            if (!_running || _suspended) return;

            if (_polling)
            {
                _pending = true;
                _pendingManual |= manual;
                return;
            }

            if (!manual && Clock() < _nextAllowedPoll) return;   // backing off

            _polling = true;
        }

        _ = RunPollLoopAsync(manual);
    }

    /// <summary>
    /// Runs the query, applies the result, then services any request that arrived meanwhile.
    /// Loops rather than recurses so a burst of requests cannot grow the stack.
    /// </summary>
    private static async Task RunPollLoopAsync(bool manual)
    {
        try
        {
            while (true)
            {
                CancellationToken token;
                lock (_gate)
                {
                    if (!_running) return;
                    token = _cts?.Token ?? CancellationToken.None;
                }

                await PollOnceAsync(token);

                lock (_gate)
                {
                    if (!_running || _suspended || !_pending) return;

                    manual = _pendingManual;
                    _pending = false;
                    _pendingManual = false;

                    if (!manual && Clock() < _nextAllowedPoll) return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("DeviceWatcher loop failed: " + ex.Message);
        }
        finally
        {
            lock (_gate) _polling = false;
        }
    }

    private static async Task PollOnceAsync(CancellationToken token)
    {
        DeviceDiscoveryResult result;
        try
        {
            result = await DiscoveryProvider(token);
        }
        catch (OperationCanceledException)
        {
            return;   // orderly shutdown: not an error
        }
        catch (Exception ex)
        {
            result = DeviceDiscoveryResult.Failure(DeviceDiscoveryOutcome.StartFailed, ex.Message);
        }

        lock (_gate) { if (!_running) return; }

        if (result.IsSuccess) ApplySuccess(result);
        else NoteFailure(result);
    }

    /// <summary>
    /// A failed poll leaves device state unknown: the confirmed snapshot, the signature and
    /// the selection are all preserved, and no disconnect notification is raised.
    /// Repeated identical failures are logged once to avoid flooding the log.
    /// </summary>
    private static void NoteFailure(DeviceDiscoveryResult result)
    {
        bool shouldLog;
        int failures;
        lock (_gate)
        {
            _consecutiveFailures++;
            failures = _consecutiveFailures;

            var delay = BackoffFor(failures);
            _nextAllowedPoll = Clock() + delay;

            shouldLog = _lastLoggedFailure != result.Outcome;
            _lastLoggedFailure = result.Outcome;
        }

        // Cancellation during shutdown is expected, never an error.
        if (result.Outcome == DeviceDiscoveryOutcome.Cancelled) return;

        if (shouldLog)
            Log.Warn($"DeviceWatcher: device query {result.Outcome} " +
                     $"(failure {failures}); keeping last known devices. {result.Diagnostic}");
    }

    /// <summary>Exponential backoff, capped so recovery always keeps trying.</summary>
    internal static TimeSpan BackoffFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return TimeSpan.Zero;
        var factor = Math.Min(consecutiveFailures - 1, 16);
        var ticks = BackoffBase.Ticks * (1L << (int)factor);
        return ticks >= BackoffMax.Ticks || ticks <= 0 ? BackoffMax : TimeSpan.FromTicks(ticks);
    }

    /// <summary>Current backoff delay (0 when healthy).</summary>
    internal static TimeSpan CurrentBackoff
    {
        get { lock (_gate) return BackoffFor(_consecutiveFailures); }
    }

    internal static int ConsecutiveFailures { get { lock (_gate) return _consecutiveFailures; } }

    private static void ApplySuccess(DeviceDiscoveryResult result)
    {
        var devices = result.Devices.ToList();
        var signature = Signature(devices);

        List<AdbDevice> previous;
        bool changed;
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _lastLoggedFailure = null;
            _nextAllowedPoll = DateTimeOffset.MinValue;

            changed = signature != _lastSignature;
            previous = _lastConfirmed;
        }

        if (!changed) return;

        // Reconcile and notify OUTSIDE the lock, then commit the signature only once the
        // snapshot has actually been applied.
        Reconcile(previous, devices);

        lock (_gate)
        {
            _lastConfirmed = devices;
            _lastSignature = signature;
        }

        Dispatch(() => DevicesChanged?.Invoke(devices));
    }

    // ---------------- reconciliation (Part D) ----------------

    /// <summary>
    /// The de-duplication key for a poll result. Identical signatures suppress the
    /// DevicesChanged event.
    /// </summary>
    internal static string Signature(IReadOnlyList<AdbDevice> devices)
        => string.Join("|", devices.Select(d => $"{d.Serial}:{d.State}"));

    /// <summary>Difference between two confirmed snapshots.</summary>
    internal sealed record SnapshotDelta(
        IReadOnlyList<AdbDevice> Added,
        IReadOnlyList<AdbDevice> Removed,
        IReadOnlyList<AdbDevice> StateChanged);

    /// <summary>Pure diff, so the notification rules are testable without a UI.</summary>
    internal static SnapshotDelta Diff(IReadOnlyList<AdbDevice> previous, IReadOnlyList<AdbDevice> current)
    {
        var before = previous.ToDictionary(d => d.Serial, StringComparer.Ordinal);
        var after = current.ToDictionary(d => d.Serial, StringComparer.Ordinal);

        var added = current.Where(d => !before.ContainsKey(d.Serial)).ToList();
        var removed = previous.Where(d => !after.ContainsKey(d.Serial)).ToList();
        var stateChanged = current
            .Where(d => before.TryGetValue(d.Serial, out var old) && old.State != d.State)
            .ToList();

        return new SnapshotDelta(added, removed, stateChanged);
    }

    private static void Reconcile(IReadOnlyList<AdbDevice> previous, IReadOnlyList<AdbDevice> current)
    {
        var delta = Diff(previous, current);

        foreach (var dev in delta.Added.Where(d => d.IsAuthorized))
            Toast.Show($"تم اكتشاف جهاز: {dev.DisplayName}");

        foreach (var dev in delta.Removed)
            Toast.Show($"انقطع الاتصال بالجهاز {dev.DisplayName}", ok: false);

        NotifyUnauthorized(current);
        SyncSelection(current);
    }

    /// <summary>
    /// Tells the user once per device that it needs authorizing, instead of repeating the
    /// same guidance on every poll. The reminder re-arms if the device leaves and returns.
    /// </summary>
    private static void NotifyUnauthorized(IReadOnlyList<AdbDevice> devices)
    {
        var unauthorized = devices.Where(d => d.State == "unauthorized").ToList();

        List<AdbDevice> toNotify;
        lock (_gate)
        {
            var present = devices.Select(d => d.Serial).ToHashSet(StringComparer.Ordinal);
            _unauthorizedNotified.RemoveWhere(s => !present.Contains(s));

            toNotify = unauthorized.Where(d => _unauthorizedNotified.Add(d.Serial)).ToList();

            // Re-arm the reminder for any device that has since been authorized.
            foreach (var d in devices.Where(d => d.State != "unauthorized"))
                _unauthorizedNotified.Remove(d.Serial);
        }

        foreach (var dev in toNotify)
            Toast.Show($"الجهاز {dev.DisplayName} بانتظار الإذن — وافق على «تصحيح USB» على الهاتف.",
                ok: false);
    }

    /// <summary>What a confirmed snapshot implies for the currently selected device.</summary>
    internal enum CurrentDeviceAction { None, Disconnect, UpdateState, SelectFirst }

    /// <summary>
    /// Pure decision half of <see cref="SyncSelection"/>: works out what should happen to the
    /// current device without performing any state change or notification.
    /// </summary>
    internal static (CurrentDeviceAction Action, AdbDevice? Target) DecideCurrentDevice(
        AdbDevice? current, IReadOnlyList<AdbDevice> devices)
    {
        if (current != null)
        {
            var match = devices.FirstOrDefault(d => d.Serial == current.Serial);
            if (match == null)
            {
                // Genuinely gone: fall back to another authorized device when one exists.
                var replacement = devices.FirstOrDefault(d => d.IsAuthorized);
                return replacement != null
                    ? (CurrentDeviceAction.SelectFirst, replacement)
                    : (CurrentDeviceAction.Disconnect, null);
            }
            if (match.State != current.State) return (CurrentDeviceAction.UpdateState, match);
            return (CurrentDeviceAction.None, null);
        }

        var first = devices.FirstOrDefault(d => d.IsAuthorized);
        return first != null
            ? (CurrentDeviceAction.SelectFirst, first)
            : (CurrentDeviceAction.None, null);
    }

    private static void SyncSelection(IReadOnlyList<AdbDevice> devices)
    {
        var cur = AppState.CurrentDevice;
        var (action, target) = DecideCurrentDevice(cur, devices);

        switch (action)
        {
            case CurrentDeviceAction.Disconnect:
                Dispatch(() => AppState.SetCurrent(null));
                break;

            case CurrentDeviceAction.UpdateState:
                Dispatch(() =>
                {
                    cur!.State = target!.State;
                    AppState.SetCurrent(cur);
                });
                break;

            case CurrentDeviceAction.SelectFirst:
                Dispatch(() => AppState.SetCurrent(target));
                break;
        }
    }

    /// <summary>Runs an action on the UI thread when one exists; inline when headless.</summary>
    private static void Dispatch(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app == null) { action(); return; }
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action);
    }
}

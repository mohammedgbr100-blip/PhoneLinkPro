using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Views;

public partial class DashboardView : UserControl, IRefreshable
{
    private bool _busy;

    /// <summary>
    /// The most recent watcher/refresh snapshot observed while _busy was already claimed by
    /// another operation, coalesced to the latest arrival — paired with the sequence number it
    /// was assigned when observed (Batch 3D), so whoever eventually dequeues it can tell
    /// whether something newer has already been applied in the meantime.
    /// </summary>
    private (List<AdbDevice> Devices, int Sequence)? _missedUpdate;

    /// <summary>
    /// Bumped once for every snapshot this instance observes — from RefreshAsync's own
    /// discovery attempt and from every DeviceWatcher.DevicesChanged event alike — so ordering
    /// can be judged consistently between the two entry points (Batch 3D).
    /// </summary>
    private int _nextSequence;

    /// <summary>
    /// The sequence of the most recent snapshot actually applied to DeviceList/AppState.
    /// ApplyDevicesCoreAsync refuses to apply anything whose sequence is not strictly greater,
    /// so a snapshot left queued behind a result that has since been applied — whether because
    /// the operation that queued behind it simply finished before reaching it, or because that
    /// operation threw before it could drain the queue at all — can never overwrite that result
    /// once it is eventually attempted (Batch 3D; closes the ordering gap identified during the
    /// Batch 3C review).
    /// </summary>
    private int _lastAppliedSequence;

    /// <summary>True while this instance holds the DeviceWatcher.DevicesChanged subscription.</summary>
    private bool _watcherSubscribed;

    /// <summary>
    /// Bumped every time this instance is Unloaded (Batch 3C). Every async operation captures
    /// the current value before its first await; if this instance is Unloaded any time after
    /// that capture, the captured value no longer equals <see cref="_generation"/> and the
    /// operation is stale. Where an operation happened to start relative to Loaded/Unloaded is
    /// irrelevant to correctness — MainWindow calls OnShown before the view is even attached to
    /// the tree, for instance — only whether an Unloaded occurred after the capture matters,
    /// which <see cref="IsCurrent"/> re-checks after every subsequent await.
    /// </summary>
    private int _generation;

    /// <summary>True when the caller's captured generation is still the active one.</summary>
    private bool IsCurrent(int generation) => generation == _generation;

    /// <summary>
    /// STA self-test harness switch (mirrors MainWindow.SuppressStartupForHarness): when set,
    /// Loaded still subscribes exactly as in production, but skips the device refresh. This
    /// build's own output directory carries the real engine binaries (CopyEngineToBuild), so
    /// unlike the xUnit test host — where no engine folder exists and ToolLocator.AdbAvailable
    /// is naturally false — PLP_SELFTEST would otherwise start a real PhoneLink.Bridge.exe.
    /// Never set outside PLP_SELFTEST.
    /// </summary>
    internal static bool SuppressRefreshForHarness;

    /// <summary>
    /// Test-only replacement for AdbService.EnrichAsync (Batch 3C). AdbService exposes no
    /// injectable seam for this call and must not gain one just for a test, so the seam lives
    /// here instead: null in production, meaning "?? AdbService.EnrichAsync" below always
    /// resolves to the real call. Settable only by DashboardStaleContinuationSelfTest, to pause
    /// an authorized device's enrichment at a controlled point via a TaskCompletionSource and
    /// prove the stale-continuation guard without ever starting a real adb/bridge process.
    /// </summary>
    internal static Func<AdbDevice, Task>? EnrichOverrideForTests;

    /// <summary>
    /// Test-only replacement for AdbService.DiscoverDevicesAsync (Batch 3D). AdbService exposes
    /// no injectable seam for this call and must not gain one just for a test, so the seam lives
    /// here instead: null in production, meaning the "??" fallback below always resolves to the
    /// real call (with its default CancellationToken). Settable only by the Batch 3D
    /// missed-update-ordering self-test, to pause RefreshAsync's discovery at a controlled point
    /// via a TaskCompletionSource and prove the ordering guard without ever starting a real
    /// adb/bridge process.
    /// </summary>
    internal static Func<Task<DeviceDiscoveryResult>>? DiscoverDevicesOverrideForTests;

    /// <summary>Reveal-toggle for the hero's masked serial (Batch 4A — mirrors MainWindow's
    /// own sidebar reveal toggle; pure view state. M2 repair: reset only when the hero's device
    /// IDENTITY changes — see <see cref="UpdateHero"/> — not on every routine repaint of the
    /// same device.</summary>
    private bool _serialRevealed;

    /// <summary>The device the hero currently displays (Batch 4A — pure view state, used only
    /// so the reveal toggle can read its unmasked serial without re-scanning DeviceList).</summary>
    private AdbDevice? _heroDevice;

    /// <summary>
    /// Test-only read of the hero device (Batch 4A repair). Before M2's hero/list separation,
    /// the Batch 3B/3C/3D self-tests could confirm "which device won a race" by reading
    /// DeviceList.ItemsSource directly, because it always mirrored the full applied snapshot.
    /// Now that the hero device is deliberately EXCLUDED from that list, a solo device under
    /// test never appears there at all — this seam is the honest replacement, reflecting
    /// exactly what ApplyDevicesCoreAsync's own UpdateHero(hero) call just painted, regardless
    /// of the device's authorization state (DeviceList only ever holds the OTHER devices).
    /// </summary>
    internal AdbDevice? HeroDeviceForTests => _heroDevice;

    /// <summary>
    /// Test-only causal signal (Batch 5A). Invoked exactly once, at the end of RefreshAsync's
    /// or ApplyDevicesAsync's own finally block, immediately after this instance's busy gate is
    /// released for a still-current generation — i.e. only once any drain that operation was
    /// responsible for has already finished. Lets the missed-update-ordering self-test await the
    /// precise moment one specific operation (and everything it coalesced/drained) has fully
    /// settled, instead of guessing how many dispatcher pumps "should" be enough. Null in
    /// production; invoking it never changes application behavior.
    /// </summary>
    internal static Action? OperationSettledForTests;

    /// <summary>Test-only read of the busy gate (Batch 5A) — see <see cref="_busy"/>.</summary>
    internal bool IsBusyForTests => _busy;

    /// <summary>Test-only read of the coalesced queue (Batch 5A) — see <see cref="_missedUpdate"/>.</summary>
    internal (List<AdbDevice> Devices, int Sequence)? MissedUpdateForTests => _missedUpdate;

    public DashboardView()
    {
        InitializeComponent();
        Loaded += DashboardView_Loaded;
        Unloaded += DashboardView_Unloaded;
    }

    // MainWindow caches one DashboardView instance per process and swaps it in and out of
    // PageHost.Content as the user navigates; that reconnects/disconnects it from the visual
    // tree, so Loaded/Unloaded fire repeatedly on the SAME instance rather than once per page
    // visit. The DeviceWatcher.DevicesChanged subscription must track exactly that lifetime.
    private void DashboardView_Loaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        if (!SuppressRefreshForHarness) _ = RefreshAsync();
    }

    private void DashboardView_Unloaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        // Batch 3C: invalidate anything in flight and release the busy gate, so an abandoned
        // continuation can never mutate the UI/AppState later, and a fresh Loaded is never
        // blocked by a busy flag that belongs to work nobody is waiting on any more.
        _generation++;
        _busy = false;
        _missedUpdate = null;
    }

    private void Subscribe()
    {
        if (_watcherSubscribed) return;
        DeviceWatcher.DevicesChanged += OnDevicesChanged;
        // Batch 4A mirror/live-state repair: ScrcpyService already raises this event (UI
        // thread when an Application exists — see its own XML doc) whenever a session starts,
        // survives startup, or ends, which is exactly what UpdateHero's old ActiveCount read
        // was missing — nothing forced a repaint after a session change, so the live/offline
        // dot could stay wrong indefinitely. Tied to the same _watcherSubscribed guard as the
        // DeviceWatcher subscription above: one Loaded/Unloaded lifecycle, no new bool, no
        // polling, no timer.
        ScrcpyService.SessionsChanged += OnSessionsChanged;
        _watcherSubscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_watcherSubscribed) return;
        DeviceWatcher.DevicesChanged -= OnDevicesChanged;
        ScrcpyService.SessionsChanged -= OnSessionsChanged;
        _watcherSubscribed = false;
    }

    /// <summary>
    /// Named ScrcpyService.SessionsChanged handler, wired only while Loaded (Batch 4A). Only
    /// repaints the hero's live/offline state — never resets the serial-reveal toggle and
    /// never re-derives the rest of the hero, exactly like a stale generation's UpdateHero call
    /// would be wrong to run, a session event for a hero that no longer exists must be a no-op.
    /// </summary>
    private void OnSessionsChanged()
    {
        if (_heroDevice == null) return;
        UpdateHeroLiveState(_heroDevice);
    }

    public void OnShown() => _ = RefreshAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        SetBusyVisual(true);
        var generation = _generation;
        // Batch 3D: claimed now — the moment this refresh commits to being an observation
        // attempt — so a watcher event that arrives later, even while discovery is still in
        // flight, is correctly treated as newer than whatever this refresh goes on to apply.
        var sequence = ++_nextSequence;
        RefreshBtn.IsEnabled = false;
        try
        {
            if (!ToolLocator.AdbAvailable)
            {
                ShowEmpty("تعذّر العثور على محرّك التشغيل — يُرجى إعادة تثبيت البرنامج.");
                DeviceList.ItemsSource = null;
            }
            else
            {
                DeviceWatcher.Invalidate();
                var discover = DiscoverDevicesOverrideForTests ?? (() => AdbService.DiscoverDevicesAsync());
                var result = await discover();

                if (result.IsSuccess)
                {
                    await ApplyDevicesCoreAsync(result.Devices.ToList(), generation, sequence);
                }
                else
                {
                    // Device state is unknown, not empty: keep showing what we last confirmed
                    // rather than wrongly reporting that everything unplugged (DEV-01).
                    var known = DeviceWatcher.LastConfirmed;
                    if (known.Count > 0) await ApplyDevicesCoreAsync(known.ToList(), generation, sequence);
                    else if (IsCurrent(generation))
                        ShowEmpty("تعذّر قراءة قائمة الأجهزة — سيُعاد المحاولة تلقائياً.");
                }
            }

            // Batch 3D: a watcher snapshot that arrived while this refresh held _busy must not
            // be left dangling for the next, unrelated caller to dequeue and misapply out of
            // order — drain it here, in order, before this refresh gives up ownership.
            await DrainQueuedUpdatesAsync(generation);
        }
        finally
        {
            // A stale generation's busy flag was already released by Unloaded — or, if a
            // newer refresh is now running, belongs to that one. Either way, never touch it.
            if (IsCurrent(generation))
            {
                _busy = false;
                RefreshBtn.IsEnabled = true;
                SetBusyVisual(false);
                OperationSettledForTests?.Invoke();
            }
        }
    }

    /// <summary>Named DeviceWatcher.DevicesChanged handler, wired only while Loaded.</summary>
    private void OnDevicesChanged(List<AdbDevice> devices) => _ = ApplyDevicesAsync(devices);

    /// <summary>
    /// Called by DeviceWatcher when devices appear/disappear/change state. A watcher update
    /// arriving while a manual refresh is running is queued rather than dropped (DEV-02),
    /// coalesced to the latest arrival if more than one queues up before it can be serviced.
    /// </summary>
    private async Task ApplyDevicesAsync(List<AdbDevice> devices)
    {
        var sequence = ++_nextSequence;
        if (_busy) { _missedUpdate = (devices, sequence); return; }

        _busy = true;
        SetBusyVisual(true);
        var generation = _generation;
        try
        {
            await ApplyDevicesCoreAsync(devices, generation, sequence);
            await DrainQueuedUpdatesAsync(generation);
        }
        finally
        {
            if (IsCurrent(generation))
            {
                _busy = false;
                SetBusyVisual(false);
                OperationSettledForTests?.Invoke();
            }
        }
    }

    /// <summary>
    /// Applies whatever snapshot is queued in _missedUpdate, coalesced to the latest, before
    /// returning control to the caller (Batch 3D). Used by both RefreshAsync and
    /// ApplyDevicesAsync, so a snapshot that arrived while either one owned _busy is serviced by
    /// that same operation instead of being left for some later, unrelated caller to dequeue —
    /// the fix for the ordering gap the Batch 3C review identified. Stops the instant this
    /// generation goes stale, so an abandoned drain can never consume a newer generation's
    /// queue; each dequeued snapshot is still subject to ApplyDevicesCoreAsync's own sequence
    /// check, so even a snapshot left un-drained by an exception elsewhere can never overwrite a
    /// result that was applied after it.
    /// </summary>
    private async Task DrainQueuedUpdatesAsync(int generation)
    {
        while (IsCurrent(generation) && _missedUpdate is { } queued)
        {
            _missedUpdate = null;
            await ApplyDevicesCoreAsync(queued.Devices, generation, queued.Sequence);
        }
    }

    /// <summary>
    /// Applies one device snapshot. <paramref name="generation"/> is the value captured by the
    /// caller before its first await; if this instance has since been Unloaded (Batch 3C), the
    /// generation no longer matches and every mutation below is skipped — both the entry check
    /// (the caller's own earlier await may already have gone stale) and the check after this
    /// method's own await (enrichment may still be running when the view is unloaded).
    /// <paramref name="sequence"/> is the order this snapshot was observed in, relative to every
    /// other snapshot this instance has ever observed (Batch 3D); if a later-observed snapshot
    /// has already been applied, this one is discarded rather than overwriting it.
    /// </summary>
    private async Task ApplyDevicesCoreAsync(List<AdbDevice> devices, int generation, int sequence)
    {
        if (!IsCurrent(generation) || sequence <= _lastAppliedSequence) return;
        _lastAppliedSequence = sequence;

        if (devices.Count == 0)
        {
            ShowEmpty("لا توجد أجهزة متصلة");
            DeviceList.ItemsSource = null;
            return;
        }

        // Keep current selection if still present; otherwise pick first authorized.
        var keep = devices.FirstOrDefault(d => d.Serial == AppState.CurrentSerial)
                   ?? devices.FirstOrDefault(d => d.IsAuthorized);
        var hero = keep ?? devices[0];
        if (keep != null) AppState.SetCurrent(keep);

        ApplyOtherDevicesList(devices, hero);
        UpdateHero(hero);

        // Fetch details for all devices in parallel.
        await Task.WhenAll(devices.Where(d => d.IsAuthorized).Select(EnrichOverrideForTests ?? AdbService.EnrichAsync));
        if (!IsCurrent(generation)) return;   // the view moved on while enrichment was in flight

        // Refresh the list (and sidebar) to show enriched details.
        DeviceList.Items.Refresh();
        if (keep != null && AppState.CurrentSerial == keep.Serial) AppState.SetCurrent(keep);
        UpdateHero(hero);   // enrichment may have just populated model/battery/etc.
    }

    /// <summary>
    /// Populates the lower list with every OTHER discovered device — never the one the hero
    /// above is already showing (Batch 4A hero/list de-duplication). Same AdbDevice instances
    /// as the full snapshot, so the post-enrichment DeviceList.Items.Refresh() above still
    /// repaints them; the heading and the list container are hidden together whenever there is
    /// nothing left to show, so a single-device connection never leaves an orphan heading.
    /// </summary>
    private void ApplyOtherDevicesList(List<AdbDevice> devices, AdbDevice hero)
    {
        var others = devices.Where(d => d.Serial != hero.Serial).ToList();
        DeviceList.ItemsSource = others;
        var visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OthersHeading.Visibility = visibility;
        DeviceList.Visibility = visibility;
    }

    private void ShowEmpty(string message)
    {
        HeroEmptyTitle.Text = message;
        OthersHeading.Visibility = Visibility.Collapsed;
        DeviceList.Visibility = Visibility.Collapsed;
        UpdateHero(null);
    }

    // ---------------------------------------------------------------- hero (Batch 4A, view-only)
    // Everything below reads state that ApplyDevicesCoreAsync/RefreshAsync already computed and
    // writes it to named XAML elements exactly like MainWindow.UpdateSidebarDevice does for the
    // shell chip — no new service calls, no new subscriptions, no new async operations.

    /// <summary>
    /// Reflects the current busy/refresh state into the header. Only shown while the hero has
    /// no device to display, so a routine background poll of an already-connected device never
    /// dims or flickers the hero — the same "truthful, not decorative" rule the empty/offline
    /// states follow.
    /// </summary>
    private void SetBusyVisual(bool busy)
        => LoadingHint.Visibility = busy && HeroEmpty.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Paints the hero for one device, or the empty variant when <paramref name="device"/> is
    /// null. Called at the same points ApplyDevicesCoreAsync already updates DeviceList/AppState,
    /// so the hero never lags a frame behind the list it summarizes.
    /// </summary>
    private void UpdateHero(AdbDevice? device)
    {
        // M2 repair: UpdateHero runs on every routine refresh/poll/enrichment repaint of the
        // SAME device, not only when the hero's identity actually changes — resetting
        // _serialRevealed unconditionally here reveals-then-immediately-re-masks the serial on
        // every ordinary same-device tick. Reveal now survives exactly while the hero still
        // represents the same device identity (same serial, ordinal comparison — the app's
        // existing serial-equality rule), and resets only when that identity changes: a
        // different serial becomes hero, or the hero disappears entirely.
        var previousSerial = _heroDevice?.Serial;
        var identityChanged = device == null
            || previousSerial == null
            || !string.Equals(previousSerial, device.Serial, StringComparison.Ordinal);
        if (identityChanged) _serialRevealed = false;
        _heroDevice = device;

        if (device == null)
        {
            HeroLive.Visibility = Visibility.Collapsed;
            HeroEmpty.Visibility = Visibility.Visible;
            LoadingHint.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        HeroEmpty.Visibility = Visibility.Collapsed;
        LoadingHint.Visibility = Visibility.Collapsed;
        HeroLive.Visibility = Visibility.Visible;

        // M4 repair: a device only really IS "الجهاز الحالي" when it is the one
        // ApplyDevicesCoreAsync just committed to AppState — the caption above the name line
        // must say so truthfully rather than always claiming "current" even for a bare
        // fallback shown only because no authorized device exists yet.
        HeroDeviceLabel.Text = device.Serial == AppState.CurrentSerial ? "الجهاز الحالي" : "الجهاز المكتشف";
        // M4 repair: the old fallback masked the serial here too, so an unauthorized/unknown
        // device showed the SAME masked value on both the name line and the serial line below
        // it. A truthful state label never repeats the serial — the raw/masked value belongs
        // only in the LTR mono HeroSerial field.
        HeroDeviceName.Text = ShellLayout.DeviceNameOrFallback(device.Model, device.DisplayName, device.State);
        HeroConnectionBadge.Text = device.ConnectionLabel;
        UpdateRevealAffordance();

        UpdateHeroLiveState(device);

        HeroUnauthorizedHint.Visibility =
            device.State == "unauthorized" ? Visibility.Visible : Visibility.Collapsed;

        HeroAndroidText.Text = string.IsNullOrWhiteSpace(device.AndroidVersion)
            ? "—" : "Android " + device.AndroidVersion;
        HeroResolutionText.Text = string.IsNullOrWhiteSpace(device.Resolution) ? "—" : device.Resolution;

        var hasBattery = device.IsAuthorized && device.Battery >= 0;
        HeroBattery.Visibility = hasBattery ? Visibility.Visible : Visibility.Hidden;
        if (hasBattery)
        {
            var pct = Math.Clamp(device.Battery, 0, 100);
            HeroBatteryVal.Text = pct + "%";
            HeroBatteryFill.Width = HeroBatteryTrack.Width * pct / 100.0;
        }

        HeroStartBtn.Tag = device.Serial;
        HeroStartBtn.IsEnabled = device.IsAuthorized;
        AutomationProperties.SetName(HeroStartBtn,
            device.IsAuthorized
                ? $"بدء البث من {ShellLayout.DeviceNameOrFallback(device.Model, device.DisplayName, device.State)}"
                : "بدء البث — الجهاز غير مصرّح له بعد");
    }

    /// <summary>
    /// Repaints only the state dot/text that a mirror session change can affect (Batch 4A
    /// mirror/live-state repair) — split out of UpdateHero so <see cref="OnSessionsChanged"/>
    /// can call it without also resetting the serial-reveal toggle or re-deriving every other
    /// hero field UpdateHero owns.
    /// </summary>
    private void UpdateHeroLiveState(AdbDevice device)
    {
        var live = device.IsAuthorized && ScrcpyService.ActiveCount > 0;
        var (dot, text) = live
            ? ("Brush.State.Live", "يتم بث الشاشة الآن")
            : device.IsAuthorized
                ? ("Brush.State.Success", device.StateLabel)
                : device.State == "unauthorized"
                    ? ("Brush.State.Warning", device.StateLabel)
                    : ("Brush.Text.Muted", device.StateLabel);
        HeroStateDot.Fill = (Brush)FindResource(dot);
        HeroStateText.Text = text;
        HeroStateText.Foreground = (Brush)FindResource(dot);
    }

    private void UpdateRevealAffordance()
    {
        if (_heroDevice == null) return;
        HeroSerial.Text = _serialRevealed ? _heroDevice.Serial : ShellLayout.MaskSerial(_heroDevice.Serial);
        var label = _serialRevealed ? "إخفاء الرقم التسلسلي" : "إظهار الرقم التسلسلي";
        HeroRevealBtn.ToolTip = label;
        AutomationProperties.SetName(HeroRevealBtn, label);
    }

    private void RevealSerial_Click(object sender, RoutedEventArgs e)
    {
        if (_heroDevice == null) return;
        _serialRevealed = !_serialRevealed;
        UpdateRevealAffordance();
    }

    // ---------------------------------------------------------------- existing actions (unchanged)

    private void SelectAndMirror_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string serial) return;
        // Batch 4A: DeviceList.ItemsSource is now the OTHER-devices list (the hero device is
        // deliberately excluded from it — ApplyOtherDevicesList), so HeroStartBtn's own click
        // must also check _heroDevice or its clicks would silently no-op once the hero is the
        // only authorized device connected.
        var dev = _heroDevice != null && _heroDevice.Serial == serial
            ? _heroDevice
            : (DeviceList.ItemsSource as IEnumerable<AdbDevice>)?.FirstOrDefault(d => d.Serial == serial);
        if (dev == null) return;

        AppState.SetCurrent(dev);

        // One-click: launch a balanced "high" session immediately. A second click while the
        // session is starting must not spawn a second engine.
        b.IsEnabled = false;
        try
        {
            var result = ScrcpyService.Launch(ScrcpyService.Preset("high", serial));
            Toast.Show(result.Started ? $"جارٍ بدء البث من {dev.DisplayName}..." : result.Message,
                result.Started);
        }
        finally
        {
            b.IsEnabled = true;
        }
    }

    /// <summary>
    /// Opens the guided device-setup wizard (Batch 4B), seeded with this view's own already-known
    /// snapshot — the hero device plus every other discovered device — so no second discovery
    /// call is made just to open a dialog. Manually opened only; there is no automatic first-run
    /// trigger. M4 repair: a successful Finish now repaints this Dashboard immediately from the
    /// wizard's own result, rather than waiting for an unrelated DeviceWatcher event.
    /// </summary>
    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var devices = new List<AdbDevice>();
        if (_heroDevice != null) devices.Add(_heroDevice);
        if (DeviceList.ItemsSource is IEnumerable<AdbDevice> others) devices.AddRange(others);

        var wizard = new Windows.DeviceSetupWizardWindow(devices, _heroDevice?.Serial)
            { Owner = Window.GetWindow(this) };
        var accepted = wizard.ShowDialog();
        if (accepted == true && wizard.Result != null) ApplySetupResult(wizard.Result);
    }

    /// <summary>
    /// M4 repair: the one authoritative path that applies a successful device-setup-wizard
    /// result to this Dashboard — AppState, hero, other-devices list and focus all update
    /// synchronously from the SAME snapshot the wizard just confirmed, with no second discovery
    /// call and no dependency on a DeviceWatcher event that may never arrive (or may arrive much
    /// later) to trigger the repaint. Bumping the applied-sequence counter (Batch 3D's ordering
    /// guard) means an unrelated refresh/watcher snapshot that was already in flight when the
    /// wizard opened can never overwrite this result once it resolves.
    /// </summary>
    internal void ApplySetupResult(Windows.DeviceSetupWizardWindow.SetupWizardResult result)
    {
        _lastAppliedSequence = ++_nextSequence;
        AppState.SetCurrent(result.SelectedDevice);
        ApplyOtherDevicesList(result.LatestSnapshot.ToList(), result.SelectedDevice);
        UpdateHero(result.SelectedDevice);

        if (HeroStartBtn.IsEnabled) HeroStartBtn.Focus();
        else RefreshBtn.Focus();
    }
}

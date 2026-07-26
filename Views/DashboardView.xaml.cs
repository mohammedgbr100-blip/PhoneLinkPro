using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Views;

public partial class DashboardView : UserControl, IRefreshable
{
    private bool _busy;
    private List<AdbDevice>? _missedUpdate;

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
        _watcherSubscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_watcherSubscribed) return;
        DeviceWatcher.DevicesChanged -= OnDevicesChanged;
        _watcherSubscribed = false;
    }

    public void OnShown() => _ = RefreshAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        var generation = _generation;
        RefreshBtn.IsEnabled = false;
        try
        {
            if (!ToolLocator.AdbAvailable)
            {
                ShowEmpty("تعذّر العثور على محرّك التشغيل — يُرجى إعادة تثبيت البرنامج.");
                DeviceList.ItemsSource = null;
                return;
            }

            DeviceWatcher.Invalidate();
            var result = await AdbService.DiscoverDevicesAsync();

            if (result.IsSuccess)
            {
                await ApplyDevicesCoreAsync(result.Devices.ToList(), generation);
            }
            else
            {
                // Device state is unknown, not empty: keep showing what we last confirmed
                // rather than wrongly reporting that everything unplugged (DEV-01).
                var known = DeviceWatcher.LastConfirmed;
                if (known.Count > 0) await ApplyDevicesCoreAsync(known.ToList(), generation);
                else if (IsCurrent(generation))
                    ShowEmpty("تعذّر قراءة قائمة الأجهزة — سيُعاد المحاولة تلقائياً.");
            }
        }
        finally
        {
            // A stale generation's busy flag was already released by Unloaded — or, if a
            // newer refresh is now running, belongs to that one. Either way, never touch it.
            if (IsCurrent(generation))
            {
                _busy = false;
                RefreshBtn.IsEnabled = true;
            }
        }
    }

    /// <summary>Named DeviceWatcher.DevicesChanged handler, wired only while Loaded.</summary>
    private void OnDevicesChanged(List<AdbDevice> devices) => _ = ApplyDevicesAsync(devices);

    /// <summary>
    /// Called by DeviceWatcher when devices appear/disappear/change state. A watcher update
    /// arriving while a manual refresh is running is queued rather than dropped (DEV-02).
    /// </summary>
    private async Task ApplyDevicesAsync(List<AdbDevice> devices)
    {
        if (_busy) { _missedUpdate = devices; return; }

        _busy = true;
        var generation = _generation;
        try
        {
            await ApplyDevicesCoreAsync(devices, generation);

            // Service whatever arrived while we were busy, coalesced to the latest. Stops the
            // moment this generation goes stale, so an abandoned loop can never consume a
            // _missedUpdate meant for whichever generation is current now.
            while (IsCurrent(generation) && _missedUpdate is { } queued)
            {
                _missedUpdate = null;
                await ApplyDevicesCoreAsync(queued, generation);
            }
        }
        finally
        {
            if (IsCurrent(generation)) _busy = false;
        }
    }

    /// <summary>
    /// Applies one device snapshot. <paramref name="generation"/> is the value captured by the
    /// caller before its first await; if this instance has since been Unloaded (Batch 3C), the
    /// generation no longer matches and every mutation below is skipped — both the entry check
    /// (the caller's own earlier await may already have gone stale) and the check after this
    /// method's own await (enrichment may still be running when the view is unloaded).
    /// </summary>
    private async Task ApplyDevicesCoreAsync(List<AdbDevice> devices, int generation)
    {
        if (!IsCurrent(generation)) return;

        if (devices.Count == 0)
        {
            ShowEmpty("لا توجد أجهزة متصلة");
            DeviceList.ItemsSource = null;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        DeviceList.ItemsSource = devices;

        // Keep current selection if still present; otherwise pick first authorized.
        var keep = devices.FirstOrDefault(d => d.Serial == AppState.CurrentSerial)
                   ?? devices.FirstOrDefault(d => d.IsAuthorized);
        if (keep != null) AppState.SetCurrent(keep);

        // Fetch details for all devices in parallel.
        await Task.WhenAll(devices.Where(d => d.IsAuthorized).Select(EnrichOverrideForTests ?? AdbService.EnrichAsync));
        if (!IsCurrent(generation)) return;   // the view moved on while enrichment was in flight

        // Refresh the list (and sidebar) to show enriched details.
        DeviceList.Items.Refresh();
        if (keep != null && AppState.CurrentSerial == keep.Serial) AppState.SetCurrent(keep);
    }

    private void ShowEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void SelectAndMirror_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string serial) return;
        var devices = DeviceList.ItemsSource as IEnumerable<AdbDevice>;
        var dev = devices?.FirstOrDefault(d => d.Serial == serial);
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

    private void Help_Click(object sender, RoutedEventArgs e)
        => new Windows.OnboardingWindow { Owner = Window.GetWindow(this) }.ShowDialog();
}

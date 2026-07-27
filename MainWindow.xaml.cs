using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using ScrcpyDeck.Views;
using ScrcpyDeck.Windows;

namespace ScrcpyDeck;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _cache = new();

    /// <summary>True once the ADB server and device watcher have been started.</summary>
    private bool _engineServicesStarted;

    /// <summary>Dedup key of the last integrity dialog shown (one dialog per distinct state).</summary>
    private string? _lastIntegrityNotificationKey;

    /// <summary>Shell status-strip engine state (display only — never drives behavior).</summary>
    private enum EngineStripState { Scanning, Ready, Blocked }

    private EngineStripState _engineStripState = EngineStripState.Scanning;

    /// <summary>
    /// Sidebar serial chip reveal toggle. Reset to masked by every UpdateSidebarDevice pass,
    /// which means: the selected device changing (AppState.CurrentDeviceChanged), AND any
    /// mirror-session change (ScrcpyService.SessionsChanged, via OnSessionsChanged). A reveal
    /// is therefore never carried across either kind of state change.
    /// </summary>
    private bool _serialRevealed;

    /// <summary>Long-lived event subscriptions, released once when the window closes.</summary>
    private readonly ShellSubscriptions _subscriptions = new();

    /// <summary>Set in Closed; after this no handler may touch the visual tree.</summary>
    private bool _closed;

    /// <summary>
    /// STA harness switch. When set, Loaded does NOTHING: no consent, no integrity
    /// verification, no engine services, no navigation, no onboarding. The shell self-tests
    /// need a real window to measure chrome and subscriptions, and must never reach
    /// StartEngineServicesAsync — that would start the bridge and could raise a firewall
    /// prompt on a developer or build machine.
    /// </summary>
    internal static bool SuppressStartupForHarness;

    /// <summary>
    /// A static service event can still fire between Closing and Closed, and again while the
    /// application shuts down. Handlers check this before dereferencing any shell element.
    /// </summary>
    private bool CanUpdateShell => ShellSubscriptions.CanDispatch(
        _closed, AppLifecycle.IsShuttingDown, Dispatcher.HasShutdownStarted);

    public MainWindow()
    {
        InitializeComponent();
        ShellWindowBounds.ApplyStartupBounds(this);
        // Design-QA tool mode parks the shell offscreen BEFORE it is ever shown, so the
        // capture never flashes on the user's desktop.
        if (ShellSnapshotToolMode.IsActive) ShellSnapshotToolMode.ConfigureForCapture(this);
        LoadBranding();
        UpdateCompactMode();

        // Every long-lived subscription is paired with its release in one place, so the
        // window holds exactly one of each and gives them all back when it closes.
        _subscriptions.Add(
            () => AppState.CurrentDeviceChanged += OnCurrentDeviceChanged,
            () => AppState.CurrentDeviceChanged -= OnCurrentDeviceChanged);
        _subscriptions.Add(
            () => DeviceWatcher.DevicesChanged += OnDevicesChanged,    // raised on the UI dispatcher
            () => DeviceWatcher.DevicesChanged -= OnDevicesChanged);
        _subscriptions.Add(
            () => ScrcpyService.SessionsChanged += OnSessionsChanged,  // raised on the UI dispatcher
            () => ScrcpyService.SessionsChanged -= OnSessionsChanged);
        _subscriptions.Add(
            () => Toast.Requested += ShowToast,
            () => Toast.Requested -= ShowToast);

        SizeChanged += OnShellSizeChanged;
        StateChanged += OnShellStateChanged;
        Closed += OnShellClosed;

        UpdateEngineStrip();
        UpdateSidebarDevice();

        // Deterministic cleanup before the window goes away; Application.OnExit is the
        // backstop for paths that bypass this (AppLifecycle.Shutdown runs only once).
        Closing += (_, _) => AppLifecycle.Shutdown();
        Loaded += async (_, _) =>
        {
            // STA self-test harness: measure the shell, touch nothing else.
            if (SuppressStartupForHarness) return;

            // Design-QA tool mode owns this window: render the SHELL CHROME only. No consent,
            // no integrity verification, no engine services, no onboarding — and deliberately
            // no navigation either, because constructing a feature page queries the device
            // engine on load (DashboardView.Loaded -> AdbService.DiscoverDevicesAsync), which
            // would start the bridge this mode must never start. ShellSnapshotToolMode drives
            // the capture and the process exit code.
            if (ShellSnapshotToolMode.IsActive) return;

            // First-run flow: privacy consent (blocking) then onboarding tutorial.
            if (!AppSettings.ConsentAccepted)
            {
                var consent = new ConsentWindow { Owner = this };
                var ok = consent.ShowDialog() == true;
                if (!ok) { Application.Current.Shutdown(); return; }
                AppSettings.ConsentAccepted = true;
            }

            // SEC-01: engine binaries (bridge + mirror engine) may only run after the
            // embedded manifest has confirmed them. The window itself always opens, so the
            // repair guidance, About and Policy pages stay reachable either way.
            var integrity = await EngineIntegrityVerifier.VerifyAsync();
            if (integrity.IsValid) await StartEngineServicesAsync();
            else PresentIntegrityFailure(integrity);

            var start = Environment.GetEnvironmentVariable("PLP_START");
            if (!string.IsNullOrWhiteSpace(start)) SelectNav(start);
            else Navigate("Dashboard");

            if (!AppSettings.OnboardingSeen)
            {
                new OnboardingWindow { Owner = this }.ShowDialog();
                AppSettings.OnboardingSeen = true;
            }
        };
    }

    // ---------------- shell chrome (Batch 3A) ----------------

    /// <summary>Keeps a maximised shell inside the work area instead of under the taskbar.</summary>
    private void OnShellStateChanged(object? sender, EventArgs e)
        => ShellWindowBounds.ApplyMaximizeConstraint(this);

    /// <summary>
    /// Single teardown point: releases every long-lived subscription and the window's own
    /// event handlers, then blocks any further shell updates. Idempotent through
    /// <see cref="ShellSubscriptions.Dispose"/>, so a repeated Close is harmless.
    /// </summary>
    private void OnShellClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _subscriptions.Dispose();

        SizeChanged -= OnShellSizeChanged;
        StateChanged -= OnShellStateChanged;
        Closed -= OnShellClosed;
    }

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!CanUpdateShell) return;
        UpdateCompactMode();
    }

    private void UpdateCompactMode()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var compact = ShellLayout.IsCompactWidth(width);
        if (compact != ShellChrome.GetIsCompact(this))
            Log.Info($"Shell: nav mode -> {(compact ? "compact rail" : "full sidebar")} (window width {width:F0})");
        ShellChrome.SetIsCompact(this, compact);
    }

    // ---------------- engine integrity (SEC-01) ----------------

    /// <summary>
    /// Starts (or resumes after a successful Retry) everything that talks to the engine.
    /// Idempotent: the server start and watcher start are both safe to reach twice.
    /// </summary>
    private async Task StartEngineServicesAsync()
    {
        IntegrityBanner.Visibility = Visibility.Collapsed;
        PageHost.IsEnabled = true;
        _engineStripState = EngineStripState.Ready;
        UpdateEngineStrip();

        if (_engineServicesStarted)
        {
            DeviceWatcher.RequestRefresh();   // resume promptly after a repair
            return;
        }
        _engineServicesStarted = true;

        // HW-INSTALL-001: only the process that started the server may stop it at exit.
        AppLifecycle.NoteEngineServicesStarted();
        await AdbService.StartServerAsync();
        DeviceWatcher.Start();
    }

    /// <summary>
    /// Persistent, non-fatal failure state: engine-dependent pages go inert, the banner
    /// explains what to do, and ONE dialog is shown per distinct integrity state — never one
    /// per watcher poll. The app keeps running; nothing is killed.
    /// </summary>
    private void PresentIntegrityFailure(EngineIntegrityResult result)
    {
        PageHost.IsEnabled = false;
        IntegrityBanner.Visibility = Visibility.Visible;
        IntegrityBannerText.Text = EngineIntegrityMessages.For(result);
        _engineStripState = EngineStripState.Blocked;
        UpdateEngineStrip();

        var key = EngineIntegrityMessages.NotificationKey(result);
        if (key == _lastIntegrityNotificationKey) return;
        _lastIntegrityNotificationKey = key;

        MessageBox.Show(this, EngineIntegrityMessages.For(result), "PhoneLink Pro",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void IntegrityRetry_Click(object sender, RoutedEventArgs e)
    {
        IntegrityRetryBtn.IsEnabled = false;
        try
        {
            var result = await EngineIntegrityVerifier.RetryAsync();
            if (result.IsValid)
            {
                _lastIntegrityNotificationKey = null;
                await StartEngineServicesAsync();
                Toast.Show("تم التحقق من ملفات البرنامج بنجاح.");
            }
            else
            {
                // The user explicitly asked for a re-check, so a fresh dialog is warranted
                // even when the state has not changed.
                _lastIntegrityNotificationKey = null;
                PresentIntegrityFailure(result);
            }
        }
        finally
        {
            IntegrityRetryBtn.IsEnabled = true;
        }
    }

    // ---------------- status strip ----------------

    private void OnDevicesChanged(List<AdbDevice> devices)
    {
        if (!CanUpdateShell) return;
        DeviceCountText.Text = devices.Count.ToString(CultureInfo.InvariantCulture);
    }

    private void OnSessionsChanged()
    {
        if (!CanUpdateShell) return;
        UpdateEngineStrip();
        UpdateSidebarDevice();
    }

    private void OnCurrentDeviceChanged()
    {
        if (!CanUpdateShell) return;
        UpdateSidebarDevice();
    }

    /// <summary>
    /// Display-only mapping to the Direction D status strip. State text always accompanies
    /// the colored dot, so no information is conveyed by color alone.
    /// </summary>
    private void UpdateEngineStrip()
    {
        var live = _engineStripState == EngineStripState.Ready && ScrcpyService.ActiveCount > 0;
        var (dot, text) = _engineStripState switch
        {
            EngineStripState.Blocked => ("Brush.State.Danger", "المحرّك: متوقف"),
            EngineStripState.Scanning => ("Brush.Text.Muted", "المحرّك: يفحص…"),
            _ when live => ("Brush.State.Live", "المحرّك: جلسة نشطة"),
            _ => ("Brush.State.Success", "المحرّك: جاهز"),
        };
        EngineDot.Fill = (Brush)FindResource(dot);
        EngineStatusText.Text = text;
        StripShield.Stroke = _engineStripState == EngineStripState.Blocked
            ? (Brush)FindResource("Brush.State.Danger")
            : (Brush)FindResource("Brush.Text.Secondary");
    }

    // ---------------- toasts ----------------

    private async void ShowToast(string message, bool ok)
    {
        if (!CanUpdateShell) return;

        // Keep at most 4 toasts on screen.
        while (ToastHost.Children.Count >= 4) ToastHost.Children.RemoveAt(0);

        var accent = (Brush)FindResource(ok ? "Brush.State.Success" : "Brush.State.Danger");
        var text = new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 10, 14, 10),
        };
        var bar = new Border { Width = 3, Background = accent };
        Grid.SetColumn(bar, 1);   // second column renders at the physical left in RTL

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        grid.Children.Add(bar);

        var border = new Border
        {
            Background = (Brush)FindResource("Brush.Surface.RaisedAlt"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 6, 0, 0),
            MaxWidth = 420,
            Opacity = 0,
            Child = grid,
        };

        ToastHost.Children.Add(border);
        border.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));

        await System.Threading.Tasks.Task.Delay(3200);

        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) => ToastHost.Children.Remove(border);
        border.BeginAnimation(OpacityProperty, fade);
    }

    private void SelectNav(string page)
    {
        var rb = FindName("Nav" + page) as System.Windows.Controls.RadioButton;
        if (rb != null) rb.IsChecked = true; // triggers Nav_Checked -> Navigate
        else Navigate(page);
    }

    private void LoadBranding()
    {
        Icon = LoadPng("app.png");
        // REL-03: displayed versions always come from the compiled assembly.
        VersionBadge.Text = "v" + AppVersionInfo.ShortVersion;
        StripVersionText.Text = "v" + AppVersionInfo.ProductVersion;
    }

    private static BitmapImage? LoadPng(string name)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            return bmp;
        }
        catch { return null; }
    }

    // ---------------- navigation ----------------
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var rb = (System.Windows.Controls.RadioButton)sender;
        Navigate(rb.Name.Replace("Nav", ""));
    }

    private void Navigate(string page)
    {
        if (!_cache.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "Dashboard" => new DashboardView(),
                "Mirror" => new MirrorView(),
                "Wireless" => new WirelessView(),
                "Launcher" => new LauncherView(),
                "Apps" => new AppsView(),
                "Files" => new TransfersView(),
                _ => new DashboardView()
            };
            _cache[page] = view;
        }

        if (view is IRefreshable r) r.OnShown();
        PageHost.Content = view;
        AppDiagnostics.CurrentOperation = "page:" + page;   // context for global-exception logs
    }

    /// <summary>
    /// Batch 4C wireless-page transition: navigates to the Dashboard and applies an already-
    /// authorized device selection to it, reusing the exact
    /// <see cref="Windows.DeviceSetupWizardWindow.SetupWizardResult"/> record and
    /// <c>DashboardView.ApplySetupResult</c> mechanism the setup wizard's own Finish already
    /// established (M4) — no second, competing device-transition path. Navigating first
    /// guarantees the Dashboard instance exists in <see cref="_cache"/>; ApplySetupResult's own
    /// sequence-number guard then wins over whatever refresh that navigation's OnShown may have
    /// just started, exactly as it already does for Help_Click.
    /// </summary>
    internal void NavigateToDashboard(Windows.DeviceSetupWizardWindow.SetupWizardResult result)
    {
        SelectNav("Dashboard");
        if (_cache.TryGetValue("Dashboard", out var view) && view is Views.DashboardView dashboard)
            dashboard.ApplySetupResult(result);
    }

    /// <summary>
    /// «حول البرنامج والدعم» is a command-style navigation item: it opens the existing
    /// About window (same destination as before) and never stays selected.
    /// </summary>
    private void NavAbout_Click(object sender, RoutedEventArgs e)
    {
        NavAboutItem.IsChecked = false;
        new AboutWindow { Owner = this }.ShowDialog();
    }

    // ---------------- sidebar device chip ----------------

    private void UpdateSidebarDevice()
    {
        var dev = AppState.CurrentDevice;
        _serialRevealed = false;   // never carry a reveal across device changes

        if (dev == null)
        {
            ChipDeviceName.Text = "لا يوجد جهاز";
            ChipDeviceName.Foreground = (Brush)FindResource("Brush.Text.Muted");
            ChipSerialRow.Visibility = Visibility.Collapsed;
            ChipLivePill.Visibility = Visibility.Collapsed;
            ChipStateText.Text = "غير متصل";
            SetChipDots((Brush)FindResource("Brush.Text.Muted"));
            ChipTile.ToolTip = "لا يوجد جهاز — غير متصل";
            return;
        }

        var live = dev.IsAuthorized && ScrcpyService.ActiveCount > 0;
        // DisplayName falls back to the RAW serial when the model is not known yet; masking
        // it here keeps the reveal control the only way to see a full serial.
        var label = ShellLayout.SafeDeviceLabel(dev.Model, dev.DisplayName, dev.Serial);
        ChipDeviceName.Text = label;
        ChipDeviceName.Foreground = (Brush)FindResource("Brush.Text.Primary");
        ChipSerialRow.Visibility = Visibility.Visible;
        ChipSerial.Text = ShellLayout.MaskSerial(dev.Serial);
        UpdateRevealAffordance();
        ChipLivePill.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        ChipStateText.Text = live ? "يتم بث الشاشة الآن" : $"{dev.ConnectionLabel} · {dev.StateLabel}";

        var dot = live ? "Brush.State.Live"
            : dev.IsAuthorized ? "Brush.State.Success"
            : dev.State == "unauthorized" ? "Brush.State.Warning"
            : "Brush.Text.Muted";
        SetChipDots((Brush)FindResource(dot));
        // Compact mode shows only this tile, so its tooltip uses the same masked label.
        ChipTile.ToolTip = $"{label} — {ChipStateText.Text}";
    }

    private void SetChipDots(Brush brush)
    {
        ChipDot.Fill = brush;
        TileDot.Fill = brush;
    }

    private void RevealSerial_Click(object sender, RoutedEventArgs e)
    {
        var dev = AppState.CurrentDevice;
        if (dev == null) return;
        _serialRevealed = !_serialRevealed;
        ChipSerial.Text = _serialRevealed ? dev.Serial : ShellLayout.MaskSerial(dev.Serial);
        UpdateRevealAffordance();
    }

    private void UpdateRevealAffordance()
    {
        var label = _serialRevealed ? "إخفاء الرقم التسلسلي" : "إظهار الرقم التسلسلي";
        BtnRevealSerial.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(BtnRevealSerial, label);
    }

    // ---------------- title bar actions ----------------
    private void Help_Click(object sender, RoutedEventArgs e)
        => new OnboardingWindow { Owner = this }.ShowDialog();

    private void About_Click(object sender, RoutedEventArgs e)
        => new AboutWindow { Owner = this }.ShowDialog();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Views implement this to refresh when navigated back to.</summary>
public interface IRefreshable
{
    void OnShown();
}

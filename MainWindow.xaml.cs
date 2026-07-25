using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    public MainWindow()
    {
        InitializeComponent();
        LoadBranding();
        AppState.CurrentDeviceChanged += UpdateSidebarDevice;
        Toast.Requested += ShowToast;
        // Deterministic cleanup before the window goes away; Application.OnExit is the
        // backstop for paths that bypass this (AppLifecycle.Shutdown runs only once).
        Closing += (_, _) => AppLifecycle.Shutdown();
        Loaded += async (_, _) =>
        {
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

    // ---------------- engine integrity (SEC-01) ----------------

    /// <summary>
    /// Starts (or resumes after a successful Retry) everything that talks to the engine.
    /// Idempotent: the server start and watcher start are both safe to reach twice.
    /// </summary>
    private async Task StartEngineServicesAsync()
    {
        IntegrityBanner.Visibility = Visibility.Collapsed;
        PageHost.IsEnabled = true;

        if (_engineServicesStarted)
        {
            DeviceWatcher.RequestRefresh();   // resume promptly after a repair
            return;
        }
        _engineServicesStarted = true;

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

    // ---------------- toasts ----------------
    private async void ShowToast(string message, bool ok)
    {
        // Keep at most 4 toasts on screen.
        while (ToastHost.Children.Count >= 4) ToastHost.Children.RemoveAt(0);

        var border = new Border
        {
            Background = new SolidColorBrush(ok ? Color.FromRgb(0x1C, 0x1F, 0x27) : Color.FromRgb(0x3A, 0x1E, 0x1B)),
            BorderBrush = ok ? (Brush)FindResource("Adb") : (Brush)FindResource("Danger"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 11, 16, 11),
            Margin = new Thickness(0, 6, 0, 0),
            MaxWidth = 420,
            Opacity = 0,
            Child = new TextBlock
            {
                Text = message,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("Text")
            }
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
        TitleIcon.Source = LoadPng("logo.png");
        Icon = LoadPng("app.png");
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
                "Files" => new FilesView(),
                _ => new DashboardView()
            };
            _cache[page] = view;
        }

        if (view is IRefreshable r) r.OnShown();
        PageHost.Content = view;
    }

    private void UpdateSidebarDevice()
    {
        var dev = AppState.CurrentDevice;
        if (dev == null)
        {
            SidebarDeviceName.Text = "لا يوجد جهاز";
            SidebarDeviceState.Text = "غير متصل";
            SidebarDot.Fill = (Brush)FindResource("Muted");
            return;
        }
        SidebarDeviceName.Text = dev.DisplayName;
        SidebarDeviceState.Text = $"{dev.ConnectionLabel} · {dev.StateLabel}";
        SidebarDot.Fill = dev.IsAuthorized
            ? (Brush)FindResource("Scrcpy")
            : (Brush)FindResource("Warn");
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

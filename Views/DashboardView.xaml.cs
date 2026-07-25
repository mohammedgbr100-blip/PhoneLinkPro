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

    public DashboardView()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = RefreshAsync();
        DeviceWatcher.DevicesChanged += devices => _ = ApplyDevicesAsync(devices);
    }

    public void OnShown() => _ = RefreshAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
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
                await ApplyDevicesCoreAsync(result.Devices.ToList());
            }
            else
            {
                // Device state is unknown, not empty: keep showing what we last confirmed
                // rather than wrongly reporting that everything unplugged (DEV-01).
                var known = DeviceWatcher.LastConfirmed;
                if (known.Count > 0) await ApplyDevicesCoreAsync(known.ToList());
                else ShowEmpty("تعذّر قراءة قائمة الأجهزة — سيُعاد المحاولة تلقائياً.");
            }
        }
        finally
        {
            _busy = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Called by DeviceWatcher when devices appear/disappear/change state. A watcher update
    /// arriving while a manual refresh is running is queued rather than dropped (DEV-02).
    /// </summary>
    private async Task ApplyDevicesAsync(List<AdbDevice> devices)
    {
        if (_busy) { _missedUpdate = devices; return; }

        _busy = true;
        try
        {
            await ApplyDevicesCoreAsync(devices);

            // Service whatever arrived while we were busy, coalesced to the latest.
            while (_missedUpdate is { } queued)
            {
                _missedUpdate = null;
                await ApplyDevicesCoreAsync(queued);
            }
        }
        finally { _busy = false; }
    }

    private async Task ApplyDevicesCoreAsync(List<AdbDevice> devices)
    {
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
        await Task.WhenAll(devices.Where(d => d.IsAuthorized).Select(AdbService.EnrichAsync));

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

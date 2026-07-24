using System.Windows;
using System.Windows.Controls;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Views;

public partial class AppsView : UserControl, IRefreshable
{
    private List<AppEntry> _all = new();
    private bool _busy;
    private string? _loadedSerial = "\0"; // sentinel so the first device triggers a load

    public AppsView()
    {
        InitializeComponent();
        AppState.CurrentDeviceChanged += () =>
        {
            UpdateHint();
            // Only reload when the actual device changed (state flips shouldn't refetch).
            if (AppState.CurrentSerial != _loadedSerial) _ = LoadAsync();
        };
        Loaded += (_, _) => { UpdateHint(); _ = LoadAsync(); };
    }

    public void OnShown() => UpdateHint();

    private void UpdateHint()
    {
        var dev = AppState.CurrentDevice;
        DeviceHint.Text = dev == null
            ? "لم يتم تحديد جهاز — افتح لوحة الأجهزة أولاً"
            : $"الجهاز: {dev.DisplayName}";
    }

    private string Scope() =>
        ScopeSystem.IsChecked == true ? "-s" :
        ScopeAll.IsChecked == true ? "" : "-3";

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void Scope_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) await LoadAsync(); }

    private async Task LoadAsync()
    {
        if (_busy) return;
        var serial = AppState.CurrentSerial;
        if (serial == null)
        {
            _all = new();
            _loadedSerial = null;
            AppList.ItemsSource = null;
            return;
        }

        _busy = true;
        ShowStatus("جارٍ تحميل قائمة التطبيقات...");
        try
        {
            var pkgs = await AdbService.ListPackagesAsync(serial, Scope());
            _all = pkgs.Select(p => new AppEntry(p)).ToList();
            _loadedSerial = serial;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Log.Error("Loading package list failed", ex);
            ShowStatus("خطأ: " + ex.Message);
            return;
        }
        finally { _busy = false; }
        HideStatus();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        AppList.ItemsSource = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(a => a.Package.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || a.PrettyName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        LoadingOverlay.Visibility = Visibility.Visible;
    }
    private void HideStatus() => LoadingOverlay.Visibility = Visibility.Collapsed;

    // ---------------- per-app actions ----------------
    private string? Pkg(object sender) => (sender as Button)?.Tag as string;

    private async void ForceStop_Click(object sender, RoutedEventArgs e)
    {
        var pkg = Pkg(sender); var s = AppState.CurrentSerial;
        if (pkg == null || s == null) return;
        var r = await AdbService.ForceStopAsync(s, pkg);
        Toast.Show(r.Success ? $"تم إيقاف {AppNames.Pretty(pkg)}" : "فشل الإيقاف: " + r.Output, r.Success);
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var pkg = Pkg(sender); var s = AppState.CurrentSerial;
        if (pkg == null || s == null) return;
        if (!Confirm($"مسح جميع بيانات:\n{pkg}؟")) return;
        var r = await AdbService.ClearAsync(s, pkg);
        Toast.Show(r.Success ? $"مسح بيانات {AppNames.Pretty(pkg)}" : "فشل المسح: " + r.Output, r.Success);
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        var pkg = Pkg(sender); var s = AppState.CurrentSerial;
        if (pkg == null || s == null) return;
        if (!Confirm($"تعطيل التطبيق:\n{pkg}؟\n(يمكن إعادة تفعيله عبر قائمة «الكل»)")) return;
        var r = await AdbService.DisableAsync(s, pkg);
        Toast.Show(r.Success ? $"تم تعطيل {AppNames.Pretty(pkg)}" : "فشل التعطيل: " + r.Output, r.Success);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var pkg = Pkg(sender); var s = AppState.CurrentSerial;
        if (pkg == null || s == null) return;
        if (!Confirm($"حذف التطبيق نهائياً:\n{pkg}؟")) return;
        var r = await AdbService.UninstallAsync(s, pkg);
        if (r.Success) { _all.RemoveAll(a => a.Package == pkg); ApplyFilter(); }
        Toast.Show(r.Success ? $"تم حذف {AppNames.Pretty(pkg)}" : "فشل الحذف: " + r.Output, r.Success);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.CurrentSerial;
        if (s == null) { Toast.Show("حدّد جهازاً أولاً", ok: false); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Android APK|*.apk", Multiselect = false };
        if (dlg.ShowDialog() != true) return;

        ShowStatus("جارٍ تثبيت التطبيق...");
        var r = await AdbService.InstallAsync(s, dlg.FileName);
        HideStatus();
        var ok = r.Success || (r.Outcome == ProcessOutcome.Exited && r.StdOut.Contains("Success"));
        Toast.Show(ok ? "تم التثبيت بنجاح" : "فشل التثبيت: " + r.Output, ok);
        await LoadAsync();
    }

    private static bool Confirm(string msg) =>
        MessageBox.Show(msg, "تأكيد", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Views;

public partial class FilesView : UserControl, IRefreshable
{
    public FilesView()
    {
        InitializeComponent();
        AppState.CurrentDeviceChanged += UpdateHint;
        Loaded += (_, _) =>
        {
            UpdateHint();
            LocalDirBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            LoadBusyLogo();
        };
    }

    public void OnShown() => UpdateHint();

    private void LoadBusyLogo()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (!File.Exists(path)) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            BusyLogo.Source = bmp;
        }
        catch { }
    }

    private void UpdateHint()
    {
        var dev = AppState.CurrentDevice;
        DeviceHint.Text = dev == null
            ? "لم يتم تحديد جهاز — افتح لوحة الأجهزة أولاً"
            : $"الجهاز: {dev.DisplayName}";
    }

    private void Log(string msg)
    {
        LogBox.AppendText("\n" + msg);
        LogBox.ScrollToEnd();
    }

    // ---------------- busy overlay ----------------
    private void ShowBusy(string title, string file = "", string count = "")
    {
        BusyTitle.Text = title;
        BusyFile.Text = file;
        BusyCount.Text = count;
        BusyOverlay.Visibility = Visibility.Visible;
    }
    private void UpdateBusy(string file, string count)
    {
        BusyFile.Text = file;
        BusyCount.Text = count;
    }
    private void HideBusy() => BusyOverlay.Visibility = Visibility.Collapsed;

    // ---------------- drag & drop ----------------
    private void Drop_Enter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropZone.BorderBrush = (Brush)FindResource("Scrcpy");
            DropLabel.Text = "أفلت للرفع";
        }
    }

    private void Drop_Leave(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0x34, 0x30, 0x2A));
        DropLabel.Text = "أفلت الملفات هنا";
    }

    private async void Drop_File(object sender, DragEventArgs e)
    {
        Drop_Leave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await PushFiles(files);
    }

    private async void Pick_File(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
        if (dlg.ShowDialog() == true) await PushFiles(dlg.FileNames);
    }

    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
        if (dlg.ShowDialog() == true) await PushFiles(dlg.FileNames);
    }

    private async Task PushFiles(string[] files)
    {
        var s = AppState.CurrentSerial;
        if (s == null) { Log("✗ لم يتم تحديد جهاز."); return; }

        var remote = RemoteDirBox.Text.Trim();
        if (string.IsNullOrEmpty(remote)) remote = "/sdcard/Download/";

        int ok = 0, i = 0;
        ShowBusy("جارٍ رفع الملفات إلى الهاتف...");
        try
        {
            foreach (var f in files)
            {
                i++;
                var name = Path.GetFileName(f);
                UpdateBusy(name, $"{i} / {files.Length}");
                Log($"• رفع {name} ...");
                var r = await AdbService.PushAsync(s, f, remote);
                if (r.Success) { ok++; Log("  ✓ تم"); }
                else Log("  ✗ " + r.Output);
            }
        }
        finally { HideBusy(); }
        Log($"— اكتمل الرفع: {ok} من {files.Length} ملف");
    }

    // ---------------- pull ----------------
    private void BrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() == true) LocalDirBox.Text = dlg.FolderName;
    }

    private async void Pull_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.CurrentSerial;
        if (s == null) { Log("✗ لم يتم تحديد جهاز."); return; }

        var remote = PullRemoteBox.Text.Trim();
        var local = LocalDirBox.Text.Trim();
        if (string.IsNullOrEmpty(remote)) { Log("✗ أدخل مسار الملف على الهاتف."); return; }
        if (string.IsNullOrEmpty(local)) local = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        ShowBusy("جارٍ سحب الملف من الهاتف...", Path.GetFileName(remote.TrimEnd('/')));
        Log($"• سحب {remote} ...");
        try
        {
            var r = await AdbService.PullAsync(s, remote, local);
            Log(r.Success ? $"  ✓ حُفظ في {local}" : "  ✗ " + r.Output);
        }
        finally { HideBusy(); }
    }
}

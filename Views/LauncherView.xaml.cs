using System.Windows;
using System.Windows.Controls;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Views;

public partial class LauncherView : UserControl, IRefreshable
{
    public LauncherView()
    {
        InitializeComponent();
        AppState.CurrentDeviceChanged += UpdateHint;
        Loaded += (_, _) => { UpdateHint(); UpdatePreview(); };

        // Wire every control to refresh the live preview.
        foreach (var cb in new[]
        {
            CkFullscreen, CkOnTop, CkBorderless, CkNoAudio, CkMic, CkPrintFps,
            CkTurnOff, CkStayAwake, CkShowTouches, CkNoControl, CkPowerOff,
            CkDisableSaver, CkUhid, CkNewDisplay, CkRecord, CkNoPlayback
        })
        {
            cb.Checked += (_, _) => UpdatePreview();
            cb.Unchecked += (_, _) => UpdatePreview();
        }
        foreach (var tb in new[]
        {
            MaxSizeBox, BitRateBox, FpsBox, TitleBox, DisplayIdBox, NewDisplayBox, RecordPathBox
        })
        {
            tb.TextChanged += (_, _) => UpdatePreview();
        }

        CkRecord.Checked += (_, _) => ToggleRecord(true);
        CkRecord.Unchecked += (_, _) => ToggleRecord(false);
    }

    public void OnShown() => UpdateHint();

    private void UpdateHint()
    {
        var dev = AppState.CurrentDevice;
        DeviceHint.Text = dev == null
            ? "لم يتم تحديد جهاز — سيُشغَّل على الجهاز الافتراضي"
            : $"الجهاز: {dev.DisplayName}";
        UpdatePreview();
    }

    private void ToggleRecord(bool on)
    {
        RecordPathBox.IsEnabled = on;
        BrowseBtn.IsEnabled = on;
        CkNoPlayback.IsEnabled = on;
        if (on && string.IsNullOrWhiteSpace(RecordPathBox.Text))
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            RecordPathBox.Text = System.IO.Path.Combine(docs, $"PhoneLinkPro-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        }
        UpdatePreview();
    }

    private static int? ParseInt(string s) =>
        int.TryParse(s.Trim(), out var v) ? v : null;

    private ScrcpyOptions BuildOptions()
    {
        var o = new ScrcpyOptions
        {
            Serial = AppState.CurrentSerial,
            MaxSize = ParseInt(MaxSizeBox.Text),
            VideoBitRate = string.IsNullOrWhiteSpace(BitRateBox.Text) ? null : BitRateBox.Text.Trim(),
            MaxFps = ParseInt(FpsBox.Text),
            NoAudio = CkNoAudio.IsChecked == true,
            MicAsSource = CkMic.IsChecked == true,
            Fullscreen = CkFullscreen.IsChecked == true,
            AlwaysOnTop = CkOnTop.IsChecked == true,
            Borderless = CkBorderless.IsChecked == true,
            WindowTitle = string.IsNullOrWhiteSpace(TitleBox.Text) ? null : TitleBox.Text.Trim(),
            TurnScreenOff = CkTurnOff.IsChecked == true,
            StayAwake = CkStayAwake.IsChecked == true,
            ShowTouches = CkShowTouches.IsChecked == true,
            NoControl = CkNoControl.IsChecked == true,
            PowerOffOnClose = CkPowerOff.IsChecked == true,
            DisableScreensaver = CkDisableSaver.IsChecked == true,
            UhidKeyboard = CkUhid.IsChecked == true,
            DisplayId = ParseInt(DisplayIdBox.Text),
            PrintFps = CkPrintFps.IsChecked == true,
            Record = CkRecord.IsChecked == true,
            RecordPath = RecordPathBox.Text.Trim(),
            NoPlaybackWhileRecording = CkNoPlayback.IsChecked == true
        };

        if (CkNewDisplay.IsChecked == true)
            o.NewDisplay = NewDisplayBox.Text.Trim(); // empty string => default virtual display

        return o;
    }

    private void UpdatePreview()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(MaxSizeBox.Text)) parts.Add($"دقة {MaxSizeBox.Text.Trim()}p");
        if (!string.IsNullOrWhiteSpace(BitRateBox.Text)) parts.Add($"جودة {BitRateBox.Text.Trim()}");
        if (!string.IsNullOrWhiteSpace(FpsBox.Text)) parts.Add($"{FpsBox.Text.Trim()} إطار");
        if (CkFullscreen.IsChecked == true) parts.Add("ملء الشاشة");
        if (CkOnTop.IsChecked == true) parts.Add("في المقدمة");
        if (CkNoAudio.IsChecked == true) parts.Add("بدون صوت");
        else if (CkMic.IsChecked == true) parts.Add("صوت الميكروفون");
        if (CkTurnOff.IsChecked == true) parts.Add("إطفاء شاشة الهاتف");
        if (CkStayAwake.IsChecked == true) parts.Add("إبقاء مستيقظاً");
        if (CkShowTouches.IsChecked == true) parts.Add("إظهار اللمس");
        if (CkNoControl.IsChecked == true) parts.Add("عرض فقط");
        if (CkNewDisplay.IsChecked == true) parts.Add("شاشة افتراضية");
        if (CkRecord.IsChecked == true) parts.Add("تسجيل");
        CmdPreview.Text = parts.Count == 0 ? "الإعدادات الافتراضية" : string.Join("  ·  ", parts);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "MP4 video|*.mp4|MKV video|*.mkv",
            FileName = $"PhoneLinkPro-{DateTime.Now:yyyyMMdd-HHmmss}.mp4"
        };
        if (dlg.ShowDialog() == true) RecordPathBox.Text = dlg.FileName;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
        => ScrcpyService.Launch(BuildOptions());
}

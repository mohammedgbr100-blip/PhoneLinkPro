using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Views;

public partial class MirrorView : UserControl, IRefreshable
{
    private string _preset = "high";

    public MirrorView()
    {
        InitializeComponent();
        AppState.CurrentDeviceChanged += UpdateHint;
        // Selecting a different device must show THAT device's session state.
        AppState.CurrentDeviceChanged += UpdateSessionsUi;
        ScrcpyService.SessionsChanged += UpdateSessionsUi;
        Loaded += (_, _) => { UpdateHint(); UpdatePreview(); UpdateSessionsUi(); };

        foreach (var cb in new[] { OptTurnOff, OptStayAwake, OptFullscreen, OptOnTop, OptNoAudio, OptShowTouches })
        {
            cb.Checked += (_, _) => UpdatePreview();
            cb.Unchecked += (_, _) => UpdatePreview();
        }
    }

    public void OnShown() => UpdateHint();

    private void UpdateHint()
    {
        var dev = AppState.CurrentDevice;
        DeviceHint.Text = dev == null
            ? "لم يتم تحديد جهاز — افتح لوحة الأجهزة أولاً"
            : $"الجهاز: {dev.DisplayName}  ·  اختر مستوى الجودة وابدأ البث";
        UpdatePreview();
    }

    private void Preset_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag)
        {
            _preset = tag;
            HighlightSelected(b);
            UpdatePreview();
        }
    }

    private void HighlightSelected(Border selected)
    {
        // Reset all preset cards, highlight the chosen one.
        if (selected.Parent is UniformGrid grid)
        {
            foreach (var child in grid.Children)
                if (child is Border bd)
                    bd.BorderBrush = (Brush)FindResource("Border");
        }
        selected.BorderBrush = (Brush)FindResource("Scrcpy");
    }

    private ScrcpyOptions BuildOptions()
    {
        var o = ScrcpyService.Preset(_preset, AppState.CurrentSerial);
        o.TurnScreenOff = OptTurnOff.IsChecked == true;
        o.StayAwake = OptStayAwake.IsChecked == true;
        o.Fullscreen = OptFullscreen.IsChecked == true;
        o.AlwaysOnTop = OptOnTop.IsChecked == true;
        o.NoAudio = OptNoAudio.IsChecked == true;
        o.ShowTouches = OptShowTouches.IsChecked == true;
        return o;
    }

    private void UpdatePreview()
    {
        var quality = _preset switch
        {
            "low" => "منخفض · 1024p · 30 إطار",
            "medium" => "متوسط · 1280p · 60 إطار",
            "high" => "عالٍ · 1920p · 60 إطار",
            "ultra" => "Ultra · 2560p · 60 إطار",
            _ => "مخصّص"
        };
        var extras = new List<string>();
        if (OptTurnOff.IsChecked == true) extras.Add("إطفاء شاشة الهاتف");
        if (OptStayAwake.IsChecked == true) extras.Add("إبقاء مستيقظاً");
        if (OptFullscreen.IsChecked == true) extras.Add("ملء الشاشة");
        if (OptOnTop.IsChecked == true) extras.Add("في المقدمة");
        if (OptNoAudio.IsChecked == true) extras.Add("بدون صوت");
        if (OptShowTouches.IsChecked == true) extras.Add("إظهار اللمس");
        CmdPreview.Text = extras.Count == 0 ? quality : $"{quality}  ·  {string.Join(" · ", extras)}";
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.CurrentDevice == null)
        {
            Toast.Show("لم يتم تحديد جهاز. افتح لوحة الأجهزة وحدّد جهازاً أولاً.", ok: false);
            return;
        }

        // Guard the click itself: the button is disabled while a session is active, but a
        // rapid double-click can land before the first update is dispatched.
        if (ScrcpyService.IsActive(AppState.CurrentSerial))
        {
            Toast.Show("يوجد بث نشط لهذا الجهاز بالفعل.", ok: false);
            return;
        }

        StartBtn.IsEnabled = false;                 // immediate, before any await/dispatch
        var result = ScrcpyService.Launch(BuildOptions());

        if (result.Started)
            Toast.Show($"جارٍ بدء البث من {AppState.CurrentDevice.DisplayName}...");
        else
            Toast.Show(result.Message, ok: false);

        // A refused or failed launch must give the button back.
        UpdateSessionsUi();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        var serial = AppState.CurrentSerial;
        if (ScrcpyService.IsActive(serial))
        {
            ScrcpyService.Stop(serial);
            Toast.Show("تم إيقاف بث هذا الجهاز");
        }
        else
        {
            ScrcpyService.StopAll();
            Toast.Show("تم إيقاف جميع جلسات البث");
        }
        UpdateSessionsUi();
    }

    /// <summary>
    /// Reflects the SELECTED device's own session state, so switching devices shows that
    /// device's reality rather than a global count.
    /// </summary>
    private void UpdateSessionsUi()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateSessionsUi); return; }

        var session = ScrcpyService.GetSession(AppState.CurrentSerial);
        var state = session?.State;
        var busy = session?.IsActive == true || state == MirrorSessionState.Stopping;

        StartBtn.IsEnabled = AppState.CurrentDevice != null && !busy;
        StopBtn.IsEnabled = busy || ScrcpyService.ActiveCount > 0;

        var total = ScrcpyService.ActiveCount;
        SessionHint.Text = state switch
        {
            MirrorSessionState.Starting => "● جارٍ بدء البث لهذا الجهاز...",
            MirrorSessionState.Running => "● بث نشط لهذا الجهاز",
            MirrorSessionState.Stopping => "● جارٍ إيقاف البث...",
            _ => total switch
            {
                0 => "",
                1 => "● جلسة بث نشطة على جهاز آخر",
                _ => $"● جلسات بث نشطة: {total}"
            }
        };
        SessionHint.Visibility = string.IsNullOrEmpty(SessionHint.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}

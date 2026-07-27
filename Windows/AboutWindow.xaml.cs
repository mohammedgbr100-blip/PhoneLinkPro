using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Windows;

/// <summary>
/// PhoneLink Pro 2.0 About &amp; Support page (Batch 4F). A truthful, side-effect-free surface:
/// every field shown here is either a compiled-in identity string or a cached read of a service
/// the shell already trusts (<see cref="AppVersionInfo"/>, <see cref="EngineIntegrityVerifier"/>,
/// <see cref="ToolLocator"/>, <see cref="AppSettings"/>). Opening this window never starts the
/// device watcher, ADB, scrcpy, or a fresh integrity re-verification, never writes settings, and
/// never deletes logs — those all remain exclusively Settings' job.
/// </summary>
public partial class AboutWindow : Window
{
    // ---------------- single-instance (one About window at a time) ----------------

    private static AboutWindow? _current;

    /// <summary>How many times a new instance was actually constructed vs. an existing one was
    /// focused — proof for the self-test that repeated activation does not spawn copies.</summary>
    internal static int ConstructedCountForTests { get; set; }

    /// <summary>Shows the single About window, or focuses the one already open.</summary>
    public static void ShowOrActivate(Window owner)
    {
        if (_current != null)
        {
            try
            {
                if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
                _current.Activate();
                return;
            }
            catch
            {
                _current = null; // the old reference is dead; fall through and create a fresh one
            }
        }

        // Repair batch §5: construction, Owner assignment, and Show can all throw (e.g. an owner
        // that is closing, or one that was never itself shown). None of that may leave `_current`
        // pointing at a half-built or never-displayed window — a caller must always be able to
        // retry ShowOrActivate afterwards instead of getting a permanently stuck reference.
        try
        {
            var window = new AboutWindow { Owner = owner };
            _current = window;
            window.Closed += (_, _) => { if (ReferenceEquals(_current, window)) _current = null; };
            window.Show();
        }
        catch
        {
            _current = null;
            throw;
        }
    }

    /// <summary>Test hook: pristine singleton state between self-test scenarios.</summary>
    internal static void ResetForTests()
    {
        _current = null;
        ConstructedCountForTests = 0;
        _policyWindow = null;
        _noticesWindow = null;
        PolicyOpenCountForTests = 0;
        NoticesOpenCountForTests = 0;
    }

    /// <summary>The live singleton reference, or null when no About window is open.</summary>
    internal static AboutWindow? CurrentForTests => _current;

    // ---------------- policy / notices single-instance ----------------

    private static PolicyWindow? _policyWindow;
    private static NoticesWindow? _noticesWindow;

    internal static int PolicyOpenCountForTests { get; private set; }
    internal static int NoticesOpenCountForTests { get; private set; }
    internal static bool PolicyCurrentlyOpenForTests => _policyWindow != null;
    internal static bool NoticesCurrentlyOpenForTests => _noticesWindow != null;

    /// <summary>
    /// Test seam for the modal "display" step only. Production always calls
    /// <see cref="Window.ShowDialog"/> (real modality, blocking the owner); the self-test harness
    /// cannot drive a real nested message loop, so it substitutes a non-blocking
    /// <see cref="Window.Show"/> here to exercise the single-instance/activation logic and the
    /// read-only content without hanging. The single-instance GUARD logic itself is identical in
    /// both paths — only how the window is displayed differs.
    /// </summary>
    internal static Action<Window>? PolicyShowOverrideForTests;
    internal static Action<Window>? NoticesShowOverrideForTests;

    // ---------------- test-visible state ----------------

    internal bool LinkFailedVisibleForTests => LinkFailedText.Visibility == Visibility.Visible;
    internal bool CopySucceededForTests { get; private set; }
    internal bool CopyFailedForTests { get; private set; }
    internal string SummaryPreviewForTests => SummaryPreviewBox.Text;
    internal string ToolsStatusForTests => ToolsStatusText.Text;
    internal string IntegrityStatusForTests => IntegrityStatusText.Text;
    internal string ConsentStatusForTests => ConsentStatusText.Text;
    internal string PolicyVersionForTests => PolicyVersionText.Text;
    internal bool ExportingForTests { get; private set; }

    public AboutWindow()
    {
        InitializeComponent();
        ConstructedCountForTests++;
        PreviewKeyDown += AboutWindow_PreviewKeyDown;

        VersionText.Text = "الإصدار " + AppVersionInfo.ProductVersion;
        CopyrightText.Text = "© 2026 Mohammed Sattar — جميع الحقوق محفوظة";
        ArchitectureText.Text = RuntimeInformation.ProcessArchitecture.ToString();
        EngineVersionText.Text = "v" + EngineManifest.Version.ToString(CultureInfo.InvariantCulture);

        LoadLogo();
        RefreshReadOnlySections();
    }

    private void AboutWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void LoadLogo()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                Logo.Source = bmp;
            }
        }
        catch { /* a missing/unreadable logo must never block the page from opening */ }
    }

    /// <summary>
    /// Refreshes every read-only section from cached state only — no verification, no polling,
    /// no device access. Mirrors <c>SettingsView.RefreshReadOnlySections</c> wording so the two
    /// surfaces never disagree about the same underlying state.
    /// </summary>
    internal void RefreshReadOnlySections()
    {
        var missing = ToolLocator.MissingEngineFiles();
        ToolsStatusText.Text = missing.Count == 0
            ? "الأدوات: متاحة"
            : $"الأدوات: غير مكتملة ({missing.Count} من الملفات مفقود)";
        ToolsStatusDot.Fill = (System.Windows.Media.Brush)FindResource(
            missing.Count == 0 ? "Brush.State.Success" : "Brush.State.Danger");

        var integrity = EngineIntegrityVerifier.LastCompleted;
        string integrityText;
        string integrityBrushKey;
        if (integrity == null)
        {
            integrityText = "سلامة الملفات: لم يتم التحقق بعد";
            integrityBrushKey = "Brush.Text.Muted";
        }
        else if (integrity.IsValid)
        {
            integrityText = "سلامة الملفات: تم التحقق";
            integrityBrushKey = "Brush.State.Success";
        }
        else
        {
            integrityText = "سلامة الملفات: فشل التحقق";
            integrityBrushKey = "Brush.State.Danger";
        }
        IntegrityStatusText.Text = integrityText;
        IntegrityStatusDot.Fill = (System.Windows.Media.Brush)FindResource(integrityBrushKey);

        PolicyVersionText.Text = $"سياسة الخصوصية — الإصدار {PolicyText.Version}";
        ConsentStatusText.Text = AppSettings.ConsentAccepted
            ? $"تمت الموافقة — الإصدار {AppSettings.ConsentVersion}"
            : "لم تتم الموافقة بعد";

        SummaryPreviewBox.Text = SupportSummary.Build();
    }

    // ---------------- developer & support (fixed-link launcher) ----------------

    private void Email_Click(object sender, RoutedEventArgs e) => OpenLink(SafeLinkDestination.Email);
    private void Telegram_Click(object sender, RoutedEventArgs e) => OpenLink(SafeLinkDestination.Telegram);
    private void Instagram_Click(object sender, RoutedEventArgs e) => OpenLink(SafeLinkDestination.Instagram);
    private void TikTok_Click(object sender, RoutedEventArgs e) => OpenLink(SafeLinkDestination.TikTok);

    internal SafeLinkOutcome LastLinkOutcomeForTests { get; private set; }

    private void OpenLink(SafeLinkDestination destination)
    {
        var outcome = SafeLinkLauncher.TryOpen(destination);
        LastLinkOutcomeForTests = outcome;

        // Busy is not a failure — nothing was attempted this call, so the UI stays exactly as it
        // was (repair batch §8: a suppressed duplicate must never render as success OR failure).
        if (outcome == SafeLinkOutcome.Busy) return;

        LinkFailedText.Visibility = outcome == SafeLinkOutcome.Launched
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ---------------- privacy & legal (read-only, single-instance) ----------------

    private void Policy_Click(object sender, RoutedEventArgs e)
    {
        if (_policyWindow != null)
        {
            try { _policyWindow.Activate(); return; }
            catch { _policyWindow = null; }
        }

        PolicyOpenCountForTests++;
        try
        {
            var w = new PolicyWindow { Owner = this };
            _policyWindow = w;
            w.Closed += (_, _) => { if (ReferenceEquals(_policyWindow, w)) _policyWindow = null; };
            if (PolicyShowOverrideForTests != null) PolicyShowOverrideForTests(w);
            else w.ShowDialog();
        }
        catch
        {
            _policyWindow = null; // never leave a half-built/never-shown window as "current"
            throw;
        }
    }

    private void Notices_Click(object sender, RoutedEventArgs e)
    {
        if (_noticesWindow != null)
        {
            try { _noticesWindow.Activate(); return; }
            catch { _noticesWindow = null; }
        }

        NoticesOpenCountForTests++;
        try
        {
            var w = new NoticesWindow { Owner = this };
            _noticesWindow = w;
            w.Closed += (_, _) => { if (ReferenceEquals(_noticesWindow, w)) _noticesWindow = null; };
            if (NoticesShowOverrideForTests != null) NoticesShowOverrideForTests(w);
            else w.ShowDialog();
        }
        catch
        {
            _noticesWindow = null; // never leave a half-built/never-shown window as "current"
            throw;
        }
    }

    // ---------------- safe support summary (clipboard) ----------------

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        CopySucceededForTests = false;
        CopyFailedForTests = false;

        var text = SupportSummary.Build();
        SummaryPreviewBox.Text = text;
        var ok = ClipboardService.TrySetText(text);

        if (ok)
        {
            CopySucceededForTests = true;
            CopyStatusText.Text = "تم نسخ الملخص إلى الحافظة.";
            CopyStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.State.Success");
            Toast.Show("تم نسخ ملخص الدعم.", ok: true);
        }
        else
        {
            CopyFailedForTests = true;
            CopyStatusText.Text = "تعذّر النسخ إلى الحافظة. حاول مرة أخرى.";
            CopyStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.State.Danger");
            Toast.Show("تعذّر نسخ ملخص الدعم.", ok: false);
        }
    }

    // ---------------- advanced: full sanitized diagnostics bundle ----------------

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (ExportingForTests) return; // single-flight
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "حفظ حزمة التشخيص",
            FileName = $"PhoneLinkPro-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "ZIP|*.zip",
            DefaultExt = ".zip",
        };
        if (dialog.ShowDialog(this) != true) return;

        ExportingForTests = true;
        ExportDiagnosticsBtn.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => SupportBundle.Export(dialog.FileName));
            Toast.Show(result.Success
                    ? "تم حفظ حزمة التشخيص بنجاح."
                    : "تعذّر إنشاء حزمة التشخيص. جرّب مساراً آخر أو تحقق من المساحة المتوفرة.",
                ok: result.Success);
        }
        finally
        {
            ExportingForTests = false;
            ExportDiagnosticsBtn.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

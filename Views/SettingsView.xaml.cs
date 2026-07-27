using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Views;

/// <summary>
/// PhoneLink Pro 2.0 settings page (Batch 4E) — a truthful, safe, accessible surface over the
/// application's real, already-applied preferences. Deliberately small: only the default
/// file-transfer destination is a genuine editable preference (Draft/Save/Cancel, §6); consent
/// and onboarding are shown/actioned but never bundled into "Reset" (§7/§8); theme and language
/// are read-only descriptions because no real selector infrastructure exists for either (§9);
/// the engine-status card is entirely read-only, sourced from the same services the shell
/// already trusts (<see cref="ToolLocator"/>, <see cref="EngineIntegrityVerifier"/>).
/// <para>
/// No live subscription to any shared static service exists on this page (no device watcher, no
/// timer, no polling): every value shown here is a snapshot taken when the page is shown.
/// </para>
/// </summary>
public partial class SettingsView : UserControl, IRefreshable
{
    // ---------------- test seams (mirrors TransfersView/WirelessView convention) ----------------

    internal static Func<StandardRemoteDestination, Task<bool>>? SaveOverrideForTests;
    internal static Func<Task<bool>>? ResetOverrideForTests;
    internal static Func<bool, Task<bool>>? SetOnboardingSeenOverrideForTests;
    internal static Func<string, Task<bool>>? ClearLogsOverrideForTests;
    internal static Func<string, Task<bool>>? OpenPathOverrideForTests;

    // ---------------- lifecycle ----------------

    private bool _shown;
    private int _generation;
    private bool IsCurrent(int generation) => generation == _generation;

    // ---------------- draft state (the one real editable preference) ----------------

    private StandardRemoteDestination _persisted;
    private StandardRemoteDestination _draft;
    private bool _saving;
    private bool _saveFailed;

    // ---------------- reset state ----------------

    private bool _resetConfirmVisible;
    private bool _resetting;
    private bool _resetFailed;

    // ---------------- clear-logs state ----------------

    private bool _clearLogsConfirmVisible;
    private bool _clearingLogs;
    private bool _clearLogsFailed;

    // ---------------- tour-reset state (repair batch) ----------------

    private bool _tourResetting;
    private bool _tourResetFailed;

    // ---------------- open-log-folder state (repair batch) ----------------

    private bool _openingLogFolder;
    private bool _openLogFolderFailed;

    private bool AnyBusy => _saving || _resetting || _clearingLogs || _tourResetting || _openingLogFolder;
    private bool IsDirty => _draft != _persisted;
    private bool CanSave => IsDirty && !AnyBusy && !_resetConfirmVisible && !_clearLogsConfirmVisible;

    // ---------------- test-visible state ----------------

    internal StandardRemoteDestination DraftForTests => _draft;
    internal StandardRemoteDestination PersistedForTests => _persisted;
    internal bool SavingForTests => _saving;
    internal bool ResettingForTests => _resetting;
    internal bool SaveFailedForTests => _saveFailed;
    internal bool ResetFailedForTests => _resetFailed;
    internal bool ClearLogsFailedForTests => _clearLogsFailed;
    internal bool ResetConfirmVisibleForTests => _resetConfirmVisible;
    internal bool ClearLogsConfirmVisibleForTests => _clearLogsConfirmVisible;
    internal bool IsDirtyForTests => IsDirty;
    internal int GenerationForTests => _generation;
    internal bool TourResettingForTests => _tourResetting;
    internal bool TourResetFailedForTests => _tourResetFailed;
    internal bool OpeningLogFolderForTests => _openingLogFolder;
    internal bool OpenLogFolderFailedForTests => _openLogFolderFailed;
    internal bool ClearingLogsForTests => _clearingLogs;

    /// <summary>Same keyboard-shortcut logic <see cref="SettingsView_PreviewKeyDown"/> uses, exposed
    /// directly because constructing a real routed <see cref="KeyEventArgs"/> needs a
    /// PresentationSource this control is never attached to in the self-test harness.</summary>
    internal void HandleKeyForTests(Key key, ModifierKeys modifiers) => HandleKey(key, modifiers);

    public SettingsView()
    {
        InitializeComponent();
        PopulateDestinationCombo();
        Unloaded += SettingsView_Unloaded;
        PreviewKeyDown += SettingsView_PreviewKeyDown;
    }

    /// <summary>
    /// Refreshes every read-only section on every show. The one draft field is seeded from the
    /// persisted value only on the FIRST show — a later show (returning to this cached page)
    /// retains whatever unsaved draft the user left, instead of silently discarding it (§6's
    /// "retain the draft within the cached page" option).
    /// </summary>
    public void OnShown()
    {
        _persisted = AppSettings.DefaultTransferDestination;
        if (!_shown)
        {
            _draft = _persisted;
            _shown = true;
        }
        _saveFailed = false;
        SyncDestinationCombo();
        RefreshReadOnlySections();
        RenderState();
    }

    private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
    {
        // Invalidate every in-flight Save/Reset continuation: its own generation check will see
        // IsCurrent == false and skip repainting. Force-clear the busy/confirm flags immediately
        // so a fresh Load never inherits a stuck "saving"/"confirming" state from a page that
        // was navigated away from mid-operation.
        _generation++;
        _saving = false;
        _resetting = false;
        _clearingLogs = false;
        _tourResetting = false;
        _openingLogFolder = false;
        _resetConfirmVisible = false;
        _clearLogsConfirmVisible = false;
    }

    private void SettingsView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        HandleKey(e.Key, Keyboard.Modifiers);
        // Escape/Ctrl+S are the only shortcuts this page defines; anything else (notably Enter)
        // is deliberately left unhandled so no button can be triggered as an implicit default.
        if (e.Key == Key.Escape || (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control))
            e.Handled = true;
    }

    private void HandleKey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape)
        {
            if (_resetConfirmVisible) ResetConfirmCancel_Click(this, new RoutedEventArgs());
            else if (_clearLogsConfirmVisible) ClearLogsConfirmCancel_Click(this, new RoutedEventArgs());
            else if (IsDirty && !AnyBusy) Cancel_Click(this, new RoutedEventArgs());
            return;
        }
        if (key == Key.S && modifiers == ModifierKeys.Control)
        {
            if (CanSave) Save_Click(this, new RoutedEventArgs());
        }
        // Enter (and every other key) is intentionally a no-op: neither Reset nor the confirm
        // panels' destructive action may ever fire from an implicit default button (§12).
    }

    // ---------------- destination draft ----------------

    private void PopulateDestinationCombo()
    {
        DestinationCombo.Items.Clear();
        foreach (StandardRemoteDestination dest in Enum.GetValues<StandardRemoteDestination>())
        {
            DestinationCombo.Items.Add(new ComboBoxItem
            {
                Content = TransferOperations.StandardDestinationLabels[dest],
                Tag = dest,
            });
        }
    }

    private void SyncDestinationCombo()
    {
        var index = 0;
        foreach (StandardRemoteDestination dest in Enum.GetValues<StandardRemoteDestination>())
        {
            if (dest == _draft) { DestinationCombo.SelectedIndex = index; return; }
            index++;
        }
    }

    private void Destination_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DestinationCombo.SelectedItem is ComboBoxItem { Tag: StandardRemoteDestination dest })
        {
            _draft = dest;
            _saveFailed = false;
            RenderState();
        }
    }

    // ---------------- Save / Cancel ----------------

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSave) return;

        _saveFailed = false;
        _saving = true;
        var generation = _generation;
        RenderState();

        try
        {
            var target = _draft;
            var op = SaveOverrideForTests ?? (d => Task.FromResult(AppSettings.TrySaveDefaultTransferDestination(d)));
            var ok = await op(target);

            if (!IsCurrent(generation)) return; // stale completion after Unloaded — never repaints

            if (ok)
            {
                _persisted = target;
                Toast.Show("تم حفظ الإعدادات.", ok: true);
            }
            else
            {
                _saveFailed = true;
            }
        }
        finally
        {
            if (IsCurrent(generation))
            {
                _saving = false;
                RenderState();
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (AnyBusy) return;
        _draft = _persisted;
        _saveFailed = false;
        SyncDestinationCombo();
        RenderState();
    }

    // ---------------- Reset ----------------

    private void ResetPreferences_Click(object sender, RoutedEventArgs e)
    {
        if (AnyBusy) return;
        _resetFailed = false;
        _resetConfirmVisible = true;
        RenderState();
        ResetConfirmCancelBtn.Focus(); // §12: safe default focus is Cancel, never the destructive action
    }

    private void ResetConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        _resetConfirmVisible = false;
        RenderState();
        ResetPreferencesBtn.Focus();
    }

    private async void ResetConfirmProceed_Click(object sender, RoutedEventArgs e)
    {
        if (_resetting || _saving) return;

        _resetting = true;
        _resetFailed = false;
        var generation = _generation;
        RenderState();

        try
        {
            var op = ResetOverrideForTests ?? (() => Task.FromResult(AppSettings.ResetDefaultTransferDestination()));
            var ok = await op();

            if (!IsCurrent(generation)) return;

            if (ok)
            {
                _persisted = StandardRemoteDestination.Download;
                _draft = _persisted;
                SyncDestinationCombo();
                _resetConfirmVisible = false;
                Toast.Show("تمت استعادة الإعدادات الافتراضية.", ok: true);
            }
            else
            {
                _resetFailed = true;
            }
        }
        finally
        {
            if (IsCurrent(generation))
            {
                _resetting = false;
                RenderState();
            }
        }
    }

    // ---------------- Privacy & diagnostics ----------------

    private async void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_openingLogFolder) return; // single-flight: a duplicate activation cannot launch twice
        _openingLogFolder = true;
        _openLogFolderFailed = false;
        var generation = _generation;
        RenderState();

        try
        {
            var op = OpenPathOverrideForTests ?? (d => Task.FromResult(OpenLogFolderReal(d)));
            var ok = await op(Log.Dir); // always the one approved, fixed directory — never user-controlled

            if (!IsCurrent(generation)) return; // stale completion after Unloaded — never repaints

            _openLogFolderFailed = !ok;
        }
        finally
        {
            if (IsCurrent(generation))
            {
                _openingLogFolder = false;
                RenderState();
            }
        }
    }

    /// <summary>
    /// The one production path that ever launches a real process from this page: the fixed
    /// application log directory, opened through the shell association only (no command
    /// interpreter, no user-supplied path). Exceptions never surface a raw message or the real
    /// path to the UI.
    /// </summary>
    private static bool OpenLogFolderReal(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    private void ClearLogsRequest_Click(object sender, RoutedEventArgs e)
    {
        if (AnyBusy) return;
        _clearLogsFailed = false;
        _clearLogsConfirmVisible = true;
        RenderState();
        ClearLogsConfirmCancelBtn.Focus(); // §12: safe default focus is Cancel
    }

    private void ClearLogsConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        _clearLogsConfirmVisible = false;
        RenderState();
        ClearLogsBtn.Focus();
    }

    private async void ClearLogsConfirmProceed_Click(object sender, RoutedEventArgs e)
    {
        if (_clearingLogs) return; // single-flight: a duplicate confirmation starts at most one delete
        _clearingLogs = true;
        _clearLogsFailed = false;
        var generation = _generation;
        RenderState();

        try
        {
            var op = ClearLogsOverrideForTests ?? (d => Task.FromResult(ClearLogsReal(d)));
            var ok = await op(Log.Dir);

            if (!IsCurrent(generation)) return; // stale completion after Unloaded — never repaints

            if (ok)
            {
                _clearLogsConfirmVisible = false;
                Toast.Show("تم مسح السجلات.", ok: true);
            }
            else
            {
                _clearLogsFailed = true;
            }
        }
        finally
        {
            if (IsCurrent(generation))
            {
                _clearingLogs = false;
                RenderState();
            }
        }
    }

    /// <summary>
    /// Deletes only the application's own log files directly inside <paramref name="dir"/>:
    /// <c>app-*.log</c> matches exactly the naming <see cref="Log"/> itself uses
    /// (<c>app-yyyyMMdd.log</c> and its <c>.overflow.log</c> rollover), never a bare "*.log" that
    /// could catch an unrelated file someone else placed there. <see cref="SearchOption.TopDirectoryOnly"/>
    /// never recurses into a nested directory, and the resolved full path of every match is
    /// re-verified to sit directly inside <paramref name="dir"/> before deletion — defense in
    /// depth against ever touching the parent directory (where settings.json lives) even if the
    /// enumeration or the directory argument were ever wrong. A locked file's exception aborts the
    /// whole operation and is reported as failure, never as a silently-partial success.
    /// </summary>
    private static bool ClearLogsReal(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;

            // Refuse a reparse-point (symlink/junction) log directory outright rather than
            // deleting through it to wherever it actually points.
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

            var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (var file in Directory.EnumerateFiles(dir, "app-*.log", SearchOption.TopDirectoryOnly))
            {
                var full = Path.GetFullPath(file);
                var parent = Path.GetDirectoryName(full);
                if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
                    continue; // never reached in practice (TopDirectoryOnly already guarantees this)
                File.Delete(full);
            }
            return true;
        }
        catch { return false; }
    }

    // ---------------- onboarding ----------------

    private async void ShowTourAgain_Click(object sender, RoutedEventArgs e)
    {
        if (AnyBusy) return; // single-flight: a duplicate activation starts at most one write
        _tourResetFailed = false;
        _tourResetting = true;
        var generation = _generation;
        RenderState();

        try
        {
            var op = SetOnboardingSeenOverrideForTests ?? (v => Task.FromResult(AppSettings.TrySetOnboardingSeen(v)));
            var ok = await op(false);

            if (!IsCurrent(generation)) return; // stale completion after Unloaded — never repaints

            if (ok)
            {
                RefreshReadOnlySections();
                Toast.Show("سيتم عرض جولة الترحيب عند إعادة فتح البرنامج.", ok: true);
            }
            else
            {
                _tourResetFailed = true;
            }
        }
        finally
        {
            if (IsCurrent(generation))
            {
                _tourResetting = false;
                RenderState();
            }
        }
    }

    // ---------------- read-only sections ----------------

    private void RefreshReadOnlySections()
    {
        ConsentStatusText.Text = AppSettings.ConsentAccepted
            ? $"تم القبول — الإصدار {AppSettings.ConsentVersion}"
            : "لم يتم القبول بعد";

        OnboardingStatusText.Text = AppSettings.OnboardingSeen
            ? "تم عرض جولة الترحيب من قبل."
            : "لم تُعرض جولة الترحيب بعد — ستظهر عند فتح البرنامج القادم.";

        var missing = ToolLocator.MissingEngineFiles();
        ToolsStatusText.Text = missing.Count == 0
            ? "الأدوات: متاحة"
            : $"الأدوات: غير مكتملة ({missing.Count} من الملفات مفقود)";
        ToolsStatusDot.Fill = (Brush)FindResource(missing.Count == 0 ? "Brush.State.Success" : "Brush.State.Danger");

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
        IntegrityStatusDot.Fill = (Brush)FindResource(integrityBrushKey);

        EngineVersionText.Text = "v" + EngineManifest.Version.ToString(CultureInfo.InvariantCulture);
        AppVersionText.Text = "v" + AppVersionInfo.ProductVersion;
    }

    // ---------------- render ----------------

    private void RenderState()
    {
        UnsavedPill.Visibility = IsDirty && !_saving ? Visibility.Visible : Visibility.Collapsed;

        SaveBtn.IsEnabled = CanSave;
        CancelBtn.IsEnabled = IsDirty && !AnyBusy;

        SaveBtn.ToolTip = _saving ? "جارٍ الحفظ…"
            : !IsDirty ? "لا توجد تغييرات لحفظها"
            : AnyBusy ? "غير متاح أثناء تنفيذ عملية أخرى"
            : "حفظ الإعدادات (Ctrl+S)";

        SaveStatusText.Text = _saving ? "جارٍ الحفظ…"
            : _saveFailed ? "تعذّر الحفظ. تحقّق من صلاحيات الملف وحاول مرة أخرى."
            : IsDirty ? "تغييرات غير محفوظة"
            : "لا توجد تغييرات غير محفوظة";
        SaveStatusText.Foreground = (Brush)FindResource(
            _saveFailed ? "Brush.State.Danger" : IsDirty ? "Brush.State.Warning" : "Brush.Text.Secondary");

        ResetPreferencesBtn.IsEnabled = !AnyBusy;
        ResetConfirmPanel.Visibility = _resetConfirmVisible ? Visibility.Visible : Visibility.Collapsed;
        ResetConfirmProceedBtn.IsEnabled = !_resetting;
        ResetFailedText.Visibility = _resetFailed ? Visibility.Visible : Visibility.Collapsed;

        ClearLogsBtn.IsEnabled = !AnyBusy;
        ClearLogsConfirmPanel.Visibility = _clearLogsConfirmVisible ? Visibility.Visible : Visibility.Collapsed;
        ClearLogsConfirmProceedBtn.IsEnabled = !_clearingLogs;
        ClearLogsFailedText.Visibility = _clearLogsFailed ? Visibility.Visible : Visibility.Collapsed;

        ShowTourAgainBtn.IsEnabled = !AnyBusy;
        TourResetFailedText.Visibility = _tourResetFailed ? Visibility.Visible : Visibility.Collapsed;

        OpenLogFolderBtn.IsEnabled = !AnyBusy;
        OpenLogFolderFailedText.Visibility = _openLogFolderFailed ? Visibility.Visible : Visibility.Collapsed;

        DestinationCombo.IsEnabled = !AnyBusy;
    }
}

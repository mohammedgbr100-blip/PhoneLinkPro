using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Views;

/// <summary>
/// Guided, safe file transfers (Batch 4D). Two independent operations — Send (one or more local
/// files to a fixed/custom Android shared-storage destination) and Receive (one exact Android
/// file path to a user-chosen local destination) — both routed through the existing hardened
/// <see cref="AdbService"/> calls; this view adds no process execution of its own, only state,
/// validation and truthful presentation.
/// <para>
/// Never opens, launches or installs a transferred file. Never browses the remote filesystem —
/// the user always supplies the exact remote path. Never persists a local path, remote path,
/// file name or transfer history beyond this page instance's in-memory session state.
/// </para>
/// </summary>
public partial class TransfersView : UserControl, IRefreshable
{
    // ---------------- test seams (mirrors WirelessView convention) ----------------

    internal static Func<bool>? AdbAvailableOverrideForTests;
    internal static Func<string, string, string, CancellationToken, Task<ProcessResult>>? PushOverrideForTests;
    internal static Func<string, string, string, CancellationToken, Task<ProcessResult>>? PullOverrideForTests;
    internal static Func<Task<DeviceDiscoveryResult>>? DiscoverDevicesOverrideForTests;

    /// <summary>Replaces the real OpenFileDialog in tests. Null return means "user cancelled".</summary>
    internal static Func<string[]?>? PickFilesOverrideForTests;
    /// <summary>Replaces the real SaveFileDialog in tests. Null return means "user cancelled".</summary>
    internal static Func<string, string?>? PickSaveLocationOverrideForTests;

    // ---------------- lifecycle ----------------

    private bool _watcherSubscribed;
    private int _generation;
    private bool IsCurrent(int generation) => generation == _generation;

    // ---------------- device selection ----------------

    private List<AdbDevice> _devices = new();
    private string? _selectedSerial;
    private bool _initialSelectionSeeded;

    // ---------------- send state ----------------

    private readonly List<string> _selectedLocalFiles = new();
    // Batch 4E: preselected from the Settings page's persisted preference, never a raw path or
    // device identifier — just one of the five fixed, non-sensitive destination categories.
    private StandardRemoteDestination _selectedDestination = AppSettings.DefaultTransferDestination;
    private bool _customDestinationSelected;
    private bool _sendBusy;
    private bool _sendSubmitAttempted;
    private IReadOnlyList<TransferItemSummary> _lastSendBatch = Array.Empty<TransferItemSummary>();

    // ---------------- receive state ----------------

    private string? _receiveLocalDestination;
    private bool _receiveBusy;
    private bool _receiveSubmitAttempted;
    private FileTransferResult? _lastReceiveResult;

    // ---------------- verify ----------------

    private bool _verifyBusy;

    // ---------------- cancellation ----------------

    private CancellationTokenSource? _activeCts;
    /// <summary>
    /// Identity of whichever Send/Receive invocation currently owns <see cref="_activeCts"/>.
    /// A stale operation's own <c>finally</c> block only clears/disposes-via-field the shared
    /// <see cref="_activeCts"/> when it still recognizes itself here via <see cref="ReferenceEquals"/>
    /// — never by token or generation-integer comparison, which a reload racing an in-flight
    /// operation could make ambiguous. This prevents an old operation's late completion from
    /// nulling out (and disabling Cancel for) a newer operation's token source.
    /// </summary>
    private object? _activeOperationOwner;

    // ---------------- current-session results (never persisted) ----------------

    private readonly List<TransferItemSummary> _results = new();

    // ---------------- test-only observation points ----------------

    internal bool IsWatcherSubscribedForTests => _watcherSubscribed;
    internal IReadOnlyList<AdbDevice> DevicesForTests => _devices;
    internal string? SelectedSerialForTests => _selectedSerial;
    internal bool SendBusyForTests => _sendBusy;
    internal bool ReceiveBusyForTests => _receiveBusy;
    internal bool VerifyBusyForTests => _verifyBusy;
    internal IReadOnlyList<string> SelectedLocalFilesForTests => _selectedLocalFiles;
    internal IReadOnlyList<TransferItemSummary> LastSendBatchForTests => _lastSendBatch;
    internal FileTransferResult? LastReceiveResultForTests => _lastReceiveResult;
    internal IReadOnlyList<TransferItemSummary> ResultsForTests => _results;
    /// <summary>The token of whichever operation currently owns <see cref="_activeCts"/>, or null
    /// when no operation is active. Lets a test prove that a stale operation's finally block never
    /// steals/nulls a newer operation's active token (compared via <see cref="CancellationToken"/>
    /// value equality, which is stable even if the field were replaced by a different CTS wrapping
    /// the same conceptual operation).</summary>
    internal CancellationToken? ActiveTokenForTests => _activeCts?.Token;

    /// <summary>True while ANY of Send/Receive/Verify is in flight — the single shared operation
    /// gate (Phase 8): Send and Receive never overlap each other, and Verify never contradicts an
    /// active transfer.</summary>
    private bool AnyOperationBusy => _sendBusy || _receiveBusy || _verifyBusy;

    public TransfersView()
    {
        InitializeComponent();
        PopulateDestinationCombo();
        Loaded += TransfersView_Loaded;
        Unloaded += TransfersView_Unloaded;
    }

    public void OnShown() => RefreshFromWatcherSnapshot();

    private void TransfersView_Loaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        RefreshFromWatcherSnapshot();
    }

    /// <summary>
    /// MainWindow caches one TransfersView instance and swaps it in/out of PageHost.Content, so
    /// Loaded/Unloaded fire repeatedly on the SAME instance. Cancels any in-flight operation,
    /// invalidates repaint via the generation counter, and clears transient sensitive path state
    /// — chosen local file paths and the chosen receive destination are never worth keeping once
    /// the user has navigated away (Phase 9).
    /// </summary>
    private void TransfersView_Unloaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        _generation++;
        try { _activeCts?.Cancel(); } catch { /* best effort */ }
        _sendBusy = false;
        _receiveBusy = false;
        _verifyBusy = false;
        _selectedLocalFiles.Clear();
        _receiveLocalDestination = null;
    }

    private static bool ToolsAvailable()
        => AdbAvailableOverrideForTests?.Invoke() ?? ToolLocator.AdbAvailable;

    private void RefreshFromWatcherSnapshot()
    {
        _devices = DeviceWatcher.LastConfirmed.ToList();
        SeedInitialSelectionIfNeeded();
        RenderAll();
    }

    /// <summary>
    /// Batch 4D Phase 4: defaults to <see cref="AppState.CurrentDevice"/> ONLY once, the first
    /// time this page instance ever computes a selection, and only when that device is still
    /// present and authorized. Every later render relies solely on
    /// <see cref="TransferOperations.DecideDeviceSelection"/> — so if the selected device later
    /// disappears, this page never silently re-adopts whatever <see cref="AppState"/> may have
    /// since been switched to by <see cref="DeviceWatcher"/>'s own unrelated fallback; it simply
    /// requires an explicit re-selection (or auto-selects a genuine single remaining candidate).
    /// </summary>
    private void SeedInitialSelectionIfNeeded()
    {
        if (_initialSelectionSeeded) return;
        _initialSelectionSeeded = true;

        var appStateSerial = AppState.CurrentSerial;
        if (appStateSerial != null &&
            _devices.Any(d => d.IsAuthorized && string.Equals(d.Serial, appStateSerial, StringComparison.Ordinal)))
        {
            _selectedSerial = appStateSerial;
        }
    }

    // ---------------- DeviceWatcher lifecycle (named handler, single subscription) ----------------

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

    private void OnDevicesChanged(List<AdbDevice> devices)
    {
        _devices = devices;
        RenderDevices();
        RenderStatusBanner();
        RenderSendCard();
        RenderReceiveCard();
    }

    // ---------------- render ----------------

    private void RenderAll()
    {
        RenderToolsBanner();
        RenderDevices();
        RenderSendCard();
        RenderReceiveCard();
        RenderResults();
        RenderStatusBanner();
    }

    private void RenderToolsBanner()
        => ToolsUnavailableBanner.Visibility = ToolsAvailable() ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Repaints the Send card together with the top status banner. Any handler that can change
    /// <see cref="HasVisibleValidationErrors"/> (or any other banner-affecting condition) for the
    /// Send side must go through this — never <see cref="RenderSendCard"/> alone — so the banner
    /// can never remain stuck on a stale "correct the highlighted fields" message after the field
    /// itself has already been fixed.
    /// </summary>
    private void RenderSendCardAndBanner()
    {
        RenderSendCard();
        RenderStatusBanner();
    }

    /// <summary>Receive-side counterpart of <see cref="RenderSendCardAndBanner"/>.</summary>
    private void RenderReceiveCardAndBanner()
    {
        RenderReceiveCard();
        RenderStatusBanner();
    }

    // ---------------- device selector ----------------

    private AdbDevice? SelectedDevice => _devices.FirstOrDefault(d => d.Serial == _selectedSerial);
    private bool HasReadyDevice => ToolsAvailable() && SelectedDevice is { IsAuthorized: true };

    private void RenderDevices()
    {
        var authorized = _devices.Where(d => d.IsAuthorized).ToList();
        _selectedSerial = TransferOperations.DecideDeviceSelection(authorized, _selectedSerial);

        DeviceSelectorList.Children.Clear();
        NoDevicesText.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GoToWirelessBtn.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var row in WirelessDeviceDisplay.BuildRows(_devices))
        {
            var baseLabelText = new TextBlock
            {
                Text = row.BaseLabel, Style = (Style)FindResource("Text.Secondary"), VerticalAlignment = VerticalAlignment.Center,
            };
            var maskedText = new TextBlock
            {
                Text = row.Masked, Style = (Style)FindResource("Text.Mono"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            var connectionText = new TextBlock
            {
                Text = row.Device.ConnectionLabel, Style = (Style)FindResource("Text.Secondary"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            var stateText = new TextBlock
            {
                Text = row.Device.StateLabel, Style = (Style)FindResource("Text.Secondary"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                Foreground = row.Device.IsAuthorized
                    ? (Brush)FindResource("Brush.State.Success")
                    : (Brush)FindResource("Brush.State.Warning"),
            };
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal };
            rowPanel.Children.Add(baseLabelText);
            rowPanel.Children.Add(maskedText);
            if (row.DisambiguatorToken != null)
            {
                rowPanel.Children.Add(new TextBlock
                {
                    Text = row.DisambiguatorToken, Style = (Style)FindResource("Text.Mono"),
                    Foreground = (Brush)FindResource("Brush.Accent.Bright"),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
                });
            }
            rowPanel.Children.Add(connectionText);
            rowPanel.Children.Add(stateText);

            var serial = row.Device.Serial;
            var isAuthorized = row.Device.IsAuthorized;
            var radio = new RadioButton
            {
                GroupName = "TransfersDeviceSelector",
                Content = rowPanel,
                IsChecked = serial == _selectedSerial,
                IsEnabled = isAuthorized,
                Margin = new Thickness(0, 4, 0, 4),
            };
            AutomationProperties.SetName(radio, $"اختيار الجهاز {row.AccessibleName}");
            if (!isAuthorized)
                AutomationProperties.SetHelpText(radio, $"غير متاح للنقل — {row.Device.StateLabel}");
            radio.Checked += (_, _) =>
            {
                _selectedSerial = serial;
                RenderSendCard();
                RenderReceiveCard();
                RenderStatusBanner();
            };
            DeviceSelectorList.Children.Add(radio);
        }
    }

    private void GoToWireless_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.NavWireless.IsChecked = true;
    }

    // ---------------- status banner ----------------

    private void RenderStatusBanner()
    {
        var (colorKey, iconKey, text) = ComputeBanner();
        var brush = (Brush)FindResource(colorKey);
        StatusDot.Fill = brush;
        StatusIcon.Stroke = brush;
        StatusIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, iconKey);
        StatusText.Text = text;
    }

    private (string ColorKey, string IconKey, string Text) ComputeBanner()
    {
        if (!ToolsAvailable())
            return ("Brush.State.Danger", "Icon.Warning",
                "النقل معطّل — أدوات الاتصال بالجهاز غير متوفرة على هذا الحاسوب.");

        if (_sendBusy) return ("Brush.Text.Muted", "Icon.Folder", "جارٍ الإرسال…");
        if (_receiveBusy) return ("Brush.Text.Muted", "Icon.Folder", "جارٍ الاستلام…");
        if (_verifyBusy) return ("Brush.Text.Muted", "Icon.Folder", "جارٍ التحقّق من حالة الجهاز…");

        if (HasVisibleValidationErrors())
            return ("Brush.State.Warning", "Icon.Warning", "صحّح الحقول المظلّلة أدناه قبل المتابعة.");

        if (_devices.Count == 0)
            return ("Brush.Text.Muted", "Icon.Warning", "لا يوجد جهاز أندرويد متصل — قم بالإقران أو التوصيل أولاً.");

        if (SelectedDevice == null)
            return ("Brush.State.Warning", "Icon.Warning", "اختر جهازاً مصرَّحاً له من القائمة أعلاه.");

        if (!SelectedDevice.IsAuthorized)
            return ("Brush.State.Warning", "Icon.Warning",
                SelectedDevice.State == "unauthorized"
                    ? "الجهاز المحدد بانتظار الإذن على الهاتف."
                    : $"الجهاز المحدد غير جاهز حالياً ({SelectedDevice.StateLabel}).");

        return ("Brush.State.Success", "Icon.Shield", "الجهاز جاهز — يمكنك الإرسال أو الاستلام الآن.");
    }

    private bool HasVisibleValidationErrors()
    {
        if (_sendSubmitAttempted && _customDestinationSelected)
        {
            if (!TransferOperations.TryValidateRemotePath(CustomDestinationBox.Text, out _, out _)) return true;
        }
        if (_receiveSubmitAttempted)
        {
            if (!TransferOperations.TryValidateRemotePath(ReceiveRemotePathBox.Text, out _, out _)) return true;
        }
        return false;
    }

    // ---------------- send card ----------------

    private void PopulateDestinationCombo()
    {
        DestinationCombo.Items.Clear();
        var selectedIndex = 0;
        var index = 0;
        foreach (StandardRemoteDestination dest in Enum.GetValues<StandardRemoteDestination>())
        {
            DestinationCombo.Items.Add(new ComboBoxItem
            {
                Content = TransferOperations.StandardDestinationLabels[dest],
                Tag = dest,
            });
            if (dest == _selectedDestination) selectedIndex = index;
            index++;
        }
        DestinationCombo.Items.Add(new ComboBoxItem { Content = "مسار مخصّص…", Tag = null });
        DestinationCombo.SelectedIndex = selectedIndex;
    }

    private void Destination_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DestinationCombo.SelectedItem is ComboBoxItem { Tag: StandardRemoteDestination dest })
        {
            _selectedDestination = dest;
            _customDestinationSelected = false;
        }
        else
        {
            _customDestinationSelected = true;
        }
        RenderSendCardAndBanner();
    }

    private void CustomDestination_Changed(object sender, TextChangedEventArgs e) => RenderSendCardAndBanner();

    private void ChooseFiles_Click(object sender, RoutedEventArgs e)
    {
        string[]? files;
        if (PickFilesOverrideForTests != null)
        {
            files = PickFilesOverrideForTests();
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
            files = dlg.ShowDialog() == true ? dlg.FileNames : null;
        }
        if (files == null) return;

        _selectedLocalFiles.Clear();
        _selectedLocalFiles.AddRange(files);
        RenderSendCardAndBanner();
    }

    private bool TryGetSendDestination(out string destination, out string error)
    {
        destination = "";
        error = "";
        if (!_customDestinationSelected)
        {
            destination = TransferOperations.StandardDestinationPaths[_selectedDestination];
            return true;
        }
        return TransferOperations.TryValidateRemotePath(CustomDestinationBox.Text, out destination, out error);
    }

    private void RenderSendCard()
    {
        var toolsUnavailable = !ToolsAvailable();
        CustomDestinationPanel.Visibility = _customDestinationSelected ? Visibility.Visible : Visibility.Collapsed;

        var destOk = TryGetSendDestination(out _, out var destError);
        CustomDestinationError.Text = destError;
        CustomDestinationError.Visibility =
            _sendSubmitAttempted && _customDestinationSelected && !destOk ? Visibility.Visible : Visibility.Collapsed;

        SelectedFilesList.Items.Clear();
        if (_selectedLocalFiles.Count == 0)
        {
            SelectedFilesCountText.Text = "لم يتم اختيار ملفات بعد.";
        }
        else
        {
            SelectedFilesCountText.Text = $"تم اختيار {_selectedLocalFiles.Count} ملف:";
            foreach (var f in _selectedLocalFiles)
            {
                SelectedFilesList.Items.Add(new TextBlock
                {
                    Text = "• " + TransferOperations.SafeFileName(f),
                    Style = (Style)FindResource("Text.Mono"),
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
        }

        var ready = HasReadyDevice && _selectedLocalFiles.Count > 0 && destOk && !AnyOperationBusy;
        SendBtn.IsEnabled = ready;
        ChooseFilesBtn.IsEnabled = !_sendBusy;
        DestinationCombo.IsEnabled = !_sendBusy;
        CustomDestinationBox.IsEnabled = !_sendBusy;

        AutomationProperties.SetHelpText(SendBtn, toolsUnavailable
            ? "الإرسال معطّل — أدوات الاتصال بالجهاز غير متوفرة"
            : !HasReadyDevice
                ? "اختر جهازاً مصرَّحاً له أولاً"
                : _selectedLocalFiles.Count == 0
                    ? "اختر ملفاً واحداً أو أكثر أولاً"
                    : !destOk
                        ? "أدخل وجهة صالحة على الهاتف"
                        : "إرسال الملفات المختارة إلى الجهاز المحدد");

        if (_sendBusy)
        {
            SendResultText.Text = "جارٍ الإرسال…";
            SendResultText.Foreground = (Brush)FindResource("Brush.Text.Muted");
            SendResultText.Visibility = Visibility.Visible;
        }
        else if (_lastSendBatch.Count > 0)
        {
            var (ok, _) = TransferOperations.SummarizeBatch(_lastSendBatch);
            SendResultText.Text = ok == _lastSendBatch.Count
                ? $"تم إرسال {ok} من {_lastSendBatch.Count} ملف بنجاح."
                : $"اكتمل الإرسال جزئياً: نجح {ok} من {_lastSendBatch.Count} — راجع النتائج أدناه.";
            SendResultText.Foreground = (Brush)FindResource(
                ok == _lastSendBatch.Count ? "Brush.State.Success" : "Brush.State.Warning");
            SendResultText.Visibility = Visibility.Visible;
        }
        else
        {
            SendResultText.Visibility = Visibility.Collapsed;
        }

        RenderCancelButton();
    }

    // ---------------- receive card ----------------

    private void ReceiveRemotePath_Changed(object sender, TextChangedEventArgs e) => RenderReceiveCardAndBanner();

    private void ChooseSaveLocation_Click(object sender, RoutedEventArgs e)
    {
        var suggested = TransferOperations.TryValidateRemotePath(ReceiveRemotePathBox.Text, out var normalized, out _)
            ? TransferOperations.SuggestLocalFileName(normalized)
            : "ملف_من_الهاتف";

        string? chosen;
        if (PickSaveLocationOverrideForTests != null)
        {
            chosen = PickSaveLocationOverrideForTests(suggested);
        }
        else
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = suggested };
            chosen = dlg.ShowDialog() == true ? dlg.FileName : null;
        }
        if (chosen == null) return;

        _receiveLocalDestination = chosen;
        RenderReceiveCardAndBanner();
    }

    private void RenderReceiveCard()
    {
        var toolsUnavailable = !ToolsAvailable();
        var pathOk = TransferOperations.TryValidateRemotePath(ReceiveRemotePathBox.Text, out _, out var pathErr);

        ReceiveRemotePathError.Text = pathErr;
        ReceiveRemotePathError.Visibility =
            _receiveSubmitAttempted && !pathOk ? Visibility.Visible : Visibility.Collapsed;

        ReceiveDestinationText.Text = _receiveLocalDestination == null
            ? "لم يتم اختيار مكان الحفظ بعد."
            : $"سيُحفظ باسم: {TransferOperations.SafeFileName(_receiveLocalDestination)}";

        var ready = HasReadyDevice && pathOk && _receiveLocalDestination != null && !AnyOperationBusy;
        ReceiveBtn.IsEnabled = ready;
        ReceiveRemotePathBox.IsEnabled = !_receiveBusy;
        ChooseSaveLocationBtn.IsEnabled = !_receiveBusy;
        VerifyBtn.IsEnabled = !toolsUnavailable && !AnyOperationBusy;

        AutomationProperties.SetHelpText(ReceiveBtn, toolsUnavailable
            ? "الاستلام معطّل — أدوات الاتصال بالجهاز غير متوفرة"
            : !HasReadyDevice
                ? "اختر جهازاً مصرَّحاً له أولاً"
                : !pathOk
                    ? "أدخل مسار ملف صالح على الهاتف"
                    : _receiveLocalDestination == null
                        ? "اختر مكان الحفظ أولاً"
                        : "استلام الملف من الجهاز المحدد");

        if (_receiveBusy)
        {
            ReceiveResultText.Text = "جارٍ الاستلام…";
            ReceiveResultText.Foreground = (Brush)FindResource("Brush.Text.Muted");
            ReceiveResultText.Visibility = Visibility.Visible;
        }
        else if (_lastReceiveResult != null)
        {
            ReceiveResultText.Text = _lastReceiveResult.Message;
            ReceiveResultText.Foreground = (Brush)FindResource(
                _lastReceiveResult.Success ? "Brush.State.Success" : "Brush.State.Danger");
            ReceiveResultText.Visibility = Visibility.Visible;
        }
        else
        {
            ReceiveResultText.Visibility = Visibility.Collapsed;
        }

        RenderCancelButton();
    }

    private void RenderCancelButton()
    {
        var active = _sendBusy || _receiveBusy;
        CancelBtn.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        CancelBtn.IsEnabled = active;
    }

    // ---------------- results ----------------

    private void RenderResults()
    {
        ResultsList.Items.Clear();
        NoResultsText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in _results)
        {
            var directionLabel = item.Direction == FileTransferDirection.Send ? "إرسال" : "استلام";
            var row = new TextBlock
            {
                Style = (Style)FindResource("Text.Secondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = (Brush)FindResource(item.Success ? "Brush.State.Success" : "Brush.State.Danger"),
                Text = $"• [{directionLabel}] {item.SafeFileName} — {item.Message}",
            };
            ResultsList.Items.Add(row);
        }
    }

    private void ClearResults_Click(object sender, RoutedEventArgs e)
    {
        _results.Clear();
        RenderResults();
    }

    // ---------------- actions: Send ----------------

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (AnyOperationBusy) return;

        _sendSubmitAttempted = true;
        var destOk = TryGetSendDestination(out var destination, out _);
        RenderSendCard();
        RenderStatusBanner();
        if (!HasReadyDevice || _selectedLocalFiles.Count == 0 || !destOk) return;

        var serial = _selectedSerial!;
        var files = _selectedLocalFiles.ToList();

        _sendBusy = true;
        _sendSubmitAttempted = false;
        var generation = _generation;
        var owner = new object();
        var cts = new CancellationTokenSource();
        _activeOperationOwner = owner;
        _activeCts = cts;
        var ct = cts.Token;
        RenderSendCard();
        RenderReceiveCard();   // Receive/Verify share the operation gate — must disable too
        RenderStatusBanner();

        var batch = new List<TransferItemSummary>();
        try
        {
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;   // stop starting new files

                var safeName = TransferOperations.SafeFileName(file);
                if (!File.Exists(file))
                {
                    var missing = new TransferItemSummary(FileTransferDirection.Send, safeName, false,
                        "الملف لم يعد موجوداً في مكانه الأصلي.");
                    batch.Add(missing);
                    if (!IsCurrent(generation)) return;
                    _lastSendBatch = batch.ToList();
                    _results.Add(missing);
                    RenderSendCard();
                    RenderResults();
                    continue;
                }

                var call = PushOverrideForTests ?? AdbService.PushAsync;
                ProcessResult raw;
                try { raw = await call(serial, file, destination, ct); }
                catch (OperationCanceledException) { raw = new ProcessResult(ProcessOutcome.Cancelled, null, "", ""); }

                if (!IsCurrent(generation)) return;

                var classified = TransferOperations.ClassifyPush(raw);
                var item = new TransferItemSummary(FileTransferDirection.Send, safeName, classified.Success, classified.Message);
                batch.Add(item);
                _lastSendBatch = batch.ToList();
                _results.Add(item);
                RenderSendCard();
                RenderResults();
            }
        }
        finally
        {
            if (ReferenceEquals(_activeOperationOwner, owner))
            {
                _activeOperationOwner = null;
                _activeCts = null;
            }
            cts.Dispose();

            if (IsCurrent(generation))
            {
                _sendBusy = false;
                _selectedLocalFiles.Clear();
                RenderSendCard();
                RenderReceiveCard();
                RenderResults();
                RenderStatusBanner();
            }
        }
    }

    // ---------------- actions: Receive ----------------

    private async void Receive_Click(object sender, RoutedEventArgs e)
    {
        if (AnyOperationBusy) return;

        _receiveSubmitAttempted = true;
        var pathOk = TransferOperations.TryValidateRemotePath(ReceiveRemotePathBox.Text, out var remotePath, out _);
        RenderReceiveCard();
        RenderStatusBanner();
        if (!HasReadyDevice || !pathOk || _receiveLocalDestination == null) return;

        var serial = _selectedSerial!;
        var localDestination = _receiveLocalDestination;
        var safeName = TransferOperations.SafeFileName(localDestination);

        _receiveBusy = true;
        _receiveSubmitAttempted = false;
        var generation = _generation;
        var owner = new object();
        var cts = new CancellationTokenSource();
        _activeOperationOwner = owner;
        _activeCts = cts;
        var ct = cts.Token;
        RenderReceiveCard();
        RenderSendCard();   // Send/Verify share the operation gate — must disable too
        RenderStatusBanner();
        try
        {
            var call = PullOverrideForTests ?? AdbService.PullAsync;
            ProcessResult raw;
            try { raw = await call(serial, remotePath, localDestination, ct); }
            catch (OperationCanceledException) { raw = new ProcessResult(ProcessOutcome.Cancelled, null, "", ""); }

            if (!IsCurrent(generation)) return;

            _lastReceiveResult = TransferOperations.ClassifyPull(raw);
            _results.Add(new TransferItemSummary(
                FileTransferDirection.Receive, safeName, _lastReceiveResult.Success, _lastReceiveResult.Message));
            RenderResults();
        }
        finally
        {
            if (ReferenceEquals(_activeOperationOwner, owner))
            {
                _activeOperationOwner = null;
                _activeCts = null;
            }
            cts.Dispose();

            if (IsCurrent(generation))
            {
                _receiveBusy = false;
                _receiveLocalDestination = null;
                RenderReceiveCard();
                RenderSendCard();
                RenderStatusBanner();
            }
        }
    }

    // ---------------- actions: Verify ----------------

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (AnyOperationBusy) return;

        _verifyBusy = true;
        var generation = _generation;
        RenderReceiveCard();
        RenderSendCard();
        RenderStatusBanner();
        try
        {
            var discover = DiscoverDevicesOverrideForTests ?? (() => AdbService.DiscoverDevicesAsync());
            var result = await discover();
            if (!IsCurrent(generation)) return;

            if (result.IsSuccess) _devices = result.Devices.ToList();
            RenderDevices();
        }
        finally
        {
            if (IsCurrent(generation))
            {
                _verifyBusy = false;
                RenderReceiveCard();
                RenderSendCard();
                RenderStatusBanner();
            }
        }
    }

    // ---------------- actions: Cancel ----------------

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try { _activeCts?.Cancel(); } catch { /* best effort */ }
    }
}

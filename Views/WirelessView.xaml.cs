using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Views;

/// <summary>
/// Guided Android wireless pairing/connection (Batch 4C). Two independent operations — Pair
/// (temporary pairing port + code) and Connect (separate wireless-debugging connection port) —
/// both routed through the existing hardened <see cref="AdbService"/> calls; this view adds no
/// process execution of its own, only state, validation and truthful presentation.
/// <para>
/// A successful Pair never claims the phone is connected, and a successful Connect COMMAND never
/// claims the phone is connected either — both are reconciled against the real
/// <see cref="DeviceWatcher"/>/adb device snapshot before ever showing a connected device.
/// </para>
/// </summary>
public partial class WirelessView : UserControl, IRefreshable
{
    // ---------------- test seams (mirrors DashboardView/DeviceSetupWizardWindow convention) ----------------

    internal static Func<bool>? AdbAvailableOverrideForTests;
    internal static Func<string, string, Task<ProcessResult>>? PairOverrideForTests;
    internal static Func<string, Task<ProcessResult>>? ConnectOverrideForTests;
    internal static Func<Task<DeviceDiscoveryResult>>? DiscoverDevicesOverrideForTests;

    // ---------------- lifecycle ----------------

    private bool _watcherSubscribed;

    /// <summary>Bumped on Unloaded; every async operation checks this after its first await so a
    /// stale continuation (the page was unloaded while it was in flight) can never repaint.</summary>
    private int _generation;
    private bool IsCurrent(int generation) => generation == _generation;

    // ---------------- pair state ----------------

    private bool _pairBusy;
    private bool _pairSubmitAttempted;
    private WirelessPairResult? _lastPairResult;

    // ---------------- connect state ----------------

    private bool _connectBusy;
    private bool _verifyBusy;
    private bool _connectSubmitAttempted;
    private WirelessConnectResult? _lastConnectResult;
    private string? _lastConnectTargetSerial;

    /// <summary>Truthful, transient message for the last explicit Verify attempt that itself
    /// failed to query real device state (as opposed to a Connect result) — cleared by the next
    /// successful Verify or by starting a new Connect attempt.</summary>
    private string? _lastVerifyError;

    // ---------------- device list ----------------

    private List<AdbDevice> _devices = new();
    private string? _selectedWirelessSerial;

    // ---------------- test-only observation points ----------------

    internal bool IsWatcherSubscribedForTests => _watcherSubscribed;
    internal WirelessPairResult? LastPairResultForTests => _lastPairResult;
    internal WirelessConnectResult? LastConnectResultForTests => _lastConnectResult;
    internal string? SelectedWirelessSerialForTests => _selectedWirelessSerial;
    internal bool PairBusyForTests => _pairBusy;
    internal bool ConnectBusyForTests => _connectBusy;
    internal bool VerifyBusyForTests => _verifyBusy;
    internal string? LastVerifyErrorForTests => _lastVerifyError;
    internal IReadOnlyList<AdbDevice> DevicesForTests => _devices;

    public WirelessView()
    {
        InitializeComponent();
        Loaded += WirelessView_Loaded;
        Unloaded += WirelessView_Unloaded;
    }

    public void OnShown() => RefreshFromWatcherSnapshot();

    private void WirelessView_Loaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        RefreshFromWatcherSnapshot();
    }

    /// <summary>
    /// MainWindow caches one WirelessView instance and swaps it in/out of PageHost.Content, so
    /// Loaded/Unloaded fire repeatedly on the SAME instance rather than once per visit — exactly
    /// the pattern DashboardView/DeviceSetupWizardWindow already rely on. The pairing code is
    /// cleared here unconditionally, satisfying "cleared on page unload" regardless of whether an
    /// operation was in flight.
    /// </summary>
    private void WirelessView_Unloaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        _generation++;
        _pairBusy = false;
        _connectBusy = false;
        _verifyBusy = false;
        ClearPairingCode();
    }

    private static bool ToolsAvailable()
        => AdbAvailableOverrideForTests?.Invoke() ?? ToolLocator.AdbAvailable;

    /// <summary>
    /// Repaints from whatever DeviceWatcher already knows — no new adb call, no polling of this
    /// view's own. DeviceWatcher's own background cadence keeps this fresh; an explicit Verify
    /// click is the only one-shot discovery this page ever triggers itself.
    /// </summary>
    private void RefreshFromWatcherSnapshot()
    {
        _devices = DeviceWatcher.LastConfirmed.ToList();
        RenderAll();
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
        RenderConnectCard();
        RenderStatusBanner();
    }

    // ---------------- render ----------------

    private void RenderAll()
    {
        RenderToolsBanner();
        RenderPairCard();
        RenderConnectCard();
        RenderDevices();
        RenderStatusBanner();
    }

    private void RenderToolsBanner()
        => ToolsUnavailableBanner.Visibility = ToolsAvailable() ? Visibility.Collapsed : Visibility.Visible;

    private void RenderStatusBanner()
    {
        var (colorKey, iconKey, text) = ComputeBanner();
        var brush = (Brush)FindResource(colorKey);
        StatusDot.Fill = brush;
        StatusIcon.Stroke = brush;
        StatusIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, iconKey);
        StatusText.Text = text;
    }

    /// <summary>
    /// Single source of truth for the page-level banner. Priority: tools-unavailable, then any
    /// busy operation, then the most recently attempted CONNECT result (reconciled against real
    /// device state — never claims "connected" from the command result alone), then the most
    /// recent PAIR result (never claims "connected"), then a neutral idle message.
    /// </summary>
    private (string ColorKey, string IconKey, string Text) ComputeBanner()
    {
        if (!ToolsAvailable())
            return ("Brush.State.Danger", "Icon.Warning",
                "الإقران والاتصال معطّلان — أدوات الاتصال بالجهاز غير متوفرة على هذا الحاسوب.");

        if (_pairBusy) return ("Brush.Text.Muted", "Icon.Wireless", "جارٍ الإقران…");
        if (_connectBusy) return ("Brush.Text.Muted", "Icon.Wireless", "جارٍ الاتصال…");
        if (_verifyBusy) return ("Brush.Text.Muted", "Icon.Wireless", "جارٍ التحقّق من ظهور الجهاز…");

        if (_lastConnectResult != null)
        {
            var (msg, colorKey) = ConnectStatusMessage();
            var iconKey = colorKey == "Brush.State.Success" ? "Icon.Shield" : "Icon.Warning";
            return (colorKey, iconKey, msg);
        }

        if (_lastPairResult != null)
            return _lastPairResult.Success
                ? ("Brush.State.Success", "Icon.Shield", _lastPairResult.Message + " الاتصال لا يزال خطوة منفصلة.")
                : ("Brush.State.Danger", "Icon.Warning", _lastPairResult.Message);

        // Neither a Pair nor a Connect result currently has priority: reflect what the user is
        // actually looking at rather than a blanket "ready" — validation errors demand attention
        // first, then a device this page already knows is connected and ready, then idle.
        if (HasVisibleValidationErrors())
            return ("Brush.State.Warning", "Icon.Warning", "صحّح الحقول المظلّلة أدناه قبل المتابعة.");

        if (_devices.Any(d => d.IsWireless && d.IsAuthorized))
            return ("Brush.State.Success", "Icon.Shield", "تم اكتشاف جهاز لاسلكي متصل وجاهز أدناه.");

        return ("Brush.Text.Muted", "Icon.Wireless", "جاهز — أدخل بيانات الإقران أو الاتصال أدناه.");
    }

    /// <summary>True when a submitted Pair or Connect section currently shows a real inline
    /// field error — mirrors exactly the same checks RenderPairCard/RenderConnectCard use to
    /// decide whether to show each field's own error text, so the banner never disagrees with
    /// what is actually highlighted below it.</summary>
    private bool HasVisibleValidationErrors()
    {
        if (_pairSubmitAttempted)
        {
            var hostOk = WirelessOperations.TryValidateHost(PairHostBox.Text, out _, out _);
            var portOk = TryParsePortText(PairPortBox.Text, out var port) && AdbInput.TryValidatePort(port, out _);
            var codeOk = AdbInput.TryValidatePairingCode(PairCodeBox.Password, out _);
            if (!hostOk || !portOk || !codeOk) return true;
        }
        if (_connectSubmitAttempted)
        {
            var hostOk = WirelessOperations.TryValidateHost(ConnectHostBox.Text, out _, out _);
            var portOk = TryParsePortText(ConnectPortBox.Text, out var port) && AdbInput.TryValidatePort(port, out _);
            if (!hostOk || !portOk) return true;
        }
        return false;
    }

    /// <summary>
    /// Reconciles the last CONNECT command result against the live device snapshot. Never
    /// returns a "connected" message unless a matching, authorized device is actually present —
    /// the command succeeding is necessary but not sufficient.
    /// </summary>
    private (string Message, string ColorKey) ConnectStatusMessage()
    {
        if (_lastConnectResult == null) return ("", "Brush.Text.Muted");
        if (!_lastConnectResult.CommandSucceeded) return (_lastConnectResult.Message, "Brush.State.Danger");

        var match = _devices.FirstOrDefault(d => d.Serial == _lastConnectTargetSerial);
        if (match == null)
            return ("تم إرسال أمر الاتصال، لكن الجهاز لم يظهر بعد في قائمة الأجهزة — اضغط «تحقّق من الظهور».",
                "Brush.State.Warning");
        if (match.IsAuthorized)
            return ("الجهاز متصل ومصرَّح له.", "Brush.State.Success");
        return (match.State == "unauthorized"
            ? "تم الاتصال، لكن الهاتف بانتظار السماح — افتح الهاتف ووافق على مربع الحوار."
            : $"تم الاتصال، لكن الجهاز غير جاهز حالياً ({match.StateLabel}).", "Brush.State.Warning");
    }

    private void RenderPairCard()
    {
        var toolsUnavailable = !ToolsAvailable();
        var hostOk = WirelessOperations.TryValidateHost(PairHostBox.Text, out _, out var hostErr);
        var portErr = "";
        var portOk = TryParsePortText(PairPortBox.Text, out var port) && AdbInput.TryValidatePort(port, out portErr);
        if (!portOk && string.IsNullOrEmpty(portErr)) portErr = "منفذ الإقران غير صالح.";
        var codeOk = AdbInput.TryValidatePairingCode(PairCodeBox.Password, out var codeErr);

        PairHostError.Text = hostErr;
        PairHostError.Visibility = _pairSubmitAttempted && !hostOk ? Visibility.Visible : Visibility.Collapsed;
        PairPortError.Text = portErr;
        PairPortError.Visibility = _pairSubmitAttempted && !portOk ? Visibility.Visible : Visibility.Collapsed;
        PairCodeError.Text = codeErr;
        PairCodeError.Visibility = _pairSubmitAttempted && !codeOk ? Visibility.Visible : Visibility.Collapsed;

        var ready = hostOk && portOk && codeOk && !toolsUnavailable;
        PairBtn.IsEnabled = ready && !_pairBusy;
        PairHostBox.IsEnabled = PairPortBox.IsEnabled = PairCodeBox.IsEnabled = !_pairBusy;

        AutomationProperties.SetHelpText(PairBtn, toolsUnavailable
            ? "الإقران معطّل — أدوات الاتصال بالجهاز غير متوفرة"
            : ready
                ? "إقران هذا الكمبيوتر بالهاتف عبر الرمز والمنفذ المدخلين"
                : "أكمل عنوان الهاتف ومنفذ الإقران ورمز الإقران أولاً");

        if (_pairBusy)
        {
            PairResultText.Text = "جارٍ الإقران…";
            PairResultText.Foreground = (Brush)FindResource("Brush.Text.Muted");
            PairResultText.Visibility = Visibility.Visible;
        }
        else if (_lastPairResult != null)
        {
            PairResultText.Text = _lastPairResult.Message;
            PairResultText.Foreground = (Brush)FindResource(
                _lastPairResult.Success ? "Brush.State.Success" : "Brush.State.Danger");
            PairResultText.Visibility = Visibility.Visible;
        }
        else
        {
            PairResultText.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderConnectCard()
    {
        var toolsUnavailable = !ToolsAvailable();
        var hostOk = WirelessOperations.TryValidateHost(ConnectHostBox.Text, out _, out var hostErr);
        var portErr = "";
        var portOk = TryParsePortText(ConnectPortBox.Text, out var port) && AdbInput.TryValidatePort(port, out portErr);
        if (!portOk && string.IsNullOrEmpty(portErr)) portErr = "منفذ الاتصال غير صالح.";

        ConnectHostError.Text = hostErr;
        ConnectHostError.Visibility = _connectSubmitAttempted && !hostOk ? Visibility.Visible : Visibility.Collapsed;
        ConnectPortError.Text = portErr;
        ConnectPortError.Visibility = _connectSubmitAttempted && !portOk ? Visibility.Visible : Visibility.Collapsed;

        var ready = hostOk && portOk && !toolsUnavailable;
        ConnectBtn.IsEnabled = ready && !_connectBusy;
        ConnectHostBox.IsEnabled = ConnectPortBox.IsEnabled = !_connectBusy;

        var showVerify = _lastConnectResult is { CommandSucceeded: true } && !_connectBusy;
        VerifyBtn.Visibility = showVerify ? Visibility.Visible : Visibility.Collapsed;
        VerifyBtn.IsEnabled = !_verifyBusy && !toolsUnavailable;

        AutomationProperties.SetHelpText(ConnectBtn, toolsUnavailable
            ? "الاتصال معطّل — أدوات الاتصال بالجهاز غير متوفرة"
            : ready
                ? "الاتصال بالهاتف عبر عنوان ومنفذ الاتصال اللاسلكي"
                : "أكمل عنوان الهاتف ومنفذ الاتصال اللاسلكي أولاً");

        if (_connectBusy)
        {
            ConnectResultText.Text = "جارٍ الاتصال…";
            ConnectResultText.Foreground = (Brush)FindResource("Brush.Text.Muted");
            ConnectResultText.Visibility = Visibility.Visible;
        }
        else if (_verifyBusy)
        {
            ConnectResultText.Text = "جارٍ التحقّق من ظهور الجهاز…";
            ConnectResultText.Foreground = (Brush)FindResource("Brush.Text.Muted");
            ConnectResultText.Visibility = Visibility.Visible;
        }
        else if (_lastVerifyError != null)
        {
            ConnectResultText.Text = _lastVerifyError;
            ConnectResultText.Foreground = (Brush)FindResource("Brush.State.Warning");
            ConnectResultText.Visibility = Visibility.Visible;
        }
        else if (_lastConnectResult != null)
        {
            var (msg, colorKey) = ConnectStatusMessage();
            ConnectResultText.Text = msg;
            ConnectResultText.Foreground = (Brush)FindResource(colorKey);
            ConnectResultText.Visibility = Visibility.Visible;
        }
        else
        {
            ConnectResultText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Rebuilds the connected-wireless-devices list from the live snapshot. Selection is decided
    /// by <see cref="WirelessOperations.DecideWirelessSelection"/> — a still-present choice is
    /// kept, a single remaining candidate is a safe default, and two-or-more unselected
    /// candidates resolve to no selection (never silently picks the wrong device). Row identity
    /// is built by <see cref="WirelessDeviceDisplay.BuildRows"/> (M5 repair): two devices whose
    /// base label and masked endpoint would otherwise render identically — e.g.
    /// <c>192.168.1.42:5555</c> and <c>192.168.1.55:5555</c> both mask to "192.•••••55" — are
    /// given a short, deterministic disambiguation token (not a cryptographic anonymization
    /// guarantee — see <see cref="WirelessDeviceDisplay.DisambiguatorToken"/>) so they stay
    /// distinguishable.
    /// </summary>
    private void RenderDevices()
    {
        var wireless = _devices.Where(d => d.IsWireless).ToList();
        _selectedWirelessSerial = WirelessOperations.DecideWirelessSelection(wireless, _selectedWirelessSerial);

        DeviceSelectorList.Children.Clear();
        NoWirelessDevicesText.Visibility = wireless.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var row in WirelessDeviceDisplay.BuildRows(wireless))
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
            var descriptorText = new TextBlock
            {
                Text = row.Device.StateLabel, Style = (Style)FindResource("Text.Secondary"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
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
            rowPanel.Children.Add(descriptorText);

            var serial = row.Device.Serial;
            var radio = new RadioButton
            {
                GroupName = "WirelessDeviceSelector",
                Content = rowPanel,
                IsChecked = serial == _selectedWirelessSerial,
                Margin = new Thickness(0, 4, 0, 4),
            };
            AutomationProperties.SetName(radio, $"اختيار الجهاز {row.AccessibleName}");
            radio.Checked += (_, _) => { _selectedWirelessSerial = serial; RenderFooterAction(); };
            DeviceSelectorList.Children.Add(radio);
        }

        RenderFooterAction();
    }

    private void RenderFooterAction()
    {
        var selected = _devices.FirstOrDefault(d => d.Serial == _selectedWirelessSerial);
        var toolsUnavailable = !ToolsAvailable();
        var enabled = !toolsUnavailable && selected is { IsAuthorized: true };

        GoToDashboardBtn.Visibility = _devices.Any(d => d.IsWireless) ? Visibility.Visible : Visibility.Collapsed;
        GoToDashboardBtn.IsEnabled = enabled;
        AutomationProperties.SetHelpText(GoToDashboardBtn, toolsUnavailable
            ? "الانتقال معطّل — أدوات الاتصال بالجهاز غير متوفرة"
            : enabled
                ? "الانتقال إلى لوحة التحكم بالجهاز المحدد"
                : "يتطلب اختيار جهاز لاسلكي متصل ومصرَّح له");
    }

    // ---------------- input helpers ----------------

    /// <summary>Digits only, no sign — mirrors AdbInput's own port-text parsing convention.</summary>
    private static bool TryParsePortText(string? text, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port);
    }

    private void ClearPairingCode() => PairCodeBox.Password = "";

    // ---------------- field change / key handlers ----------------

    private void PairText_Changed(object sender, TextChangedEventArgs e) => RenderPairCard();
    private void PairCode_Changed(object sender, RoutedEventArgs e) => RenderPairCard();
    private void ConnectField_Changed(object sender, TextChangedEventArgs e) => RenderConnectCard();

    private void PairPort_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        PairCodeBox.Focus();
    }

    private void PairCode_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Pair_Click(sender, e);
    }

    private void ConnectPort_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Connect_Click(sender, e);
    }

    // ---------------- actions ----------------

    /// <summary>
    /// Pair this computer. Single-flight (an activation while one is already running is a
    /// no-op). A LOCAL validation failure (invalid host, port or code) returns before any adb
    /// call is attempted and deliberately LEAVES the pairing code in place, so the user can fix
    /// the highlighted field without re-typing a code they already entered correctly. Once an
    /// actual Pair attempt is made, the code is cleared unconditionally when it completes —
    /// success, failure, or a rejection the service layer's own defense-in-depth validation
    /// still catches — and always on Unloaded regardless of what was submitted.
    /// </summary>
    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (_pairBusy) return;

        _pairSubmitAttempted = true;
        var hostOk = WirelessOperations.TryValidateHost(PairHostBox.Text, out var normalizedHost, out _);
        var portOk = TryParsePortText(PairPortBox.Text, out var port) && AdbInput.TryValidatePort(port, out _);
        var codeOk = AdbInput.TryValidatePairingCode(PairCodeBox.Password, out _);
        RenderPairCard();
        if (!hostOk || !portOk || !codeOk) return;

        var endpoint = $"{normalizedHost}:{port}";
        var code = PairCodeBox.Password;

        _pairBusy = true;
        var generation = _generation;
        RenderPairCard();
        RenderStatusBanner();
        try
        {
            var call = PairOverrideForTests ?? AdbService.PairAsync;
            var result = await call(endpoint, code);
            if (!IsCurrent(generation)) return;

            _lastPairResult = WirelessOperations.ClassifyPair(result);
            _pairSubmitAttempted = false;

            // Convenience only, for this window session: never overwrites a value the user
            // already typed into the Connect card, and never touches the port fields — pairing
            // and connection ports are never conflated.
            if (_lastPairResult.Success && string.IsNullOrWhiteSpace(ConnectHostBox.Text))
                ConnectHostBox.Text = normalizedHost;
        }
        finally
        {
            ClearPairingCode();
            if (IsCurrent(generation))
            {
                _pairBusy = false;
                RenderPairCard();
                RenderStatusBanner();
                if (_lastPairResult?.Success == true) ConnectPortBox.Focus();
            }
        }
    }

    /// <summary>
    /// Connect to the paired phone. A successful command sets a truthful "not yet confirmed"
    /// state — real device state is only ever taken from <see cref="_devices"/> (DeviceWatcher's
    /// own snapshot or an explicit Verify), never assumed from the command alone.
    /// </summary>
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connectBusy) return;

        _connectSubmitAttempted = true;
        var hostOk = WirelessOperations.TryValidateHost(ConnectHostBox.Text, out var normalizedHost, out _);
        var portOk = TryParsePortText(ConnectPortBox.Text, out var port) && AdbInput.TryValidatePort(port, out _);
        RenderConnectCard();
        if (!hostOk || !portOk) return;

        var endpoint = $"{normalizedHost}:{port}";

        _connectBusy = true;
        _lastConnectTargetSerial = endpoint;
        _lastVerifyError = null;   // a fresh Connect attempt supersedes any stale Verify error
        var generation = _generation;
        RenderConnectCard();
        RenderStatusBanner();
        try
        {
            var call = ConnectOverrideForTests ?? AdbService.ConnectAsync;
            var result = await call(endpoint);
            if (!IsCurrent(generation)) return;

            _lastConnectResult = WirelessOperations.ClassifyConnect(result);
            _connectSubmitAttempted = false;
            RenderDevices();
        }
        finally
        {
            if (IsCurrent(generation))
            {
                _connectBusy = false;
                RenderConnectCard();
                RenderStatusBanner();
            }
        }
    }

    /// <summary>
    /// One explicit, one-shot discovery call — the same call DeviceSetupWizardWindow's own
    /// Verify action uses — never a timer, never repeated automatically. Independent of
    /// Pair/Connect's own busy gates (its own <see cref="_verifyBusy"/> flag only): it queries
    /// device state rather than mutating a pairing/connection attempt, so it is safe to run
    /// while a Pair or Connect call is also in flight — each updates <see cref="_devices"/> and
    /// repaints from its own awaited result, never from the other's.
    /// </summary>
    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (_verifyBusy) return;

        _verifyBusy = true;
        var generation = _generation;
        RenderConnectCard();   // disables VerifyBtn before the first await, not after
        try
        {
            var discover = DiscoverDevicesOverrideForTests ?? (() => AdbService.DiscoverDevicesAsync());
            var result = await discover();
            if (!IsCurrent(generation)) return;

            if (result.IsSuccess)
            {
                _devices = result.Devices.ToList();
                _lastVerifyError = null;
            }
            else
            {
                // DEV-01 convention: an unsuccessful query leaves the last known devices in
                // place rather than reading it as "everything disappeared" — but the user still
                // deserves a truthful, bounded explanation that THIS verify attempt failed.
                _lastVerifyError = "تعذّر التحقّق من حالة الجهاز الآن. سيتم الاحتفاظ بآخر حالة معروفة — أعد المحاولة لاحقاً.";
            }
            RenderDevices();
        }
        finally
        {
            // RenderStatusBanner() is deliberately called here — AFTER _verifyBusy is cleared —
            // and not inside the try block above: ComputeBanner() checks _verifyBusy first, so
            // rendering it while still busy would show "جارٍ التحقّق…" and then never repaint,
            // permanently stuck on that busy text once the call actually settles (a real bug
            // this repair's own new Verify self-test scenario caught).
            if (IsCurrent(generation))
            {
                _verifyBusy = false;
                RenderConnectCard();
                RenderStatusBanner();
            }
        }
    }

    /// <summary>
    /// M4-style transition (Batch 4C Phase 7): reuses the exact
    /// <see cref="Windows.DeviceSetupWizardWindow.SetupWizardResult"/> record and
    /// <c>DashboardView.ApplySetupResult</c> the setup wizard already established, through one
    /// new <c>MainWindow.NavigateToDashboard</c> method — no second, competing transition path.
    /// No mirror session is started here.
    /// </summary>
    private void GoToDashboard_Click(object sender, RoutedEventArgs e)
    {
        // Defense in depth: RenderFooterAction already disables this button when tools are
        // unavailable, but RaiseEvent (used by tests/tool modes) bypasses IsEnabled entirely.
        if (!ToolsAvailable()) return;

        var selected = _devices.FirstOrDefault(d => d.Serial == _selectedWirelessSerial);
        if (selected == null || !selected.IsAuthorized) return;

        if (Window.GetWindow(this) is MainWindow main)
            main.NavigateToDashboard(new Windows.DeviceSetupWizardWindow.SetupWizardResult(selected, _devices));
    }
}

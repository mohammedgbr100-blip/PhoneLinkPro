using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;

namespace ScrcpyDeck.Windows;

/// <summary>
/// First-run device-setup wizard (Batch 4B). Guides the user through Developer options, USB
/// debugging and computer authorization without ever claiming any of that can be skipped —
/// every state shown here is derived from real <see cref="AdbDevice"/> data the app already
/// has, via <see cref="DeviceSetupEvaluator"/>. Introduces no new discovery mechanism and no
/// polling of its own: it reuses <see cref="DeviceWatcher"/>'s push events for live updates and
/// <see cref="AdbService.DiscoverDevicesAsync"/> (the same call <c>DashboardView</c> already
/// uses) for the one-shot "تحقق من الاتصال" action.
/// </summary>
public partial class DeviceSetupWizardWindow : Window
{
    private const int StepConnect = 0;
    private const int StepDevOptions = 1;
    private const int StepUsbDebug = 2;
    private const int StepAuthorize = 3;
    private const int StepVerify = 4;
    private static readonly string[] StepTitles =
    {
        "توصيل الهاتف", "تفعيل خيارات المطوّر", "تفعيل تصحيح USB", "السماح لهذا الكمبيوتر", "التحقق والإنهاء",
    };

    private IReadOnlyList<AdbDevice> _devices;
    private string? _selectedSerial;
    private int _stepIndex;
    private bool _whyExpanded;
    private bool _watcherSubscribed;
    private bool _closed;
    private AdbDevice? _focused;
    private DeviceSetupStage _stage = DeviceSetupStage.NoDevice;
    private bool _verifyInProgress;

    /// <summary>
    /// M3 repair: the auto-navigation target is picked exactly ONCE, from whatever the live
    /// state is at the moment this window opens — never re-applied on a later render. Every
    /// later <see cref="RenderState"/> call (a DeviceWatcher snapshot, a device-selector change,
    /// a Verify result) leaves <see cref="_stepIndex"/> exactly where the user's own Back/Next
    /// clicks last put it.
    /// </summary>
    private bool _initialNavigationDone;

    private readonly Border[] _stepCircles = new Border[5];
    private readonly TextBlock[] _stepNumbers = new TextBlock[5];
    private readonly TextBlock[] _stepStateLabels = new TextBlock[5];
    private readonly StackPanel[] _stepContainers = new StackPanel[5];

    /// <summary>Test-only observation point (Batch 4B self-test) — the stage this render pass
    /// actually computed, so assertions never have to re-derive it independently.</summary>
    internal DeviceSetupStage StageForTests => _stage;

    /// <summary>Test-only observation point — which instructional card is currently shown.</summary>
    internal int StepIndexForTests => _stepIndex;

    /// <summary>Test-only observation point (M2 repair) — whether step <paramref name="step"/>
    /// is technically proven right now, independent of which card is currently displayed.</summary>
    internal bool StepConfirmedForTests(int step) => StepConfirmed(step);

    /// <summary>Test-only observation point — the exact rendered stepper word
    /// ("تم"/"الحالي"/"غير متحقق"/"لاحقاً") for every step, in order.</summary>
    internal string[] StepWordsForTests => _stepStateLabels.Select(l => l.Text).ToArray();

    /// <summary>
    /// Test-only replacement for <see cref="ToolLocator.AdbAvailable"/> (Batch 4B repair).
    /// ToolLocator exposes no injectable seam and must not gain one just for a test; null in
    /// production always resolves to the real property. Lets tests and the snapshot tool
    /// render the truthful ToolsUnavailable state without needing the local engine files to
    /// actually be missing.
    /// </summary>
    internal static Func<bool>? AdbAvailableOverrideForTests;

    /// <summary>
    /// Test-only replacement for <see cref="AdbService.DiscoverDevicesAsync"/> (Batch 4B
    /// repair), mirroring <see cref="Views.DashboardView.DiscoverDevicesOverrideForTests"/>:
    /// null in production always resolves to the real one-shot discovery call Verify already
    /// uses; tests substitute a controlled fake so no real adb process ever starts.
    /// </summary>
    internal static Func<Task<DeviceDiscoveryResult>>? DiscoverDevicesOverrideForTests;

    /// <summary>
    /// M4 repair: the explicit, authoritative outcome of a successful Finish — the device the
    /// wizard confirmed ready, plus the latest device snapshot it observed. The caller (normally
    /// <c>DashboardView.Help_Click</c>) applies this to <c>AppState</c> and repaints itself from
    /// it directly; the wizard never repeats work an unrelated discovery call would duplicate.
    /// </summary>
    internal sealed record SetupWizardResult(AdbDevice SelectedDevice, IReadOnlyList<AdbDevice> LatestSnapshot);

    /// <summary>Non-null only after a successful Finish (never after Cancel/Close/Escape).</summary>
    internal SetupWizardResult? Result { get; private set; }

    public DeviceSetupWizardWindow(IReadOnlyList<AdbDevice>? initialDevices = null, string? focusSerial = null)
    {
        InitializeComponent();
        _devices = initialDevices ?? Array.Empty<AdbDevice>();
        _selectedSerial = focusSerial;

        BuildStepper();
        BuildManufacturerCombo();

        Loaded += (_, _) => Subscribe();
        Closed += (_, _) =>
        {
            _closed = true;
            Unsubscribe();
        };

        RenderState();
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

    /// <summary>Test-only subscriber-lifecycle probe, mirroring the pattern the Dashboard
    /// self-tests already use (reflection over the static event's invocation list).</summary>
    internal bool IsWatcherSubscribedForTests => _watcherSubscribed;

    private void OnDevicesChanged(List<AdbDevice> devices)
    {
        // A static event can still fire after this window has closed; never touch the
        // (possibly torn-down) visual tree once that has happened.
        if (_closed) return;
        _devices = devices;
        RenderState();
    }

    // ---------------- stepper ----------------

    private void BuildStepper()
    {
        for (var i = 0; i < StepTitles.Length; i++)
        {
            var index = i;
            var circle = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
                Background = (Brush)FindResource("Brush.Surface.Inset"),
                BorderBrush = (Brush)FindResource("Brush.Border.Subtle"), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var number = new TextBlock
            {
                Text = (index + 1).ToString(), FontWeight = FontWeights.Bold, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("Brush.Text.Muted"),
            };
            circle.Child = number;

            var title = new TextBlock
            {
                Text = StepTitles[index], Style = (Style)FindResource("Text.Caption"),
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 6, 2, 0),
            };
            var state = new TextBlock
            {
                Style = (Style)FindResource("Text.Caption"), FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
            };

            var stack = new StackPanel { Margin = new Thickness(4, 0, 4, 0), Focusable = false };

            stack.Children.Add(circle);
            stack.Children.Add(title);
            stack.Children.Add(state);

            _stepCircles[index] = circle;
            _stepNumbers[index] = number;
            _stepStateLabels[index] = state;
            _stepContainers[index] = stack;
            StepperHost.Items.Add(stack);
        }
    }

    /// <summary>
    /// M2 repair: step words are now honest about THREE distinct things rather than collapsing
    /// them onto one word — "تم" (technically proven, from <see cref="StepConfirmed"/>), "الحالي"
    /// (the exact one step the instructional body is currently showing), "غير متحقق" (a step the
    /// user has already read past but real state still does not confirm) and "لاحقاً" (a step
    /// never yet read). A step already visited but not confirmed no longer wears the same
    /// "لاحقاً" word as a step the user has never seen.
    /// </summary>
    private void RenderStepper()
    {
        for (var i = 0; i < _stepCircles.Length; i++)
        {
            var confirmed = StepConfirmed(i);
            var current = i == _stepIndex;
            var visited = i < _stepIndex;

            Brush background, border, foreground;
            string stateWord;
            if (confirmed)
            {
                background = (Brush)FindResource("Brush.State.Success");
                border = background;
                foreground = (Brush)FindResource("Brush.Text.OnAccent");
                stateWord = "تم";
            }
            else if (current)
            {
                background = (Brush)FindResource("Brush.Accent.Fill");
                border = (Brush)FindResource("Brush.Accent.Primary");
                foreground = (Brush)FindResource("Brush.Text.OnAccent");
                stateWord = "الحالي";
            }
            else if (visited)
            {
                background = (Brush)FindResource("Brush.Surface.Inset");
                border = (Brush)FindResource("Brush.State.Warning");
                foreground = (Brush)FindResource("Brush.Text.Muted");
                stateWord = "غير متحقق";
            }
            else
            {
                background = (Brush)FindResource("Brush.Surface.Inset");
                border = (Brush)FindResource("Brush.Border.Subtle");
                foreground = (Brush)FindResource("Brush.Text.Muted");
                stateWord = "لاحقاً";
            }

            _stepCircles[i].Background = background;
            _stepCircles[i].BorderBrush = border;
            _stepNumbers[i].Foreground = foreground;
            _stepNumbers[i].Text = confirmed ? "✓" : (i + 1).ToString();
            _stepStateLabels[i].Text = stateWord;
            _stepStateLabels[i].Foreground = current
                ? (Brush)FindResource("Brush.Accent.Primary")
                : (Brush)FindResource("Brush.Text.Muted");

            // Section 12: each step container carries its own number, title and truthful
            // technical status — never color alone — and exactly one ever reports "الحالي".
            System.Windows.Automation.AutomationProperties.SetName(
                _stepContainers[i], $"الخطوة {i + 1}: {StepTitles[i]} — {stateWord}");
        }
    }

    /// <summary>
    /// Whether a step is confirmed by CURRENT real state — never by navigation history. If the
    /// device disconnects, an earlier step's checkmark honestly disappears rather than lying
    /// about progress that is no longer true.
    /// <para>
    /// M2 repair: <see cref="DeviceSetupStage.Offline"/> now confirms ONLY that a device was
    /// observed (step 0) — it used to also confirm Developer options/USB debugging (steps 1-2),
    /// which is false for a device seen in bootloader/recovery/sideload or any other
    /// unrecognized adb state that <see cref="DeviceSetupEvaluator"/> reports as Offline; those
    /// states fail closed rather than borrowing Unauthorized's proof that adb's handshake with
    /// USB debugging is actually working. <see cref="DeviceSetupStage.Unauthorized"/> alone
    /// proves that much, because adb can only ever report "unauthorized" once USB debugging is
    /// live and answering. Computer authorization (step 3) and the verify/finish step itself
    /// (step 4) are proven only by <see cref="DeviceSetupStage.Ready"/> — and step 4 never, since
    /// it IS the action Finish performs, not something Ready alone can already prove.
    /// </para>
    /// </summary>
    private bool StepConfirmed(int step) => step switch
    {
        StepConnect => _stage is DeviceSetupStage.Unauthorized or DeviceSetupStage.Offline or DeviceSetupStage.Ready,
        StepDevOptions or StepUsbDebug => _stage is DeviceSetupStage.Unauthorized or DeviceSetupStage.Ready,
        StepAuthorize => _stage == DeviceSetupStage.Ready,
        _ => false, // StepVerify: never — see remarks above.
    };

    // ---------------- manufacturer guide ----------------

    private void BuildManufacturerCombo()
    {
        foreach (var guide in ManufacturerGuides.All)
            ManufacturerCombo.Items.Add(new ComboBoxItem { Content = guide.DisplayName, Tag = guide.Id });
        ManufacturerCombo.SelectedIndex = ManufacturerGuides.All.Count - 1; // "other" — never pre-guessed
    }

    private ManufacturerGuide SelectedGuide
        => ManufacturerGuides.ById((ManufacturerCombo.SelectedItem as ComboBoxItem)?.Tag as string);

    private void ManufacturerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RenderStepBody();

    private static void FillBullets(ItemsControl host, IReadOnlyList<string> bullets)
    {
        host.Items.Clear();
        foreach (var b in bullets)
            host.Items.Add(new TextBlock
            {
                Text = "•  " + b, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                FontSize = 13.5, Foreground = (Brush)Application.Current.FindResource("Brush.Text.Secondary"),
            });
    }

    // ---------------- multi-device selector ----------------

    /// <summary>
    /// M1 repair: the masked serial/endpoint used to be concatenated straight into one plain
    /// string RadioButton.Content, which inherits this window's RTL FlowDirection. That is
    /// harmless for a USB serial (letters/digits read the same either way) but visually reverses
    /// a wireless endpoint like "192.•••••55" into "55•••••.192" — the Unicode bidi algorithm
    /// mirrors that digit/dot/bullet run inside an RTL context. Building the row from two
    /// separate TextBlocks keeps the masked value in its own explicit LTR (<c>Text.Mono</c>)
    /// element — always in logical left-to-right order, whether it is a USB serial or a wireless
    /// endpoint — beside a normal RTL Arabic descriptor, both inside one associated row.
    /// </summary>
    private void BuildDeviceSelector()
    {
        DeviceSelectorList.Children.Clear();
        if (_devices.Count <= 1) { MultiDevicePanel.Visibility = Visibility.Collapsed; return; }

        MultiDevicePanel.Visibility = Visibility.Visible;
        foreach (var dev in _devices)
        {
            var masked = ShellLayout.MaskSerial(dev.Serial);
            var descriptor = $"{dev.ConnectionLabel} — {dev.StateLabel}";

            var maskedText = new TextBlock
            {
                Text = masked, Style = (Style)FindResource("Text.Mono"), VerticalAlignment = VerticalAlignment.Center,
            };
            var descriptorText = new TextBlock
            {
                Text = descriptor, Style = (Style)FindResource("Text.Secondary"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(maskedText);
            row.Children.Add(descriptorText);

            var radio = new RadioButton
            {
                GroupName = "SetupWizardDeviceSelector",
                Content = row,
                IsChecked = dev.Serial == _focused?.Serial,
                Margin = new Thickness(0, 4, 0, 4),
                Tag = dev.Serial,
                TabIndex = 3, // section 12 tab order: close, manufacturer, device selector, ...
            };
            System.Windows.Automation.AutomationProperties.SetName(radio, $"اختيار الجهاز {masked} — {descriptor}");
            radio.Checked += (_, _) =>
            {
                _selectedSerial = dev.Serial;
                RenderState();
            };
            DeviceSelectorList.Children.Add(radio);
        }
    }

    // ---------------- main render ----------------

    private void RenderState()
    {
        _focused = DeviceSetupEvaluator.PreferredDevice(_devices, _selectedSerial);
        var toolsAvailable = AdbAvailableOverrideForTests?.Invoke() ?? ToolLocator.AdbAvailable;
        _stage = DeviceSetupEvaluator.Evaluate(toolsAvailable, _focused);

        // M3 repair: this used to run on every render — including a DeviceWatcher snapshot, a
        // device-selector change, or a Verify result — silently overriding Back/Next the moment
        // any of those fired. It now runs exactly once, the first time this window ever renders,
        // to pick the most useful initial instruction for the live state it opened with; every
        // later render leaves _stepIndex exactly where the user's own navigation put it.
        if (!_initialNavigationDone)
        {
            _stepIndex = InitialStepIndexFor(_stage);
            _initialNavigationDone = true;
        }

        BuildDeviceSelector();
        RenderStatusBanner();
        RenderStepper();
        RenderStepBody();
        RenderFooter();
    }

    /// <summary>
    /// The single instructional card worth opening on, given the live state at construction
    /// time. A device already observed but not yet ready (Offline) opens on the developer-
    /// options/USB-debugging card rather than "connect your phone" — the device manifestly is
    /// connected — while Unauthorized and Ready jump straight to the step their own real
    /// progress has already earned.
    /// </summary>
    private static int InitialStepIndexFor(DeviceSetupStage stage) => stage switch
    {
        DeviceSetupStage.Unauthorized => StepAuthorize,
        DeviceSetupStage.Ready => StepVerify,
        DeviceSetupStage.Offline => StepDevOptions,
        _ => StepConnect,
    };

    private void RenderStatusBanner()
    {
        var (icon, colorKey, text) = _stage switch
        {
            DeviceSetupStage.ToolsUnavailable => ("Icon.Warning", "Brush.State.Danger",
                "تعذّر العثور على أدوات الاتصال بالجهاز داخل البرنامج — هذه ليست مشكلة في تصحيح USB على هاتفك، بل في ملفات البرنامج على هذا الحاسوب."),
            DeviceSetupStage.NoDevice => ("Icon.Device", "Brush.Text.Muted",
                "لم يتم العثور على أي جهاز متصل بعد."),
            DeviceSetupStage.Unauthorized => ("Icon.Warning", "Brush.State.Warning",
                "تم رصد الهاتف — بانتظار موافقتك على هذا الكمبيوتر من شاشة الهاتف."),
            DeviceSetupStage.Offline => ("Icon.Warning", "Brush.State.Warning",
                "تم رصد الهاتف سابقاً لكنه غير جاهز الآن. أعد توصيل الكابل، أو جرّب تغيير وضع USB على الهاتف (نقل الملفات بدلاً من الشحن فقط)."),
            DeviceSetupStage.Ready => ("Icon.Shield", "Brush.State.Success",
                "الهاتف متصل ومصرّح له — جاهز للبدء."),
            _ => ("Icon.Device", "Brush.Text.Muted", ""),
        };

        var brush = (Brush)FindResource(colorKey);
        StatusDot.Fill = brush;
        StatusIcon.Stroke = brush;
        StatusIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, icon);
        StatusText.Text = text;
        StatusBanner.BorderBrush = brush;

        SupportLinkBtn.Visibility = _stage == DeviceSetupStage.ToolsUnavailable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderStepBody()
    {
        StepConnectPanel.Visibility = _stepIndex == StepConnect ? Visibility.Visible : Visibility.Collapsed;
        StepDevOptionsPanel.Visibility = _stepIndex == StepDevOptions ? Visibility.Visible : Visibility.Collapsed;
        StepUsbDebugPanel.Visibility = _stepIndex == StepUsbDebug ? Visibility.Visible : Visibility.Collapsed;
        StepAuthorizePanel.Visibility = _stepIndex == StepAuthorize ? Visibility.Visible : Visibility.Collapsed;
        StepVerifyPanel.Visibility = _stepIndex == StepVerify ? Visibility.Visible : Visibility.Collapsed;

        ConnectWaitingText.Text = _focused == null
            ? "جارٍ انتظار توصيل الهاتف…"
            : "تم رصد جهاز متصل.";

        var guide = SelectedGuide;
        FillBullets(DevOptionsBullets, guide.DeveloperOptions.Bullets);
        FillBullets(UsbDebugBullets, guide.UsbDebugging.Bullets);
        FillBullets(AuthorizeBullets, new[]
        {
            "أبقِ الهاتف مفتوحاً وغير مقفل",
            "ابحث عن مربع حوار على شاشة الهاتف بعنوان «السماح بتصحيح USB؟»",
            "اضغط «سماح» / «موافق»",
            "بعد الموافقة ارجع إلى هذه النافذة — سيتحدّث الحال هنا تلقائياً",
        });

        var ready = _stage == DeviceSetupStage.Ready && _focused != null;
        ReadyCard.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        if (ready)
        {
            ReadyDeviceName.Text = ShellLayout.DeviceNameOrFallback(_focused!.Model, _focused.DisplayName, _focused.State);
            ReadySerial.Text = ShellLayout.MaskSerial(_focused.Serial);
            ReadyConnection.Text = _focused.ConnectionLabel;
        }
        NotReadyHint.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        NotReadyHint.Text = _stage switch
        {
            DeviceSetupStage.NoDevice => "لا يمكن الإنهاء بعد — لم يتم رصد أي جهاز.",
            DeviceSetupStage.Unauthorized => "لا يمكن الإنهاء بعد — بانتظار موافقتك على هذا الكمبيوتر من شاشة الهاتف.",
            DeviceSetupStage.Offline => "لا يمكن الإنهاء بعد — الجهاز غير جاهز الآن.",
            DeviceSetupStage.ToolsUnavailable => "لا يمكن الإنهاء — أدوات الاتصال بالجهاز غير متوفرة داخل البرنامج.",
            _ => "",
        };

        var whyByStep = new[]
        {
            "PhoneLink Pro يحتاج معرفة أن الهاتف موصول فعلياً قبل أن يعرض أي معلومة عنه.",
            "خيارات المطوّر هي الباب الذي يكشف إعداد «تصحيح USB» على هاتفك؛ أندرويد يخفيه افتراضياً لحماية المستخدم العادي.",
            "«تصحيح USB» هو ما يسمح لأدوات الاتصال (adb) على الحاسوب برؤية الهاتف والتواصل معه.",
            "أندرويد يطلب موافقتك صراحة في كل مرة يتصل فيها حاسوب جديد، لمنع أي جهاز غير موثوق من التحكم بهاتفك دون علمك.",
            "بعد التحقق من هذه الخطوات، الهاتف أصبح جاهزاً فعلياً للبث والتحكم.",
        };
        WhyText.Text = whyByStep[Math.Clamp(_stepIndex, 0, whyByStep.Length - 1)];
    }

    private void RenderFooter()
    {
        // Section 7 / minors: Verify and Next used to stay enabled even when the local ADB
        // tools are missing, even though neither can do anything useful in that state — a
        // silent no-op button. Both are disabled here, with their own truthful HelpText.
        var toolsUnavailable = _stage == DeviceSetupStage.ToolsUnavailable;

        BackBtn.IsEnabled = _stepIndex > StepConnect;
        NextBtn.IsEnabled = !toolsUnavailable && _stepIndex < StepVerify;
        VerifyBtn.IsEnabled = !toolsUnavailable && !_verifyInProgress;
        FinishBtn.IsEnabled = _stage == DeviceSetupStage.Ready && _focused != null;

        var nextReason = toolsUnavailable
            ? "التالي معطّل — أدوات الاتصال بالجهاز غير متوفرة داخل البرنامج"
            : "الانتقال إلى الخطوة التالية";
        System.Windows.Automation.AutomationProperties.SetHelpText(NextBtn, nextReason);

        var verifyReason = toolsUnavailable
            ? "التحقق معطّل — أدوات الاتصال بالجهاز غير متوفرة داخل البرنامج"
            : "يبحث عن الجهاز الآن عبر آلية الاكتشاف نفسها المستخدمة في باقي البرنامج";
        VerifyBtn.ToolTip = verifyReason;
        System.Windows.Automation.AutomationProperties.SetHelpText(VerifyBtn, verifyReason);

        var reason = FinishBtn.IsEnabled
            ? "الجهاز جاهز — يمكنك الإنهاء الآن"
            : "يتطلب الإنهاء جهازاً مصرّحاً له وجاهزاً";
        FinishBtn.ToolTip = reason;
        System.Windows.Automation.AutomationProperties.SetHelpText(FinishBtn, reason);
    }

    // ---------------- interaction ----------------

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex > StepConnect) _stepIndex--;
        RenderStepper();
        RenderStepBody();
        RenderFooter();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex < StepVerify) _stepIndex++;
        RenderStepper();
        RenderStepBody();
        RenderFooter();
    }

    /// <summary>
    /// Section 7 repair: discovery now goes through an injectable seam (null in production —
    /// always <see cref="AdbService.DiscoverDevicesAsync"/>, the exact call DashboardView's own
    /// refresh uses) so tests can drive this exact code path without a real adb process. One
    /// click still means exactly one discovery call.
    /// </summary>
    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (_stage == DeviceSetupStage.ToolsUnavailable) return;

        _verifyInProgress = true;
        RenderFooter();
        try
        {
            var discover = DiscoverDevicesOverrideForTests ?? (() => AdbService.DiscoverDevicesAsync());
            var result = await discover();
            // The window may have closed while the discovery call was in flight; an awaited
            // continuation must never repaint a torn-down visual tree.
            if (_closed) return;

            // DEV-01 convention: an unsuccessful query leaves the last known devices in place
            // rather than being read as "everything unplugged".
            if (result.IsSuccess) _devices = result.Devices.ToList();
            RenderState();
        }
        finally
        {
            _verifyInProgress = false;
            if (!_closed) RenderFooter();
        }
    }

    /// <summary>
    /// M4 repair: Finish no longer touches <see cref="AppState"/> itself — the caller
    /// (<c>DashboardView.Help_Click</c>) is now the one authoritative path that applies
    /// <see cref="Result"/>, so the same state is never set once here and once more outside.
    /// </summary>
    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (_stage != DeviceSetupStage.Ready || _focused == null) return;
        Result = new SetupWizardResult(_focused, _devices);
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            // Constructed via `new DeviceSetupWizardWindow().Show()` rather than ShowDialog —
            // PLP_SELFTEST and the snapshot tool mode both do this. DialogResult can only be set
            // for a window shown modally; Result above is the contract those callers read
            // instead.
        }
        Close();
    }

    private void WhyToggle_Click(object sender, RoutedEventArgs e)
    {
        _whyExpanded = !_whyExpanded;
        WhyText.Visibility = _whyExpanded ? Visibility.Visible : Visibility.Collapsed;
        var label = _whyExpanded ? "إخفاء التوضيح" : "لماذا نحتاج هذا؟";
        WhyToggleBtn.Content = label;
        System.Windows.Automation.AutomationProperties.SetName(WhyToggleBtn, label);
    }

    private void SupportLink_Click(object sender, RoutedEventArgs e)
        => new AboutWindow { Owner = this }.ShowDialog();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        // Minor repair: PreviewKeyDown tunnels from the Window down BEFORE the ComboBox's own
        // bubbling Escape handling ever runs, so without this check Escape closed the whole
        // wizard out from under an open dropdown instead of just dismissing it.
        if (ManufacturerCombo.IsDropDownOpen)
        {
            ManufacturerCombo.IsDropDownOpen = false;
            e.Handled = true;
            return;
        }

        // Closing never changes device state, so Escape is always safe here.
        Close();
    }
}

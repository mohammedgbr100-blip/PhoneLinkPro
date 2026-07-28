namespace ScrcpyDeck.Services;

/// <summary>
/// Repair batch (independent review, blocking B1 / M3): the one authoritative classification of
/// every hidden, environment-variable-triggered automated process PhoneLink Pro can run as,
/// instead of the eight independent <c>Environment.GetEnvironmentVariable(...)</c> checks that
/// used to be scattered across <c>App.OnStartup</c> (one per tool mode, plus PLP_SELFTEST).
///
/// <para>
/// This enum and <see cref="AutomatedModeClassifier"/> exist so App.xaml.cs can install log
/// isolation for the CORRECT directory before the very first <c>Log.*</c> call, for every known
/// automated mode uniformly, from a single call site — see <see cref="AutomatedModeClassifier.Classify"/>.
/// </para>
/// </summary>
internal enum AutomatedMode
{
    /// <summary>Normal interactive application. Never automated-mode-isolated.</summary>
    None,

    /// <summary>PLP_SELFTEST=1 — constructs every window and runs every headless self-test.</summary>
    SelfTest,

    /// <summary>PLP_INTEGRITY_TOOL=verify|generate — build-time engine-manifest tool.</summary>
    IntegrityTool,

    /// <summary>PLP_TOOL_MODE=shell-snapshot.</summary>
    ShellSnapshot,

    /// <summary>PLP_TOOL_MODE=dashboard-snapshot.</summary>
    DashboardSnapshot,

    /// <summary>PLP_TOOL_MODE=setup-wizard-snapshot.</summary>
    SetupWizardSnapshot,

    /// <summary>PLP_TOOL_MODE=wireless-snapshot.</summary>
    WirelessSnapshot,

    /// <summary>PLP_TOOL_MODE=transfers-snapshot.</summary>
    TransfersSnapshot,

    /// <summary>PLP_TOOL_MODE=settings-snapshot.</summary>
    SettingsSnapshot,

    /// <summary>PLP_TOOL_MODE=about-snapshot.</summary>
    AboutSnapshot,
}

/// <summary>
/// Result of <see cref="AutomatedModeClassifier.Classify"/>: exactly one resolved
/// <see cref="AutomatedMode"/> (or <see cref="AutomatedMode.None"/>), or a conflict when more
/// than one independent automated-mode signal was present in the environment at once.
/// </summary>
internal readonly record struct AutomatedModeResult(
    AutomatedMode Mode,
    bool Conflict,
    IReadOnlyList<AutomatedMode> DetectedSignals);

/// <summary>
/// Classifies the current process as normal or one specific automated mode, from an explicit
/// known-mode allowlist — never an environment-variable-prefix wildcard, so an unrelated
/// <c>PLP_</c>-prefixed variable a developer happens to have set can never silently enable test
/// behavior. Two independent environment signals participate:
/// <c>PLP_SELFTEST</c>/<c>PLP_INTEGRITY_TOOL</c> (each a mode of their own), and
/// <c>PLP_TOOL_MODE</c> (one of seven known snapshot-tool values, mirroring each tool mode's own
/// <c>ModeValue</c> constant). Because they are independent variables, a caller COULD set more
/// than one at once (e.g. both PLP_SELFTEST=1 and PLP_TOOL_MODE=about-snapshot); that is treated
/// as a hard conflict, not a silently-resolved precedence, so two automated processes can never
/// run entangled in the same instance.
/// </summary>
internal static class AutomatedModeClassifier
{
    internal static AutomatedModeResult Classify(Func<string, string?> getEnvironmentVariable)
    {
        var detected = new List<AutomatedMode>();

        if (getEnvironmentVariable(IntegrityToolMode.ModeVariable) is { Length: > 0 })
            detected.Add(AutomatedMode.IntegrityTool);

        if (getEnvironmentVariable("PLP_SELFTEST") == "1")
            detected.Add(AutomatedMode.SelfTest);

        // Case-insensitive equality against each exact ModeValue — matching every individual
        // tool mode's own IsRequested() comparison (StringComparison.OrdinalIgnoreCase) exactly,
        // rather than a switch expression's default ordinal constant-pattern matching (which
        // would silently stop recognizing "About-Snapshot" or "ABOUT-SNAPSHOT"). Any other
        // PLP_TOOL_MODE value (including a typo or a future, not-yet-wired mode) is deliberately
        // NOT a known mode: unknown values must never auto-enable test behavior.
        var toolMode = getEnvironmentVariable(ShellSnapshotToolMode.ModeVariable)?.Trim();
        AutomatedMode? snapshotMode =
            Matches(toolMode, ShellSnapshotToolMode.ModeValue) ? AutomatedMode.ShellSnapshot :
            Matches(toolMode, DashboardSnapshotToolMode.ModeValue) ? AutomatedMode.DashboardSnapshot :
            Matches(toolMode, DeviceSetupWizardSnapshotToolMode.ModeValue) ? AutomatedMode.SetupWizardSnapshot :
            Matches(toolMode, WirelessSnapshotToolMode.ModeValue) ? AutomatedMode.WirelessSnapshot :
            Matches(toolMode, TransfersSnapshotToolMode.ModeValue) ? AutomatedMode.TransfersSnapshot :
            Matches(toolMode, SettingsSnapshotToolMode.ModeValue) ? AutomatedMode.SettingsSnapshot :
            Matches(toolMode, AboutSnapshotToolMode.ModeValue) ? AutomatedMode.AboutSnapshot :
            null;
        if (snapshotMode is { } resolvedSnapshotMode)
            detected.Add(resolvedSnapshotMode);

        return detected.Count switch
        {
            0 => new AutomatedModeResult(AutomatedMode.None, Conflict: false, detected),
            1 => new AutomatedModeResult(detected[0], Conflict: false, detected),
            _ => new AutomatedModeResult(AutomatedMode.None, Conflict: true, detected),
        };
    }

    private static bool Matches(string? value, string modeValue)
        => string.Equals(value, modeValue, StringComparison.OrdinalIgnoreCase);
}

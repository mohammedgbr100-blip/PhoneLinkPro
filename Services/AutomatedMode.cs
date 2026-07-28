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
/// <see cref="AutomatedMode"/> (or <see cref="AutomatedMode.None"/>), a conflict when more than
/// one independent automated-mode signal was present in the environment at once, or an unknown
/// tool mode when <c>PLP_TOOL_MODE</c> carried a non-blank value that matches none of the known
/// snapshot modes.
///
/// <para>
/// Repair batch 5B: <see cref="UnknownToolMode"/> is a DISTINCT outcome from
/// <see cref="Conflict"/> — a non-blank, unrecognized <c>PLP_TOOL_MODE</c> value (a typo, or a
/// future value nothing has wired up yet) is not "two signals disagreeing", it is "one signal
/// nobody understands", and conflating the two would hide which diagnostic actually applies.
/// Both outcomes fail closed identically at the call site (<c>App.OnStartup</c>): never create
/// the normal shell, never start the device watcher, never touch the real log or settings.
/// </para>
/// </summary>
internal readonly record struct AutomatedModeResult(
    AutomatedMode Mode,
    bool Conflict,
    bool UnknownToolMode,
    IReadOnlyList<AutomatedMode> DetectedSignals,
    string? UnknownToolModeValue = null);

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

        // Repair batch 5B (§3): PLP_INTEGRITY_TOOL's own executor (IntegrityToolMode.TryRun)
        // treats null/empty/whitespace-only as "not requested" via IsNullOrWhiteSpace. The
        // classifier must agree EXACTLY, or a whitespace-only value gets classified here as
        // IntegrityTool but then TryRun silently declines to run anything, leaving the caller's
        // exit code at its default 0 — a whitespace value would silently "succeed" while doing
        // nothing. Length:>0 (the previous check) did not agree with that contract; this does.
        var integrityMode = getEnvironmentVariable(IntegrityToolMode.ModeVariable);
        if (!string.IsNullOrWhiteSpace(integrityMode))
            detected.Add(AutomatedMode.IntegrityTool);

        if (getEnvironmentVariable("PLP_SELFTEST") == "1")
            detected.Add(AutomatedMode.SelfTest);

        // Case-insensitive equality against each exact ModeValue — matching every individual
        // tool mode's own former IsRequested() comparison (StringComparison.OrdinalIgnoreCase)
        // exactly, rather than a switch expression's default ordinal constant-pattern matching
        // (which would silently stop recognizing "About-Snapshot" or "ABOUT-SNAPSHOT").
        //
        // Repair batch 5B (§2): absent, empty, or whitespace-only PLP_TOOL_MODE (i.e. blank
        // after Trim()) is treated as "not requested" — consistent with the PLP_INTEGRITY_TOOL
        // contract just above, so both automated-mode variables share one blank-is-unset rule.
        // A NON-BLANK value that matches none of the seven known modes is a different thing
        // entirely: someone clearly intended an automated mode and got the value wrong (a typo,
        // stale documentation, a future mode nothing has wired up yet). Silently falling through
        // to a normal interactive launch there would start the real shell, the real device
        // watcher and real engine integrity/adb work under what the caller believed was a bounded
        // tool-mode run — so it is reported as UnknownToolMode instead of folding into
        // AutomatedMode.None, and App.OnStartup fails closed on it exactly like a conflict.
        var rawToolMode = getEnvironmentVariable(ShellSnapshotToolMode.ModeVariable);
        var toolMode = rawToolMode?.Trim();
        var unknownToolMode = false;

        if (!string.IsNullOrEmpty(toolMode))
        {
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
            else
                unknownToolMode = true;
        }

        // Unknown takes priority over both the normal count-based resolution AND Conflict: it is
        // reported as its own distinct outcome (never overloaded onto Conflict, even when a known
        // signal such as PLP_SELFTEST or PLP_INTEGRITY_TOOL is ALSO present at the same time —
        // both combinations must still fail closed, and DetectedSignals still carries whatever
        // known signals were also present for diagnostics).
        if (unknownToolMode)
            return new AutomatedModeResult(
                AutomatedMode.None, Conflict: false, UnknownToolMode: true, detected, rawToolMode);

        return detected.Count switch
        {
            0 => new AutomatedModeResult(AutomatedMode.None, Conflict: false, UnknownToolMode: false, detected),
            1 => new AutomatedModeResult(detected[0], Conflict: false, UnknownToolMode: false, detected),
            _ => new AutomatedModeResult(AutomatedMode.None, Conflict: true, UnknownToolMode: false, detected),
        };
    }

    private static bool Matches(string? value, string modeValue)
        => string.Equals(value, modeValue, StringComparison.OrdinalIgnoreCase);
}

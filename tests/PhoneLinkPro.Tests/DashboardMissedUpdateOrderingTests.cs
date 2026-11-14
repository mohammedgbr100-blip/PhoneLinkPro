using System.Reflection;
using ScrcpyDeck.Views;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 3D — DashboardView's missed-update ordering guard.
///
/// <para>
/// The genuine behavioural proof — pausing RefreshAsync's discovery and a busy operation's
/// enrichment with TaskCompletionSources, racing queued watcher snapshots against them, and
/// confirming an older snapshot never overwrites a newer one — runs in
/// DashboardMissedUpdateOrderingSelfTest under PLP_SELFTEST, for the same reason Batch 3B/3C's
/// equivalent proofs do not live in xUnit: constructing a real DashboardView needs
/// Application.Resources merged, which means constructing ScrcpyDeck.App, which would change
/// DeviceWatcher.Start()'s dispatcher-timer branch for every other test class sharing this
/// xUnit process.
/// </para>
///
/// <para>
/// What IS safely verifiable here: the reflection-visible shape of the new ordering fields and
/// seam (not a source-text guess), and that every call site the review named actually threads
/// the sequence parameter through. This class is supplementary coverage only; it is not the
/// proof that the guard works.
/// </para>
/// </summary>
public sealed class DashboardMissedUpdateOrderingTests
{
    // ---------------------------------------------------------------- reflection-verified shape

    [Fact] // the sequence counter is a real field, not merely mentioned in a comment
    public void NextSequence_IsAPrivateInstanceInt()
    {
        var field = typeof(DashboardView).GetField(
            "_nextSequence", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(int), field!.FieldType);
        Assert.False(field.IsStatic, "must be per-instance: each cached view tracks its own ordering");
    }

    [Fact] // the high-water mark is a real field, not merely mentioned in a comment
    public void LastAppliedSequence_IsAPrivateInstanceInt()
    {
        var field = typeof(DashboardView).GetField(
            "_lastAppliedSequence", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(int), field!.FieldType);
        Assert.False(field.IsStatic, "must be per-instance: each cached view tracks its own ordering");
    }

    [Fact] // the discovery seam is internal, static, nullable and defaults to null (production)
    public void DiscoverDevicesOverrideForTests_IsAnInternalStaticNullableFuncDefaultingToNull()
    {
        var field = typeof(DashboardView).GetField(
            "DiscoverDevicesOverrideForTests", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(field!.IsStatic);
        Assert.True(field.IsAssembly, "must be internal, not public — never a caller-facing switch");
        Assert.False(field.IsPublic);
        Assert.Null(field.GetValue(null));
    }

    // ---------------------------------------------------------------- supplementary source guard
    // Not the proof — DashboardMissedUpdateOrderingSelfTest (PLP_SELFTEST) is. This only confirms
    // every mutation point the review named is actually gated, and cannot regress silently.

    [Fact] // [REGRESSION] a snapshot can only ever apply if it is strictly newer than the last
           // one actually applied — the fix for the ordering gap Batch 3C's review deferred
    public void Source_ApplyDevicesCoreAsyncRefusesSnapshotsNotStrictlyNewerThanTheLastApplied()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");
        var body = ExtractMethodBody(code, "private async Task ApplyDevicesCoreAsync(");

        var entryGuard = body.IndexOf(
            "if (!IsCurrent(generation) || sequence <= _lastAppliedSequence) return;", StringComparison.Ordinal);
        var claim = body.IndexOf("_lastAppliedSequence = sequence;", StringComparison.Ordinal);

        Assert.True(entryGuard >= 0, "the combined generation/sequence entry guard must exist");
        Assert.True(claim > entryGuard,
            "the sequence must be claimed only after the entry guard passes");
    }

    [Fact] // [REGRESSION] both entry points that can leave work queued behind them must drain
           // that queue through the same shared helper before giving up _busy
    public void Source_RefreshAsyncAndApplyDevicesAsyncBothDrainQueuedUpdates()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");

        var refreshBody = ExtractMethodBody(code, "private async Task RefreshAsync()");
        var applyBody = ExtractMethodBody(code, "private async Task ApplyDevicesAsync(");

        Assert.Contains("await DrainQueuedUpdatesAsync(generation);", refreshBody);
        Assert.Contains("await DrainQueuedUpdatesAsync(generation);", applyBody);

        // The drain itself must still stop the instant the generation goes stale, and must
        // thread each dequeued snapshot's own captured sequence, not the caller's.
        var drainBody = ExtractMethodBody(code, "private async Task DrainQueuedUpdatesAsync(");
        Assert.Contains("while (IsCurrent(generation) && _missedUpdate is { } queued)", drainBody);
        Assert.Contains(
            "await ApplyDevicesCoreAsync(queued.Devices, generation, queued.Sequence);", drainBody);
    }

    [Fact] // [REGRESSION] a snapshot queued while busy must capture its own sequence at the
           // moment it is observed, not reuse whatever the busy owner captured
    public void Source_QueuedSnapshotsCaptureTheirOwnSequence()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");
        var applyBody = ExtractMethodBody(code, "private async Task ApplyDevicesAsync(");

        var sequenceCapture = applyBody.IndexOf("var sequence = ++_nextSequence;", StringComparison.Ordinal);
        var queueWrite = applyBody.IndexOf("_missedUpdate = (devices, sequence);", StringComparison.Ordinal);

        Assert.True(sequenceCapture >= 0 && queueWrite > sequenceCapture,
            "the sequence must be captured before it is stored alongside the queued devices");
    }

    // ---------------------------------------------------------------- Batch 5A: deterministic
    // synchronization contract (supplementary source guard, not the behavioral proof — see
    // DashboardMissedUpdateOrderingSelfTest's own remarks on why Scenarios C and E previously
    // raced a fixed dispatcher-pump count against an asynchronous TCS continuation)

    [Fact] // [REGRESSION] the self-test's wait primitive must key off the real causal signal —
           // not merely pump a fixed number of dispatcher frames and hope
    public void Source_RunAndWaitForSettlePumpsAgainstTheOperationSettledSignalNotAFixedCount()
    {
        var code = TestPaths.ReadRepoFile("Services", "DashboardMissedUpdateOrderingSelfTest.cs");
        var body = ExtractMethodBody(code, "private static bool RunAndWaitForSettle(");

        Assert.Contains("DispatcherWait.UntilSignaled(", body);
        Assert.Contains("DashboardView.OperationSettledForTests = signal;", body);
        Assert.Contains("return settled;", body);
    }

    [Fact] // [REGRESSION] public-release audit: the intermittent
           // RealLogDirectory_IsUnchangedAcrossACompleteSelfTestProcess failure was a 200-pump
           // budget expiring while the awaited continuation was still in its thread-pool hop.
           // No wait in this harness may be bounded by a pump COUNT again — only by wall-clock
           // time through DispatcherWait (behavior proven by DispatcherWaitTests).
    public void Source_NoHarnessWaitIsBoundedByAPumpCount()
    {
        var code = TestPaths.ReadRepoFile("Services", "DashboardMissedUpdateOrderingSelfTest.cs");

        Assert.DoesNotContain("maxIterations", code, StringComparison.Ordinal);
        var pumpUntil = ExtractMethodBody(code, "private static bool PumpUntilComplete(");
        Assert.Contains("DispatcherWait.UntilCompleted(", pumpUntil);
    }

    [Fact] // [REGRESSION] independent review: after one genuine hang, the remaining waits of the
           // run must be short, so a hung run still reports its named failures inside the
           // caller's process timeout (AutomatedModeTests 60 s, release.ps1 120 s)
    public void Source_EveryHarnessWaitDrawsOnOneWaitBudgetResetPerRun()
    {
        var code = TestPaths.ReadRepoFile("Services", "DashboardMissedUpdateOrderingSelfTest.cs");

        var settle = ExtractMethodBody(code, "private static bool RunAndWaitForSettle(");
        Assert.Contains("timeout ?? s_budget.Next", settle);
        Assert.Contains("s_budget.Record(", settle);

        var pumpUntil = ExtractMethodBody(code, "private static bool PumpUntilComplete(");
        Assert.Contains("s_budget.Record(DispatcherWait.UntilCompleted(task, s_budget.Next))", pumpUntil);

        // a fresh budget at the start of each entry point, so one run's hang never shortens the next
        Assert.Contains("s_budget = new WaitBudget(DispatcherWait.DefaultTimeout);",
            ExtractMethodBody(code, "internal static Result Run("));
        Assert.Contains("s_budget = new WaitBudget(DispatcherWait.DefaultTimeout);",
            ExtractMethodBody(code, "internal static Result RunOrderingStressGate("));
        Assert.DoesNotContain(", DispatcherWait.DefaultTimeout)", settle + pumpUntil);
    }

    [Fact] // [REGRESSION] Scenario C's release of the busy-holding operation, and Scenario E's
           // failure completion, must each be awaited through RunAndWaitForSettle — the two
           // exact points that used to race a fixed Pump() against an async continuation
    public void Source_ScenarioCAndEWaitForSettleAroundTheirTcsResolution()
    {
        var code = TestPaths.ReadRepoFile("Services", "DashboardMissedUpdateOrderingSelfTest.cs");

        var scenarioC = ExtractMethodBody(code, "private static (int Total, List<string> Failures, string Summary) ScenarioC(");
        var scenarioE = ExtractMethodBody(code, "private static (int Total, List<string> Failures, string Summary) ScenarioE(");

        Assert.Contains("var settled = RunAndWaitForSettle(holdTcs.SetResult);", scenarioC);
        Assert.Contains("var settledFailure = RunAndWaitForSettle(", scenarioE);

        // The previous, occasionally-flaky design blindly pumped a fixed number of frames right
        // after resolving the TCS, with no regard for whether the resulting continuation had
        // actually run yet — that helper must no longer exist at all.
        Assert.DoesNotContain("private static void Pump()", code);
    }

    [Fact] // [REGRESSION] the deterministic ordering stress gate (Batch 5A) must actually exist
           // and exercise both previously-flaky scenarios, not just Scenario A-F run once
    public void Source_OrderingStressGateExistsAndCoversScenariosCAndE()
    {
        var code = TestPaths.ReadRepoFile("Services", "DashboardMissedUpdateOrderingSelfTest.cs");
        var body = ExtractMethodBody(code, "internal static Result RunOrderingStressGate(");

        Assert.Contains("ScenarioC($\"stress-c-iter{i}\")", body);
        Assert.Contains("ScenarioE($\"stress-e-iter{i}\")", body);
    }

    /// <summary>
    /// Extracts one method's full body (signature through its closing brace) by counting braces
    /// from the first '{' after <paramref name="signaturePrefix"/> (mirrors the helper in
    /// DashboardStaleContinuationTests; duplicated rather than shared so each batch's
    /// supplementary tests stay self-contained).
    /// </summary>
    private static string ExtractMethodBody(string code, string signaturePrefix)
    {
        var start = code.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signaturePrefix}' must still exist");

        var braceStart = code.IndexOf('{', start);
        Assert.True(braceStart > start, $"'{signaturePrefix}' must be followed by a method body");

        var depth = 0;
        for (var i = braceStart; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[start..(i + 1)];
        }

        throw new InvalidOperationException($"'{signaturePrefix}' has no matching closing brace");
    }
}

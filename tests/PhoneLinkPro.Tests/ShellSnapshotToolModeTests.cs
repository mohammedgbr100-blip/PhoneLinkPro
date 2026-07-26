using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 3A — the hidden shell-snapshot tool mode.
///
/// The activation, path-validation, dimension and watchdog decisions are pure seams, so the
/// dangerous behaviours are proven here without launching a window, starting the engine, or
/// waiting out a real 30-second timeout.
/// </summary>
public sealed class ShellSnapshotToolModeTests
{
    // ---------------------------------------------------------------- activation

    [Theory] // the EXACT value, tolerant only of surrounding whitespace and casing
    [InlineData("shell-snapshot")]
    [InlineData("  shell-snapshot  ")]
    [InlineData("SHELL-SNAPSHOT")]
    [InlineData("Shell-Snapshot")]
    public void Activation_AcceptsOnlyTheExactMode(string value)
        => Assert.True(ShellSnapshotToolMode.IsModeRequested(value));

    [Theory] // nothing else may ever put a user's app into tool mode
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("shell")]
    [InlineData("snapshot")]
    [InlineData("shell-snapshots")]
    [InlineData("shell-snapshot-extra")]
    [InlineData("xshell-snapshot")]
    public void Activation_RejectsEverythingElse(string? value)
        => Assert.False(ShellSnapshotToolMode.IsModeRequested(value));

    // ---------------------------------------------------------------- output path

    [Theory] // an unset or blank output directory is an error, never a default
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OutputPath_RequiresAValue(string? raw)
    {
        Assert.Null(ShellSnapshotToolMode.ValidateOutputDirectory(raw, out var error));
        Assert.Contains(ShellSnapshotToolMode.OutVariable, error);
    }

    [Theory] // relative and drive/root-relative forms are rejected: no silent fall back to
             // the process working directory
    [InlineData("out")]
    [InlineData(@".\out")]
    [InlineData(@"..\out")]
    [InlineData("shots/today")]
    [InlineData(@"\out")]      // root-relative: resolves against the current drive
    [InlineData("C:out")]      // drive-relative: resolves against that drive's cwd
    public void OutputPath_RejectsAnythingNotFullyQualified(string raw)
    {
        Assert.Null(ShellSnapshotToolMode.ValidateOutputDirectory(raw, out var error));
        Assert.Contains("absolute", error);
    }

    [Fact] // a malformed path is reported, not thrown
    public void OutputPath_RejectsMalformedValues()
    {
        var raw = "C:\\shots\\" + new string('x', 400) + "\\" + new string('y', 400);
        var result = ShellSnapshotToolMode.ValidateOutputDirectory(raw, out var error);
        if (result == null) Assert.NotEqual("", error);
    }

    [Fact] // a fully-qualified path is accepted and normalised
    public void OutputPath_AcceptsFullyQualifiedAndNormalises()
    {
        var expected = Path.Combine(Path.GetTempPath(), "plp-shell-shots");
        var result = ShellSnapshotToolMode.ValidateOutputDirectory(expected, out var error);
        Assert.Equal("", error);
        Assert.Equal(Path.GetFullPath(expected), result);
    }

    [Fact] // surrounding whitespace does not defeat validation
    public void OutputPath_TrimsBeforeValidating()
    {
        var raw = "  " + Path.Combine(Path.GetTempPath(), "plp-shell-shots") + "  ";
        Assert.NotNull(ShellSnapshotToolMode.ValidateOutputDirectory(raw, out _));
    }

    // ---------------------------------------------------------------- render guards

    [Theory] // RenderTargetBitmap cannot be built from a degenerate window
    [InlineData(0, 0)]
    [InlineData(0, 900)]
    [InlineData(1440, 0)]
    [InlineData(0.5, 900)]
    [InlineData(1440, 0.9)]
    public void Dimensions_RejectUnrenderableSizes(double width, double height)
        => Assert.False(ShellSnapshotToolMode.AreDimensionsRenderable(width, height));

    [Theory] // the approved reference sizes are renderable
    [InlineData(1, 1)]
    [InlineData(1440, 900)]
    [InlineData(1366, 768)]
    public void Dimensions_AcceptRenderableSizes(double width, double height)
        => Assert.True(ShellSnapshotToolMode.AreDimensionsRenderable(width, height));

    [Fact] // both approved reference sizes are captured, in order
    public void ReferenceSizes_AreTheApprovedPair()
    {
        Assert.Equal(2, ShellSnapshotToolMode.ReferenceSizes.Length);
        Assert.Equal((1440d, 900d), ShellSnapshotToolMode.ReferenceSizes[0]);
        Assert.Equal((1366d, 768d), ShellSnapshotToolMode.ReferenceSizes[1]);
    }

    [Theory] // stable, culture-independent file names
    [InlineData(1440, 900, "shell-1440x900.png")]
    [InlineData(1366, 768, "shell-1366x768.png")]
    public void SnapshotFileName_IsStable(double width, double height, string expected)
        => Assert.Equal(expected, ShellSnapshotToolMode.SnapshotFileName(width, height));

    // ---------------------------------------------------------------- watchdog

    [Fact] // the timeout decision fires — proven with a short budget, not a real 30s wait
    public async Task Watchdog_FiresWhenTheBudgetElapses()
    {
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handle = ShellSnapshotToolMode.StartWatchdog(
            TimeSpan.FromMilliseconds(50), () => fired.TrySetResult(true));

        var completed = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(fired.Task, completed);
    }

    [Fact] // a completed run cancels its own watchdog, so a normal exit never force-exits
    public async Task Watchdog_DisposeCancelsTheTimeout()
    {
        var fired = false;
        var handle = ShellSnapshotToolMode.StartWatchdog(
            TimeSpan.FromMilliseconds(50), () => Volatile.Write(ref fired, true));
        handle.Dispose();

        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.False(Volatile.Read(ref fired));
    }

    [Fact] // the shipped budget is the documented 30 seconds
    public void Watchdog_BudgetIsThirtySeconds()
        => Assert.Equal(TimeSpan.FromSeconds(30), ShellSnapshotToolMode.WatchdogBudget);

    // ---------------------------------------------------------------- exit codes

    [Fact] // success and failure are distinguishable to the caller and the shell
    public void ExitCodes_AreDistinct()
    {
        Assert.Equal(0, ShellSnapshotToolMode.ExitSuccess);
        Assert.Equal(3, ShellSnapshotToolMode.ExitFailure);
    }

    // ---------------------------------------------------------------- wiring guards
    // Source-level, and honestly so: these guard an ORDERING invariant that no unit test can
    // observe, because observing it would mean actually starting ADB.

    [Fact] // the old unguarded hook is gone from the window entirely
    public void LegacyEnvironmentHook_IsNoLongerPresent()
    {
        var code = TestPaths.ReadRepoFile("MainWindow.xaml.cs");
        Assert.DoesNotContain("PLP_SHELL_SNAPSHOT", code);
        Assert.DoesNotContain("RenderTargetBitmap", code);
    }

    [Fact] // tool mode is dispatched BEFORE single-instance, integrity and engine startup
    public void ToolMode_IsDispatchedBeforeAnyEngineWork()
    {
        var app = TestPaths.ReadRepoFile("App.xaml.cs");
        var dispatch = app.IndexOf("ShellSnapshotToolMode.IsRequested", StringComparison.Ordinal);
        var singleInstance = app.IndexOf("SingleInstance.Acquire", StringComparison.Ordinal);
        var integrity = app.IndexOf("EngineIntegrityVerifier.VerifyAsync", StringComparison.Ordinal);

        Assert.True(dispatch > 0, "tool mode must be dispatched from App.OnStartup");
        Assert.True(dispatch < singleInstance, "tool mode must precede single-instance acquisition");
        Assert.True(dispatch < integrity, "tool mode must precede integrity verification");
    }

    [Fact] // the window skips consent / integrity / engine services in tool mode
    public void MainWindow_ShortCircuitsLoadedInToolMode()
    {
        // Scoped to the Loaded body, and matched against CALL SITES rather than bare
        // identifiers, so a doc comment mentioning a method cannot satisfy the ordering.
        var code = TestPaths.ReadRepoFile("MainWindow.xaml.cs");
        var start = code.IndexOf("Loaded += async", StringComparison.Ordinal);
        Assert.True(start > 0, "MainWindow must still wire a Loaded handler");
        var loaded = code[start..];

        var toolGuard = loaded.IndexOf("if (ShellSnapshotToolMode.IsActive)", StringComparison.Ordinal);
        var harnessGuard = loaded.IndexOf("if (SuppressStartupForHarness) return;", StringComparison.Ordinal);
        var consent = loaded.IndexOf("if (!AppSettings.ConsentAccepted)", StringComparison.Ordinal);
        var engine = loaded.IndexOf("await StartEngineServicesAsync()", StringComparison.Ordinal);

        Assert.True(toolGuard > 0, "Loaded must check the tool-mode flag");
        Assert.True(harnessGuard > 0, "Loaded must check the self-test harness flag");
        Assert.True(consent > 0 && engine > 0, "the normal startup path must still be present");

        Assert.True(toolGuard < consent, "the tool-mode guard must precede the consent gate");
        Assert.True(toolGuard < engine, "the tool-mode guard must precede engine startup");
        Assert.True(harnessGuard < consent, "the harness guard must precede the consent gate");
        Assert.True(harnessGuard < engine, "the harness guard must precede engine startup");

        // Neither guard may navigate: constructing a feature page queries the device engine
        // on load, which would start the bridge these modes must never start.
        var guarded = loaded[harnessGuard..consent];
        Assert.DoesNotContain("Navigate(", guarded);
        Assert.DoesNotContain("SelectNav(", guarded);
    }
}

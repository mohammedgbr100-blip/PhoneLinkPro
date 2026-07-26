using System.Reflection;
using ScrcpyDeck.Views;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 3C — DashboardView's stale-async-continuation guard.
///
/// <para>
/// The genuine behavioural proof — pausing a real ApplyDevicesCoreAsync call mid-enrichment
/// with a TaskCompletionSource, unloading the view, and confirming the resumed continuation
/// touches neither DeviceList nor AppState.CurrentDevice — runs in
/// DashboardStaleContinuationSelfTest under PLP_SELFTEST, for the same reason Batch 3B's
/// equivalent proof does not live in xUnit: constructing a real DashboardView needs
/// Application.Resources merged, which means constructing ScrcpyDeck.App, which would change
/// DeviceWatcher.Start()'s dispatcher-timer branch for every other test class sharing this
/// xUnit process.
/// </para>
///
/// <para>
/// What IS safely verifiable here: the reflection-visible shape of the guard (a real, private,
/// zero-defaulting generation counter and a matching predicate — not a source-text guess), and
/// that every mutation point the review named is actually gated in source. The second class is
/// supplementary coverage only; it is not the proof that the guard works.
/// </para>
/// </summary>
public sealed class DashboardStaleContinuationTests
{
    // ---------------------------------------------------------------- reflection-verified shape

    [Fact] // the generation counter is a real field, not merely mentioned in a comment
    public void Generation_IsAPrivateInstanceInt()
    {
        // Cannot construct a DashboardView here to check its runtime default (0): xUnit runs
        // MTA with no Application.Resources merged, so DashboardView.xaml's StaticResource
        // lookups cannot resolve — see the class remarks. Shape only; PLP_SELFTEST proves the
        // runtime value via DashboardStaleContinuationSelfTest's own "A" scenario (a fresh
        // instance's first operation is never treated as stale).
        var field = typeof(DashboardView).GetField(
            "_generation", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(int), field!.FieldType);
        Assert.False(field.IsStatic, "must be per-instance: each cached view tracks its own generation");
    }

    [Fact] // the staleness predicate exists and has the shape callers rely on
    public void IsCurrent_MethodExistsWithAnIntParameterAndBoolReturn()
    {
        var method = typeof(DashboardView).GetMethod(
            "IsCurrent", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);
        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(int), parameters[0].ParameterType);
    }

    [Fact] // the enrichment seam is internal, static, nullable and defaults to null (production)
    public void EnrichOverrideForTests_IsAnInternalStaticNullableFuncDefaultingToNull()
    {
        var field = typeof(DashboardView).GetField(
            "EnrichOverrideForTests", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(field!.IsStatic);
        Assert.True(field.IsAssembly, "must be internal, not public — never a caller-facing switch");
        Assert.False(field.IsPublic);
        Assert.Null(field.GetValue(null));
    }

    // ---------------------------------------------------------------- supplementary source guard
    // Not the proof — DashboardStaleContinuationSelfTest (PLP_SELFTEST) is. This only confirms
    // every mutation point the review named is actually gated, and cannot regress silently.

    [Fact] // [REGRESSION] Unloaded must invalidate in-flight work AND release the busy gate,
           // or a fresh reload can be silently blocked by an abandoned operation
    public void Source_UnloadedInvalidatesGenerationAndReleasesBusyState()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");
        var start = code.IndexOf("private void DashboardView_Unloaded(", StringComparison.Ordinal);
        Assert.True(start > 0, "DashboardView must still wire an Unloaded handler");
        var end = code.IndexOf("private void Subscribe()", start, StringComparison.Ordinal);
        var body = code[start..end];

        Assert.Contains("Unsubscribe();", body);
        Assert.Contains("_generation++;", body);
        Assert.Contains("_busy = false;", body);
        Assert.Contains("_missedUpdate = null;", body);
    }

    [Fact] // [REGRESSION] every mutation ApplyDevicesCoreAsync performs after its own await
           // must be preceded by a fresh staleness check
    public void Source_ApplyDevicesCoreAsyncGuardsBeforeAndAfterItsOwnAwait()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");
        var body = ExtractMethodBody(code, "private async Task ApplyDevicesCoreAsync(");

        var entryGuard = body.IndexOf("if (!IsCurrent(generation)) return;", StringComparison.Ordinal);
        var enrichAwait = body.IndexOf("await Task.WhenAll(", StringComparison.Ordinal);
        var postAwaitGuard = body.IndexOf(
            "if (!IsCurrent(generation)) return;", enrichAwait, StringComparison.Ordinal);
        var refresh = body.IndexOf("DeviceList.Items.Refresh();", StringComparison.Ordinal);
        var secondSetCurrent = body.LastIndexOf("AppState.SetCurrent(keep);", StringComparison.Ordinal);

        Assert.True(entryGuard >= 0 && entryGuard < enrichAwait,
            "the entry guard must precede the enrichment await");
        Assert.True(postAwaitGuard > enrichAwait,
            "a staleness check must follow the enrichment await");
        Assert.True(postAwaitGuard < refresh && postAwaitGuard < secondSetCurrent,
            "the post-await guard must precede both Items.Refresh() and the second SetCurrent");
    }

    [Fact] // [REGRESSION] a resumed continuation must never clobber a busy flag or coalescing
           // queue that a newer generation now owns
    public void Source_FinallyBlocksAndCoalescingLoopAreGenerationGuarded()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");

        // RefreshAsync's finally: _busy and RefreshBtn.IsEnabled must both sit INSIDE the
        // IsCurrent guard, not merely appear somewhere near it. Structural (comments stripped,
        // whitespace collapsed) rather than indentation-exact, so a reformat can't falsely fail
        // this, and a guard that stopped actually wrapping the statements can't falsely pass it.
        var refreshBody = ExtractMethodBody(code, "private async Task RefreshAsync()");
        var refreshFinallyIdx = refreshBody.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(refreshFinallyIdx >= 0, "RefreshAsync must still have a finally block");
        var refreshFinally = NormalizeStructure(refreshBody[refreshFinallyIdx..]);
        Assert.Contains(
            "if (IsCurrent(generation)) { _busy = false; RefreshBtn.IsEnabled = true; }",
            refreshFinally);

        // ApplyDevicesAsync's finally: only release _busy when still current.
        Assert.Contains("if (IsCurrent(generation)) _busy = false;", code);

        // The coalescing loop stops the instant this generation goes stale.
        Assert.Contains("while (IsCurrent(generation) && _missedUpdate is { } queued)", code);
    }

    /// <summary>
    /// Extracts one method's full body (signature through its closing brace) by counting
    /// braces from the first '{' after <paramref name="signaturePrefix"/> — simple enough to
    /// stay honest about C#'s grammar without becoming a parser, and unlike slicing to the next
    /// known method or to end-of-file, it can't silently absorb whatever gets added after it.
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

    /// <summary>Strips // comments and collapses whitespace, so structural checks don't depend
    /// on exact indentation or comment wording.</summary>
    private static string NormalizeStructure(string code)
    {
        var noComments = System.Text.RegularExpressions.Regex.Replace(code, "//[^\n]*", "");
        return System.Text.RegularExpressions.Regex.Replace(noComments, @"\s+", " ").Trim();
    }

    [Fact] // the captured generation is threaded through every entry point that starts work
    public void Source_EveryEntryPointCapturesGenerationBeforeItsFirstAwait()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");

        Assert.Contains("var generation = _generation;", code);
        Assert.Contains("ApplyDevicesCoreAsync(result.Devices.ToList(), generation);", code);
        Assert.Contains("ApplyDevicesCoreAsync(known.ToList(), generation);", code);
        Assert.Contains("ApplyDevicesCoreAsync(devices, generation);", code);
        Assert.Contains("ApplyDevicesCoreAsync(queued, generation);", code);
    }

    [Fact] // the enrichment call site actually reaches for the test seam before the real one
    public void Source_EnrichmentCallSiteUsesTheOverrideSeam()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");

        Assert.Contains("EnrichOverrideForTests ?? AdbService.EnrichAsync", code);
    }
}

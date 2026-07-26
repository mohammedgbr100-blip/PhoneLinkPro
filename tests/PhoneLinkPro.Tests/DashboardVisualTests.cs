using System.Reflection;
using ScrcpyDeck.Views;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 4A — Dashboard 2.0 visual layer (repaired after the independent pre-commit review
/// found several of the original guards vacuous — in particular, an occurrence-count check
/// that could not tell a legitimate call site from a stray one anywhere else in the file).
///
/// <para>
/// The genuine behavioural proof — constructing a real DashboardView, feeding it fixture
/// devices through the DeviceWatcher seam, and asserting on the realised visual tree — runs in
/// DashboardVisualSelfTest under PLP_SELFTEST, for the same reason every other Dashboard proof
/// in this codebase does not live in xUnit (see DashboardStaleContinuationTests' remarks).
/// </para>
///
/// <para>
/// What IS safely verifiable here: the reflection-visible shape of the new view-state seam, and
/// — via source text, structurally rather than by mere occurrence count — that this batch wires
/// its new controls and events to the handlers the review named, introduces no second
/// subscription of any kind, and does not reintroduce the local-BorderBrush or raw-serial
/// defects the review found.
/// </para>
/// </summary>
public sealed class DashboardVisualTests
{
    // ---------------------------------------------------------------- reflection-verified shape

    [Fact] // the Batch 4A repair test seam is internal, read-only, and reflects the hero field
    public void HeroDeviceForTests_IsAnInternalReadOnlyProperty()
    {
        var prop = typeof(DashboardView).GetProperty("HeroDeviceForTests", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.Null(prop!.GetSetMethod(nonPublic: true));   // read-only, even internally
        Assert.False(prop.GetGetMethod(nonPublic: true)!.IsPublic);
    }

    // ---------------------------------------------------------------- supplementary source guard
    // Not the proof — DashboardVisualSelfTest (PLP_SELFTEST) is. This only confirms every new
    // interactive control reaches for an existing handler and that no button was left dead.

    [Fact] // [REGRESSION] the hero's primary action reuses the one existing select+mirror
           // handler — it must not gain a second, parallel implementation
    public void Source_HeroStartButtonUsesTheExistingSelectAndMirrorHandler()
    {
        var xaml = TestPaths.ReadRepoFile("Views", "DashboardView.xaml");
        Assert.Contains("x:Name=\"HeroStartBtn\"", xaml);
        Assert.Contains("Click=\"SelectAndMirror_Click\"", xaml);

        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");
        // Exactly one implementation of the click handler exists.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(
            code, @"private void SelectAndMirror_Click\(").Count;
        Assert.Equal(1, occurrences);
    }

    [Fact] // [REGRESSION] every <Button in the new XAML either has a Click handler or is the
           // one deliberately non-interactive case (none, currently) — no dead buttons
    public void Source_EveryButtonHasAClickHandler()
    {
        var xaml = TestPaths.ReadRepoFile("Views", "DashboardView.xaml");
        var buttonStarts = System.Text.RegularExpressions.Regex.Matches(xaml, @"<Button\b");
        Assert.True(buttonStarts.Count > 0, "the redesigned Dashboard must still contain buttons");

        foreach (System.Text.RegularExpressions.Match start in buttonStarts)
        {
            var tagEnd = xaml.IndexOf('>', start.Index);
            Assert.True(tagEnd > start.Index, "a <Button start tag must close");
            // The opening tag may be self-closing (attributes only) or open a content span;
            // either way every Click= attribute for THIS button appears before that first '>'.
            var openTag = xaml[start.Index..tagEnd];
            Assert.Contains("Click=\"", openTag);
        }
    }

    [Fact] // [REGRESSION] this batch must not introduce a second DeviceWatcher.DevicesChanged
           // subscription anywhere in the file — Subscribe()/Unsubscribe() remain the only one
    public void Source_NoAdditionalDeviceWatcherSubscriptionIntroduced()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");

        var subscribes = System.Text.RegularExpressions.Regex.Matches(
            code, @"DeviceWatcher\.DevicesChanged\s*\+=").Count;
        var unsubscribes = System.Text.RegularExpressions.Regex.Matches(
            code, @"DeviceWatcher\.DevicesChanged\s*-=").Count;

        Assert.Equal(1, subscribes);
        Assert.Equal(1, unsubscribes);
        Assert.Contains("DeviceWatcher.DevicesChanged += OnDevicesChanged;", code);
        Assert.Contains("DeviceWatcher.DevicesChanged -= OnDevicesChanged;", code);
    }

    [Fact] // [REGRESSION] the mirror/live-state repair's ScrcpyService.SessionsChanged
           // subscription is exactly balanced (one += and one -=), wired inside the SAME
           // Subscribe()/Unsubscribe() pair the DeviceWatcher subscription already uses — not a
           // second lifecycle bool, not a new event handler pair somewhere else in the file
    public void Source_SessionsChangedSubscriptionIsSingleAndSharesTheExistingLifecycle()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");

        var subscribes = System.Text.RegularExpressions.Regex.Matches(
            code, @"ScrcpyService\.SessionsChanged\s*\+=").Count;
        var unsubscribes = System.Text.RegularExpressions.Regex.Matches(
            code, @"ScrcpyService\.SessionsChanged\s*-=").Count;
        Assert.Equal(1, subscribes);
        Assert.Equal(1, unsubscribes);

        var subscribeBody = ExtractMethodBody(code, "private void Subscribe()");
        var unsubscribeBody = ExtractMethodBody(code, "private void Unsubscribe()");
        Assert.Contains("ScrcpyService.SessionsChanged += OnSessionsChanged;", subscribeBody);
        Assert.Contains("ScrcpyService.SessionsChanged -= OnSessionsChanged;", unsubscribeBody);

        // Both subscriptions must be gated by the SAME guard field — one Loaded/Unloaded
        // lifecycle, not a second bool introduced just for the new event.
        Assert.Contains("if (_watcherSubscribed) return;", subscribeBody);
        Assert.Contains("if (!_watcherSubscribed) return;", unsubscribeBody);
    }

    [Fact] // [REGRESSION] the session-change handler only repaints live/offline state — it must
           // never call the full UpdateHero (which would reset the serial-reveal toggle merely
           // because a mirror session started or stopped)
    public void Source_OnSessionsChangedRepaintsOnlyLiveStateNeverFullUpdateHero()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");
        var body = ExtractMethodBody(code, "private void OnSessionsChanged()");

        Assert.Contains("UpdateHeroLiveState(", body);
        Assert.DoesNotContain("UpdateHero(", body.Replace("UpdateHeroLiveState(", ""));
    }

    [Fact] // [REGRESSION] the hero paint routine only runs from the same points
           // ApplyDevicesCoreAsync/ShowEmpty already existed at — never from a new async entry
           // point. Structural: verifies each call site is textually INSIDE the expected
           // method's own extracted body, not merely that the total occurrence count matches
           // (a plain count cannot tell a legitimate call site from one moved elsewhere).
    public void Source_UpdateHeroIsCalledOnlyFromApplyDevicesCoreAsyncAndShowEmpty()
    {
        var rawCode = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");
        // Comments stripped so a doc-comment MENTIONING "UpdateHero(" (e.g. explaining what
        // another method reads) can never be miscounted as a call site.
        var code = System.Text.RegularExpressions.Regex.Replace(rawCode, @"//[^\n]*", "");

        var definitions = System.Text.RegularExpressions.Regex.Matches(
            code, @"private void UpdateHero\(AdbDevice\? device\)").Count;
        Assert.Equal(1, definitions);

        var applyBody = ExtractMethodBody(code, "private async Task ApplyDevicesCoreAsync(");
        var showEmptyBody = ExtractMethodBody(code, "private void ShowEmpty(");
        var callsInApply = System.Text.RegularExpressions.Regex.Matches(applyBody, @"UpdateHero\(").Count;
        var callsInShowEmpty = System.Text.RegularExpressions.Regex.Matches(showEmptyBody, @"UpdateHero\(").Count;
        Assert.Equal(2, callsInApply);       // pre- and post-enrichment
        Assert.Equal(1, callsInShowEmpty);

        // Every call site in the whole file must fall inside one of those two method bodies —
        // catches a call added to some new async entry point that a bare total-count check
        // would miss as long as the grand total still added up to three.
        var totalCalls = System.Text.RegularExpressions.Regex.Matches(code, @"UpdateHero\(").Count;
        Assert.Equal(definitions + callsInApply + callsInShowEmpty, totalCalls);
    }

    [Fact] // [REGRESSION] M1 repair: the dead "selected lower row" presentation is gone —
           // SelectedSerial, SerialEqualsConverter, the accent-ring DataTrigger, and the "الجهاز
           // المحدد حالياً" badge can never come back, since the hero is the only current/selected
           // device presentation now and no lower row can ever legitimately match it
    public void Source_NoDeadSelectedRowArtifactsRemain()
    {
        var xaml = TestPaths.ReadRepoFile("Views", "DashboardView.xaml");
        Assert.DoesNotContain("<Border.BorderBrush>", xaml);
        Assert.DoesNotContain("SelectedSerial", xaml);
        Assert.DoesNotContain("SerialEquals", xaml);
        Assert.DoesNotContain("الجهاز المحدد حالياً", xaml);
        Assert.DoesNotContain("DataTrigger", xaml);   // the row Style now has only the hover Trigger

        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");
        Assert.DoesNotContain("SelectedSerial", code);

        var convertersCode = TestPaths.ReadRepoFile("Converters", "Converters.cs");
        Assert.DoesNotContain("SerialEqualsConverter", convertersCode);
    }

    [Fact] // [REGRESSION] M1 repair: the surviving other-device row Style still repaints on hover
           // — the hover Trigger was never removed, only the unreachable selected-row DataTrigger
    public void Source_RowHoverTriggerStillSetsTheStrongerBorderBrush()
    {
        var xaml = TestPaths.ReadRepoFile("Views", "DashboardView.xaml");
        var cardStart = xaml.IndexOf("<ItemsControl x:Name=\"DeviceList\">", StringComparison.Ordinal);
        Assert.True(cardStart >= 0, "the device-list ItemsControl must still exist");
        var cardEnd = xaml.IndexOf("</ItemsControl>", cardStart, StringComparison.Ordinal);
        var rowXaml = xaml[cardStart..cardEnd];

        Assert.Contains("Property=\"IsMouseOver\"", rowXaml);
        Assert.Contains("Setter Property=\"BorderBrush\" Value=\"{StaticResource Brush.Border.Strong}\"", rowXaml);
        Assert.Contains("Value=\"{StaticResource Brush.Border.Subtle}\"", rowXaml);
    }

    [Fact] // [REGRESSION] M4 repair: under this row's inherited FlowDirection="RightToLeft",
           // WPF mirrors the HorizontalAlignment axis, so the literal token "Left" — not
           // "Right" — is what renders the identity stack (name + masked serial) on the
           // PHYSICAL right, immediately beside the device icon, bounded to its own content
           // width rather than stretched across the whole star column. Comments are stripped
           // before matching so this can never pass by matching a comment that merely
           // discusses the physical/literal distinction, and the search is scoped to the
           // other-device row's own identity container so an unrelated
           // HorizontalAlignment="Left" elsewhere in the file cannot satisfy it.
    public void Source_RowIdentityIsPhysicallyRightAnchoredUnderRtl()
    {
        var rawXaml = TestPaths.ReadRepoFile("Views", "DashboardView.xaml");
        var xaml = System.Text.RegularExpressions.Regex.Replace(
            rawXaml, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);

        var listStart = xaml.IndexOf("<ItemsControl x:Name=\"DeviceList\">", StringComparison.Ordinal);
        Assert.True(listStart >= 0, "the device-list ItemsControl must still exist");
        var listEnd = xaml.IndexOf("</ItemsControl>", listStart, StringComparison.Ordinal);
        Assert.True(listEnd > listStart, "the device-list ItemsControl must still close");
        var rowXaml = xaml[listStart..listEnd];

        var panelStart = rowXaml.IndexOf("<StackPanel Grid.Column=\"1\"", StringComparison.Ordinal);
        Assert.True(panelStart >= 0, "the row's info/identity StackPanel must still exist");
        var panelTagEnd = rowXaml.IndexOf('>', panelStart);
        Assert.True(panelTagEnd > panelStart, "the identity StackPanel's opening tag must close");
        var panelOpenTag = rowXaml[panelStart..panelTagEnd];
        Assert.Contains("HorizontalAlignment=\"Left\"", panelOpenTag);

        var identityEnd = rowXaml.IndexOf("<Button Grid.Column=\"2\"", panelStart, StringComparison.Ordinal);
        Assert.True(identityEnd > panelStart, "the row's action button must still follow the identity stack");
        var identityXaml = rowXaml[panelStart..identityEnd];
        Assert.Contains("Converter={StaticResource SafeDeviceLabel}", identityXaml);
        Assert.Contains("{Binding Serial, Converter={StaticResource MaskSerial}}", identityXaml);
    }

    [Fact] // [REGRESSION] M2 repair: UpdateHero must judge hero identity by comparing the
    // PREVIOUS and NEW device's serial before overwriting _heroDevice, and reset the
    // serial-reveal toggle only when that identity actually changed — never unconditionally on
    // every call, which used to reveal-then-immediately-re-mask on every routine same-device
    // refresh/poll/enrichment repaint
    public void Source_UpdateHeroResetsRevealOnlyOnIdentityChange()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");
        var body = ExtractMethodBody(code, "private void UpdateHero(AdbDevice? device)");

        Assert.Contains("_heroDevice?.Serial", body);
        Assert.Contains("identityChanged", body);
        Assert.Contains("StringComparison.Ordinal", body);
        Assert.Contains("if (identityChanged) _serialRevealed = false;", body);
        // The unconditional reset the review flagged must be gone.
        Assert.DoesNotContain("_serialRevealed = false;   // never carry a reveal", body);
    }

    [Fact] // [REGRESSION] M3 repair: the device-list row never binds a raw device string
           // directly — the serial goes through the MaskSerial converter and the row title goes
           // through SafeDeviceLabel (DisplayName alone falls back to the raw serial when Model
           // is unknown, which is the normal state for an unauthorized device)
    public void Source_DeviceListRowNeverBindsARawSerialOrUnsafeDisplayName()
    {
        var xaml = TestPaths.ReadRepoFile("Views", "DashboardView.xaml");
        var cardStart = xaml.IndexOf("<ItemsControl x:Name=\"DeviceList\">", StringComparison.Ordinal);
        var cardEnd = xaml.IndexOf("</ItemsControl>", cardStart, StringComparison.Ordinal);
        var rowXaml = xaml[cardStart..cardEnd];

        // Serial is legitimately bound unmasked to Button.Tag (used only to look the device up
        // by serial in code-behind, never rendered) — the defect is a raw serial reaching a
        // TEXT binding, so only that display path is asserted here.
        Assert.DoesNotContain("Text=\"{Binding Serial}\"", rowXaml);
        Assert.DoesNotContain("Text=\"{Binding DisplayName}\"", rowXaml);
        Assert.Contains("{Binding Serial, Converter={StaticResource MaskSerial}}", rowXaml);
        Assert.Contains("Converter={StaticResource SafeDeviceLabel}", rowXaml);
    }

    [Fact] // [REGRESSION] M2 repair: the lower list is populated from a filtered "other
           // devices" projection that excludes the hero device, never the raw discovered list
    public void Source_LowerListIsPopulatedFromOtherDevicesNotTheRawSnapshot()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");
        Assert.Contains("private void ApplyOtherDevicesList(", code);

        var body = ExtractMethodBody(code, "private void ApplyOtherDevicesList(");
        Assert.Contains("Where(d => d.Serial != hero.Serial)", body);
        Assert.Contains("DeviceList.ItemsSource = others;", body);

        // ApplyDevicesCoreAsync must route through this projection, never assign the raw
        // snapshot straight to DeviceList.
        var applyBody = ExtractMethodBody(code, "private async Task ApplyDevicesCoreAsync(");
        Assert.Contains("ApplyOtherDevicesList(devices, hero);", applyBody);
        Assert.DoesNotContain("DeviceList.ItemsSource = devices;", applyBody);
    }

    /// <summary>
    /// Extracts one method's full body (signature through its closing brace) by counting
    /// braces from the first '{' after <paramref name="signaturePrefix"/> — mirrors
    /// DashboardStaleContinuationTests' own helper of the same shape.
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

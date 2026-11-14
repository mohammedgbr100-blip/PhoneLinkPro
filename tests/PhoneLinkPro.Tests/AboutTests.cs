using System.Diagnostics;
using System.Reflection;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 4F — About &amp; Support 2.0 (repair pass). Pure/unit coverage for the fixed-link
/// allowlist (<see cref="SafeLinkLauncher"/>), the privacy-safe support summary
/// (<see cref="SupportSummary"/>), the clipboard seam (<see cref="ClipboardService"/>), the
/// truthful engine manifest-format label (<see cref="EngineManifest.FormatVersionLabelArabic"/>),
/// and source-level guards that a future change cannot silently widen (a user-controlled URI, a
/// second Process.Start boundary, the old public product name reappearing). Runtime/visual
/// behavior — dialog single-instance, side-effect-free construction, rendered state text — is
/// covered by <see cref="AboutSelfTest"/> under PLP_SELFTEST, exactly like every other 2.0 page.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class AboutTests : IDisposable
{
    public AboutTests() => SafeLinkLauncher.ResetForTests();

    public void Dispose()
    {
        SafeLinkLauncher.ResetForTests();
        ClipboardService.ResetForTests();
        SupportSummary.ResetForTests();
    }

    // ---------------------------------------------------------------- SafeLinkLauncher: allowlist

    [Theory]
    [InlineData(SafeLinkDestination.Email, "mailto:mohammedgbr5@gmail.com")]
    [InlineData(SafeLinkDestination.Telegram, "https://t.me/m_s_73kg")]
    [InlineData(SafeLinkDestination.Instagram, "https://www.instagram.com/m_s_73kg/")]
    [InlineData(SafeLinkDestination.TikTok, "https://www.tiktok.com/@mohammed_sattar")]
    public void GetDestination_MatchesExactApprovedAddress(SafeLinkDestination destination, string expectedPrefix)
        => Assert.StartsWith(expectedPrefix, SafeLinkLauncher.GetDestination(destination).AbsoluteUri,
            StringComparison.Ordinal);

    [Fact]
    public void Allowlist_ContainsExactlyTheFourApprovedDestinations()
    {
        var all = Enum.GetValues<SafeLinkDestination>();
        Assert.Equal(4, all.Length);
        foreach (var d in all)
            Assert.NotNull(SafeLinkLauncher.GetDestination(d)); // throws (KeyNotFound) if any enum value lacks an entry
    }

    [Fact]
    public void EmailDestination_UsesMailtoSchemeAndFixedSafeSubject()
    {
        var uri = SafeLinkLauncher.GetDestination(SafeLinkDestination.Email);
        Assert.Equal("mailto", uri.Scheme);
        Assert.StartsWith("mohammedgbr5@gmail.com", uri.OriginalString["mailto:".Length..], StringComparison.Ordinal);
        // no local information (path, machine name, user) may ride in the subject
        Assert.DoesNotContain(Environment.MachineName, uri.OriginalString, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- SafeLinkLauncher: scheme validation

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("mailto:someone@example.com")]
    public void IsAllowedAbsoluteUri_AcceptsOnlyHttpsAndMailto(string raw)
        => Assert.True(SafeLinkLauncher.IsAllowedAbsoluteUri(raw, out var uri) && uri != null);

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("http://example.com")] // http (unencrypted) is deliberately not on the allowlist
    [InlineData("ftp://example.com")]
    [InlineData("some/relative/path")]
    [InlineData(@"\\server\share\file.exe")] // UNC — parses as file: once made absolute
    [InlineData("")]
    [InlineData("   ")]
    public void IsAllowedAbsoluteUri_RejectsDisallowedSchemesAndShapes(string raw)
        => Assert.False(SafeLinkLauncher.IsAllowedAbsoluteUri(raw, out var uri) || uri != null);

    [Fact] // mutation-sensitive: if a scheme guard is ever loosened to allow file:, this fails
    public void IsAllowedAbsoluteUri_RejectsFileScheme()
        => Assert.False(SafeLinkLauncher.IsAllowedAbsoluteUri("file:///secrets.txt", out _));

    // ---------------------------------------------------------------- SafeLinkLauncher: closed API surface

    [Fact] // mutation-sensitive: if TryOpen ever grows a string-accepting overload, this fails
    public void TryOpen_AcceptsOnlyTheClosedEnum_NeverACallerSuppliedString()
    {
        var methods = typeof(SafeLinkLauncher).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "TryOpen" or "Open").ToList();
        Assert.NotEmpty(methods);
        foreach (var m in methods)
        {
            var p = m.GetParameters();
            Assert.Single(p);
            Assert.Equal(typeof(SafeLinkDestination), p[0].ParameterType);
        }
    }

    // ---------------------------------------------------------------- SafeLinkLauncher: ProcessStartInfo boundary (repair §3)

    [Fact] // behavioral proof that the REAL production path (allowlist → scheme check → PSI
           // construction) ran, not a bypass of it — a source-text search cannot catch a
           // cmd.exe/PowerShell mutation, but asserting on the exact PSI the seam receives can.
    public void TryOpen_BuildsTheExactTruthfulProcessStartInfo()
    {
        ProcessStartInfo? seen = null;
        SafeLinkLauncher.ProcessStartOverrideForTests = psi => { seen = psi; return true; };

        var outcome = SafeLinkLauncher.TryOpen(SafeLinkDestination.Telegram);

        Assert.Equal(SafeLinkOutcome.Launched, outcome);
        Assert.NotNull(seen);
        Assert.Equal(SafeLinkLauncher.GetDestination(SafeLinkDestination.Telegram).AbsoluteUri, seen!.FileName);
        Assert.True(seen.UseShellExecute);
        Assert.Equal("", seen.Arguments);
        Assert.Empty(seen.ArgumentList);
        Assert.True(string.IsNullOrEmpty(seen.Verb));
    }

    [Theory] // every destination must independently produce the same truthful shape
    [InlineData(SafeLinkDestination.Email)]
    [InlineData(SafeLinkDestination.Telegram)]
    [InlineData(SafeLinkDestination.Instagram)]
    [InlineData(SafeLinkDestination.TikTok)]
    public void TryOpen_EveryDestination_ProducesFileNameEqualToItsOwnAllowlistedUri(SafeLinkDestination destination)
    {
        ProcessStartInfo? seen = null;
        SafeLinkLauncher.ProcessStartOverrideForTests = psi => { seen = psi; return true; };

        SafeLinkLauncher.TryOpen(destination);

        Assert.Equal(SafeLinkLauncher.GetDestination(destination).AbsoluteUri, seen!.FileName);
    }

    [Fact]
    public void TryOpen_ReturnsFailed_WhenTheProcessBoundaryReportsFailure()
    {
        SafeLinkLauncher.ProcessStartOverrideForTests = _ => false;
        Assert.Equal(SafeLinkOutcome.Failed, SafeLinkLauncher.TryOpen(SafeLinkDestination.Instagram));
    }

    [Fact] // the real browser/mail client is never invoked from the test process
    public void TryOpen_NeverLaunchesARealProcess_WhenASeamIsInstalled()
    {
        var calls = 0;
        SafeLinkLauncher.ProcessStartOverrideForTests = _ => { calls++; return true; };
        SafeLinkLauncher.TryOpen(SafeLinkDestination.Email);
        SafeLinkLauncher.TryOpen(SafeLinkDestination.Email);
        Assert.Equal(2, calls); // two SEQUENTIAL (non-overlapping) calls both launch normally
    }

    // ---------------------------------------------------------------- SafeLinkLauncher: Busy truthfulness (repair §8)

    [Fact] // mutation-sensitive: a suppressed duplicate reported as success/Launched fails this
    public void TryOpen_ReentrantCallWhileInFlight_ReportsBusy_NeverLaunched()
    {
        SafeLinkOutcome? reentrantOutcome = null;
        var startCalls = 0;
        SafeLinkLauncher.ProcessStartOverrideForTests = _ =>
        {
            startCalls++;
            // Called WHILE the outer TryOpen is still inside its critical section.
            reentrantOutcome = SafeLinkLauncher.TryOpen(SafeLinkDestination.Telegram);
            return true;
        };

        var outerOutcome = SafeLinkLauncher.TryOpen(SafeLinkDestination.Email);

        Assert.Equal(SafeLinkOutcome.Launched, outerOutcome);
        Assert.Equal(SafeLinkOutcome.Busy, reentrantOutcome);
        Assert.Equal(1, startCalls); // the busy call never reached the process boundary
    }

    [Fact]
    public void TryOpen_AfterABusyRejection_CanStillLaunchOnTheNextCall()
    {
        SafeLinkLauncher.ProcessStartOverrideForTests = _ => true;
        SafeLinkLauncher.TryOpen(SafeLinkDestination.Email); // completes and clears the in-flight flag
        Assert.Equal(SafeLinkOutcome.Launched, SafeLinkLauncher.TryOpen(SafeLinkDestination.Email));
    }

    // ---------------------------------------------------------------- one approved Process.Start boundary

    [Fact] // mutation-sensitive: a second Process.Start added to this file would fail this guard.
           // Whether that one call is actually safe (UseShellExecute, no Verb, no shell metachar
           // injection) is NOT proven by source text — a comment could satisfy any such string
           // search without the real ProcessStartInfo being safe — so that proof is left entirely
           // to the behavioral PSI tests above (TryOpen_BuildsTheExactTruthfulProcessStartInfo and
           // TryOpen_EveryDestination_ProducesFileNameEqualToItsOwnAllowlistedUri), which assert on
           // the actual ProcessStartInfo instance the seam receives.
    public void SafeLinkLauncherSource_ContainsExactlyOneProcessStartCall()
    {
        var source = TestPaths.ReadRepoFile("Services", "SafeLinkLauncher.cs");
        // Only real CODE lines count — doc-comment lines (e.g. a <see cref="Process.Start(...)"/>
        // reference) are not a second call site.
        var codeOnly = string.Join('\n', source.Split('\n').Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
        var count = System.Text.RegularExpressions.Regex.Matches(codeOnly, @"Process\.Start\(").Count;
        Assert.Equal(1, count);
    }

    // ---------------------------------------------------------------- EngineManifest: truthful format-version label (repair §2)

    [Fact]
    public void FormatVersionLabel_NeverClaimsToBeAScrcpyOrPackageOrReleaseVersion()
    {
        var label = EngineManifest.FormatVersionLabelArabic;
        Assert.DoesNotContain("scrcpy", label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("حزمة", label, StringComparison.Ordinal);
        Assert.DoesNotContain("إصدار المحرّك المضمّن", label, StringComparison.Ordinal);
        Assert.DoesNotContain("engine release", label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("package version", label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // About and Settings must use the exact SAME compiled-in label — never a second, drifted copy
    public void AboutAndSettingsXaml_BothBindToTheSharedManifestFormatLabelConstant()
    {
        var aboutXaml = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml");
        var settingsXaml = TestPaths.ReadRepoFile("Views", "SettingsView.xaml");
        const string binding = "{x:Static services:EngineManifest.FormatVersionLabelArabic}";
        Assert.Contains(binding, aboutXaml, StringComparison.Ordinal);
        Assert.Contains(binding, settingsXaml, StringComparison.Ordinal);
    }

    [Fact] // mutation-sensitive: About "escaping" the shared label back to a literal false claim fails this
    public void AboutWindowXaml_NeverPairsEngineVersionWithAFalseScrcpyOrPackageClaim()
    {
        var xaml = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml");
        Assert.DoesNotContain("إصدار حزمة المحرّك", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("إصدار محرّك scrcpy", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("إصدار المحرّك المضمّن", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationVersion_StillComesFromAppVersionInfo_NotFromEngineManifest()
    {
        // The two must remain independent concepts: a manifest-format bump must never look like
        // an app release, and vice-versa.
        Assert.NotEqual(AppVersionInfo.ProductVersion, "v" + EngineManifest.Version);
    }

    // ---------------------------------------------------------------- SupportSummary: exact allowlist contract (repair §7)

    [Fact]
    public void Build_ContainsOnlyTheDocumentedAllowlistedFields()
    {
        SupportSummary.IntegrityProvider = () => null;
        SupportSummary.MissingEngineFilesProvider = () => Array.Empty<string>();
        SupportSummary.OsDescriptionProvider = () => "Windows 10.0.26200";

        var text = SupportSummary.Build();

        Assert.Contains(AppVersionInfo.ProductVersion, text);
        Assert.Contains("v" + EngineManifest.Version, text);
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), text);
        Assert.Contains("متاحة", text);
        Assert.Contains("لم يتم التحقق بعد", text);
        Assert.Contains("Windows 10.0.26200", text);
    }

    [Fact] // the exact output CONTRACT: fixed header + exactly one line per FieldLabels entry, in
           // order, nothing extra — an appended harmless-looking line must fail this test
    public void Build_ProducesExactlyTheDocumentedLinesInTheDocumentedOrder_NoExtraLine()
    {
        SupportSummary.IntegrityProvider = () => null;
        SupportSummary.MissingEngineFilesProvider = () => Array.Empty<string>();
        SupportSummary.OsDescriptionProvider = () => "Windows 10.0.26200";

        var text = SupportSummary.Build();
        var lines = text.Split("\r\n");

        Assert.Equal(1 + SupportSummary.FieldLabels.Count, lines.Length);
        Assert.Equal(SupportSummary.Header, lines[0]);
        for (var i = 0; i < SupportSummary.FieldLabels.Count; i++)
            Assert.StartsWith(SupportSummary.FieldLabels[i] + ": ", lines[i + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void FieldLabels_ContainsNoDuplicateLabel()
        => Assert.Equal(SupportSummary.FieldLabels.Count, SupportSummary.FieldLabels.Distinct().Count());

    [Fact] // mutation-sensitive: pins the engine-version FIELD LABEL against the shared truthful
           // constant independently — a self-referential test against SupportSummary.FieldLabels
           // alone would not catch that constant being swapped for a hardcoded false claim
    public void FieldLabels_EngineVersionEntry_IsExactlyTheSharedTruthfulManifestFormatConstant()
        => Assert.Contains(EngineManifest.FormatVersionLabelArabic, SupportSummary.FieldLabels);

    [Fact] // same independent pin, exercised through the actual built text
    public void Build_EngineVersionLine_UsesTheSharedTruthfulManifestFormatConstant()
    {
        SupportSummary.IntegrityProvider = () => null;
        SupportSummary.MissingEngineFilesProvider = () => Array.Empty<string>();
        SupportSummary.OsDescriptionProvider = () => "";

        var text = SupportSummary.Build();
        Assert.Contains($"{EngineManifest.FormatVersionLabelArabic}: v{EngineManifest.Version}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NeverContainsAUserProfilePathSerialOrIpAddress()
    {
        SupportSummary.IntegrityProvider = () => null;
        SupportSummary.MissingEngineFilesProvider = () => new[] { "PhoneLink.Engine.exe" };
        SupportSummary.OsDescriptionProvider = () => "Microsoft Windows 10.0.26200";

        var text = SupportSummary.Build();

        Assert.DoesNotContain(@"C:\Users\", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", text);
        Assert.DoesNotContain("adb", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scrcpy", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_ProviderFailures_FallBackSafelyRatherThanThrowing()
    {
        SupportSummary.IntegrityProvider = () => throw new InvalidOperationException("boom");
        SupportSummary.MissingEngineFilesProvider = () => throw new InvalidOperationException("boom");
        SupportSummary.OsDescriptionProvider = () => throw new InvalidOperationException("boom");

        var text = SupportSummary.Build(); // must not throw

        Assert.Contains(AppVersionInfo.ProductVersion, text);
        Assert.Contains("لم يتم التحقق بعد", text);
        // even a provider failure must still produce the exact documented line count (fallback "-"
        // fills the OS line rather than the line disappearing).
        Assert.Equal(1 + SupportSummary.FieldLabels.Count, text.Split("\r\n").Length);
    }

    [Fact]
    public void Build_IsBoundedAndUsesCrLfLineEndingsOnly()
    {
        SupportSummary.IntegrityProvider = () => null;
        SupportSummary.MissingEngineFilesProvider = () => Array.Empty<string>();
        SupportSummary.OsDescriptionProvider = () => "";

        var text = SupportSummary.Build();

        Assert.True(text.Length <= SupportSummary.MaxLength);
        Assert.DoesNotContain("\r\r\n", text, StringComparison.Ordinal);
        // every bare '\n' must be immediately preceded by '\r'
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                Assert.True(i > 0 && text[i - 1] == '\r', "found a bare LF not preceded by CR");
    }

    [Theory]
    [InlineData(0, "متاحة")]
    [InlineData(1, "غير مكتملة")]
    public void ToolsStateLabel_IsTruthful(int missingCount, string expectedSubstring)
    {
        var missing = Enumerable.Range(0, missingCount).Select(i => "file" + i).ToArray();
        Assert.Contains(expectedSubstring, SupportSummary.ToolsStateLabel(missing));
    }

    [Fact]
    public void IntegrityStateLabel_DistinguishesAllThreeStates()
    {
        Assert.Contains("لم يتم التحقق", SupportSummary.IntegrityStateLabel(null));

        var valid = new EngineIntegrityResult(EngineIntegrityStatus.Valid, null, null, 1, TimeSpan.Zero);
        Assert.Contains("تم التحقق", SupportSummary.IntegrityStateLabel(valid));

        var failed = new EngineIntegrityResult(EngineIntegrityStatus.HashMismatch, "engine/core.dat", null, 1, TimeSpan.Zero);
        Assert.Contains("فشل", SupportSummary.IntegrityStateLabel(failed));
    }

    // ---------------------------------------------------------------- ClipboardService seam

    [Fact]
    public void ClipboardService_TrySetText_ReportsTheSeamResultTruthfully()
    {
        ClipboardService.SetTextOverrideForTests = _ => true;
        Assert.True(ClipboardService.TrySetText("hello"));

        ClipboardService.SetTextOverrideForTests = _ => false;
        Assert.False(ClipboardService.TrySetText("hello"));
    }

    [Fact]
    public void ClipboardService_TrySetText_RecoversFromAThrowingSeam()
    {
        ClipboardService.SetTextOverrideForTests = _ => throw new InvalidOperationException("boom");
        Assert.False(ClipboardService.TrySetText("hello")); // must never throw through the UI
    }

    // ---------------------------------------------------------------- identity / branding guards

    /// <summary>Strips the internal x:Class/xmlns declarations (the "ScrcpyDeck" .NET namespace,
    /// unrelated to user-visible branding — see Batch 4F §9) so only user-visible text remains.</summary>
    private static string UserVisibleXaml(string xaml)
        => System.Text.RegularExpressions.Regex.Replace(xaml, @"(x:Class|xmlns(:\w+)?)=""[^""]*""", "");

    [Fact]
    public void AboutWindowXaml_ContainsNoOldPublicProductName()
    {
        var xaml = UserVisibleXaml(TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml"));
        Assert.DoesNotContain("ScrcpyDeck", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutWindowSource_ShowsTheApprovedDeveloperAndCopyrightStrings()
    {
        var xaml = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml");
        Assert.Contains("ENG. Mohammed Sattar Jabr", xaml, StringComparison.Ordinal);

        var code = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml.cs");
        Assert.Contains("Mohammed Sattar", code, StringComparison.Ordinal);
    }

    [Fact] // [REGRESSION] public-release licensing audit: the identity card credits the developer,
           // so it must also say the engine components belong to their owners (no implied ownership)
    public void AboutWindowXaml_IdentityCardAttributesThirdPartyComponents()
    {
        var xaml = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml");
        var start = xaml.IndexOf("x:Name=\"ThirdPartyAttributionText\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the About identity card must keep its third-party attribution line");

        var tag = xaml[start..xaml.IndexOf("/>", start, StringComparison.Ordinal)];
        Assert.Contains("مفتوحة المصدر", tag, StringComparison.Ordinal);
        // It names the button that actually opens the notices, by its real label.
        Assert.Contains($"«{NoticesButtonLabel(xaml)}»", tag, StringComparison.Ordinal);

        // it must sit inside the identity card, before the developer credit it qualifies
        Assert.True(start < xaml.IndexOf("ENG. Mohammed Sattar Jabr", StringComparison.Ordinal),
            "the attribution must appear on the identity card, ahead of the developer credit");
    }

    [Fact] // [REGRESSION] independent review: the policy text sends users to the notices button
           // by name, so that name must be the button's real label
    public void PolicyText_NamesTheNoticesButtonByItsRealLabel()
    {
        var label = NoticesButtonLabel(TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml"));
        Assert.Contains($"«{label}»", PolicyText.Privacy, StringComparison.Ordinal);
        Assert.DoesNotContain("«إشعارات الطرف الثالث»", PolicyText.Privacy, StringComparison.Ordinal);
    }

    private static string NoticesButtonLabel(string aboutXaml)
    {
        var m = System.Text.RegularExpressions.Regex.Match(aboutXaml, "x:Name=\"NoticesBtn\" Content=\"([^\"]+)\"");
        Assert.True(m.Success, "the About window must keep its NoticesBtn with a Content label");
        return m.Groups[1].Value;
    }

    [Fact]
    public void NoticesWindowXaml_ContainsNoOldPublicProductNameAndNoInventedLicenseClaim()
    {
        var xaml = UserVisibleXaml(TestPaths.ReadRepoFile("Windows", "NoticesWindow.xaml"));
        Assert.DoesNotContain("ScrcpyDeck", xaml, StringComparison.Ordinal);

        var code = TestPaths.ReadRepoFile("Windows", "NoticesWindow.xaml.cs");
        // the missing-notice path must exist and must not silently claim completeness
        Assert.Contains("MissingMessage", code, StringComparison.Ordinal);
    }

    [Fact] // the notices window must read the REAL repository file through its production default
    public void Source_NoticesWindowProductionDefault_ReadsTheActualThirdPartyNoticesFile()
    {
        var real = TestPaths.ReadRepoFile("THIRD-PARTY-NOTICES.txt");
        Assert.Contains("scrcpy", real, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Genymobile", real, StringComparison.Ordinal);

        var code = TestPaths.ReadRepoFile("Windows", "NoticesWindow.xaml.cs");
        Assert.Contains("THIRD-PARTY-NOTICES.txt", code, StringComparison.Ordinal);
        // the production reader must be the file-system default, not a fixture string
        Assert.Contains("ReadRealNotices", code, StringComparison.Ordinal);
    }

    [Fact] // mutation-sensitive: reintroducing a ContentProviderForTests fixture override in the
           // notices-dialog snapshot scenario would silently go back to showing fabricated text
    public void AboutSnapshotToolModeSource_NoticesDialogScenario_UsesTheRealProductionPath()
    {
        var code = TestPaths.ReadRepoFile("Services", "AboutSnapshotToolMode.cs");
        var start = code.IndexOf("if (scenario.OpenNoticesDialog)", StringComparison.Ordinal);
        var end = code.IndexOf("Settle(captureTarget);", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var region = code[start..end];
        Assert.DoesNotContain("ContentProviderForTests =", region, StringComparison.Ordinal);
    }

    [Fact] // REL-03 convention: no window may hardcode a second version literal
    public void AboutWindowXaml_ContainsNoHardcodedVersionLiteral()
    {
        var xaml = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml");
        Assert.DoesNotMatch(@"v\d+\.\d+", xaml);
        Assert.DoesNotMatch(@"الإصدار\s+\d", xaml);
    }

    // ---------------------------------------------------------------- layout: no clipped legal button (repair §9)

    [Fact] // mutation-sensitive: reverting the notices/policy row to a horizontal side-by-side
           // StackPanel (the clipping layout) fails this guard
    public void AboutWindowXaml_PolicyAndNoticesButtons_AreNotForcedSideBySideHorizontally()
    {
        var xaml = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml");
        var policyIdx = xaml.IndexOf("x:Name=\"PolicyBtn\"", StringComparison.Ordinal);
        var noticesIdx = xaml.IndexOf("x:Name=\"NoticesBtn\"", StringComparison.Ordinal);
        Assert.True(policyIdx > 0 && noticesIdx > policyIdx);

        // the enclosing panel's own opening tag (which precedes PolicyBtn) is the one that
        // actually carries Orientation — checking only the text BETWEEN the two buttons would
        // miss "<StackPanel Orientation=\"Horizontal\">" written before PolicyBtn's own tag.
        var enclosingPanelStart = xaml.LastIndexOf("<StackPanel", policyIdx, StringComparison.Ordinal);
        Assert.True(enclosingPanelStart >= 0);
        var enclosingPanelOpenTagEnd = xaml.IndexOf('>', enclosingPanelStart);
        var enclosingPanelOpenTag = xaml[enclosingPanelStart..enclosingPanelOpenTagEnd];

        Assert.DoesNotContain("Orientation=\"Horizontal\"", enclosingPanelOpenTag, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- About snapshot: full-
    // content capture (Batch 5A repair — the legal buttons stacking vertically in repair batch
    // §9 grew the "privacy & legal" card enough that the fixed 1500px capture height used to
    // clip the advanced-diagnostics action and the footer). The genuine behavioral proof is the
    // actual PLP_TOOL_MODE=about-snapshot capture run (verified separately, images inspected by
    // hand); what's safely checkable here is that the height is MEASURED, not guessed again, and
    // that a bounds proof — not just "the math should work" — gates a successful capture.

    [Fact] // [REGRESSION] independent review, B2: growing the WINDOW to fit content was proven
           // to hit a hard OS ceiling (Windows silently caps a window's actual height at roughly
           // the primary screen's height, even off-screen) — the capture must render
           // BodyScroll's content directly instead, which has no such ceiling
    public void AboutSnapshotToolModeSource_RendersContentDirectlyInsteadOfGrowingTheWindow()
    {
        var code = TestPaths.ReadRepoFile("Services", "AboutSnapshotToolMode.cs");

        Assert.Contains("TryComposeFullContentBitmap(", code, StringComparison.Ordinal);
        Assert.Contains("contentBitmap.Render(content);", code, StringComparison.Ordinal);
        // the approach this replaced (grow window.Height until BodyScroll needs no scrolling)
        // must not reappear as actual code — only as historical doc-comment context
        Assert.DoesNotContain("GrowToFitContent(window", code, StringComparison.Ordinal);
        Assert.DoesNotContain("window.Height = ", code, StringComparison.Ordinal);
    }

    [Fact] // [REGRESSION] a successful capture must be gated on the advanced-diagnostics action
           // AND the footer both actually lying inside their respective rendered bounds — not
           // merely trusted from the composition arithmetic
    public void AboutSnapshotToolModeSource_GatesCaptureOnExportDiagnosticsBtnAndFooterBounds()
    {
        var code = TestPaths.ReadRepoFile("Services", "AboutSnapshotToolMode.cs");

        var composeBody = ExtractMethodBody(code, "private static bool TryComposeFullContentBitmap(");

        var footerCheckIdx = composeBody.IndexOf(
            "ControlIsWithinWindowForTests(window.FooterCloseBtn, window, width, window.ActualHeight)", StringComparison.Ordinal);
        var contentCheckIdx = composeBody.IndexOf(
            "ControlIsWithinWindowForTests(window.ExportDiagnosticsBtn, content, content.ActualWidth, contentHeight)", StringComparison.Ordinal);
        var bitmapIdx = composeBody.IndexOf("new RenderTargetBitmap(", StringComparison.Ordinal);

        Assert.True(footerCheckIdx > 0, "the footer bounds check must still exist");
        Assert.True(contentCheckIdx > footerCheckIdx, "the content bounds check must still exist");
        Assert.True(bitmapIdx > contentCheckIdx,
            "both bounds checks must run before any bitmap is captured, not after");
    }

    [Fact] // [REGRESSION] AboutWindow is FlowDirection="RightToLeft"; a bounds check that only
           // computes "topLeft + size = bottomRight" silently produces nonsense under WPF's RTL
           // mirror transform — proven in practice (independent review, B2 repair) when the
           // footer close button's transformed X position exceeded the window's own width
    public void AboutSnapshotToolModeSource_BoundsCheckIsMirrorSafe()
    {
        var code = TestPaths.ReadRepoFile("Services", "AboutSnapshotToolMode.cs");
        var body = ExtractMethodBody(code, "private static bool ControlIsWithinWindowForTests(");

        Assert.Contains("Math.Min(corner1.X, corner2.X)", body, StringComparison.Ordinal);
        Assert.Contains("Math.Max(corner1.X, corner2.X)", body, StringComparison.Ordinal);
    }

    [Fact] // [REGRESSION] rendering BodyScroll's content as an isolated root applies WPF's RTL
           // mirror a second time (confirmed empirically: without countering it, every card's
           // text rendered as a horizontal mirror image) — the counter-mirror must stay in place
    public void AboutSnapshotToolModeSource_CountersTheDoubleRtlMirrorOnContentRender()
    {
        var code = TestPaths.ReadRepoFile("Services", "AboutSnapshotToolMode.cs");
        Assert.Contains("new ScaleTransform(-1, 1, width / 2, 0)", code, StringComparison.Ordinal);
    }

    [Fact] // the named ScrollViewer and footer button the composition depends on must still exist
    public void AboutWindowXaml_HasTheNamedBodyScrollViewerAndFooterButton()
    {
        var xaml = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml");
        Assert.Contains("x:Name=\"BodyScroll\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FooterCloseBtn\"", xaml, StringComparison.Ordinal);
    }

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

    // ---------------------------------------------------------------- RTL spacer-column layout
    // (Batch 5A supplementary guard — visual regression protection only, not full DPI proof; the
    // dashboard/settings/about snapshot tool modes are what actually render this at real DPI)

    [Fact] // [REGRESSION] the engine-status Grid must keep a dedicated positive-width spacer
           // column between the two status groups, not fall back to an outer-edge margin
    public void AboutWindowXaml_EngineStatusGrid_HasADedicatedSpacerColumn()
    {
        var xaml = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml");
        var grid = XamlSpacerColumnGuard.ExtractStatusGrid(xaml, "EngineVersionLabelText");
        XamlSpacerColumnGuard.AssertHasDedicatedSpacerColumn(grid);
    }

    // ---------------------------------------------------------------- no network / no timers

    [Fact]
    public void SupportSummarySource_NeverPerformsANetworkRequest()
    {
        var source = TestPaths.ReadRepoFile("Services", "SupportSummary.cs");
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WebRequest", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutWindowSource_NeverStartsATimerOrPollingLoop()
    {
        var code = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml.cs");
        Assert.DoesNotContain("DispatcherTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Timers.Timer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", code, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- About side-effect source guards (supplementary)

    [Fact]
    public void AboutWindowSource_NeverReferencesDeviceWatcherOrAdbServiceOrScrcpyService()
    {
        var code = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml.cs");
        Assert.DoesNotContain("DeviceWatcher", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AdbService", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrcpyService", code, StringComparison.Ordinal);
        Assert.DoesNotContain("MirrorSession", code, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutWindowSource_NeverCallsSettingsStoreOrAppSettingsSetters()
    {
        var code = TestPaths.ReadRepoFile("Windows", "AboutWindow.xaml.cs");
        Assert.DoesNotContain("SettingsStore.Save", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySaveDefaultTransferDestination", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySetOnboardingSeen", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ConsentAccepted =", code, StringComparison.Ordinal); // getter-only use
    }
}

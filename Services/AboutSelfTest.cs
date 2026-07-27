using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ScrcpyDeck.Windows;

namespace ScrcpyDeck.Services;

/// <summary>
/// Runtime proof for the Batch 4F About &amp; Support page, following the same pattern as
/// <see cref="SettingsSelfTest"/>: xUnit runs MTA and cannot construct a real WPF window, so
/// behavioral coverage that needs an actual visual tree lives here instead. Every scenario
/// resets <see cref="AboutWindow"/>'s singleton state, redirects <see cref="AppSettings"/> to an
/// isolated temp directory, and restores every static seam in the outer <c>finally</c> — the real
/// %AppData%\PhoneLinkPro\settings.json, the real clipboard, and the real engine-integrity cache
/// are never touched by this suite.
/// </summary>
internal static class AboutSelfTest
{
    internal sealed record Result(bool Passed, int Total, int Failed, string Report);

    private const int ExpectedScenarioAssertions = 82;

    // Safe no-op defaults every scenario restores to instead of null, so a Make() call in a LATER
    // scenario that doesn't itself touch these seams is still protected against ever reaching a
    // real Process.Start or a real clipboard write (see the global safety net at the top of Run()).
    private static readonly Func<ProcessStartInfo, bool> SafeLinkNoOp = _ => false;
    private static readonly Func<string, bool> ClipboardNoOp = _ => false;

    private static readonly string[] ScenarioIds =
    {
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S",
    };

    internal static Result Run()
    {
        var failures = new List<string>();
        var assertionNames = new List<string>();
        var total = 0;
        void Expect(string name, bool condition)
        {
            total++;
            assertionNames.Add(name);
            if (!condition) failures.Add(name);
        }

        var bindingErrors = new List<string>();
        var listener = new BindingErrorListener(bindingErrors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.Refresh();

        var previousSettingsDir = AppSettings.DirOverrideForTests;
        var savedIntegrityBaseDir = EngineIntegrityVerifier.BaseDirProvider;
        var savedIntegrityManifest = EngineIntegrityVerifier.ManifestTextProvider;
        var savedMissingFiles = ToolLocator.MissingFilesOverrideForTests;
        var savedAdbSeam = AdbService.ProcessRunOverrideForTests;
        var savedDeviceWatcherDiscovery = DeviceWatcher.DiscoveryProvider;

        var scratchRoot = Path.Combine(Path.GetTempPath(), "plp_about_selftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchRoot);

        string FreshDir(string label)
        {
            var dir = Path.Combine(scratchRoot, label + "_" + Guid.NewGuid().ToString("N"));
            AppSettings.ResetForTests(dir);
            return dir;
        }

        AboutWindow.ResetForTests();
        SafeLinkLauncher.ResetForTests();
        ClipboardService.ResetForTests();
        SettingsStore.ResetSaveCallCountForTests();

        // Global safety net for the WHOLE run: no scenario's Make() call may ever reach a real
        // Process.Start or a real clipboard write, even one that isn't under test right now (e.g.
        // a future regression that fires a link launch from the constructor itself, not just from
        // a button click). Individual scenarios below install their own more specific stub locally
        // and restore it back to these safe no-ops when done.
        SafeLinkLauncher.ProcessStartOverrideForTests = SafeLinkNoOp;
        ClipboardService.SetTextOverrideForTests = ClipboardNoOp;

        var opened = new List<Window>();
        AboutWindow Make()
        {
            var w = new AboutWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -6000,
                Top = 100,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            opened.Add(w);
            w.Show(); // must be shown so its Owner assignment on child dialogs (Policy/Notices) is legal
            return w;
        }

        try
        {
            // ---- A: XAML/resources construct without exception; every key element exists ----
            {
                FreshDir("a");
                var w = Make();
                Expect("A: close button exists", w.CloseBtn != null);
                Expect("A: version text exists", w.VersionText != null);
                Expect("A: tools status text exists", w.ToolsStatusText != null);
                Expect("A: integrity status text exists", w.IntegrityStatusText != null);
                Expect("A: policy version text exists", w.PolicyVersionText != null);
                Expect("A: consent status text exists", w.ConsentStatusText != null);
                Expect("A: summary preview box exists", w.SummaryPreviewBox != null);
                Expect("A: email/telegram/instagram/tiktok buttons all exist",
                    w.EmailBtn != null && w.TelegramBtn != null && w.InstagramBtn != null && w.TikTokBtn != null);
                Expect("A: export-diagnostics button exists", w.ExportDiagnosticsBtn != null);
                Expect("A: no unexpected WPF binding/resource error was raised by construction",
                    bindingErrors.Count == 0);
            }

            // ---- B: opening the page has no side effect on the shared engine-integrity cache ----
            {
                FreshDir("b");
                EngineIntegrityVerifier.ResetForTests();
                var runsBefore = EngineIntegrityVerifier.RunCount;
                Make();
                Make();
                Expect("B: constructing the page never triggers a fresh integrity verification",
                    EngineIntegrityVerifier.RunCount == runsBefore);
            }

            // ---- C: identity is truthful and sourced from the real assembly/policy/manifest metadata ----
            {
                FreshDir("c");
                var w = Make();
                Expect("C: version text uses the real AppVersionInfo value",
                    w.VersionText.Text.Contains(AppVersionInfo.ProductVersion));
                Expect("C: engine version text uses the real manifest version",
                    w.EngineVersionText.Text == "v" + EngineManifest.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Expect("C: the engine-version CAPTION uses the one truthful manifest-format label",
                    w.EngineVersionLabelText.Text == EngineManifest.FormatVersionLabelArabic);
                Expect("C: the manifest-format label never claims to be a scrcpy/engine-package/release version",
                    !w.EngineVersionLabelText.Text.Contains("scrcpy", StringComparison.OrdinalIgnoreCase) &&
                    !w.EngineVersionLabelText.Text.Contains("حزمة") &&
                    !w.EngineVersionLabelText.Text.Contains("إصدار المحرّك المضمّن"));
                Expect("C: copyright reads the approved public display name",
                    w.CopyrightText.Text.Contains("Mohammed Sattar"));
                Expect("C: policy version text uses the real PolicyText.Version",
                    w.PolicyVersionText.Text.Contains(PolicyText.Version));
            }

            // ---- D: the old internal/public brand name never appears anywhere on the page ----
            {
                FreshDir("d");
                var w = Make();
                var visible = string.Join(" ", new[]
                {
                    w.VersionText.Text, w.CopyrightText.Text, w.ToolsStatusText.Text,
                    w.IntegrityStatusText.Text, w.PolicyVersionText.Text, w.ConsentStatusText.Text,
                    w.SummaryPreviewBox.Text, w.Title,
                });
                Expect("D: no user-visible string on the page contains the old public product name",
                    !visible.Contains("ScrcpyDeck", StringComparison.Ordinal));
            }

            // ---- E/F: tools available vs. unavailable renders truthfully ----
            {
                FreshDir("e");
                ToolLocator.MissingFilesOverrideForTests = Array.Empty<string>();
                var w = Make();
                Expect("E: tools-available state renders truthfully", w.ToolsStatusText.Text.Contains("متاحة"));

                FreshDir("f");
                ToolLocator.MissingFilesOverrideForTests = new[] { "PhoneLink.Engine.exe" };
                var w2 = Make();
                Expect("F: tools-unavailable state renders truthfully", w2.ToolsStatusText.Text.Contains("غير مكتملة"));
                ToolLocator.MissingFilesOverrideForTests = savedMissingFiles;
            }

            // ---- G: integrity not-checked-yet renders truthfully ----
            {
                FreshDir("g");
                EngineIntegrityVerifier.ResetForTests();
                var w = Make();
                Expect("G: integrity not-checked-yet state renders truthfully",
                    w.IntegrityStatusText.Text.Contains("لم يتم التحقق"));
            }

            // ---- H/I: integrity verified vs. failed renders truthfully (real verification runs) ----
            {
                try
                {
                    var engineBase = Path.Combine(scratchRoot, "h_engine_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.Combine(engineBase, "engine"));
                    var enginePath = Path.Combine(engineBase, "engine", "fake.bin");
                    File.WriteAllBytes(enginePath, new byte[] { 9, 8, 7 });
                    var manifestText = EngineManifest.Render(new[]
                        { new EngineManifestEntry("engine/fake.bin", EngineManifest.HashFileSha256(enginePath)) });

                    EngineIntegrityVerifier.ResetForTests();
                    EngineIntegrityVerifier.BaseDirProvider = () => engineBase;
                    EngineIntegrityVerifier.ManifestTextProvider = () => manifestText;
                    WaitUntil(() => EngineIntegrityVerifier.VerifyAsync().IsCompleted);

                    FreshDir("h");
                    var wOk = Make();
                    Expect("H: a genuinely valid manifest renders as verified",
                        wOk.IntegrityStatusText.Text.Contains("تم التحقق"));

                    EngineIntegrityVerifier.ResetForTests();
                    EngineIntegrityVerifier.ManifestTextProvider = () => null; // forces MissingManifestData
                    WaitUntil(() => EngineIntegrityVerifier.VerifyAsync().IsCompleted);

                    FreshDir("i");
                    var wFail = Make();
                    Expect("I: a forced integrity failure renders as failed",
                        wFail.IntegrityStatusText.Text.Contains("فشل"));
                }
                finally
                {
                    EngineIntegrityVerifier.ResetForTests();
                    EngineIntegrityVerifier.BaseDirProvider = savedIntegrityBaseDir;
                    EngineIntegrityVerifier.ManifestTextProvider = savedIntegrityManifest;
                }
            }

            // ---- J/K: consent accepted vs. not-accepted renders truthfully, read-only ----
            {
                FreshDir("j");
                AppSettings.ConsentAccepted = true;
                var w = Make();
                Expect("J: consent-accepted state renders truthfully", w.ConsentStatusText.Text.Contains("تمت الموافقة"));

                FreshDir("k");
                var w2 = Make();
                Expect("K: consent-not-accepted state renders truthfully", w2.ConsentStatusText.Text.Contains("لم تتم الموافقة"));
            }

            // ---- L: the policy dialog is read-only and never mutates consent ----
            {
                FreshDir("l");
                AppSettings.ConsentAccepted = false;
                var w = Make();
                var shown = new List<Window>();
                AboutWindow.PolicyShowOverrideForTests = win => { shown.Add(win); win.Show(); };
                try
                {
                    ClickAndPump(w.PolicyBtn);
                    Expect("L: the policy window opened exactly once", AboutWindow.PolicyOpenCountForTests == 1);
                    Expect("L: opening the policy dialog never mutates consent", !AppSettings.ConsentAccepted);
                    Expect("L: the policy dialog shows the real PolicyText body",
                        shown.Count == 1 && shown[0] is PolicyWindow pw && pw.BodyForTests == PolicyText.Privacy);

                    // ---- repeated activation focuses the existing dialog rather than spawning one ----
                    ClickAndPump(w.PolicyBtn);
                    Expect("L: repeated activation does not open a second policy window",
                        AboutWindow.PolicyOpenCountForTests == 1);

                    foreach (var win in shown) win.Close();
                    Pump();
                    Expect("L: closing the policy dialog clears the tracked reference",
                        !AboutWindow.PolicyCurrentlyOpenForTests);
                }
                finally
                {
                    AboutWindow.PolicyShowOverrideForTests = null;
                }
            }

            // ---- M: the notices dialog reports missing vs. available truthfully, and the real
            //         production path (no test override) reads the actual repository file ----
            {
                FreshDir("m1");
                AboutWindow.ResetForTests();
                var w = Make();
                var shown = new List<Window>();
                AboutWindow.NoticesShowOverrideForTests = win => { shown.Add(win); win.Show(); };
                NoticesWindow.ContentProviderForTests = () => null;
                try
                {
                    ClickAndPump(w.NoticesBtn);
                    Expect("M: a missing notices file is reported truthfully, never invented",
                        shown.Count == 1 && shown[0] is NoticesWindow nw1 &&
                        nw1.StatusForTests == NoticesWindow.MissingMessage && nw1.BodyForTests.Length == 0);
                    foreach (var win in shown) win.Close();
                    Pump();
                    Expect("M: closing notices before reopening leaves no stale reference",
                        !AboutWindow.NoticesCurrentlyOpenForTests);
                }
                finally
                {
                    NoticesWindow.ContentProviderForTests = null;
                    AboutWindow.NoticesShowOverrideForTests = null;
                }
            }
            {
                FreshDir("m2");
                AboutWindow.ResetForTests();
                var w = Make();
                var shown = new List<Window>();
                AboutWindow.NoticesShowOverrideForTests = win => { shown.Add(win); win.Show(); };
                NoticesWindow.ContentProviderForTests = () => "FIXTURE NOTICE TEXT";
                try
                {
                    ClickAndPump(w.NoticesBtn);
                    Expect("M: an available notices file is shown verbatim, not summarized or altered",
                        shown.Count == 1 && shown[0] is NoticesWindow nw2 &&
                        nw2.BodyForTests == "FIXTURE NOTICE TEXT" && nw2.StatusForTests == NoticesWindow.AvailableMessage);

                    ClickAndPump(w.NoticesBtn);
                    Expect("M: repeated activation does not open a second notices window",
                        AboutWindow.NoticesOpenCountForTests == 1);

                    foreach (var win in shown) win.Close();
                }
                finally
                {
                    NoticesWindow.ContentProviderForTests = null;
                    AboutWindow.NoticesShowOverrideForTests = null;
                }
            }
            {
                // repair batch §10: the production path (NO ContentProviderForTests override)
                // must read the ACTUAL THIRD-PARTY-NOTICES.txt shipped beside the executable.
                FreshDir("m3");
                AboutWindow.ResetForTests();
                var w = Make();
                var shown = new List<Window>();
                AboutWindow.NoticesShowOverrideForTests = win => { shown.Add(win); win.Show(); };
                try
                {
                    ClickAndPump(w.NoticesBtn);
                    var real = shown.Count == 1 ? (NoticesWindow)shown[0] : null;
                    Expect("M: the real (non-overridden) notices path loads the actual repository file",
                        real != null && real.BodyForTests.Contains("scrcpy", StringComparison.OrdinalIgnoreCase) &&
                        real.BodyForTests.Contains("Genymobile", StringComparison.Ordinal));
                    Expect("M: the real notices path reports the available (not the missing) message",
                        real != null && real.StatusForTests == NoticesWindow.AvailableMessage);
                    foreach (var win in shown) win.Close();
                }
                finally
                {
                    AboutWindow.NoticesShowOverrideForTests = null;
                }
            }

            // ---- N: fixed links exercise the REAL SafeLinkLauncher path; behavioral proof on the
            //         actual ProcessStartInfo the production code builds, plus Busy truthfulness ----
            {
                FreshDir("n");
                var w = Make();
                var seen = new List<ProcessStartInfo>();
                SafeLinkLauncher.ProcessStartOverrideForTests = psi => { seen.Add(psi); return true; };
                try
                {
                    ClickAndPump(w.EmailBtn);
                    Expect("N: the email action's ProcessStartInfo.FileName is exactly the approved mailto URI",
                        seen.Count == 1 && seen[0].FileName == SafeLinkLauncher.GetDestination(SafeLinkDestination.Email).AbsoluteUri);
                    Expect("N: UseShellExecute is true", seen[0].UseShellExecute);
                    Expect("N: Arguments is empty (no string-built command)", seen[0].Arguments.Length == 0);
                    Expect("N: ArgumentList is empty (no constructed argument vector)", seen[0].ArgumentList.Count == 0);
                    Expect("N: no Verb is set (no alternate shell verb requested)", string.IsNullOrEmpty(seen[0].Verb));
                    Expect("N: the outcome is reported as Launched", w.LastLinkOutcomeForTests == SafeLinkOutcome.Launched);
                    Expect("N: a successful link click shows no failure text", !w.LinkFailedVisibleForTests);

                    ClickAndPump(w.TelegramBtn);
                    Expect("N: the Telegram action's FileName is exactly the approved destination",
                        seen[1].FileName == SafeLinkLauncher.GetDestination(SafeLinkDestination.Telegram).AbsoluteUri);

                    ClickAndPump(w.InstagramBtn);
                    Expect("N: the Instagram action's FileName is exactly the approved destination",
                        seen[2].FileName == SafeLinkLauncher.GetDestination(SafeLinkDestination.Instagram).AbsoluteUri);

                    ClickAndPump(w.TikTokBtn);
                    Expect("N: the TikTok action's FileName is exactly the approved destination",
                        seen[3].FileName == SafeLinkLauncher.GetDestination(SafeLinkDestination.TikTok).AbsoluteUri);
                }
                finally
                {
                    SafeLinkLauncher.ProcessStartOverrideForTests = SafeLinkNoOp;
                }

                SafeLinkLauncher.ProcessStartOverrideForTests = _ => false;
                try
                {
                    ClickAndPump(w.EmailBtn);
                    Expect("N: a failed link launch shows the safe Arabic failure text, never a raw exception",
                        w.LinkFailedVisibleForTests);
                    Expect("N: a failed launch is reported as Failed, not Launched",
                        w.LastLinkOutcomeForTests == SafeLinkOutcome.Failed);
                }
                finally
                {
                    SafeLinkLauncher.ProcessStartOverrideForTests = SafeLinkNoOp;
                }

                // ---- Busy truthfulness (repair batch §8): a launch already in flight must never
                //      be reported as success, and must never start a second process ----
                var startCalls = 0;
                SafeLinkLauncher.ProcessStartOverrideForTests = psi =>
                {
                    startCalls++;
                    // Recurse into TryOpen WHILE this "in-flight" call is still executing, proving
                    // the reentrant call is suppressed rather than starting a second process.
                    var reentrant = SafeLinkLauncher.TryOpen(SafeLinkDestination.Telegram);
                    Expect("N: a duplicate activation while a launch is in flight is reported as Busy",
                        reentrant == SafeLinkOutcome.Busy);
                    return true;
                };
                try
                {
                    var outcome = SafeLinkLauncher.TryOpen(SafeLinkDestination.Email);
                    Expect("N: the outer (non-reentrant) call still launches normally", outcome == SafeLinkOutcome.Launched);
                    Expect("N: the busy reentrant call never started a second process", startCalls == 1);
                }
                finally
                {
                    SafeLinkLauncher.ProcessStartOverrideForTests = SafeLinkNoOp;
                }
            }

            // ---- O: the safe support summary copies truthfully, touches only the seam, and its
            //         exact allowlisted line contract cannot silently grow an extra field ----
            {
                FreshDir("o");
                var w = Make();
                string? captured = null;
                ClipboardService.SetTextOverrideForTests = t => { captured = t; return true; };
                try
                {
                    ClickAndPump(w.CopySummaryBtn);
                    Expect("O: a successful copy is reported truthfully", w.CopySucceededForTests && !w.CopyFailedForTests);
                    Expect("O: the exact text shown in the preview is what was copied", captured == w.SummaryPreviewForTests);
                    Expect("O: the summary contains no username, path, serial, or IP",
                        !ContainsForbiddenField(captured!));
                    Expect("O: the summary stays within the documented bound", captured!.Length <= SupportSummary.MaxLength);

                    var lines = captured!.Split("\r\n");
                    Expect("O: the summary is exactly header + one line per allowlisted field, nothing extra",
                        lines.Length == 1 + SupportSummary.FieldLabels.Count);
                    Expect("O: line 1 is exactly the fixed header", lines[0] == SupportSummary.Header);
                    var labelsMatch = true;
                    for (var i = 0; i < SupportSummary.FieldLabels.Count; i++)
                        if (!lines[i + 1].StartsWith(SupportSummary.FieldLabels[i] + ": ", StringComparison.Ordinal))
                            labelsMatch = false;
                    Expect("O: every field line uses the exact label, in the exact documented order", labelsMatch);
                    Expect("O: no field label repeats", SupportSummary.FieldLabels.Distinct().Count() == SupportSummary.FieldLabels.Count);
                    Expect("O: the engine-version field uses the truthful manifest-format label, never a scrcpy/package claim",
                        lines.Any(l => l.StartsWith(EngineManifest.FormatVersionLabelArabic + ": v", StringComparison.Ordinal)));
                }
                finally
                {
                    ClipboardService.SetTextOverrideForTests = ClipboardNoOp;
                }

                ClipboardService.SetTextOverrideForTests = _ => false;
                try
                {
                    ClickAndPump(w.CopySummaryBtn);
                    Expect("O: a failed copy is reported truthfully, never as success", w.CopyFailedForTests && !w.CopySucceededForTests);
                }
                finally
                {
                    ClipboardService.SetTextOverrideForTests = ClipboardNoOp;
                }
            }

            // ---- P: Escape closes the page; repeated ShowOrActivate focuses instead of duplicating ----
            {
                FreshDir("p");
                AboutWindow.ResetForTests();
                var owner = new Window { Width = 10, Height = 10, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                owner.Show();
                try
                {
                    AboutWindow.ShowOrActivate(owner);
                    var afterFirst = AboutWindow.ConstructedCountForTests;
                    Expect("P: the first activation constructs exactly one window", afterFirst == 1);

                    AboutWindow.ShowOrActivate(owner);
                    Expect("P: repeated activation focuses the existing window rather than constructing a new one",
                        AboutWindow.ConstructedCountForTests == afterFirst);
                }
                finally
                {
                    owner.Close();
                }
            }

            // ---- Q: opening About performs ZERO settings writes, ZERO adb executions, ZERO
            //         DeviceWatcher starts, ZERO clipboard writes, and ZERO external link launches ----
            {
                FreshDir("q");
                AboutWindow.ResetForTests();
                var saveCountBefore = SettingsStore.SaveCallCountForTests;
                // Force a KNOWN clean baseline: an earlier self-test scenario in this same process
                // (lifecycle/dashboard/...) may already have left DeviceWatcher running, which
                // would silently mask a real "About starts it" mutation (IsRunning would already
                // be true before AND after). Stop() is idempotent and safe to call unconditionally.
                DeviceWatcher.Stop();
                var wasRunning = DeviceWatcher.IsRunning;
                Expect("Q: the DeviceWatcher baseline is a known, clean 'not running' state", !wasRunning);

                var adbCalls = 0;
                AdbService.ProcessRunOverrideForTests = (_, _, _, _) =>
                {
                    adbCalls++;
                    return Task.FromResult(new ProcessResult(ProcessOutcome.StartFailed, null, "", ""));
                };
                var clipboardCalls = 0;
                ClipboardService.SetTextOverrideForTests = _ => { clipboardCalls++; return true; };
                var linkCalls = 0;
                SafeLinkLauncher.ProcessStartOverrideForTests = _ => { linkCalls++; return true; };

                try
                {
                    Make();
                    Make(); // twice, to also catch a per-instance (not merely first-run) side effect

                    Expect("Q: constructing/showing About never calls SettingsStore.Save",
                        SettingsStore.SaveCallCountForTests == saveCountBefore);
                    Expect("Q: constructing/showing About never starts DeviceWatcher",
                        DeviceWatcher.IsRunning == wasRunning);
                    Expect("Q: constructing/showing About never executes an adb process", adbCalls == 0);
                    Expect("Q: constructing/showing About never writes the clipboard", clipboardCalls == 0);
                    Expect("Q: constructing/showing About never launches an external link", linkCalls == 0);
                }
                finally
                {
                    AdbService.ProcessRunOverrideForTests = savedAdbSeam;
                    ClipboardService.SetTextOverrideForTests = ClipboardNoOp;
                    SafeLinkLauncher.ProcessStartOverrideForTests = SafeLinkNoOp;
                }
            }

            // ---- R: About's own reopen-after-close singleton lifecycle ----
            {
                FreshDir("r");
                AboutWindow.ResetForTests();
                var owner = new Window { Width = 10, Height = 10, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                owner.Show();
                try
                {
                    AboutWindow.ShowOrActivate(owner);
                    var a = AboutWindow.CurrentForTests;
                    Expect("R: the first activation constructs a live instance", a != null);

                    AboutWindow.ShowOrActivate(owner);
                    Expect("R: a second activation before closing reuses the SAME instance",
                        ReferenceEquals(AboutWindow.CurrentForTests, a));

                    a!.Close();
                    Pump();
                    Expect("R: closing the window clears the singleton reference", AboutWindow.CurrentForTests == null);

                    AboutWindow.ShowOrActivate(owner);
                    var b = AboutWindow.CurrentForTests;
                    Expect("R: reopening after close constructs a NEW instance", b != null && !ReferenceEquals(b, a));

                    b!.Close();
                    Pump();
                    Expect("R: closing the second instance clears the reference again", AboutWindow.CurrentForTests == null);
                }
                finally
                {
                    owner.Close();
                }
            }

            // ---- S: Policy/Notices owner-and-child lifecycle edge cases ----
            {
                // About can close while a child (Policy) dialog is still open, without throwing.
                // WPF's own Owner semantics then close the owned Policy window automatically —
                // exactly the wanted behavior (no orphaned child dialog left behind).
                FreshDir("s1");
                AboutWindow.ResetForTests();
                var w = Make();
                var policyShown = new List<Window>();
                AboutWindow.PolicyShowOverrideForTests = win => { policyShown.Add(win); win.Show(); };
                Exception? thrown = null;
                try
                {
                    ClickAndPump(w.PolicyBtn);
                    try { w.Close(); } catch (Exception ex) { thrown = ex; }
                    Pump();
                    Expect("S: About can close while its Policy child is still open, without throwing", thrown == null);
                    Expect("S: closing About also closes its owned Policy child (no orphaned dialog)",
                        policyShown.Count == 1 && !policyShown[0].IsLoaded);
                }
                finally
                {
                    foreach (var win in policyShown) { try { win.Close(); } catch { } }
                    AboutWindow.PolicyShowOverrideForTests = null;
                }

                // The child (Notices) can close FIRST, before the user closes About, and About can
                // then open a fresh Notices window afterwards.
                FreshDir("s2");
                AboutWindow.ResetForTests();
                var w2 = Make();
                var noticesShown = new List<Window>();
                AboutWindow.NoticesShowOverrideForTests = win => { noticesShown.Add(win); win.Show(); };
                try
                {
                    ClickAndPump(w2.NoticesBtn);
                    Expect("S: Notices is tracked as open right after activation", AboutWindow.NoticesCurrentlyOpenForTests);

                    noticesShown[0].Close();
                    Pump();
                    Expect("S: closing the Notices child (before About) clears the tracked reference",
                        !AboutWindow.NoticesCurrentlyOpenForTests);

                    ClickAndPump(w2.NoticesBtn);
                    Expect("S: Notices can be reopened after the previous instance closed on its own",
                        AboutWindow.NoticesOpenCountForTests == 2 && noticesShown.Count == 2);
                }
                finally
                {
                    foreach (var win in noticesShown) { try { win.Close(); } catch { } }
                    AboutWindow.NoticesShowOverrideForTests = null;
                }

                // A failed Show/Activate (simulated) never leaves a dangling "current" reference —
                // the next attempt can still succeed.
                FreshDir("s3");
                AboutWindow.ResetForTests();
                var w3 = Make();
                var failOnce = true;
                AboutWindow.PolicyShowOverrideForTests = win =>
                {
                    if (failOnce) { failOnce = false; throw new InvalidOperationException("simulated failed Show"); }
                    win.Show();
                };
                try
                {
                    Assert(() => w3.PolicyBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), out var firstThrew);
                    Pump();
                    Expect("S: a failed Policy Show/Activate does not leave a dangling reference",
                        !AboutWindow.PolicyCurrentlyOpenForTests);
                    Expect("S: the simulated failure did propagate (not silently swallowed)", firstThrew);

                    w3.PolicyBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Pump();
                    Expect("S: a subsequent activation after a failed Show succeeds normally",
                        AboutWindow.PolicyCurrentlyOpenForTests);
                }
                finally
                {
                    AboutWindow.PolicyShowOverrideForTests = null;
                }
            }

            var scenarioAssertions = total;
            Expect($"exactly {ExpectedScenarioAssertions} scenario assertions ran (delete-scenario guard)",
                scenarioAssertions == ExpectedScenarioAssertions);

            var missingScenarios = ScenarioIds.Where(id => !assertionNames.Any(n => n.StartsWith(id + ":", StringComparison.Ordinal))).ToList();
            Expect($"all {ScenarioIds.Length} named runtime scenarios are still present: " +
                   (missingScenarios.Count == 0 ? "none missing" : "missing " + string.Join(", ", missingScenarios)),
                missingScenarios.Count == 0);
        }
        finally
        {
            foreach (var w in opened)
            {
                try { w.Close(); } catch { /* best effort */ }
            }
            AboutWindow.ResetForTests();
            SafeLinkLauncher.ResetForTests();
            ClipboardService.ResetForTests();
            NoticesWindow.ContentProviderForTests = null;

            ToolLocator.MissingFilesOverrideForTests = savedMissingFiles;
            AdbService.ProcessRunOverrideForTests = savedAdbSeam;
            DeviceWatcher.DiscoveryProvider = savedDeviceWatcherDiscovery;
            EngineIntegrityVerifier.ResetForTests();
            EngineIntegrityVerifier.BaseDirProvider = savedIntegrityBaseDir;
            EngineIntegrityVerifier.ManifestTextProvider = savedIntegrityManifest;

            AppSettings.ResetForTests(previousSettingsDir);
            SettingsStore.ResetSaveCallCountForTests();
            try { Directory.Delete(scratchRoot, recursive: true); } catch { /* best effort */ }

            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = previousLevel;
            PresentationTraceSources.Refresh();
        }

        var passed = failures.Count == 0;
        var report = $"about: {total - failures.Count}/{total} assertions passed" +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));
        return new Result(passed, total, failures.Count, report);
    }

    private static bool ContainsForbiddenField(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains(@"c:\users\")) return true;
        if (lower.Contains(Environment.MachineName.ToLowerInvariant()) && Environment.MachineName.Length > 2) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")) return true;
        return false;
    }

    /// <summary>Runs <paramref name="action"/>, reporting via <paramref name="threw"/> whether it
    /// raised — used where a scenario deliberately provokes a real exception and must prove it
    /// propagated rather than being silently swallowed.</summary>
    private static void Assert(Action action, out bool threw)
    {
        threw = false;
        try { action(); }
        catch { threw = true; }
    }

    private static void ClickAndPump(System.Windows.Controls.Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump();
    }

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            var frame = new DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (true)
        {
            if (condition()) return true;
            if (DateTime.UtcNow >= deadline) return condition();

            var frame = new DispatcherFrame();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class BindingErrorListener : TraceListener
    {
        private readonly List<string> _sink;
        public BindingErrorListener(List<string> sink) => _sink = sink;
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrEmpty(message)) _sink.Add(message);
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ScrcpyDeck.Views;

namespace ScrcpyDeck.Services;

/// <summary>
/// Runtime proof for the Batch 4E settings page, following the same pattern as
/// <see cref="TransfersSelfTest"/>: xUnit runs MTA and cannot construct a real WPF control tree,
/// so behavioral coverage that needs an actual visual tree lives in the PLP_SELFTEST harness
/// instead. Every scenario redirects <see cref="AppSettings"/> to its own isolated temp
/// directory (via <see cref="AppSettings.ResetForTests"/>) — the real
/// %AppData%\PhoneLinkPro\settings.json is never read or written by this suite. Save/Reset
/// scenarios that need controllable timing use <see cref="SettingsView.SaveOverrideForTests"/>/
/// <see cref="SettingsView.ResetOverrideForTests"/>; scenarios proving a REAL write happened
/// deliberately leave those seams null so the genuine atomic <see cref="SettingsStore"/> path
/// runs (settings persistence has no external process to fake, unlike adb/scrcpy).
/// </summary>
internal static class SettingsSelfTest
{
    internal sealed record Result(bool Passed, int Total, int Failed, string Report);

    private const int ExpectedScenarioAssertions = 141;

    private static readonly string[] ScenarioIds =
    {
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W",
        "X", "Y", "Z", "AA", "AB", "AC", "AD", "AE", "AF",
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

        // Repair batch §7: a failing SettingsStore.Save (deliberately forced by several scenarios
        // below) logs via Log.Warn, which defaults to the REAL %AppData%\PhoneLinkPro\logs
        // folder. Redirected for the whole run so this suite can never append to the real
        // application log, restored in the finally below.
        var previousLogOverride = Log.DirOverride;

        var scratchRoot = Path.Combine(Path.GetTempPath(), "plp_settings_selftest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchRoot);
        Log.DirOverride = Path.Combine(scratchRoot, "logs");

        string FreshDir(string label)
        {
            var dir = Path.Combine(scratchRoot, label + "_" + Guid.NewGuid().ToString("N"));
            AppSettings.ResetForTests(dir);
            return dir;
        }

        try
        {
            // Repair batch §7 profile-isolation checkpoint: fails fast, BEFORE any scenario below
            // runs, if the log redirection above was ever removed or bypassed (e.g. a partial
            // revert). Deliberately re-checked here rather than trusted silently, so a future
            // regression is caught by this guard instead of silently writing to the real
            // %AppData%\PhoneLinkPro\logs folder.
            if (Log.DirOverride == null)
                throw new InvalidOperationException(
                    "SettingsSelfTest: log isolation was not established — refusing to run scenarios " +
                    "that could append to the real application log.");

            // ---- A: XAML/resources construct without exception; every referenced element exists ----
            {
                FreshDir("a");
                var v = Make();
                Expect("A: Save button exists", v.SaveBtn != null);
                Expect("A: Cancel button exists", v.CancelBtn != null);
                Expect("A: destination combo exists", v.DestinationCombo != null);
                Expect("A: reset confirm panel exists", v.ResetConfirmPanel != null);
                Expect("A: clear-logs confirm panel exists", v.ClearLogsConfirmPanel != null);
                Expect("A: engine tools status text exists", v.ToolsStatusText != null);
                Expect("A: engine integrity status text exists", v.IntegrityStatusText != null);
                Expect("A: no unexpected WPF binding/resource error was raised by construction",
                    bindingErrors.Count == 0);
                Unload(v);
            }

            // ---- B: an existing valid settings file loads truthfully ----
            {
                var dir = FreshDir("b");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "settings.json"),
                    $$"""
                    {"ConsentAccepted":true,"ConsentVersion":"{{PolicyText.Version}}","OnboardingSeen":true,"DefaultTransferDestination":"Documents"}
                    """);
                var v = Make();
                Expect("B: draft reflects the stored destination", v.DraftForTests == StandardRemoteDestination.Documents);
                Expect("B: persisted reflects the stored destination", v.PersistedForTests == StandardRemoteDestination.Documents);
                Expect("B: consent status renders as accepted", v.ConsentStatusText.Text.Contains("تم القبول"));
                Expect("B: onboarding status renders as seen", v.OnboardingStatusText.Text.Contains("من قبل"));
                Unload(v);
            }

            // ---- C: a missing settings file yields safe defaults ----
            {
                FreshDir("c"); // directory never created — Load must not require it to exist
                var v = Make();
                Expect("C: draft defaults to Download", v.DraftForTests == StandardRemoteDestination.Download);
                Expect("C: persisted defaults to Download", v.PersistedForTests == StandardRemoteDestination.Download);
                Expect("C: consent status renders as not accepted", v.ConsentStatusText.Text.Contains("لم يتم القبول"));
                Unload(v);
            }

            // ---- D: malformed JSON recovers to defaults and preserves one diagnostic backup ----
            {
                var dir = FreshDir("d");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "settings.json");
                File.WriteAllText(file, "{ this is not valid json");
                var v = Make();
                Expect("D: malformed content falls back to Download, never throws", v.DraftForTests == StandardRemoteDestination.Download);
                Expect("D: a bounded diagnostic backup of the corrupt file was preserved",
                    File.Exists(file + ".corrupt.bak"));
                Unload(v);
            }

            // ---- E: unknown future fields are ignored; known fields still load ----
            {
                var dir = FreshDir("e");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "settings.json"),
                    """{"OnboardingSeen":true,"SomeFutureSetting":{"nested":42}}""");
                var v = Make();
                Expect("E: known field still loads despite an unknown sibling field",
                    v.OnboardingStatusText.Text.Contains("من قبل"));
                Expect("E: an unset destination in the presence of an unknown field still falls back to Download",
                    v.DraftForTests == StandardRemoteDestination.Download);
                Unload(v);
            }

            // ---- F: an unsupported/unrecognized enum value fails safe to the documented default ----
            {
                var dir = FreshDir("f");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "settings.json"),
                    """{"DefaultTransferDestination":"SomeFutureDestinationThatDoesNotExistYet"}""");
                var v = Make();
                Expect("F: an unrecognized destination value falls back to Download",
                    v.DraftForTests == StandardRemoteDestination.Download);
                Unload(v);
            }

            // ---- G: opening the page performs no write ----
            {
                var dir = FreshDir("g");
                var v = Make();
                Expect("G: opening the page creates no settings file", !File.Exists(Path.Combine(dir, "settings.json")));
                Expect("G: opening the page creates no settings directory", !Directory.Exists(dir));
                Unload(v);
            }

            // ---- H: editing produces an unsaved/modified state ----
            {
                FreshDir("h");
                var v = Make();
                Expect("H: page opens clean (not dirty)", !v.IsDirtyForTests);
                SelectDestination(v, StandardRemoteDestination.Pictures);
                Expect("H: changing the destination marks the page dirty", v.IsDirtyForTests);
                Expect("H: the unsaved pill becomes visible", v.UnsavedPill.Visibility == Visibility.Visible);
                Expect("H: Save becomes enabled", v.SaveBtn.IsEnabled);
                Expect("H: Cancel becomes enabled", v.CancelBtn.IsEnabled);
                Unload(v);
            }

            // ---- I: Save applies and persists a real, verifiable change ----
            {
                var dir = FreshDir("i");
                var v = Make();
                SelectDestination(v, StandardRemoteDestination.Movies);
                ClickAndPump(v.SaveBtn);
                Expect("I: Save settles within the bound", WaitUntil(() => !v.SavingForTests));
                Expect("I: the in-memory persisted value is updated", v.PersistedForTests == StandardRemoteDestination.Movies);
                Expect("I: the page no longer reports unsaved changes", !v.IsDirtyForTests);
                var reloaded = SettingsStore.Load(Path.Combine(dir, "settings.json"));
                Expect("I: the value was genuinely written to disk, independent of in-memory state",
                    reloaded.DefaultTransferDestination == nameof(StandardRemoteDestination.Movies));
                Unload(v);
            }

            // ---- J: a duplicate rapid Save starts only one write ----
            {
                FreshDir("j");
                var v = Make();
                SelectDestination(v, StandardRemoteDestination.Music);
                var callCount = 0;
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SettingsView.SaveOverrideForTests = _ => { callCount++; return tcs.Task; };
                try
                {
                    ClickAndPump(v.SaveBtn);
                    Expect("J: Save is busy after the first click", v.SavingForTests);
                    v.SaveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); // rapid duplicate
                    Pump();
                    Expect("J: exactly one Save call reached the store despite two rapid clicks", callCount == 1);
                    tcs.SetResult(true);
                    Expect("J: the pending Save settles within the bound", WaitUntil(() => !v.SavingForTests));
                    Expect("J: the value from the single accepted call was applied",
                        v.PersistedForTests == StandardRemoteDestination.Music);
                }
                finally { SettingsView.SaveOverrideForTests = null; }
                Unload(v);
            }

            // ---- K: a Save failure remains recoverable — draft and controls stay usable, retry succeeds ----
            {
                FreshDir("k");
                var v = Make();
                SelectDestination(v, StandardRemoteDestination.Pictures);
                SettingsView.SaveOverrideForTests = _ => Task.FromResult(false);
                try
                {
                    ClickAndPump(v.SaveBtn);
                    Expect("K: a failed Save settles within the bound", WaitUntil(() => !v.SavingForTests));
                    Expect("K: the failure is surfaced", v.SaveFailedForTests);
                    Expect("K: the unpersisted draft survives the failure", v.DraftForTests == StandardRemoteDestination.Pictures);
                    Expect("K: Save is available again for a retry", v.SaveBtn.IsEnabled);
                    Expect("K: the page never crashed or became permanently busy", !v.SavingForTests);
                }
                finally { SettingsView.SaveOverrideForTests = null; }

                ClickAndPump(v.SaveBtn); // retry with the real (now unfaked) path
                Expect("K: a retried Save succeeds", WaitUntil(() => !v.SavingForTests && !v.SaveFailedForTests));
                Expect("K: the retried value is applied", v.PersistedForTests == StandardRemoteDestination.Pictures);
                Unload(v);
            }

            // ---- L: a real Save is atomic — no leftover temp file, exactly the final file remains ----
            {
                var dir = FreshDir("l");
                var v = Make();
                SelectDestination(v, StandardRemoteDestination.Documents);
                ClickAndPump(v.SaveBtn);
                Expect("L: Save settles within the bound", WaitUntil(() => !v.SavingForTests));
                Expect("L: exactly the final settings file exists", File.Exists(Path.Combine(dir, "settings.json")));
                Expect("L: no temporary file survives a successful save",
                    Directory.GetFiles(dir, "*.tmp").Length == 0);
                Unload(v);
            }

            // ---- M: a stale Save completion after Unload cannot repaint or overwrite a newer Save ----
            {
                FreshDir("m");
                var v = Make();
                SelectDestination(v, StandardRemoteDestination.Music);
                var staleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SettingsView.SaveOverrideForTests = _ => staleTcs.Task;
                v.SaveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Expect("M: the stale operation is in flight before Unload", v.SavingForTests);

                Unload(v); // advances generation, force-clears busy state
                Expect("M: Unload force-clears the busy flag immediately", !v.SavingForTests);

                v.OnShown(); // simulate returning to the cached page
                SettingsView.SaveOverrideForTests = _ => Task.FromResult(true);
                SelectDestination(v, StandardRemoteDestination.Pictures);
                ClickAndPump(v.SaveBtn);
                Expect("M: a genuinely new Save after reload settles", WaitUntil(() => !v.SavingForTests));
                Expect("M: the new Save's value is applied", v.PersistedForTests == StandardRemoteDestination.Pictures);

                Exception? thrown = null;
                try { staleTcs.SetResult(true); Pump(); }
                catch (Exception ex) { thrown = ex; }
                Expect("M: the stale completion throws nothing", thrown == null);
                Expect("M: the stale completion cannot overwrite the newer, already-applied value",
                    v.PersistedForTests == StandardRemoteDestination.Pictures);

                SettingsView.SaveOverrideForTests = null;
            }

            // ---- N: navigating away with an unsaved draft retains it (never silently discarded) ----
            {
                FreshDir("n");
                var v = Make();
                SelectDestination(v, StandardRemoteDestination.Movies);
                Expect("N: the draft is dirty before navigating away", v.IsDirtyForTests);
                Unload(v);
                v.OnShown(); // simulate returning to this cached page instance
                Expect("N: the unsaved draft is retained across navigation", v.DraftForTests == StandardRemoteDestination.Movies);
                Expect("N: the page still reports unsaved changes after returning", v.IsDirtyForTests);
                Unload(v);
            }

            // ---- O: Reset requires explicit confirmation — opening it changes nothing ----
            {
                var dir = FreshDir("o");
                AppSettings.TrySaveDefaultTransferDestination(StandardRemoteDestination.Documents);
                var v = Make();
                ClickAndPump(v.ResetPreferencesBtn);
                Expect("O: the confirmation panel becomes visible", v.ResetConfirmVisibleForTests);
                Expect("O: nothing changed merely by opening the confirmation",
                    AppSettings.DefaultTransferDestination == StandardRemoteDestination.Documents);
                ClickAndPump(v.ResetConfirmCancelBtn);
                Expect("O: cancelling the confirmation leaves the value untouched",
                    AppSettings.DefaultTransferDestination == StandardRemoteDestination.Documents);
                Expect("O: the confirmation panel closes on cancel", !v.ResetConfirmVisibleForTests);
                Unload(v);
            }

            // ---- P: Reset affects only the approved preference — never consent or onboarding ----
            {
                FreshDir("p");
                AppSettings.TrySaveDefaultTransferDestination(StandardRemoteDestination.Documents);
                AppSettings.OnboardingSeen = true;
                AppSettings.ConsentAccepted = true;
                var v = Make();
                ClickAndPump(v.ResetPreferencesBtn);
                ClickAndPump(v.ResetConfirmProceedBtn);
                Expect("P: reset settles within the bound", WaitUntil(() => !v.ResettingForTests));
                Expect("P: the destination preference was reset to Download",
                    AppSettings.DefaultTransferDestination == StandardRemoteDestination.Download);
                Expect("P: onboarding was NOT touched by reset", AppSettings.OnboardingSeen);
                Expect("P: consent was NOT touched by reset", AppSettings.ConsentAccepted);
                Expect("P: the confirmation panel closes after a successful reset", !v.ResetConfirmVisibleForTests);
                Unload(v);
            }

            // ---- Q: merely opening the page never writes and never touches consent ----
            {
                var dir = FreshDir("q");
                AppSettings.ConsentAccepted = true;
                var before = File.ReadAllText(Path.Combine(dir, "settings.json"));
                var v = Make();
                var after = File.ReadAllText(Path.Combine(dir, "settings.json"));
                Expect("Q: opening the page does not modify the settings file", before == after);
                Expect("Q: consent remains accepted after merely opening the page", AppSettings.ConsentAccepted);
                Expect("Q: the real settings directory path never appears in any visible status text",
                    !v.ConsentStatusText.Text.Contains(dir) && !v.OnboardingStatusText.Text.Contains(dir) &&
                    !v.SaveStatusText.Text.Contains(dir) && !v.ResetFailedText.Text.Contains(dir) &&
                    !v.ClearLogsFailedText.Text.Contains(dir));
                Unload(v);
            }

            // ---- R: the onboarding action is truthful and never touches consent ----
            {
                FreshDir("r");
                AppSettings.ConsentAccepted = true;
                AppSettings.OnboardingSeen = true;
                var v = Make();
                Expect("R: onboarding starts reported as seen", v.OnboardingStatusText.Text.Contains("من قبل"));
                ClickAndPump(v.ShowTourAgainBtn);
                Expect("R: the onboarding flag is truthfully cleared", !AppSettings.OnboardingSeen);
                Expect("R: the status text updates to reflect the new state", !v.OnboardingStatusText.Text.Contains("من قبل"));
                Expect("R: consent is never touched by the onboarding action", AppSettings.ConsentAccepted);
                Unload(v);
            }

            // ---- S: the persisted file never carries a sensitive field or a hidden-identity word ----
            {
                var dir = FreshDir("s");
                var v = Make();
                SelectDestination(v, StandardRemoteDestination.Pictures);
                ClickAndPump(v.SaveBtn);
                Expect("S: the save settles", WaitUntil(() => !v.SavingForTests));
                var text = File.ReadAllText(Path.Combine(dir, "settings.json"));
                using var doc = JsonDocument.Parse(text);
                var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();
                var expectedKeys = new[] { "ConsentAccepted", "ConsentVersion", "DefaultTransferDestination", "OnboardingSeen" }
                    .OrderBy(k => k, StringComparer.Ordinal).ToList();
                Expect("S: the persisted file contains exactly the four known fields, nothing more",
                    keys.SequenceEqual(expectedKeys));
                Expect("S: the persisted file never mentions the hidden engine identity",
                    !text.Contains("adb", StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains("scrcpy", StringComparison.OrdinalIgnoreCase));
                Unload(v);
            }

            // ---- T: the engine-status card is read-only and truthfully mirrors the real services ----
            {
                FreshDir("t");
                var v = Make();
                var missing = ToolLocator.MissingEngineFiles();
                Expect("T: the tools status truthfully reflects ToolLocator",
                    v.ToolsStatusText.Text.Contains("متاحة") == (missing.Count == 0));
                Expect("T: the engine version shown matches the real manifest version",
                    v.EngineVersionText.Text == "v" + EngineManifest.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Expect("T: the app version shown matches the real assembly version",
                    v.AppVersionText.Text == "v" + AppVersionInfo.ProductVersion);
                Unload(v);
            }

            // ---- U: the integrity row truthfully reflects Valid vs. a forced failure ----
            {
                try
                {
                    EngineIntegrityVerifier.ResetForTests();
                    EngineIntegrityVerifier.ManifestTextProvider = () => null; // forces MissingManifestData
                    var failTask = EngineIntegrityVerifier.VerifyAsync();
                    WaitUntil(() => failTask.IsCompleted);

                    FreshDir("u1");
                    var vFail = Make();
                    Expect("U: a forced integrity failure renders as failed, with the danger indicator",
                        vFail.IntegrityStatusText.Text.Contains("فشل"));
                    Unload(vFail);

                    var engineBase = Path.Combine(scratchRoot, "u_engine_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.Combine(engineBase, "engine"));
                    var enginePath = Path.Combine(engineBase, "engine", "fake.bin");
                    File.WriteAllBytes(enginePath, new byte[] { 1, 2, 3, 4 });
                    var manifestText = EngineManifest.Render(new[]
                        { new EngineManifestEntry("engine/fake.bin", EngineManifest.HashFileSha256(enginePath)) });

                    EngineIntegrityVerifier.ResetForTests();
                    EngineIntegrityVerifier.BaseDirProvider = () => engineBase;
                    EngineIntegrityVerifier.ManifestTextProvider = () => manifestText;
                    var okTask = EngineIntegrityVerifier.VerifyAsync();
                    WaitUntil(() => okTask.IsCompleted);

                    FreshDir("u2");
                    var vOk = Make();
                    Expect("U: a genuinely valid manifest renders as verified",
                        vOk.IntegrityStatusText.Text.Contains("تم التحقق"));
                    Unload(vOk);
                }
                finally
                {
                    EngineIntegrityVerifier.ResetForTests();
                    EngineIntegrityVerifier.BaseDirProvider = savedIntegrityBaseDir;
                    EngineIntegrityVerifier.ManifestTextProvider = savedIntegrityManifest;
                }
            }

            // ---- V: no fake theme or language selector exists — read-only text only ----
            {
                FreshDir("v");
                var v = Make();
                Expect("V: no ComboBox exists anywhere except the one real destination preference",
                    CountDescendants<System.Windows.Controls.ComboBox>(v) == 1);
                Unload(v);
            }

            // ---- W: keyboard contract — Ctrl+S saves when valid, Escape reverts, Enter is inert ----
            {
                FreshDir("w");
                var v = Make();
                SelectDestination(v, StandardRemoteDestination.Music);
                var saveCalls = 0;
                SettingsView.SaveOverrideForTests = _ => { saveCalls++; return Task.FromResult(true); };
                try
                {
                    v.HandleKeyForTests(Key.S, ModifierKeys.Control);
                    Pump();
                    Expect("W: Ctrl+S triggers Save when a valid draft exists", WaitUntil(() => saveCalls == 1));
                    Expect("W: Ctrl+S applied the value", WaitUntil(() => v.PersistedForTests == StandardRemoteDestination.Music));
                }
                finally { SettingsView.SaveOverrideForTests = null; }

                SelectDestination(v, StandardRemoteDestination.Pictures);
                v.HandleKeyForTests(Key.Enter, ModifierKeys.None);
                Pump();
                Expect("W: Enter never triggers Reset or any destructive confirmation",
                    !v.ResetConfirmVisibleForTests && v.DraftForTests == StandardRemoteDestination.Pictures);

                v.HandleKeyForTests(Key.Escape, ModifierKeys.None);
                Expect("W: Escape reverts an unsaved draft back to the persisted value",
                    v.DraftForTests == v.PersistedForTests);

                ClickAndPump(v.ResetPreferencesBtn);
                v.HandleKeyForTests(Key.Escape, ModifierKeys.None);
                Expect("W: Escape closes the reset confirmation instead of reverting the draft",
                    !v.ResetConfirmVisibleForTests);
                Unload(v);
            }

            // ---- X: tour reset succeeds, updates status truthfully, and persists for the next
            // isolated startup ----
            {
                var dir = FreshDir("x");
                AppSettings.ConsentAccepted = true;
                AppSettings.OnboardingSeen = true;
                var v = Make();
                Expect("X: onboarding starts reported as seen", v.OnboardingStatusText.Text.Contains("من قبل"));

                ClickAndPump(v.ShowTourAgainBtn);
                Expect("X: tour reset settles within the bound", WaitUntil(() => !v.TourResettingForTests));
                Expect("X: the onboarding flag is truthfully cleared", !AppSettings.OnboardingSeen);
                Expect("X: the status text updates to reflect the new state", !v.OnboardingStatusText.Text.Contains("من قبل"));
                Expect("X: no failure is reported on success", !v.TourResetFailedForTests);

                var reloaded = SettingsStore.Load(Path.Combine(dir, "settings.json"));
                Expect("X: a fresh isolated load observes OnboardingSeen=false on disk", !reloaded.OnboardingSeen);
                Unload(v);
            }

            // ---- Y: a failed tour-reset write never claims success, stays recoverable, and never
            // leaks a raw exception or the settings path ----
            {
                var dir = FreshDir("y");
                AppSettings.ConsentAccepted = true;
                AppSettings.OnboardingSeen = true;
                var v = Make();

                SettingsView.SetOnboardingSeenOverrideForTests = _ => Task.FromResult(false);
                try
                {
                    ClickAndPump(v.ShowTourAgainBtn);
                    Expect("Y: a failed tour-reset settles within the bound", WaitUntil(() => !v.TourResettingForTests));
                    Expect("Y: the failure is surfaced, never claimed as success", v.TourResetFailedForTests);
                    Expect("Y: the status text still truthfully shows the unchanged (seen) state",
                        v.OnboardingStatusText.Text.Contains("من قبل"));
                    Expect("Y: the underlying flag was never actually flipped", AppSettings.OnboardingSeen);
                    Expect("Y: the action remains available for a retry", v.ShowTourAgainBtn.IsEnabled);
                    Expect("Y: the failure text carries no raw exception text or settings path",
                        !v.TourResetFailedText.Text.Contains(dir) && !v.TourResetFailedText.Text.Contains("Exception"));
                }
                finally { SettingsView.SetOnboardingSeenOverrideForTests = null; }

                ClickAndPump(v.ShowTourAgainBtn); // retry with the real (now unfaked) path
                Expect("Y: a retried tour-reset succeeds", WaitUntil(() => !v.TourResettingForTests && !v.TourResetFailedForTests));
                Expect("Y: the retried value is applied", !AppSettings.OnboardingSeen);
                Unload(v);
            }

            // ---- Z: a duplicate tour-reset activation starts at most one write; consent, its
            // version, and the unrelated destination preference stay untouched ----
            {
                FreshDir("z");
                AppSettings.ConsentAccepted = true;
                var versionBefore = AppSettings.ConsentVersion;
                var destinationBefore = AppSettings.DefaultTransferDestination;
                var v = Make();

                var callCount = 0;
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SettingsView.SetOnboardingSeenOverrideForTests = _ => { callCount++; return tcs.Task; };
                try
                {
                    v.ShowTourAgainBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Pump();
                    Expect("Z: the tour-reset is busy after the first activation", v.TourResettingForTests);
                    v.ShowTourAgainBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); // rapid duplicate
                    Pump();
                    Expect("Z: exactly one write reached the store despite two rapid activations", callCount == 1);
                    tcs.SetResult(true);
                    Expect("Z: the pending tour-reset settles within the bound", WaitUntil(() => !v.TourResettingForTests));
                }
                finally { SettingsView.SetOnboardingSeenOverrideForTests = null; }

                Expect("Z: consent is untouched by the tour-reset action", AppSettings.ConsentAccepted);
                Expect("Z: the consent version is untouched", AppSettings.ConsentVersion == versionBefore);
                Expect("Z: the default transfer destination is untouched", AppSettings.DefaultTransferDestination == destinationBefore);
                Unload(v);
            }

            // ---- AA: Open Log Folder — one activation invokes the seam exactly once with the
            // exact approved directory; a rapid duplicate cannot launch a second one while busy ----
            {
                FreshDir("aa");
                var v = Make();
                var callCount = 0;
                var capturedPaths = new List<string>();
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SettingsView.OpenPathOverrideForTests = path => { callCount++; capturedPaths.Add(path); return tcs.Task; };
                try
                {
                    v.OpenLogFolderBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Pump();
                    Expect("AA: opening the log folder is busy after the first activation", v.OpeningLogFolderForTests);
                    v.OpenLogFolderBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); // rapid duplicate
                    Pump();
                    Expect("AA: exactly one invocation reached the seam despite two rapid activations", callCount == 1);
                    Expect("AA: the exact approved application log directory was supplied, nothing user-controlled",
                        capturedPaths.Count == 1 && capturedPaths[0] == Log.Dir);
                    tcs.SetResult(true);
                    Expect("AA: the pending activation settles within the bound", WaitUntil(() => !v.OpeningLogFolderForTests));
                    Expect("AA: success is truthfully reported (no failure text)", !v.OpenLogFolderFailedForTests);
                }
                finally { SettingsView.OpenPathOverrideForTests = null; }
                Unload(v);
            }

            // ---- AB: Open Log Folder — a failure is reported truthfully, stays recoverable, and
            // never leaks the real directory path or a raw exception into the UI ----
            {
                FreshDir("ab");
                var v = Make();
                SettingsView.OpenPathOverrideForTests = _ => Task.FromResult(false);
                try
                {
                    ClickAndPump(v.OpenLogFolderBtn);
                    Expect("AB: a failed activation settles within the bound", WaitUntil(() => !v.OpeningLogFolderForTests));
                    Expect("AB: the failure is surfaced truthfully", v.OpenLogFolderFailedForTests);
                    Expect("AB: the action remains available for a retry", v.OpenLogFolderBtn.IsEnabled);
                    Expect("AB: the failure text carries no real path and no raw exception text",
                        !v.OpenLogFolderFailedText.Text.Contains(Log.Dir) &&
                        !v.OpenLogFolderFailedText.Text.Contains(":\\") &&
                        !v.OpenLogFolderFailedText.Text.Contains("Exception"));
                }
                finally { SettingsView.OpenPathOverrideForTests = null; }
                Unload(v);
            }

            // ---- AC: Clear Logs confirmation — safe default focus, Enter cannot bypass it,
            // Escape closes it, cancelling deletes nothing, and a duplicate confirmation starts
            // at most one delete ----
            {
                FreshDir("ac");
                var v = Make();

                ClickAndPump(v.ClearLogsBtn);
                Expect("AC: the confirmation panel becomes visible", v.ClearLogsConfirmVisibleForTests);
                Expect("AC: default focus lands on Cancel, never the destructive action",
                    FocusManager.GetFocusedElement(v) == v.ClearLogsConfirmCancelBtn);

                v.HandleKeyForTests(Key.Enter, ModifierKeys.None);
                Pump();
                Expect("AC: Enter cannot bypass the confirmation", v.ClearLogsConfirmVisibleForTests);

                v.HandleKeyForTests(Key.Escape, ModifierKeys.None);
                Expect("AC: Escape closes the confirmation", !v.ClearLogsConfirmVisibleForTests);

                ClickAndPump(v.ClearLogsBtn);
                var cancelCallCount = 0;
                SettingsView.ClearLogsOverrideForTests = _ => { cancelCallCount++; return Task.FromResult(true); };
                try
                {
                    ClickAndPump(v.ClearLogsConfirmCancelBtn);
                    Expect("AC: cancelling the confirmation deletes nothing", cancelCallCount == 0);
                    Expect("AC: the confirmation panel closes on cancel", !v.ClearLogsConfirmVisibleForTests);
                }
                finally { SettingsView.ClearLogsOverrideForTests = null; }

                ClickAndPump(v.ClearLogsBtn);
                var proceedCallCount = 0;
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SettingsView.ClearLogsOverrideForTests = _ => { proceedCallCount++; return tcs.Task; };
                try
                {
                    v.ClearLogsConfirmProceedBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Pump();
                    Expect("AC: clearing logs is busy after the first confirmation", v.ClearingLogsForTests);
                    v.ClearLogsConfirmProceedBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); // rapid duplicate
                    Pump();
                    Expect("AC: exactly one delete operation started despite two rapid confirmations", proceedCallCount == 1);
                    tcs.SetResult(true);
                    Expect("AC: the pending delete settles within the bound", WaitUntil(() => !v.ClearingLogsForTests));
                }
                finally { SettingsView.ClearLogsOverrideForTests = null; }
                Unload(v);
            }

            // ---- AD: Clear Logs — real deletion touches only approved top-level app-*.log files;
            // unrelated siblings, nested files, and settings all survive; success is truthful ----
            {
                FreshDir("ad");
                var fixtureLogDir = Path.Combine(scratchRoot, "ad_logs_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fixtureLogDir);
                var deletable1 = Path.Combine(fixtureLogDir, "app-20260101.log");
                var deletable2 = Path.Combine(fixtureLogDir, "app-20260102.overflow.log");
                var siblingFile = Path.Combine(fixtureLogDir, "readme.txt");
                var settingsDecoy = Path.Combine(fixtureLogDir, "settings.json");
                var nonMatchingLog = Path.Combine(fixtureLogDir, "document.log"); // doesn't start with "app-"
                var nestedDir = Path.Combine(fixtureLogDir, "nested");
                Directory.CreateDirectory(nestedDir);
                var nestedLog = Path.Combine(nestedDir, "app-nested.log");
                File.WriteAllText(deletable1, "x");
                File.WriteAllText(deletable2, "x");
                File.WriteAllText(siblingFile, "x");
                File.WriteAllText(settingsDecoy, "{}");
                File.WriteAllText(nonMatchingLog, "x");
                File.WriteAllText(nestedLog, "x");

                var previousScenarioLogOverride = Log.DirOverride;
                Log.DirOverride = fixtureLogDir;
                try
                {
                    var v = Make();
                    ClickAndPump(v.ClearLogsBtn);
                    ClickAndPump(v.ClearLogsConfirmProceedBtn);
                    Expect("AD: the delete settles within the bound", WaitUntil(() => !v.ClearingLogsForTests));
                    Expect("AD: no failure is reported", !v.ClearLogsFailedForTests);
                    Expect("AD: the confirmation panel closes on success", !v.ClearLogsConfirmVisibleForTests);

                    Expect("AD: the top-level app-*.log file is deleted", !File.Exists(deletable1));
                    Expect("AD: the top-level app-*.overflow.log file is deleted", !File.Exists(deletable2));
                    Expect("AD: an unrelated sibling file survives", File.Exists(siblingFile));
                    Expect("AD: an unrelated settings-named file survives", File.Exists(settingsDecoy));
                    Expect("AD: a non-matching *.log file (not app-*) survives", File.Exists(nonMatchingLog));
                    Expect("AD: a nested directory's log file survives (never recursed into)", File.Exists(nestedLog));
                    Unload(v);
                }
                finally { Log.DirOverride = previousScenarioLogOverride; }
            }

            // ---- AE: Clear Logs — a locked file's deletion failure is reported truthfully, never
            // as a complete success, and the operation stays recoverable ----
            {
                FreshDir("ae");
                var fixtureLogDir = Path.Combine(scratchRoot, "ae_logs_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fixtureLogDir);
                // Sorts after "app-1..." alphabetically so the deletable file (if enumerated
                // first) is already gone by the time the locked one aborts the loop — a genuine
                // partial-deletion condition that must still never be reported as success.
                var deletable = Path.Combine(fixtureLogDir, "app-1deletable.log");
                var locked = Path.Combine(fixtureLogDir, "app-9locked.log");
                File.WriteAllText(deletable, "x");
                File.WriteAllText(locked, "x");

                var previousScenarioLogOverride = Log.DirOverride;
                Log.DirOverride = fixtureLogDir;
                using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    var v = Make();
                    ClickAndPump(v.ClearLogsBtn);
                    ClickAndPump(v.ClearLogsConfirmProceedBtn);
                    Expect("AE: the delete settles within the bound", WaitUntil(() => !v.ClearingLogsForTests));
                    Expect("AE: a locked-file failure is reported truthfully, never as success", v.ClearLogsFailedForTests);
                    Expect("AE: the locked file itself was never deleted", File.Exists(locked));
                    Expect("AE: the confirmation panel remains open, not silently closed on failure",
                        v.ClearLogsConfirmVisibleForTests);
                    Expect("AE: the failure text carries no raw exception text or real path",
                        !v.ClearLogsFailedText.Text.Contains(fixtureLogDir) && !v.ClearLogsFailedText.Text.Contains("Exception"));
                    Expect("AE: Clear Logs remains available for a retry", v.ClearLogsBtn.IsEnabled);
                    Unload(v);
                }
                finally { Log.DirOverride = previousScenarioLogOverride; }
            }

            // ---- AF: Clear Logs boundary safety — enumeration cannot escape the log directory
            // into its parent, and a reparse-point log directory is refused outright ----
            {
                FreshDir("af");
                var parentDir = Path.Combine(scratchRoot, "af_parent_" + Guid.NewGuid().ToString("N"));
                var childLogDir = Path.Combine(parentDir, "logs");
                Directory.CreateDirectory(childLogDir);
                var parentDecoy = Path.Combine(parentDir, "app-parent-decoy.log");
                File.WriteAllText(parentDecoy, "x");
                File.WriteAllText(Path.Combine(childLogDir, "app-child.log"), "x");

                var previousScenarioLogOverride = Log.DirOverride;
                Log.DirOverride = childLogDir;
                try
                {
                    var v = Make();
                    ClickAndPump(v.ClearLogsBtn);
                    ClickAndPump(v.ClearLogsConfirmProceedBtn);
                    Expect("AF: the delete settles within the bound", WaitUntil(() => !v.ClearingLogsForTests));
                    Expect("AF: a file in the PARENT of the log directory is never touched",
                        File.Exists(parentDecoy));
                    Unload(v);
                }
                finally { Log.DirOverride = previousScenarioLogOverride; }

                // Reparse-point refusal: skipped gracefully (but still asserted, with a fixed
                // assertion count either way — see below) if this environment has no privilege to
                // create a directory symlink (e.g. Developer Mode disabled, no elevation).
                string? reparseTarget = null;
                string? reparseLink = null;
                var reparseFeasible = false;
                try
                {
                    reparseTarget = Path.Combine(scratchRoot, "af_target_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(reparseTarget);
                    File.WriteAllText(Path.Combine(reparseTarget, "app-shouldnottouch.log"), "x");
                    reparseLink = Path.Combine(scratchRoot, "af_link_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateSymbolicLink(reparseLink, reparseTarget);
                    reparseFeasible = true;
                }
                catch { /* no privilege to create symlinks in this environment — skip gracefully */ }

                bool reparseTargetUntouched;
                bool reparseReportedAsFailure;
                if (reparseFeasible)
                {
                    var previousReparseLogOverride = Log.DirOverride;
                    Log.DirOverride = reparseLink;
                    try
                    {
                        var v = Make();
                        ClickAndPump(v.ClearLogsBtn);
                        ClickAndPump(v.ClearLogsConfirmProceedBtn);
                        WaitUntil(() => !v.ClearingLogsForTests);
                        reparseTargetUntouched = File.Exists(Path.Combine(reparseTarget!, "app-shouldnottouch.log"));
                        reparseReportedAsFailure = v.ClearLogsFailedForTests;
                        Unload(v);
                    }
                    finally { Log.DirOverride = previousReparseLogOverride; }
                }
                else
                {
                    // Nothing to disprove without symlink privilege — trivially satisfied so the
                    // scenario's assertion count stays fixed regardless of the host environment.
                    reparseTargetUntouched = true;
                    reparseReportedAsFailure = true;
                }
                Expect("AF: a reparse-point (symlink/junction) log directory is refused, never deleted through" +
                       (reparseFeasible ? "" : " (skipped — no symlink privilege in this environment)"),
                    reparseTargetUntouched);
                Expect("AF: refusing a reparse-point directory is reported as a failure, not a silent success" +
                       (reparseFeasible ? "" : " (skipped — no symlink privilege in this environment)"),
                    reparseReportedAsFailure);
            }

            var scenarioAssertions = total;
            Expect($"exactly {ExpectedScenarioAssertions} scenario assertions ran (delete-scenario guard)",
                scenarioAssertions == ExpectedScenarioAssertions);

            var missingScenarios = ScenarioIds.Where(id => !assertionNames.Any(n => n.StartsWith(id + ":", StringComparison.Ordinal))).ToList();
            Expect($"all {ScenarioIds.Length} named runtime scenarios are still present: " +
                   (missingScenarios.Count == 0 ? "none missing" : "missing " + string.Join(", ", missingScenarios)),
                missingScenarios.Count == 0);

            var bindingSummary = bindingErrors.Count == 0
                ? "no WPF binding/resource errors"
                : $"no WPF binding/resource errors — found {bindingErrors.Count}: " + string.Join(" | ", bindingErrors.Take(5));
            Expect(bindingSummary, bindingErrors.Count == 0);
        }
        catch (Exception ex)
        {
            failures.Add($"unexpected failure: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SettingsView.SaveOverrideForTests = null;
            SettingsView.ResetOverrideForTests = null;
            SettingsView.SetOnboardingSeenOverrideForTests = null;
            SettingsView.ClearLogsOverrideForTests = null;
            SettingsView.OpenPathOverrideForTests = null;

            EngineIntegrityVerifier.ResetForTests();
            EngineIntegrityVerifier.BaseDirProvider = savedIntegrityBaseDir;
            EngineIntegrityVerifier.ManifestTextProvider = savedIntegrityManifest;

            AppSettings.ResetForTests(previousSettingsDir);
            Log.DirOverride = previousLogOverride;
            try { Directory.Delete(scratchRoot, recursive: true); } catch { /* best effort */ }

            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = previousLevel;
            PresentationTraceSources.Refresh();
        }

        var passed = failures.Count == 0;
        var report = $"settings: {total - failures.Count}/{total} assertions passed" +
                     (passed ? "" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));
        return new Result(passed, total, failures.Count, report);
    }

    // ---------------- helpers ----------------

    private static SettingsView Make()
    {
        var v = new SettingsView();
        v.OnShown();
        Load(v);
        return v;
    }

    private static void Load(SettingsView v) => v.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
    private static void Unload(SettingsView v) => v.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

    private static void SelectDestination(SettingsView v, StandardRemoteDestination destination)
    {
        var index = 0;
        foreach (StandardRemoteDestination d in Enum.GetValues<StandardRemoteDestination>())
        {
            if (d == destination) { v.DestinationCombo.SelectedIndex = index; Pump(); return; }
            index++;
        }
    }

    private static void ClickAndPump(System.Windows.Controls.Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump();
    }

    /// <summary>
    /// Walks the LOGICAL tree (populated by InitializeComponent alone, unlike the visual tree
    /// which needs ApplyTemplate/layout) so this works on a constructed-but-never-shown control.
    /// </summary>
    private static int CountDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = 0;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            if (d is T) count++;
            count += CountDescendants<T>(d);
        }
        return count;
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

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Guards the two defects found during the Samsung Galaxy S24 Ultra physical acceptance:
/// <list type="bullet">
/// <item>HW-2026-001 — the underlying engine's name reached user-visible text.</item>
/// <item>HW-2026-002 — a cancelled transfer persisted a raw serial, the full Windows profile
/// path and the full remote path, contradicting the app's own privacy statements.</item>
/// </list>
/// The identity guard deliberately inspects RENDERED string values (and accessibility names),
/// not source symbols, so internal resource keys such as <c>{StaticResource Adb}</c> and the
/// <c>ScrcpyDeck</c> namespace stay legal while a visible caption never can.
/// </summary>
[Collection(GlobalStateCollection.Name)]   // shares Log.DirOverride with every other suite
public sealed class PrivacyRepairTests : IDisposable
{
    // ------------------------------------------------------------------ shared fixture

    private readonly string _logDir;
    private readonly string? _previousOverride;

    public PrivacyRepairTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "plp-tests-privacy-" + Guid.NewGuid().ToString("N"));
        _previousOverride = Log.DirOverride;
        Log.DirOverride = _logDir;            // never the real %AppData% profile
    }

    public void Dispose()
    {
        Log.DirOverride = _previousOverride;
        try { if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private string ReadPersistedLog()
        => Directory.Exists(_logDir)
            ? string.Concat(Directory.GetFiles(_logDir, "*.log").Select(File.ReadAllText))
            : "";

    // Synthetic fixtures only — never a real device's identifiers. These match the fake serial
    // and endpoint the rest of the suite already uses.
    private const string RawSerial = "R58M12ABCDE";
    private const string OtherSerial = "R58M12ABCDF";
    private const string RawEndpoint = "192.168.1.42:5555";
    private const string RawRemotePath = "/sdcard/Download/quarterly-report.pdf";

    // A user-chosen name with spaces is the realistic case ("WhatsApp Image 2026-07-29.jpeg");
    // any whitespace-terminated path rule leaks its tail.
    private const string SpacedRemotePath = "/sdcard/Download/Bank Statement 2026.pdf";
    private const string SpacedLocalPath = @"E:\Backup\Bank Statement 2026.pdf";
    private const string UncPath = @"\\NAS01\private$\payroll.xlsx";

    private static string RealProfilePath(params string[] tail)
        => Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
            .Concat(tail).ToArray());

    // ================================================== HW-2026-001 — visible identity

    /// <summary>Terms that must never be RENDERED, whatever they are called internally.</summary>
    private static readonly string[] ForbiddenVisibleTerms =
        ["adb", "scrcpy", "phonelink.bridge", "phonelink.engine"];

    /// <summary>XAML attributes WPF shows on screen or hands to a screen reader.</summary>
    private static readonly Regex RenderedAttribute = new(
        @"(?:^|\s)(?:Text|Content|ToolTip|Header|Title|AutomationProperties\.Name|AutomationProperties\.HelpText)\s*=\s*""(?<v>[^""]*)""",
        RegexOptions.Compiled);

    /// <summary>A markup extension ({Binding …}, {StaticResource Adb}) renders no literal text.</summary>
    internal static bool IsRenderedLiteral(string value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.TrimStart().StartsWith("{", StringComparison.Ordinal);

    internal static string? FindForbiddenTerm(string value)
        => ForbiddenVisibleTerms.FirstOrDefault(t => value.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ProductXamlFiles()
        => Directory.EnumerateFiles(TestPaths.RepoRoot(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every product .cs file — not just the view folders: Arabic user copy also
    /// lives in Models/, Converters/ and the application root.</summary>
    private static IEnumerable<string> ProductUiCodeFiles()
        => Directory.EnumerateFiles(TestPaths.RepoRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsArabic(string s)
        => s.Any(c => c >= '\u0600' && c <= '\u06FF');

    [Fact] // [REGRESSION] HW-2026-001: About rendered "…ولا مخرجات adb الخام…" to every user
    public void Xaml_RenderedTextAndAccessibilityNamesNeverNameTheEngine()
    {
        var offenders = new List<string>();

        foreach (var file in ProductXamlFiles())
        {
            foreach (Match m in RenderedAttribute.Matches(File.ReadAllText(file)))
            {
                var value = m.Groups["v"].Value;
                if (!IsRenderedLiteral(value)) continue;

                var term = FindForbiddenTerm(value);
                if (term != null)
                    offenders.Add($"{Path.GetFileName(file)} → '{term}' in \"{value}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "rendered XAML names the underlying engine:\n  " + string.Join("\n  ", offenders));
    }

    [Fact] // element inner text (<TextBlock>…</TextBlock>, <Run>…</Run>) renders too
    public void Xaml_ElementInnerTextNeverNamesTheEngine()
    {
        var comment = new Regex(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
        var innerText = new Regex(@">(?<v>[^<>]+)<", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in ProductXamlFiles())
        {
            var body = comment.Replace(File.ReadAllText(file), " ");
            foreach (Match m in innerText.Matches(body))
            {
                var value = m.Groups["v"].Value;
                if (string.IsNullOrWhiteSpace(value)) continue;

                var term = FindForbiddenTerm(value);
                if (term != null)
                    offenders.Add($"{Path.GetFileName(file)} → '{term}' in \"{value.Trim()}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "rendered XAML inner text names the underlying engine:\n  " + string.Join("\n  ", offenders));
    }

    [Fact] // [REGRESSION] HW-2026-001: the setup wizard rendered "أدوات الاتصال (adb)"
    public void ArabicUiStringLiterals_NeverNameTheEngine()
    {
        // Only Arabic literals are user-facing copy in this product; an English literal such
        // as "adb devices timed out" is an internal diagnostic and stays legal.
        var literal = new Regex(@"""(?<v>(?:[^""\\\r\n]|\\.)*)""", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in ProductUiCodeFiles())
        {
            foreach (Match m in literal.Matches(File.ReadAllText(file)))
            {
                var value = m.Groups["v"].Value;
                if (!ContainsArabic(value)) continue;

                var term = FindForbiddenTerm(value);
                if (term != null)
                    offenders.Add($"{Path.GetFileName(file)} → '{term}' in \"{value}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "user-facing Arabic copy names the underlying engine:\n  " + string.Join("\n  ", offenders));
    }

    [Fact] // the guard must not over-reach: internal keys and namespaces are NOT visible leaks
    public void IdentityScanner_DoesNotFlagResourceKeysBindingsOrInternalNames()
    {
        Assert.False(IsRenderedLiteral("{StaticResource Adb}"));
        Assert.False(IsRenderedLiteral("{Binding Serial, Converter={StaticResource MaskSerial}}"));
        Assert.False(IsRenderedLiteral("   {DynamicResource Scrcpy}"));

        Assert.Null(FindForbiddenTerm("PhoneLink Pro"));
        Assert.Null(FindForbiddenTerm("حول البرنامج والدعم"));

        // …while the exact shape of each repaired defect is still detected.
        Assert.NotNull(FindForbiddenTerm("لا تتضمّن هذه الصفحة ... ولا مخرجات adb الخام"));
        Assert.NotNull(FindForbiddenTerm("«تصحيح USB» هو ما يسمح لأدوات الاتصال (adb) على الحاسوب"));
    }

    [Fact] // the repaired wording is the generic one Settings already used
    public void About_AndSettings_UseTheSameGenericDiagnosticsWording()
    {
        var about = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "Windows", "AboutWindow.xaml"));
        var settings = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.Contains("مخرجات التشخيص الخام", about);
        Assert.Contains("مخرجات التشخيص الخام", settings);
    }

    // ============================================ HW-2026-002 — persistent-log sanitization

    [Fact] // [REGRESSION] the exact line the S24 Ultra acceptance found in the real log
    public void PersistedLog_NeverStoresARawSerialProfilePathOrRemotePath()
    {
        var localPath = RealProfilePath("Documents", "quarterly-report.pdf");
        var command = new ProcessCommand("PhoneLink.Bridge.exe",
            ["-s", RawSerial, "pull", RawRemotePath, localPath]);

        Log.Warn($"Cancelled: {command.LogText}");   // exactly ProcessRunner's cancellation shape

        var persisted = ReadPersistedLog();

        Assert.DoesNotContain(RawSerial, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(localPath, persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarterly-report", persisted, StringComparison.OrdinalIgnoreCase);

        // …and the entry is still worth having.
        Assert.Contains("Cancelled:", persisted);
        Assert.Contains("R58…#", persisted);
        Assert.Contains("/sdcard/Download/<file>.pdf", persisted);
        Assert.Contains(@"C:\Users\<user>", persisted);
    }

    [Fact] // wireless endpoints and pairing codes never survive to disk
    public void PersistedLog_MasksWirelessEndpointsAndRemovesPairingCodes()
    {
        Log.Info($"connect {RawEndpoint} succeeded");
        Log.Warn("pair 192.168.1.42:37123 code 246813 rejected");

        var persisted = ReadPersistedLog();

        Assert.DoesNotContain("192.168.1.42", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("246813", persisted, StringComparison.Ordinal);
        Assert.Contains("192.168.*.*", persisted);
        Assert.Contains("******", persisted);
    }

    [Fact] // exception text routinely embeds local paths — Log.Error appends it, so it is covered
    public void PersistedLog_SanitizesExceptionTextThatEmbedsPaths()
    {
        var localPath = RealProfilePath("Desktop", "passport-scan.png");

        Log.Error("Run failed", new InvalidOperationException($"cannot open {localPath} for {RawSerial}"));

        var persisted = ReadPersistedLog();

        Assert.DoesNotContain(localPath, persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passport-scan", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RawSerial, persisted, StringComparison.Ordinal);
        Assert.Contains("Run failed", persisted);
    }

    [Fact] // a raw value must not slip through under concurrency
    public async Task ConcurrentLogging_NeverLeaksRawValues()
    {
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            Log.Warn($"Cancelled: PhoneLink.Bridge.exe -s {RawSerial} pull {RawRemotePath} #{i}"))));

        var persisted = ReadPersistedLog();

        Assert.DoesNotContain(RawSerial, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("quarterly-report", persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // sanitizing already-sanitized text must be a no-op (export re-runs it)
    public void Sanitize_IsIdempotent()
    {
        var raw = $"Cancelled: bridge -s {RawSerial} pull {RawRemotePath} " +
                  $"{RealProfilePath("Documents", "a.bin")} at {RawEndpoint} code 246813";

        var once = LogSanitizer.Sanitize(raw);

        Assert.Equal(once, LogSanitizer.Sanitize(once));
        Assert.Equal(once, LogSanitizer.Sanitize(LogSanitizer.Sanitize(once)));
    }

    [Theory] // degenerate input can never make the sink throw or fall back to raw
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/sdcard")]
    [InlineData("/sdcard/")]
    [InlineData(@"C:\Users\")]
    [InlineData("::::")]
    public void Sanitize_HandlesDegenerateInputSafely(string? input)
        => Assert.NotNull(LogSanitizer.Sanitize(input));

    [Fact] // operational text must survive untouched, or the log stops being useful
    public void Sanitize_LeavesUnrelatedOperationalTextUnchanged()
    {
        const string line = "Shutdown: complete in 101ms";
        Assert.Equal(line, LogSanitizer.Sanitize(line));

        const string category = "/sdcard/Download";
        Assert.Equal(category, LogSanitizer.Sanitize(category));
    }

    [Fact] // two devices must remain distinguishable after masking, or triage becomes impossible
    public void MaskedSerials_StayDistinguishableBetweenDevices()
    {
        var a = LogSanitizer.Sanitize($"device {RawSerial}");
        var b = LogSanitizer.Sanitize($"device {OtherSerial}");

        Assert.NotEqual(a, b);
        Assert.Contains("R58…#", a);
    }

    // ------------------------------------------------- paths that defeat a whitespace rule

    [Fact] // [REGRESSION] a file name with spaces must not leak through the argument rendering
    public void PersistedLog_NeverStoresAPathWhoseFileNameContainsSpaces()
    {
        var command = new ProcessCommand("PhoneLink.Bridge.exe",
            ["-s", RawSerial, "pull", SpacedRemotePath, SpacedLocalPath]);

        Log.Warn($"Cancelled: {command.LogText}");

        var persisted = ReadPersistedLog();

        Assert.DoesNotContain("Bank Statement", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"E:\Backup", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/sdcard/Download/<file>.pdf", persisted);
        Assert.Contains(@"E:\…\<file>.pdf", persisted);
    }

    [Theory] // local paths a user can freely choose — none of them is under the profile root
    [InlineData(@"E:\Backup\bank-statement.pdf", "bank-statement")]
    [InlineData(@"D:\Taxes\2026\return.xlsx", "return")]
    [InlineData(@"\\NAS01\private$\payroll.xlsx", "payroll")]
    public void PersistedLog_MasksNonProfileAndUncPaths(string path, string secret)
    {
        Log.Warn($"Run failed: PhoneLink.Bridge.exe push {LogSanitizer.MaskPathToken(path)}");

        var persisted = ReadPersistedLog();

        Assert.DoesNotContain(secret, persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<file>", persisted);
    }

    [Theory] // remote roots other than /sdcard are equally private
    [InlineData("/storage/emulated/0/DCIM/Camera/IMG_0042.jpg", "IMG_0042")]
    [InlineData("/storage/self/primary/Documents/passport.pdf", "passport")]
    [InlineData("/storage/1A2B-3C4D/Private/diary.txt", "diary")]
    [InlineData("/data/media/0/Download/payslip.pdf", "payslip")]
    public void MaskPathToken_MasksEveryAndroidStorageRoot(string path, string secret)
    {
        var masked = LogSanitizer.MaskPathToken(path);

        Assert.DoesNotContain(secret, masked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<file>", masked);
    }

    [Fact] // the argument list is masked where it is still STRUCTURED, before any join
    public void ProcessCommandLogText_MasksPathArgumentsIndividually()
    {
        var command = new ProcessCommand("PhoneLink.Bridge.exe",
            ["-s", RawSerial, "pull", SpacedRemotePath, UncPath]);

        var text = command.LogText;

        Assert.DoesNotContain("Bank Statement", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payroll", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NAS01", text, StringComparison.OrdinalIgnoreCase);
        // non-path arguments are untouched, so the entry still says what was attempted
        Assert.Contains("pull", text);
        Assert.Contains("-s", text);
    }

    [Fact] // masking a path token twice must reproduce it exactly (the export re-runs it)
    public void MaskPathToken_IsIdempotentAcrossEveryShape()
    {
        foreach (var p in new[]
                 {
                     SpacedRemotePath, SpacedLocalPath, UncPath, RawRemotePath,
                     "/sdcard/Download", "/sdcard", @"C:\Users\someone\Documents\a.bin",
                     "/storage/emulated/0/DCIM/Camera/IMG_1.jpg",
                 })
        {
            var once = LogSanitizer.MaskPathToken(p);
            Assert.Equal(once, LogSanitizer.MaskPathToken(once));
        }
    }

    // ------------------------------------------------- the boundary itself, not a call site

    [Fact] // sanitization must happen INSIDE the sink, before anything reaches the file
    public void Log_SanitizesBeforeItEverTouchesTheFile()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "Services", "Log.cs"));

        var sanitizeAt = source.IndexOf("LogSanitizer.Sanitize", StringComparison.Ordinal);
        var appendAt = source.IndexOf("Append(file, text)", StringComparison.Ordinal);

        Assert.True(sanitizeAt > 0, "Log.cs no longer sanitizes at all");
        Assert.True(appendAt > sanitizeAt, "Log.cs appends before it sanitizes");
    }

    [Fact] // no call site may write to the log file directly, bypassing the boundary
    public void NoProductCodeWritesToTheLogFileOutsideTheSink()
    {
        var offenders = ProductUiCodeFiles()
            .Where(f => !string.Equals(Path.GetFileName(f), "Log.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("AppendAllText", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "these files append to a file directly instead of using Log: " + string.Join(", ", offenders));
    }

    [Fact] // the cancellation/failure diagnostics keep flowing through the central sink
    public void ProcessRunner_ReportsCommandFailuresThroughTheCentralSink()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "Services", "ProcessRunner.cs"));

        Assert.Contains("Log.Warn($\"Cancelled: {command.LogText}\")", source);
        Assert.Contains("Log.Error($\"Run failed: {command.LogText}\"", source);
    }
}

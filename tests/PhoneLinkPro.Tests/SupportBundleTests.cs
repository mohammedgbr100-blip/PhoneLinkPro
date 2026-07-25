using System.IO.Compression;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// LOG-02 (Batch 1F) — the support-bundle export: correct deterministic structure, hard
/// privacy rules (masked serials, no user paths, no pairing codes, no engine binaries, no
/// hash values in the summary), size caps, and graceful failure. Everything runs against
/// temporary directories; the real user profile is never touched.
///
/// In the global collection: the exporter's providers and Log.DirOverride are process-wide.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class SupportBundleTests : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly string? _previousLogOverride;

    public SupportBundleTests()
    {
        SupportBundle.ResetForTests();
        AppDiagnostics.ResetForTests();

        _root = Path.Combine(Path.GetTempPath(), "plp-tests-bundle-" + Guid.NewGuid().ToString("N"));
        _logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_logDir);

        _previousLogOverride = Log.DirOverride;
        Log.DirOverride = Path.Combine(_root, "applog");   // keep exporter noise out of _logDir

        SupportBundle.LogDirProvider = () => _logDir;
        SupportBundle.EngineDirProvider = () => Path.Combine(_root, "engine-not-present");
        SupportBundle.Clock = () => new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);
        SupportBundle.DevicesProvider = () => new List<AdbDevice>
        {
            new() { Serial = "R58M12ABCDE", State = "device", Model = "Galaxy" },
            new() { Serial = "192.168.1.42:5555", State = "offline" },
        };
        SupportBundle.IntegrityProvider = () => new EngineIntegrityResult(
            EngineIntegrityStatus.Valid, null, null, 11, TimeSpan.FromMilliseconds(40));
        SupportBundle.ManifestProvider = () =>
            "PLP-ENGINE-MANIFEST 1\n" +
            new string('a', 64) + " engine/PhoneLink.Engine.exe\n" +
            new string('b', 64) + " engine/SDL2.dll\n";
        SupportBundle.SettingsProvider = () => (true, true);
    }

    public void Dispose()
    {
        SupportBundle.ResetForTests();
        AppDiagnostics.ResetForTests();
        Log.DirOverride = _previousLogOverride;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ZipPath(string name = "bundle.zip") => Path.Combine(_root, name);

    private static Dictionary<string, string> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var result = new Dictionary<string, string>();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            result[entry.FullName] = reader.ReadToEnd();
        }
        return result;
    }

    // ---------------------------------------------------------------- structure

    [Fact] // both summaries always present; logs included; nothing else
    public void Export_ProducesTheDocumentedStructure()
    {
        File.WriteAllText(Path.Combine(_logDir, "app-20260725.log"), "hello log\n");

        var result = SupportBundle.Export(ZipPath());
        Assert.True(result.Success);

        var entries = ReadZip(ZipPath());
        Assert.Contains("diagnostics-summary.txt", entries.Keys);
        Assert.Contains("diagnostics-summary.json", entries.Keys);
        Assert.Contains("logs/app-20260725.log", entries.Keys);
        Assert.Equal(3, entries.Count);
    }

    [Fact] // deterministic: same inputs => identical entry list and content
    public void Export_IsDeterministicForEqualInputs()
    {
        File.WriteAllText(Path.Combine(_logDir, "app-20260725.log"), "stable line\n");

        Assert.True(SupportBundle.Export(ZipPath("a.zip")).Success);
        Assert.True(SupportBundle.Export(ZipPath("b.zip")).Success);

        var a = ReadZip(ZipPath("a.zip"));
        var b = ReadZip(ZipPath("b.zip"));
        Assert.Equal(a.Keys.OrderBy(k => k), b.Keys.OrderBy(k => k));
        foreach (var key in a.Keys) Assert.Equal(a[key], b[key]);
    }

    [Fact] // summary carries versions, OS, runtime, engine names, integrity STATUS, devices
    public void Summary_CarriesTheDocumentedFields()
    {
        var result = SupportBundle.Export(ZipPath());
        Assert.True(result.Success);
        var text = ReadZip(ZipPath())["diagnostics-summary.txt"];

        Assert.Contains(AppVersionInfo.ProductVersion, text);
        Assert.Contains("2026-07-25 10:00:00Z", text);
        Assert.Contains("Engine manifest:     v1 (2 expected files)", text);
        Assert.Contains("Engine integrity:    Valid", text);
        Assert.Contains("PhoneLink.Engine.exe", text);
        Assert.Contains("SDL2.dll", text);
        Assert.Contains("state=device", text);
        Assert.Contains("Consent accepted:    True", text);
    }

    // ---------------------------------------------------------------- privacy

    [Fact] // full serials never appear anywhere in the bundle — even in historical logs
    public void Export_MasksSerialsUserPathsAndSecrets()
    {
        File.WriteAllText(Path.Combine(_logDir, "app-20260725.log"),
            "old line engine start (R58M12ABCDE)\n" +
            @"copy from C:\Users\testuser\Documents\file.txt" + "\n" +
            "pair 192.168.1.42:37123 code 246813\n");

        Assert.True(SupportBundle.Export(ZipPath()).Success);
        var all = string.Join("\n", ReadZip(ZipPath()).Values);

        Assert.DoesNotContain("R58M12ABCDE", all);          // masked serial (summary + logs)
        Assert.DoesNotContain(@"C:\Users\testuser", all);      // masked profile path
        Assert.DoesNotContain("192.168.1.42", all);         // masked host
        Assert.DoesNotContain("246813", all);               // pairing code scrubbed
        Assert.Contains("R58…#", all);                      // still correlatable
    }

    [Fact] // the summary never carries hash values; log hashes are shortened
    public void Export_CarriesNoFullHashValues()
    {
        File.WriteAllText(Path.Combine(_logDir, "app-20260725.log"),
            $"EngineIntegrity: hash mismatch expected {new string('c', 64)}\n");

        Assert.True(SupportBundle.Export(ZipPath()).Success);
        var entries = ReadZip(ZipPath());

        Assert.DoesNotContain(new string('a', 64), entries["diagnostics-summary.txt"]);
        Assert.DoesNotContain(new string('a', 64), entries["diagnostics-summary.json"]);
        Assert.DoesNotContain(new string('c', 64), entries["logs/app-20260725.log"]);
    }

    [Fact] // no engine binaries, no user content — only text entries in known folders
    public void Export_ContainsOnlyKnownTextEntries()
    {
        File.WriteAllText(Path.Combine(_logDir, "app-20260725.log"), "x\n");
        // Hostile neighbors that must NOT be swept up:
        File.WriteAllBytes(Path.Combine(_logDir, "not-a-log.dll"), new byte[] { 1, 2 });
        File.WriteAllBytes(Path.Combine(_root, "video.mp4"), new byte[] { 3, 4 });

        Assert.True(SupportBundle.Export(ZipPath()).Success);

        foreach (var name in ReadZip(ZipPath()).Keys)
            Assert.True(
                name is "diagnostics-summary.txt" or "diagnostics-summary.json"
                || (name.StartsWith("logs/", StringComparison.Ordinal)
                    && (name.EndsWith(".log", StringComparison.Ordinal)
                        || name.EndsWith(".unavailable.txt", StringComparison.Ordinal))),
                $"unexpected entry: {name}");
    }

    // ---------------------------------------------------------------- limits + failure

    [Fact] // the log section respects the total size budget and keeps the newest content
    public void Export_CapsTotalLogSize()
    {
        SupportBundle.MaxTotalLogBytes = 4 * 1024;
        File.WriteAllText(Path.Combine(_logDir, "app-20260724.log"),
            new string('o', 8 * 1024) + "\nOLD-MARKER\n");
        File.WriteAllText(Path.Combine(_logDir, "app-20260725.log"),
            new string('n', 2 * 1024) + "\nNEW-MARKER\n");

        Assert.True(SupportBundle.Export(ZipPath()).Success);
        var entries = ReadZip(ZipPath());

        var totalLogBytes = entries.Where(e => e.Key.StartsWith("logs/", StringComparison.Ordinal))
            .Sum(e => e.Value.Length);
        Assert.True(totalLogBytes <= 8 * 1024, $"log section too large: {totalLogBytes}");
        Assert.Contains("NEW-MARKER", entries["logs/app-20260725.log"]);
    }

    [Fact] // a locked log becomes a note, not a failure
    public void Export_HandlesALockedLogGracefully()
    {
        var locked = Path.Combine(_logDir, "app-20260725.log");
        File.WriteAllText(locked, "will be locked\n");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = SupportBundle.Export(ZipPath());

        Assert.True(result.Success);
        var entries = ReadZip(ZipPath());
        Assert.Contains("logs/app-20260725.log.unavailable.txt", entries.Keys);
        Assert.Contains("IOException", entries["logs/app-20260725.log.unavailable.txt"]);
    }

    [Fact] // an unwritable destination returns a failure result — never a thrown exception
    public void Export_ReportsWriteFailuresWithoutThrowing()
    {
        var impossible = Path.Combine(_root, "no-such-dir", "sub", "bundle.zip");

        var result = SupportBundle.Export(impossible);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureCategory);
        Assert.False(File.Exists(impossible));
    }

    [Fact] // a failed export leaves no partial zip behind
    public void Export_CleansUpPartialOutputOnFailure()
    {
        var target = ZipPath();
        Directory.CreateDirectory(target);   // a directory at the zip path => create fails

        var result = SupportBundle.Export(target);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(target));   // our directory untouched, no stray file
    }
}

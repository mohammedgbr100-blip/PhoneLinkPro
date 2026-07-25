using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// SEC-01 (Batch 1E) — runtime engine integrity verification: streaming SHA-256 against the
/// embedded manifest, path containment, single-flight concurrency, per-process caching and
/// the deliberate Retry path. Runs entirely against throwaway temp directories.
///
/// In the global collection: the verifier is a process-wide singleton whose providers and
/// cache these tests replace.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class EngineIntegrityVerifierTests : IDisposable
{
    private readonly string _root;
    private readonly string _engineDir;
    private readonly string _logDir;
    private readonly string? _previousLogOverride;

    public EngineIntegrityVerifierTests()
    {
        EngineIntegrityVerifier.ResetForTests();

        _root = Path.Combine(Path.GetTempPath(), "plp-tests-integrity-" + Guid.NewGuid().ToString("N"));
        _engineDir = Path.Combine(_root, "engine");
        Directory.CreateDirectory(_engineDir);

        _logDir = Path.Combine(_root, "logs");
        _previousLogOverride = Log.DirOverride;
        Log.DirOverride = _logDir;

        EngineIntegrityVerifier.BaseDirProvider = () => _root;
    }

    public void Dispose()
    {
        EngineIntegrityVerifier.ResetForTests();
        Log.DirOverride = _previousLogOverride;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Writes engine files and points the verifier at a manifest generated for them.</summary>
    private void SetUpValidEngine(params (string Name, byte[] Content)[] extraFiles)
    {
        foreach (var name in new[]
                 {
                     "PhoneLink.Engine.exe", "PhoneLink.Bridge.exe", "core.dat",
                     "AdbWinApi.dll", "AdbWinUsbApi.dll", "SDL2.dll", "libusb-1.0.dll",
                     "avcodec-61.dll", "avformat-61.dll", "avutil-59.dll", "swresample-5.dll",
                 })
        {
            File.WriteAllText(Path.Combine(_engineDir, name), "content of " + name);
        }
        foreach (var (name, content) in extraFiles)
            File.WriteAllBytes(Path.Combine(_engineDir, name), content);

        var generated = EngineManifestGenerator.Generate(_engineDir);
        Assert.True(generated.Success, generated.Error ?? "setup generation failed");
        var text = generated.ManifestText!;
        EngineIntegrityVerifier.ManifestTextProvider = () => text;
    }

    private void UseManifestText(string? text)
        => EngineIntegrityVerifier.ManifestTextProvider = () => text;

    private string ReadLogs()
    {
        if (!Directory.Exists(_logDir)) return "";
        return string.Concat(Directory.GetFiles(_logDir, "*.log").Select(File.ReadAllText));
    }

    private const string EmptySha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    // ---------------------------------------------------------------- outcomes

    [Fact]
    public async Task AllFilesValid_ReportsValid()
    {
        SetUpValidEngine();

        var result = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.Valid, result.Status);
        Assert.True(result.IsValid);
        Assert.Equal(11, result.ExpectedFileCount);
        Assert.Null(result.AffectedPath);
        Assert.True(EngineIntegrityVerifier.IsVerifiedValid);
    }

    [Fact]
    public async Task MissingFile_IsReportedWithItsRelativePath()
    {
        SetUpValidEngine();
        File.Delete(Path.Combine(_engineDir, "SDL2.dll"));

        var result = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.MissingFile, result.Status);
        Assert.Equal("engine/SDL2.dll", result.AffectedPath);
        Assert.False(EngineIntegrityVerifier.IsVerifiedValid);
    }

    [Fact]
    public async Task ModifiedFile_ReportsHashMismatch()
    {
        SetUpValidEngine();
        var target = Path.Combine(_engineDir, "core.dat");
        var bytes = File.ReadAllBytes(target);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(target, bytes);

        var result = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.HashMismatch, result.Status);
        Assert.Equal("engine/core.dat", result.AffectedPath);
    }

    [Fact] // truncation to zero bytes is a mismatch like any other content change
    public async Task TruncatedToZeroBytes_ReportsHashMismatch()
    {
        SetUpValidEngine();
        File.WriteAllBytes(Path.Combine(_engineDir, "libusb-1.0.dll"), Array.Empty<byte>());

        var result = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.HashMismatch, result.Status);
        Assert.Equal("engine/libusb-1.0.dll", result.AffectedPath);
    }

    [Fact] // a legitimately empty expected file verifies fine
    public async Task ZeroByteFileMatchingItsManifest_IsValid()
    {
        SetUpValidEngine(("core.dat", Array.Empty<byte>()));

        var result = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.Valid, result.Status);
    }

    [Fact] // a locked file is Unreadable (safe exception category, no OS message, no full path)
    public async Task LockedFile_ReportsUnreadable()
    {
        SetUpValidEngine();
        var locked = Path.Combine(_engineDir, "AdbWinApi.dll");
        using var _ = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.UnreadableFile, result.Status);
        Assert.Equal("engine/AdbWinApi.dll", result.AffectedPath);
        Assert.Equal(nameof(IOException), result.Detail);
        Assert.DoesNotContain(_root, result.Detail);
    }

    // ---------------------------------------------------------------- manifest problems

    [Fact]
    public async Task MissingManifest_ReportsMissingManifestData()
    {
        SetUpValidEngine();
        UseManifestText(null);

        var result = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.MissingManifestData, result.Status);
    }

    [Fact]
    public async Task UnparseableManifest_ReportsMissingManifestData()
    {
        SetUpValidEngine();
        UseManifestText("this is not a manifest");

        var result = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.MissingManifestData, result.Status);
    }

    [Fact]
    public async Task DuplicateManifestPath_ReportsUnexpectedDuplicatePath()
    {
        SetUpValidEngine();
        UseManifestText(EngineManifest.Header + "\n" +
                        EmptySha256 + " engine/a.dll\n" +
                        EmptySha256 + " engine/A.dll\n");

        var result = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.UnexpectedDuplicatePath, result.Status);
        Assert.Equal("engine/A.dll", result.AffectedPath);
    }

    [Theory] // traversal and rooted entries are rejected as untrustworthy manifest data
    [InlineData("engine/../PhoneLinkPro.dll")]
    [InlineData("C:/Windows/System32/kernel32.dll")]
    [InlineData("/engine/x.dll")]
    [InlineData("engine\\x.dll")]
    public async Task HostileManifestPaths_AreRejectedWithoutReadingAnything(string path)
    {
        SetUpValidEngine();
        UseManifestText(EngineManifest.Header + "\n" + EmptySha256 + " " + path + "\n");

        var result = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.MissingManifestData, result.Status);
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task PreCancelledToken_ReportsCancelled_AndIsNeverCached()
    {
        SetUpValidEngine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancelled = await EngineIntegrityVerifier.VerifyAsync(cts.Token);
        Assert.Equal(EngineIntegrityStatus.VerificationCancelled, cancelled.Status);
        Assert.False(EngineIntegrityVerifier.IsVerifiedValid);

        // A cancelled run proves nothing: the next call verifies for real.
        var fresh = await EngineIntegrityVerifier.VerifyAsync();
        Assert.Equal(EngineIntegrityStatus.Valid, fresh.Status);
    }

    // ---------------------------------------------------------------- caching + single flight

    [Fact]
    public async Task SecondCall_ReturnsCachedResultWithoutRehashing()
    {
        SetUpValidEngine();

        var first = await EngineIntegrityVerifier.VerifyAsync();
        var runsAfterFirst = EngineIntegrityVerifier.RunCount;
        var second = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.Valid, first.Status);
        Assert.Same(first, second);
        Assert.Equal(runsAfterFirst, EngineIntegrityVerifier.RunCount);
    }

    [Fact] // failures are cached too (polls stay cheap) until a deliberate Retry
    public async Task FailureIsCached_UntilRetry()
    {
        SetUpValidEngine();
        File.Delete(Path.Combine(_engineDir, "core.dat"));

        var failed = await EngineIntegrityVerifier.VerifyAsync();
        var again = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.MissingFile, failed.Status);
        Assert.Same(failed, again);
        Assert.Equal(1, EngineIntegrityVerifier.RunCount);
    }

    [Fact] // the repair/reinstall flow: Retry re-reads the real files and recovers
    public async Task RetryAfterRepair_ReturnsValid()
    {
        SetUpValidEngine();
        var target = Path.Combine(_engineDir, "core.dat");
        var original = File.ReadAllBytes(target);
        File.WriteAllBytes(target, new byte[] { 1, 2, 3 });

        var failed = await EngineIntegrityVerifier.VerifyAsync();
        Assert.Equal(EngineIntegrityStatus.HashMismatch, failed.Status);

        File.WriteAllBytes(target, original);   // "reinstall"
        var repaired = await EngineIntegrityVerifier.RetryAsync();

        Assert.Equal(EngineIntegrityStatus.Valid, repaired.Status);
        Assert.True(EngineIntegrityVerifier.IsVerifiedValid);
        Assert.Equal(2, EngineIntegrityVerifier.RunCount);
        Assert.Contains("retry requested", ReadLogs());
    }

    [Fact] // concurrent callers share ONE verification run
    public async Task ConcurrentCallers_ShareASingleRun()
    {
        // A few MB per file so the run takes long enough for genuine overlap.
        var payload = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        SetUpValidEngine(("SDL2.dll", payload), ("core.dat", payload));

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => EngineIntegrityVerifier.VerifyAsync()))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(EngineIntegrityStatus.Valid, r.Status));
        Assert.Equal(1, EngineIntegrityVerifier.RunCount);
    }

    [Fact] // a large file is verified by streaming (no whole-file load) and matches
    public async Task LargeFile_IsVerifiedByStreaming()
    {
        var large = new byte[64 * 1024 * 1024];   // 64 MB — far larger than any hash buffer
        SetUpValidEngine(("core.dat", large));

        var result = await EngineIntegrityVerifier.VerifyAsync();

        Assert.Equal(EngineIntegrityStatus.Valid, result.Status);
    }

    // ---------------------------------------------------------------- logging (Part G)

    [Fact]
    public async Task Log_CarriesVersionCountsDurationAndMismatchHashes()
    {
        SetUpValidEngine();
        var target = Path.Combine(_engineDir, "SDL2.dll");
        File.WriteAllText(target, "tampered");

        await EngineIntegrityVerifier.VerifyAsync();

        var logs = ReadLogs();
        Assert.Contains("EngineIntegrity: HashMismatch", logs);
        Assert.Contains("manifest v1", logs);
        Assert.Contains("files=11", logs);
        Assert.Contains("file=engine/SDL2.dll", logs);
        Assert.Contains("expected ", logs);   // both hashes are in the log...
        Assert.Contains("actual ", logs);
    }

    [Fact] // ...but identical repeated outcomes are logged only once
    public async Task Log_DoesNotRepeatIdenticalOutcomes()
    {
        SetUpValidEngine();
        File.Delete(Path.Combine(_engineDir, "core.dat"));

        await EngineIntegrityVerifier.VerifyAsync();
        await EngineIntegrityVerifier.VerifyAsync();
        await EngineIntegrityVerifier.VerifyAsync();

        var occurrences = ReadLogs().Split("EngineIntegrity: MissingFile").Length - 1;
        Assert.Equal(1, occurrences);
    }

    // ---------------------------------------------------------------- embedded resource

    [Fact] // the production manifest actually rides inside the app assembly and parses
    public void EmbeddedManifest_IsPresentAndParseable()
    {
        var text = EngineIntegrityVerifier.ReadEmbeddedManifest();

        Assert.False(string.IsNullOrWhiteSpace(text));
        var problem = EngineManifest.TryParse(text, out var entries, out _);
        Assert.Equal(EngineManifestProblem.None, problem);
        Assert.True(entries.Count >= 11, $"expected ≥11 entries, got {entries.Count}");
        Assert.All(entries, e => Assert.StartsWith("engine/", e.Path, StringComparison.Ordinal));
    }
}

using System.Text;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// SEC-01 (Batch 1E) — the engine integrity manifest format and its build-time generator.
/// Everything here runs against throwaway temp directories; the real engine bundle is never
/// touched. No collection: only local state is used.
/// </summary>
public sealed class EngineManifestTests : IDisposable
{
    private readonly string _root;
    private readonly string _engineDir;

    /// <summary>SHA-256 of the empty input (well-known vector).</summary>
    private const string EmptySha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>SHA-256 of ASCII "abc" (well-known vector).</summary>
    private const string AbcSha256 =
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    public EngineManifestTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "plp-tests-manifest-" + Guid.NewGuid().ToString("N"));
        _engineDir = Path.Combine(_root, "engine");
        Directory.CreateDirectory(_engineDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Creates the full mandatory engine file set with deterministic contents.</summary>
    private void CreateMandatoryFiles()
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
    }

    private string GenerateOrFail()
    {
        var result = EngineManifestGenerator.Generate(_engineDir);
        Assert.True(result.Success, result.Error ?? "generation failed");
        return result.ManifestText!;
    }

    // ---------------------------------------------------------------- determinism

    [Fact] // same files => byte-for-byte identical manifest
    public void Generate_IsByteForByteDeterministic()
    {
        CreateMandatoryFiles();

        var first = GenerateOrFail();
        var second = GenerateOrFail();

        Assert.Equal(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));
    }

    [Fact] // timestamps and creation order must not influence the output
    public void Generate_IgnoresTimestampsAndCreationOrder()
    {
        CreateMandatoryFiles();
        var before = GenerateOrFail();

        // Recreate the same byte content in reverse order with different timestamps.
        var files = Directory.GetFiles(_engineDir);
        var contents = files.ToDictionary(f => f, File.ReadAllBytes);
        foreach (var f in files) File.Delete(f);
        foreach (var f in files.OrderByDescending(f => f, StringComparer.Ordinal))
        {
            File.WriteAllBytes(f, contents[f]);
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-Random.Shared.Next(1, 300)));
        }

        Assert.Equal(before, GenerateOrFail());
    }

    [Fact] // entries are sorted ordinally by path
    public void Generate_SortsEntriesDeterministically()
    {
        CreateMandatoryFiles();

        var lines = GenerateOrFail().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var paths = lines.Skip(1).Select(l => l[65..]).ToList();

        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal).ToList(), paths);
    }

    [Fact] // LF-only, no BOM-ish characters, single trailing newline
    public void Generate_UsesCanonicalTextShape()
    {
        CreateMandatoryFiles();

        var text = GenerateOrFail();

        Assert.DoesNotContain('\r', text);
        Assert.StartsWith("PLP-ENGINE-MANIFEST 1\n", text);
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\n\n", text);
    }

    // ---------------------------------------------------------------- hashing

    [Fact] // SHA-256 values are correct (known vectors)
    public void Generate_ComputesCorrectSha256()
    {
        CreateMandatoryFiles();
        File.WriteAllBytes(Path.Combine(_engineDir, "core.dat"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(_engineDir, "SDL2.dll"), "abc"u8.ToArray());

        var text = GenerateOrFail();

        Assert.Contains($"{EmptySha256} engine/core.dat", text);
        Assert.Contains($"{AbcSha256} engine/SDL2.dll", text);
    }

    // ---------------------------------------------------------------- mandatory set

    [Theory] // a bundle that cannot run must not get a manifest
    [InlineData("PhoneLink.Engine.exe")]
    [InlineData("PhoneLink.Bridge.exe")]
    [InlineData("core.dat")]
    [InlineData("SDL2.dll")]
    [InlineData("libusb-1.0.dll")]
    [InlineData("AdbWinApi.dll")]
    public void Generate_FailsWhenAMandatoryFileIsMissing(string name)
    {
        CreateMandatoryFiles();
        File.Delete(Path.Combine(_engineDir, name));

        var result = EngineManifestGenerator.Generate(_engineDir);

        Assert.False(result.Success);
        Assert.Contains(name, result.MissingMandatoryFiles);
    }

    [Fact] // version-suffixed codec families are required by prefix, not exact name
    public void Generate_FailsWhenACodecFamilyIsMissing()
    {
        CreateMandatoryFiles();
        File.Delete(Path.Combine(_engineDir, "avcodec-61.dll"));

        var result = EngineManifestGenerator.Generate(_engineDir);

        Assert.False(result.Success);
        Assert.Contains("avcodec-*.dll", result.MissingMandatoryFiles);
    }

    [Fact] // a renamed codec version still satisfies the family requirement
    public void Generate_AcceptsADifferentCodecVersion()
    {
        CreateMandatoryFiles();
        File.Move(
            Path.Combine(_engineDir, "avcodec-61.dll"),
            Path.Combine(_engineDir, "avcodec-62.dll"));

        var text = GenerateOrFail();

        Assert.Contains("engine/avcodec-62.dll", text);
    }

    [Fact]
    public void Generate_FailsWhenTheEngineDirectoryDoesNotExist()
    {
        var result = EngineManifestGenerator.Generate(Path.Combine(_root, "nope"));

        Assert.False(result.Success);
    }

    // ---------------------------------------------------------------- coverage rules

    [Fact] // logs, settings, text files, nested folders: never covered
    public void Generate_ExcludesUnrelatedFiles()
    {
        CreateMandatoryFiles();
        File.WriteAllText(Path.Combine(_engineDir, "readme.txt"), "x");
        File.WriteAllText(Path.Combine(_engineDir, "user.log"), "x");
        File.WriteAllText(Path.Combine(_engineDir, "recording.mp4"), "x");
        Directory.CreateDirectory(Path.Combine(_engineDir, "nested"));
        File.WriteAllText(Path.Combine(_engineDir, "nested", "inner.dll"), "x");

        var text = GenerateOrFail();

        Assert.DoesNotContain("readme.txt", text);
        Assert.DoesNotContain("user.log", text);
        Assert.DoesNotContain("recording.mp4", text);
        Assert.DoesNotContain("inner.dll", text);
    }

    [Fact] // extra top-level DLLs ARE covered (they ship with the bundle)
    public void Generate_IncludesAdditionalTopLevelDlls()
    {
        CreateMandatoryFiles();
        File.WriteAllText(Path.Combine(_engineDir, "extra-helper.dll"), "x");

        Assert.Contains("engine/extra-helper.dll", GenerateOrFail());
    }

    [Fact] // Arabic / Unicode file names round-trip
    public void Generate_HandlesUnicodeFileNames()
    {
        CreateMandatoryFiles();
        File.WriteAllText(Path.Combine(_engineDir, "مساعد.dll"), "x");

        var text = GenerateOrFail();
        Assert.Contains("engine/مساعد.dll", text);

        var problem = EngineManifest.TryParse(text, out var entries, out _);
        Assert.Equal(EngineManifestProblem.None, problem);
        Assert.Contains(entries, e => e.Path == "engine/مساعد.dll");
    }

    [Fact] // names with spaces are unambiguous (the hash column is fixed-width)
    public void Generate_HandlesFileNamesWithSpaces()
    {
        CreateMandatoryFiles();
        File.WriteAllText(Path.Combine(_engineDir, "my helper lib.dll"), "x");

        var text = GenerateOrFail();
        var problem = EngineManifest.TryParse(text, out var entries, out _);

        Assert.Equal(EngineManifestProblem.None, problem);
        Assert.Contains(entries, e => e.Path == "engine/my helper lib.dll");
    }

    [Fact] // no absolute paths, drive letters, backslashes or timestamps in the output
    public void Generate_EmitsNoMachineSpecificData()
    {
        CreateMandatoryFiles();

        var text = GenerateOrFail();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(EngineManifest.Header, lines[0]);
        foreach (var line in lines.Skip(1))
        {
            Assert.Matches("^[0-9a-f]{64} engine/[^\\\\:]+$", line);
            Assert.DoesNotContain(_root, line);
        }
    }

    // ---------------------------------------------------------------- parsing

    [Fact]
    public void TryParse_RoundTripsGeneratedText()
    {
        CreateMandatoryFiles();
        var text = GenerateOrFail();

        var problem = EngineManifest.TryParse(text, out var entries, out _);

        Assert.Equal(EngineManifestProblem.None, problem);
        Assert.Equal(11, entries.Count);
        Assert.Equal(text, EngineManifest.Render(entries));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void TryParse_ReportsMissingOrEmpty(string? text)
        => Assert.Equal(EngineManifestProblem.MissingOrEmpty,
            EngineManifest.TryParse(text, out _, out _));

    [Theory]
    [InlineData("WRONG-HEADER\n")]
    [InlineData("PLP-ENGINE-MANIFEST 2\n")]                 // future version: not trusted
    [InlineData("plp-engine-manifest 1\n")]                 // case matters
    public void TryParse_ReportsBadHeader(string text)
        => Assert.Equal(EngineManifestProblem.BadHeader,
            EngineManifest.TryParse(text, out _, out _));

    [Fact] // a header alone (zero entries) is not a usable manifest
    public void TryParse_ReportsHeaderOnlyAsEmpty()
        => Assert.Equal(EngineManifestProblem.MissingOrEmpty,
            EngineManifest.TryParse(EngineManifest.Header + "\n", out _, out _));

    [Theory]
    [InlineData("nonsense line")]
    [InlineData("abc engine/x.dll")]                                        // hash too short
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000 engine/x.dll")]
    public void TryParse_ReportsMalformedEntries(string entryLine)
        => Assert.Equal(EngineManifestProblem.MalformedEntry,
            EngineManifest.TryParse(EngineManifest.Header + "\n" + entryLine + "\n", out _, out _));

    [Fact] // uppercase hex is accepted and normalized to lowercase
    public void TryParse_NormalizesUppercaseHex()
    {
        var text = EngineManifest.Header + "\n" + EmptySha256.ToUpperInvariant() + " engine/x.dll\n";

        var problem = EngineManifest.TryParse(text, out var entries, out _);

        Assert.Equal(EngineManifestProblem.None, problem);
        Assert.Equal(EmptySha256, entries[0].Sha256);
    }

    [Theory] // traversal, rooted, escaping and non-canonical paths are all rejected
    [InlineData("engine/../PhoneLinkPro.dll")]
    [InlineData("engine/sub/../../x.dll")]
    [InlineData("C:/windows/system32/x.dll")]
    [InlineData("/engine/x.dll")]
    [InlineData("\\engine\\x.dll")]
    [InlineData("engine\\x.dll")]
    [InlineData("other/x.dll")]
    [InlineData("engine/")]
    [InlineData("engine//x.dll")]
    [InlineData("engine/x.dll ")]
    [InlineData("engine/x.dll.")]
    [InlineData("engine/x:stream.dll")]
    public void TryParse_RejectsInvalidPaths(string path)
    {
        var text = EngineManifest.Header + "\n" + EmptySha256 + " " + path + "\n";

        Assert.Equal(EngineManifestProblem.InvalidPath,
            EngineManifest.TryParse(text, out _, out _));
    }

    [Fact] // duplicate normalized paths (case-insensitive) are rejected
    public void TryParse_RejectsDuplicatePaths()
    {
        var text = EngineManifest.Header + "\n" +
                   EmptySha256 + " engine/SDL2.dll\n" +
                   AbcSha256 + " engine/sdl2.DLL\n";

        var problem = EngineManifest.TryParse(text, out _, out var offender);

        Assert.Equal(EngineManifestProblem.DuplicatePath, problem);
        Assert.Equal("engine/sdl2.DLL", offender);
    }

    [Fact] // CRLF input (e.g. a git-normalized checkout) still parses
    public void TryParse_ToleratesCrLf()
    {
        var text = EngineManifest.Header + "\r\n" + EmptySha256 + " engine/x.dll\r\n";

        Assert.Equal(EngineManifestProblem.None, EngineManifest.TryParse(text, out var entries, out _));
        Assert.Single(entries);
    }

    // ---------------------------------------------------------------- rendering

    [Fact]
    public void Render_RejectsDuplicatePaths()
        => Assert.Throws<ArgumentException>(() => EngineManifest.Render(new[]
        {
            new EngineManifestEntry("engine/a.dll", EmptySha256),
            new EngineManifestEntry("engine/A.DLL", AbcSha256),
        }));

    [Fact]
    public void Render_RejectsInvalidPathsAndHashes()
    {
        Assert.Throws<ArgumentException>(() => EngineManifest.Render(new[]
        {
            new EngineManifestEntry("engine/../a.dll", EmptySha256),
        }));
        Assert.Throws<ArgumentException>(() => EngineManifest.Render(new[]
        {
            new EngineManifestEntry("engine/a.dll", "nothex"),
        }));
    }

    [Fact] // rendering sorts, whatever order entries arrive in
    public void Render_SortsInput()
    {
        var text = EngineManifest.Render(new[]
        {
            new EngineManifestEntry("engine/z.dll", EmptySha256),
            new EngineManifestEntry("engine/a.dll", AbcSha256),
        });

        Assert.True(text.IndexOf("engine/a.dll", StringComparison.Ordinal) <
                    text.IndexOf("engine/z.dll", StringComparison.Ordinal));
    }
}

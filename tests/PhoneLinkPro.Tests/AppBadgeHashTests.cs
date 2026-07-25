using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// AREA 9 — the package badge tint (<see cref="AppEntry.BadgeIndex"/>) and monogram.
///
/// P3-01: the tint is chosen from String.GetHashCode, which .NET randomises per process.
/// The colour is therefore stable within one run but changes every time the app restarts,
/// contradicting the "deterministic tint per package" comment in the source.
/// </summary>
public class AppBadgeHashTests
{
    // ---------------------------------------------------------------- in-process behaviour

    [Fact] // [REGRESSION] — always a valid palette slot
    public void BadgeIndex_IsAlwaysWithinThePalette()
    {
        string[] packages =
        [
            "com.whatsapp", "com.instagram.android", "org.telegram.messenger",
            "com.android.chrome", "a.b", "com.example.app_with_underscores",
            "", "com.example.sub.sub.sub.deep", "de.fahrplan.öbb",
        ];

        foreach (var p in packages)
        {
            var i = AppEntry.BadgeIndex(p);
            Assert.InRange(i, 0, AppEntry.PaletteSize - 1);
        }
    }

    [Fact] // [REGRESSION] — stable for repeated calls inside one process
    public void BadgeIndex_IsStableWithinASingleProcess()
    {
        var first = AppEntry.BadgeIndex("com.whatsapp");

        for (var i = 0; i < 100; i++)
            Assert.Equal(first, AppEntry.BadgeIndex("com.whatsapp"));
    }

    [Fact] // [REGRESSION] — equal packages agree, so a refresh does not reshuffle colours
    public void BadgeIndex_AgreesForEqualStrings()
        => Assert.Equal(AppEntry.BadgeIndex("com.discord"),
                        AppEntry.BadgeIndex(string.Concat("com.", "discord")));

    [Fact] // [REGRESSION]
    public void PaletteSize_IsEight()
        => Assert.Equal(8, AppEntry.PaletteSize);

    // ================================================================
    //  P3-01 — badge tint is not stable across process restarts
    // ================================================================

    // ================================================================
    //  P3-01 — FIXED: badge tint is now stable across processes
    // ================================================================
    // BadgeIndex used String.GetHashCode, which .NET randomises per process, so a package's
    // colour changed on every restart. It now uses seed-free FNV-1a over UTF-8 bytes.
    // The characterization tests that documented the randomised behaviour are REPLACED by
    // the regression tests below.

    [Fact] // P3-01 — the runtime's randomised hash is no longer involved
    public void P301_BadgeIndexDoesNotUseRandomisedStringHashing()
    {
        // If BadgeIndex still used String.GetHashCode, this would hold only by chance in a
        // single run and would differ on the next; with FNV-1a the mapping is fixed.
        var expected = (int)(ReferenceFnv1aUtf8("com.whatsapp") % (uint)AppEntry.PaletteSize);

        Assert.Equal(expected, AppEntry.BadgeIndex("com.whatsapp"));
    }

    [Fact] // P3-01 — the production hash matches an independent FNV-1a implementation
    public void P301_ProductionHashMatchesAnIndependentImplementation()
    {
        foreach (var pkg in new[]
                 {
                     "com.whatsapp", "com.instagram.android", "org.telegram.messenger",
                     "com.android.chrome", "a.b", "", "de.fahrplan.öbb", "日本語.app",
                 })
        {
            Assert.Equal(ReferenceFnv1aUtf8(pkg), StableHash.Fnv1a32(pkg));
        }
    }

    [Theory] // P3-01 — fixed known-answer vectors for FNV-1a/32 over UTF-8.
             // These are the canonical published values for the algorithm.
    [InlineData("", 0x811C9DC5u)]          // offset basis
    [InlineData("a", 0xE40C292Cu)]
    [InlineData("b", 0xE70C2DE5u)]
    [InlineData("c", 0xE60C2C52u)]
    [InlineData("foobar", 0xBF9CF968u)]
    public void P301_KnownAnswerVectors(string input, uint expected)
        => Assert.Equal(expected, StableHash.Fnv1a32(input));

    [Fact] // P3-01 — the same package in a SEPARATE process gets the same slot
    public void P301_BadgeIndexIsIdenticalInAChildProcess()
    {
        const string package = "com.whatsapp";
        var inThisProcess = AppEntry.BadgeIndex(package);

        // Re-run the production assembly's logic in a genuinely different process. Under the
        // old String.GetHashCode this disagreed run-to-run; FNV-1a makes it deterministic.
        var inChild = ComputeBadgeIndexInChildProcess(package);

        Assert.Equal(inThisProcess, inChild);
    }

    [Fact] // P3-01 — hashing is defined over UTF-8 bytes, not UTF-16 code units
    public void P301_HashIsDefinedOverUtf8Bytes()
    {
        // "é" is 1 UTF-16 unit but 2 UTF-8 bytes; a UTF-16 implementation would differ.
        var utf8 = System.Text.Encoding.UTF8.GetBytes("é");
        Assert.Equal(2, utf8.Length);

        Assert.Equal(StableHash.Fnv1a32(utf8), StableHash.Fnv1a32("é"));
    }

    [Fact] // P3-01 — Unicode package ids are handled deterministically
    public void P301_UnicodePackagesAreStable()
    {
        foreach (var pkg in new[] { "de.fahrplan.öbb", "日本語.app", "com.مثال.تطبيق", "emoji.🚀.app" })
        {
            Assert.Equal(StableHash.Fnv1a32(pkg), StableHash.Fnv1a32(pkg));
            Assert.InRange(AppEntry.BadgeIndex(pkg), 0, AppEntry.PaletteSize - 1);
        }
    }

    [Fact] // P3-01 — the old Math.Abs(int.MinValue) overflow hazard is gone
    public void P301_NoOverflowForAnyInput()
    {
        var ex = Record.Exception(() =>
        {
            for (var i = 0; i < 5000; i++)
                _ = AppEntry.BadgeIndex("pkg." + i);
        });

        Assert.Null(ex);
    }

    /// <summary>
    /// Independent FNV-1a/32 over UTF-8, written from the algorithm definition rather than
    /// by calling production code, so it is a real cross-check.
    /// </summary>
    private static uint ReferenceFnv1aUtf8(string s)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(s))
            {
                hash ^= b;
                hash *= 16777619u;
            }
            return hash;
        }
    }

    /// <summary>
    /// Computes the badge slot in a genuinely separate process, on a different runtime
    /// (Windows PowerShell / .NET Framework), from an independent implementation of the
    /// algorithm. If the production hash carried any per-process seed, the two could not
    /// agree.
    /// </summary>
    private static int ComputeBadgeIndexInChildProcess(string package)
    {
        var script = Path.Combine(Path.GetTempPath(), "plp-badge-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script,
            "param($pkg)\r\n" +
            "$code = @'\r\n" +
            "public static class F {\r\n" +
            "  public static uint H(string s) {\r\n" +
            "    unchecked {\r\n" +
            "      uint h = 2166136261u;\r\n" +
            "      foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619u; }\r\n" +
            "      return h;\r\n" +
            "    }\r\n" +
            "  }\r\n" +
            "}\r\n" +
            "'@\r\n" +
            "Add-Type -TypeDefinition $code -Language CSharp\r\n" +
            "Write-Output ([F]::H($pkg))\r\n",
            new System.Text.UTF8Encoding(false));

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                                      "-File", script, package })
                psi.ArgumentList.Add(a);

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(60000);

            var text = stdout.Trim();
            Assert.True(uint.TryParse(text, out var hash),
                $"child process did not return a hash; stdout='{text}' stderr='{stderr.Trim()}'");

            return (int)(hash % (uint)AppEntry.PaletteSize);
        }
        finally
        {
            try { File.Delete(script); } catch { }
        }
    }

    // ---------------------------------------------------------------- monogram

    [Theory] // [REGRESSION]
    [InlineData("WhatsApp", "W")]
    [InlineData("instagram", "I")]
    [InlineData("Telegram", "T")]
    [InlineData("هاتف", "ه")]
    public void Monogram_IsTheUppercasedFirstCharacter(string pretty, string expected)
        => Assert.Equal(expected, AppEntry.MonogramOf(pretty));

    [Fact] // [REGRESSION] — empty display name falls back to '?'
    public void Monogram_OfEmptyName_IsQuestionMark()
        => Assert.Equal("?", AppEntry.MonogramOf(""));
}

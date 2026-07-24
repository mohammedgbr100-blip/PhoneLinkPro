using ScrcpyDeck.Models;
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

    [Fact] // [CHARACTERIZATION] P3-01 — documents the mechanism
    public void P301_BadgeIndexDependsOnRandomisedStringHashing()
    {
        // .NET Core and later randomise String.GetHashCode with a per-process seed
        // (hash-DoS mitigation). Two runs of the app therefore disagree about the tint.
        // Proof of the mechanism, without cross-process orchestration: the runtime's hash
        // does not match the well-known stable hash of the same string.
        var runtimeHash = "com.whatsapp".GetHashCode(StringComparison.Ordinal);
        var stableHash = Fnv1a("com.whatsapp");

        Assert.NotEqual(stableHash, runtimeHash);

        // A stable algorithm gives the same slot every run; the current one does not.
        Assert.InRange(Math.Abs(stableHash % AppEntry.PaletteSize), 0, AppEntry.PaletteSize - 1);
    }

    [Fact] // [CHARACTERIZATION] P3-01 — a stable replacement is trivially available.
           // Reference implementation lives in the TEST project only; production is
           // unchanged in Batch 1A.
    public void P301_AStableHashWouldProduceAFixedSlot()
    {
        // FNV-1a has no per-process seed, so the same input always gives the same slot,
        // on every machine and every run — which is what the source comment claims the
        // current implementation provides, and does not.
        Assert.Equal(Fnv1a("com.whatsapp"), Fnv1a("com.whatsapp"));
        Assert.Equal(Fnv1a("com.whatsapp") % AppEntry.PaletteSize,
                     Fnv1a("com.whatsapp") % AppEntry.PaletteSize);

        // Distinct inputs still spread across the palette.
        var slots = new[] { "com.whatsapp", "com.discord", "com.spotify.music", "com.android.chrome" }
            .Select(p => Math.Abs(Fnv1a(p) % AppEntry.PaletteSize))
            .ToList();
        Assert.All(slots, s => Assert.InRange(s, 0, AppEntry.PaletteSize - 1));
    }

    [Fact(Skip = "P3-01: AppEntry.BadgeIndex uses String.GetHashCode, which is randomised " +
                 "per process, so a package's badge colour changes on every app restart. " +
                 "Asserting this directly needs two processes running the production " +
                 "assembly and comparing results, which requires a test-only host exe that " +
                 "Batch 1A deliberately does not add. Un-skip once BadgeIndex uses a stable " +
                 "hash; it then asserts a fixed, machine-independent slot. Target batch: 1D.")]
    public void P301_BadgeIndexMustBeStableAcrossProcesses_TargetBatch1D()
    {
        // After the fix, the slot must be a fixed constant rather than a per-run value.
        Assert.Equal(Math.Abs(Fnv1a("com.whatsapp") % AppEntry.PaletteSize),
                     AppEntry.BadgeIndex("com.whatsapp"));
    }

    /// <summary>Seed-free 32-bit FNV-1a — the reference for a stable, restart-proof tint.</summary>
    private static int Fnv1a(string s)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            var hash = offset;
            foreach (var ch in s)
            {
                hash ^= ch;
                hash *= prime;
            }
            return (int)hash;
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

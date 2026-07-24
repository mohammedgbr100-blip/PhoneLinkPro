using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// AREA 2 — `pm list packages` parsing (<see cref="AdbService.ParsePackages"/>)
/// and the friendly-name mapping in <see cref="AppNames"/>.
/// No new validation is introduced here; current behaviour is pinned as-is.
/// </summary>
public class AdbPackageParsingTests
{
    [Fact] // [REGRESSION]
    public void NormalPackageLines_AreParsedAndSorted()
    {
        const string output = """
            package:com.whatsapp
            package:com.android.chrome
            package:org.telegram.messenger
            """;

        var pkgs = AdbService.ParsePackages(output);

        // Output is sorted case-insensitively, not left in device order.
        Assert.Equal(new[] { "com.android.chrome", "com.whatsapp", "org.telegram.messenger" }, pkgs);
    }

    [Fact] // [REGRESSION]
    public void SystemListingWithApkPaths_KeepsTheWholeRemainderAfterPackagePrefix()
    {
        // `pm list packages -f` prints "package:<apk path>=<id>". The parser does not split
        // on '=', so the whole remainder is kept. Pinned as current behaviour.
        const string output = "package:/system/app/Settings/Settings.apk=com.android.settings\n";

        var p = Assert.Single(AdbService.ParsePackages(output));

        Assert.Equal("/system/app/Settings/Settings.apk=com.android.settings", p);
    }

    [Fact] // [REGRESSION]
    public void BlankAndWhitespaceLines_AreIgnored()
    {
        const string output = "package:com.whatsapp\n\n   \n\npackage:com.discord\n\n";

        var pkgs = AdbService.ParsePackages(output);

        Assert.Equal(new[] { "com.discord", "com.whatsapp" }, pkgs);
    }

    [Fact] // [REGRESSION] — lines without the package: prefix are dropped
    public void MalformedLines_WithoutPackagePrefix_AreIgnored()
    {
        const string output = """
            Error: could not access the Package Manager
            com.whatsapp
            package:com.discord
            random noise here
            """;

        var p = Assert.Single(AdbService.ParsePackages(output));

        Assert.Equal("com.discord", p);
    }

    [Fact] // [CHARACTERIZATION] — duplicates are NOT de-duplicated today
    public void DuplicatePackages_AreKeptTwice()
    {
        const string output = "package:com.whatsapp\npackage:com.whatsapp\n";

        var pkgs = AdbService.ParsePackages(output);

        Assert.Equal(2, pkgs.Count);
        Assert.All(pkgs, p => Assert.Equal("com.whatsapp", p));
    }

    [Fact] // [REGRESSION]
    public void CarriageReturns_AreTrimmed()
    {
        const string output = "package:com.whatsapp\r\npackage:com.discord\r\n";

        var pkgs = AdbService.ParsePackages(output);

        Assert.Equal(new[] { "com.discord", "com.whatsapp" }, pkgs);
    }

    [Fact] // [REGRESSION]
    public void EmptyOutput_YieldsNoPackages()
        => Assert.Empty(AdbService.ParsePackages(""));

    [Theory] // [REGRESSION] — unusual but valid Android package ids survive intact
    [InlineData("a.b")]
    [InlineData("com.example.app_with_underscores")]
    [InlineData("com.example.MixedCase")]
    [InlineData("com.example.app2")]
    [InlineData("com.example.sub.sub.sub.sub.deep")]
    [InlineData("de.fahrplan.öbb")]   // non-ASCII, accepted by some OEM builds
    public void UnusualButValidPackageNames_ArePreservedVerbatim(string pkg)
    {
        var parsed = Assert.Single(AdbService.ParsePackages($"package:{pkg}\n"));

        Assert.Equal(pkg, parsed);
    }

    [Fact] // [CHARACTERIZATION] — "package:" with nothing after it yields an empty entry
    public void EmptyPackageId_ProducesAnEmptyEntry()
    {
        var parsed = Assert.Single(AdbService.ParsePackages("package:\n"));

        Assert.Equal("", parsed);
    }

    // ---------------------------------------------------------------- friendly names

    [Theory] // [REGRESSION]
    [InlineData("com.whatsapp", "WhatsApp")]
    [InlineData("com.instagram.android", "Instagram")]
    [InlineData("org.telegram.messenger", "Telegram")]
    [InlineData("com.android.vending", "Google Play")]
    public void KnownPackages_MapToTheirRealNames(string pkg, string expected)
        => Assert.Equal(expected, AppNames.Pretty(pkg));

    [Fact] // [REGRESSION] — lookup is case-insensitive
    public void KnownPackageLookup_IsCaseInsensitive()
        => Assert.Equal("WhatsApp", AppNames.Pretty("COM.WhatsApp"));

    [Theory] // [REGRESSION] — unknown ids fall back to a cleaned last segment
    [InlineData("com.example.notepad", "Notepad")]
    [InlineData("com.example.my_app", "My app")]      // '_' -> ' ', first letter upper
    [InlineData("com.vendor.camera", "Camera")]
    public void UnknownPackages_FallBackToLastSegment(string pkg, string expected)
        => Assert.Equal(expected, AppNames.Pretty(pkg));

    [Theory] // [REGRESSION] — generic trailing segments step back one level
    [InlineData("com.brave.browser.app", "Browser")]
    [InlineData("com.example.notes.android", "Notes")]
    [InlineData("com.acme.reader.mobile", "Reader")]
    public void GenericTrailingSegments_StepBackOneSegment(string pkg, string expected)
        => Assert.Equal(expected, AppNames.Pretty(pkg));

    [Fact] // [CHARACTERIZATION] — a single-character result falls back to the full id
    public void SingleCharacterSegment_FallsBackToFullPackageId()
        => Assert.Equal("com.example.x", AppNames.Pretty("com.example.x"));

    [Fact] // [CHARACTERIZATION] — empty input is returned unchanged
    public void EmptyPackage_IsReturnedUnchanged()
        => Assert.Equal("", AppNames.Pretty(""));
}

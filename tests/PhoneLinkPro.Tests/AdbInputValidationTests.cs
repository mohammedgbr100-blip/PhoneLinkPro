using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// CMD-02 — <see cref="AdbInput"/> validation rules.
///
/// The rules must reject values that could be read as adb options or carry control
/// characters, WITHOUT rejecting the serials, package ids and endpoints that real Samsung,
/// Xiaomi, emulator and TCP/IP devices actually use.
/// </summary>
public class AdbInputValidationTests
{
    // ---------------------------------------------------------------- serials: accept

    [Theory]
    [InlineData("R58M12ABCDE")]          // Samsung
    [InlineData("9A291FFAZ00A9K")]       // Pixel
    [InlineData("0123456789ABCDEF")]
    [InlineData("emulator-5554")]        // emulator, contains '-'
    [InlineData("192.168.1.42:5555")]    // TCP/IP
    [InlineData("10.0.0.5:37123")]
    [InlineData("[fe80::1]:5555")]       // bracketed IPv6
    [InlineData("[fe80::1%wlan0]:5555")] // IPv6 with zone id
    [InlineData("adb-R58M12ABCDE-abc123._adb-tls-connect._tcp")]  // mDNS serial
    [InlineData("XIAOMI_1234")]
    public void LegitimateSerials_AreAccepted(string serial)
    {
        Assert.True(AdbInput.TryValidateSerial(serial, out var error), $"rejected: {error}");
        Assert.Equal("", error);
    }

    // ---------------------------------------------------------------- serials: reject

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-s")]                    // would be read as an option
    [InlineData("--help")]
    [InlineData("abc kill-server")]       // whitespace injection
    [InlineData("abc\tdef")]
    [InlineData("abc\ndef")]
    [InlineData("abc\0def")]              // control character
    [InlineData("abc\"def")]
    [InlineData("abc'def")]
    [InlineData("abc;rm")]
    [InlineData("abc|def")]
    [InlineData("abc&def")]
    [InlineData("abc$(id)")]
    [InlineData("abc`id`")]
    [InlineData("abc>out")]
    [InlineData("abc\\def")]
    public void HostileSerials_AreRejectedWithAnArabicReason(string? serial)
    {
        Assert.False(AdbInput.TryValidateSerial(serial, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void AbsurdlyLongSerial_IsRejected()
        => Assert.False(AdbInput.TryValidateSerial(new string('a', 300), out _));

    // ---------------------------------------------------------------- packages: accept

    [Theory]
    [InlineData("com.whatsapp")]
    [InlineData("com.google.android.youtube")]
    [InlineData("a.b")]
    [InlineData("com.example.app_with_underscores")]
    [InlineData("com.example.MixedCase")]
    [InlineData("com.example.app2")]
    [InlineData("com.example.sub.sub.sub.sub.deep")]
    [InlineData("de.fahrplan.öbb")]        // Unicode letters are legal Java identifiers
    [InlineData("_internal.tool")]
    [InlineData("com.sec.android.app.camera")]
    public void LegitimatePackages_AreAccepted(string pkg)
    {
        Assert.True(AdbInput.TryValidatePackage(pkg, out var error), $"rejected: {error}");
        Assert.Equal("", error);
    }

    // ---------------------------------------------------------------- packages: reject

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("com.example.app --user 0")]   // whitespace injection
    [InlineData("com.example.app; rm -rf /")]  // shell operator
    [InlineData("com.example.app|cat")]
    [InlineData("com.example.app&&id")]
    [InlineData("com.example.app\"x")]
    [InlineData("com.example.app'x")]
    [InlineData("com.example.app`id`")]
    [InlineData("com.example.app$(id)")]
    [InlineData("com.example.app\nrm")]
    [InlineData("com.example.app\0")]
    [InlineData(".com.example")]               // leading separator
    [InlineData("com.example.")]               // trailing separator
    [InlineData("com..example")]               // empty segment
    [InlineData("com.2example")]               // segment starting with a digit
    [InlineData("-com.example")]               // would be read as an option
    [InlineData("com.example/../etc")]
    public void HostilePackages_AreRejectedWithAnArabicReason(string? pkg)
    {
        Assert.False(AdbInput.TryValidatePackage(pkg, out var error));
        Assert.NotEqual("", error);
    }

    // ---------------------------------------------------------------- endpoints

    [Theory]
    [InlineData("192.168.1.42:5555", "192.168.1.42:5555")]
    [InlineData("10.0.0.5:37123", "10.0.0.5:37123")]
    [InlineData("192.168.1.42", "192.168.1.42")]
    [InlineData(" 192.168.1.42:5555 ", "192.168.1.42:5555")]   // trimmed
    [InlineData("[::1]:5555", "[::1]:5555")]
    [InlineData("[fe80::1]", "[fe80::1]")]
    public void ValidEndpoints_AreAcceptedAndNormalized(string input, string expected)
    {
        Assert.True(AdbInput.TryValidateEndpoint(input, out var normalized, out var error),
            $"rejected: {error}");
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void BareIpv6_IsNormalizedIntoBrackets()
    {
        Assert.True(AdbInput.TryValidateEndpoint("::1", out var normalized, out _));
        Assert.Equal("[::1]", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("192.168.1.42:0")]         // port 0
    [InlineData("192.168.1.42:65536")]     // port out of range
    [InlineData("192.168.1.42:-1")]
    [InlineData("192.168.1.42:abc")]
    [InlineData("192.168.1.42:5555 && id")]
    [InlineData("192.168.1.999:5555")]     // invalid octet
    [InlineData("192.168.1.42;rm")]
    [InlineData("[::1")]                   // unterminated bracket
    [InlineData("[::1]:70000")]
    public void InvalidEndpoints_AreRejectedWithAnArabicReason(string? endpoint)
    {
        Assert.False(AdbInput.TryValidateEndpoint(endpoint, out _, out var error));
        Assert.NotEqual("", error);
    }

    // ---------------------------------------------------------------- ports

    [Theory]
    [InlineData(1)]
    [InlineData(5555)]
    [InlineData(65535)]
    public void ValidPorts_AreAccepted(int port)
        => Assert.True(AdbInput.TryValidatePort(port, out _));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void InvalidPorts_AreRejected(int port)
    {
        Assert.False(AdbInput.TryValidatePort(port, out var error));
        Assert.NotEqual("", error);
    }

    // ---------------------------------------------------------------- pairing codes

    [Theory]
    [InlineData("123456")]
    [InlineData("000000")]
    [InlineData(" 654321 ")]   // trimmed
    public void ValidPairingCodes_AreAccepted(string code)
        => Assert.True(AdbInput.TryValidatePairingCode(code, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]        // too short
    [InlineData("1234567")]      // too long
    [InlineData("12345a")]
    [InlineData("12 345")]
    [InlineData("123456; id")]
    [InlineData("١٢٣٤٥٦")]        // Arabic-Indic digits are not accepted by adb
    public void InvalidPairingCodes_AreRejected(string? code)
    {
        Assert.False(AdbInput.TryValidatePairingCode(code, out var error));
        Assert.NotEqual("", error);
    }

    // ---------------------------------------------------------------- android shell quoting

    [Fact]
    public void ShellQuote_WrapsInSingleQuotes()
        => Assert.Equal("'/sdcard/file.txt'", AdbInput.ShellQuote("/sdcard/file.txt"));

    [Theory] // inside single quotes sh treats everything literally
    [InlineData("a b", "'a b'")]
    [InlineData("a;b", "'a;b'")]
    [InlineData("a&b", "'a&b'")]
    [InlineData("a|b", "'a|b'")]
    [InlineData("a>b", "'a>b'")]
    [InlineData("$(id)", "'$(id)'")]
    [InlineData("`id`", "'`id`'")]
    [InlineData("$HOME", "'$HOME'")]
    [InlineData("a\nb", "'a\nb'")]
    [InlineData("a(b)c", "'a(b)c'")]
    [InlineData("ملف.txt", "'ملف.txt'")]
    [InlineData("a\"b", "'a\"b'")]
    public void ShellQuote_NeutralisesShellSyntax(string input, string expected)
        => Assert.Equal(expected, AdbInput.ShellQuote(input));

    [Fact] // the one character single quotes cannot contain must be broken out correctly
    public void ShellQuote_EscapesEmbeddedSingleQuote()
    {
        Assert.Equal(@"'a'\''b'", AdbInput.ShellQuote("a'b"));

        // The classic break-out attempt closes the quote and appends a command; after
        // quoting, the whole thing is a single literal token again.
        var hostile = "x'; rm -rf /sdcard; echo '";
        var quoted = AdbInput.ShellQuote(hostile);
        Assert.StartsWith("'", quoted);
        Assert.EndsWith("'", quoted);
        // No bare (unescaped) single quote survives inside the body.
        Assert.DoesNotContain("'", quoted[1..^1].Replace(@"'\''", ""));
    }
}

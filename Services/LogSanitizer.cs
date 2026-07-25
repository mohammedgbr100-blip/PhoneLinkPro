using System.Text.RegularExpressions;

namespace ScrcpyDeck.Services;

/// <summary>
/// Redaction helpers used everywhere text might leave the machine (support bundles) or
/// persist (logs): device serials, user-profile paths, network addresses and long hashes
/// are masked while keeping enough shape for diagnostics (LOG-02).
/// </summary>
public static class LogSanitizer
{
    /// <summary>Masks a device serial: 3 leading characters + a stable 8-hex tag.</summary>
    public static string MaskSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return "(none)";
        var prefix = serial.Length <= 3 ? serial : serial[..3];
        return $"{prefix}…#{StableHash.Fnv1a32(serial):x8}";
    }

    // Any drive's Users\<name> segment, whatever follows.
    private static readonly Regex UserProfilePattern = new(
        @"[A-Za-z]:\\Users\\[^\\/:*?""<>|\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // IPv4 with optional port: keep the first two octets (network shape), mask the host.
    private static readonly Regex Ipv4Pattern = new(
        @"\b(\d{1,3}\.\d{1,3})\.\d{1,3}\.\d{1,3}(:\d{1,5})?\b",
        RegexOptions.Compiled);

    // 32+ hex chars (hashes): shorten. Applied before the serial rule so hex is never
    // mistaken for a serial.
    private static readonly Regex LongHexPattern = new(
        @"\b[0-9a-fA-F]{32,}\b",
        RegexOptions.Compiled);

    // Serial-shaped tokens: 9-24 alphanumerics containing BOTH a letter and a digit.
    private static readonly Regex SerialLikePattern = new(
        @"\b(?=[A-Za-z0-9]{9,24}\b)(?=[A-Za-z0-9]*\d)(?=[A-Za-z0-9]*[A-Za-z])[A-Za-z0-9]+\b",
        RegexOptions.Compiled);

    private static readonly Regex PureHexPattern = new(
        @"^[0-9a-fA-F]+$",
        RegexOptions.Compiled);

    // A 6-digit token on a pairing line is a pairing code. Production never logs codes
    // (commands are redacted), but exported HISTORICAL lines must be safe regardless.
    private static readonly Regex PairingCodePattern = new(
        @"(?i)(pair[^\r\n]*?)\b\d{6}\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Sanitizes one line/blob of text. Applied to exported logs even when the historical
    /// lines were written before redaction existed, and to global-exception stack traces.
    /// Deliberately over-masks rather than under-masks: this text is meant for support, not
    /// for forensics.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var result = text;

        // The REAL profile path first (covers subst'd/odd drives), then the generic shape.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            result = result.Replace(profile, @"C:\Users\<user>", StringComparison.OrdinalIgnoreCase);
        result = UserProfilePattern.Replace(result, @"C:\Users\<user>");

        result = PairingCodePattern.Replace(result, "$1******");

        result = Ipv4Pattern.Replace(result, "$1.*.*$2");

        result = LongHexPattern.Replace(result, m => m.Value[..8] + "…");

        result = SerialLikePattern.Replace(result, m =>
            PureHexPattern.IsMatch(m.Value) ? m.Value : MaskSerial(m.Value));

        return result;
    }
}

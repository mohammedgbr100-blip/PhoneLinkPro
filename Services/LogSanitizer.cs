using System.Text.RegularExpressions;

namespace ScrcpyDeck.Services;

/// <summary>
/// Redaction helpers used everywhere text might leave the machine (support bundles) or
/// persist (logs): device serials, user-profile paths, network addresses and long hashes
/// are masked while keeping enough shape for diagnostics (LOG-02).
/// <para>
/// PRIV-01 makes this the pre-persistence contract as well, not just the export contract.
/// Paths are masked as whole TOKENS (<see cref="MaskPathToken"/>) rather than by a free-form
/// path regex, because a user-chosen file name may contain spaces
/// ("Bank Statement.pdf") and a whitespace-terminated pattern would leak the tail.
/// </para>
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

    private const string ProfileToken = @"C:\Users\<user>";
    private const string FileToken = "<file>";
    private const string Ellipsis = "…";

    /// <summary>Extension of a path leaf (".bin"), or "" when it has none.</summary>
    private static string ExtensionOf(string leaf)
    {
        var dot = leaf.LastIndexOf('.');
        return dot > 0 && dot < leaf.Length - 1 ? leaf[dot..] : "";
    }

    /// <summary>Android storage roots whose FIRST segment is a public category, not private.</summary>
    private static readonly Regex AndroidRootPattern = new(
        @"^(?:/sdcard|/mnt/sdcard|/storage/emulated/\d+|/storage/self/primary|/storage/[A-Za-z0-9]{4}-[A-Za-z0-9]{4}|/data/media/\d+|/data/local/tmp)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Masks one whole path token. Because the token is already delimited (an argument, or a
    /// quoted/whitespace-delimited run), a file name containing spaces cannot leak a tail.
    /// <para>Idempotent: re-masking an output reproduces it exactly, which matters because the
    /// support export re-sanitizes lines that the sink already sanitized.</para>
    /// </summary>
    public static string MaskPathToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return token ?? "";

        // ---- POSIX / Android absolute path ----
        if (token[0] == '/')
        {
            var m = AndroidRootPattern.Match(token);
            var root = m.Success ? m.Value : "";
            var tail = token[root.Length..].Trim('/');
            var segments = tail.Length == 0
                ? []
                : tail.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (root.Length == 0)
                // Unknown POSIX root: keep nothing but the shape.
                return segments.Length == 0 ? "/" : $"/{Ellipsis}/{FileToken}{ExtensionOf(segments[^1])}";

            if (segments.Length == 0) return root;
            if (segments.Length == 1)
            {
                var ext1 = ExtensionOf(segments[0]);
                // A lone extension-less segment is a public category (…/Download), not a file.
                return ext1.Length == 0 ? $"{root}/{segments[0]}" : $"{root}/{FileToken}{ext1}";
            }
            return $"{root}/{segments[0]}/{FileToken}{ExtensionOf(segments[^1])}";
        }

        // ---- UNC share ----
        if (token.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var segs = token.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return segs.Length == 0
                ? @"\\"
                : $@"\\{Ellipsis}\{FileToken}{ExtensionOf(segs[^1])}";
        }

        // ---- drive-rooted Windows path ----
        if (token.Length >= 3 && char.IsLetter(token[0]) && token[1] == ':' && token[2] == '\\')
        {
            var segs = token[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0) return $"{token[..2]}\\";

            // Keep the recognisable profile token when the path is profile-rooted; the account
            // name itself is still dropped.
            var profileRooted = segs.Length >= 2 && segs[0].Equals("Users", StringComparison.OrdinalIgnoreCase);
            if (profileRooted)
                return segs.Length == 2
                    ? ProfileToken
                    : $@"{ProfileToken}\{Ellipsis}\{FileToken}{ExtensionOf(segs[^1])}";

            return segs.Length == 1
                ? $@"{token[..2]}\{FileToken}{ExtensionOf(segs[0])}"
                : $@"{token[..2]}\{Ellipsis}\{FileToken}{ExtensionOf(segs[^1])}";
        }

        return token;   // relative path, flag, package name — nothing private to drop
    }

    // A delimited run that starts like an absolute path. The leading negative lookbehind keeps
    // the match anchored to a token boundary: without it the '/' inside a RELATIVE path such as
    // "engine/SDL2.dll" would match and the diagnostic would be masked away for nothing.
    // The trailing class deliberately allows '<' and '>' so an ALREADY-masked token is matched
    // whole and re-masked to itself (idempotence).
    private static readonly Regex AbsolutePathTokenPattern = new(
        @"(?<![^\s'""(\[=,;])(?:[A-Za-z]:\\|\\\\|/)[^\s'""]*",
        RegexOptions.Compiled);

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
    /// Sanitizes one line/blob of text. Applied to every persisted log entry AND to exported
    /// logs — including historical lines written before redaction existed — and to
    /// global-exception stack traces. Deliberately over-masks rather than under-masks: this
    /// text is meant for support, not for forensics.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var result = text;

        // Whole path tokens first, so their segments are never mistaken for serials below.
        result = AbsolutePathTokenPattern.Replace(result, m => MaskPathToken(m.Value));

        result = PairingCodePattern.Replace(result, "$1******");

        result = Ipv4Pattern.Replace(result, "$1.*.*$2");

        result = LongHexPattern.Replace(result, m => m.Value[..8] + Ellipsis);

        result = SerialLikePattern.Replace(result, m =>
            PureHexPattern.IsMatch(m.Value) ? m.Value : MaskSerial(m.Value));

        return result;
    }
}

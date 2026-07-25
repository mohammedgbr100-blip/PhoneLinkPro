using System.IO;
using System.Security.Cryptography;

namespace ScrcpyDeck.Services;

/// <summary>One expected engine file: its canonical relative path and SHA-256 (lowercase hex).</summary>
internal sealed record EngineManifestEntry(string Path, string Sha256);

/// <summary>Why a manifest text could not be parsed.</summary>
internal enum EngineManifestProblem
{
    None,
    /// <summary>No text at all (missing embedded resource, empty file).</summary>
    MissingOrEmpty,
    /// <summary>The first line is not the expected header/version.</summary>
    BadHeader,
    /// <summary>A line is not "&lt;64 hex chars&gt;&lt;space&gt;&lt;path&gt;".</summary>
    MalformedEntry,
    /// <summary>An entry path is rooted, traverses upwards, or escapes engine/.</summary>
    InvalidPath,
    /// <summary>Two entries normalize to the same path.</summary>
    DuplicatePath,
}

/// <summary>
/// The engine integrity manifest format (SEC-01).
///
/// <para><b>Shape.</b> Line 1 is the fixed header <c>PLP-ENGINE-MANIFEST 1</c>; every further
/// line is <c>&lt;sha256 lowercase hex&gt; &lt;relative path&gt;</c>. The hash is fixed-length,
/// so a single space separates it unambiguously from a path that may itself contain spaces.</para>
///
/// <para><b>Determinism.</b> Rendering always produces the same bytes for the same entries:
/// entries are sorted ordinally by path, hashes are lowercased, lines end in LF, the text is
/// UTF-8 without BOM, and nothing machine- or time-dependent is ever written.</para>
///
/// <para><b>Canonical paths only.</b> An entry path must start with <c>engine/</c>, use forward
/// slashes, and contain no rooted form, no <c>.</c>/<c>..</c> segment, no drive or stream
/// colon, and no Windows-reserved character. The parser REJECTS non-canonical input rather
/// than normalizing it, so there is exactly one accepted spelling per file.</para>
/// </summary>
internal static class EngineManifest
{
    /// <summary>Manifest format version (logged as the "integrity manifest version").</summary>
    public const int Version = 1;

    public const string Header = "PLP-ENGINE-MANIFEST 1";

    /// <summary>The only directory manifest entries may point into, relative to the app base.</summary>
    public const string EngineDirName = "engine";

    private const string RequiredPrefix = EngineDirName + "/";
    private const int Sha256HexLength = 64;

    // ---------------- parsing ----------------

    /// <summary>
    /// Parses manifest text. On success returns <see cref="EngineManifestProblem.None"/> with
    /// the entries in file order; otherwise reports the first problem found.
    /// <paramref name="offender"/> carries the affected path only when it is safe to show
    /// (a path that already passed validation); malformed raw lines are never echoed back.
    /// </summary>
    public static EngineManifestProblem TryParse(
        string? text, out List<EngineManifestEntry> entries, out string? offender)
    {
        entries = new List<EngineManifestEntry>();
        offender = null;

        if (string.IsNullOrWhiteSpace(text)) return EngineManifestProblem.MissingOrEmpty;

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd('\r');

        if (lines.Length == 0 || lines[0] != Header) return EngineManifestProblem.BadHeader;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;   // tolerate the trailing newline

            if (line.Length < Sha256HexLength + 2 || line[Sha256HexLength] != ' ')
                return EngineManifestProblem.MalformedEntry;

            var hex = line[..Sha256HexLength];
            if (!IsHex(hex)) return EngineManifestProblem.MalformedEntry;

            var path = line[(Sha256HexLength + 1)..];
            if (!IsValidEntryPath(path)) return EngineManifestProblem.InvalidPath;

            if (!seen.Add(path))
            {
                offender = path;
                return EngineManifestProblem.DuplicatePath;
            }

            entries.Add(new EngineManifestEntry(path, hex.ToLowerInvariant()));
        }

        return entries.Count == 0 ? EngineManifestProblem.MissingOrEmpty : EngineManifestProblem.None;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a canonical manifest path: forward slashes, under
    /// <c>engine/</c>, no rooted form, no traversal, no colon (drives, NTFS streams), no
    /// reserved characters, no Windows-ambiguous trailing dot/space in any segment.
    /// </summary>
    public static bool IsValidEntryPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 260) return false;
        if (!path.StartsWith(RequiredPrefix, StringComparison.Ordinal)) return false;
        if (path.Length == RequiredPrefix.Length) return false;

        foreach (var ch in path)
        {
            if (ch < 0x20) return false;
            if (ch is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) return false;                    // "//" or trailing "/"
            if (segment is "." or "..") return false;                 // traversal
            if (segment[0] == ' ') return false;
            if (segment[^1] is ' ' or '.') return false;              // Windows-ambiguous names
        }

        // The prefix check above already excludes rooted paths; keep the explicit guard so
        // the rule survives any future prefix change.
        return !System.IO.Path.IsPathRooted(path);
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
                return false;
        return true;
    }

    // ---------------- rendering ----------------

    /// <summary>
    /// Renders entries to canonical manifest text (LF endings, ordinal path order, trailing
    /// newline). Throws <see cref="ArgumentException"/> on an invalid path, an invalid hash,
    /// or a duplicate path — a manifest that cannot round-trip must never be written.
    /// </summary>
    public static string Render(IReadOnlyList<EngineManifestEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (!IsValidEntryPath(e.Path))
                throw new ArgumentException($"invalid manifest path: {e.Path}");
            if (e.Sha256.Length != Sha256HexLength || !IsHex(e.Sha256))
                throw new ArgumentException($"invalid SHA-256 for: {e.Path}");
            if (!seen.Add(e.Path))
                throw new ArgumentException($"duplicate manifest path: {e.Path}");
        }

        var sorted = entries.OrderBy(e => e.Path, StringComparer.Ordinal);

        var sb = new System.Text.StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (var e in sorted)
            sb.Append(e.Sha256.ToLowerInvariant()).Append(' ').Append(e.Path).Append('\n');
        return sb.ToString();
    }

    // ---------------- hashing ----------------

    /// <summary>Streaming SHA-256 of a file, as lowercase hex. Never loads the file whole.</summary>
    public static string HashFileSha256(string fullPath)
    {
        using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 17, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

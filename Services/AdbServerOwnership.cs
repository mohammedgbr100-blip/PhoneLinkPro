using System.Diagnostics;
using System.IO;

namespace ScrcpyDeck.Services;

/// <summary>What PhoneLink Pro is allowed to do with a running ADB server at shutdown.</summary>
public enum AdbOwnershipDecision
{
    /// <summary>No ADB server of ours is running; nothing to do.</summary>
    NothingToStop,
    /// <summary>Exactly one server is running and it IS our bundled binary: safe to stop.</summary>
    OwnedByUs,
    /// <summary>A server is running but belongs to someone else (Android Studio, ...): leave it.</summary>
    NotOurs,
    /// <summary>Ownership could not be established. Fail safe: leave it running.</summary>
    Ambiguous,
}

/// <summary>One candidate ADB server process, reduced to what the decision needs.</summary>
/// <param name="ProcessId">OS process id.</param>
/// <param name="ExecutablePath">Full image path, or null when it could not be read.</param>
public sealed record AdbServerProcess(int ProcessId, string? ExecutablePath);

/// <summary>
/// Decides whether an ADB server may be stopped when PhoneLink Pro exits.
///
/// <para><b>Policy.</b> ADB is a shared, machine-wide service. Android Studio, Gradle, Flutter
/// and other tools all talk to the same server, and killing it breaks their workflow with no
/// warning. PhoneLink Pro therefore stops a server ONLY when it can prove the server process
/// is the binary it ships, running from its own engine directory.</para>
///
/// <para><b>Why not a dedicated server port.</b> Running our own server on a private port
/// (<c>adb -P</c>) would make ownership trivial, but an ADB server claims each USB device
/// exclusively: a second server cannot see a phone that the default server already holds. On
/// any machine with Android Studio running, a private port would therefore break USB
/// mirroring outright. Preserving USB and wireless functionality is the higher priority, so
/// the shared default port is kept and ownership is proven by image path instead.</para>
///
/// <para><b>Never</b> matched by executable name alone, and never applied to "all matching
/// processes": the full path must equal our own bundled bridge, and anything ambiguous is
/// left alone.</para>
/// </summary>
public static class AdbServerOwnership
{
    /// <summary>Pure decision logic, separated from process enumeration so it is testable.</summary>
    /// <param name="ourBridgePath">Full path of the bundled bridge executable.</param>
    /// <param name="candidates">Every ADB-family server process currently running.</param>
    public static AdbOwnershipDecision Decide(
        string? ourBridgePath, IReadOnlyList<AdbServerProcess> candidates)
    {
        if (string.IsNullOrWhiteSpace(ourBridgePath)) return AdbOwnershipDecision.Ambiguous;
        if (candidates.Count == 0) return AdbOwnershipDecision.NothingToStop;

        // A path we cannot read is a process we cannot vouch for.
        if (candidates.Any(c => string.IsNullOrWhiteSpace(c.ExecutablePath)))
            return AdbOwnershipDecision.Ambiguous;

        var ours = candidates.Where(c => IsSamePath(c.ExecutablePath!, ourBridgePath)).ToList();

        if (ours.Count == 0) return AdbOwnershipDecision.NotOurs;

        // Our binary is running, but so is a foreign server: stopping ours could still
        // disturb a shared workflow, and we cannot tell which one holds the devices.
        if (ours.Count != candidates.Count) return AdbOwnershipDecision.Ambiguous;

        return AdbOwnershipDecision.OwnedByUs;
    }

    /// <summary>Case-insensitive full-path comparison with normalization.</summary>
    internal static bool IsSamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enumerates running ADB-family server processes. Name matching only narrows the
    /// candidate set — the decision itself is made on the full image path.
    /// </summary>
    public static IReadOnlyList<AdbServerProcess> EnumerateCandidates()
    {
        var found = new List<AdbServerProcess>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return found; }

        foreach (var p in all)
        {
            string name;
            try { name = p.ProcessName; }
            catch { continue; }

            var isCandidate = name.Equals("adb", StringComparison.OrdinalIgnoreCase)
                              || name.Equals("PhoneLink.Bridge", StringComparison.OrdinalIgnoreCase);
            if (!isCandidate) { try { p.Dispose(); } catch { } continue; }

            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch { /* 32/64-bit or permission mismatch: leave null => Ambiguous */ }

            int id;
            try { id = p.Id; } catch { try { p.Dispose(); } catch { } continue; }

            found.Add(new AdbServerProcess(id, path));
            try { p.Dispose(); } catch { }
        }

        return found;
    }

    /// <summary>
    /// Works out what to do at shutdown and reports it. Never terminates anything itself —
    /// stopping is done by asking OUR adb to kill its own server.
    /// </summary>
    public static AdbOwnershipDecision Evaluate()
        => Decide(SafeBridgePath(), EnumerateCandidates());

    private static string? SafeBridgePath()
    {
        try { return ToolLocator.AdbPath; }
        catch { return null; }
    }
}

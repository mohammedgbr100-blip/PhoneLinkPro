using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// PROC-01 (ADB ownership) — PhoneLink Pro may stop an ADB server ONLY when it can prove the
/// server process is its own bundled binary. Android Studio's server must survive.
/// </summary>
public class AdbServerOwnershipTests
{
    private const string OurBridge = @"C:\Program Files\PhoneLink Pro\engine\PhoneLink.Bridge.exe";
    private const string StudioAdb = @"C:\Users\dev\AppData\Local\Android\Sdk\platform-tools\adb.exe";

    private static AdbServerProcess P(int id, string? path) => new(id, path);

    // ---------------------------------------------------------------- owned

    [Fact]
    public void OurOwnBundledServer_MayBeStopped()
    {
        var decision = AdbServerOwnership.Decide(OurBridge, [P(100, OurBridge)]);

        Assert.Equal(AdbOwnershipDecision.OwnedByUs, decision);
    }

    [Fact] // path comparison is case-insensitive and normalizes separators
    public void OwnershipMatchIsCaseInsensitiveAndNormalized()
    {
        var messy = @"C:\Program Files\PhoneLink Pro\engine\..\engine\PHONELINK.BRIDGE.EXE";

        Assert.Equal(AdbOwnershipDecision.OwnedByUs,
            AdbServerOwnership.Decide(OurBridge, [P(100, messy)]));
    }

    // ---------------------------------------------------------------- not ours

    [Fact] // the whole point: Android Studio's server must not be touched
    public void AndroidStudioServer_MustNotBeStopped()
    {
        var decision = AdbServerOwnership.Decide(OurBridge, [P(200, StudioAdb)]);

        Assert.Equal(AdbOwnershipDecision.NotOurs, decision);
    }

    [Fact] // a same-named binary from another location is NOT ours
    public void ServerOutsideOurEngineDirectory_MustNotBeStopped()
    {
        var impostor = @"C:\Temp\other\engine\PhoneLink.Bridge.exe";

        var decision = AdbServerOwnership.Decide(OurBridge, [P(300, impostor)]);

        Assert.Equal(AdbOwnershipDecision.NotOurs, decision);
    }

    [Fact] // a second PhoneLink install is a different product instance
    public void BridgeFromADifferentInstallation_IsNotOurs()
    {
        var otherInstall = @"D:\PhoneLink Pro\engine\PhoneLink.Bridge.exe";

        Assert.Equal(AdbOwnershipDecision.NotOurs,
            AdbServerOwnership.Decide(OurBridge, [P(400, otherInstall)]));
    }

    // ---------------------------------------------------------------- ambiguous: fail safe

    [Fact] // an unreadable path means we cannot vouch for the process
    public void UnreadableProcessPath_FailsSafe()
    {
        Assert.Equal(AdbOwnershipDecision.Ambiguous,
            AdbServerOwnership.Decide(OurBridge, [P(500, null)]));

        Assert.Equal(AdbOwnershipDecision.Ambiguous,
            AdbServerOwnership.Decide(OurBridge, [P(500, "   ")]));
    }

    [Fact] // ours AND a foreign server running together: leave both alone
    public void MixedOwnership_FailsSafe()
    {
        var decision = AdbServerOwnership.Decide(OurBridge, [P(100, OurBridge), P(200, StudioAdb)]);

        Assert.Equal(AdbOwnershipDecision.Ambiguous, decision);
    }

    [Fact] // our binary plus an unreadable process is still ambiguous
    public void OursPlusUnreadable_FailsSafe()
        => Assert.Equal(AdbOwnershipDecision.Ambiguous,
            AdbServerOwnership.Decide(OurBridge, [P(100, OurBridge), P(200, null)]));

    [Fact]
    public void UnknownOwnBridgePath_FailsSafe()
    {
        Assert.Equal(AdbOwnershipDecision.Ambiguous,
            AdbServerOwnership.Decide(null, [P(100, OurBridge)]));

        Assert.Equal(AdbOwnershipDecision.Ambiguous,
            AdbServerOwnership.Decide("  ", [P(100, OurBridge)]));
    }

    // ---------------------------------------------------------------- nothing running

    [Fact]
    public void NoServerRunning_MeansNothingToStop()
        => Assert.Equal(AdbOwnershipDecision.NothingToStop,
            AdbServerOwnership.Decide(OurBridge, []));

    // ---------------------------------------------------------------- policy invariants

    [Fact] // multiple copies of OUR exact binary are all ours
    public void MultipleInstancesOfOurBinary_AreOwned()
        => Assert.Equal(AdbOwnershipDecision.OwnedByUs,
            AdbServerOwnership.Decide(OurBridge, [P(1, OurBridge), P(2, OurBridge)]));

    [Fact] // name alone must never authorize a kill
    public void MatchingNameButDifferentPath_IsNeverOwned()
    {
        // Same file NAME as our bridge, different directory.
        var sameName = @"C:\Somewhere\Else\PhoneLink.Bridge.exe";

        var decision = AdbServerOwnership.Decide(OurBridge, [P(1, sameName)]);

        Assert.NotEqual(AdbOwnershipDecision.OwnedByUs, decision);
    }

    [Fact] // plain "adb.exe" is never assumed to be ours
    public void PlainAdbExe_IsNeverOwned()
        => Assert.NotEqual(AdbOwnershipDecision.OwnedByUs,
            AdbServerOwnership.Decide(OurBridge, [P(1, @"C:\tools\adb.exe")]));

    // ---------------------------------------------------------------- real enumeration

    [Fact] // enumeration must be safe on a machine with no adb at all
    public void EnumerateCandidates_DoesNotThrow()
    {
        var ex = Record.Exception(() => AdbServerOwnership.EnumerateCandidates());

        Assert.Null(ex);
    }

    [Fact] // the live evaluation must never throw, whatever is running
    public void Evaluate_DoesNotThrow()
    {
        var ex = Record.Exception(() => AdbServerOwnership.Evaluate());

        Assert.Null(ex);
    }
}

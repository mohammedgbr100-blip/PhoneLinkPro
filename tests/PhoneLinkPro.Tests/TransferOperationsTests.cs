using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 4D — pure logic for the guided file-transfers page: remote (Android) path validation,
/// standard destination construction, device-selection policy, safe classification of adb
/// push/pull results into narrow <see cref="FileTransferResult"/> values, and batch aggregation.
/// No adb process is ever started by these tests — every result is a hand-built
/// <see cref="ProcessResult"/> fixture.
/// </summary>
public sealed class TransferOperationsTests
{
    // ---------------------------------------------------------------- remote path: acceptance

    [Theory]
    [InlineData("/sdcard/Download/file.txt")]
    [InlineData("/sdcard/DCIM/Camera/photo.jpg")]
    [InlineData("/storage/emulated/0/Documents/report.pdf")]
    public void TryValidateRemotePath_AcceptsSharedStorageFilePaths(string path)
    {
        Assert.True(TransferOperations.TryValidateRemotePath(path, out var normalized, out _));
        Assert.Equal(path, normalized);
    }

    [Fact] // Unicode file/folder names must be accepted
    public void TryValidateRemotePath_AcceptsUnicodeNames()
        => Assert.True(TransferOperations.TryValidateRemotePath("/sdcard/Download/صورة عائلية 家族.jpg", out _, out _));

    [Fact] // spaces in a path must be accepted — no ArgumentList quoting bug possible
    public void TryValidateRemotePath_AcceptsSpaces()
        => Assert.True(TransferOperations.TryValidateRemotePath("/sdcard/Download/my file name.txt", out _, out _));

    [Fact] // only surrounding whitespace is trimmed, never internal content
    public void TryValidateRemotePath_TrimsOnlySurroundingWhitespace()
    {
        Assert.True(TransferOperations.TryValidateRemotePath("  /sdcard/Download/file.txt  ", out var normalized, out _));
        Assert.Equal("/sdcard/Download/file.txt", normalized);
    }

    // ---------------------------------------------------------------- remote path: shared-storage root rules

    [Theory]
    [InlineData("/data/local/tmp/file.txt")]
    [InlineData("/system/file.txt")]
    [InlineData("/root/file.txt")]
    public void TryValidateRemotePath_RejectsPathsOutsideApprovedRoots(string path)
        => Assert.False(TransferOperations.TryValidateRemotePath(path, out _, out _));

    [Theory]
    [InlineData("/sdcard/")]
    [InlineData("/sdcard")]
    [InlineData("/storage/emulated/0/")]
    [InlineData("/storage/emulated/0")]
    public void TryValidateRemotePath_RejectsTheBareRootWithNoFileSegment(string path)
        => Assert.False(TransferOperations.TryValidateRemotePath(path, out _, out _));

    // ---------------------------------------------------------------- remote path: traversal rejection

    [Theory]
    [InlineData("/sdcard/Download/../../../etc/passwd")]
    [InlineData("/sdcard/../data/local/tmp/x")]
    [InlineData("/sdcard/Download/..")]
    public void TryValidateRemotePath_RejectsTraversalSegments(string path)
        => Assert.False(TransferOperations.TryValidateRemotePath(path, out _, out _));

    [Fact] // a real file/folder legitimately named "..foo" (not a bare ".." segment) is untouched
    public void TryValidateRemotePath_DoesNotFalsePositiveOnDotDotPrefixedNames()
        => Assert.True(TransferOperations.TryValidateRemotePath("/sdcard/Download/..hidden-report.txt", out _, out _));

    // ---------------------------------------------------------------- remote path: control characters

    [Theory]
    [InlineData("/sdcard/Download/file\0name.txt")]      // embedded NUL
    [InlineData("/sdcard/Download/file\r\nname.txt")]     // embedded CR/LF
    [InlineData("/sdcard/Download/file\tname.txt")]       // embedded tab (control)
    public void TryValidateRemotePath_RejectsControlCharacters(string path)
        => Assert.False(TransferOperations.TryValidateRemotePath(path, out _, out _));

    // ---------------------------------------------------------------- remote path: URI schemes / Windows paths / relative

    [Theory]
    [InlineData("content://media/external/file")]
    [InlineData("file:///sdcard/Download/x.txt")]
    public void TryValidateRemotePath_RejectsUriSchemes(string path)
        => Assert.False(TransferOperations.TryValidateRemotePath(path, out _, out _));

    [Theory]
    [InlineData(@"C:\Users\test\file.txt")]
    [InlineData("C:/Users/test/file.txt")]
    [InlineData(@"D:\file.txt")]
    public void TryValidateRemotePath_RejectsWindowsDrivePaths(string path)
        => Assert.False(TransferOperations.TryValidateRemotePath(path, out _, out _));

    [Theory]
    [InlineData("sdcard/Download/file.txt")]     // relative — no leading slash
    [InlineData("Download/file.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidateRemotePath_RejectsRelativeOrEmpty(string path)
        => Assert.False(TransferOperations.TryValidateRemotePath(path, out _, out _));

    [Theory] // shell metacharacters are rejected everywhere in the path, not merely at the start
    [InlineData("/sdcard/Download/a;b.txt")]
    [InlineData("/sdcard/Download/a|b.txt")]
    [InlineData("/sdcard/Download/a&b.txt")]
    [InlineData("/sdcard/Download/a$b.txt")]
    [InlineData("/sdcard/Download/a`b.txt")]
    [InlineData("/sdcard/Download/a<b.txt")]
    [InlineData("/sdcard/Download/a>b.txt")]
    [InlineData(@"/sdcard/Download/a\b.txt")]
    public void TryValidateRemotePath_RejectsShellMetacharactersAnywhereInThePath(string path)
        => Assert.False(TransferOperations.TryValidateRemotePath(path, out _, out _));

    [Fact] // a path that BEGINS with a shell operator is rejected the same way (no root match anyway)
    public void TryValidateRemotePath_RejectsPathBeginningWithShellOperator()
        => Assert.False(TransferOperations.TryValidateRemotePath(";/sdcard/Download/file.txt", out _, out _));

    // ---------------------------------------------------------------- local path (pure)

    [Fact]
    public void TryValidateLocalSendPath_RejectsEmpty()
        => Assert.False(TransferOperations.TryValidateLocalSendPath("", out _));

    [Fact]
    public void TryValidateLocalSendPath_RejectsRelativePaths()
        => Assert.False(TransferOperations.TryValidateLocalSendPath("relative\\path.txt", out _));

    [Fact]
    public void TryValidateLocalSendPath_AcceptsAnOrdinaryAbsoluteWindowsPath()
        => Assert.True(TransferOperations.TryValidateLocalSendPath(@"C:\Users\test\file.txt", out _));

    [Fact]
    public void TryValidateLocalSendPath_RejectsControlCharacters()
        => Assert.False(TransferOperations.TryValidateLocalSendPath("C:\\Users\\test\\file\0.txt", out _));

    // ---------------------------------------------------------------- safe file name / redaction

    [Fact] // never the full path — only the leaf name
    public void SafeFileName_NeverContainsTheDirectoryPortion()
    {
        var name = TransferOperations.SafeFileName(@"C:\Users\SECRETUSER\Documents\report.pdf");
        Assert.Equal("report.pdf", name);
        Assert.DoesNotContain("SECRETUSER", name);
        Assert.DoesNotContain(@"\", name);
    }

    [Fact]
    public void SafeFileName_PreservesUnicodeAndSpaces()
        => Assert.Equal("صورة عائلية 家族.jpg", TransferOperations.SafeFileName(@"C:\temp\صورة عائلية 家族.jpg"));

    [Fact] // suggested local file name is sanitized, never assumed already legal
    public void SuggestLocalFileName_SanitizesIllegalWindowsCharacters()
    {
        var suggested = TransferOperations.SuggestLocalFileName("/sdcard/Download/weird:name?.txt");
        Assert.DoesNotContain(':', suggested);
        Assert.DoesNotContain('?', suggested);
    }

    [Fact]
    public void SuggestLocalFileName_PreservesUnicodeNames()
        => Assert.Equal("صورة.jpg", TransferOperations.SuggestLocalFileName("/sdcard/DCIM/صورة.jpg"));

    [Fact]
    public void SuggestLocalFileName_NeverEmptyForATrailingSlashPath()
        => Assert.NotEqual("", TransferOperations.SuggestLocalFileName("/sdcard/Download/"));

    // ---------------------------------------------------------------- standard destinations

    [Theory]
    [InlineData(StandardRemoteDestination.Download)]
    [InlineData(StandardRemoteDestination.Documents)]
    [InlineData(StandardRemoteDestination.Pictures)]
    [InlineData(StandardRemoteDestination.Movies)]
    [InlineData(StandardRemoteDestination.Music)]
    public void StandardDestinationPaths_AreThemselvesValidRemotePaths(StandardRemoteDestination dest)
    {
        // Constructed from internal constants, but must still obey the same contract a custom
        // destination is validated against — plus a trailing file segment for a real transfer.
        var path = TransferOperations.StandardDestinationPaths[dest] + "/file.txt";
        Assert.True(TransferOperations.TryValidateRemotePath(path, out _, out _));
    }

    [Fact] // every standard destination has an approved Arabic label
    public void StandardDestinationLabels_CoverEveryDestination()
    {
        foreach (StandardRemoteDestination dest in Enum.GetValues<StandardRemoteDestination>())
            Assert.False(string.IsNullOrWhiteSpace(TransferOperations.StandardDestinationLabels[dest]));
    }

    [Fact] // standard destinations are distinct paths — never accidentally aliased
    public void StandardDestinationPaths_AreAllDistinct()
    {
        var paths = TransferOperations.StandardDestinationPaths.Values.ToList();
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.Ordinal).Count());
    }

    // ---------------------------------------------------------------- device selection

    [Fact]
    public void DecideDeviceSelection_NoDevices_ReturnsNull()
        => Assert.Null(TransferOperations.DecideDeviceSelection(new List<AdbDevice>(), null));

    [Fact]
    public void DecideDeviceSelection_SingleDevice_IsAutoSelected()
    {
        var dev = new AdbDevice { Serial = "R5CX10A", State = "device" };
        Assert.Equal(dev.Serial, TransferOperations.DecideDeviceSelection(new List<AdbDevice> { dev }, null));
    }

    [Fact] // multiple devices are never silently chosen for the user
    public void DecideDeviceSelection_MultipleDevices_NoPriorSelection_ReturnsNull()
    {
        var devices = new List<AdbDevice>
        {
            new() { Serial = "R5CX10A", State = "device" },
            new() { Serial = "R5CX10B", State = "device" },
        };
        Assert.Null(TransferOperations.DecideDeviceSelection(devices, null));
    }

    [Fact] // an existing selection is preserved by ordinal serial match across a refreshed snapshot
    public void DecideDeviceSelection_PreservesAnExistingSelection_ByOrdinalSerial()
    {
        var devices = new List<AdbDevice>
        {
            new() { Serial = "R5CX10A", State = "device" },
            new() { Serial = "R5CX10B", State = "device" },
        };
        Assert.Equal("R5CX10B", TransferOperations.DecideDeviceSelection(devices, "R5CX10B"));
    }

    [Fact] // a selection whose device vanished from the snapshot is dropped, never carried over
    public void DecideDeviceSelection_DropsASelectionThatNoLongerExists()
    {
        var devices = new List<AdbDevice>
        {
            new() { Serial = "R5CX10A", State = "device" },
            new() { Serial = "R5CX10B", State = "device" },
        };
        Assert.Null(TransferOperations.DecideDeviceSelection(devices, "R5CX10VANISHED"));
    }

    [Fact] // ordering has no effect on which serial is preserved
    public void DecideDeviceSelection_IsStableAcrossReordering()
    {
        var a = new AdbDevice { Serial = "R5CX10A", State = "device" };
        var b = new AdbDevice { Serial = "R5CX10B", State = "device" };
        Assert.Equal("R5CX10B", TransferOperations.DecideDeviceSelection(new List<AdbDevice> { b, a }, "R5CX10B"));
        Assert.Equal("R5CX10B", TransferOperations.DecideDeviceSelection(new List<AdbDevice> { a, b }, "R5CX10B"));
    }

    [Fact] // a replacement object with the same serial preserves the selection
    public void DecideDeviceSelection_SurvivesAReplacementObjectWithTheSameSerial()
    {
        var devices = new List<AdbDevice> { new() { Serial = "R5CX10A", State = "unauthorized" } };
        Assert.Equal("R5CX10A", TransferOperations.DecideDeviceSelection(devices, "R5CX10A"));
    }

    // ---------------------------------------------------------------- collision-safe display identity reuse

    [Fact] // Batch 4D reuses WirelessDeviceDisplay.BuildRows unchanged — it is not wireless-specific
    public void BuildRows_DisambiguatesCollidingDevicesForTransfersToo()
    {
        var a = new AdbDevice { Serial = "ABCD1234EF", State = "device", Model = "Galaxy S24" };
        var b = new AdbDevice { Serial = "WXYZ5678QR", State = "device", Model = "Galaxy S24" };
        // Both are USB-style serials with identical MaskSerial output potential is unlikely, so
        // use the exact reviewed wireless collision pair instead — the point is the function
        // works for ANY AdbDevice list, wireless or not.
        var wa = new AdbDevice { Serial = "192.168.1.42:5555", State = "device", Model = "Galaxy S24" };
        var wb = new AdbDevice { Serial = "192.168.1.55:5555", State = "device", Model = "Galaxy S24" };

        var rows = WirelessDeviceDisplay.BuildRows(new List<AdbDevice> { wa, wb });
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].DisambiguatorToken);
        Assert.NotNull(rows[1].DisambiguatorToken);
        Assert.NotEqual(rows[0].DisambiguatorToken, rows[1].DisambiguatorToken);

        // sanity: a and b above are not actually colliding (different masks) — proves BuildRows
        // does not over-trigger disambiguation for genuinely distinct USB serials either.
        var plainRows = WirelessDeviceDisplay.BuildRows(new List<AdbDevice> { a, b });
        Assert.All(plainRows, r => Assert.Null(r.DisambiguatorToken));
    }

    // ---------------------------------------------------------------- ArgumentList / no shell-string construction

    [Fact] // local and remote are always separate ArgumentList elements, never concatenated
    public void PushArgs_KeepsLocalAndRemoteAsSeparateArguments()
    {
        var args = AdbService.PushArgs(@"C:\Users\test\my file.txt", "/sdcard/Download");
        Assert.Equal(3, args.Count);
        Assert.Equal("push", args[0]);
        Assert.Equal(@"C:\Users\test\my file.txt", args[1]);
        Assert.Equal("/sdcard/Download", args[2]);
    }

    [Fact]
    public void PullArgs_KeepsRemoteAndLocalAsSeparateArguments()
    {
        var args = AdbService.PullArgs("/sdcard/DCIM/a b.jpg", @"C:\out\a b.jpg");
        Assert.Equal(3, args.Count);
        Assert.Equal("pull", args[0]);
        Assert.Equal("/sdcard/DCIM/a b.jpg", args[1]);
        Assert.Equal(@"C:\out\a b.jpg", args[2]);
    }

    [Fact] // the device serial is its own separate argument too, via WithSerialArgs
    public void WithSerialArgs_KeepsSerialSeparateFromPushArguments()
    {
        var pushArgs = AdbService.PushArgs("local.txt", "/sdcard/Download");
        var full = AdbService.WithSerialArgs("192.168.1.42:5555", pushArgs);
        Assert.Equal(new[] { "-s", "192.168.1.42:5555", "push", "local.txt", "/sdcard/Download" }, full);
    }

    // ---------------------------------------------------------------- classification: exit code requirement

    [Fact]
    public void ClassifyPush_ExitZero_RecognizedSuccessText_IsSuccess()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped. 5 MB/s (100 bytes in 0.02s)", "");
        Assert.True(TransferOperations.ClassifyPush(r).Success);
    }

    [Fact] // a non-zero exit is always a failure, even with a "100%"-looking positive line
    public void ClassifyPush_NonZeroExit_WithPositiveLookingText_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 1, "[100%] 1 file pushed, 0 skipped.", "");
        Assert.False(TransferOperations.ClassifyPush(r).Success);
    }

    [Fact]
    public void ClassifyPull_ExitZero_RecognizedSuccessText_IsSuccess()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "1 file pulled, 0 skipped.", "");
        Assert.True(TransferOperations.ClassifyPull(r).Success);
    }

    [Fact]
    public void ClassifyPull_NonZeroExit_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 1, "1 file pulled, 0 skipped.", "");
        Assert.False(TransferOperations.ClassifyPull(r).Success);
    }

    // ---------------------------------------------------------------- classification: negative-before-positive

    [Fact] // a genuine failure whose real text landed on stderr must never be read as a success
    public void ClassifyPush_PositiveStdout_NegativeStderr_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped.", "adb: error: failed to copy");
        Assert.False(TransferOperations.ClassifyPush(r).Success);
    }

    [Fact]
    public void ClassifyPull_PositiveStdout_NegativeStderr_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "1 file pulled, 0 skipped.", "adb: error: no such file or directory");
        Assert.False(TransferOperations.ClassifyPull(r).Success);
    }

    [Fact]
    public void ClassifyPush_SingleStreamCarriesBothMarkers_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "1 file pushed, 0 skipped. error: permission denied", "");
        Assert.False(TransferOperations.ClassifyPush(r).Success);
    }

    [Fact] // "0 files pushed" is itself a negative marker even though it contains "pushed"
    public void ClassifyPush_ZeroFilesPushed_IsFailure()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "0 files pushed, 1 skipped.", "");
        Assert.False(TransferOperations.ClassifyPush(r).Success);
    }

    // ---------------------------------------------------------------- classification: unknown fails closed

    [Fact]
    public void ClassifyPush_ExitZero_EmptyOutput_FailsClosed()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "", "");
        Assert.False(TransferOperations.ClassifyPush(r).Success);
    }

    [Fact]
    public void ClassifyPull_ExitZero_UnrecognizedOutput_FailsClosed()
    {
        var r = new ProcessResult(ProcessOutcome.Exited, 0, "some unexpected diagnostic text", "");
        Assert.False(TransferOperations.ClassifyPull(r).Success);
    }

    // ---------------------------------------------------------------- classification: other outcomes / privacy

    [Fact]
    public void ClassifyPush_StartFailed_IsToolsUnavailable_AndNeverLeaksDiagnostic()
    {
        var r = new ProcessResult(ProcessOutcome.StartFailed, null, "", "", FailureDetail: "engine integrity: Invalid x");
        var result = TransferOperations.ClassifyPush(r);
        Assert.Equal(FileTransferOutcome.ToolsUnavailable, result.Outcome);
        Assert.DoesNotContain("Invalid", result.Message);
        Assert.DoesNotContain("engine integrity", result.Message);
    }

    [Fact]
    public void ClassifyPush_Cancelled_IsCancelledNotSuccess()
    {
        var r = new ProcessResult(ProcessOutcome.Cancelled, null, "", "");
        var result = TransferOperations.ClassifyPush(r);
        Assert.Equal(FileTransferOutcome.Cancelled, result.Outcome);
        Assert.False(result.Success);
    }

    [Fact]
    public void ClassifyPull_TimedOut_IsFailedNotSuccess()
    {
        var r = new ProcessResult(ProcessOutcome.TimedOut, null, "", "");
        var result = TransferOperations.ClassifyPull(r);
        Assert.Equal(FileTransferOutcome.Failed, result.Outcome);
    }

    [Fact] // never forwards raw stdout/stderr into the message, on failure or success
    public void ClassifyPush_NeverIncludesRawAdbOutput()
    {
        const string rawDiagnostic = "SECRET-DIAGNOSTIC-TEXT";
        var success = new ProcessResult(ProcessOutcome.Exited, 0, $"1 file pushed. {rawDiagnostic}", "");
        var failure = new ProcessResult(ProcessOutcome.Exited, 1, "", rawDiagnostic);

        Assert.DoesNotContain(rawDiagnostic, TransferOperations.ClassifyPush(success).Message);
        Assert.DoesNotContain(rawDiagnostic, TransferOperations.ClassifyPush(failure).Message);
    }

    [Fact]
    public void ClassifyPush_InvalidInput_CarriesOnlyTheValidationReason()
    {
        var r = ProcessResult.Invalid("المسار غير صالح.");
        var result = TransferOperations.ClassifyPush(r);
        Assert.Equal(FileTransferOutcome.InvalidInput, result.Outcome);
        Assert.Equal("المسار غير صالح.", result.Message);
    }

    // ---------------------------------------------------------------- batch / cancellation aggregation

    [Fact]
    public void SummarizeBatch_AllSucceeded_ReportsFullCount()
    {
        var items = new List<TransferItemSummary>
        {
            new(FileTransferDirection.Send, "a.txt", true, "ok"),
            new(FileTransferDirection.Send, "b.txt", true, "ok"),
        };
        var (ok, failed) = TransferOperations.SummarizeBatch(items);
        Assert.Equal(2, ok);
        Assert.Equal(0, failed);
    }

    [Fact] // partial success: never claims the full count succeeded
    public void SummarizeBatch_PartialSuccess_ReportsBothCountsTruthfully()
    {
        var items = new List<TransferItemSummary>
        {
            new(FileTransferDirection.Send, "a.txt", true, "ok"),
            new(FileTransferDirection.Send, "b.txt", false, "failed"),
            new(FileTransferDirection.Send, "c.txt", true, "ok"),
        };
        var (ok, failed) = TransferOperations.SummarizeBatch(items);
        Assert.Equal(2, ok);
        Assert.Equal(1, failed);
        Assert.NotEqual(items.Count, ok);   // the specific defect this guards against
    }

    [Fact] // cancellation: completed items before the cancellation point remain counted, not rolled back
    public void SummarizeBatch_AfterCancellation_CompletedItemsRemainCounted()
    {
        var items = new List<TransferItemSummary>
        {
            new(FileTransferDirection.Send, "done-before-cancel.txt", true, "ok"),
            new(FileTransferDirection.Send, "cancelled.txt", false, "تم إلغاء عملية النقل."),
        };
        var (ok, failed) = TransferOperations.SummarizeBatch(items);
        Assert.Equal(1, ok);
        Assert.Equal(1, failed);
    }

    [Fact]
    public void SummarizeBatch_Empty_ReportsZeroZero()
    {
        var (ok, failed) = TransferOperations.SummarizeBatch(new List<TransferItemSummary>());
        Assert.Equal(0, ok);
        Assert.Equal(0, failed);
    }
}

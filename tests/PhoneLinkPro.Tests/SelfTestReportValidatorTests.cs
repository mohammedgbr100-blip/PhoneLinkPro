using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Repair batch 5B (§5) — release.ps1's exact, unique-suite PLP_SELFTEST report validator
/// (SelfTestReportValidator.ps1). Genuinely BEHAVIORAL: each test spawns a real PowerShell
/// subprocess that dot-sources the actual validator file and calls Test-SelfTestReport against
/// sample report text, exactly like release.ps1 itself does — never a source-text guess at what
/// the script "probably" does. Never executes any build/publish/signing step.
/// </summary>
public sealed class SelfTestReportValidatorTests
{
    // Mirrors the real, accepted PLP_SELFTEST report shape and exact per-suite totals documented
    // in SelfTestReportValidator.ps1 (dashboard-ordering grew 42 -> 55 in this batch; every other
    // suite is unchanged).
    private const string ValidReport =
        "OK: all windows constructed\n" +
        "OK: design: 6/6 runtime assertions passed\n" +
        "OK: fit: 6/6 reference displays fit inside the work area\n" +
        "OK: lifecycle: 25/25 subscription assertions passed\n" +
        "OK: dashboard: 14/14 lifecycle assertions passed\n" +
        "OK: dashboard-stale: 19/19 guard assertions passed\n" +
        "OK: dashboard-ordering: 55/55 ordering assertions passed\n" +
        "OK: dashboard-ordering-stress: 1100/1100 assertions passed over 50/50 attempted iterations\n" +
        "OK: dashboard-visual: 89/89 visual assertions passed\n" +
        "OK: setup-wizard: 107/107 assertions passed\n" +
        "OK: wireless: 105/105 assertions passed\n" +
        "OK: transfers: 179/179 assertions passed\n" +
        "OK: settings: 144/144 assertions passed\n" +
        "OK: about: 84/84 assertions passed\n";

    private const int ExpectedTotal = 6 + 6 + 25 + 14 + 19 + 55 + 89 + 107 + 105 + 179 + 144 + 84 + 1100;

    [Fact]
    public void ExactValidReport_IsAccepted()
    {
        var result = Validate(ValidReport);
        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(ExpectedTotal, result.Total);
    }

    [Fact] // a suite missing entirely must fail, even though every OTHER suite is exact
    public void MissingDesignSuite_IsRejected()
    {
        var report = RemoveLine(ValidReport, "design:");
        var result = Validate(report);

        Assert.False(result.IsValid);
        Assert.Contains("missing", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("design", result.Reason);
    }

    [Fact] // a duplicated suite line must fail, never silently overwrite/accumulate
    public void DuplicateDesignSuite_IsRejected()
    {
        var report = ValidReport + "OK: design: 6/6 runtime assertions passed\n";
        var result = Validate(report);

        Assert.False(result.IsValid);
        Assert.Contains("duplicate", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("design", result.Reason);
    }

    [Fact] // [REGRESSION] the exact scenario the previous sum-based check could not catch: a
           // duplicated suite plus an omitted suite with a compensating denominator
    public void DuplicateDesignPlusMissingFit_IsRejected()
    {
        var report = RemoveLine(ValidReport, "fit:") + "OK: design: 6/6 runtime assertions passed\n";
        var result = Validate(report);

        Assert.False(result.IsValid);
        // Whichever the validator finds first (the duplicate, scanned top-to-bottom, comes
        // before the missing-suite pass) - either way the report must be rejected.
        Assert.True(
            result.Reason.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
            result.Reason.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // a self-consistent (numerator == denominator) suite whose denominator does not match
           // the required exact value must still fail
    public void WrongSuiteDenominator_IsRejected()
    {
        var report = ReplaceLine(ValidReport,
            "OK: dashboard-ordering: 55/55 ordering assertions passed",
            "OK: dashboard-ordering: 41/41 ordering assertions passed");
        var result = Validate(report);

        Assert.False(result.IsValid);
        Assert.Contains("dashboard-ordering", result.Reason);
    }

    [Fact] // the same overall total, reached via the wrong per-suite distribution, must fail —
           // proves the check is per-suite, not merely a running sum
    public void CorrectTotalButWrongDistribution_IsRejected()
    {
        // Moves one assertion from 'about' to 'design': total is still ExpectedTotal, but neither
        // suite matches its required exact denominator any more.
        var report = ReplaceLine(ValidReport,
            "OK: design: 6/6 runtime assertions passed", "OK: design: 7/7 runtime assertions passed");
        report = ReplaceLine(report,
            "OK: about: 84/84 assertions passed", "OK: about: 83/83 assertions passed");
        var result = Validate(report);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void UnknownSuite_IsRejected()
    {
        var report = ValidReport + "OK: mystery-suite: 3/3 something passed\n";
        var result = Validate(report);

        Assert.False(result.IsValid);
        Assert.Contains("unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mystery-suite", result.Reason);
    }

    [Fact]
    public void MalformedLine_IsRejected()
    {
        var report = ReplaceLine(ValidReport,
            "OK: design: 6/6 runtime assertions passed", "OK: design: six/6 runtime assertions passed");
        var result = Validate(report);

        Assert.False(result.IsValid);
        Assert.Contains("malformed", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailLine_IsRejected()
    {
        var report = ReplaceLine(ValidReport,
            "OK: design: 6/6 runtime assertions passed", "FAIL: design: 5/6 runtime assertions passed");
        var result = Validate(report);

        Assert.False(result.IsValid);
        Assert.Contains("FAIL", result.Reason);
    }

    [Fact] // a duplicated dashboard-ordering line specifically (the exact suite this batch grew)
    public void DuplicateOrderingLine_IsRejected()
    {
        var report = ValidReport + "OK: dashboard-ordering: 55/55 ordering assertions passed\n";
        var result = Validate(report);

        Assert.False(result.IsValid);
        Assert.Contains("dashboard-ordering", result.Reason);
    }

    [Fact]
    public void MissingStressLine_IsRejected()
    {
        var report = RemoveLine(ValidReport, "dashboard-ordering-stress:");
        var result = Validate(report);

        Assert.False(result.IsValid);
        Assert.Contains("dashboard-ordering-stress", result.Reason);
    }

    [Fact] // the whole report repeated verbatim (every suite duplicated) must still fail
    public void RepeatedEntireReport_IsRejected()
    {
        var report = ValidReport + ValidReport;
        var result = Validate(report);

        Assert.False(result.IsValid);
        Assert.Contains("duplicate", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("OK: design: 0/0 runtime assertions passed")]  // zero
    [InlineData("OK: design: -6/-6 runtime assertions passed")] // negative
    [InlineData("OK: design: many/many runtime assertions passed")] // nonnumeric
    public void ZeroNegativeOrNonNumericTotals_AreRejected(string replacementLine)
    {
        var report = ReplaceLine(ValidReport, "OK: design: 6/6 runtime assertions passed", replacementLine);
        var result = Validate(report);

        Assert.False(result.IsValid);
    }

    // ---------------------------------------------------------------- subprocess harness

    private static (bool IsValid, string Reason, int Total) Validate(string report)
    {
        var scriptPath = Path.Combine(TestPaths.RepoRoot(), "SelfTestReportValidator.ps1");
        Assert.True(File.Exists(scriptPath), $"validator script not found at '{scriptPath}'");

        // Base64 round-trip so embedded newlines/quotes in the sample report never need escaping
        // through a process argument list.
        var encodedReport = Convert.ToBase64String(Encoding.UTF8.GetBytes(report));
        var command =
            $". '{scriptPath.Replace("'", "''")}'; " +
            $"$bytes = [Convert]::FromBase64String('{encodedReport}'); " +
            "$reportText = [System.Text.Encoding]::UTF8.GetString($bytes); " +
            "(Test-SelfTestReport -Report $reportText) | ConvertTo-Json -Compress";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        var exited = process.WaitForExit(30000);
        if (!exited) { try { process.Kill(entireProcessTree: true); } catch { } }
        Assert.True(exited, "validator subprocess did not exit within the bounded timeout");
        Assert.True(process.ExitCode == 0, $"validator subprocess failed (exit {process.ExitCode}): {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        return (
            root.GetProperty("IsValid").GetBoolean(),
            root.GetProperty("Reason").GetString() ?? "",
            root.GetProperty("Total").GetInt32());
    }

    private static string RemoveLine(string report, string containing)
        => string.Join('\n', report.Split('\n').Where(l => !l.Contains(containing, StringComparison.Ordinal)));

    private static string ReplaceLine(string report, string from, string to)
    {
        Assert.Contains(from, report, StringComparison.Ordinal);
        return report.Replace(from, to, StringComparison.Ordinal);
    }
}

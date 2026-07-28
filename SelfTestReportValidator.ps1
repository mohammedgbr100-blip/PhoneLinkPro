# ============================================================
#  PhoneLink Pro - exact, unique-suite PLP_SELFTEST report validator (repair batch 5B, §5)
#
#  Dot-sourced by release.ps1 so the release gate and this file share ONE definition of the
#  required suites and ONE validation function — never two copies that could silently drift.
#  Kept in its own file (rather than inline in release.ps1) so it can also be dot-sourced by a
#  lightweight PowerShell subprocess in tests, without running any build/publish/signing step.
#
#  Previous weakness: release.ps1 summed every matching suite line's DENOMINATOR into one
#  running total and only checked that total against 1920. A duplicated suite line and an
#  omitted suite line with the same denominator could theoretically cancel out and still sum to
#  the expected total, and nothing distinguished "this suite" from "any suite" at all. This
#  replaces that sum with an exact dictionary keyed by suite name: every required suite must
#  appear EXACTLY once, with its numerator equal to its denominator, and its denominator equal to
#  the one documented value below - no duplicate, no omission, no substitution can pass.
# ============================================================

# Repair batch 5B arithmetic (see the batch report for the full per-suite breakdown): the
# dashboard-ordering suite grew from 42 to 55 (13 new, real, deterministic assertions - Scenarios
# G/H prove a throwing OperationSettledForTests test hook can never mask the real operation's own
# failure, block busy release, or block a successful operation's own queue drain). Every other
# suite is unchanged from the accepted Batch 5A baseline.
$Script:RequiredSelfTestSuites = [ordered]@{
    'design'                    = 6
    'fit'                       = 6
    'lifecycle'                 = 25
    'dashboard'                 = 14
    'dashboard-stale'           = 19
    'dashboard-ordering'        = 55
    'dashboard-visual'          = 89
    'setup-wizard'              = 107
    'wireless'                  = 105
    'transfers'                 = 179
    'settings'                  = 144
    'about'                     = 84
    'dashboard-ordering-stress' = 1100
}

$Script:ExpectedSelfTestTotal =
    ($Script:RequiredSelfTestSuites.Values | Measure-Object -Sum).Sum

<#
.SYNOPSIS
Validates a PLP_SELFTEST report against the exact, unique-suite contract above.

.DESCRIPTION
Returns a [pscustomobject] with:
  IsValid  - $true only when every required suite appears exactly once, every numerator equals
             its denominator, every denominator equals the documented exact value, no FAIL line
             is present, no unknown suite line is present, and no line is malformed.
  Reason   - empty when valid; otherwise the first problem found (a report can have more than one
             problem, but reporting the first is enough to fail the release - this is a gate, not
             a linter).
  Total    - the sum of every required suite's denominator, only meaningful when IsValid is $true.

Never executes anything else (no build, publish or signing); pure string parsing over the
already-captured report text.
#>
function Test-SelfTestReport {
    param([Parameter(Mandatory)][string]$Report)

    $seen = @{}

    foreach ($line in ($Report -split "`r?`n")) {
        if ($line -notmatch '^(OK|FAIL):') { continue }   # not a status line at all; ignore

        if ($line -match '^FAIL:') {
            return [pscustomobject]@{ IsValid = $false; Reason = "FAIL line present: $line"; Total = 0 }
        }

        # A suite-total line has the shape "OK: <name>: <numerator>/<denominator> ...". Any other
        # OK: line (e.g. "OK: all windows constructed") carries no suite identity and is ignored
        # here - it is neither required nor forbidden, just not a suite total.
        if ($line -notmatch '^OK:\s+([\w-]+):\s+(\S+)/(\S+)\b') { continue }

        $name = $Matches[1]
        $rawNumerator = $Matches[2]
        $rawDenominator = $Matches[3]

        if ($rawNumerator -notmatch '^\d+$' -or $rawDenominator -notmatch '^\d+$') {
            return [pscustomobject]@{
                IsValid = $false
                Reason  = "malformed (non-numeric) suite line: $line"
                Total   = 0
            }
        }

        if (-not $Script:RequiredSelfTestSuites.Contains($name)) {
            return [pscustomobject]@{ IsValid = $false; Reason = "unknown suite '$name': $line"; Total = 0 }
        }

        if ($seen.ContainsKey($name)) {
            return [pscustomobject]@{ IsValid = $false; Reason = "duplicate suite '$name': $line"; Total = 0 }
        }

        $numerator = [int]$rawNumerator
        $denominator = [int]$rawDenominator

        if ($numerator -ne $denominator) {
            return [pscustomobject]@{
                IsValid = $false
                Reason  = "suite '$name' did not fully pass ($numerator/$denominator): $line"
                Total   = 0
            }
        }

        $expectedDenominator = $Script:RequiredSelfTestSuites[$name]
        if ($denominator -ne $expectedDenominator) {
            return [pscustomobject]@{
                IsValid = $false
                Reason  = "suite '$name' denominator $denominator does not match the required " +
                          "exact $expectedDenominator`: $line"
                Total   = 0
            }
        }

        $seen[$name] = $denominator
    }

    $missing = @($Script:RequiredSelfTestSuites.Keys | Where-Object { -not $seen.ContainsKey($_) })
    if ($missing.Count -gt 0) {
        return [pscustomobject]@{
            IsValid = $false
            Reason  = "missing required suite(s): $($missing -join ', ')"
            Total   = 0
        }
    }

    $total = ($seen.Values | Measure-Object -Sum).Sum
    if ($total -ne $Script:ExpectedSelfTestTotal) {
        return [pscustomobject]@{
            IsValid = $false
            Reason  = "total $total does not match the required exact $($Script:ExpectedSelfTestTotal)"
            Total   = $total
        }
    }

    return [pscustomobject]@{ IsValid = $true; Reason = ''; Total = $total }
}

# ============================================================
#  PhoneLink Pro - installer switch regression tests
#
#  Usage:
#    powershell -NoProfile -ExecutionPolicy Bypass -File installer\test-installer-switches.ps1
#
#  Compiles the two test-only harnesses in installer\tests (they #include the
#  SHIPPED switch logic, installer\InstallerSwitches.iss) and runs them as real
#  Inno Setup processes:
#    1. SwitchProbe    - the /REMOVESETTINGS and /ALLOWDOWNGRADE decisions for
#                        a matrix of real command lines (quoting, case, repeats).
#    2. UninstallProbe - a throwaway app installed into a temporary folder and
#                        uninstalled again, so the real two-phase uninstaller
#                        (switch forwarding included) decides whether to delete
#                        a temporary settings folder.
#
#  Safety: everything happens under one new %TEMP%\plp-switch-tests-<guid>
#  folder, which is the only thing this script deletes. %AppData%\PhoneLinkPro,
#  the installed PhoneLink Pro and the registry's Add/Remove Programs list are
#  never touched (the probe app has its own AppId and CreateUninstallRegKey=no).
#
#  Exit codes: 0 all passed, 1 a test failed, 2 Inno Setup 6 not found.
# ============================================================
param(
    [string]$Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Iscc)) {
    Write-Host "Inno Setup 6 not found at $Iscc" -ForegroundColor Red
    exit 2
}

$testsDir = Join-Path $PSScriptRoot 'tests'
$root = Join-Path ([IO.Path]::GetTempPath()) ('plp-switch-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

$failures = New-Object System.Collections.Generic.List[string]
$passed = 0

function Invoke-Exe([string]$exe, [string]$arguments, [int]$timeoutMs = 60000) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    if (-not $p.WaitForExit($timeoutMs)) {
        try { $p.Kill() } catch { }
        throw "timed out: $exe $arguments"
    }
    return $p.ExitCode
}

function Wait-ForFile([string]$path, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if (Test-Path $path) {
            try {
                $content = [IO.File]::ReadAllText($path)
                if ($content.Trim().Length -gt 0) { return $content }
            } catch { }   # still being written
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Check([string]$name, [string]$expected, [string]$actual) {
    if ($expected -eq $actual) {
        $script:passed++
        Write-Host ("  PASS  {0}" -f $name)
    } else {
        $script:failures.Add("$name - expected '$expected', got '$actual'")
        Write-Host ("  FAIL  {0}  (expected '{1}', got '{2}')" -f $name, $expected, $actual) -ForegroundColor Red
    }
}

try {
    # ---------- compile the harnesses ----------
    foreach ($harness in 'SwitchProbe', 'UninstallProbe') {
        $null = & $Iscc /Q "/O$root" (Join-Path $testsDir "$harness.iss")
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed to compile $harness.iss (exit $LASTEXITCODE)" }
    }
    $switchProbe = Join-Path $root 'SwitchProbe.exe'
    $uninstallProbe = Join-Path $root 'UninstallProbe.exe'

    # ---------- 1) decision matrix against real command lines ----------
    # Remove   = would the uninstaller delete %AppData%\PhoneLinkPro (1) or keep it (0)?
    # Downgrade = would Setup allow installing over a newer version (1) or refuse (0)?
    $cases = @(
        @{ Name = 'no switch: settings kept, downgrade refused';            Args = '';                                       Remove = '0'; Downgrade = '0' }
        @{ Name = '/REMOVESETTINGS (bare flag) removes';                    Args = '/REMOVESETTINGS';                        Remove = '1'; Downgrade = '0' }
        @{ Name = '/REMOVESETTINGS=1 removes';                              Args = '/REMOVESETTINGS=1';                      Remove = '1'; Downgrade = '0' }
        @{ Name = '/REMOVESETTINGS=0 keeps (the reported bug)';             Args = '/REMOVESETTINGS=0';                      Remove = '0'; Downgrade = '0' }
        @{ Name = 'switch name is case-insensitive (=1)';                   Args = '/removesettings=1';                      Remove = '1'; Downgrade = '0' }
        @{ Name = 'switch name is case-insensitive (bare)';                 Args = '/RemoveSettings';                        Remove = '1'; Downgrade = '0' }
        @{ Name = 'empty value keeps';                                      Args = '/REMOVESETTINGS=';                       Remove = '0'; Downgrade = '0' }
        @{ Name = 'unrecognised value 2 keeps (fail safe)';                 Args = '/REMOVESETTINGS=2';                      Remove = '0'; Downgrade = '0' }
        @{ Name = 'unrecognised value 10 keeps (fail safe)';                Args = '/REMOVESETTINGS=10';                     Remove = '0'; Downgrade = '0' }
        @{ Name = 'unrecognised value yes keeps (only 1 means remove)';     Args = '/REMOVESETTINGS=yes';                    Remove = '0'; Downgrade = '0' }
        @{ Name = 'longer switch name /REMOVESETTINGSX=1 is not the switch'; Args = '/REMOVESETTINGSX=1';                    Remove = '0'; Downgrade = '0' }
        @{ Name = 'dash prefix -REMOVESETTINGS=1 is not the switch';        Args = '-REMOVESETTINGS=1';                      Remove = '0'; Downgrade = '0' }
        @{ Name = 'switch text inside another quoted argument is ignored';  Args = '"/PROBENOTE=keep /REMOVESETTINGS off"';  Remove = '0'; Downgrade = '0' }
        @{ Name = 'whole switch in quotes still counts';                    Args = '"/REMOVESETTINGS=1"';                    Remove = '1'; Downgrade = '0' }
        @{ Name = 'repeated switch: last wins (1 then 0 keeps)';            Args = '/REMOVESETTINGS=1 /REMOVESETTINGS=0';    Remove = '0'; Downgrade = '0' }
        @{ Name = 'repeated switch: last wins (0 then bare removes)';       Args = '/REMOVESETTINGS=0 /REMOVESETTINGS';      Remove = '1'; Downgrade = '0' }
        @{ Name = 'mixed with standard Inno switches (=0 keeps)';           Args = '/SP- /REMOVESETTINGS=0 /NOCANCEL';       Remove = '0'; Downgrade = '0' }
        @{ Name = '/ALLOWDOWNGRADE=1 allows (unchanged)';                   Args = '/ALLOWDOWNGRADE=1';                      Remove = '0'; Downgrade = '1' }
        @{ Name = '/ALLOWDOWNGRADE=0 refuses (unchanged)';                  Args = '/ALLOWDOWNGRADE=0';                      Remove = '0'; Downgrade = '0' }
        @{ Name = '/ALLOWDOWNGRADE bare refuses, needs =1 (unchanged)';     Args = '/ALLOWDOWNGRADE';                        Remove = '0'; Downgrade = '0' }
        @{ Name = '/ALLOWDOWNGRADE=yes refuses (unchanged)';                Args = '/ALLOWDOWNGRADE=yes';                    Remove = '0'; Downgrade = '0' }
        @{ Name = '/allowdowngrade=1 switch name is case-insensitive (unchanged)'; Args = '/allowdowngrade=1';                Remove = '0'; Downgrade = '1' }
        @{ Name = 'both switches are independent';                          Args = '/ALLOWDOWNGRADE=1 /REMOVESETTINGS=1';    Remove = '1'; Downgrade = '1' }
    )

    Write-Host '== Switch decisions (SwitchProbe) =='
    $i = 0
    foreach ($c in $cases) {
        $i++
        $out = Join-Path $root "probe-$i.txt"
        $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART `"/PROBEOUT=$out`" $($c.Args)"
        $null = Invoke-Exe $switchProbe $arguments
        $content = Wait-ForFile $out 30000
        if ($null -eq $content) {
            $failures.Add("$($c.Name) - probe wrote no result")
            Write-Host "  FAIL  $($c.Name)  (probe wrote no result)" -ForegroundColor Red
            continue
        }
        $remove = if ($content -match 'removesettings=(\d)') { $Matches[1] } else { '?' }
        $downgrade = if ($content -match 'allowdowngrade=(\d)') { $Matches[1] } else { '?' }
        Check "$($c.Name) [remove]" $c.Remove $remove
        Check "$($c.Name) [downgrade]" $c.Downgrade $downgrade
    }

    # ---------- 2) end to end through the real two-phase uninstaller ----------
    $e2e = @(
        @{ Name = 'uninstall, no switch: settings folder kept';        Args = '';                    Kept = 'True' }
        @{ Name = 'uninstall /REMOVESETTINGS: settings folder deleted';   Args = '/REMOVESETTINGS';     Kept = 'False' }
        @{ Name = 'uninstall /REMOVESETTINGS=1: settings folder deleted'; Args = '/REMOVESETTINGS=1';   Kept = 'False' }
        @{ Name = 'uninstall /REMOVESETTINGS=0: settings folder kept';    Args = '/REMOVESETTINGS=0';   Kept = 'True' }
        @{ Name = 'uninstall /REMOVESETTINGSX=1: settings folder kept';   Args = '/REMOVESETTINGSX=1';  Kept = 'True' }
    )

    Write-Host '== Real uninstaller (UninstallProbe) =='
    $i = 0
    foreach ($c in $e2e) {
        $i++
        $appDir = Join-Path $root "app-$i"
        $settingsDir = Join-Path $root "settings-$i"
        $out = Join-Path $root "uninstall-$i.txt"
        New-Item -ItemType Directory -Path $settingsDir | Out-Null
        Set-Content -Path (Join-Path $settingsDir 'plp-switch-probe-sentinel.txt') -Value 'sentinel'
        Set-Content -Path (Join-Path $settingsDir 'settings.json') -Value '{}'

        $code = Invoke-Exe $uninstallProbe "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART `"/DIR=$appDir`""
        $uninstaller = Join-Path $appDir 'unins000.exe'
        if ($code -ne 0 -or -not (Test-Path $uninstaller)) {
            $failures.Add("$($c.Name) - probe install failed (exit $code)")
            Write-Host "  FAIL  $($c.Name)  (probe install failed, exit $code)" -ForegroundColor Red
            continue
        }

        $null = Invoke-Exe $uninstaller "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART `"/PROBESETTINGS=$settingsDir`" `"/PROBEOUT=$out`" $($c.Args)"
        # The first phase can return before the second phase (running from %TEMP%) finishes.
        $content = Wait-ForFile $out 60000
        if ($null -eq $content -or $content.Trim() -ne 'done') {
            $failures.Add("$($c.Name) - uninstaller did not run the policy (result '$content')")
            Write-Host "  FAIL  $($c.Name)  (uninstaller result '$content')" -ForegroundColor Red
            continue
        }
        Check $c.Name $c.Kept ([string](Test-Path (Join-Path $settingsDir 'settings.json')))

        # Let the uninstaller finish deleting itself before the temp folder is cleaned.
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ((Test-Path $uninstaller) -and $sw.ElapsedMilliseconds -lt 30000) { Start-Sleep -Milliseconds 200 }
    }
}
catch {
    $failures.Add("harness error: $($_.Exception.Message)")
    Write-Host "  ERROR $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Start-Sleep -Milliseconds 500
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host ("Installer switch tests: {0} passed, {1} FAILED" -f $passed, $failures.Count) -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host ("Installer switch tests: {0} passed, 0 failed" -f $passed) -ForegroundColor Green
exit 0

# ============================================================
#  PhoneLink Pro - deterministic release pipeline (REL-02, Part G)
#
#  Usage:
#    powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1
#    ... [-AllowDirty] [-SkipInstaller] [-CheckOnly] [-OutDir artifacts\release]
#
#  Steps: repo checks -> clean controlled output -> build (warnings are
#  errors) -> tests (0 failed, 0 skipped) -> publish (engine-integrity
#  gate) -> publish content checks -> installer -> signing (or UNSIGNED
#  labelling) -> SHA-256 checksums -> release-manifest.json.
#
#  This script never pushes, never tags, never touches the network, and
#  deletes nothing outside its controlled output directories.
# ============================================================
param(
    [switch]$AllowDirty,     # local development only: skip the clean-tree requirement
    [switch]$SkipInstaller,
    [switch]$CheckOnly,      # run repo/version checks then stop (dry-run verifier)
    [string]$OutDir = 'artifacts\release'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Fail([string]$msg) { Write-Host "RELEASE FAILED: $msg" -ForegroundColor Red; exit 1 }
function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# Repair batch 5B (§5): the exact, unique-suite report validator lives in its own file so it can
# be dot-sourced by a lightweight PowerShell subprocess in tests too, without running any part of
# the pipeline below.
. (Join-Path $PSScriptRoot 'SelfTestReportValidator.ps1')

# ---------- 1) repository checks ----------
Step 'Repository checks'
$dirty = git status --porcelain
if ($LASTEXITCODE -ne 0) { Fail 'not a git repository' }
if ($dirty -and -not $AllowDirty) { Fail 'working tree is dirty (commit first, or pass -AllowDirty for local experiments)' }
$commit = (git rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $commit) { Fail 'HEAD is not a committed revision' }

# ---------- 2) single authoritative version ----------
$props = Get-Content 'Directory.Build.props' -Raw
if ($props -notmatch '<PlpProductVersion>([0-9]+\.[0-9]+\.[0-9]+)</PlpProductVersion>') {
    Fail 'PlpProductVersion not found in Directory.Build.props'
}
$version = $Matches[1]
Step "Version $version @ $($commit.Substring(0,8)) (dirty=$([bool]$dirty))"

if ($CheckOnly) { Write-Host 'Check-only mode: repository and version checks passed.'; exit 0 }

# ---------- 3) clean controlled output only ----------
Step 'Cleaning controlled output'
$publishDir = 'bin\Release\net9.0-windows\win-x64\publish'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force $OutDir | Out-Null

# ---------- 4) build with zero-warning enforcement ----------
Step 'Build (Release, warnings are errors)'
dotnet build ScrcpyDeck.csproj -c Release -warnaserror --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail 'Release build failed (a compiler warning fails the release build)' }

# ---------- 4b) test project: analyzer diagnostics are errors ----------
# Independent review (M1): the test project's own <TreatWarningsAsErrors> (its .csproj) already
# makes this true for every build of it, including the implicit build inside `dotnet test` below —
# this explicit step exists only so a reintroduced xUnit analyzer diagnostic (xUnit1xxx/2xxx)
# shows up here, clearly labelled, rather than buried inside the test step's own output.
Step 'Build (test project, analyzer warnings are errors)'
dotnet build 'tests\PhoneLinkPro.Tests\PhoneLinkPro.Tests.csproj' -c Release -warnaserror --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail 'test project build failed (an xUnit analyzer diagnostic fails the release build)' }

# ---------- 5) tests: zero failed, zero skipped ----------
Step 'Tests'
$testOut = dotnet test 'tests\PhoneLinkPro.Tests\PhoneLinkPro.Tests.csproj' --nologo -v q
if ($LASTEXITCODE -ne 0) { $testOut | Select-Object -Last 5; Fail 'test run failed' }
$summary = ($testOut | Select-String 'Passed!|Failed!').Line
if ($summary -notmatch 'Failed:\s+0,\s+Passed:\s+(\d+),\s+Skipped:\s+0,\s+Total:\s+(\d+)') {
    Fail "unexpected test summary (need 0 failed / 0 skipped): $summary"
}
$testsPassed = [int]$Matches[1]
$testsTotal = [int]$Matches[2]
Step "Tests: $testsPassed/$testsTotal passed, 0 skipped"

# ---------- 5b) Release PLP_SELFTEST: the full deterministic runtime gate ----------
# Independent review (M2): xUnit alone never constructs the real WPF Application, so it cannot
# prove the runtime self-test suite (design/fit/lifecycle/dashboard-ordering/.../about — 1920
# assertions total) actually passes against the Release build. This runs the already-built
# Release exe from step 4 above as a bounded child process with ONLY PLP_SELFTEST set, and
# requires an EXACT match on the two totals the independent review named plus the overall count —
# not just "exit code 0" — so a suite silently shrinking (fewer assertions than before) fails the
# release exactly as loudly as a suite silently failing.
Step 'Release PLP_SELFTEST (deterministic runtime gate)'
$selfTestExe = 'bin\Release\net9.0-windows\PhoneLinkPro.exe'
if (-not (Test-Path $selfTestExe)) { Fail "Release exe not found at $selfTestExe" }

$selfTestReportPath = Join-Path $env:TEMP 'plp_selftest.txt'
if (Test-Path $selfTestReportPath) { Remove-Item $selfTestReportPath -Force }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Resolve-Path $selfTestExe).Path
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
# Only PLP_SELFTEST for the child — inherits the rest of this script's own environment (PATH,
# SystemRoot, etc.) exactly as ProcessStartInfo does by default, but any stray PLP_* variable
# that happened to already be set is stripped first so it can never combine into a conflicting-
# modes run.
foreach ($key in @($psi.EnvironmentVariables.Keys)) {
    if ($key -like 'PLP_*') { $psi.EnvironmentVariables.Remove($key) }
}
$psi.EnvironmentVariables['PLP_SELFTEST'] = '1'

$selfTestProcess = [System.Diagnostics.Process]::Start($psi)
$selfTestTimeoutMs = 120000
if (-not $selfTestProcess.WaitForExit($selfTestTimeoutMs)) {
    try { $selfTestProcess.Kill($true) } catch { }
    Fail "Release PLP_SELFTEST did not exit within $($selfTestTimeoutMs / 1000)s"
}
# The parent script's own environment was never touched (only the child ProcessStartInfo's own
# environment block was), so there is nothing to clean up here.

$selfTestExitCode = $selfTestProcess.ExitCode
if (-not (Test-Path $selfTestReportPath)) { Fail 'Release PLP_SELFTEST produced no report file' }
$selfTestReport = Get-Content $selfTestReportPath -Raw
if ($selfTestExitCode -ne 0) { Fail "Release PLP_SELFTEST exited $selfTestExitCode`n$selfTestReport" }

# Repair batch 5B (§5): exact, unique-suite validation - see SelfTestReportValidator.ps1. Each of
# the 13 required suites must appear EXACTLY once with numerator == denominator == its documented
# exact value; a duplicated suite and an omitted suite can no longer cancel out to the same total,
# an unknown suite line is never accepted silently, and any FAIL or malformed line fails closed.
$selfTestValidation = Test-SelfTestReport -Report $selfTestReport
if (-not $selfTestValidation.IsValid) {
    Fail "Release PLP_SELFTEST: $($selfTestValidation.Reason)`n$selfTestReport"
}
$selfTestAssertionTotal = $selfTestValidation.Total
Step "Release PLP_SELFTEST: $selfTestAssertionTotal/$selfTestAssertionTotal runtime assertions (exact, unique-suite match), ordering 55/55, stress 1100/1100"

# ---------- 6) publish (runs the engine-integrity publish gate) ----------
Step 'Publish (self-contained win-x64 + integrity gate)'
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail 'publish failed (or the engine-integrity gate rejected the bundle)' }

# ---------- 7) publish content checks ----------
Step 'Publish content checks'
if (-not (Test-Path "$publishDir\THIRD-PARTY-NOTICES.txt")) { Fail 'THIRD-PARTY-NOTICES.txt missing from publish output' }
$licenseCount = (Get-ChildItem "$publishDir\Licenses" -Filter '*.txt' -ErrorAction SilentlyContinue | Measure-Object).Count
if ($licenseCount -lt 1) { Fail 'license texts missing from publish output' }
$testLeak = Get-ChildItem $publishDir -Recurse -Include '*Tests*', 'xunit*' | Measure-Object
if ($testLeak.Count -ne 0) { Fail 'test assemblies leaked into publish output' }
$exeVersion = (Get-Item "$publishDir\PhoneLinkPro.exe").VersionInfo.FileVersion
if ($exeVersion -notlike "$version.*" -and $exeVersion -ne $version) {
    Fail "version drift: exe FileVersion '$exeVersion' does not match PlpProductVersion '$version'"
}

# ---------- 8) installer ----------
$installerSource = 'dist\PhoneLinkPro-Setup.exe'
$installerBuilt = $false
if (-not $SkipInstaller) {
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (Test-Path $iscc) {
        Step 'Installer (Inno Setup)'
        & $iscc 'installer\PhoneLinkPro.iss' /Q
        if ($LASTEXITCODE -ne 0) { Fail 'installer build failed' }
        $installerBuilt = $true
    }
    else {
        Step 'Installer SKIPPED: Inno Setup 6 not installed'
    }
}
else { Step 'Installer skipped by request' }

# ---------- 9) signing (or honest UNSIGNED labelling) ----------
Step 'Signing'
$signTargets = @("$publishDir\PhoneLinkPro.exe", "$publishDir\PhoneLinkPro.dll")
if ($installerBuilt) { $signTargets += $installerSource }
& powershell -NoProfile -ExecutionPolicy Bypass -File 'Security\sign.ps1' -Files ($signTargets -join ';') -Quiet
$signExit = $LASTEXITCODE
if ($signExit -eq 0) { $signed = $true; Step 'Outputs signed and verified' }
elseif ($signExit -eq 2) { $signed = $false; Step 'Outputs are UNSIGNED (no signing identity configured)' }
else { Fail 'signing was configured but failed' }

# ---------- 10) stage artifacts + checksums ----------
Step 'Checksums and release manifest'
$suffix = ''
if (-not $signed) { $suffix = '-UNSIGNED' }

$installerArtifact = $null
$installerSha = $null
if ($installerBuilt) {
    $installerArtifact = Join-Path $OutDir "PhoneLinkPro-Setup-v$version$suffix.exe"
    Copy-Item $installerSource $installerArtifact
    $installerSha = (Get-FileHash $installerArtifact -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Relative-path checksum list over the whole publish payload.
$checksumFile = Join-Path $OutDir "publish-checksums-v$version$suffix.txt"
$publishRoot = (Resolve-Path $publishDir).Path
$lines = Get-ChildItem $publishRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($publishRoot.Length + 1).Replace('\', '/')
    $sha = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sha  $rel"
}
Set-Content -Path $checksumFile -Value $lines -Encoding UTF8
$checksumSha = (Get-FileHash $checksumFile -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    product               = 'PhoneLink Pro'
    version               = $version
    commitSha             = $commit
    builtUtc              = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    signed                = $signed
    releasable            = $signed
    installerBuilt        = $installerBuilt
    installerArtifact     = if ($installerArtifact) { Split-Path $installerArtifact -Leaf } else { $null }
    installerSha256       = $installerSha
    publishChecksumFile   = Split-Path $checksumFile -Leaf
    publishChecksumSha256 = $checksumSha
    publishFileCount      = ($lines | Measure-Object).Count
    engineManifestVersion = 1
    tests                 = [ordered]@{ total = $testsTotal; passed = $testsPassed; failed = 0; skipped = 0 }
    compilerWarnings      = 0
    localDirtyTree        = [bool]$dirty
}
$manifest | ConvertTo-Json | Set-Content (Join-Path $OutDir "release-manifest-v$version$suffix.json") -Encoding UTF8

if (-not $signed) {
    @(
        'NOT FOR PUBLIC RELEASE'
        ''
        'These artifacts are UNSIGNED. Windows SmartScreen will warn users, and the'
        'files carry no publisher identity. Configure an Authenticode identity'
        '(see Security\SIGNING.md) and re-run release.ps1 before distributing.'
    ) | Set-Content (Join-Path $OutDir 'NOT-FOR-PUBLIC-RELEASE.txt') -Encoding UTF8
}

Step "Release artifacts in $OutDir"
Get-ChildItem $OutDir | ForEach-Object { Write-Host ("   " + $_.Name) }
if (-not $signed) { Write-Host 'NOTE: UNSIGNED build - not publicly releasable.' -ForegroundColor Yellow }
exit 0

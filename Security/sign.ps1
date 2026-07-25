# ============================================================
#  PhoneLink Pro - Authenticode signing (SEC-02)
#
#  Certificate-agnostic and secret-free by design:
#    - NO password, thumbprint or path is ever stored in this file.
#    - Configuration comes exclusively from the environment:
#        PLP_SIGN_MODE        store | pfx        (unset/none => UNSIGNED)
#        PLP_SIGN_THUMBPRINT  cert thumbprint    (mode: store, CurrentUser\My)
#        PLP_SIGN_PFX_PATH    path to .pfx       (mode: pfx; keep OUTSIDE the repo)
#        PLP_SIGN_PFX_PASSWORD  password         (mode: pfx; env/CI secret only)
#        PLP_SIGN_TSA         RFC-3161 timestamp URL
#                             (default http://timestamp.digicert.com)
#    - The password is never echoed, never logged, never written to disk.
#    - Only FIRST-PARTY files may be passed in: PhoneLinkPro.exe,
#      PhoneLinkPro.dll, PhoneLinkPro-Setup*.exe. Third-party binaries
#      (engine, runtime) are never signed as if we authored them.
#
#  Exit codes:  0 signed+verified · 2 unsigned (no identity configured) · 1 failure
#
#  Local development builds are expected to run UNSIGNED (exit 2);
#  release.ps1 labels such output "NOT FOR PUBLIC RELEASE".
# ============================================================
param(
    # Accepts a real array, or a single ';'-joined string (how release.ps1 passes the
    # list across the powershell.exe -File boundary, which cannot carry arrays).
    [Parameter(Mandatory = $true)][string[]]$Files,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$Files = @($Files | ForEach-Object { $_ -split ';' } | Where-Object { $_ -ne '' })

function Write-Info([string]$msg) { if (-not $Quiet) { Write-Host $msg } }

# ---- first-party allowlist (never sign third-party binaries) ----
$allowed = @('PhoneLinkPro.exe', 'PhoneLinkPro.dll')
foreach ($f in $Files) {
    $leaf = Split-Path $f -Leaf
    $isSetup = $leaf -like 'PhoneLinkPro-Setup*.exe'
    if (-not ($allowed -contains $leaf) -and -not $isSetup) {
        Write-Error "Refusing to sign non-first-party file: $leaf"
        exit 1
    }
    if (-not (Test-Path $f)) {
        Write-Error "File not found: $f"
        exit 1
    }
}

# ---- signing identity from the environment ----
$mode = "$env:PLP_SIGN_MODE".Trim().ToLowerInvariant()
if ($mode -eq '' -or $mode -eq 'none') {
    Write-Info 'UNSIGNED: no signing identity configured (set PLP_SIGN_MODE=store|pfx).'
    exit 2
}

$tsa = "$env:PLP_SIGN_TSA"
if ($tsa -eq '') { $tsa = 'http://timestamp.digicert.com' }

# ---- locate signtool ----
$signtool = $null
$kitRoots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")
foreach ($root in $kitRoots) {
    if (Test-Path $root) {
        $candidate = Get-ChildItem $root -Directory -Filter '10.*' |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
        if ($candidate) { $signtool = $candidate; break }
    }
}
if (-not $signtool) {
    Write-Error 'signtool.exe not found. Install the Windows 10/11 SDK.'
    exit 1
}

# ---- build identity arguments (password handled only in-memory) ----
$identityArgs = @()
switch ($mode) {
    'store' {
        $thumb = "$env:PLP_SIGN_THUMBPRINT".Trim()
        if ($thumb -eq '') { Write-Error 'PLP_SIGN_MODE=store requires PLP_SIGN_THUMBPRINT.'; exit 1 }
        $identityArgs = @('/sha1', $thumb, '/s', 'My')
    }
    'pfx' {
        $pfx = "$env:PLP_SIGN_PFX_PATH"
        if ($pfx -eq '' -or -not (Test-Path $pfx)) { Write-Error 'PLP_SIGN_MODE=pfx requires a valid PLP_SIGN_PFX_PATH.'; exit 1 }
        $identityArgs = @('/f', $pfx)
        if ("$env:PLP_SIGN_PFX_PASSWORD" -ne '') {
            $identityArgs += @('/p', $env:PLP_SIGN_PFX_PASSWORD)
        }
    }
    default { Write-Error "Unknown PLP_SIGN_MODE '$mode' (use store, pfx or unset)."; exit 1 }
}

# ---- sign then verify every file ----
foreach ($f in $Files) {
    $leaf = Split-Path $f -Leaf
    Write-Info "Signing $leaf ..."
    # Argument array form: the command line is never composed into an echoed string,
    # so the password cannot leak through output.
    & $signtool sign @identityArgs /fd sha256 /tr $tsa /td sha256 $f | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "Signing failed for $leaf"; exit 1 }
}

foreach ($f in $Files) {
    $leaf = Split-Path $f -Leaf
    & $signtool verify /pa $f | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "Signature verification failed for $leaf"; exit 1 }
    Write-Info "Verified  $leaf"
}

Write-Info 'All files signed and verified.'
exit 0

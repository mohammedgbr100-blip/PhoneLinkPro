# ============================================================
#  PhoneLink Pro - reproduce the Android SDK Platform-Tools NOTICE for the bundled adb
#
#  The bundled engine/PhoneLink.Bridge.exe is Google's prebuilt adb.exe from
#  Platform-Tools 36.0.0 (build 13206524), the exact package scrcpy v3.3.4 takes it
#  from (app/deps/adb_windows.sh). It statically links third-party libraries whose
#  licences require their notices to travel with the binary; Google lists them in
#  that package's NOTICE.txt. This script obtains that file from the official package
#  and copies it to Licenses\platform-tools-NOTICE.txt, which the build, the publish
#  output and the installer then ship automatically (Licenses\*.txt).
#
#  The package is downloaded from Google under the Android SDK License Agreement
#  (https://developer.android.com/studio/terms). Read those terms first; the script
#  refuses to download unless -AcceptAndroidSdkLicense is passed. It is never run by
#  the build, the tests or release.ps1.
#
#  Usage:
#    powershell -NoProfile -ExecutionPolicy Bypass -File Security\fetch-platform-tools-notice.ps1 -AcceptAndroidSdkLicense
#    ... -ZipPath <already-downloaded platform-tools_r36.0.0-win.zip>   (no download)
#
#  It fails closed, and writes nothing, unless ALL of these hold:
#    - the zip is 7138784 bytes with SHA-256 12c2841f...107c (the value scrcpy v3.3.4 pins);
#    - platform-tools\source.properties says Pkg.Revision=36.0.0;
#    - adb.exe, AdbWinApi.dll and AdbWinUsbApi.dll in the zip are byte-identical to the
#      shipped engine files recorded in Resources\engine.manifest;
#    - the zip contains platform-tools\NOTICE.txt.
#
#  Exit codes: 0 notice written, 1 verification failed, 2 terms not accepted.
# ============================================================
param(
    [switch]$AcceptAndroidSdkLicense,
    [string]$ZipPath,
    [string]$OutFile = (Join-Path $PSScriptRoot '..\Licenses\platform-tools-NOTICE.txt')
)

$ErrorActionPreference = 'Stop'

$PackageUrl    = 'https://dl.google.com/android/repository/platform-tools_r36.0.0-win.zip'
$PackageSha256 = '12c2841f354e92a0eb2fd7bf6f0f9bf8538abce7bd6b060ac8349d6f6a61107c'
$PackageSize   = 7138784
$Revision      = 'Pkg.Revision=36.0.0'
# Shipped engine file (Resources\engine.manifest path) = file inside platform-tools\
$EngineFiles = [ordered]@{
    'engine/PhoneLink.Bridge.exe' = 'adb.exe'
    'engine/AdbWinApi.dll' = 'AdbWinApi.dll'
    'engine/AdbWinUsbApi.dll' = 'AdbWinUsbApi.dll'
}

function Fail([string]$msg) { Write-Host "NOTICE NOT WRITTEN: $msg" -ForegroundColor Red; exit 1 }

$work = Join-Path ([IO.Path]::GetTempPath()) ('plp-platform-tools-' + [guid]::NewGuid().ToString('N'))
try {
    if (-not $ZipPath) {
        if (-not $AcceptAndroidSdkLicense) {
            Write-Host 'Downloading Platform-Tools is governed by the Android SDK License Agreement:'
            Write-Host '  https://developer.android.com/studio/terms'
            Write-Host 'Re-run with -AcceptAndroidSdkLicense once the publisher has reviewed and accepted it,'
            Write-Host 'or pass -ZipPath with a copy of platform-tools_r36.0.0-win.zip obtained under those terms.'
            exit 2
        }
        New-Item -ItemType Directory -Path $work | Out-Null
        $ZipPath = Join-Path $work 'platform-tools_r36.0.0-win.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Write-Host "Downloading $PackageUrl"
        Invoke-WebRequest -Uri $PackageUrl -OutFile $ZipPath -UseBasicParsing
    }
    if (-not (Test-Path $ZipPath)) { Fail "zip not found: $ZipPath" }

    # ---- 1) the package is the official one ----
    $size = (Get-Item $ZipPath).Length
    $sha = (Get-FileHash -Algorithm SHA256 $ZipPath).Hash.ToLowerInvariant()
    if ($sha -ne $PackageSha256) { Fail "SHA-256 mismatch: expected $PackageSha256, got $sha" }
    if ($size -ne $PackageSize) { Fail "size mismatch: expected $PackageSize bytes, got $size" }
    Write-Host "Package verified: $size bytes, SHA-256 $sha"

    if (-not (Test-Path $work)) { New-Item -ItemType Directory -Path $work | Out-Null }
    $extract = Join-Path $work 'extract'
    Expand-Archive -Path $ZipPath -DestinationPath $extract -Force
    $pt = Join-Path $extract 'platform-tools'

    $props = Join-Path $pt 'source.properties'
    if (-not (Test-Path $props) -or -not ((Get-Content $props) -contains $Revision)) {
        Fail "source.properties does not say $Revision"
    }

    # ---- 2) its adb files ARE the shipped engine files ----
    $manifestFile = Join-Path $PSScriptRoot '..\Resources\engine.manifest'
    $expected = @{}
    foreach ($line in Get-Content $manifestFile) {
        if ($line -match '^([0-9a-f]{64}) (.+)$') { $expected[$Matches[2]] = $Matches[1] }
    }
    foreach ($entry in $EngineFiles.GetEnumerator()) {
        $file = Join-Path $pt $entry.Value
        if (-not (Test-Path $file)) { Fail "$($entry.Value) missing from the package" }
        $actual = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
        if (-not $expected.ContainsKey($entry.Key)) { Fail "$($entry.Key) not in Resources\engine.manifest" }
        if ($actual -ne $expected[$entry.Key]) {
            Fail "$($entry.Value) in the package ($actual) is not the shipped $($entry.Key) ($($expected[$entry.Key]))"
        }
        Write-Host "  $($entry.Value) == $($entry.Key)  ($actual)"
    }

    # ---- 3) copy the notice, unmodified ----
    $notice = Join-Path $pt 'NOTICE.txt'
    if (-not (Test-Path $notice)) { Fail 'platform-tools\NOTICE.txt missing from the package' }
    Copy-Item -Path $notice -Destination $OutFile -Force
    $noticeSha = (Get-FileHash -Algorithm SHA256 $OutFile).Hash.ToLowerInvariant()
    Write-Host "Written: $OutFile ($((Get-Item $OutFile).Length) bytes, SHA-256 $noticeSha)"
    Write-Host 'Record this SHA-256 in THIRD-PARTY-NOTICES.txt, item (d).'
    exit 0
}
finally {
    if (Test-Path $work) { Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue }
}

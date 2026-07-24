@echo off
REM ============================================================
REM  PhoneLink Pro — Authenticode code signing
REM ============================================================
REM  Signs the app exe and the installer with SHA-256 + a trusted
REM  RFC-3161 timestamp (so signatures stay valid after the cert
REM  expires).
REM
REM  1) Put your certificate details below (PFX file + password),
REM     OR leave CERT empty to auto-pick the best cert from your
REM     Windows certificate store (/a).
REM  2) Recommended order:
REM        publish.bat
REM        (optional) Security\obfuscate.bat
REM        Security\sign.bat            <-- signs publish\PhoneLinkPro.exe
REM        installer\build-installer.bat
REM        Security\sign.bat            <-- signs dist installer too
REM ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

REM ---- EDIT THESE ----
set "CERT="
set "PASS="
set "TSA=http://timestamp.digicert.com"
REM --------------------

REM Locate the newest signtool.exe from the Windows SDK
set "SIGNTOOL="
for /f "delims=" %%D in ('dir /b /ad /o-n "C:\Program Files (x86)\Windows Kits\10\bin\10.*" 2^>nul') do (
    if exist "C:\Program Files (x86)\Windows Kits\10\bin\%%D\x64\signtool.exe" (
        set "SIGNTOOL=C:\Program Files (x86)\Windows Kits\10\bin\%%D\x64\signtool.exe"
        goto :found
    )
)
:found
if not defined SIGNTOOL (
    echo [!] signtool.exe not found. Install the Windows 10/11 SDK.
    pause & exit /b 1
)
echo Using: %SIGNTOOL%

if defined CERT (
    set "AUTH=/f "%CERT%""
    if defined PASS set "AUTH=!AUTH! /p "%PASS%""
) else (
    set "AUTH=/a"
)

set "EXE=%~dp0..\bin\Release\net9.0-windows\win-x64\publish\PhoneLinkPro.exe"
set "SETUP=%~dp0..\dist\PhoneLinkPro-Setup.exe"

for %%F in ("%EXE%" "%SETUP%") do (
    if exist "%%~F" (
        echo.
        echo Signing %%~nxF ...
        "%SIGNTOOL%" sign !AUTH! /fd sha256 /tr "%TSA%" /td sha256 "%%~F"
        if errorlevel 1 ( echo [!] Signing failed for %%~nxF & pause & exit /b 1 )
        "%SIGNTOOL%" verify /pa "%%~F"
    )
)

echo.
echo Done.
pause

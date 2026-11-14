@echo off
REM ===== PhoneLink Pro — build the branded installer (Inno Setup) =====
cd /d "%~dp0"

set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

if not exist %ISCC% (
    echo [!] Inno Setup 6 not found at %ISCC%
    echo     Install it from https://jrsoftware.org/isdl.php then re-run.
    pause
    exit /b 1
)

if not exist "..\bin\Release\net9.0-windows\win-x64\publish\PhoneLinkPro.exe" (
    echo [!] Publish output missing. Run ..\publish.bat first.
    pause
    exit /b 1
)

echo Testing installer switches (/REMOVESETTINGS, /ALLOWDOWNGRADE)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-installer-switches.ps1"
if errorlevel 1 (
    echo [!] Installer switch tests failed - the installer was NOT built.
    pause
    exit /b 1
)

echo Compiling installer...
%ISCC% PhoneLinkPro.iss
if errorlevel 1 (
    echo [!] Installer build failed.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo  Installer ready:  ..\dist\PhoneLinkPro-Setup.exe
echo ============================================================
pause

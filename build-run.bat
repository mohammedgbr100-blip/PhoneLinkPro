@echo off
REM ===== PhoneLink Pro — build & run (development) =====
cd /d "%~dp0"
echo Building PhoneLink Pro (Debug)...
dotnet build -c Debug -nologo
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)
echo.
echo Launching...
start "" "bin\Debug\net9.0-windows\PhoneLinkPro.exe"

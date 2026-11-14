@echo off
REM ===== PhoneLink Pro — publish a clean self-contained build =====
REM Output: bin\Release\net9.0-windows\win-x64\publish\
cd /d "%~dp0"

set PUB=bin\Release\net9.0-windows\win-x64\publish

echo Cleaning previous publish output...
if exist "%PUB%" rmdir /s /q "%PUB%"

echo Publishing (self-contained, folder)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -nologo
if errorlevel 1 (
    echo.
    echo Publish failed.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo  Published to: %PUB%
echo.
echo  Next steps:
echo   installer\build-installer.bat       - build PhoneLinkPro-Setup.exe
echo ============================================================
pause

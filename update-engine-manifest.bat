@echo off
REM ===== PhoneLink Pro - regenerate Resources\engine.manifest (SEC-01) =====
REM The ONLY step that rewrites the tracked engine integrity manifest. Run it after
REM deliberately updating the engine bundle in ..\scrcpy, review the diff, commit it,
REM then rebuild so the new manifest is embedded. Normal builds never regenerate it.
cd /d "%~dp0"

echo Building (Debug) so the engine folder and generator are current...
dotnet build ScrcpyDeck.csproj -c Debug -nologo -v q
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

set PLP_INTEGRITY_TOOL=generate
set PLP_INTEGRITY_OUT=%~dp0Resources\engine.manifest
"bin\Debug\net9.0-windows\PhoneLinkPro.exe"
set EXITCODE=%ERRORLEVEL%
set PLP_INTEGRITY_TOOL=
set PLP_INTEGRITY_OUT=

if not "%EXITCODE%"=="0" (
    echo Manifest generation FAILED - see %%TEMP%%\plp_integrity_tool.txt
    exit /b %EXITCODE%
)

echo Manifest written to Resources\engine.manifest
echo Review the diff, commit it, then rebuild so the new manifest is embedded.
exit /b 0

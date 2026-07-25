; ============================================================
;  PhoneLink Pro — Inno Setup installer script
;  © 2026 ENG. Mohammed Sattar
;  Build with:  ISCC.exe PhoneLinkPro.iss
;
;  Batch 1F hardening:
;   - AppVersion is read FROM the published executable, which is
;     stamped from Directory.Build.props — never edited by hand.
;   - AppMutex matches the application's single-instance mutex, so
;     Setup asks the user to close a running app (orderly shutdown,
;     which also ends mirror sessions) instead of killing anything.
;   - Before upgrade/uninstall the app's own maintenance command
;     (PhoneLinkPro.exe --prepare-update) stops a leftover bundled
;     ADB server ONLY when its image path proves it is ours; foreign
;     or ambiguous servers (Android Studio...) are never touched.
;   - Downgrades are refused unless /ALLOWDOWNGRADE is passed.
;   - User settings/logs in %AppData%\PhoneLinkPro are PRESERVED on
;     uninstall by default; /REMOVESETTINGS=1 removes them.
; ============================================================

#define MyAppName "PhoneLink Pro"
#define MyAppPublisher "ENG. Mohammed Sattar"
#define MyAppExeName "PhoneLinkPro.exe"
#define PublishDir "..\bin\Release\net9.0-windows\win-x64\publish"
#define PublishedExe PublishDir + "\" + MyAppExeName

; ---- Single authoritative version (REL-03): taken from the published exe. ----
#if !FileExists(PublishedExe)
  #error Publish output missing - run publish.bat (or release.ps1) before building the installer
#endif
#define MyAppVersion4 GetVersionNumbersString(PublishedExe)
#define MyAppVersion Copy(MyAppVersion4, 1, RPos(".", MyAppVersion4) - 1)

; Must equal Services\SingleInstance.cs MutexName (a drift test guards this).
#define MyAppMutex "PhoneLinkPro.SingleInstance"

[Setup]
AppId={{A3F5C1E2-9B4D-4A7E-8C6F-2D1E9B7A4C30}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=© 2026 ENG. Mohammed Sattar. Includes third-party open-source components — see THIRD-PARTY-NOTICES.txt
VersionInfoVersion={#MyAppVersion4}
DefaultDirName={autopf}\PhoneLink Pro
DefaultGroupName=PhoneLink Pro
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\dist
OutputBaseFilename=PhoneLinkPro-Setup
SetupIconFile=..\Assets\app.ico
WizardStyle=modern
WizardImageFile=wizard_large.bmp
WizardSmallImageFile=wizard_small.bmp
WizardImageStretch=no
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ShowLanguageDialog=no
; The app holds this session-scoped mutex while running: Setup/Uninstall detect it and ask
; the user to close the app (= the app's own orderly shutdown). Nothing is ever taskkilled.
AppMutex={#MyAppMutex}
SetupMutex=PhoneLinkProSetup.SingleInstance
; If a file is still held (e.g. Explorer preview), use the Restart Manager rather than
; failing; never auto-restart the app half-installed.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
; restartreplace: if a legacy (pre-1.2.0) bundled bridge still holds an engine file, the
; file is queued for replacement at reboot and Setup succeeds with "restart needed"
; (exit 3) instead of aborting — nothing is ever killed to force the copy.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace
; Third-party notices + licence texts, sourced from the repository so they ship
; even if the publish output is stale. Required by the components' licences.
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Licenses\*.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\PhoneLink Pro"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Third-Party Notices"; Filename: "{app}\THIRD-PARTY-NOTICES.txt"
Name: "{group}\Uninstall PhoneLink Pro"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PhoneLink Pro"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch PhoneLink Pro now"; Flags: nowait postinstall skipifsilent

; NOTE deliberately NO [UninstallDelete] for {userappdata}\PhoneLinkPro: user settings and
; logs survive uninstall/upgrade by default. Pass /REMOVESETTINGS=1 to remove them.

[Code]

{ ---------- version comparison (numeric, per dot-part) ---------- }

function VersionPart(const S: string; Index: Integer): Integer;
var
  Parts: TStringList;
begin
  Result := 0;
  Parts := TStringList.Create;
  try
    Parts.Delimiter := '.';
    Parts.StrictDelimiter := True;
    Parts.DelimitedText := S;
    if (Index < Parts.Count) then
      Result := StrToIntDef(Trim(Parts[Index]), 0);
  finally
    Parts.Free;
  end;
end;

{ >0 when A newer than B, 0 equal, <0 when A older. }
function CompareVersions(const A, B: string): Integer;
var
  I, PA, PB: Integer;
begin
  Result := 0;
  for I := 0 to 3 do
  begin
    PA := VersionPart(A, I);
    PB := VersionPart(B, I);
    if PA <> PB then
    begin
      if PA > PB then Result := 1 else Result := -1;
      exit;
    end;
  end;
end;

function InstalledVersion(): string;
var
  Key: string;
begin
  Result := '';
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' +
         '{A3F5C1E2-9B4D-4A7E-8C6F-2D1E9B7A4C30}_is1';
  if not RegQueryStringValue(HKCU, Key, 'DisplayVersion', Result) then
    RegQueryStringValue(HKLM, Key, 'DisplayVersion', Result);
end;

{ ---------- downgrade guard (REL-02) ---------- }

function InitializeSetup(): Boolean;
var
  Existing: string;
begin
  Result := True;
  Existing := InstalledVersion();
  if (Existing <> '') and (CompareVersions('{#MyAppVersion}', Existing) < 0) then
  begin
    if ExpandConstant('{param:ALLOWDOWNGRADE|0}') <> '1' then
    begin
      MsgBox('A newer version of PhoneLink Pro (' + Existing + ') is already installed.' + #13#10 +
             'Downgrading to {#MyAppVersion} is not supported.' + #13#10#13#10 +
             'To force a downgrade, re-run Setup with /ALLOWDOWNGRADE=1.',
             mbError, MB_OK);
      Result := False;
    end;
  end;
end;

{ ---------- maintenance handoff (REL-02) ----------
  Runs the INSTALLED exe's hidden cleanup: stops a leftover bundled ADB server only when
  its full image path proves it is ours. Exit codes (see Services\MaintenanceMode.cs):
    0  clean            10 foreign ADB left running (does not lock our files)
    11 ambiguous        12 integrity invalid (bridge not executed)   13 error/timeout   }

procedure RunPrepareUpdate(const Context: string);
var
  ExePath: string;
  ResultCode: Integer;
begin
  ExePath := ExpandConstant('{app}\{#MyAppExeName}');
  if not FileExists(ExePath) then exit;

  { The maintenance argument exists from 1.2.0 on. A LEGACY installed exe (1.0/1.1) would
    treat it as a normal launch, open the full UI and block Setup forever — so for legacy
    installs the handoff is skipped entirely: the AppMutex prompt still closes a running
    app, and a leftover legacy bridge is handled by restartreplace (reboot request),
    never by killing anything. }
  if CompareVersions(InstalledVersion(), '1.2.0') < 0 then
  begin
    Log(Context + ': installed version predates --prepare-update; skipping maintenance handoff');
    exit;
  end;

  if Exec(ExePath, '--prepare-update', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log(Context + ': --prepare-update exit code ' + IntToStr(ResultCode));
    if (ResultCode = 10) or (ResultCode = 11) then
      { A foreign/ambiguous ADB server (e.g. Android Studio) is running. It does not lock
        PhoneLink Pro files, so we proceed — but we explain rather than kill anything. }
      MsgBox('Another ADB server (for example Android Studio) is running on this PC.' + #13#10 +
             'PhoneLink Pro will not touch it. If Setup later reports a locked file,' + #13#10 +
             'close the other Android tool and retry, or restart Windows.',
             mbInformation, MB_OK);
  end
  else
    Log(Context + ': --prepare-update could not be started (code ' + IntToStr(ResultCode) + ')');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  { Upgrade path: the AppMutex prompt has already made the user close the app (its orderly
    shutdown ends mirror sessions). What can remain is the bundled ADB server. }
  RunPrepareUpdate('install');
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  RunPrepareUpdate('uninstall');
end;

{ Explicit command-line scan (the uninstaller forwards its parameters when it respawns
  from %TEMP%, and GetCmdTail sees them in both instances). }
function RemoveSettingsRequested(): Boolean;
begin
  Result := Pos('/REMOVESETTINGS', UpperCase(GetCmdTail())) > 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { Default: PRESERVE %AppData%\PhoneLinkPro (settings, consent, logs).
    Explicit removal: uninstaller run with /REMOVESETTINGS=1. }
  if CurUninstallStep = usPostUninstall then
  begin
    if RemoveSettingsRequested() then
    begin
      DelTree(ExpandConstant('{userappdata}\PhoneLinkPro'), True, True, True);
      Log('uninstall: user settings removed on request (/REMOVESETTINGS=1)');
    end
    else
      Log('uninstall: user settings preserved (default)');
  end;
end;

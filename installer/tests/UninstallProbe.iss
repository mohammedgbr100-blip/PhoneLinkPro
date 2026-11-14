; ============================================================
;  TEST HARNESS ONLY - never shipped, never signed.
;  Compiled and run by installer\test-installer-switches.ps1.
;
;  A throwaway app (own AppId, no Add/Remove Programs entry) that the test
;  installs into a temporary folder and then uninstalls with a given command
;  line. Its uninstaller runs the SHIPPED removal policy
;  (..\InstallerSwitches.iss: ApplySettingsRemovalPolicy) - but against a
;  temporary settings folder the test created (/PROBESETTINGS=...), never
;  against %AppData%. This covers the real two-phase uninstaller, including
;  the forwarding of command-line switches to its second phase in %TEMP%.
; ============================================================

[Setup]
AppId=PhoneLinkPro.UninstallProbe
AppName=PhoneLink Pro uninstall probe
AppVersion=0.0.0
DefaultDirName={localappdata}\PhoneLinkProUninstallProbe
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
CreateUninstallRegKey=no
Uninstallable=yes
PrivilegesRequired=lowest
OutputBaseFilename=UninstallProbe
ShowLanguageDialog=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Code]

#include "..\InstallerSwitches.iss"

const
  ProbeSentinel = 'plp-switch-probe-sentinel.txt';

{ Test-only: last /NAME=value on the command line (probe plumbing, not under test). }
function ProbeArg(const Name: string): string;
var
  I: Integer;
  Prefix: string;
begin
  Result := '';
  Prefix := '/' + UpperCase(Name) + '=';
  for I := 1 to ParamCount do
    if Copy(UpperCase(ParamStr(I)), 1, Length(Prefix)) = Prefix then
      Result := Copy(ParamStr(I), Length(Prefix) + 1, Length(ParamStr(I)));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SettingsDir, OutFile: string;
  Lines: TArrayOfString;
begin
  if CurUninstallStep <> usPostUninstall then exit;
  SettingsDir := ProbeArg('PROBESETTINGS');
  OutFile := ProbeArg('PROBEOUT');
  if OutFile = '' then exit;
  SetArrayLength(Lines, 1);
  { Safety: only ever act on a folder the test runner created and marked. }
  if (SettingsDir = '') or (Pos('PLP-SWITCH-TESTS-', UpperCase(SettingsDir)) = 0) or
     not FileExists(AddBackslash(SettingsDir) + ProbeSentinel) then
    Lines[0] := 'refused'
  else
  begin
    ApplySettingsRemovalPolicy(SettingsDir);
    Lines[0] := 'done';
  end;
  SaveStringsToUTF8File(OutFile, Lines, False);
end;

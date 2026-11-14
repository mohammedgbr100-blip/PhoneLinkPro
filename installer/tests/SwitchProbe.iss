; ============================================================
;  TEST HARNESS ONLY - never shipped, never signed, never installs anything.
;  Compiled and run by installer\test-installer-switches.ps1.
;
;  Evaluates the SHIPPED switch logic (..\InstallerSwitches.iss) against the
;  real command line of a real Inno Setup process, writes the decisions to the
;  file named by /PROBEOUT=..., and exits from InitializeSetup (no wizard).
; ============================================================

[Setup]
AppId=PhoneLinkPro.SwitchProbe
AppName=PhoneLink Pro switch probe
AppVersion=0.0.0
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
OutputBaseFilename=SwitchProbe
ShowLanguageDialog=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Code]

#include "..\InstallerSwitches.iss"

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

function InitializeSetup(): Boolean;
var
  OutFile: string;
  Lines: TArrayOfString;
begin
  Result := False; { never continue into a wizard or an installation }
  OutFile := ProbeArg('PROBEOUT');
  if OutFile = '' then exit;
  SetArrayLength(Lines, 2);
  if RemoveSettingsRequested() then Lines[0] := 'removesettings=1' else Lines[0] := 'removesettings=0';
  if DowngradeAllowed() then Lines[1] := 'allowdowngrade=1' else Lines[1] := 'allowdowngrade=0';
  SaveStringsToUTF8File(OutFile, Lines, False);
end;

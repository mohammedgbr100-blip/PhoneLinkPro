{ ============================================================
  PhoneLink Pro - installer/uninstaller command-line switches.
  Shared by PhoneLinkPro.iss and the switch test harnesses in installer\tests,
  so the harnesses exercise exactly the code the shipped installer runs.
  ============================================================ }

{ /ALLOWDOWNGRADE=1 lets Setup install over a newer installed version. }
function DowngradeAllowed(): Boolean;
begin
  Result := ExpandConstant('{param:ALLOWDOWNGRADE|0}') = '1';
end;

{ ---------- /REMOVESETTINGS (uninstaller) ----------
  %AppData%\PhoneLinkPro (settings, consent, logs) is deleted ONLY on an explicit request:
    /REMOVESETTINGS   or   /REMOVESETTINGS=1          -> remove
    /REMOVESETTINGS=0, an empty or any other value,
    or no switch at all                               -> keep (fail safe)
  The switch is matched as a whole command-line argument, case-insensitively, so
  /REMOVESETTINGSX=1, -REMOVESETTINGS=1 or the text inside another argument's quoted
  value are not the switch. When it is given more than once, the last one wins.
  (The uninstaller forwards its arguments when it respawns from %TEMP%, so ParamStr
  sees them in both phases; installer\test-installer-switches.ps1 covers both.)

  The old check searched the raw command-line text for "/REMOVESETTINGS" anywhere, so
  /REMOVESETTINGS=0 was a removal request and deleted the user's settings. }

const
  RemoveSettingsSwitch = '/REMOVESETTINGS';

{ 1 = remove, 0 = keep, -1 = this argument is not the REMOVESETTINGS switch. }
function RemoveSettingsArgDecision(const Arg: string): Integer;
var
  A: string;
begin
  Result := -1;
  A := UpperCase(Trim(Arg));
  if A = RemoveSettingsSwitch then
    Result := 1
  else if Copy(A, 1, Length(RemoveSettingsSwitch) + 1) = RemoveSettingsSwitch + '=' then
  begin
    if Copy(A, Length(RemoveSettingsSwitch) + 2, Length(A)) = '1' then
      Result := 1
    else
      Result := 0;
  end;
end;

function RemoveSettingsRequested(): Boolean;
var
  I, Decision: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    Decision := RemoveSettingsArgDecision(ParamStr(I));
    if Decision >= 0 then
      Result := Decision = 1;
  end;
end;

{ Deletes SettingsDir only when the command line explicitly asks for it. }
procedure ApplySettingsRemovalPolicy(const SettingsDir: string);
begin
  if SettingsDir = '' then exit;
  if RemoveSettingsRequested() then
  begin
    DelTree(SettingsDir, True, True, True);
    Log('uninstall: user settings removed on request (/REMOVESETTINGS or /REMOVESETTINGS=1)');
  end
  else
    Log('uninstall: user settings preserved (default)');
end;

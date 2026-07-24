; ============================================================
;  PhoneLink Pro — Inno Setup installer script
;  © 2026 ENG. Mohammed Sattar
;  Build with:  ISCC.exe PhoneLinkPro.iss
; ============================================================

#define MyAppName "PhoneLink Pro"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "ENG. Mohammed Sattar"
#define MyAppExeName "PhoneLinkPro.exe"
#define PublishDir "..\bin\Release\net9.0-windows\win-x64\publish"

[Setup]
AppId={{A3F5C1E2-9B4D-4A7E-8C6F-2D1E9B7A4C30}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=© 2026 ENG. Mohammed Sattar. Includes third-party open-source components — see THIRD-PARTY-NOTICES.txt
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

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
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

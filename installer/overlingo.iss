; Inno Setup – instalátor Overlingo. Sestavení: ISCC /DAppVersion=1.0.4 installer\overlingo.iss
; (předpokládá složku "publish" z `dotnet publish ... -o publish` v kořeni repozitáře)
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{6E1B6C52-4B7B-4D1C-9A52-0A7C0E0B0C11}
AppName=Overlingo
AppVersion={#AppVersion}
AppPublisher=Overlingo contributors
DefaultDirName={autopf}\Overlingo
DefaultGroupName=Overlingo
UninstallDisplayIcon={app}\Overlingo.exe
OutputDir=Output
OutputBaseFilename=Overlingo-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
LicenseFile=..\LICENSE

[Languages]
Name: "czech"; MessagesFile: "compiler:Languages\Czech.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Overlingo"; Filename: "{app}\Overlingo.exe"
Name: "{autodesktop}\Overlingo"; Filename: "{app}\Overlingo.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Overlingo.exe"; Description: "{cm:LaunchProgram,Overlingo}"; Flags: nowait postinstall skipifsilent

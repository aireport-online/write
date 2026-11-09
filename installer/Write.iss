; Installer for Write. Built by CI with:  iscc /DAppVersion=x.y.z installer\Write.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6C3B7B6E-5C0B-4E0B-9B1E-7A51D0B3F1A2}
AppName=Write
AppVersion={#AppVersion}
AppPublisher=AI Report
DefaultDirName={autopf}\Write
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=Write-Setup-{#AppVersion}
SetupIconFile=..\src\Write.App\Assets\write.ico
UninstallDisplayIcon={app}\Write.exe
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
ChangesAssociations=yes

[Languages]
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Write"; Filename: "{app}\Write.exe"
Name: "{autodesktop}\Write"; Filename: "{app}\Write.exe"; Tasks: desktopicon

[Registry]
; Offer Write under "Openen met" for Word documents, without taking over the default program.
Root: HKA; Subkey: "Software\Classes\Write.Document"; ValueType: string; ValueName: ""; ValueData: "Word-document"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Write.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Write.exe,0"
Root: HKA; Subkey: "Software\Classes\Write.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Write.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.docx\OpenWithProgids"; ValueType: string; ValueName: "Write.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\Write.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Write.exe"" ""%1"""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\Write.exe\SupportedTypes"; ValueType: string; ValueName: ".docx"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\Write.exe\SupportedTypes"; ValueType: string; ValueName: ".rtf"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\Write.exe\SupportedTypes"; ValueType: string; ValueName: ".txt"; ValueData: ""

[Run]
Filename: "{app}\Write.exe"; Description: "{cm:LaunchProgram,Write}"; Flags: nowait postinstall skipifsilent

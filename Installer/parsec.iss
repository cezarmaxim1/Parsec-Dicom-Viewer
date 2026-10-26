; Inno Setup Script for Parsec Medical DICOM Viewer
#define MyAppName "Parsec Medical DICOM Viewer"
#define MyAppVersion "1.2 Beta"
#define MyAppPublisher "Parsec Medical Imaging"
#define MyAppExeName "Parsec.exe"
#define MyAppAssocName "DICOM Medical Image"
#define MyAppAssocExt ".dcm"
#define MyAppAssocKey StringChange(MyAppAssocName, " ", "") + MyAppAssocExt

[Setup]
AppId={{D37E88A1-8012-4217-910D-57788C8F796C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Parsec
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=F:\Rad\Installer\Output
OutputBaseFilename=Parsec_Setup_v1.2_Beta
SetupIconFile=F:\Rad\Installer\Setup\app_icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes
CloseApplicationsFilter=Parsec.exe
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "associate"; Description: "Associate .dcm files with Parsec"; GroupDescription: "File Associations:"

[Files]
Source: "F:\Rad\Installer\Payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Parsec"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Parsec"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; .dcm file association
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocExt}\OpenWithProgids"; ValueType: string; ValueName: "Parsec.Dicom"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Parsec.Dicom"; ValueType: string; ValueName: ""; ValueData: "{#MyAppAssocName}"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Parsec.Dicom\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\Parsec.Dicom\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: associate

; Directory context menu ("Open with Parsec")
Root: HKA; Subkey: "Software\Classes\Directory\shell\Parsec"; ValueType: string; ValueName: ""; ValueData: "Open with Parsec"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Directory\shell\Parsec"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKA; Subkey: "Software\Classes\Directory\shell\Parsec\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

; Background directory context menu ("Open with Parsec")
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\Parsec"; ValueType: string; ValueName: ""; ValueData: "Open with Parsec"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\Parsec"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\Parsec\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%V"""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

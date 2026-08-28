#ifndef SourceDir
  #error SourceDir must point to the prepared release directory.
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef IconFile
  #error IconFile must point to the application icon.
#endif

[Setup]
AppId={{D2449822-5657-4C87-AC1F-0F3CECF55D54}
AppName=HBK Wwise
AppVersion={#AppVersion}
AppPublisher=HBK Wwise contributors
DefaultDirName={localappdata}\Programs\HBK Wwise
DefaultGroupName=HBK Wwise
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=HbkWwise-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=no
WizardStyle=modern
LicenseFile={#SourceDir}\LICENSE.txt
UninstallDisplayIcon={app}\HbkWwise.exe
ChangesAssociations=yes
SetupLogging=yes
SetupIconFile={#IconFile}

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\HBK Wwise"; Filename: "{app}\HbkWwise.exe"; WorkingDir: "{app}"

[Registry]
Root: HKA; Subkey: "Software\Classes\.hbkproj"; ValueType: string; ValueName: ""; ValueData: "HbkWwise.Project"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\HbkWwise.Project"; ValueType: string; ValueName: ""; ValueData: "HBK Wwise project"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\HbkWwise.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\HbkWwise.exe,0"
Root: HKA; Subkey: "Software\Classes\HbkWwise.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\HbkWwise.exe"" ""%1"""

[Run]
Filename: "{app}\HbkWwise.exe"; Description: "Launch HBK Wwise"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\tools\win-x64\oo2core_9_win64.dll"

#define MyAppName "S-Music Box Auto Scheduler"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "S-Music Box"
#define MyAppExeName "SMusicBoxAutoScheduler.exe"

[Setup]
AppId={{A68C0D74-9C3E-4C3D-A5E7-7F30C9F5A210}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\SMusicBoxAutoScheduler
DefaultGroupName={#MyAppName}
OutputDir=Distribution
OutputBaseFilename=SMusicBoxAutoScheduler_Setup
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
Uninstallable=yes

[Dirs]
Name: "{localappdata}\SMusicBoxAutoScheduler\Data\Database"
Name: "{localappdata}\SMusicBoxAutoScheduler\Data\Downloads"
Name: "{localappdata}\SMusicBoxAutoScheduler\Data\Credentials"

[Files]
Source: "Distribution\Windows-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Distribution\InstallerAssets\client_secret.json"; DestDir: "{localappdata}\SMusicBoxAutoScheduler\Data\Credentials"; Flags: onlyifdoesntexist

[Icons]
Name: "{autoprograms}\S-Music Box Auto Scheduler"; Filename: "{app}\SMusicBoxAutoScheduler.exe"
Name: "{autodesktop}\S-Music Box Auto Scheduler"; Filename: "{app}\SMusicBoxAutoScheduler.exe"

[Run]
Filename: "{app}\SMusicBoxAutoScheduler.exe"; Description: "Launch S-Music Box Auto Scheduler"; Flags: postinstall nowait skipifsilent

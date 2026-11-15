[Setup]
AppId=krislechy.Castar
AppName=Castar
AppVersion={#CastarVersion}
AppPublisher=krislechy
AppPublisherURL=https://github.com/krislechy/Castar
AppUpdatesURL=https://github.com/krislechy/Castar/releases/latest
DefaultDirName={localappdata}\Programs\Castar
DefaultGroupName=Castar
UninstallDisplayIcon={app}\Castar.exe
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputBaseFilename=Castar-Setup-win-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\Castar\logo.ico

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Castar"; Filename: "{app}\Castar.exe"

[Run]
Filename: "{app}\Castar.exe"; Description: "{cm:LaunchProgram,Castar}"; Flags: nowait postinstall skipifsilent

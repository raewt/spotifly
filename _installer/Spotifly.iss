#define MyAppName "Spotifly"
#define MyAppVersion "1.2.84.465"
#define MyAppPublisher "Spotifly"
#define MyAppExeName "Spotifly.exe"

[Setup]
AppId={{E8B3C4A1-7D2F-4A91-9C6E-1F5B8A2D3E4F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Spotifly-Setup
SetupIconFile=..\_tools\spotifly.ico
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CloseApplications=yes
CloseApplicationsFilter=Spotifly.exe
RestartApplications=no
ChangesAssociations=no
UsedUserAreasWarning=no
DisableWelcomePage=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "..\Spotifly.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Spotifly.exe.sig"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Spotifly.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Spotifly.dll.sig"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\chrome_elf.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\libcef.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\libcef.dll.sig"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\libEGL.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\libGLESv2.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\d3dcompiler_47.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\vk_swiftshader.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\vulkan-1.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\chrome_100_percent.pak"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\chrome_200_percent.pak"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\resources.pak"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\icudtl.dat"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\v8_context_snapshot.bin"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\vk_swiftshader_icd.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\toast_icon.png"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Apps\xpui.spa"; DestDir: "{app}\Apps"; Flags: ignoreversion
Source: "..\Apps\login.spa"; DestDir: "{app}\Apps"; Flags: ignoreversion
Source: "..\locales\*"; DestDir: "{app}\locales"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Cache"
Type: filesandordirs; Name: "{app}\GPUCache"
Type: filesandordirs; Name: "{app}\Code Cache"
Type: filesandordirs; Name: "{app}\DawnCache"

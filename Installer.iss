[Setup]
AppId={{8F9E4A12-3C7B-4D9A-9E15-7C8A2B4F6031}
AppName=codeLess
AppVersion=3.4.1
AppVerName=codeLess Native Agentic IDE v3.4.1
AppPublisher=Savatarr
AppPublisherURL=https://github.com/Sambhrantsingh777/codeLess
AppSupportURL=https://github.com/Sambhrantsingh777/codeLess/issues
AppUpdatesURL=https://github.com/Sambhrantsingh777/codeLess/releases
AppCopyright=Copyright (C) 2026 Savatarr. Released under the MIT License.
VersionInfoVersion=3.4.1.0
VersionInfoTextVersion=3.4.1
VersionInfoCompany=Savatarr
VersionInfoDescription=codeLess Native Agentic IDE Setup
VersionInfoCopyright=Copyright (C) 2026 Savatarr. Released under the MIT License.
VersionInfoProductName=codeLess
VersionInfoProductVersion=3.4.1.0
VersionInfoProductTextVersion=3.4.1
VersionInfoOriginalFileName=codeLess-Setup.exe
DefaultDirName={autopf}\codeLess
DefaultGroupName=codeLess
OutputDir=Output
OutputBaseFilename=codeLess-Setup
SetupIconFile=assets\savatarr_app.ico
ChangesAssociations=yes
UninstallDisplayName=codeLess (Savatarr)
UninstallDisplayIcon={app}\codeLess.exe
WizardImageFile=assets\wizard_large.bmp
WizardSmallImageFile=assets\wizard_small.bmp
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
WizardStyle=modern
#ifdef USE_SIGNTOOL
SignTool=signtool $f
SignedUninstaller=yes
#endif

[Files]
Source: "codeLess.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "codeLess.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "assets\codeless.ico"; DestDir: "{app}\assets"; Flags: ignoreversion
Source: "assets\savatarr.ico"; DestDir: "{app}\assets"; Flags: ignoreversion
Source: "assets\savatarr_app.ico"; DestDir: "{app}\assets"; Flags: ignoreversion
Source: "assets\savatarr_bolt.png"; DestDir: "{app}\assets"; Flags: ignoreversion
Source: "assets\savatarr_full_logo.png"; DestDir: "{app}\assets"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\codeLess IDE"; Filename: "{app}\codeLess.exe"; IconFilename: "{app}\assets\savatarr_app.ico"; AppUserModelID: "Savatarr.codeLess"
Name: "{autodesktop}\codeLess IDE"; Filename: "{app}\codeLess.exe"; IconFilename: "{app}\assets\savatarr_app.ico"; AppUserModelID: "Savatarr.codeLess"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\codeLess"; ValueType: string; ValueName: ""; ValueData: "Open with codeLess"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\codeLess"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\codeLess.exe"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\codeLess\command"; ValueType: string; ValueName: ""; ValueData: """{app}\codeLess.exe"" ""%V"""; Flags: uninsdeletekey

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Run]
Filename: "{app}\codeLess.exe"; Description: "{cm:LaunchProgram,codeLess}"; Flags: nowait postinstall skipifsilent

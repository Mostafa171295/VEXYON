; VEXYON installer script (Inno Setup 6)
; Run BuildRelease.bat to build the exe and compile this file automatically.

#define MyAppName "VEXYON"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Eng Moustafa Khalaf"
#define MyAppURL "mailto:mostafakhalaf861@gmail.com"
#define MyAppExe "VEXYON.exe"

[Setup]
; Keep this GUID fixed for all versions of the app. Generate your own in Inno Setup: Tools > Generate GUID
AppId={{B7D3C2A1-5E4F-4A86-9C1D-2F6E8A0B7C93}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppSupportPhone=+201030087988
AppContact=mostafakhalaf861@gmail.com
AppCopyright=(c) 2026 Eng Moustafa Khalaf
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
OutputDir=Output
OutputBaseFilename=VEXYON-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Agreement shown during installation:
LicenseFile=EULA.txt
; Installer icon (same as the app icon):
SetupIconFile=icon.ico

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "Release\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion
Source: "THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "EULA.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

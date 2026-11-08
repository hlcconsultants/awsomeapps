; Per-user installer: no admin prompt, installs to %LocalAppData%\Programs\FastGet.
#define AppName "FastGet"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6F4C2B1E-8D3A-4E57-9B60-2C7A1F5D9E34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\FastGet.SingleInstance
CloseApplications=yes
OutputDir=output
OutputBaseFilename=FastGet-Setup-{#AppVersion}
SetupIconFile=..\src\FastGet.App\app.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\FastGet.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Messages]
FinishedLabel=FastGet is installed.%n%nTo capture downloads in Chrome, add the FastGet extension. See "Connect Chrome" in the Start menu.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\extension\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\FastGet"; Filename: "{app}\FastGet.exe"
Name: "{group}\Connect Chrome"; Filename: "{app}\extension"; Comment: "Folder to select under Load unpacked in chrome://extensions"
Name: "{autodesktop}\FastGet"; Filename: "{app}\FastGet.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FastGet.exe"; Parameters: "--register"; Flags: runhidden waituntilterminated
Filename: "{app}\FastGet.exe"; Description: "Start FastGet"; Flags: postinstall nowait skipifsilent

[Registry]
; Written by the app; listed here so uninstall removes them.
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.fastget.host"; ValueType: none; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "FastGet"; Flags: dontcreatekey uninsdeletevalue

[UninstallDelete]
Type: files; Name: "{userappdata}\FastGet\com.fastget.host.json"

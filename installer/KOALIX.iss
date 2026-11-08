; Inno Setup 6 script for KOALIX (not compiled in CI yet — build on Windows with ISCC.exe)
#define AppName "KOALIX"
#define AppVersion "0.1.0"
#define AppExe "KOALIX.exe"

[Setup]
AppId={{6E7C2B4D-1F0A-4C8E-9B3A-4E4558555301}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=KOALIX
DefaultDirName={autopf}\KOALIX
DefaultGroupName=KOALIX
OutputBaseFilename=KOALIX-Setup-{#AppVersion}-x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\Koalix.App\Assets\koalix.ico

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
; Persian messages file is available from the Inno Setup unofficial translations (Farsi.isl)
; Name: "fa"; MessagesFile: "compiler:Languages\Farsi.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\artifacts\win-x64\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\KOALIX"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\KOALIX"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,KOALIX}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove leftover kill-switch firewall rules and stop engines if the app was killed.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -Command ""Get-NetFirewallRule -DisplayName KOALIX-KillSwitch-* -ErrorAction SilentlyContinue | Remove-NetFirewallRule"""; Flags: runhidden; RunOnceId: "fwclean"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM sing-box.exe"; Flags: runhidden; RunOnceId: "killsb"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM xray.exe"; Flags: runhidden; RunOnceId: "killxr"

[Code]
// User data (%LOCALAPPDATA%\KOALIX) is intentionally kept on uninstall (profiles, logs).

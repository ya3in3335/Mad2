; VELTRIX OS — Inno Setup 6 script
; Build:  ISCC.exe /DAppVersion=0.1.0 /DPublishDir=C:\path\to\publish VeltrixOS.iss
; The installer is NOT code-signed unless a signing step is added with a real certificate.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\Veltrix.Desktop\bin\Release\net10.0-windows\win-x64\publish"
#endif
; Attendance-terminal connector (Windows service), published separately: dotnet publish apps/windows/Veltrix.Connector -o publish-connector
#ifndef ConnectorDir
  #define ConnectorDir AddBackslash(PublishDir) + "..\publish-connector"
#endif

; 32-bit ZKTeco SDK bridge (the ZKTeco SDK itself is NOT bundled: see tools\zkteco\Install-ZktecoSdk.ps1)
#ifndef ZkBridgeDir
  #define ZkBridgeDir AddBackslash(PublishDir) + "..\publish-zkbridge"
#endif

[Setup]
AppId={{6E1B7C2A-9F4D-4D8B-A1C3-5E2F7A9B0C41}
AppName=VELTRIX OS
AppVersion={#AppVersion}
AppVerName=VELTRIX OS {#AppVersion}
AppPublisher=VELTRIX
DefaultDirName={autopf}\VELTRIX OS
DefaultGroupName=VELTRIX OS
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
WizardImageFile=wizard-image.bmp
WizardSmallImageFile=wizard-small.bmp
SetupIconFile=..\Veltrix.Desktop\Assets\veltrix.ico
UninstallDisplayIcon={app}\VeltrixOS.exe
OutputBaseFilename=VELTRIX-OS-Windows-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
MinVersion=10.0.17763

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
; Hand-off folder: the app (user) writes connector.json (database location, no secrets); the service only accepts a VELTRIX OS
; database inside a user profile. Everything else in %ProgramData%\VELTRIX stays admin-only.
Name: "{commonappdata}\VELTRIX\Connector"; Permissions: users-modify

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ConnectorDir}\*"; DestDir: "{app}\Connector"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ZkBridgeDir}\*"; DestDir: "{app}\ZkBridge"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\..\tools\zkteco\Install-ZktecoSdk.ps1"; DestDir: "{app}\ZkBridge"; Flags: ignoreversion

[Icons]
; Ctrl+Alt+V launches VELTRIX OS (or brings the running window to front: the app is single-instance).
; Win+V cannot be used: Windows reserves it for clipboard history.
Name: "{group}\VELTRIX OS"; Filename: "{app}\VeltrixOS.exe"; HotKey: ctrl+alt+v
Name: "{group}\{cm:UninstallProgram,VELTRIX OS}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\VELTRIX OS"; Filename: "{app}\VeltrixOS.exe"; Tasks: desktopicon

[Run]
; VELTRIX Connector: polls attendance terminals even when the app is closed (LocalSystem, delayed automatic start, restarts on failure).
Filename: "{sys}\sc.exe"; Parameters: "create VeltrixConnector binPath= ""{app}\Connector\VeltrixConnector.exe"" start= delayed-auto DisplayName= ""VELTRIX Connector"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "config VeltrixConnector binPath= ""{app}\Connector\VeltrixConnector.exe"" start= delayed-auto"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description VeltrixConnector ""VELTRIX OS: imports attendance-terminal events into the local database."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure VeltrixConnector reset= 86400 actions= restart/60000/restart/60000/restart/300000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start VeltrixConnector"; Flags: runhidden waituntilterminated
Filename: "{app}\VeltrixOS.exe"; Description: "{cm:LaunchProgram,VELTRIX OS}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop VeltrixConnector"; Flags: runhidden waituntilterminated; RunOnceId: "StopConnector"
Filename: "{sys}\timeout.exe"; Parameters: "/t 3 /nobreak"; Flags: runhidden waituntilterminated; RunOnceId: "WaitConnector"
Filename: "{sys}\sc.exe"; Parameters: "delete VeltrixConnector"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteConnector"

[Code]
// Upgrade: stop the running connector before its files are replaced (the [Run] entries restart it).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop VeltrixConnector', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(3000);
  Result := '';
end;

// Anti-copy: the device binding is created HERE, while setup runs elevated. VeltrixOS.exe --bind writes the binding
// (DPAPI LocalMachine + HMAC) to %ProgramData%\VELTRIX\binding.dat and HKLM\SOFTWARE\VELTRIX, both admin-only. The normal
// app only verifies it, so program files copied to another PC refuse to start there. The vendor deletes the installer afterwards.
// Exit codes: 0 = bound, 2 = not elevated, other = failure.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not Exec(ExpandConstant('{app}\VeltrixOS.exe'), '--bind', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('VELTRIX OS could not be bound to this computer (code ' + IntToStr(ResultCode) + '). ' +
        'Run the installer again as administrator; the application will not start until it is bound.', mbCriticalError, MB_OK);
  end;
end;

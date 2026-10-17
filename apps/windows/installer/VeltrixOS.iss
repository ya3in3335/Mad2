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

; 32-bit ZKTeco SDK bridge. The ZKTeco SDK itself is not bundled: setup downloads it from ZKTeco's GitHub (SHA-256 pinned).
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

[UninstallDelete]
; ZKTeco SDK files downloaded by setup / the app (not tracked by the uninstaller otherwise)
Type: filesandordirs; Name: "{app}\ZkBridge"

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop VeltrixConnector"; Flags: runhidden waituntilterminated; RunOnceId: "StopConnector"
Filename: "{sys}\timeout.exe"; Parameters: "/t 3 /nobreak"; Flags: runhidden waituntilterminated; RunOnceId: "WaitConnector"
Filename: "{sys}\sc.exe"; Parameters: "delete VeltrixConnector"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteConnector"

[Code]
// ZKTeco Standalone SDK 13 files: downloaded from ZKTeco's official GitHub repository with pinned SHA-256 (same list as
// ZkTecoSdkFiles.cs), copied next to the bridge. Offline setup continues; the app offers "Download ZKTeco SDK" later.
var
  DownloadPage: TDownloadWizardPage;
  ZkDownloaded: Boolean;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'ZKTeco Standalone SDK', @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpReady then
  begin
    DownloadPage.Clear;
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/commpro.dll', 'commpro.dll', '4d574b6268a7f8478b189de83d66a2467fb65cdda60113d75aa8f236a34b10fd');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/comms.dll', 'comms.dll', 'cbb6b257d89d787662b0bb1d94fb63b19d26681095d771229be68a426c53c2ff');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/plcommpro.dll', 'plcommpro.dll', '042855e5409ad48f399fdb0b0fd50f737fad04f31a21205e2a61087ae8117df2');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/plcomms.dll', 'plcomms.dll', '2fea08225edb6169138dea938e7d8aebd88d54ba7a4767d4076abf70c879efc4');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/plrscagent.dll', 'plrscagent.dll', 'ba0dd7b6a7684f698ca120aa7f1dadf5c063c21929b05acc7a052766dac2755c');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/plrscomm.dll', 'plrscomm.dll', '3a984350c94a68385fe4864a0f872d89b5d8a07aa1ef4dd5f378e6643d4835f6');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/pltcpcomm.dll', 'pltcpcomm.dll', 'd7af6d2b1c64e0b2d9dc366d5ecd4f92f85d06de51102903f3886087c4f5d2c7');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/rscagent.dll', 'rscagent.dll', 'f4c4dae04630cdb0117192bfef300a9553ab00964ea804babfdc9bea1c6764de');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/rscomm.dll', 'rscomm.dll', '12d4393ad481dd70c7b0b12e4021952c8ad51624059c9d5f421707d865595429');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/tcpcomm.dll', 'tcpcomm.dll', '1a082e01885e3ca67c858bcfbabf5191cc5b85b77e374edc5596478af2d53167');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/usbcomm.dll', 'usbcomm.dll', '0f523f2333d2e01cbbba31544bf2a2d3a57e1d9ad5642d2f47f1b1d5d53a6d89');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/zkemkeeper.dll', 'zkemkeeper.dll', '134d58586597746c788c37997d0ff64056bfac97e6cc73ca29a5a004eccd47b5');
  DownloadPage.Add('https://raw.githubusercontent.com/ZKTeco/Standalone-SDK/master/Communication%20Protocol%20SDK(32Bit%20Ver6.2.4.11)/sdk/zkemsdk.dll', 'zkemsdk.dll', '0c94035aa1a50f83069e77d8d9ee393a1e40f4e81824bcbb20ae775d8e587a40');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        ZkDownloaded := True;
      except
        ZkDownloaded := False;
        Log('ZKTeco SDK download skipped: ' + GetExceptionMessage);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

procedure CopyZkTecoSdk;
begin
  if ZkDownloaded then
  begin
    FileCopy(ExpandConstant('{tmp}\commpro.dll'), ExpandConstant('{app}\ZkBridge\commpro.dll'), False);
    FileCopy(ExpandConstant('{tmp}\comms.dll'), ExpandConstant('{app}\ZkBridge\comms.dll'), False);
    FileCopy(ExpandConstant('{tmp}\plcommpro.dll'), ExpandConstant('{app}\ZkBridge\plcommpro.dll'), False);
    FileCopy(ExpandConstant('{tmp}\plcomms.dll'), ExpandConstant('{app}\ZkBridge\plcomms.dll'), False);
    FileCopy(ExpandConstant('{tmp}\plrscagent.dll'), ExpandConstant('{app}\ZkBridge\plrscagent.dll'), False);
    FileCopy(ExpandConstant('{tmp}\plrscomm.dll'), ExpandConstant('{app}\ZkBridge\plrscomm.dll'), False);
    FileCopy(ExpandConstant('{tmp}\pltcpcomm.dll'), ExpandConstant('{app}\ZkBridge\pltcpcomm.dll'), False);
    FileCopy(ExpandConstant('{tmp}\rscagent.dll'), ExpandConstant('{app}\ZkBridge\rscagent.dll'), False);
    FileCopy(ExpandConstant('{tmp}\rscomm.dll'), ExpandConstant('{app}\ZkBridge\rscomm.dll'), False);
    FileCopy(ExpandConstant('{tmp}\tcpcomm.dll'), ExpandConstant('{app}\ZkBridge\tcpcomm.dll'), False);
    FileCopy(ExpandConstant('{tmp}\usbcomm.dll'), ExpandConstant('{app}\ZkBridge\usbcomm.dll'), False);
    FileCopy(ExpandConstant('{tmp}\zkemkeeper.dll'), ExpandConstant('{app}\ZkBridge\zkemkeeper.dll'), False);
    FileCopy(ExpandConstant('{tmp}\zkemsdk.dll'), ExpandConstant('{app}\ZkBridge\zkemsdk.dll'), False);
  end;
end;

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
    CopyZkTecoSdk;
    if not Exec(ExpandConstant('{app}\VeltrixOS.exe'), '--bind', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('VELTRIX OS could not be bound to this computer (code ' + IntToStr(ResultCode) + '). ' +
        'Run the installer again as administrator; the application will not start until it is bound.', mbCriticalError, MB_OK);
  end;
end;

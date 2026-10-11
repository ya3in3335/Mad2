; VELTRIX OS — Inno Setup 6 script
; Build:  ISCC.exe /DAppVersion=0.1.0 /DPublishDir=C:\path\to\publish VeltrixOS.iss
; The installer is NOT code-signed unless a signing step is added with a real certificate.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\Veltrix.Desktop\bin\Release\net10.0-windows\win-x64\publish"
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

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\VELTRIX OS"; Filename: "{app}\VeltrixOS.exe"
Name: "{group}\{cm:UninstallProgram,VELTRIX OS}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\VELTRIX OS"; Filename: "{app}\VeltrixOS.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VeltrixOS.exe"; Description: "{cm:LaunchProgram,VELTRIX OS}"; Flags: nowait postinstall skipifsilent

[Code]
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

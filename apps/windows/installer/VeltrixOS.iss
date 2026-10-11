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

[Dirs]
; Device binding + one-time install token. The app runs unelevated (asInvoker), so Users get modify rights here to
; consume the token and write %ProgramData%\VELTRIX\binding.dat.
Name: "{commonappdata}\VELTRIX"; Permissions: users-modify

[Registry]
; Second binding location (HKLM\SOFTWARE\VELTRIX, value "Binding"), writable by the unelevated app.
Root: HKLM64; Subkey: "SOFTWARE\VELTRIX"; Permissions: users-modify; Flags: uninsdeletekeyifempty

[Icons]
Name: "{group}\VELTRIX OS"; Filename: "{app}\VeltrixOS.exe"
Name: "{group}\{cm:UninstallProgram,VELTRIX OS}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\VELTRIX OS"; Filename: "{app}\VeltrixOS.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VeltrixOS.exe"; Description: "{cm:LaunchProgram,VELTRIX OS}"; Flags: nowait postinstall skipifsilent

[Code]
// Anti-copy, trust-on-first-use: every (re)installation by the vendor writes a fresh one-time token. On its first start the
// app binds itself to this PC (DPAPI LocalMachine) and deletes the token. Program files copied to another PC have no token and
// no valid binding there, so the app refuses to start. The vendor deletes the installer after installing.
function NewInstallToken(): String;
begin
  Result := GetSHA256OfString(GetDateTimeString('yyyymmddhhnnsszzz', #0, #0) + '|' + IntToStr(Random(2147483647)) + '|' +
    IntToStr(Random(2147483647)) + '|' + ExpandConstant('{computername}') + '|' + ExpandConstant('{app}'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Dir: String;
begin
  if CurStep = ssPostInstall then
  begin
    Dir := ExpandConstant('{commonappdata}\VELTRIX');
    ForceDirectories(Dir);
    if not SaveStringToFile(Dir + '\install.token', NewInstallToken(), False) then
      MsgBox('Could not write the installation token. The application will not start.', mbError, MB_OK);
  end;
end;

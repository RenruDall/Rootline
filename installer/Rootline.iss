; Rootline installer (Inno Setup 6). Per-user install: no admin rights needed.
; Build: iscc /DAppVersion=1.0.0 installer\Rootline.iss   (after building src\Rootline.App in Release)
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{0339420D-CACC-4B40-95DA-18E64074DF36}
AppName=Rootline
AppVersion={#AppVersion}
AppVerName=Rootline {#AppVersion}
AppPublisher=Michael Ladurner
AppCopyright=© 2026 Michael Ladurner · Apache License 2.0
AppPublisherURL=https://github.com/RenruDall/Rootline
DefaultDirName={localappdata}\Programs\Rootline
DefaultGroupName=Rootline
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Rootline-Setup-{#AppVersion}
SetupIconFile=..\src\Rootline.App\rootline.ico
UninstallDisplayIcon={app}\Rootline.exe
UninstallDisplayName=Rootline
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; Flags: unchecked

[Files]
Source: "..\src\Rootline.App\bin\Release\net48\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Excludes: "*.pdb"
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Rootline"; Filename: "{app}\Rootline.exe"
Name: "{autodesktop}\Rootline"; Filename: "{app}\Rootline.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Rootline.exe"; Description: "Open Rootline"; Flags: nowait postinstall skipifsilent

[Code]
// Rootline shows its window with Microsoft Edge WebView2 (part of Windows 11 and most Windows 10 PCs).
function WebView2Installed(): Boolean;
var v: String;
begin
  Result := RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0');
  if not Result then
    Result := RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var code: Integer;
begin
  if (CurStep = ssPostInstall) and (not WebView2Installed()) and (not WizardSilent()) then
    if MsgBox('Rootline needs the Microsoft Edge WebView2 Runtime, which is missing on this PC.' + #13#10 + #13#10 +
              'Open the Microsoft download page now?', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://go.microsoft.com/fwlink/p/?LinkId=2124703', '', '', SW_SHOWNORMAL, ewNoWait, code);
end;

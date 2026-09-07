; Inno Setup script for WasabiDrive.
; Build the app first with scripts\publish.ps1, then compile this with ISCC.exe
; (Inno Setup 6+, https://jrsoftware.org/isdl.php). scripts\build-installer.ps1 does both.

#define AppName "WasabiDrive"
#define AppVersion "0.10.0"
#define AppPublisher "RHC Solutions"
#define AppPublisherUrl "https://rhcsolutions.com/"
#define PublishDir "..\src\WasabiDrive.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
#define WinFspMsi "..\third_party\winfsp\winfsp.msi"
; Keep in lockstep with the winfsp.net package (see third_party/winfsp/SOURCE.txt).
#define WinFspVersion "2.1.25156"
#define AppIcon "..\src\WasabiDrive.App\Assets\wasabidrive.ico"

[Setup]
AppId={{5F3B8C2A-7E1D-4C6A-9B0E-WASABIDRIVE01}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppPublisherUrl}
AppSupportURL={#AppPublisherUrl}
AppUpdatesURL=https://github.com/RHC-Solutions/WasabiDrive/releases
AppCopyright=© RHC Solutions. https://rhcsolutions.com/
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=.\output
OutputBaseFilename=WasabiDrive-Setup-{#AppVersion}
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\WasabiDrive.exe
Compression=lzma2
SolidCompression=yes
; Per-user install (no elevation) keeps rclone mounts in the user session; WinFsp still needs admin.
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#WinFspMsi}"; DestDir: "{tmp}"; DestName: "winfsp.msi"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\WasabiDrive.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\WasabiDrive.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
; Install WinFsp when it is missing OR older than the version we ship. An older driver is not
; good enough: the managed binding refuses to load against a different major.minor.
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\winfsp.msi"" /qn /norestart"; \
  StatusMsg: "Installing WinFsp {#WinFspVersion} (required to mount drives)..."; \
  Flags: waituntilterminated; Check: WinFspNeedsInstall
; Offer to launch the app after install.
Filename: "{app}\WasabiDrive.exe"; Description: "Launch WasabiDrive"; \
  Flags: nowait postinstall skipifsilent

[Code]
{ WinFsp records no version in the registry, so the installed version is read from the file
  version of its own DLL under InstallDir. }
function TryGetInstalledWinFspVersion(var Packed: Int64): Boolean;
var
  Dir: String;
begin
  Result := False;
  if not RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\WinFsp', 'InstallDir', Dir) then
    if not RegQueryStringValue(HKLM, 'SOFTWARE\WinFsp', 'InstallDir', Dir) then
      exit;
  if Dir = '' then
    exit;
  Result := GetPackedVersion(AddBackslash(Dir) + 'bin\winfsp-x64.dll', Packed);
end;

function WinFspNeedsInstall(): Boolean;
var
  Installed, Required: Int64;
begin
  { Nothing installed, or a driver we cannot identify: install ours. }
  if not TryGetInstalledWinFspVersion(Installed) then
  begin
    Result := True;
    exit;
  end;
  if not StrToVersion('{#WinFspVersion}', Required) then
  begin
    Result := False;
    exit;
  end;
  Result := ComparePackedVersion(Installed, Required) < 0;
end;

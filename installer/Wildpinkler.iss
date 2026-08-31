#ifndef AppVersion
  #error AppVersion must be passed to ISCC.exe.
#endif

#ifndef SourceDir
  #error SourceDir must be passed to ISCC.exe.
#endif

#ifndef OutputDir
  #error OutputDir must be passed to ISCC.exe.
#endif

#define AppName "Wildpinkler"
#define AppExeName "Wildpinkler.App.exe"
#define DotNetDesktopRuntimeUrl "https://dotnet.microsoft.com/download/dotnet/8.0/runtime"

[Setup]
AppId={{76C108DF-2AC9-4EFD-A109-6E12C01A739D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Wildpinkler
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Wildpinkler-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExeName}

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Code]
function HasDotNetDesktopRuntime8: Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  if RegGetSubkeyNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Versions) then
  begin
    for Index := 0 to GetArrayLength(Versions) - 1 do
    begin
      if Pos('8.', Versions[Index]) = 1 then
      begin
        Result := True;
        exit;
      end;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := HasDotNetDesktopRuntime8;
  if not Result then
  begin
    MsgBox(
      '{#AppName} requires the x64 .NET 8 Desktop Runtime. Install it, then run this installer again.',
      mbError,
      MB_OK);
    ShellExec('open', '{#DotNetDesktopRuntimeUrl}', '', '', SW_SHOWNORMAL, ewNoWait, False);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox(
      'Remove Wildpinkler user data from %LOCALAPPDATA%? This permanently deletes profiles, downloaded archives, and mod installations.',
      mbConfirmation,
      MB_YESNO) = IDYES then
    begin
      DelTree(ExpandConstant('{localappdata}\Wildpinkler'), True, True, True);
    end;
  end;
end;
; Compositor 한국어판 — the installer, one script for both builds (docs/progress.md 9).
;
; Per user, no administrator: it goes to %LOCALAPPDATA%\Programs\Compositor_korean_win and
; registers under HKCU, so Settings > Apps (Add or Remove Programs) lists it and its uninstaller
; takes everything away — the program folder, and the settings the app keeps in
; %APPDATA%\Compositor_korean_win. The app writes nowhere else.
;
; The install folder goes on the user's PATH — a task on the Additional Tasks page, ticked by
; default — so an assistant told "use the compositor command"
; finds compositor.exe wherever this was installed (docs/progress.md 26); uninstalling takes it off.
;
; Both builds share one AppId, so they are one program to Windows: installing either over the
; other replaces it, and the plain build clears the AI build's runtime and model out of the way.
;
; CI passes AppVersion, Source (the package folder), WithAi ("1" or "0", a string as /D gives
; every value) and OutputName.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Source
  #define Source "..\build\package-plain"
#endif
#ifndef WithAi
  #define WithAi "0"
#endif
#ifndef OutputName
  #define OutputName "Compositor_korean_win-setup"
#endif
#ifndef OutputDir
  #define OutputDir "..\build\installers"
#endif

#define AppName "Compositor 한국어판"
#define AppExe "Compositor_korean_win.exe"
#define Repository "https://github.com/dreamurl/Compositor_korean_win"

[Setup]
AppId={{8F3C2A51-6D4B-4E7A-9C1E-2B5D7F0A9E34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=dreamurl
AppPublisherURL={#Repository}
AppSupportURL={#Repository}/issues
AppUpdatesURL={#Repository}/releases
DefaultDirName={autopf}\Compositor_korean_win
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
; The PATH change reaches newly started programs without signing out.
ChangesEnvironment=yes

[Languages]
#if FileExists(CompilerPath + "Languages\Korean.isl")
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
#if FileExists(CompilerPath + "Languages\Korean.isl")
korean.AiGroup=AI 연결:
korean.AddPath=AI가 쓰는 compositor 명령어 등록 (설치 폴더를 PATH에 추가)
#endif
english.AiGroup=AI connection:
english.AddPath=Register the compositor command for AI assistants (adds the install folder to PATH)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Ticked unless the person unticks it; uninstalling takes the entry off either way.
Name: "addpath"; Description: "{cm:AddPath}"; GroupDescription: "{cm:AiGroup}"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

#if WithAi == "0"
[InstallDelete]
; Over the AI build: its runtime, model and their licences go.
Type: files; Name: "{app}\onnxruntime.dll"
Type: files; Name: "{app}\onnxruntime_providers_shared.dll"
Type: files; Name: "{app}\DirectML.dll"
Type: filesandordirs; Name: "{app}\models"
Type: filesandordirs; Name: "{app}\licenses"
#endif

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; \
    Tasks: addpath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The settings the app keeps per user, so nothing of it is left behind.
Type: filesandordirs; Name: "{userappdata}\Compositor_korean_win"
Type: dirifempty; Name: "{app}"

[Code]
{ Whether Dir is missing from the user's Path, compared without case and as a whole entry. }
function NeedsAddPath(Dir: string): Boolean;
var
  Current: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Current) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Current) + ';') = 0;
end;

{ Takes Dir back off the user's Path, leaving every other entry as it was. }
procedure RemovePath(Dir: string);
var
  Current, Padded: string;
  At: Integer;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Current) then exit;
  Padded := ';' + Current + ';';
  At := Pos(';' + Uppercase(Dir) + ';', Uppercase(Padded));
  if At = 0 then exit;
  Delete(Padded, At, Length(Dir) + 1);
  Current := Copy(Padded, 2, Length(Padded) - 2);
  RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Current);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RemovePath(ExpandConstant('{app}'));
end;

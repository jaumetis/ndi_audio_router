; Instal·lador d'NDI Audio Router (Inno Setup 6)
; Ús local:  iscc /DMyAppVersion=1.0.0 installer\NdiAudioRouter.iss   (cal haver executat publish.bat abans)

#define MyAppName "NDI Audio Router"
#define MyAppExe  "NdiAudioRouter.exe"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{6F1D9B52-3C7A-4E0B-9A41-5D2E8C7B1A90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=jaumetis
AppPublisherURL=https://github.com/jaumetis/ndi_audio_router
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=NdiAudioRouter-Setup-{#MyAppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "catalan"; MessagesFile: "compiler:Languages\Catalan.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// L'app necessita NDI Runtime (NDI Tools). Si no hi és, ho avisem i oferim obrir la pàgina de descàrrega.
function NdiRuntimeFound: Boolean;
begin
  Result := (GetEnv('NDI_RUNTIME_DIR_V6') <> '') or (GetEnv('NDI_RUNTIME_DIR_V5') <> '')
         or (GetEnv('NDI_RUNTIME_DIR_V4') <> '');
end;

function InitializeSetup: Boolean;
var
  ErrCode: Integer;
begin
  Result := True;
  if not NdiRuntimeFound then
  begin
    if MsgBox('No s''ha trobat NDI Runtime (NDI Tools) en aquest ordinador.' + #13#10 +
              'El programa el necessita per emetre l''stream NDI.' + #13#10#13#10 +
              'Vols obrir la pàgina de descàrrega d''NDI Tools ara?' + #13#10 +
              '(La instal·lació continuarà igualment.)',
              mbInformation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://ndi.video/tools/', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
  end;
end;

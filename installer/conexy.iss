; ConexyAI Desktop — установщик (Inno Setup 6+)
; Сборка:
;   iscc installer\conexy.iss /DMyAppVersion=0.9.29 /DMySourceDir=dist
; Подпись exe выполняется ДО сборки установщика (scripts\publish.ps1), иначе SmartScreen
; и антивирусы будут ругаться на неподписанный установщик.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\dist"
#endif

[Setup]
AppId={{6C1E6F4A-9B37-4A8B-9E6D-3B7C5D2A1F84}
AppName=ConexyAI Desktop
AppVersion={#MyAppVersion}
AppPublisher=ConexyAI
AppPublisherURL=https://conexyai.ru
AppSupportURL=https://conexyai.ru
AppUpdatesURL=https://conexyai.ru
DefaultDirName={localappdata}\ConexyAI
DefaultGroupName=ConexyAI
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=conexy-setup-{#MyAppVersion}
SetupIconFile=..\ConexyAI.Desktop\conexy.ico
UninstallDisplayIcon={app}\conexy.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
ChangesEnvironment=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Дополнительно:"
Name: "addtopath"; Description: "Добавить conexy в PATH (запуск из любого терминала)"; GroupDescription: "Дополнительно:"

[Files]
Source: "{#MySourceDir}\conexy-win-x64.exe"; DestDir: "{app}"; DestName: "conexy.exe"; Flags: ignoreversion
Source: "..\ConexyAI.Desktop\conexy.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\ConexyAI"; Filename: "{app}\conexy.exe"; WorkingDir: "{userdocs}"; IconFilename: "{app}\conexy.ico"; Tasks: desktopicon
Name: "{group}\ConexyAI"; Filename: "{app}\conexy.exe"; WorkingDir: "{userdocs}"; IconFilename: "{app}\conexy.ico"

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath('{app}'); Flags: preservestringtype

[Run]
Filename: "{app}\conexy.exe"; Description: "Открыть Conexy в терминале"; Flags: postinstall nowait skipifsilent

[Code]
function NeedsAddPath(Param: string): boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Param) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Path: string;
  AppDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AppDir := ExpandConstant('{app}');
    if RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Path) then
    begin
      StringChangeEx(Path, ';' + AppDir, '', True);
      StringChangeEx(Path, AppDir + ';', '', True);
      RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', Path);
    end;
  end;
end;

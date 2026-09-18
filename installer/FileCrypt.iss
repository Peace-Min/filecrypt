; FileCrypt - Inno Setup 스크립트
;
; 단일 setup.exe 를 만들고 싶을 때 씁니다. 필수는 아닙니다.
; 설치.cmd 로도 동일하게 설치되며, 그쪽은 별도 도구가 필요 없습니다.
;
; 쓰는 법
;   1) Inno Setup 6 설치 (https://jrsoftware.org/isdl.php)
;   2) gui 폴더에서 dotnet build -c Release
;   3) 이 파일을 Inno Setup Compiler 로 열고 F9
;   -> installer\Output\FileCrypt-Setup-2.0.0.exe 생성
;
; 관리자 권한 없이 현재 사용자에게만 설치합니다 (PrivilegesRequired=lowest).

#define MyAppName      "FileCrypt"
#define MyAppVersion   "2.0.0"
#define MyAppPublisher "FileCrypt"
#define MyAppExeName   "FileCrypt.exe"

[Setup]
AppId={{8F3A1C24-5B7E-4D91-A2E6-9C0F1B4D7E32}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=FileCrypt-Setup-{#MyAppVersion}
SetupIconFile=..\gui\FileCrypt.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
; 앱이 x64 로 고정돼 있다(WebView2 네이티브 로더와 아키텍처를 맞추기 위해).
; 32비트 Windows 에서는 아예 설치를 막는다 - 설치된 뒤에 죽는 것보다 낫다.
ArchitecturesAllowed=x64compatible

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 바로가기 만들기"; GroupDescription: "추가 작업:"

[Files]
Source: "..\gui\bin\Release\net48\FileCrypt.exe";        DestDir: "{app}"; Flags: ignoreversion
Source: "..\gui\bin\Release\net48\FileCrypt.exe.config"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\gui\FileCrypt.ico";                          DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md";                                  DestDir: "{app}"; Flags: ignoreversion

; 근태관리 연동에 필요한 DLL 전부(WebView2 + System.Text.Json 계열).
; 개별로 나열하면 의존성이 하나 늘 때마다 또 빠뜨린다 - 개발 폴더에서는 되는데
; 설치본만 죽는 함정이라, 빌드가 내놓은 DLL 을 통째로 넣는다.
; WebView2Loader.dll 은 네이티브라 exe 와 같은 x64 여야 한다(0x8007000B 방지).
Source: "..\gui\bin\Release\net48\*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";        Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "지금 실행"; Flags: nowait postinstall skipifsilent

[Code]
// .NET Framework 4.8 확인. Windows 10/11 에는 기본 포함돼 있으므로 보통 통과한다.
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  Result := True;
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) then
  begin
    if Release < 528040 then
    begin
      if MsgBox('.NET Framework 4.8 이 필요합니다. 계속 진행할까요?' + #13#10 +
                '(Windows 10 1903 이상에는 기본 포함돼 있습니다)',
                mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end;
end;

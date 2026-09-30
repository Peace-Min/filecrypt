<#
    목업 근태관리 사이트를 띄우고, 그 사이트에 붙는 FileCrypt 를 연다 - 화면에서 직접 눌러 보기용.

      powershell -ExecutionPolicy Bypass -File .\tools\netcus-mock\start-mock.ps1

    - 실제 사이트에 닿지 않는다. www.netcus.com 은 127.0.0.1 의 목업으로 간다(FILECRYPT_NETCUS_MOCK).
    - 설정·계정·기록은 %TEMP%\fc_mockui\appdata 에 따로 둔다(실제 %LOCALAPPDATA%\FileCrypt 를 건드리지 않음).
    - 목업 계정: 아이디 mock / 비밀번호 mock1234 (앱의 [계정 정보 관리] 에서 저장 -> [로그인 확인])
    - 목업 브라우저 창: https://www.netcus.com 대신 아래에 찍히는 주소를 쓰면 저장된 내용을 볼 수 있다
      (브라우저는 인증서 경고를 띄운다 - 자체 서명).
    - 이 창을 닫으면(또는 Enter) 목업이 멈춘다.
#>
param(
    [int]$Port = 0,
    [string]$Id = 'mock',
    [string]$Password = 'mock1234',
    # 장애 주입: 저장할 때 이 글자수로 자름(0 = 안 함)
    [int]$Truncate = 0,
    # 장애 주입: 모든 응답 전에 쉬는 시간(ms)
    [int]$Delay = 0
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe  = Join-Path $root 'gui\bin\Release\net48\FileCrypt.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "빌드된 exe 가 없습니다: $exe" }

. (Join-Path $PSScriptRoot 'Import-NetcusMock.ps1')
$mock = New-Object FileCryptMock.NetcusMock
$mock.AddUser($Id, $Password)
$mock.TruncateContentAt = $Truncate
$mock.DelayMs = $Delay
$mock.Start($Port)

$data = Join-Path $env:TEMP 'fc_mockui\appdata'
New-Item -ItemType Directory -Force $data | Out-Null
$env:FILECRYPT_NETCUS_MOCK = [string]$mock.Port
$env:FILECRYPT_DATA_DIR = $data

Write-Host ''
Write-Host ('  목업 근태관리  https://127.0.0.1:{0}/pjm/login.htm' -f $mock.Port) -ForegroundColor Cyan
Write-Host ('  계정           {0} / {1}' -f $Id, $Password)
Write-Host ('  앱 데이터      {0}' -f $data) -ForegroundColor DarkGray
Write-Host ''
$app = Start-Process -FilePath $exe -PassThru
Write-Host '  FileCrypt 를 열었습니다. 창 제목과 근태관리 창에 [목업] 표시를 확인하세요.' -ForegroundColor Green
Write-Host '  Enter 를 누르면 목업을 멈춥니다.' -ForegroundColor DarkGray
[void](Read-Host)
Write-Host ('  요청 {0}건 · 로그인 {1}회 · 기록 {2}건' -f $mock.Requests, $mock.LoginPosts, $mock.WriteLog.Count)
$mock.Dispose()

<#
    FileCrypt 설치 스크립트

    관리자 권한이 필요 없습니다. 현재 사용자 계정에만 설치합니다.
      프로그램      %LOCALAPPDATA%\Programs\FileCrypt
      시작 메뉴     사용자 시작 메뉴
      제거          설정 > 앱 목록에 표시 (또는 제거.cmd)

    외부 도구를 쓰지 않으므로 오프라인 PC 에서도 그대로 동작합니다.
#>
[CmdletBinding()]
param(
    [switch]$NoDesktopShortcut,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$AppName    = 'FileCrypt'
$AppVersion = '2.0.0'
$Publisher  = 'FileCrypt'
$RegKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FileCrypt'

$Root    = Split-Path $PSScriptRoot -Parent
$SrcExe  = Join-Path $Root 'gui\bin\Release\net48\FileCrypt.exe'
$SrcIco  = Join-Path $Root 'gui\FileCrypt.ico'
$Target  = Join-Path $env:LOCALAPPDATA 'Programs\FileCrypt'

function Say([string]$t, [string]$c = 'Gray') { if (-not $Quiet) { Write-Host $t -ForegroundColor $c } }

# 덮어쓸 대상, 즉 "설치 폴더에서 실행 중인" 프로세스만 막는다.
# 프로젝트 폴더에서 돌리는 Debug 빌드까지 막으면 개발 중에 설치가 안 된다.
function Get-RunningInTarget([string]$dir) {
    return @(Get-Process -Name 'FileCrypt' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
}


Say ''
Say '============================================================' DarkCyan
Say '  FileCrypt 설치' Cyan
Say '============================================================' DarkCyan
Say ''

# ------------------------------------------------- setup.exe 로 이미 설치돼 있으면 막는다
# 두 경로가 같은 폴더에 설치하면서 제거 등록만 따로 남으면 정리가 꼬인다.
$uninstRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
$innoEntry = Get-ChildItem $uninstRoot -ErrorAction SilentlyContinue | Where-Object {
    $d = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    $d.DisplayName -like 'FileCrypt*' -and $d.UninstallString -like '*unins*'
} | Select-Object -First 1

if ($innoEntry) {
    Say '  이미 setup.exe 로 설치돼 있습니다.' Yellow
    Say ''
    Say '  설정 > 앱 에서 FileCrypt 를 먼저 제거한 뒤 다시 실행하거나,' DarkGray
    Say '  installer\Output\FileCrypt-Setup-*.exe 를 다시 실행해 덮어쓰세요 (권장).' DarkGray
    Say ''
    return 3
}

# ---------------------------------------------------------------- 빌드 확인
if (-not (Test-Path -LiteralPath $SrcExe)) {
    Say '  FileCrypt.exe 가 없습니다. 빌드를 시도합니다...' Yellow
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Say ''
        Say '  [실패] dotnet SDK 가 없어 빌드할 수 없습니다.' Red
        Say '         Visual Studio 로 gui\FileCrypt.csproj 를 Release 빌드한 뒤 다시 실행하세요.' DarkGray
        return 1
    }
    Push-Location (Join-Path $Root 'gui')
    try { & dotnet build -c Release --nologo -v q | Out-Null } finally { Pop-Location }
    if (-not (Test-Path -LiteralPath $SrcExe)) {
        Say '  [실패] 빌드에 실패했습니다.' Red
        return 1
    }
    Say '  빌드 완료.' Green
}

# ---------------------------------------------------------------- 실행 중이면 중지 요청
$running = Get-RunningInTarget $Target
if ($running.Count -gt 0) {
    Say '  설치된 FileCrypt 가 실행 중입니다. 창을 닫고 다시 실행하세요.' Red
    return 2
}

# ---------------------------------------------------------------- 복사
if (-not (Test-Path -LiteralPath $Target)) { New-Item -ItemType Directory -Force -Path $Target | Out-Null }

Copy-Item -LiteralPath $SrcExe -Destination (Join-Path $Target 'FileCrypt.exe') -Force
if (Test-Path -LiteralPath $SrcIco) { Copy-Item -LiteralPath $SrcIco -Destination (Join-Path $Target 'FileCrypt.ico') -Force }
$cfg = $SrcExe + '.config'
if (Test-Path -LiteralPath $cfg) { Copy-Item -LiteralPath $cfg -Destination (Join-Path $Target 'FileCrypt.exe.config') -Force }

# 제거 스크립트를 설치 폴더에 같이 둔다 (원본 폴더가 사라져도 제거 가능하도록)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination (Join-Path $Target 'uninstall.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '제거.cmd')       -Destination (Join-Path $Target '제거.cmd')       -Force

$ExePath = Join-Path $Target 'FileCrypt.exe'
Say ('  프로그램   {0}' -f $Target)

# ---------------------------------------------------------------- 바로가기
$shell = New-Object -ComObject WScript.Shell

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$lnkStart  = Join-Path $startMenu 'FileCrypt.lnk'
$sc = $shell.CreateShortcut($lnkStart)
$sc.TargetPath       = $ExePath
$sc.WorkingDirectory = $Target
$sc.IconLocation     = $ExePath + ',0'
$sc.Description      = '파일·폴더를 텍스트로 바꿔 복사·붙여넣기로 옮기는 도구'
$sc.Save()
Say ('  시작 메뉴  {0}' -f $lnkStart)

if (-not $NoDesktopShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnkDesk = Join-Path $desktop 'FileCrypt.lnk'
    $sd = $shell.CreateShortcut($lnkDesk)
    $sd.TargetPath       = $ExePath
    $sd.WorkingDirectory = $Target
    $sd.IconLocation     = $ExePath + ',0'
    $sd.Description      = '파일·폴더를 텍스트로 바꿔 복사·붙여넣기로 옮기는 도구'
    $sd.Save()
    Say ('  바탕화면   {0}' -f $lnkDesk)
}

[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null

# ---------------------------------------------------------------- 앱 목록 등록 (현재 사용자만)
$sizeKb = [int]((Get-ChildItem -LiteralPath $Target -File -Recurse | Measure-Object Length -Sum).Sum / 1KB)
if (-not (Test-Path $RegKey)) { New-Item -Path $RegKey -Force | Out-Null }
New-ItemProperty -Path $RegKey -Name 'DisplayName'     -Value $AppName    -PropertyType String -Force | Out-Null
New-ItemProperty -Path $RegKey -Name 'DisplayVersion'  -Value $AppVersion -PropertyType String -Force | Out-Null
New-ItemProperty -Path $RegKey -Name 'Publisher'       -Value $Publisher  -PropertyType String -Force | Out-Null
New-ItemProperty -Path $RegKey -Name 'DisplayIcon'     -Value $ExePath    -PropertyType String -Force | Out-Null
New-ItemProperty -Path $RegKey -Name 'InstallLocation' -Value $Target     -PropertyType String -Force | Out-Null
New-ItemProperty -Path $RegKey -Name 'UninstallString' `
    -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}\uninstall.ps1"' -f $Target) -PropertyType String -Force | Out-Null
New-ItemProperty -Path $RegKey -Name 'EstimatedSize'   -Value $sizeKb -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $RegKey -Name 'NoModify'        -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $RegKey -Name 'NoRepair'        -Value 1 -PropertyType DWord -Force | Out-Null
Say '  앱 목록    설정 > 앱 에서 제거 가능'

Say ''
Say '  설치 완료' Green
Say ''
Say '  시작 메뉴에서 "FileCrypt" 를 검색하거나 바탕화면 아이콘으로 실행하세요.' DarkGray
Say ''
return 0

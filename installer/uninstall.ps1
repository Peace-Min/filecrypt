<#
    FileCrypt 제거 스크립트

    설치 때 만든 것만 지웁니다.
      %LOCALAPPDATA%\Programs\FileCrypt
      시작 메뉴 / 바탕화면 바로가기
      HKCU 앱 목록 등록

    사용자가 만든 문서나 복원한 파일은 건드리지 않습니다.
#>
[CmdletBinding()]
param([switch]$Quiet)

$ErrorActionPreference = 'Continue'

$RegKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FileCrypt'
$Target = Join-Path $env:LOCALAPPDATA 'Programs\FileCrypt'

function Say([string]$t, [string]$c = 'Gray') { if (-not $Quiet) { Write-Host $t -ForegroundColor $c } }

Say ''
Say '============================================================' DarkCyan
Say '  FileCrypt 제거' Cyan
Say '============================================================' DarkCyan
Say ''

$running = @(Get-Process -Name 'FileCrypt' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($Target, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
})
if ($running.Count -gt 0) {
    Say '  설치된 FileCrypt 가 실행 중입니다. 창을 닫고 다시 실행하세요.' Red
    return 2
}

# ---- 바로가기
foreach ($lnk in @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\FileCrypt.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'FileCrypt.lnk')
)) {
    if (Test-Path -LiteralPath $lnk) {
        Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue
        Say ('  바로가기 삭제  {0}' -f $lnk)
    }
}

# ---- 앱 목록
if (Test-Path $RegKey) {
    Remove-Item -Path $RegKey -Recurse -Force -ErrorAction SilentlyContinue
    Say '  앱 목록 등록 해제'
}

# ---- 프로그램 폴더
# 제거 스크립트 자신이 이 폴더 안에 있을 수 있으므로, 임시 위치에서 지우도록 예약한다.
if (Test-Path -LiteralPath $Target) {
    $self = $MyInvocation.MyCommand.Path
    $inside = $self -and $self.StartsWith($Target, [StringComparison]::OrdinalIgnoreCase)

    if ($inside) {
        $bat = Join-Path $env:TEMP ('fc_rm_{0}.cmd' -f ([guid]::NewGuid().ToString('N')))
        @(
            '@echo off',
            'ping 127.0.0.1 -n 2 >nul',
            ('rmdir /s /q "{0}"' -f $Target),
            ('del "%~f0" >nul 2>&1')
        ) | Set-Content -LiteralPath $bat -Encoding OEM
        Start-Process -FilePath $bat -WindowStyle Hidden
        Say ('  프로그램 폴더 삭제 예약  {0}' -f $Target)
    } else {
        Remove-Item -LiteralPath $Target -Recurse -Force -ErrorAction SilentlyContinue
        Say ('  프로그램 폴더 삭제  {0}' -f $Target)
    }
}

Say ''
Say '  제거 완료' Green
Say ''
return 0

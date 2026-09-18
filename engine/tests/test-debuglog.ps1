$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

# 디버깅 기록(DebugLog). 근태관리 연동은 사이트가 있어야 돌아가서 개발 중 재현이 안 되므로,
# 무엇이 어느 단계에서 어그러졌는지는 이 기록이 유일한 단서다.
# 가장 중요한 건 '비밀번호가 절대 기록에 남지 않는 것'.
# 주의: 실제 %LOCALAPPDATA%\FileCrypt 를 건드리므로 원본을 백업했다 되돌린다.

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$EXE     = Join-Path $ROOTDIR 'gui\bin\Release\net48\FileCrypt.exe'
$WORK    = Join-Path $env:TEMP ('fc_log_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $WORK | Out-Null

$n = 0; $fail = 0
function Ok([string]$name, [bool]$cond, [string]$extra) {
    $script:n++
    if ($cond) { Write-Host ('  [PASS] {0}  {1}' -f $name.PadRight(50), $extra) -ForegroundColor Green }
    else       { Write-Host ('  [FAIL] {0}  {1}' -f $name.PadRight(50), $extra) -ForegroundColor Red; $script:fail++ }
}

Write-Host ''
Write-Host '########## 디버깅 기록 (DebugLog) ##########' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $EXE)) { Write-Host '  [FAIL] gui 빌드 없음' -ForegroundColor Red; exit 1 }
$exeCopy = Join-Path $WORK 'FileCrypt.exe'
Copy-Item -LiteralPath $EXE -Destination $exeCopy -Force
[void][Reflection.Assembly]::LoadFrom($exeCopy)
$LOG = [FileCrypt.DebugLog]
$CFG = [FileCrypt.AppConfig]

# ---- 사용자 실제 설정 백업 ------------------------------------------------
$cfgFile = $CFG::File_
$backup = $null
if (Test-Path -LiteralPath $cfgFile) {
    $backup = Join-Path $WORK 'config.ini.orig'
    Copy-Item -LiteralPath $cfgFile -Destination $backup -Force
}
try {
    if (Test-Path -LiteralPath $cfgFile) { Remove-Item -LiteralPath $cfgFile -Force }
    $CFG::Reload()

    $f = $LOG::TodayFile
    $before = if (Test-Path -LiteralPath $f) { (Get-Item -LiteralPath $f).Length } else { 0 }

    # ================================================================ 1) 기록된다
    $mark = 'TESTMARK-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $LOG::Write('테스트', $mark)
    Ok '기록 파일이 만들어진다' (Test-Path -LiteralPath $f) $f
    $text = [System.IO.File]::ReadAllText($f)
    Ok '  남긴 내용이 들어 있다' ($text.Contains($mark)) ''
    Ok '  분류가 함께 남는다' ($text -match ('\[테스트\].*' + $mark)) ''
    Ok '  시각이 앞에 붙는다' ($text -match '\d{2}:\d{2}:\d{2}\.\d{3}') ''

    # ================================================================ 2) 구간 구분
    $LOG::Section('구간표시테스트')
    $text = [System.IO.File]::ReadAllText($f)
    Ok '구간 표시가 남는다' ($text.Contains('구간표시테스트')) ''

    # ================================================================ 3) 비밀번호는 절대 남지 않는다
    $secret = 'Sup3rSecret!pw-' + [Guid]::NewGuid().ToString('N').Substring(0,6)
    $CFG::NetcusId = 'tester'
    $CFG::NetcusPassword = $secret
    $LOG::Write('테스트', '로그인 시도 pw=' + $secret + ' 끝')
    $text = [System.IO.File]::ReadAllText($f)
    Ok '저장된 비밀번호가 기록에 남지 않는다' (-not $text.Contains($secret)) ''
    Ok '  가려진 표시로 대체된다' ($text.Contains('***')) ''

    # ================================================================ 4) 앱을 막지 않는다
    $err = $null
    try { $LOG::Write($null, $null) } catch { $err = $_ }
    Ok 'null 을 줘도 예외를 내지 않는다' ($null -eq $err) ''

    # ================================================================ 5) 폴더 경로
    Ok '기록 폴더가 설정 폴더 아래에 있다' ($LOG::Dir.StartsWith($CFG::Dir)) $LOG::Dir
    Ok '  파일명이 날짜별이다' ((Split-Path $f -Leaf) -match '^filecrypt-\d{8}\.log$') (Split-Path $f -Leaf)
}
finally {
    try {
        if ($backup) { Copy-Item -LiteralPath $backup -Destination $cfgFile -Force }
        elseif (Test-Path -LiteralPath $cfgFile) { Remove-Item -LiteralPath $cfgFile -Force }
        $CFG::Reload()
        Write-Host ''
        Write-Host '  (원래 설정 복구 완료)' -ForegroundColor DarkGray
    } catch { Write-Host ('  [경고] 설정 복구 실패: ' + $_.Exception.Message) -ForegroundColor Yellow }
}

Write-Host ''
Write-Host ('########## 디버깅 기록: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor Cyan

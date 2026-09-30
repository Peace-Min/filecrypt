. (Join-Path $PSScriptRoot '_common.ps1')

# 디버깅 기록(DebugLog). 근태관리 연동은 사이트가 있어야 돌아가서 개발 중 재현이 안 되므로,
# 무엇이 어느 단계에서 어그러졌는지는 이 기록이 유일한 단서다.
# 가장 중요한 건 '비밀번호가 절대 기록에 남지 않는 것'.
# FILECRYPT_DATA_DIR 로 작업 폴더를 쓰므로 실제 %LOCALAPPDATA%\FileCrypt 는 건드리지 않는다.

Start-Test -Tag log -Title '디버깅 기록 (DebugLog)' -Pad 50
Import-FileCrypt
$LOG = [FileCrypt.DebugLog]
$CFG = [FileCrypt.AppConfig]

# ================================================================ 0) 격리
Ok '설정 폴더가 작업 폴더 아래 (실제 설정 보호)' `
   ($CFG::Dir.StartsWith($WORK, [StringComparison]::OrdinalIgnoreCase)) $CFG::Dir
Ok '  기록 폴더도 작업 폴더 아래' ($LOG::Dir.StartsWith($WORK, [StringComparison]::OrdinalIgnoreCase)) $LOG::Dir

$cfgFile = $CFG::File_
if (Test-Path -LiteralPath $cfgFile) { Remove-Item -LiteralPath $cfgFile -Force }
$CFG::Reload()

$f = $LOG::TodayFile

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

Complete-Test

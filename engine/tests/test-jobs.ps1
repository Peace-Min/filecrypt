$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

# GUI 창이 실행하는 처리 절차(FileCryptJobs)를 직접 돌린다.
# 예전에는 이 로직이 MainWindow 안에 있어 자동 테스트가 닿지 않았다.

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$EXE     = Join-Path $ROOTDIR 'gui\bin\Release\net48\FileCrypt.exe'
$WORK    = Join-Path $env:TEMP ('fc_jobs_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $WORK | Out-Null
$u8n = New-Object System.Text.UTF8Encoding($false)

$n = 0; $fail = 0
function Ok([string]$name, [bool]$cond, [string]$extra) {
    $script:n++
    if ($cond) { Write-Host ('  [PASS] {0}  {1}' -f $name.PadRight(48), $extra) -ForegroundColor Green }
    else       { Write-Host ('  [FAIL] {0}  {1}' -f $name.PadRight(48), $extra) -ForegroundColor Red; $script:fail++ }
}
function Sha([byte[]]$b) {
    $s = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($s.ComputeHash($b))).Replace('-','') } finally { $s.Dispose() }
}
function ShaFile([string]$p) { return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }

Write-Host ''
Write-Host '########## GUI 처리 절차 (FileCryptJobs) ##########' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $EXE)) { Write-Host '  [FAIL] gui 빌드 없음' -ForegroundColor Red; exit 1 }
$exeCopy = Join-Path $WORK 'FileCrypt.exe'
Copy-Item -LiteralPath $EXE -Destination $exeCopy -Force
[void][Reflection.Assembly]::LoadFrom($exeCopy)
$JOBS = [FileCrypt.FileCryptJobs]
$CORE = [FileCrypt.FileCryptCore]

function NewInput([string]$full, [string]$rel) {
    $i = New-Object FileCrypt.FileCryptJobs+PackInput
    $i.FullPath = $full
    if ($rel) { $i.RelPath = $rel }
    return $i
}
function NewOpt([bool]$archive, [int]$split, [int]$autoOver = 0) {
    $o = New-Object FileCrypt.FileCryptJobs+PackOptions
    $o.Archive = $archive
    $o.SplitChars = $split
    $o.AutoSplitOver = $autoOver
    return $o
}
function TextList($arr) {
    # PowerShell 의 object[] 는 IEnumerable<string> 으로 변환되지 않는다. 명시적으로 담아 준다.
    $l = New-Object 'System.Collections.Generic.List[string]'
    foreach ($a in $arr) { $l.Add([string]$a) }
    return ,$l
}
function InputList($arr) {
    $l = New-Object 'System.Collections.Generic.List[FileCrypt.FileCryptJobs+PackInput]'
    foreach ($a in $arr) { $l.Add($a) }
    return ,$l
}

# ---------------------------------------------------------------- 표본
$srcDir = Join-Path $WORK 'src'
New-Item -ItemType Directory -Force (Join-Path $srcDir 'sub') | Out-Null
$expect = @{}
foreach ($nm in @('a.cs','b.xaml','한글 [대괄호].txt')) {
    $p = Join-Path $srcDir $nm
    [System.IO.File]::WriteAllText($p, ("내용 $nm`r`n" * 60), $u8n)
    $expect[$nm] = ShaFile $p
}
$binPath = Join-Path $srcDir 'sub\데이터.bin'
[System.IO.File]::WriteAllBytes($binPath, ([byte[]](0..255)))
$expect['sub\데이터.bin'] = ShaFile $binPath
$emptyPath = Join-Path $srcDir 'sub\빈파일.dat'
[System.IO.File]::WriteAllBytes($emptyPath, (New-Object byte[] 0))
$expect['sub\빈파일.dat'] = ShaFile $emptyPath

$all = Get-ChildItem -LiteralPath $srcDir -File -Recurse
Ok '표본 생성' ($all.Count -eq 5) ('{0}개' -f $all.Count)

# ================================================================ 1) 파일 1개 -> 원래 이름 물려받기
$one = Join-Path $WORK 'out1'
$r = $JOBS::Pack((InputList @((NewInput (Join-Path $srcDir 'a.cs') $null))), $one, (NewOpt $false 0))
$made = [System.IO.Path]::GetFileName($r.WrittenFiles[0])
Ok '파일 1개 -> "이름.enc.txt"' ($made -eq 'a.cs.enc.txt') $made
Ok '  클립보드 내용 = 전체 텍스트' ($r.ClipboardText -eq $r.FullText) ''

# ================================================================ 2) 여러 파일 -> 블록 방식
$two = Join-Path $WORK 'out2'
$inputs = InputList @(
    (NewInput (Join-Path $srcDir 'a.cs') $null),
    (NewInput (Join-Path $srcDir 'b.xaml') $null),
    (NewInput (Join-Path $srcDir '한글 [대괄호].txt') $null)
)
$r2 = $JOBS::Pack($inputs, $two, (NewOpt $false 0))
Ok '여러 파일 -> 묶음 파일 1개' ($r2.WrittenFiles.Count -eq 1) ([System.IO.Path]::GetFileName($r2.WrittenFiles[0]))
Ok '  이름이 "FCRYPT 묶음 3개 ..." 형식' ([System.IO.Path]::GetFileName($r2.WrittenFiles[0]) -like 'FCRYPT 묶음 3개 *.txt') ''
$blocks = $CORE::ExtractBlocks([System.IO.File]::ReadAllText($r2.WrittenFiles[0]))
Ok '  블록이 파일 수만큼 (독립 블록)' ($blocks.Count -eq 3) ('{0}개' -f $blocks.Count)

# ================================================================ 3) 아카이브 모드
$three = Join-Path $WORK 'out3'
$r3 = $JOBS::Pack($inputs, $three, (NewOpt $true 0))
$b3 = $CORE::ExtractBlocks([System.IO.File]::ReadAllText($r3.WrittenFiles[0]))
Ok '아카이브 -> 블록 1개' ($b3.Count -eq 1) ('{0}개' -f $b3.Count)
Ok '  아카이브가 블록 방식보다 작음' ($r3.FullText.Length -lt $r2.FullText.Length) `
   ('{0:N0} < {1:N0} 자' -f $r3.FullText.Length, $r2.FullText.Length)

# ================================================================ 4) 폴더 순회 -> 상대 경로 보존 왕복
$four = Join-Path $WORK 'out4'
$entries = $CORE::EnumerateFolder($srcDir)
$fi = InputList (@($entries | ForEach-Object { NewInput $_.FullPath $_.RelativePath }))
$r4 = $JOBS::Pack($fi, $four, (NewOpt $true 0))
$back4 = Join-Path $WORK 'back4'
$u4 = $JOBS::Unpack((TextList @([System.IO.File]::ReadAllText($r4.WrittenFiles[0]))), $back4)
Ok '폴더 -> 아카이브 -> 되돌리기' ($u4.OkCount -eq 5) ('{0}개 복원, 실패 {1}' -f $u4.OkCount, $u4.FailedCount)

$rootName = [System.IO.Path]::GetFileName($srcDir)
$bad = 0
foreach ($k in $expect.Keys) {
    $p = Join-Path $u4.TargetDir (Join-Path $rootName $k)
    if (-not (Test-Path -LiteralPath $p)) { $bad++; continue }
    if ((ShaFile $p) -ne $expect[$k]) { $bad++ }
}
Ok '  폴더 구조 + 전 파일 해시 일치' ($bad -eq 0) ('불일치 {0}개' -f $bad)
Ok '  파일이 여러 개면 하위 폴더를 만든다' ($u4.TargetDir -ne $back4) ([System.IO.Path]::GetFileName($u4.TargetDir))

# ================================================================ 5) 파일 1개 복원은 폴더를 안 만든다
$back5 = Join-Path $WORK 'back5'
$u5 = $JOBS::Unpack((TextList @($r.FullText)), $back5)
Ok '파일 1개 복원 -> 하위 폴더 없음' ($u5.TargetDir -eq $back5) ''
Ok '  원본과 일치' ((ShaFile $u5.WrittenFiles[0]) -eq $expect['a.cs']) ''

# ================================================================ 6) 조각내기
$six = Join-Path $WORK 'out6'
$big = Join-Path $srcDir 'big.bin'
$rand = New-Object System.Random 42
$bb = New-Object byte[] (300KB); $rand.NextBytes($bb)
[System.IO.File]::WriteAllBytes($big, $bb)
$r6 = $JOBS::Pack((InputList @((NewInput $big $null))), $six, (NewOpt $false 100000))
Ok '조각내기 -> 파일 여러 개' ($r6.PartCount -ge 4) ('{0}조각' -f $r6.PartCount)
Ok '  각 조각이 한도 이하' ($r6.LongestPartChars -le 100000) ('최대 {0:N0}자' -f $r6.LongestPartChars)
Ok '  파일명이 "[1of N]" 형식' ([System.IO.Path]::GetFileName($r6.WrittenFiles[0]) -like '*`[1of*`].txt') `
   ([System.IO.Path]::GetFileName($r6.WrittenFiles[0]))
Ok '  클립보드에는 1번 조각' ($r6.ClipboardText -eq ([System.IO.File]::ReadAllText($r6.WrittenFiles[0])).TrimEnd()) ''

# 조각을 뒤섞어 되돌리기
$texts = @($r6.WrittenFiles | Sort-Object { Get-Random } | ForEach-Object { [System.IO.File]::ReadAllText($_) })
$back6 = Join-Path $WORK 'back6'
$u6 = $JOBS::Unpack((TextList $texts), $back6)
Ok '  조각을 뒤섞어 넣어도 복원' ($u6.OkCount -eq 1) ('{0}개' -f $u6.OkCount)
Ok '  원본과 해시 일치' ((ShaFile $u6.WrittenFiles[0]) -eq (Sha $bb)) ''

# 조각이 모자라면
$lack = @($texts | Select-Object -Skip 1)
$back7 = Join-Path $WORK 'back7'
$u7 = $JOBS::Unpack((TextList $lack), $back7)
Ok '  조각 부족 -> 복원 안 함' ($u7.BlockCount -eq 0) ('블록 {0}개' -f $u7.BlockCount)
$desc = $JOBS::DescribePending($u7.PendingParts)
Ok '  없는 조각을 문장으로 알려줌' (($u7.PendingParts.Count -eq 1) -and ($desc -match '조각이 모자랍니다') -and ($desc -match '없는 것')) $desc

# ================================================================ 7) 오류 격리
$eight = Join-Path $WORK 'out8'
$missingPath = Join-Path $srcDir '없는파일.txt'
$mix = InputList @(
    (NewInput (Join-Path $srcDir 'a.cs') $null),
    (NewInput $missingPath $null),
    (NewInput (Join-Path $srcDir 'b.xaml') $null)
)
$r8 = $JOBS::Pack($mix, $eight, (NewOpt $false 0))
Ok '없는 파일 1개 섞임 -> 나머지는 처리' (($r8.FileCount -eq 2) -and ($r8.FailedCount -eq 1)) `
   ('성공 {0} / 실패 {1}' -f $r8.FileCount, $r8.FailedCount)
Ok '  실패 사유를 남긴다' ($r8.Errors.Count -eq 1) ($r8.Errors[0].Substring(0, [Math]::Min(40, $r8.Errors[0].Length)))

# 잠긴 파일 (다른 프로그램이 잡고 있는 경우)
$lockPath = Join-Path $srcDir 'locked.bin'
[System.IO.File]::WriteAllText($lockPath, 'locked', $u8n)
$fs = [System.IO.File]::Open($lockPath, 'Open', 'Read', 'None')
try {
    $nine = Join-Path $WORK 'out9'
    $lk = InputList @((NewInput (Join-Path $srcDir 'a.cs') $null), (NewInput $lockPath $null))
    $r9 = $JOBS::Pack($lk, $nine, (NewOpt $false 0))
    Ok '잠긴 파일 -> 건너뛰고 나머지 처리' (($r9.FileCount -eq 1) -and ($r9.FailedCount -eq 1)) `
       ('성공 {0} / 실패 {1}' -f $r9.FileCount, $r9.FailedCount)
} finally { $fs.Dispose() }

# ================================================================ 8) 덮어쓰기 회피
$ten = Join-Path $WORK 'out10'
$a1 = $JOBS::Pack((InputList @((NewInput (Join-Path $srcDir 'a.cs') $null))), $ten, (NewOpt $false 0))
$a2 = $JOBS::Pack((InputList @((NewInput (Join-Path $srcDir 'a.cs') $null))), $ten, (NewOpt $false 0))
Ok '같은 이름 두 번 -> 덮어쓰지 않음' ($a1.WrittenFiles[0] -ne $a2.WrittenFiles[0]) `
   ([System.IO.Path]::GetFileName($a2.WrittenFiles[0]))

# ================================================================ 9) 손상된 텍스트
$back11 = Join-Path $WORK 'back11'
$u11 = $JOBS::Unpack((TextList @("그냥 평범한 텍스트입니다")), $back11)
Ok '블록 없는 텍스트 -> 조용히 0건' (($u11.BlockCount -eq 0) -and ($u11.PendingParts.Count -eq 0)) `
   ($JOBS::DescribePending($u11.PendingParts))

$good = $r.FullText
$brokenLines = $good -split "`r?`n"
for ($i = 0; $i -lt $brokenLines.Count; $i++) {
    if ($brokenLines[$i] -notlike '-----*' -and $brokenLines[$i].Length -gt 10) {
        $c = $brokenLines[$i][5]
        $brokenLines[$i] = $brokenLines[$i].Remove(5,1).Insert(5, $(if ($c -eq 'A') { 'B' } else { 'A' }))
        break
    }
}
$back12 = Join-Path $WORK 'back12'
$u12 = $JOBS::Unpack((TextList @(($brokenLines -join "`r`n"))), $back12)
Ok '1글자 변조 -> 거부하고 사유 남김' (($u12.OkCount -eq 0) -and ($u12.FailedCount -eq 1)) `
   ($(if ($u12.Errors.Count -gt 0) { $u12.Errors[0].Substring(0, [Math]::Min(40, $u12.Errors[0].Length)) } else { '' }))

# ================================================================ 10) 여러 입력을 합쳐서 해석
$back13 = Join-Path $WORK 'back13'
$u13 = $JOBS::Unpack((TextList @($r.FullText, $r3.FullText)), $back13)
Ok '서로 다른 묶음 2개를 한 번에' ($u13.OkCount -eq 4) ('{0}개 복원 (1 + 3)' -f $u13.OkCount)

# ================================================================ 11) "필요할 때만" 나누기
Write-Host ''
Write-Host '  -- 한도를 넘을 때만 나누기 --' -ForegroundColor DarkGray

# 작은 파일: 한도를 안 넘으므로 통짜 하나
$autoS = Join-Path $WORK 'auto_small'
$rs = $JOBS::Pack((InputList @((NewInput (Join-Path $srcDir 'a.cs') $null))), $autoS, (NewOpt $false 0 100000))
Ok '한도 안 -> 나누지 않음' (($rs.PartCount -eq 0) -and ($rs.WrittenFiles.Count -eq 1)) `
   ('{0:N0}자 / 파일 {1}개' -f $rs.FullText.Length, $rs.WrittenFiles.Count)
Ok '  자동 분할 표시 꺼짐' (-not $rs.SplitWasAutomatic) ''

# 큰 파일: 한도를 넘으므로 자동으로 나뉜다
$autoB = Join-Path $WORK 'auto_big'
$rb = $JOBS::Pack((InputList @((NewInput $big $null))), $autoB, (NewOpt $false 0 100000))
Ok '한도 초과 -> 자동으로 나눔' ($rb.PartCount -ge 4) ('{0:N0}자 -> {1}조각' -f $rb.FullText.Length, $rb.PartCount)
Ok '  자동 분할 표시 켜짐' ($rb.SplitWasAutomatic) ''
Ok '  각 조각이 한도 이하' ($rb.LongestPartChars -le 100000) ('최대 {0:N0}자' -f $rb.LongestPartChars)

# 자동으로 나뉜 것도 되돌아가야 한다
$backAuto = Join-Path $WORK 'back_auto'
$ta = TextList (@($rb.WrittenFiles | Sort-Object { Get-Random } | ForEach-Object { [System.IO.File]::ReadAllText($_) }))
$ua = $JOBS::Unpack($ta, $backAuto)
Ok '  자동 분할본 뒤섞어 복원' (($ua.OkCount -eq 1) -and ((ShaFile $ua.WrittenFiles[0]) -eq (Sha $bb))) ''

# 직접 지정이 "필요할 때만" 보다 우선
$both = Join-Path $WORK 'auto_both'
$rboth = $JOBS::Pack((InputList @((NewInput (Join-Path $srcDir 'a.cs') $null))), $both, (NewOpt $false 1000 100000))
Ok '항상 나누기가 한도 규칙보다 우선' (($rboth.PartCount -ge 1) -and (-not $rboth.SplitWasAutomatic)) `
   ('{0}조각' -f $rboth.PartCount)

# 규칙 자체를 끈 경우
$none = Join-Path $WORK 'auto_none'
$rnone = $JOBS::Pack((InputList @((NewInput $big $null))), $none, (NewOpt $false 0 0))
Ok '규칙 끔 -> 아무리 커도 통짜' (($rnone.PartCount -eq 0) -and ($rnone.WrittenFiles.Count -eq 1)) `
   ('{0:N0}자 / 파일 1개' -f $rnone.FullText.Length)

Write-Host ''
Write-Host ('########## GUI 처리 절차: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })

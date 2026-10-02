. (Join-Path $PSScriptRoot '_common.ps1')

# GUI 창이 실행하는 처리 절차(FileCryptJobs)를 직접 돌린다.
# 예전에는 이 로직이 MainWindow 안에 있어 자동 테스트가 닿지 않았다.

Start-Test -Tag jobs -Title 'GUI 처리 절차 (FileCryptJobs)' -Pad 48
Import-FileCrypt
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
   ('{0:N0}자 / 파일 {1}개' -f $rs.TotalChars, $rs.WrittenFiles.Count)
Ok '  자동 분할 표시 꺼짐' (-not $rs.SplitWasAutomatic) ''

# 큰 파일: 한도를 넘으므로 자동으로 나뉜다
$autoB = Join-Path $WORK 'auto_big'
$rb = $JOBS::Pack((InputList @((NewInput $big $null))), $autoB, (NewOpt $false 0 100000))
Ok '한도 초과 -> 자동으로 나눔' ($rb.PartCount -ge 4) ('{0:N0}자 -> {1}조각' -f $rb.TotalChars, $rb.PartCount)
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
   ('{0:N0}자 / 파일 1개' -f $rnone.TotalChars)

# ================================================================ 12) 전체 글자수(TotalChars)
# 창은 통짜 텍스트를 만들지 않고 이 값으로 '한도를 넘는지' 를 판단한다. 계산이 실제와 어긋나면
# 나눠야 할 것을 안 나누거나 그 반대가 된다.
Write-Host ''
Write-Host '  -- 전체 글자수 계산 --' -ForegroundColor DarkGray
$whole = @(@('파일 1개', $r), @('여러 파일', $r2), @('아카이브', $r3), @('규칙 끔 큰 파일', $rnone), @('한도 안 작은 파일', $rs))
foreach ($w in $whole) {
    $pr = $w[1]
    Ok ('안 나눔: TotalChars = 실제 길이 (' + $w[0] + ')') `
       (($null -ne $pr.FullText) -and ($pr.TotalChars -eq $pr.FullText.Length)) `
       ('{0:N0} / {1:N0}' -f $pr.TotalChars, $(if ($pr.FullText) { $pr.FullText.Length } else { -1 }))
}
Ok '자동 분할: TotalChars 가 한도를 넘는다' ($rb.TotalChars -gt 100000) ('{0:N0} > 100,000' -f $rb.TotalChars)
Ok '  나눴으면 통짜 텍스트는 만들지 않는다' ($null -eq $rb.FullText) ''
$partSum = 0; foreach ($wf in $rb.WrittenFiles) { $partSum += ([System.IO.File]::ReadAllText($wf)).TrimEnd().Length }
Ok '  조각 합은 통짜 길이 이상 (머리말 때문에)' ($partSum -ge ($rb.TotalChars - 2)) ('조각 합 {0:N0}' -f $partSum)

# ================================================================ 13) 미리 세기(InspectInputs) = 실제 복원(Unpack)
# 창은 목록이 바뀔 때마다 InspectInputs 로 "블록 몇 개" 를 보여 준다. Unpack 과 어긋나면 안 된다.
Write-Host ''
Write-Host '  -- 미리 세기와 실제 복원이 같은가 --' -ForegroundColor DarkGray
$t2 = [System.IO.File]::ReadAllText($r2.WrittenFiles[0])
$s2 = $JOBS::InspectInputs((TextList @($t2)))
Ok '여러 파일 묶음: MESSAGE 3개, 조각 없음' (($s2.MessageBlocks -eq 3) -and (-not $s2.HasParts)) `
   ('MESSAGE {0} / 조각묶음 {1}' -f $s2.MessageBlocks, $s2.PartGroups.Count)
$us2 = $JOBS::Unpack((TextList @($t2)), (Join-Path $WORK 'scan2'))
Ok '  Unpack 의 블록 수와 같다' ($s2.BlockCount -eq $us2.BlockCount) ('{0} = {1}' -f $s2.BlockCount, $us2.BlockCount)

$s6 = $JOBS::InspectInputs((TextList $texts))
Ok '조각 전부: 묶음 1개, 다 모임' (($s6.MessageBlocks -eq 0) -and ($s6.PartGroups.Count -eq 1) -and ($s6.CompleteGroups -eq 1)) `
   ('조각묶음 {0} / 완성 {1}' -f $s6.PartGroups.Count, $s6.CompleteGroups)
Ok '  Unpack 의 블록 수와 같다' ($s6.BlockCount -eq $u6.BlockCount) ('{0} = {1}' -f $s6.BlockCount, $u6.BlockCount)

$s7 = $JOBS::InspectInputs((TextList $lack))
Ok '조각 부족: 묶음 1개, 미완성, 블록 0' `
   (($s7.PartGroups.Count -eq 1) -and ($s7.CompleteGroups -eq 0) -and ($s7.BlockCount -eq 0) -and ($s7.PartGroups[0].Missing.Count -eq 1)) `
   ('없는 것 {0}개' -f $s7.PartGroups[0].Missing.Count)
Ok '  Unpack 도 블록 0' ($s7.BlockCount -eq $u7.BlockCount) ('{0} = {1}' -f $s7.BlockCount, $u7.BlockCount)

# 섞인 입력: MESSAGE 3개 + 다 모인 조각 묶음 1개 + 하나 빠진 다른 조각 묶음 1개
$autoParts = @($rb.WrittenFiles | ForEach-Object { [System.IO.File]::ReadAllText($_) })
$mixIn = @($t2) + $texts + @($autoParts | Select-Object -Skip 1)
$sm = $JOBS::InspectInputs((TextList $mixIn))
Ok '섞인 입력: MESSAGE 3 + 조각묶음 2 (완성 1)' `
   (($sm.MessageBlocks -eq 3) -and ($sm.PartGroups.Count -eq 2) -and ($sm.CompleteGroups -eq 1) -and ($sm.BlockCount -eq 4)) `
   ('MESSAGE {0} / 묶음 {1} / 완성 {2} / 블록 {3}' -f $sm.MessageBlocks, $sm.PartGroups.Count, $sm.CompleteGroups, $sm.BlockCount)
$um = $JOBS::Unpack((TextList $mixIn), (Join-Path $WORK 'scan_mix'))
Ok '  Unpack: 블록 4개, 파일 4개 복원' (($um.BlockCount -eq $sm.BlockCount) -and ($um.OkCount -eq 4) -and ($um.FailedCount -eq 0)) `
   ('블록 {0} / 복원 {1} / 실패 {2}' -f $um.BlockCount, $um.OkCount, $um.FailedCount)

$sc = $CORE::Scan($r.FullText)
Ok 'Scan(통짜 1개) = MESSAGE 1, 블록 1' (($sc.MessageBlocks -eq 1) -and ($sc.BlockCount -eq 1) -and (-not $sc.HasParts)) ''
$se = $CORE::Scan('그냥 평범한 텍스트입니다')
Ok 'Scan(블록 없음) = 0' (($se.BlockCount -eq 0) -and ($se.PartGroups.Count -eq 0)) ''

# ================================================================ 14) BuildContainer (보고 시스템 올리기용 묶음)
Write-Host ''
Write-Host '  -- BuildContainer: 못 읽는 파일은 건너뛴다 --' -ForegroundColor DarkGray
$bcIn = InputList @(
    (NewInput (Join-Path $srcDir 'a.cs') $null),
    (NewInput $missingPath $null),
    (NewInput (Join-Path $srcDir 'b.xaml') $null)
)
$bcCount = 0; $bcBytes = [long]0; $bcErrors = $null
$bc = $JOBS::BuildContainer($bcIn, [ref]$bcCount, [ref]$bcBytes, [ref]$bcErrors)
Ok '없는 파일 1개 섞임 -> 그래도 묶는다' (($null -ne $bc) -and ($bc.Length -gt 0)) ('{0:N0} B' -f $(if ($bc) { $bc.Length } else { 0 }))
Ok '  errors 1건' (($null -ne $bcErrors) -and ($bcErrors.Count -eq 1)) `
   ($(if ($bcErrors -and $bcErrors.Count -gt 0) { $bcErrors[0].Substring(0, [Math]::Min(40, $bcErrors[0].Length)) } else { '' }))
Ok '  파일 수 2, 원본 바이트 합 정확' `
   (($bcCount -eq 2) -and ($bcBytes -eq ((Get-Item -LiteralPath (Join-Path $srcDir 'a.cs')).Length + (Get-Item -LiteralPath (Join-Path $srcDir 'b.xaml')).Length))) `
   ('{0}개 / {1:N0} B' -f $bcCount, $bcBytes)
$bcFiles = $CORE::DecryptAll($bc)
Ok '  되돌리면 읽은 2개가 원본 그대로' `
   (($bcFiles.Count -eq 2) -and ((Sha $bcFiles[0].Data) -eq $expect['a.cs']) -and ((Sha $bcFiles[1].Data) -eq $expect['b.xaml'])) `
   ('{0}개' -f $bcFiles.Count)

# ================================================================ 끝나고 탐색기로 보여 줄 곳 (ShowPath)
Write-Host ''
Write-Host '  -- 복원 후 열어 줄 곳 --' -ForegroundColor DarkGray
$spDir = Join-Path $WORK 'showpath'
New-Item -ItemType Directory -Force (Join-Path $spDir 'src\sub') | Out-Null
$spA = Join-Path $spDir 'src\a.txt'; [IO.File]::WriteAllText($spA, 'aaa')
$spB = Join-Path $spDir 'src\sub\b.txt'; [IO.File]::WriteAllText($spB, 'bbb')

$spR1 = $JOBS::Pack((InputList @((NewInput $spA 'src/a.txt'), (NewInput $spB 'src/sub/b.txt'))), (Join-Path $spDir 'enc1'), (NewOpt $true 0))
$spU1 = $JOBS::Unpack((TextList @($spR1.FullText)), (Join-Path $spDir 'out1'))
Ok '여러 파일 -> 새로 만든 복원 폴더를 연다' (($spU1.CreatedFolder) -and ($spU1.ShowPath -eq $spU1.TargetDir) -and (Test-Path -LiteralPath $spU1.ShowPath -PathType Container)) (Split-Path $spU1.ShowPath -Leaf)
Ok '  (하위 폴더 안의 마지막 파일이 아니라 복원 폴더 자체)' (-not ($spU1.ShowPath -like '*\sub*')) ''

$spR2 = $JOBS::Pack((InputList @((NewInput $spA $null))), (Join-Path $spDir 'enc2'), (NewOpt $false 0))
$spU2 = $JOBS::Unpack((TextList @($spR2.FullText)), (Join-Path $spDir 'out2'))
Ok '파일 하나 -> 그 파일을 선택해 보여 준다' ((-not $spU2.CreatedFolder) -and ($spU2.ShowPath -eq $spU2.WrittenFiles[0]) -and (Test-Path -LiteralPath $spU2.ShowPath -PathType Leaf)) (Split-Path $spU2.ShowPath -Leaf)

$spU3 = $JOBS::Unpack((TextList @('FCRYPT 아님')), (Join-Path $spDir 'out3'))
Ok '복원한 게 없으면 아무것도 열지 않는다' ($null -eq $spU3.ShowPath) ''

# ================================================================ 경로로 추가 (ParsePaths)
Write-Host ''
Write-Host '  -- 경로 입력 --' -ForegroundColor DarkGray
$ppDir = Join-Path $WORK '경로 입력 테스트'          # 공백·한글이 든 폴더
New-Item -ItemType Directory -Force (Join-Path $ppDir 'sub') | Out-Null
$ppA = Join-Path $ppDir '보고서 A.txt'; [IO.File]::WriteAllText($ppA, 'a')
$ppB = Join-Path $ppDir 'b.txt';        [IO.File]::WriteAllText($ppB, 'b')

$pp1 = $JOBS::ParsePaths($ppA)
Ok '경로 하나 (공백·한글 포함)' (($pp1.Found.Count -eq 1) -and ($pp1.Found[0] -eq $ppA)) ''
$pp2 = $JOBS::ParsePaths(('"{0}"' -f $ppA) + "`r`n" + ('"{0}"' -f $ppB))
Ok '탐색기 "경로로 복사" 모양(따옴표, 여러 줄)' (($pp2.Found.Count -eq 2) -and ($pp2.Found[1] -eq $ppB)) ''
$pp3 = $JOBS::ParsePaths(('"{0}" "{1}"' -f $ppA, $ppB))
Ok '  따옴표 두 개가 한 줄에' ($pp3.Found.Count -eq 2) ''
$pp4 = $JOBS::ParsePaths($ppA + ';' + $ppB + ';' + $ppA)
Ok '; 로 구분 + 중복은 한 번만' (($pp4.Found.Count -eq 2)) ''
$pp5 = $JOBS::ParsePaths($ppDir + '\')
Ok '폴더도 됨 (끝의 \ 무시)' (($pp5.Found.Count -eq 1) -and ($pp5.Found[0] -eq $ppDir)) $pp5.Found[0]
$env:FC_PP_TEST = $ppDir
$pp6 = $JOBS::ParsePaths('%FC_PP_TEST%\b.txt')
Ok '환경변수 풀기 (%FC_PP_TEST%\b.txt)' (($pp6.Found.Count -eq 1) -and ($pp6.Found[0] -eq $ppB)) ''
$pp7 = $JOBS::ParsePaths((Join-Path $ppDir '없는 파일.txt'))
Ok '없는 경로 -> Missing' (($pp7.Found.Count -eq 0) -and ($pp7.Missing.Count -eq 1)) ''
$pp8 = $JOBS::ParsePaths("b.txt`r`n.\b.txt`r`nC:b.txt`r`n\b.txt")
Ok '상대 경로·C:abc·\abc -> 절대 경로 아님' (($pp8.NotAbsolute.Count -eq 4) -and ($pp8.Found.Count -eq 0)) (($pp8.NotAbsolute) -join ' | ')
$pp9 = $JOBS::ParsePaths("  `r`n ; `r`n")
Ok '빈 입력 -> 아무것도 없음' (($pp9.Found.Count + $pp9.Missing.Count + $pp9.NotAbsolute.Count) -eq 0) ''
$pp10 = $JOBS::ParsePaths(('C:/' + ($ppA.Substring(3) -replace '\\', '/')))
Ok '/ 로 쓴 경로도 됨' (($pp10.Found.Count -eq 1) -and ($pp10.Found[0] -eq $ppA)) ''

# ================================================================ 왼쪽 폴더 트리 (FolderTree)
Write-Host ''
Write-Host '  -- 폴더 트리 --' -ForegroundColor DarkGray
$FT = [FileCrypt.FolderTree]
$tp = @('C:\x\proj\a.cs', 'C:\x\proj\src\b.cs', 'C:\x\proj\src\bin\c.exe', 'C:\x\proj\doc\d.docx')
$t1 = $FT::Build((TextList $tp), $null)
$r1 = $t1.Roots[0]
Ok '겹치는 앞부분은 건너뛰고 proj 부터 (전체 경로는 툴팁)' (($t1.Roots.Count -eq 1) -and ($r1.Name -eq 'proj') -and ($r1.Path -eq 'C:\x\proj') -and ($r1.TotalFiles -eq 4)) ('{0} ({1}) = {2}' -f $r1.Name, $r1.TotalFiles, $r1.Path)
Ok '  하위 폴더는 이름순 (doc, src)' ((($r1.Children | ForEach-Object Name) -join ',') -eq 'doc,src') (($r1.Children | ForEach-Object Name) -join ',')
Ok '  처음엔 전부 처리' (($r1.State -eq $true) -and ($tp | ForEach-Object { $t1.IsIncluded($_) } | Where-Object { -not $_ }).Count -eq 0) ''

$bin = $t1.Find('C:\x\proj\src\bin')
$bin.State = $false                                      # 체크박스를 끈 것과 같다
$src = $t1.Find('C:\x\proj\src')
Ok 'bin 끄기 -> bin 의 파일만 제외' ((-not $t1.IsIncluded('C:\x\proj\src\bin\c.exe')) -and $t1.IsIncluded('C:\x\proj\src\b.cs') -and $t1.IsIncluded('C:\x\proj\a.cs')) ''
Ok '  src·맨 위는 "일부" 상태' (($null -eq $src.State) -and ($null -eq $r1.State) -and ($bin.State -eq $false)) ''
$ex = $t1.ExcludedDirs()
Ok '  꺼 둔 폴더 기억 = bin' (($ex.Count -eq 1) -and $ex.Contains('C:\x\proj\src\bin')) ''

$t2 = $FT::Build((TextList ($tp + 'C:\x\proj\src\bin\e.dll')), $ex)
Ok '파일을 더 넣어 다시 만들어도 bin 은 꺼진 채' ((-not $t2.IsIncluded('C:\x\proj\src\bin\e.dll')) -and $t2.IsIncluded('C:\x\proj\doc\d.docx')) ''

$src2 = $t2.Find('C:\x\proj\src')
$src2.State = $false
Ok '상위(src) 끄기 -> 자기 파일과 하위(bin) 전부 제외' ((-not $t2.IsIncluded('C:\x\proj\src\b.cs')) -and (-not $t2.IsIncluded('C:\x\proj\src\bin\c.exe')) -and ($src2.State -eq $false)) ''
$src2.State = $true
Ok '다시 켜기 -> 하위까지 전부 처리' ($t2.IsIncluded('C:\x\proj\src\b.cs') -and $t2.IsIncluded('C:\x\proj\src\bin\c.exe') -and ($t2.Roots[0].State -eq $true)) ''

$t2.SetAll($false)
Ok '모두 해제 -> 처리할 파일 없음, 맨 위 빈칸' (($t2.Roots[0].State -eq $false) -and (-not $t2.IsIncluded('C:\x\proj\a.cs'))) ''

$t3 = $FT::Build((TextList @('C:\a\x.txt', 'D:\b\y.txt', 'D:\b\z\w.txt')), $null)
Ok '드라이브가 다르면 맨 위가 따로' (($t3.Roots.Count -eq 2) -and ($t3.Roots[0].Path -eq 'C:\a') -and ($t3.Roots[1].Path -eq 'D:\b')) (($t3.Roots | ForEach-Object Path) -join ' / ')

$fired = 0
$t3.add_Changed({ $script:fired++ })
$t3.Roots[1].State = $false
Ok '체크가 바뀌면 알림(목록·계획 줄 다시 맞춤)' ($fired -eq 1) ''

Complete-Test

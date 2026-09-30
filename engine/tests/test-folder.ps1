. (Join-Path $PSScriptRoot '_common.ps1')

# 폴더 단위 처리. 하위 폴더 구조가 그대로 돌아와야 하고, 이름이 같은 파일이 여러 깊이에 있어도
# 평평하게 풀려 "(1)" 로 밀리면 안 된다. 악의적인 경로는 지정 폴더 밖으로 못 나가야 한다.
# (예전 test-folder + test-folder-bulk 를 합쳤다. 표본은 큰 쪽 트리 하나로 통일.)

Start-Test -Tag folder -Title '폴더 단위 처리 (구조 보존 / 대량)' -Pad 48
Import-FileCrypt
$u8b   = New-Object System.Text.UTF8Encoding($true)
$cp949 = [System.Text.Encoding]::GetEncoding(949)

# ================================================================ 표본 트리
# 5단계 깊이 / 여러 인코딩 / 빈 파일 / 바이너리 / 이름 충돌 / 한글·대괄호 경로 / 빈 폴더
$proj = Join-Path $WORK '대상폴더'
$rand = New-Object System.Random 20260903
$made = @{}          # 상대경로 -> SHA256

function Put([string]$rel, [byte[]]$bytes) {
    $p = Join-Path $proj $rel
    $d = [System.IO.Path]::GetDirectoryName($p)
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force $d | Out-Null }
    [System.IO.File]::WriteAllBytes($p, $bytes)
    $script:made['대상폴더\' + $rel] = (ShaFile $p)
}

# 1) 깊이별로 같은 이름의 파일을 뿌린다 (평평하게 풀리면 반드시 충돌)
$depths = @('', 'src', 'src\ui', 'src\ui\parts', 'src\ui\parts\deep')
foreach ($d in $depths) {
    foreach ($nm in @('App.cs', 'View.xaml', 'readme.md')) {
        $rel = if ($d -eq '') { $nm } else { Join-Path $d $nm }
        Put $rel $u8n.GetBytes("경로: $d / 이름: $nm`r`n" + ("내용 줄`r`n" * 30))
    }
}

# 2) 인코딩/종류 섞기
Put 'enc\utf8-bom.txt'   $u8b.GetBytes("한글 UTF-8 BOM`r`n" * 40)
Put 'enc\cp949.txt'      $cp949.GetBytes("한글 CP949`r`n" * 40)
Put 'enc\utf16.txt'      ([System.Text.Encoding]::Unicode.GetPreamble() + [System.Text.Encoding]::Unicode.GetBytes("한글 UTF-16`r`n" * 40))
Put 'bin\allbytes.bin'   ([byte[]](0..255))
Put 'bin\empty.dat'      (New-Object byte[] 0)
Put 'bin\one.bin'        ([byte[]](0x41))
Put '이름 [대괄호] (괄호) & 기호.txt' $u8n.GetBytes("특수문자 파일명`r`n" * 20)
Put 'イメージ\日本語.json' $u8n.GetBytes('{"키":"값"}')
# 한글 폴더 이름 / 하위 폴더 안의 대괄호 이름 (예전 test-folder 표본)
Put 'doc\보고서\최종.txt'       $u8n.GetBytes("보고서 본문`r`n" * 50)
Put 'src\ui\한글 뷰 [v2].xaml' $u8n.GetBytes("<Window Title=`"한글`"/>`r`n")

# 3) 랜덤 바이너리 다수 (압축 불가) + 텍스트 다수
for ($i = 1; $i -le 60; $i++) {
    $b = New-Object byte[] (256 + $rand.Next(4096))
    $rand.NextBytes($b)
    Put ("blob\sub{0}\r{1}.bin" -f ($i % 7), $i) $b
}
for ($i = 1; $i -le 120; $i++) {
    Put ("txt\g{0}\doc{1}.txt" -f ($i % 11), $i) $u8n.GetBytes(("문서 $i 내용 줄`r`n" * (5 + ($i % 40))))
}

# 4) 빈 폴더 (파일이 없으므로 결과에 안 나와야 정상)
New-Item -ItemType Directory -Force (Join-Path $proj 'empty-dir\nested') | Out-Null

$realCount = (Get-ChildItem -LiteralPath $proj -File -Recurse).Count
$realBytes = (Get-ChildItem -LiteralPath $proj -File -Recurse | Measure-Object Length -Sum).Sum
Write-Host ('  표본: 파일 {0:N0}개 / {1:N0} B / 최대 5단계' -f $realCount, $realBytes) -ForegroundColor DarkGray
Ok '표본 트리 생성' ($realCount -eq $made.Count) ('{0}개' -f $realCount)

# 복원 폴더를 표본과 대조한다: 누락 / 해시 불일치 / "(n)" 충돌
function Compare-Restored([string]$outRoot) {
    $missing = 0; $mismatch = 0
    foreach ($rel in $script:made.Keys) {
        $p = Join-Path $outRoot $rel
        if (-not (Test-Path -LiteralPath $p)) { $missing++; continue }
        if ((ShaFile $p) -ne $script:made[$rel]) { $mismatch++ }
    }
    $dupes = @(Get-ChildItem -LiteralPath $outRoot -File -Recurse | Where-Object { $_.Name -match '\(\d+\)' }).Count
    $count = @(Get-ChildItem -LiteralPath $outRoot -File -Recurse).Count
    return @{ Missing = $missing; Mismatch = $mismatch; Dupes = $dupes; Count = $count }
}

# ================================================================ 1) GUI 폴더 순회 (실제 GUI 가 쓰는 코드)
$entries = [FileCrypt.FileCryptCore]::EnumerateFolder($proj)
Ok 'GUI 폴더 순회: 하위 전부 잡음' ($entries.Count -eq $realCount) ('{0} / {1}' -f $entries.Count, $realCount)

$relOk = $true
foreach ($e in $entries) {
    if (-not $e.RelativePath.StartsWith('대상폴더\')) { $relOk = $false; break }
    if (-not $made.ContainsKey($e.RelativePath))      { $relOk = $false; break }
}
Ok 'GUI 폴더 순회: 상대 경로가 정확' $relOk ''

# ================================================================ 2) C# 블록 방식 (파일마다 블록) 묶고 -> 되돌리기
$sw = [Diagnostics.Stopwatch]::StartNew()
$chunks = New-Object System.Collections.Generic.List[string]
foreach ($e in $entries) {
    $c = [FileCrypt.FileCryptCore]::Encrypt($e.RelativePath, [System.IO.File]::ReadAllBytes($e.FullPath))
    $chunks.Add([FileCrypt.FileCryptCore]::ToArmor($c, 100))
}
$bundle = ($chunks -join "`r`n`r`n") + "`r`n"
$encMs = $sw.ElapsedMilliseconds
Write-Host ('  묶음: {0:N0} 자 / {1:N0} 줄 / 원본 대비 {2:N1}%  (암호화 {3:N1}s)' -f `
    $bundle.Length, ($bundle -split "`r?`n").Count, ($bundle.Length / [double]$realBytes * 100), ($encMs/1000)) -ForegroundColor DarkGray

$sw.Restart()
$blocks = [FileCrypt.FileCryptCore]::ExtractBlocks($bundle)
Ok '묶음에서 블록 전부 인식' ($blocks.Count -eq $realCount) ('{0} / {1}' -f $blocks.Count, $realCount)

$out = Join-Path $WORK 'restored'
New-Item -ItemType Directory -Force $out | Out-Null
$restoredOk = 0; $restoredNg = 0
foreach ($b in $blocks) {
    try {
        $df = [FileCrypt.FileCryptCore]::Decrypt($b)
        $dest = [FileCrypt.FileCryptCore]::ResolveNonClobbering($out, $df.FileName)
        [System.IO.File]::WriteAllBytes($dest, $df.Data)
        $restoredOk++
    } catch { $restoredNg++ }
}
Write-Host ('  복원 {0:N1}s' -f ($sw.ElapsedMilliseconds/1000)) -ForegroundColor DarkGray
Ok '전 블록 복호화 성공' ($restoredNg -eq 0) ('성공 {0} / 실패 {1}' -f $restoredOk, $restoredNg)

$cmp = Compare-Restored $out
Ok '복원 파일 개수 일치' ($cmp.Count -eq $realCount) ('{0} / {1}' -f $cmp.Count, $realCount)
Ok '전 파일 경로 그대로 존재' ($cmp.Missing -eq 0) ('누락 {0}개' -f $cmp.Missing)
Ok '전 파일 SHA-256 일치'     ($cmp.Mismatch -eq 0) ('불일치 {0}개' -f $cmp.Mismatch)
Ok '이름 충돌로 "(n)" 이 생기지 않음' ($cmp.Dupes -eq 0) ('{0}개' -f $cmp.Dupes)
Ok '빈 폴더는 결과에 없음 (파일이 없으므로)' (-not (Test-Path -LiteralPath (Join-Path $out '대상폴더\empty-dir'))) ''
Ok '5단계 깊이 파일 제자리에 복원' (Test-Path -LiteralPath (Join-Path $out '대상폴더\src\ui\parts\deep\App.cs')) ''

# ================================================================ 3) PowerShell 간편모드: 폴더 통째로
Write-Host ''
Write-Host '  -- PowerShell 간편모드 (같은 폴더) --' -ForegroundColor DarkGray
& $SIMPLE -Mode Encrypt -Path $proj *>&1 | Out-Null
$psBundle = Get-ChildItem -LiteralPath $WORK -Filter 'FCRYPT 묶음*.txt' | Select-Object -First 1
Ok 'PS 간편모드 묶음 생성' ($null -ne $psBundle) $(if ($psBundle) { '{0} / {1:N0} B' -f $psBundle.Name, $psBundle.Length } else { '없음' })

if ($psBundle) {
    $psBlocks = [FileCrypt.FileCryptCore]::ExtractBlocks([System.IO.File]::ReadAllText($psBundle.FullName))
    # 폴더 입력은 아카이브 1블록 (파일마다 블록을 만들지 않는다)
    Ok 'PS 묶음: 아카이브 1블록' ($psBlocks.Count -eq 1) ('{0}개' -f $psBlocks.Count)
    $psInside = [FileCrypt.FileCryptCore]::DecryptAll($psBlocks[0]).Count
    Ok 'PS 아카이브 안 파일 수 일치' ($psInside -eq $realCount) ('{0} / {1}' -f $psInside, $realCount)

    # (a) C# 이 되돌린다
    $out2 = Join-Path $WORK 'restored_cs'
    New-Item -ItemType Directory -Force $out2 | Out-Null
    $ng2 = 0
    foreach ($b in $psBlocks) {
        try {
            foreach ($df in [FileCrypt.FileCryptCore]::DecryptAll($b)) {
                $dest = [FileCrypt.FileCryptCore]::ResolveNonClobbering($out2, $df.FileName)
                [System.IO.File]::WriteAllBytes($dest, $df.Data)
            }
        } catch { $ng2++ }
    }
    $c2 = Compare-Restored $out2
    Ok 'PS 묶음 -> C# 복원: 전 파일 경로/해시 일치' (($ng2 -eq 0) -and ($c2.Missing -eq 0) -and ($c2.Mismatch -eq 0)) `
        ('실패 {0} / 누락 {1} / 불일치 {2}' -f $ng2, $c2.Missing, $c2.Mismatch)

    # (b) PS 엔진이 되돌린다
    $out3 = Join-Path $WORK 'restored_ps'
    New-Item -ItemType Directory -Force $out3 | Out-Null
    $global:LASTEXITCODE = 0
    & $ENGINE -Mode Decrypt -Path $psBundle.FullName -OutDir $out3 -Force -Quiet 2>$null | Out-Null
    $rc3 = $LASTEXITCODE
    $c3 = Compare-Restored $out3
    Ok 'PS 묶음 -> PS 엔진 복원' (($rc3 -eq 0) -and ($c3.Count -eq $realCount)) ('{0}개, rc={1}' -f $c3.Count, $rc3)
    Ok '  폴더 구조 그대로 (하위 폴더 포함)' ($c3.Missing -eq 0) ('누락 {0}개' -f $c3.Missing)
    Ok '  전 파일 해시 일치' ($c3.Mismatch -eq 0) ('불일치 {0}개' -f $c3.Mismatch)
    Ok '  이름 충돌로 "(1)" 이 생기지 않음' ($c3.Dupes -eq 0) ('{0}개' -f $c3.Dupes)
}

# ================================================================ 4) 간편모드: 파일 여러 개 (폴더 아님) -> 블록 방식 묶음
# 예전 test-gui-compat. 파일을 여러 개 고르면 파일마다 블록이고, GUI(C#)가 전부 읽어야 한다.
Write-Host ''
Write-Host '  -- PowerShell 간편모드 (파일 여러 개) --' -ForegroundColor DarkGray
$multi = @(
    @{ Name = 'sample.xml';          Bytes = $u8n.GetBytes("<r>`r`n" + ("  <row>데이터 행</row>`r`n" * 500) + "</r>") },
    @{ Name = '한글 보고서 [v2].txt'; Bytes = $u8n.GetBytes("한글 본문 줄`r`n" * 300) },
    @{ Name = 'bytes.bin';           Bytes = [byte[]](0..255) },
    @{ Name = 'one.bin';             Bytes = [byte[]](0x41) }
)
$srcDir = Join-Path $WORK 'bundle_src'
New-Item -ItemType Directory -Force $srcDir | Out-Null
$paths = @()
foreach ($s in $multi) {
    $p = Join-Path $srcDir $s.Name
    [System.IO.File]::WriteAllBytes($p, $s.Bytes)
    $paths += $p
}
& $SIMPLE -Mode Encrypt -Path $paths *>&1 | Out-Null
$bundleFile = Get-ChildItem -LiteralPath $srcDir -Filter 'FCRYPT 묶음*.txt' | Select-Object -First 1
if ($bundleFile) {
    $bl = [FileCrypt.FileCryptCore]::ExtractBlocks([System.IO.File]::ReadAllText($bundleFile.FullName))
    $match = $true
    foreach ($blk in $bl) {
        $df = [FileCrypt.FileCryptCore]::Decrypt($blk)
        $want = $multi | Where-Object { $_.Name -eq $df.FileName } | Select-Object -First 1
        if ($null -eq $want -or (Sha $df.Data) -ne (Sha $want.Bytes)) { $match = $false }
    }
    Ok 'PS 간편모드 여러 파일 -> C# 이 전부 복원' (($bl.Count -eq $paths.Count) -and $match) ('블록 {0}개' -f $bl.Count)
} else {
    Ok 'PS 간편모드 여러 파일 -> C# 이 전부 복원' $false '묶음 파일 없음'
}

# ================================================================ 5) 반복 루프 (같은 폴더 5회)
Write-Host ''
Write-Host '  -- 같은 폴더 5회 반복 --' -ForegroundColor DarkGray
$loopBad = 0
for ($r = 1; $r -le 5; $r++) {
    $ch = New-Object System.Collections.Generic.List[string]
    foreach ($e in $entries) {
        $c = [FileCrypt.FileCryptCore]::Encrypt($e.RelativePath, [System.IO.File]::ReadAllBytes($e.FullPath))
        $ch.Add([FileCrypt.FileCryptCore]::ToArmor($c, 100))
    }
    $bl = [FileCrypt.FileCryptCore]::ExtractBlocks(($ch -join "`r`n`r`n"))
    if ($bl.Count -ne $realCount) { $loopBad++; continue }
    foreach ($b in $bl) {
        $df = [FileCrypt.FileCryptCore]::Decrypt($b)
        if ((Sha $df.Data) -ne $made[$df.FileName]) { $loopBad++; break }
    }
}
Ok ('5회 반복 x {0}파일 = {1}회 왕복' -f $realCount, (5 * $realCount)) ($loopBad -eq 0) ('불일치 {0}회' -f $loopBad)

# ================================================================ 6) 경로 탈출 차단 (C# 코어)
Write-Host ''
Write-Host '  -- 악의적 경로 차단 (컨테이너는 남이 만들 수 있다) --' -ForegroundColor DarkGray
$jail = Join-Path $WORK 'jail'
New-Item -ItemType Directory -Force $jail | Out-Null
$evil = @(
    '..\..\..\Windows\System32\evil.dll',
    '..\바깥.txt',
    'C:\Windows\Temp\evil.txt',
    '\\서버\공유\evil.txt',
    '/etc/passwd',
    'a\..\..\b\escape.txt'
)
foreach ($e in $evil) {
    $safe = [FileCrypt.FileCryptCore]::SanitizeRelativePath($e)
    $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($jail, $safe))
    $root = [System.IO.Path]::GetFullPath($jail).TrimEnd('\') + '\'
    $inside = $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
    Ok ('탈출 차단: ' + $e) $inside ('-> ' + $safe)
}

# ================================================================ 7) PS 엔진도 경로 탈출 차단 (-Name 으로 넣은 단일 파일)
Write-Host ''
Write-Host '  -- PowerShell 엔진도 막는가 --' -ForegroundColor DarkGray
$jail2 = Join-Path $WORK 'jail_ps'
New-Item -ItemType Directory -Force $jail2 | Out-Null
$victim = Join-Path $WORK 'victim.txt'
[System.IO.File]::WriteAllText($victim, "원래 있던 파일`r`n", $u8n)
$victimHash = ShaFile $victim

$src = Join-Path $WORK 'payload.txt'
[System.IO.File]::WriteAllText($src, "덮어쓰기 시도`r`n", $u8n)
$evilTxt = Join-Path $WORK 'evil.enc.txt'
& $ENGINE -Mode Encrypt -Path $src -Name '..\victim.txt' -Out $evilTxt -Armor -Width 100 -Force -Quiet 2>$null | Out-Null

$global:LASTEXITCODE = 0
& $ENGINE -Mode Decrypt -Path $evilTxt -OutDir $jail2 -Quiet 2>$null | Out-Null
Ok '상위 폴더 덮어쓰기 시도 -> 원본 그대로' ((ShaFile $victim) -eq $victimHash) ''
Ok '지정한 폴더 안에만 씀' ((Get-ChildItem -LiteralPath $jail2 -File -Recurse).Count -ge 1) ''

Complete-Test

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ENGINE  = Join-Path $ROOTDIR 'engine\filecrypt.ps1'
$EXE     = Join-Path $ROOTDIR 'gui\bin\Release\net48\FileCrypt.exe'
$WORK    = Join-Path $env:TEMP ('fc_arch_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $WORK | Out-Null
$u8n = New-Object System.Text.UTF8Encoding($false)

$n = 0; $fail = 0
function Ok([string]$name, [bool]$cond, [string]$extra) {
    $script:n++
    if ($cond) { Write-Host ('  [PASS] {0}  {1}' -f $name.PadRight(46), $extra) -ForegroundColor Green }
    else       { Write-Host ('  [FAIL] {0}  {1}' -f $name.PadRight(46), $extra) -ForegroundColor Red; $script:fail++ }
}
function Sha([byte[]]$b) {
    $s = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($s.ComputeHash($b))).Replace('-','') } finally { $s.Dispose() }
}

Write-Host ''
Write-Host '########## 아카이브 모드 (폴더 = 1블록) ##########' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $EXE)) { Write-Host '  [FAIL] gui 빌드 없음' -ForegroundColor Red; exit 1 }
$exeCopy = Join-Path $WORK 'FileCrypt.exe'
Copy-Item -LiteralPath $EXE -Destination $exeCopy -Force
[void][Reflection.Assembly]::LoadFrom($exeCopy)

# ---------------------------------------------------------------- 표본 폴더
$proj = Join-Path $WORK '대상폴더'
$expected = @{}
function Put([string]$rel, [byte[]]$bytes) {
    $p = Join-Path $proj $rel
    $d = [System.IO.Path]::GetDirectoryName($p)
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force $d | Out-Null }
    [System.IO.File]::WriteAllBytes($p, $bytes)
    $script:expected['대상폴더\' + $rel] = (Sha $bytes)
}
# 같은 이름을 여러 깊이에 + 여러 인코딩 + 빈 파일 + 바이너리
foreach ($d in @('', 'src', 'src\ui', 'src\ui\deep')) {
    foreach ($nm in @('App.cs', 'View.xaml')) {
        $rel = if ($d -eq '') { $nm } else { Join-Path $d $nm }
        Put $rel $u8n.GetBytes("using System;`r`n// $d/$nm`r`n" + ("    public int X { get; set; }`r`n" * 25))
    }
}
Put 'bin\empty.dat'   (New-Object byte[] 0)
Put 'bin\bytes.bin'   ([byte[]](0..255))
Put '한글 [대괄호].txt' $u8n.GetBytes("한글 본문`r`n" * 50)
Put 'イメージ\日本語.json' $u8n.GetBytes('{"키":"값"}')
for ($i = 1; $i -le 120; $i++) { Put ("many\g$($i % 9)\f$i.cs") $u8n.GetBytes("namespace N$i { class C$i { public int Id; public string Name; } }`r`n") }

$count = (Get-ChildItem -LiteralPath $proj -File -Recurse).Count
$bytes = (Get-ChildItem -LiteralPath $proj -File -Recurse | Measure-Object Length -Sum).Sum
Ok '표본 트리 생성' ($count -eq $expected.Count) ('{0}개 / {1:N0} B' -f $count, $bytes)

# ================================================================ 1) PS 엔진: 폴더 -> 아카이브
$psTxt = Join-Path $WORK 'ps.enc.txt'
$global:LASTEXITCODE = 0
& $ENGINE -Mode Encrypt -Folder $proj -Out $psTxt -Armor -Width 100 -Force -Quiet 2>$null | Out-Null
Ok 'PS: 폴더 -> 아카이브 생성' (($LASTEXITCODE -eq 0) -and (Test-Path -LiteralPath $psTxt)) ('rc=' + $LASTEXITCODE)

$psBlocks = [FileCrypt.FileCryptCore]::ExtractBlocks([System.IO.File]::ReadAllText($psTxt))
Ok 'PS 아카이브는 블록이 1개' ($psBlocks.Count -eq 1) ('{0}개' -f $psBlocks.Count)

$psFiles = [FileCrypt.FileCryptCore]::DecryptAll($psBlocks[0])
Ok 'C# 이 PS 아카이브를 읽음' ($psFiles.Count -eq $count) ('{0} / {1}' -f $psFiles.Count, $count)

$bad = 0
foreach ($f in $psFiles) { if ($expected[$f.FileName] -ne (Sha $f.Data)) { $bad++ } }
Ok 'PS 아카이브: 이름/해시 전부 일치' ($bad -eq 0) ('불일치 {0}개' -f $bad)

# ================================================================ 2) C# 코어: 폴더 -> 아카이브 -> PS 복원
$entries = [FileCrypt.FileCryptCore]::EnumerateFolder($proj)
$items = New-Object 'System.Collections.Generic.List[FileCrypt.ArchiveItem]'
foreach ($e in $entries) {
    $it = New-Object FileCrypt.ArchiveItem
    $it.Name = $e.RelativePath
    $it.Data = [System.IO.File]::ReadAllBytes($e.FullPath)
    $items.Add($it)
}
$csTxt = Join-Path $WORK 'cs.enc.txt'
[System.IO.File]::WriteAllText($csTxt, [FileCrypt.FileCryptCore]::ToArmor([FileCrypt.FileCryptCore]::EncryptArchive($items), 100), $u8n)
Ok 'C#: 폴더 -> 아카이브 생성' (Test-Path -LiteralPath $csTxt) ('{0:N0} B' -f (Get-Item $csTxt).Length)

$csOut = Join-Path $WORK 'out_cs'
$global:LASTEXITCODE = 0
& $ENGINE -Mode Decrypt -Path $csTxt -OutDir $csOut -Force -Quiet 2>$null | Out-Null
Ok 'PS 가 C# 아카이브를 읽음' ($LASTEXITCODE -eq 0) ('rc=' + $LASTEXITCODE)

$restored = Get-ChildItem -LiteralPath $csOut -File -Recurse
Ok '복원 파일 개수 일치' ($restored.Count -eq $count) ('{0} / {1}' -f $restored.Count, $count)

$miss = 0; $mis = 0
foreach ($rel in $expected.Keys) {
    $p = Join-Path $csOut $rel
    if (-not (Test-Path -LiteralPath $p)) { $miss++; continue }
    if ((Sha ([System.IO.File]::ReadAllBytes($p))) -ne $expected[$rel]) { $mis++ }
}
Ok '폴더 구조 + 전 파일 해시 일치' (($miss -eq 0) -and ($mis -eq 0)) ('누락 {0} / 불일치 {1}' -f $miss, $mis)

$dupes = $restored | Where-Object { $_.Name -match '\(\d+\)' }
Ok '이름 충돌로 "(n)" 없음' ($dupes.Count -eq 0) ('{0}개' -f $dupes.Count)

# ================================================================ 3) 크기 비교 (블록 방식 vs 아카이브)
$chunks = New-Object System.Collections.Generic.List[string]
foreach ($e in $entries) {
    $c = [FileCrypt.FileCryptCore]::Encrypt($e.RelativePath, [System.IO.File]::ReadAllBytes($e.FullPath))
    $chunks.Add([FileCrypt.FileCryptCore]::ToArmor($c, 100))
}
$blockText = ($chunks -join "`r`n`r`n")
$archText  = [System.IO.File]::ReadAllText($csTxt)
Write-Host ''
Write-Host ('  원본 {0:N0} B / 파일 {1}개' -f $bytes, $count) -ForegroundColor DarkGray
Write-Host ('  블록 방식  {0,9:N0} 자   원본 대비 {1,6:N1}%' -f $blockText.Length, ($blockText.Length/[double]$bytes*100)) -ForegroundColor DarkGray
Write-Host ('  아카이브   {0,9:N0} 자   원본 대비 {1,6:N1}%   {2:N1}배 작음' -f $archText.Length, ($archText.Length/[double]$bytes*100), ($blockText.Length/[double]$archText.Length)) -ForegroundColor DarkGray
Ok '아카이브가 블록 방식보다 작음' ($archText.Length -lt $blockText.Length) ('{0:N1}배' -f ($blockText.Length/[double]$archText.Length))

# ================================================================ 4) 손상 거부 + 경로 탈출 차단
Write-Host ''
Write-Host '  -- 손상/공격 --' -ForegroundColor DarkGray
$c = [FileCrypt.FileCryptCore]::EncryptArchive($items)

$b1 = [byte[]]$c.Clone(); $p1 = $c.Length - 10; $b1[$p1] = $b1[$p1] -bxor 1
try { [FileCrypt.FileCryptCore]::DecryptAll($b1) | Out-Null; Ok '아카이브 1비트 변조 -> 거부' $false '통과해버림' }
catch { Ok '아카이브 1비트 변조 -> 거부' $true $_.Exception.GetType().Name }

$t = New-Object byte[] ($c.Length - 64); [Array]::Copy($c, $t, $t.Length)
try { [FileCrypt.FileCryptCore]::DecryptAll($t) | Out-Null; Ok '아카이브 뒤쪽 잘림 -> 거부' $false '통과해버림' }
catch { Ok '아카이브 뒤쪽 잘림 -> 거부' $true $_.Exception.GetType().Name }

# 악의적 상대 경로가 아카이브 안에 있을 때
$evil = New-Object 'System.Collections.Generic.List[FileCrypt.ArchiveItem]'
foreach ($nm in @('..\..\탈출.txt', 'C:\Windows\Temp\evil.txt')) {
    $it = New-Object FileCrypt.ArchiveItem
    $it.Name = $nm
    $it.Data = $u8n.GetBytes('x')
    $evil.Add($it)
}
$evilTxt = Join-Path $WORK 'evil.enc.txt'
[System.IO.File]::WriteAllText($evilTxt, [FileCrypt.FileCryptCore]::ToArmor([FileCrypt.FileCryptCore]::EncryptArchive($evil), 100), $u8n)
$jail = Join-Path $WORK 'jail'
New-Item -ItemType Directory -Force $jail | Out-Null
$victim = Join-Path $WORK '탈출.txt'
[System.IO.File]::WriteAllText($victim, "원래 파일`r`n", $u8n)
$vh = (Get-FileHash -LiteralPath $victim -Algorithm SHA256).Hash
& $ENGINE -Mode Decrypt -Path $evilTxt -OutDir $jail -Force -Quiet 2>$null | Out-Null
$safe = ((Get-FileHash -LiteralPath $victim -Algorithm SHA256).Hash -eq $vh)
Ok '아카이브 안 경로 탈출 -> 원본 무사' $safe ''
$inJail = (Get-ChildItem -LiteralPath $jail -File -Recurse).Count
Ok '아카이브 안 경로 탈출 -> 지정 폴더 안에만' ($inJail -ge 1) ('{0}개' -f $inJail)

# ================================================================ 4b) 긴 이름 (255바이트 초과)
Write-Host ''
Write-Host '  -- 이름이 255바이트를 넘는 경우 --' -ForegroundColor DarkGray
$longName = (('가나다라마바사아자차' * 3) + '/') * 8 + 'end.txt'   # UTF-8 로 700바이트 이상
$li = New-Object FileCrypt.ArchiveItem
$li.Name = $longName
$li.Data = $u8n.GetBytes('long name payload')
$ll = New-Object 'System.Collections.Generic.List[FileCrypt.ArchiveItem]'
$ll.Add($li)
$g1 = New-Object FileCrypt.ArchiveItem; $g1.Name = 'a.txt'; $g1.Data = $u8n.GetBytes('a'); $ll.Add($g1)
$g2 = New-Object FileCrypt.ArchiveItem; $g2.Name = 'b/c.txt'; $g2.Data = $u8n.GetBytes('bc'); $ll.Add($g2)

$lnTxt = Join-Path $WORK 'longname.enc.txt'
[System.IO.File]::WriteAllText($lnTxt, [FileCrypt.FileCryptCore]::ToArmor([FileCrypt.FileCryptCore]::EncryptArchive($ll), 100), $u8n)

$csRead = [FileCrypt.FileCryptCore]::DecryptAll([FileCrypt.FileCryptCore]::ExtractBlocks([System.IO.File]::ReadAllText($lnTxt))[0])
Ok ('C#: 이름 {0}바이트 항목 읽기' -f ([System.Text.Encoding]::UTF8.GetByteCount($longName))) `
   (($csRead.Count -eq 3) -and ($csRead[0].FileName -eq $longName)) ('{0}개' -f $csRead.Count)

# PS 엔진도 같은 것을 읽어야 한다 (저장은 경로 길이 때문에 실패할 수 있으므로 개수만 본다)
$lnOut = Join-Path $WORK 'lnout'
New-Item -ItemType Directory -Force $lnOut | Out-Null
$global:LASTEXITCODE = 0
& $ENGINE -Mode Decrypt -Path $lnTxt -OutDir $lnOut -Force -Quiet 2>$null | Out-Null
$lnRc = $LASTEXITCODE
$lnGot = (Get-ChildItem -LiteralPath $lnOut -File -Recurse).Count
Ok 'PS: 긴 이름이 섞여도 나머지는 복원' ($lnGot -ge 2) ('{0}개 복원, rc={1}' -f $lnGot, $lnRc)

# ================================================================ 5) 블록 방식과 아카이브가 한 텍스트에 섞여도
Write-Host ''
Write-Host '  -- 혼합 텍스트 --' -ForegroundColor DarkGray
$single = [FileCrypt.FileCryptCore]::ToArmor([FileCrypt.FileCryptCore]::Encrypt('낱개.txt', $u8n.GetBytes('낱개 파일 내용')), 100)
$mixed = $single + "`r`n`r`n" + $archText.TrimEnd() + "`r`n`r`n" + $single
$mb = [FileCrypt.FileCryptCore]::ExtractBlocks($mixed)
$totalFiles = 0
foreach ($b in $mb) { $totalFiles += [FileCrypt.FileCryptCore]::DecryptAll($b).Count }
Ok '단일 블록 + 아카이브 혼합 인식' ($mb.Count -eq 3) ('블록 {0}개' -f $mb.Count)
Ok '혼합에서 파일 총 개수 정확' ($totalFiles -eq ($count + 2)) ('{0} / {1}' -f $totalFiles, ($count + 2))

Write-Host ''
Write-Host ('########## 아카이브 테스트: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })

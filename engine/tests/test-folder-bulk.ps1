$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ENGINE  = Join-Path $ROOTDIR 'engine\filecrypt.ps1'
$SIMPLE  = Join-Path $ROOTDIR 'engine\simple.ps1'
$EXE     = Join-Path $ROOTDIR 'gui\bin\Release\net48\FileCrypt.exe'
$KEY     = 'FileCrypt/default/v2/no-password'
$WORK    = Join-Path $env:TEMP ('fc_bulk_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $WORK | Out-Null
$u8n = New-Object System.Text.UTF8Encoding($false)
$u8b = New-Object System.Text.UTF8Encoding($true)
$cp949 = [System.Text.Encoding]::GetEncoding(949)

$n = 0; $fail = 0
function Ok([string]$name, [bool]$cond, [string]$extra) {
    $script:n++
    if ($cond) { Write-Host ('  [PASS] {0}  {1}' -f $name.PadRight(46), $extra) -ForegroundColor Green }
    else       { Write-Host ('  [FAIL] {0}  {1}' -f $name.PadRight(46), $extra) -ForegroundColor Red; $script:fail++ }
}

Write-Host ''
Write-Host '########## 폴더 대량 실측 루프 테스트 ##########' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $EXE)) {
    Write-Host '  [FAIL] gui 빌드 산출물이 없습니다. dotnet build -c Release 먼저.' -ForegroundColor Red
    exit 1
}
$exeCopy = Join-Path $WORK 'FileCrypt.exe'
Copy-Item -LiteralPath $EXE -Destination $exeCopy -Force
[void][Reflection.Assembly]::LoadFrom($exeCopy)

# ================================================================ 표본 트리
# 5단계 깊이 / 여러 인코딩 / 빈 파일 / 바이너리 / 이름 충돌 / 빈 폴더
$proj = Join-Path $WORK '대상폴더'
$rand = New-Object System.Random 20260903
$made = @{}          # 상대경로 -> SHA256

function Put([string]$rel, [byte[]]$bytes) {
    $p = Join-Path $proj $rel
    $d = [System.IO.Path]::GetDirectoryName($p)
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force $d | Out-Null }
    [System.IO.File]::WriteAllBytes($p, $bytes)
    $script:made['대상폴더\' + $rel] = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
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

# ================================================================ 1) GUI 폴더 순회 (실제 GUI 가 쓰는 코드)
$entries = [FileCrypt.FileCryptCore]::EnumerateFolder($proj)
Ok 'GUI 폴더 순회: 하위 전부 잡음' ($entries.Count -eq $realCount) ('{0} / {1}' -f $entries.Count, $realCount)

$relOk = $true
foreach ($e in $entries) {
    if (-not $e.RelativePath.StartsWith('대상폴더\')) { $relOk = $false; break }
    if (-not $made.ContainsKey($e.RelativePath))      { $relOk = $false; break }
}
Ok 'GUI 폴더 순회: 상대 경로가 정확' $relOk ''

# ================================================================ 2) GUI 경로 그대로 묶고 → 되돌리기
$sw = [Diagnostics.Stopwatch]::StartNew()
$chunks = New-Object System.Collections.Generic.List[string]
foreach ($e in $entries) {
    $c = [FileCrypt.FileCryptCore]::Encrypt($e.RelativePath, [System.IO.File]::ReadAllBytes($e.FullPath))
    $chunks.Add([FileCrypt.FileCryptCore]::ToArmor($c, 100))
}
$bundle = ($chunks -join "`r`n`r`n") + "`r`n"
$encMs = $sw.ElapsedMilliseconds

$bundleFile = Join-Path $WORK 'bundle.txt'
[System.IO.File]::WriteAllText($bundleFile, $bundle, $u8n)
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
$decMs = $sw.ElapsedMilliseconds
Write-Host ('  복원 {0:N1}s' -f ($decMs/1000)) -ForegroundColor DarkGray
Ok '전 블록 복호화 성공' ($restoredNg -eq 0) ('성공 {0} / 실패 {1}' -f $restoredOk, $restoredNg)

# ================================================================ 3) 전수 대조
$outCount = (Get-ChildItem -LiteralPath $out -File -Recurse).Count
Ok '복원 파일 개수 일치' ($outCount -eq $realCount) ('{0} / {1}' -f $outCount, $realCount)

$missing = 0; $mismatch = 0
foreach ($rel in $made.Keys) {
    $p = Join-Path $out $rel
    if (-not (Test-Path -LiteralPath $p)) { $missing++; continue }
    if ((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne $made[$rel]) { $mismatch++ }
}
Ok '전 파일 경로 그대로 존재' ($missing -eq 0) ('누락 {0}개' -f $missing)
Ok '전 파일 SHA-256 일치'     ($mismatch -eq 0) ('불일치 {0}개' -f $mismatch)

$dupes = Get-ChildItem -LiteralPath $out -File -Recurse | Where-Object { $_.Name -match '\(\d+\)' }
Ok '이름 충돌로 "(n)" 이 생기지 않음' ($dupes.Count -eq 0) ('{0}개' -f $dupes.Count)

$emptyDir = Join-Path $out '대상폴더\empty-dir'
Ok '빈 폴더는 결과에 없음 (파일이 없으므로)' (-not (Test-Path -LiteralPath $emptyDir)) ''

# 깊이 확인
$deepFile = Join-Path $out '대상폴더\src\ui\parts\deep\App.cs'
Ok '5단계 깊이 파일 제자리에 복원' (Test-Path -LiteralPath $deepFile) ''

# ================================================================ 4) PowerShell 간편모드도 같은 폴더로
Write-Host ''
Write-Host '  -- PowerShell 간편모드 (같은 폴더) --' -ForegroundColor DarkGray
& $SIMPLE -Mode Encrypt -Path $proj *>&1 | Out-Null
$psBundle = Get-ChildItem -LiteralPath $WORK -Filter 'FCRYPT 묶음*.txt' | Select-Object -First 1
Ok 'PS 간편모드 묶음 생성' ($null -ne $psBundle) $(if ($psBundle) { '{0:N0} B' -f $psBundle.Length } else { '없음' })

if ($psBundle) {
    $psBlocks = [FileCrypt.FileCryptCore]::ExtractBlocks([System.IO.File]::ReadAllText($psBundle.FullName))
    # 폴더 입력은 아카이브 1블록 (그 안에 파일 전부)
    Ok 'PS 묶음: 아카이브 1블록' ($psBlocks.Count -eq 1) ('{0}개' -f $psBlocks.Count)
    $psInside = [FileCrypt.FileCryptCore]::DecryptAll($psBlocks[0]).Count
    Ok 'PS 아카이브 안 파일 수 일치' ($psInside -eq $realCount) ('{0} / {1}' -f $psInside, $realCount)

    $out2 = Join-Path $WORK 'restored_ps'
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
    $miss2 = 0; $mis2 = 0
    foreach ($rel in $made.Keys) {
        $p = Join-Path $out2 $rel
        if (-not (Test-Path -LiteralPath $p)) { $miss2++; continue }
        if ((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne $made[$rel]) { $mis2++ }
    }
    Ok 'PS 묶음 -> 전 파일 경로/해시 일치' (($ng2 -eq 0) -and ($miss2 -eq 0) -and ($mis2 -eq 0)) `
        ('실패 {0} / 누락 {1} / 불일치 {2}' -f $ng2, $miss2, $mis2)
}

# ================================================================ 5) 반복 루프 (같은 폴더 5회)
Write-Host ''
Write-Host '  -- 같은 폴더 5회 반복 --' -ForegroundColor DarkGray
$loopBad = 0
for ($k = 1; $k -le 5; $k++) {
    $ch = New-Object System.Collections.Generic.List[string]
    foreach ($e in $entries) {
        $c = [FileCrypt.FileCryptCore]::Encrypt($e.RelativePath, [System.IO.File]::ReadAllBytes($e.FullPath))
        $ch.Add([FileCrypt.FileCryptCore]::ToArmor($c, 100))
    }
    $bl = [FileCrypt.FileCryptCore]::ExtractBlocks(($ch -join "`r`n`r`n"))
    if ($bl.Count -ne $realCount) { $loopBad++; continue }
    foreach ($b in $bl) {
        $df = [FileCrypt.FileCryptCore]::Decrypt($b)
        $expect = $made[$df.FileName]
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $got = ([BitConverter]::ToString($sha.ComputeHash($df.Data))).Replace('-','')
        $sha.Dispose()
        if ($got -ne $expect) { $loopBad++; break }
    }
}
Ok ('5회 반복 x {0}파일 = {1}회 왕복' -f $realCount, (5 * $realCount)) ($loopBad -eq 0) ('불일치 {0}회' -f $loopBad)

Write-Host ''
Write-Host ('########## 대량 루프 테스트: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
Write-Host ('  작업 폴더: {0}' -f $WORK) -ForegroundColor DarkGray

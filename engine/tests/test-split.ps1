$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ENGINE  = Join-Path $ROOTDIR 'engine\filecrypt.ps1'
$EXE     = Join-Path $ROOTDIR 'gui\bin\Release\net48\FileCrypt.exe'
$WORK    = Join-Path $env:TEMP ('fc_split_' + (Get-Date -Format 'HHmmss'))
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

Write-Host ''
Write-Host '########## 분할 / 재조립 ##########' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $EXE)) { Write-Host '  [FAIL] gui 빌드 없음' -ForegroundColor Red; exit 1 }
$exeCopy = Join-Path $WORK 'FileCrypt.exe'
Copy-Item -LiteralPath $EXE -Destination $exeCopy -Force
[void][Reflection.Assembly]::LoadFrom($exeCopy)
$FC = [FileCrypt.FileCryptCore]

# ---------------------------------------------------------------- 표본: 압축이 안 되는 데이터 (이미지 대용)
$rand = New-Object System.Random 20260911
$blob = New-Object byte[] (400KB)
$rand.NextBytes($blob)
$blobHash = Sha $blob
$container = $FC::Encrypt('사진 모음.bin', $blob)
$whole = $FC::ToArmor($container, 100)
"  표본: {0:N0} B -> 통짜 텍스트 {1:N0} 자" -f $blob.Length, $whole.Length | Write-Host -ForegroundColor DarkGray

# ================================================================ 1) 기본 분할
$parts = $FC::ToArmorParts($container, 100000, 100)
Ok '분할 생성' ($parts.Count -ge 5) ('{0}조각' -f $parts.Count)

$max = 0
foreach ($p in $parts) { if ($p.Length -gt $max) { $max = $p.Length } }
Ok '각 조각이 지정 글자수 이하' ($max -le 100000) ('최대 {0:N0} 자' -f $max)

$sum = 0; foreach ($p in $parts) { $sum += $p.Length }
Ok '전체 크기는 통짜와 비슷 (줄지 않음)' ($sum -lt $whole.Length * 1.2) `
   ('{0:N0} 자 (통짜 대비 {1:N1}%)' -f $sum, ($sum/[double]$whole.Length*100))

# ================================================================ 2) 순서대로 합치기
function TryRestore([string[]]$chunks, [string]$label) {
    $text = ($chunks -join "`r`n`r`n")
    $blocks = $FC::ExtractBlocks($text)
    if ($blocks.Count -ne 1) { return @{ Ok = $false; Why = ('블록 {0}개' -f $blocks.Count) } }
    try {
        $df = $FC::DecryptAll($blocks[0])[0]
        $same = ((Sha $df.Data) -eq $script:blobHash) -and ($df.FileName -eq '사진 모음.bin')
        return @{ Ok = $same; Why = $(if ($same) { '원본 일치' } else { '해시 불일치' }) }
    } catch { return @{ Ok = $false; Why = $_.Exception.Message } }
}

$r = TryRestore $parts '순서대로'
Ok '순서대로 모으면 복원' $r.Ok $r.Why

# ================================================================ 3) 순서 뒤바꿈
$shuffled = $parts | Sort-Object { Get-Random }
$r = TryRestore $shuffled '뒤섞음'
Ok '순서를 뒤섞어도 복원' $r.Ok $r.Why

# 역순
$rev = @($parts); [Array]::Reverse($rev)
$r = TryRestore $rev '역순'
Ok '역순으로 붙여넣어도 복원' $r.Ok $r.Why

# ================================================================ 4) 중복 붙여넣기
$dup = @($parts) + @($parts[0], $parts[2], $parts[0])
$r = TryRestore $dup '중복'
Ok '같은 조각을 여러 번 붙여넣어도 복원' $r.Ok $r.Why

# ================================================================ 5) 조각 누락
$missing = @($parts | Where-Object { $_ -ne $parts[2] })
$text = ($missing -join "`r`n`r`n")
$blocks = $FC::ExtractBlocks($text)
Ok '조각이 빠지면 복원하지 않음' ($blocks.Count -eq 0) ('블록 {0}개' -f $blocks.Count)

$info = $FC::InspectParts($text)
$g = $info[0]
Ok '빠진 조각 번호를 정확히 알려줌' (($g.Missing.Count -eq 1) -and ($g.Missing[0] -eq 3)) `
   ('{0}/{1} 모임, 없는 것: {2}' -f $g.Have.Count, $g.Total, (($g.Missing) -join ','))

# ================================================================ 6) 서로 다른 묶음이 섞여도
$blob2 = New-Object byte[] (150KB)
$rand.NextBytes($blob2)
$hash2 = Sha $blob2
$c2 = $FC::Encrypt('두번째.bin', $blob2)
$parts2 = $FC::ToArmorParts($c2, 100000, 100)

$mixed = @()
$mixed += $parts2[0]
$mixed += $parts[1]
$mixed += $parts2[1]
$mixed += $parts[0]
foreach ($p in ($parts | Select-Object -Skip 2)) { $mixed += $p }
foreach ($p in ($parts2 | Select-Object -Skip 2)) { $mixed += $p }

$text = ($mixed -join "`r`n`r`n")
$blocks = $FC::ExtractBlocks($text)
Ok '두 묶음이 섞여 있어도 각각 복원' ($blocks.Count -eq 2) ('블록 {0}개' -f $blocks.Count)

$got = @{}
foreach ($b in $blocks) { $d = $FC::DecryptAll($b)[0]; $got[$d.FileName] = (Sha $d.Data) }
Ok '  -> 두 묶음 모두 원본과 일치' `
   (($got['사진 모음.bin'] -eq $blobHash) -and ($got['두번째.bin'] -eq $hash2)) ''

# ================================================================ 7) 붙여넣기 훼손 내성 (조각에도 적용되는가)
$messy = @()
foreach ($p in $parts) {
    $lines = $p -split "`r?`n"
    $out = @()
    foreach ($l in $lines) {
        if ($l -like '-----*') { $out += $l }
        elseif ($l.Length -ge 3) { $out += ('> ' + $l.Insert(3, [char]0x200B) + '  ') }
        else { $out += ('> ' + $l + '  ') }   # 마지막 줄은 1글자일 수 있다
        $out += ''
    }
    $messy += ("잡담 앞줄`r`n" + ($out -join "`r`n") + "`r`n확인 부탁드립니다")
}
$r = TryRestore $messy '훼손'
Ok '인용부호+제로폭문자+빈줄 섞여도 복원' $r.Ok $r.Why

# ================================================================ 8) 조각 하나만 1글자 변조 -> 거부
$broken = @($parts)
$lines = $broken[1] -split "`r?`n"
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -notlike '-----*' -and $lines[$i].Length -gt 10) {
        $ch = $lines[$i][5]
        $lines[$i] = $lines[$i].Remove(5,1).Insert(5, $(if ($ch -eq 'A') { 'B' } else { 'A' }))
        break
    }
}
$broken[1] = ($lines -join "`r`n")
$r = TryRestore $broken '변조'
Ok '조각 1글자 변조 -> 거부' (-not $r.Ok) $r.Why

# ================================================================ 9) 아카이브(폴더)도 분할 가능
$items = New-Object 'System.Collections.Generic.List[FileCrypt.ArchiveItem]'
$expect = @{}
for ($i = 1; $i -le 30; $i++) {
    $it = New-Object FileCrypt.ArchiveItem
    $it.Name = "폴더\sub$($i % 4)\f$i.bin"
    $d = New-Object byte[] (8KB)
    $rand.NextBytes($d)
    $it.Data = $d
    $expect[$it.Name] = Sha $d
    $items.Add($it)
}
$ac = $FC::EncryptArchive($items)
$aparts = $FC::ToArmorParts($ac, 120000, 100)
$ashuf = $aparts | Sort-Object { Get-Random }
$ablocks = $FC::ExtractBlocks(($ashuf -join "`r`n`r`n"))
$afiles = if ($ablocks.Count -eq 1) { $FC::DecryptAll($ablocks[0]) } else { @() }
$abad = 0
foreach ($f in $afiles) { if ($expect[$f.FileName] -ne (Sha $f.Data)) { $abad++ } }
Ok '아카이브도 분할 -> 뒤섞어 복원' (($ablocks.Count -eq 1) -and ($afiles.Count -eq 30) -and ($abad -eq 0)) `
   ('{0}조각 -> 파일 {1}개, 불일치 {2}' -f $aparts.Count, $afiles.Count, $abad)

# ================================================================ 10) 통짜와 조각이 한 텍스트에 같이 있어도
$both = ($parts -join "`r`n`r`n") + "`r`n`r`n" + $FC::ToArmor($c2, 100)
$bblocks = $FC::ExtractBlocks($both)
Ok '분할본 + 통짜본 혼합 인식' ($bblocks.Count -eq 2) ('블록 {0}개' -f $bblocks.Count)

# ================================================================ 11) PowerShell 엔진 교차 (C# 만 테스트하면 놓친다)
Write-Host ''
Write-Host '  -- PowerShell 엔진 <-> C# 교차 --' -ForegroundColor DarkGray

$src = Join-Path $WORK '표본.bin'
[System.IO.File]::WriteAllBytes($src, $blob)

# (a) PS 가 조각을 만들고 -> PS 가 되돌린다
$psOut = Join-Path $WORK 'ps.enc.txt'
$global:LASTEXITCODE = 0
& $ENGINE -Mode Encrypt -Path $src -Out $psOut -Armor -Width 100 -Split 100000 -Force -Quiet 2>$null | Out-Null
$psParts = Get-ChildItem -LiteralPath $WORK -Filter 'ps [*of*].txt'
Ok 'PS: 조각 생성' ($psParts.Count -ge 2) ('{0}조각' -f $psParts.Count)

# 뒤섞어 한 파일로 합친다 (엔진은 파일 하나를 받는다)
$shuf = $psParts | Sort-Object { Get-Random }
$mergedFile = Join-Path $WORK 'ps-merged.txt'
[System.IO.File]::WriteAllText($mergedFile, (($shuf | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`r`n"), $u8n)

$psRestore = Join-Path $WORK 'ps-restore'
$global:LASTEXITCODE = 0
& $ENGINE -Mode Decrypt -Path $mergedFile -OutDir $psRestore -Force -Quiet 2>$null | Out-Null
$rc = $LASTEXITCODE
$got = Join-Path $psRestore '표본.bin'
Ok 'PS 조각 -> PS 복원 (뒤섞은 순서)' `
   (($rc -eq 0) -and (Test-Path -LiteralPath $got) -and ((Sha ([System.IO.File]::ReadAllBytes($got))) -eq $blobHash)) ('rc=' + $rc)

# (b) PS 조각 -> C# 이 읽는다
$csBlocks = $FC::ExtractBlocks([System.IO.File]::ReadAllText($mergedFile))
$csOk = $false
if ($csBlocks.Count -eq 1) {
    $d = $FC::DecryptAll($csBlocks[0])[0]
    $csOk = ((Sha $d.Data) -eq $blobHash) -and ($d.FileName -eq '표본.bin')
}
Ok 'PS 조각 -> C# 복원' $csOk ('블록 {0}개' -f $csBlocks.Count)

# (c) C# 조각 -> PS 가 읽는다
$csParts = $FC::ToArmorParts($container, 100000, 100)
$csFile = Join-Path $WORK 'cs-parts.txt'
[System.IO.File]::WriteAllText($csFile, (($csParts | Sort-Object { Get-Random }) -join "`r`n"), $u8n)
$csRestore = Join-Path $WORK 'cs-restore'
$global:LASTEXITCODE = 0
& $ENGINE -Mode Decrypt -Path $csFile -OutDir $csRestore -Force -Quiet 2>$null | Out-Null
$rc2 = $LASTEXITCODE
$got2 = Join-Path $csRestore '사진 모음.bin'
Ok 'C# 조각 -> PS 복원' `
   (($rc2 -eq 0) -and (Test-Path -LiteralPath $got2) -and ((Sha ([System.IO.File]::ReadAllBytes($got2))) -eq $blobHash)) ('rc=' + $rc2)

# (d) 조각이 모자라면 PS 도 거부하고 무엇이 없는지 말해야 한다
$lack = Join-Path $WORK 'ps-lack.txt'
[System.IO.File]::WriteAllText($lack, (($shuf | Select-Object -Skip 1 | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`r`n"), $u8n)
# Write-Host 출력은 2>&1 로 안 잡힌다. 전 스트림을 받아야 한다.
$out = & $ENGINE -Mode Decrypt -Path $lack -OutDir (Join-Path $WORK 'ps-lack-out') -Force *>&1
$txt = ($out | Out-String)
Ok 'PS: 조각 부족 -> 거부 + 없는 번호 안내' `
   (($LASTEXITCODE -ne 0) -and ($txt -match '조각이 모자랍니다')) ''

Write-Host ''
Write-Host ('########## 분할 테스트: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ENGINE  = Join-Path $ROOTDIR 'engine\filecrypt.ps1'
$SIMPLE  = Join-Path $ROOTDIR 'engine\simple.ps1'
$EXE     = Join-Path $ROOTDIR 'gui\bin\Release\net48\FileCrypt.exe'
$WORK    = Join-Path $env:TEMP ('fc_lim_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $WORK | Out-Null
$u8n = New-Object System.Text.UTF8Encoding($false)

$n = 0; $fail = 0
function Ok([string]$name, [bool]$cond, [string]$extra) {
    $script:n++
    if ($cond) { Write-Host ('  [PASS] {0}  {1}' -f $name.PadRight(44), $extra) -ForegroundColor Green }
    else       { Write-Host ('  [FAIL] {0}  {1}' -f $name.PadRight(44), $extra) -ForegroundColor Red; $script:fail++ }
}
function Note([string]$t) { Write-Host ('         ' + $t) -ForegroundColor DarkGray }
function Sha([byte[]]$b) {
    $s = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($s.ComputeHash($b))).Replace('-','') } finally { $s.Dispose() }
}

Write-Host ''
Write-Host '########## 실사용 한계 실측 ##########' -ForegroundColor Cyan

$exeCopy = Join-Path $WORK 'FileCrypt.exe'
Copy-Item -LiteralPath $EXE -Destination $exeCopy -Force
[void][Reflection.Assembly]::LoadFrom($exeCopy)

# ================================================================ 1) 단일 파일 크기별
Write-Host ''
Write-Host '  -- 단일 파일 크기별 (압축 잘 되는 텍스트) --' -ForegroundColor DarkGray
foreach ($mb in @(1, 10, 50)) {
    $bytes = New-Object byte[] 0
    $sb = New-Object System.Text.StringBuilder
    $line = "row 0000000 : 측정 값 = 1234.5678, 상태 = 정상, 비고 = 없음`r`n"
    $need = $mb * 1MB
    while ($sb.Length * 2 -lt $need) { [void]$sb.Append($line) }
    $bytes = $u8n.GetBytes($sb.ToString())
    $src = Join-Path $WORK ("t$mb.txt")
    [System.IO.File]::WriteAllBytes($src, $bytes)
    $h = Sha $bytes

    $before = [GC]::GetTotalMemory($true)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $c = [FileCrypt.FileCryptCore]::Encrypt("t$mb.txt", $bytes)
    $armor = [FileCrypt.FileCryptCore]::ToArmor($c, 100)
    $encMs = $sw.ElapsedMilliseconds
    $peak = [GC]::GetTotalMemory($false) - $before

    $sw.Restart()
    $blocks = [FileCrypt.FileCryptCore]::ExtractBlocks($armor)
    $df = [FileCrypt.FileCryptCore]::DecryptAll($blocks[0])[0]
    $decMs = $sw.ElapsedMilliseconds

    $good = ((Sha $df.Data) -eq $h)
    Ok ("단일 {0} MB" -f $mb) $good `
       ("{0:N0}자 ({1:N1}%) · 암호 {2:N1}s · 복원 {3:N1}s · 메모리 +{4:N0} MB" -f `
        $armor.Length, ($armor.Length/[double]$bytes.Length*100), ($encMs/1000), ($decMs/1000), ($peak/1MB))
    Remove-Item -LiteralPath $src -Force
    $armor = $null; $c = $null; $df = $null; $blocks = $null; $bytes = $null; $sb = $null
    [GC]::Collect()
}

# ================================================================ 2) 압축 안 되는 대용량
Write-Host ''
Write-Host '  -- 압축 안 되는 데이터 (최악) --' -ForegroundColor DarkGray
$rb = New-Object byte[] (20MB)
$rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
$rng.GetBytes($rb); $rng.Dispose()
$h = Sha $rb
$sw = [Diagnostics.Stopwatch]::StartNew()
$c = [FileCrypt.FileCryptCore]::Encrypt('rand.bin', $rb)
$armor = [FileCrypt.FileCryptCore]::ToArmor($c, 100)
$encMs = $sw.ElapsedMilliseconds
$df = [FileCrypt.FileCryptCore]::DecryptAll([FileCrypt.FileCryptCore]::ExtractBlocks($armor)[0])[0]
Ok '랜덤 20 MB' ((Sha $df.Data) -eq $h) ("{0:N0}자 ({1:N1}%) · 암호 {2:N1}s" -f $armor.Length, ($armor.Length/20MB*100), ($encMs/1000))
Note '압축이 0% 라 Base64 만큼(약 133%) 커진다. 큰 바이너리는 이 도구가 부적합.'
$rb = $null; $armor = $null; $c = $null; $df = $null; [GC]::Collect()

# ================================================================ 3) 클립보드 한계
Write-Host ''
Write-Host '  -- 클립보드로 옮길 수 있는 크기 --' -ForegroundColor DarkGray
foreach ($kc in @(100, 1000, 5000, 20000)) {
    $t = '-----BEGIN FCRYPT MESSAGE-----' + "`r`n" + ('A' * ($kc * 1000)) + "`r`n" + '-----END FCRYPT MESSAGE-----'
    $set = $false; $back = ''
    try { Set-Clipboard -Value $t -ErrorAction Stop; $set = $true } catch { }
    if ($set) { try { $back = Get-Clipboard -Raw -ErrorAction Stop } catch { } }
    $okc = $set -and ($back.Length -ge $t.Length - 4)
    Ok ("클립보드 {0:N0}만 자" -f ($kc/10)) $okc ("{0:N0} 자 왕복" -f $t.Length)
    if (-not $okc) { break }
}

# ================================================================ 4) 긴 경로 복원
Write-Host ''
Write-Host '  -- 경로 길이 --' -ForegroundColor DarkGray
$deepName = (( '가나다라마바사아자차' ) * 3)
$rel = ($deepName + '\') * 8 + 'end.txt'
$item = New-Object FileCrypt.ArchiveItem
$item.Name = $rel
$item.Data = $u8n.GetBytes('deep')
$lst = New-Object 'System.Collections.Generic.List[FileCrypt.ArchiveItem]'
$lst.Add($item)
$deepOut = Join-Path $WORK 'deep'
New-Item -ItemType Directory -Force $deepOut | Out-Null
$thrown = $null
try {
    $files = [FileCrypt.FileCryptCore]::DecryptAll([FileCrypt.FileCryptCore]::EncryptArchive($lst))
    $dest = [FileCrypt.FileCryptCore]::ResolveNonClobbering($deepOut, $files[0].FileName)
    [System.IO.File]::WriteAllBytes($dest, $files[0].Data)
} catch { $thrown = $_.Exception.Message }
# Windows 260자 제한은 우리가 못 넘는다. 원인을 알 수 있는 메시지로 거부하면 합격.
Ok ('긴 경로 ({0}자) -> 원인 알 수 있는 거부' -f ($deepOut.Length + $rel.Length)) `
   (($null -ne $thrown) -and ($thrown -match '경로가 너무 깁니다')) `
   $(if ($thrown) { '메시지 OK' } else { '거부하지 않음' })
Note '저장 폴더를 짧은 곳(C:\복원 등)으로 잡으면 회피된다.'

# 긴 경로 한 개가 나머지 복원을 죽이지 않아야 한다
$mix = New-Object 'System.Collections.Generic.List[FileCrypt.ArchiveItem]'
$bad = New-Object FileCrypt.ArchiveItem; $bad.Name = $rel; $bad.Data = $u8n.GetBytes('deep')
$mix.Add($bad)
foreach ($nm in @('a.txt','sub\b.txt','sub\c.txt')) {
    $g = New-Object FileCrypt.ArchiveItem; $g.Name = $nm; $g.Data = $u8n.GetBytes('ok ' + $nm)
    $mix.Add($g)
}
$mixTxt = Join-Path $WORK 'mix.enc.txt'
[System.IO.File]::WriteAllText($mixTxt, [FileCrypt.FileCryptCore]::ToArmor([FileCrypt.FileCryptCore]::EncryptArchive($mix), 100), $u8n)
$mixOut = Join-Path $WORK 'mixout'
New-Item -ItemType Directory -Force $mixOut | Out-Null
$global:LASTEXITCODE = 0
& $ENGINE -Mode Decrypt -Path $mixTxt -OutDir $mixOut -Force -Quiet 2>$null | Out-Null
$rc = $LASTEXITCODE
$got = (Get-ChildItem -LiteralPath $mixOut -File -Recurse).Count
Ok '긴 경로 1개 + 정상 3개 -> 정상 3개는 복원' ($got -eq 3) ('{0}개 복원, rc={1} (5=일부 건너뜀)' -f $got, $rc)

# ================================================================ 5) 잠긴 파일 / 읽기 전용
Write-Host ''
Write-Host '  -- 접근 불가 파일 --' -ForegroundColor DarkGray
$lockDir = Join-Path $WORK 'lock'
New-Item -ItemType Directory -Force $lockDir | Out-Null
[System.IO.File]::WriteAllText((Join-Path $lockDir 'ok.txt'), 'fine', $u8n)
$lockedPath = Join-Path $lockDir 'locked.bin'
[System.IO.File]::WriteAllText($lockedPath, 'locked', $u8n)
$fs = [System.IO.File]::Open($lockedPath, 'Open', 'Read', 'None')
try {
    $entries = [FileCrypt.FileCryptCore]::EnumerateFolder($lockDir)
    Ok '잠긴 파일도 목록에는 잡힘' ($entries.Count -eq 2) ('{0}개' -f $entries.Count)
    $readFail = 0
    foreach ($e in $entries) { try { [void][System.IO.File]::ReadAllBytes($e.FullPath) } catch { $readFail++ } }
    Ok '잠긴 파일은 읽기에서 예외 (건너뛰기 대상)' ($readFail -eq 1) ('실패 {0}개' -f $readFail)
    Note 'GUI 는 실패 파일을 건너뛰고 나머지를 처리한 뒤 개수를 알려준다.'
} finally { $fs.Dispose() }

$ro = Join-Path $WORK 'readonly.txt'
[System.IO.File]::WriteAllText($ro, 'ro', $u8n)
Set-ItemProperty -LiteralPath $ro -Name IsReadOnly -Value $true
$c = [FileCrypt.FileCryptCore]::Encrypt('readonly.txt', [System.IO.File]::ReadAllBytes($ro))
Ok '읽기 전용 파일 처리' ($c.Length -gt 0) ''
Set-ItemProperty -LiteralPath $ro -Name IsReadOnly -Value $false

# ================================================================ 6) 폴더 규모별 (아카이브)
Write-Host ''
Write-Host '  -- 폴더 규모별 (아카이브 모드) --' -ForegroundColor DarkGray
foreach ($cnt in @(100, 500, 2000)) {
    $d = Join-Path $WORK ("folder$cnt")
    New-Item -ItemType Directory -Force $d | Out-Null
    for ($i = 1; $i -le $cnt; $i++) {
        $sub = Join-Path $d ("g" + ($i % 20))
        if (-not (Test-Path -LiteralPath $sub)) { New-Item -ItemType Directory -Force $sub | Out-Null }
        [System.IO.File]::WriteAllText((Join-Path $sub "f$i.cs"),
            "namespace N$i { class C$i { public int Id; public string Name; } }`r`n", $u8n)
    }
    $srcBytes = (Get-ChildItem -LiteralPath $d -File -Recurse | Measure-Object Length -Sum).Sum

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $entries = [FileCrypt.FileCryptCore]::EnumerateFolder($d)
    $items = New-Object 'System.Collections.Generic.List[FileCrypt.ArchiveItem]'
    foreach ($e in $entries) {
        $it = New-Object FileCrypt.ArchiveItem
        $it.Name = $e.RelativePath; $it.Data = [System.IO.File]::ReadAllBytes($e.FullPath)
        $items.Add($it)
    }
    $armor = [FileCrypt.FileCryptCore]::ToArmor([FileCrypt.FileCryptCore]::EncryptArchive($items), 100)
    $encMs = $sw.ElapsedMilliseconds

    $sw.Restart()
    $got = [FileCrypt.FileCryptCore]::DecryptAll([FileCrypt.FileCryptCore]::ExtractBlocks($armor)[0])
    $decMs = $sw.ElapsedMilliseconds

    Ok ("폴더 {0,4}개 파일" -f $cnt) ($got.Count -eq $cnt) `
       ("{0:N0} B -> {1:N0}자 ({2:N1}%) · 암호 {3:N2}s · 복원 {4:N2}s" -f `
        $srcBytes, $armor.Length, ($armor.Length/[double]$srcBytes*100), ($encMs/1000), ($decMs/1000))
    $armor = $null; $items = $null; $got = $null; [GC]::Collect()
}

# ================================================================ 7) 간편모드(.cmd) 도 아카이브를 쓰는가
Write-Host ''
Write-Host '  -- 암호화.cmd 경로가 GUI 와 같은 결과를 내는가 --' -ForegroundColor DarkGray
$pd = Join-Path $WORK 'cmdfolder'
New-Item -ItemType Directory -Force (Join-Path $pd 'sub') | Out-Null
for ($i = 1; $i -le 40; $i++) {
    [System.IO.File]::WriteAllText((Join-Path $pd ("sub\a$i.cs")), "class C$i { public int X; }`r`n", $u8n)
}
& $SIMPLE -Mode Encrypt -Path $pd *>&1 | Out-Null
$made = Get-ChildItem -LiteralPath $WORK -Filter 'FCRYPT 묶음*.txt' | Sort-Object LastWriteTime | Select-Object -Last 1
Ok 'PS 간편모드가 폴더를 아카이브로 처리' ($null -ne $made) $(if ($made) { $made.Name } else { '없음' })
if ($made) {
    $bl = [FileCrypt.FileCryptCore]::ExtractBlocks([System.IO.File]::ReadAllText($made.FullName))
    Ok '  -> 블록 1개 (아카이브)' ($bl.Count -eq 1) ('{0}개' -f $bl.Count)
    $fs2 = [FileCrypt.FileCryptCore]::DecryptAll($bl[0])
    Ok '  -> 파일 40개 전부 들어있음' ($fs2.Count -eq 40) ('{0}개' -f $fs2.Count)
}

Write-Host ''
Write-Host ('########## 한계 실측: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
Write-Host ('  작업 폴더: {0}' -f $WORK) -ForegroundColor DarkGray

. (Join-Path $PSScriptRoot '_common.ps1')

# 실사용 한계 실측: 파일 크기, 압축 안 되는 데이터, 클립보드, 경로 길이, 접근 불가 파일, 폴더 규모.
# 오래 걸린다 (run-all -Quick 은 이 스위트를 건너뛴다).

Start-Test -Tag lim -Title '실사용 한계 실측' -Pad 44
Import-FileCrypt

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
    # 클립보드는 이 PC 의 다른 프로그램과 같이 쓴다. 그 사이 누가 복사하거나 잡고 있으면
    # 한 번 어긋날 수 있어 3번까지 다시 해 본다(크기 한계라면 3번 다 실패한다).
    $okc = $false; $back = ''; $tries = 0
    while (-not $okc -and $tries -lt 3) {
        $tries++
        $set = $false; $back = ''
        try { Set-Clipboard -Value $t -ErrorAction Stop; $set = $true } catch { }
        if ($set) { try { $back = Get-Clipboard -Raw -ErrorAction Stop } catch { } }
        $okc = $set -and ($null -ne $back) -and ($back.Length -ge $t.Length - 4)
        if (-not $okc) { Start-Sleep -Milliseconds 500 }
    }
    Ok ("클립보드 {0:N0}만 자" -f ($kc/10)) $okc `
       ("{0:N0} 자 왕복{1}" -f $t.Length, $(if ($okc -and $tries -eq 1) { '' } elseif ($okc) { " ($tries 번째)" } else { " (받은 {0:N0}자)" -f $(if ($back) { $back.Length } else { 0 }) }))
    if (-not $okc) { break }
}

# GUI 는 PowerShell 클립보드가 아니라 WPF(System.Windows.Clipboard)를 쓴다. 같은 크기를 그쪽으로도 본다.
# WPF 클립보드는 STA 스레드에서만 된다. 이 프로세스가 STA 가 아니면 -STA 자식 프로세스에서 돌린다.
$wpfCheck = {
    param([int]$Chars)
    try {
        Add-Type -AssemblyName PresentationCore
        # base64 처럼 보이는 100자 줄 + CRLF 로 채우고, 길이를 정확히 맞춘다
        $abc  = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'
        $abc2 = $abc * 3   # 64가지 시작 위치 + 100자
        $head = "-----BEGIN FCRYPT MESSAGE-----`r`n"
        $tail = "`r`n-----END FCRYPT MESSAGE-----"
        $sb = New-Object System.Text.StringBuilder ($Chars + 256)
        [void]$sb.Append($head)
        $k = 0
        while ($sb.Length + 102 + $tail.Length -le $Chars) { [void]$sb.Append($abc2, ($k++) % 64, 100).Append("`r`n") }
        $sb.Append([char]'A', $Chars - $tail.Length - $sb.Length) | Out-Null
        [void]$sb.Append($tail)
        $text = $sb.ToString(); $sb = $null
        if ($text.Length -ne $Chars) { Write-Output ('ERR 표본 길이 {0}' -f $text.Length); return }

        $set = $false; $err = ''
        for ($i = 0; $i -lt 5 -and -not $set; $i++) {   # 다른 프로그램이 클립보드를 잡고 있으면 잠깐 기다린다
            try { [System.Windows.Clipboard]::SetText($text); $set = $true } catch { $err = $_.Exception.Message; Start-Sleep -Milliseconds 300 }
        }
        if (-not $set) { Write-Output ('ERR SetText: ' + $err); return }
        $back = $null
        for ($i = 0; $i -lt 5 -and $null -eq $back; $i++) {
            try { $back = [System.Windows.Clipboard]::GetText() } catch { $err = $_.Exception.Message; Start-Sleep -Milliseconds 300 }
        }
        if ($null -eq $back) { Write-Output ('ERR GetText: ' + $err); return }
        $same = [string]::Equals($text, $back, [StringComparison]::Ordinal)
        try { [System.Windows.Clipboard]::Clear() } catch { }   # 20MB 를 클립보드에 남겨 두지 않는다
        Write-Output ('{0} {1} {2}' -f $(if ($same) { 'OK' } else { 'MISMATCH' }), $text.Length, $back.Length)
    } catch { Write-Output ('ERR ' + $_.Exception.Message) }
}
$wpfChars = 20000000
$apt = [Threading.Thread]::CurrentThread.ApartmentState
if ($apt -eq [Threading.ApartmentState]::STA) {
    $wpfRun = { (& $wpfCheck $wpfChars | Out-String).Trim() }
    $where = '이 프로세스(STA)'
} else {
    $cmd = '& {' + $wpfCheck.ToString() + '} -Chars ' + $wpfChars
    $enc = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($cmd))
    $wpfRun = { (& powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -EncodedCommand $enc 2>&1 | Out-String).Trim() }
    $where = '-STA 자식 프로세스 (현재 ' + $apt + ')'
}
# 다른 프로그램과 부딪혀 어긋난 경우를 걸러 내려고 3번까지 다시 해 본다
$wpfTry = 0; $wpfRes = ''
while ($wpfRes -notlike 'OK *' -and $wpfTry -lt 3) { $wpfTry++; $wpfRes = & $wpfRun }
if ($wpfTry -gt 1) { $where += (' / {0}번째' -f $wpfTry) }
Ok ('WPF 클립보드 {0:N0}만 자 정확히 왕복' -f ($wpfChars / 10000)) ($wpfRes -like 'OK *') ('{0} / {1}' -f $wpfRes, $where)

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

Complete-Test

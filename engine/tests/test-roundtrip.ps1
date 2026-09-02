$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$PS1  = Join-Path (Split-Path $PSScriptRoot -Parent) 'filecrypt.ps1'
$ROOT = Join-Path $env:TEMP 'fc_tests'
if (Test-Path $ROOT) { Remove-Item $ROOT -Recurse -Force }
New-Item -ItemType Directory -Force $ROOT | Out-Null
$SRC = Join-Path $ROOT 'src'; New-Item -ItemType Directory -Force $SRC | Out-Null
$WRK = Join-Path $ROOT 'wrk'; New-Item -ItemType Directory -Force $WRK | Out-Null

function W([string]$name, [byte[]]$bytes) {
    $p = Join-Path $SRC $name
    [System.IO.File]::WriteAllBytes($p, $bytes)
    return $p
}
function WT([string]$name, [string]$text, $enc) {
    $p = Join-Path $SRC $name
    [System.IO.File]::WriteAllText($p, $text, $enc)
    return $p
}

$u8n   = New-Object System.Text.UTF8Encoding($false)
$u8b   = New-Object System.Text.UTF8Encoding($true)
$u16   = New-Object System.Text.UnicodeEncoding($false, $true)
$cp949 = [System.Text.Encoding]::GetEncoding(949)

$cases = @()
$cases += ,@('01 빈 파일 (0 byte)',           (W 'empty.bin' (New-Object byte[] 0)))
$cases += ,@('02 1 byte',                     (W 'one.bin' ([byte[]](0x41))))
$cases += ,@('03 AES블록-1 (15B)',            (W 'b15.bin' ([byte[]](1..15))))
$cases += ,@('04 AES블록 정확히 (16B)',       (W 'b16.bin' ([byte[]](1..16))))
$cases += ,@('05 AES블록+1 (17B)',            (W 'b17.bin' ([byte[]](1..17))))
$cases += ,@('06 0x00~0xFF 전 바이트값',      (W 'all256.bin' ([byte[]](0..255))))
$cases += ,@('07 NUL/0xFF 반복 10KB',         (W 'nulls.bin' ([byte[]]((@(0)*5120) + (@(255)*5120)))))
$cases += ,@('08 ASCII 텍스트',               (WT 'ascii.txt' ("hello world`n" * 500) $u8n))
$cases += ,@('09 한글 UTF-8 BOM없음',         (WT 'ko_u8n.txt' ("가나다라마 한글 테스트 데이터`n" * 500) $u8n))
$cases += ,@('10 한글 UTF-8 BOM있음',         (WT 'ko_u8b.txt' ("가나다라마 한글 테스트 데이터`n" * 500) $u8b))
$cases += ,@('11 한글 CP949 (ANSI)',          (WT 'ko_949.txt' ("가나다라마 한글 테스트 데이터`n" * 500) $cp949))
$cases += ,@('12 한글 UTF-16LE BOM',          (WT 'ko_u16.txt' ("가나다라마 한글 테스트 데이터`n" * 500) $u16))
$cases += ,@('13 CRLF 개행',                  (WT 'crlf.txt' ("line-A`r`n" * 300) $u8n))
$cases += ,@('14 LF 개행',                    (WT 'lf.txt'   ("line-A`n"   * 300) $u8n))
$cases += ,@('15 끝 개행 없음',               (WT 'noeol.txt' "abc`ndef`nno-trailing-newline" $u8n))
$cases += ,@('16 공백/탭만',                  (WT 'ws.txt' "   `t`t   `r`n   " $u8n))
$cases += ,@('17 이모지/서로게이트',          (WT 'emoji.txt' ("테스트 ok`n" * 200) $u8n))

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<Records name="Sample-001" version="1.0">')
for ($i = 1; $i -le 799; $i++) {
    [void]$sb.AppendLine("  <Item id=`"E$i`" type=`"Sample`" group=`"A`">")
    [void]$sb.AppendLine("    <Position x=`"$($i*137.25)`" y=`"$($i*88.5)`" z=`"0.0`" />")
    [void]$sb.AppendLine("    <Value range=`"12000`" angle=`"120`" ratio=`"0.87`" />")
    [void]$sb.AppendLine('  </Item>')
}
[void]$sb.AppendLine('</Records>')
$cases += ,@('18 XML 3199줄',                 (WT 'scenario.xml' $sb.ToString() $u8n))

$json = '{"runs":[' + ((1..500 | ForEach-Object { "{`"id`":$_,`"score`":$($_*1.5),`"name`":`"항목$_`"}" }) -join ',') + ']}'
$cases += ,@('19 JSON 한글포함',              (WT 'data.json' $json $u8n))

$csv = "id,name,value`r`n" + ((1..2000 | ForEach-Object { "$_,항목$_,$($_*3.14159)" }) -join "`r`n")
$cases += ,@('20 CSV 2000행',                 (WT 'data.csv' $csv $u8n))

$png = [byte[]]@(0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
                 0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,0x08,0x06,0x00,0x00,0x00,0x1F,0x15,0xC4,
                 0x89,0x00,0x00,0x00,0x0A,0x49,0x44,0x41,0x54,0x78,0x9C,0x63,0x00,0x01,0x00,0x00,
                 0x05,0x00,0x01,0x0D,0x0A,0x2D,0xB4,0x00,0x00,0x00,0x00,0x49,0x45,0x4E,0x44,0xAE,
                 0x42,0x60,0x82)
$cases += ,@('21 PNG 바이너리',               (W 'tiny.png' $png))

$zipSrc = Join-Path $WRK 'zipsrc'; New-Item -ItemType Directory -Force $zipSrc | Out-Null
Copy-Item (Join-Path $SRC 'scenario.xml') $zipSrc
$zipPath = Join-Path $SRC 'archive.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($zipSrc, $zipPath)
$cases += ,@('22 ZIP 이미압축됨',             $zipPath)

$rb = New-Object byte[] 1048576
$rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
$rng.GetBytes($rb); $rng.Dispose()
$cases += ,@('23 랜덤 1MB 압축불가',          (W 'rand1mb.bin' $rb))

$big = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt 120000; $i++) { [void]$big.AppendLine("row $i : 측정 값 = $($i * 7 % 9973)") }
$cases += ,@('24 대용량 텍스트 ~8MB',         (WT 'big.txt' $big.ToString() $u8n))

$cases += ,@('25 한글+공백 파일명',           (WT '한글 파일 이름 [테스트].txt' "한글 파일명 테스트`n" $u8n))
$cases += ,@('26 확장자 없는 파일',           (WT 'noextension' "no extension here`n" $u8n))
$cases += ,@('27 점 여러개 파일명',           (WT 'a.b.c.d.txt' "multi dot`n" $u8n))
$cases += ,@('28 이미 .enc 인 파일',          (WT 'already.enc' "not really encrypted`n" $u8n))

$configs = @(
    @{ Name = 'A 바이너리/압축ON';  P = @{};                             Ext = '.enc' },
    @{ Name = 'B 바이너리/압축OFF'; P = @{ Compress = 'Off' };           Ext = '.enc' },
    @{ Name = 'C Armor/줄폭76';     P = @{ Armor = $true; Width = 76 };  Ext = '.enc.txt' },
    @{ Name = 'D Armor/한줄';       P = @{ Armor = $true; Width = 0 };   Ext = '.enc.txt' },
    @{ Name = 'E 반복1000회';       P = @{ Iterations = 1000 };          Ext = '.enc' }
)

$PW = 'P@ss 한글 암호 #1!'
$results = New-Object System.Collections.Generic.List[object]
$idx = 0

function Invoke-FC([hashtable]$P) {
    $global:LASTEXITCODE = 0
    $out = & $PS1 @P *>&1
    return @{ Rc = $LASTEXITCODE; Out = (($out | Out-String).Trim()) }
}

function Run-Case($caseName, $srcPath, $cfg) {
    $script:idx++
    $tag  = 'c{0:d3}' -f $script:idx
    $encP = Join-Path $WRK ($tag + $cfg.Ext)
    $decP = Join-Path $WRK ($tag + '.out')

    $ep = @{ Mode='Encrypt'; Path=$srcPath; Out=$encP; Password=$PW; Force=$true }
    foreach ($k in $cfg.P.Keys) { $ep[$k] = $cfg.P[$k] }
    $e = Invoke-FC $ep
    if ($e.Rc -ne 0) {
        return [pscustomobject]@{ Case=$caseName; Config=$cfg.Name; Result='ENC FAIL'; Detail=$e.Out; Ratio='' }
    }

    $d = Invoke-FC @{ Mode='Decrypt'; Path=$encP; Out=$decP; Password=$PW; Force=$true }
    if ($d.Rc -ne 0) {
        return [pscustomobject]@{ Case=$caseName; Config=$cfg.Name; Result='DEC FAIL'; Detail=$d.Out; Ratio='' }
    }

    $ob = [System.IO.File]::ReadAllBytes($srcPath)
    $nb = [System.IO.File]::ReadAllBytes($decP)
    $same = ($ob.Length -eq $nb.Length)
    if ($same) {
        for ($i = 0; $i -lt $ob.Length; $i++) { if ($ob[$i] -ne $nb[$i]) { $same = $false; break } }
    }
    $encLen = (Get-Item $encP).Length
    $ratio = if ($ob.Length -gt 0) { '{0:N1}%' -f (($encLen / [double]$ob.Length) * 100) } else { '-' }

    [pscustomobject]@{
        Case   = $caseName
        Config = $cfg.Name
        Result = $(if ($same) { 'PASS' } else { 'MISMATCH' })
        Detail = ('원본 {0}B / 복원 {1}B / 암호문 {2}B' -f $ob.Length, $nb.Length, $encLen)
        Ratio  = $ratio
    }
}

Write-Host ''
Write-Host '################ 1. 라운드트립 (28 케이스 x 5 구성 = 140건) ################' -ForegroundColor Cyan
foreach ($c in $cases) {
    $line = '  {0,-26}' -f $c[0]
    $marks = @()
    foreach ($cfg in $configs) {
        $r = Run-Case $c[0] $c[1] $cfg
        $results.Add($r)
        $marks += $(if ($r.Result -eq 'PASS') { 'O' } else { 'X' })
    }
    $srcLen = (Get-Item $c[1]).Length
    $first = $results | Where-Object { $_.Case -eq $c[0] -and $_.Config -eq 'A 바이너리/압축ON' } | Select-Object -First 1
    $allOk = ($marks -notcontains 'X')
    Write-Host ('{0}  A:{1} B:{2} C:{3} D:{4} E:{5}   원본 {6,10:N0}B   압축률(A) {7}' -f `
        $line, $marks[0], $marks[1], $marks[2], $marks[3], $marks[4], $srcLen, $first.Ratio) `
        -ForegroundColor $(if ($allOk) { 'Green' } else { 'Red' })
}

$pass = ($results | Where-Object { $_.Result -eq 'PASS' }).Count
$fail = ($results | Where-Object { $_.Result -ne 'PASS' }).Count
Write-Host ''
Write-Host ('  >>> 라운드트립 결과: 총 {0}건 / PASS {1} / FAIL {2}' -f $results.Count, $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
if ($fail -gt 0) {
    $results | Where-Object { $_.Result -ne 'PASS' } | ForEach-Object {
        Write-Host ('  FAIL: {0} / {1} / {2}' -f $_.Case, $_.Config, $_.Result) -ForegroundColor Red
        Write-Host ('        {0}' -f ($_.Detail -replace "`r?`n", ' | ')) -ForegroundColor DarkRed
    }
}
$results | Export-Csv (Join-Path $ROOT 'roundtrip.csv') -NoTypeInformation -Encoding UTF8

# ================================================================ 2. 부정 테스트
Write-Host ''
Write-Host '################ 2. 부정/보안 테스트 ################' -ForegroundColor Cyan
$neg = New-Object System.Collections.Generic.List[object]
function Neg([string]$name, [int]$expectRc, [hashtable]$P, [scriptblock]$prep) {
    if ($prep) { & $prep }
    $r = Invoke-FC $P
    $ok = ($r.Rc -eq $expectRc)
    $neg.Add([pscustomobject]@{ Test=$name; Expect=$expectRc; Actual=$r.Rc; Result=$(if($ok){'PASS'}else{'FAIL'}) })
    Write-Host ('  [{0}] {1,-42} 기대 rc={2} / 실제 rc={3}' -f $(if($ok){'PASS'}else{'FAIL'}), $name, $expectRc, $r.Rc) -ForegroundColor $(if($ok){'Green'}else{'Red'})
}

$xml = Join-Path $SRC 'scenario.xml'
$n1  = Join-Path $WRK 'neg1.enc'
Invoke-FC @{ Mode='Encrypt'; Path=$xml; Out=$n1; Password=$PW; Force=$true } | Out-Null

Neg '틀린 암호' 4 @{ Mode='Decrypt'; Path=$n1; Out=(Join-Path $WRK 'n1.out'); Password='wrong!'; Force=$true } $null

$n2 = Join-Path $WRK 'neg2.enc'
Neg '암호문 1비트 변조' 4 @{ Mode='Decrypt'; Path=$n2; Out=(Join-Path $WRK 'n2.out'); Password=$PW; Force=$true } {
    Copy-Item $n1 $n2 -Force
    $b = [System.IO.File]::ReadAllBytes($n2); $b[200] = $b[200] -bxor 1; [System.IO.File]::WriteAllBytes($n2, $b)
}

$n3 = Join-Path $WRK 'neg3.enc'
Neg '헤더 flags 변조 (압축비트 끄기)' 4 @{ Mode='Decrypt'; Path=$n3; Out=(Join-Path $WRK 'n3.out'); Password=$PW; Force=$true } {
    Copy-Item $n1 $n3 -Force
    $b = [System.IO.File]::ReadAllBytes($n3); $b[9] = $b[9] -band 0xFE; [System.IO.File]::WriteAllBytes($n3, $b)
}

$n4 = Join-Path $WRK 'neg4.enc'
Neg 'IV 변조' 4 @{ Mode='Decrypt'; Path=$n4; Out=(Join-Path $WRK 'n4.out'); Password=$PW; Force=$true } {
    Copy-Item $n1 $n4 -Force
    $b = [System.IO.File]::ReadAllBytes($n4); $b[28] = $b[28] -bxor 0xFF; [System.IO.File]::WriteAllBytes($n4, $b)
}

$n5 = Join-Path $WRK 'neg5.enc'
Neg '원본 SHA256 필드 변조' 4 @{ Mode='Decrypt'; Path=$n5; Out=(Join-Path $WRK 'n5.out'); Password=$PW; Force=$true } {
    Copy-Item $n1 $n5 -Force
    $b = [System.IO.File]::ReadAllBytes($n5); $b[48] = $b[48] -bxor 0xFF; [System.IO.File]::WriteAllBytes($n5, $b)
}

$n6 = Join-Path $WRK 'neg6.enc'
Neg '파일 뒤쪽 잘림 (truncate)' 4 @{ Mode='Decrypt'; Path=$n6; Out=(Join-Path $WRK 'n6.out'); Password=$PW; Force=$true } {
    $b = [System.IO.File]::ReadAllBytes($n1)
    $t = New-Object byte[] ($b.Length - 32); [Array]::Copy($b, $t, $t.Length)
    [System.IO.File]::WriteAllBytes($n6, $t)
}

$n7 = Join-Path $WRK 'neg7.enc'
Neg '헤더보다 짧은 파일' 1 @{ Mode='Decrypt'; Path=$n7; Out=(Join-Path $WRK 'n7.out'); Password=$PW; Force=$true } {
    [System.IO.File]::WriteAllBytes($n7, ([byte[]](1..50)))
}

Neg 'FileCrypt 컨테이너 아님 (원본 XML)' 1 @{ Mode='Decrypt'; Path=$xml; Out=(Join-Path $WRK 'n8.out'); Password=$PW; Force=$true } $null

$n9 = Join-Path $WRK 'neg9.enc.txt'
Neg 'Armor Base64 1글자 변조' 4 @{ Mode='Decrypt'; Path=$n9; Out=(Join-Path $WRK 'n9.out'); Password=$PW; Force=$true } {
    Invoke-FC @{ Mode='Encrypt'; Path=$xml; Out=$n9; Password=$PW; Armor=$true; Width=76; Force=$true } | Out-Null
    $L = [System.IO.File]::ReadAllLines($n9)
    $ch = $L[5][10]
    $rep = if ($ch -eq 'A') { 'B' } else { 'A' }
    $L[5] = $L[5].Remove(10,1).Insert(10, $rep)
    [System.IO.File]::WriteAllLines($n9, $L)
}

Neg '존재하지 않는 파일' 1 @{ Mode='Encrypt'; Path=(Join-Path $WRK 'nope.xyz'); Out=(Join-Path $WRK 'n10.enc'); Password=$PW; Force=$true } $null

# 암호 확인 불일치는 대화형이라 제외. 유니코드/긴 암호 라운드트립:
function PwRoundTrip([string]$name, [string]$pw) {
    $o = Join-Path $WRK ('pw_' + [Math]::Abs($name.GetHashCode()) + '.enc')
    $d = Join-Path $WRK ('pw_' + [Math]::Abs($name.GetHashCode()) + '.out')
    $e = Invoke-FC @{ Mode='Encrypt'; Path=$xml; Out=$o; Password=$pw; Force=$true }
    $r = Invoke-FC @{ Mode='Decrypt'; Path=$o; Out=$d; Password=$pw; Force=$true }
    $ok = ($e.Rc -eq 0 -and $r.Rc -eq 0 -and (Get-FileHash $xml -Algorithm SHA256).Hash -eq (Get-FileHash $d -Algorithm SHA256).Hash)
    $neg.Add([pscustomobject]@{ Test=$name; Expect=0; Actual=$(if($ok){0}else{-1}); Result=$(if($ok){'PASS'}else{'FAIL'}) })
    Write-Host ('  [{0}] {1,-42} 라운드트립 해시 일치' -f $(if($ok){'PASS'}else{'FAIL'}), $name) -ForegroundColor $(if($ok){'Green'}else{'Red'})
}
PwRoundTrip '암호: 한글+공백+특수문자' '테스트 암호 #1! 2026'
PwRoundTrip '암호: 1글자' 'a'
PwRoundTrip '암호: 512자' ('x' * 512)
PwRoundTrip '암호: 이모지 포함' 'pw-테스트-ok-123'

$np = ($neg | Where-Object { $_.Result -eq 'PASS' }).Count
$nf = ($neg | Where-Object { $_.Result -ne 'PASS' }).Count
Write-Host ''
Write-Host ('  >>> 부정/보안 결과: 총 {0}건 / PASS {1} / FAIL {2}' -f $neg.Count, $np, $nf) -ForegroundColor $(if ($nf -eq 0) { 'Green' } else { 'Red' })

# ================================================================ 3. 반복 안정성
Write-Host ''
Write-Host '################ 3. 반복 안정성 (같은 파일 30회 연속) ################' -ForegroundColor Cyan
$srcHash = (Get-FileHash $xml -Algorithm SHA256).Hash
$bad = 0
for ($i = 1; $i -le 30; $i++) {
    $o = Join-Path $WRK "rep.enc"; $d = Join-Path $WRK "rep.out"
    Invoke-FC @{ Mode='Encrypt'; Path=$xml; Out=$o; Password=$PW; Force=$true } | Out-Null
    Invoke-FC @{ Mode='Decrypt'; Path=$o;   Out=$d; Password=$PW; Force=$true } | Out-Null
    if ((Get-FileHash $d -Algorithm SHA256).Hash -ne $srcHash) { $bad++ }
}
Write-Host ('  30회 반복 중 해시 불일치: {0}건' -f $bad) -ForegroundColor $(if ($bad -eq 0) { 'Green' } else { 'Red' })

# 매번 다른 암호문인지 (salt/IV 랜덤성)
$h = @{}
for ($i = 1; $i -le 10; $i++) {
    $o = Join-Path $WRK "rnd$i.enc"
    Invoke-FC @{ Mode='Encrypt'; Path=$xml; Out=$o; Password=$PW; Force=$true } | Out-Null
    $h[(Get-FileHash $o -Algorithm SHA256).Hash] = 1
}
Write-Host ('  동일 입력/암호 10회 암호화 -> 서로 다른 암호문: {0}/10' -f $h.Count) -ForegroundColor $(if ($h.Count -eq 10) { 'Green' } else { 'Red' })

# ================================================================ 4. 다른 세션(별도 프로세스)에서 복호화
Write-Host ''
Write-Host '################ 4. 별도 프로세스에서 복호화 (다른 환경 재현) ################' -ForegroundColor Cyan
$xp = Join-Path $WRK 'xfer.enc.txt'
Invoke-FC @{ Mode='Encrypt'; Path=$xml; Out=$xp; Password=$PW; Armor=$true; Width=140; Force=$true } | Out-Null
$xo = Join-Path $WRK 'xfer.out'
$null = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PS1 -Mode Decrypt -Path $xp -Out $xo -Password $PW -Force
$rc = $LASTEXITCODE
$ok = ($rc -eq 0 -and (Get-FileHash $xo -Algorithm SHA256).Hash -eq $srcHash)
Write-Host ('  [{0}] 새 powershell.exe 프로세스에서 Armor 파일 복호화 (rc={1})' -f $(if($ok){'PASS'}else{'FAIL'}), $rc) -ForegroundColor $(if($ok){'Green'}else{'Red'})

Write-Host ''
Write-Host '################ 최종 ################' -ForegroundColor Cyan
$total = $results.Count + $neg.Count + 32 + 1
$totalFail = $fail + $nf + $bad + $(if ($h.Count -eq 10) { 0 } else { 1 }) + $(if ($ok) { 0 } else { 1 })
Write-Host ('  전체 {0}건 / 실패 {1}건' -f $total, $totalFail) -ForegroundColor $(if ($totalFail -eq 0) { 'Green' } else { 'Red' })
Write-Host ('  작업 폴더: {0}' -f $ROOT) -ForegroundColor DarkGray

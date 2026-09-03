$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ENGINE  = Join-Path $ROOTDIR 'engine\filecrypt.ps1'
$EXE     = Join-Path $ROOTDIR 'gui\bin\Release\net48\FileCrypt.exe'
$KEY     = 'FileCrypt/default/v2/no-password'
$WORK    = Join-Path $env:TEMP ('fc_compat_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $WORK | Out-Null

$n = 0; $fail = 0
function Ok([string]$name, [bool]$cond, [string]$extra) {
    $script:n++
    if ($cond) { Write-Host ('  [PASS] {0}  {1}' -f $name.PadRight(48), $extra) -ForegroundColor Green }
    else       { Write-Host ('  [FAIL] {0}  {1}' -f $name.PadRight(48), $extra) -ForegroundColor Red; $script:fail++ }
}
function BytesEqual([byte[]]$a, [byte[]]$b) {
    if ($a.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { return $false } }
    return $true
}

Write-Host ''
Write-Host '########## GUI(C#) <-> 엔진(PowerShell) 포맷 호환 ##########' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $EXE)) {
    Write-Host ('  [FAIL] 빌드 산출물이 없습니다: {0}' -f $EXE) -ForegroundColor Red
    Write-Host '         gui 폴더에서 dotnet build -c Release 를 먼저 실행하세요.' -ForegroundColor DarkGray
    exit 1
}

# 빌드를 막지 않도록 복사본을 로드한다.
$exeCopy = Join-Path $WORK 'FileCrypt.exe'
Copy-Item -LiteralPath $EXE -Destination $exeCopy -Force
[void][Reflection.Assembly]::LoadFrom($exeCopy)
# LoadFrom 으로 올린 어셈블리는 [Type]::GetType 의 단순 이름 조회로는 안 잡힌다.
$typeOk = $false
try { $null = [FileCrypt.FileCryptCore]; $typeOk = $true } catch { }
Ok 'GUI 어셈블리 로드' $typeOk ''

$u8n = New-Object System.Text.UTF8Encoding($false)

# ---------------------------------------------------------------- 표본
$samples = @(
    @{ Name = 'sample.xml';        Bytes = $u8n.GetBytes("<r>`r`n" + ("  <row>데이터 행</row>`r`n" * 500) + "</r>") },
    @{ Name = '한글 보고서 [v2].txt'; Bytes = $u8n.GetBytes("한글 본문 줄`r`n" * 300) },
    @{ Name = 'bytes.bin';         Bytes = [byte[]](0..255) },
    @{ Name = 'empty.dat';         Bytes = (New-Object byte[] 0) },
    @{ Name = 'one.bin';           Bytes = [byte[]](0x41) }
)

# ================================================================ 1) PS 암호화 -> C# 복호화
Write-Host ''
Write-Host '  -- PowerShell 로 만든 것을 GUI 가 열 수 있는가 --' -ForegroundColor DarkGray
foreach ($s in $samples) {
    $src = Join-Path $WORK $s.Name
    [System.IO.File]::WriteAllBytes($src, $s.Bytes)
    $enc = Join-Path $WORK ($s.Name + '.enc.txt')

    $global:LASTEXITCODE = 0
    & $ENGINE -Mode Encrypt -Path $src -Out $enc -Armor -Width 100 -Force -Quiet 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { Ok ('PS→C# : ' + $s.Name) $false 'PS 암호화 실패'; continue }

    $text   = [System.IO.File]::ReadAllText($enc)
    $blocks = [FileCrypt.FileCryptCore]::ExtractBlocks($text)
    if ($blocks.Count -ne 1) { Ok ('PS→C# : ' + $s.Name) $false ('블록 {0}개' -f $blocks.Count); continue }

    try {
        $df = [FileCrypt.FileCryptCore]::Decrypt($blocks[0])
        $nameOk  = ($df.FileName -eq $s.Name)
        $bytesOk = BytesEqual $df.Data $s.Bytes
        Ok ('PS→C# : ' + $s.Name) ($nameOk -and $bytesOk) ('이름 {0} / 바이트 {1}' -f $(if($nameOk){'O'}else{'X'}), $(if($bytesOk){'O'}else{'X'}))
    } catch {
        Ok ('PS→C# : ' + $s.Name) $false $_.Exception.Message
    }
}

# ================================================================ 2) C# 암호화 -> PS 복호화
Write-Host ''
Write-Host '  -- GUI 로 만든 것을 PowerShell 이 열 수 있는가 --' -ForegroundColor DarkGray
foreach ($s in $samples) {
    $container = [FileCrypt.FileCryptCore]::Encrypt($s.Name, $s.Bytes)
    $armor     = [FileCrypt.FileCryptCore]::ToArmor($container, 100)
    $tf = Join-Path $WORK ('cs_' + [Math]::Abs($s.Name.GetHashCode()) + '.txt')
    [System.IO.File]::WriteAllText($tf, $armor, $u8n)

    $outDir = Join-Path $WORK ('out_' + [Math]::Abs($s.Name.GetHashCode()))
    New-Item -ItemType Directory -Force $outDir | Out-Null

    $global:LASTEXITCODE = 0
    $res = & $ENGINE -Mode Decrypt -Path $tf -OutDir $outDir -Quiet 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $res) { Ok ('C#→PS : ' + $s.Name) $false ('rc=' + $LASTEXITCODE); continue }

    $p = $res | Select-Object -Last 1
    $nameOk  = ([System.IO.Path]::GetFileName($p) -eq $s.Name)
    $bytesOk = BytesEqual ([System.IO.File]::ReadAllBytes($p)) $s.Bytes
    Ok ('C#→PS : ' + $s.Name) ($nameOk -and $bytesOk) ('이름 {0} / 바이트 {1}' -f $(if($nameOk){'O'}else{'X'}), $(if($bytesOk){'O'}else{'X'}))
}

# ================================================================ 3) 다중 블록 묶음
Write-Host ''
Write-Host '  -- 여러 파일 묶음 (다중 블록) --' -ForegroundColor DarkGray

# C# 이 만든 묶음을 C# 이 다시 읽기
$chunks = @()
foreach ($s in $samples) {
    $c = [FileCrypt.FileCryptCore]::Encrypt($s.Name, $s.Bytes)
    $chunks += [FileCrypt.FileCryptCore]::ToArmor($c, 100)
}
$bundle = ($chunks -join "`r`n`r`n") + "`r`n"
$got = [FileCrypt.FileCryptCore]::ExtractBlocks($bundle)
Ok 'C# 묶음 -> C# 이 블록 5개 인식' ($got.Count -eq $samples.Count) ('{0}개' -f $got.Count)

$allOk = $true
for ($i = 0; $i -lt $got.Count; $i++) {
    $df = [FileCrypt.FileCryptCore]::Decrypt($got[$i])
    if ($df.FileName -ne $samples[$i].Name -or -not (BytesEqual $df.Data $samples[$i].Bytes)) { $allOk = $false }
}
Ok 'C# 묶음 -> 전부 원본 일치' $allOk ''

# PS 간편모드가 만든 묶음을 C# 이 읽기
$SIMPLE = Join-Path $ROOTDIR 'engine\simple.ps1'
$srcDir = Join-Path $WORK 'bundle_src'
New-Item -ItemType Directory -Force $srcDir | Out-Null
$paths = @()
foreach ($s in $samples) {
    if ($s.Bytes.Length -eq 0) { continue }   # 간편모드 표시용으로 빈 파일은 제외
    $p = Join-Path $srcDir $s.Name
    [System.IO.File]::WriteAllBytes($p, $s.Bytes)
    $paths += $p
}
& $SIMPLE -Mode Encrypt -Path $paths *>&1 | Out-Null
$bundleFile = Get-ChildItem -LiteralPath $srcDir -Filter 'FCRYPT 묶음*.txt' | Select-Object -First 1
if ($bundleFile) {
    $t = [System.IO.File]::ReadAllText($bundleFile.FullName)
    $bl = [FileCrypt.FileCryptCore]::ExtractBlocks($t)
    $match = $true
    for ($i = 0; $i -lt $bl.Count; $i++) {
        $df = [FileCrypt.FileCryptCore]::Decrypt($bl[$i])
        $expect = $samples | Where-Object { $_.Name -eq $df.FileName } | Select-Object -First 1
        if ($null -eq $expect -or -not (BytesEqual $df.Data $expect.Bytes)) { $match = $false }
    }
    Ok 'PS 간편모드 묶음 -> C# 이 전부 복원' (($bl.Count -eq $paths.Count) -and $match) ('블록 {0}개' -f $bl.Count)
} else {
    Ok 'PS 간편모드 묶음 -> C# 이 전부 복원' $false '묶음 파일 없음'
}

# ================================================================ 4) 변조/훼손 거부
Write-Host ''
Write-Host '  -- GUI 도 손상된 데이터를 거부하는가 --' -ForegroundColor DarkGray
$c = [FileCrypt.FileCryptCore]::Encrypt('x.txt', $u8n.GetBytes('hello world ' * 100))

$bad = [byte[]]$c.Clone()
$pos = $c.Length - 10        # 암호문 구간 안쪽 (컨테이너가 작을 수 있으므로 끝에서 센다)
$bad[$pos] = $bad[$pos] -bxor 1
try { [FileCrypt.FileCryptCore]::Decrypt($bad) | Out-Null; Ok '1비트 변조 -> 거부' $false '통과해버림' }
catch [FileCrypt.FileCryptAuthException] { Ok '1비트 변조 -> 거부' $true 'AuthException' }
catch { Ok '1비트 변조 -> 거부' $true $_.Exception.GetType().Name }

$saltBad = [byte[]]$c.Clone()
$saltBad[12] = $saltBad[12] -bxor 0xFF        # salt 변조 -> 다른 키가 유도됨
try { [FileCrypt.FileCryptCore]::Decrypt($saltBad) | Out-Null; Ok 'salt 변조 -> 거부' $false '통과해버림' }
catch [FileCrypt.FileCryptAuthException] { Ok 'salt 변조 -> 거부' $true 'AuthException' }
catch { Ok 'salt 변조 -> 거부' $true $_.Exception.GetType().Name }

$trunc = New-Object byte[] ($c.Length - 32)
[Array]::Copy($c, $trunc, $trunc.Length)
try { [FileCrypt.FileCryptCore]::Decrypt($trunc) | Out-Null; Ok '뒤쪽 잘림 -> 거부' $false '통과해버림' }
catch { Ok '뒤쪽 잘림 -> 거부' $true $_.Exception.GetType().Name }

# 붙여넣기 훼손 내성 (C# 쪽)
$armor = [FileCrypt.FileCryptCore]::ToArmor($c, 100)
$messy = "안녕하세요 아래 파일입니다`r`n`r`n" + (($armor -split "`r?`n" | ForEach-Object { if ($_ -like '-----*') { $_ } else { '> ' + $_.Insert(3, [char]0x200B) + '  ' } }) -join "`r`n") + "`r`n`r`n확인 부탁드립니다"
$mb = [FileCrypt.FileCryptCore]::ExtractBlocks($messy)
$msgOk = $false
if ($mb.Count -eq 1) {
    try { $df = [FileCrypt.FileCryptCore]::Decrypt($mb[0]); $msgOk = ($df.FileName -eq 'x.txt') } catch { }
}
Ok '잡담+인용부호+제로폭문자 -> 정상 복원' $msgOk ''

Write-Host ''
Write-Host ('########## 호환 테스트: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
Write-Host ('  작업 폴더: {0}' -f $WORK) -ForegroundColor DarkGray

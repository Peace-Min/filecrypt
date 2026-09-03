$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$SIMPLE = Join-Path (Split-Path $PSScriptRoot -Parent) 'simple.ps1'
$ROOT   = Join-Path $env:TEMP 'fc_simple'
$DESK   = [Environment]::GetFolderPath('Desktop')
if (Test-Path $ROOT) { Remove-Item $ROOT -Recurse -Force }
New-Item -ItemType Directory -Force $ROOT | Out-Null
$u8n = New-Object System.Text.UTF8Encoding($false)
$fail = 0; $n = 0

function Run([hashtable]$P) {
    $global:LASTEXITCODE = 0
    $out = & $SIMPLE @P *>&1
    return @{ Rc = $LASTEXITCODE; Out = (($out | Out-String).Trim()) }
}
function Ok([string]$name, [bool]$cond, [string]$extra) {
    $script:n++
    if ($cond) { Write-Host ('  [PASS] {0}  {1}' -f $name.PadRight(46), $extra) -ForegroundColor Green }
    else       { Write-Host ('  [FAIL] {0}  {1}' -f $name.PadRight(46), $extra) -ForegroundColor Red; $script:fail++ }
}
function Hash([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }

Write-Host ''
Write-Host '########## 간편 모드 (프롬프트 0회) ##########' -ForegroundColor Cyan

# ---------------------------------------------------------------- 1) 기본 왕복
$src = Join-Path $ROOT '샘플 결과.xml'
[System.IO.File]::WriteAllText($src, ("<r>`r`n" + ("  <row>데이터 행</row>`r`n" * 800) + "</r>"), $u8n)
$srcHash = Hash $src

$e = Run @{ Mode='Encrypt'; Path=$src }
$txt = Join-Path $ROOT '샘플 결과.xml.enc.txt'
Ok '암호화 (프롬프트 없이 완료)' (($e.Rc -eq 0) -and (Test-Path -LiteralPath $txt)) ('rc=' + $e.Rc)
Ok '아무것도 묻지 않았는지' (-not ($e.Out -match 'Read-Host|입력하세요')) ''

$clip = try { Get-Clipboard -Raw } catch { '' }
Ok '클립보드 자동 복사' ($clip -and $clip.Contains('-----BEGIN FCRYPT')) ('{0:N0}자' -f $clip.Length)

$restored = Join-Path $DESK '샘플 결과.xml'
if (Test-Path -LiteralPath $restored) { Remove-Item -LiteralPath $restored -Force }
$d = Run @{ Mode='Decrypt' }
$ok = ($d.Rc -eq 0) -and (Test-Path -LiteralPath $restored) -and ((Hash $restored) -eq $srcHash)
Ok '클립보드만으로 복호화 (질문 0회)' $ok ''
Ok '복호화도 아무것도 묻지 않음' (-not ($d.Out -match 'Read-Host')) ''
if (Test-Path -LiteralPath $restored) { Remove-Item -LiteralPath $restored -Force }

# ---------------------------------------------------------------- 2) 붙여넣기 훼손 내성
$base = [System.IO.File]::ReadAllLines($txt)

function PasteTest([string]$name, [string[]]$lines, [bool]$shouldWork) {
    $p = Join-Path $ROOT ('paste_' + [Math]::Abs($name.GetHashCode()) + '.txt')
    [System.IO.File]::WriteAllLines($p, $lines, $u8n)
    $r = Run @{ Mode='Decrypt'; Path=$p }
    if ($shouldWork) {
        $out = Join-Path $ROOT '샘플 결과.xml'
        $good = ($r.Rc -eq 0) -and (Test-Path -LiteralPath $out) -and ((Hash $out) -eq $script:srcHash)
        if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
        Ok $name $good ('rc=' + $r.Rc)
    } else {
        Ok $name ($r.Rc -ne 0) ('rc=' + $r.Rc + ' (거부)')
    }
}

Write-Host ''
Write-Host '  -- 붙여넣기 과정에서 흔히 생기는 변형 --' -ForegroundColor DarkGray
PasteTest '빈 줄이 중간중간 섞임'        ($base | ForEach-Object { $_; '' })                                  $true
PasteTest '줄 앞뒤에 공백/탭이 붙음'     ($base | ForEach-Object { if ($_ -like '-----*') { $_ } else { "  $_`t" } }) $true
PasteTest '제로폭 문자(U+200B) 삽입'     ($base | ForEach-Object { if ($_ -like '-----*') { $_ } else { $_.Insert(5, [char]0x200B) } }) $true
PasteTest '줄바꿈이 전부 사라짐(한 줄)'  @('-----BEGIN FCRYPT MESSAGE-----', (($base | Where-Object { $_ -notlike '-----*' }) -join ''), '-----END FCRYPT MESSAGE-----') $true
PasteTest '줄폭을 다르게 다시 접음'      (@('-----BEGIN FCRYPT MESSAGE-----') + ((($base | Where-Object { $_ -notlike '-----*' }) -join '') -split '(.{1,40})' | Where-Object { $_ }) + @('-----END FCRYPT MESSAGE-----')) $true
PasteTest 'BEGIN/END 앞뒤에 잡담 텍스트' (@('안녕하세요 아래 파일입니다','') + $base + @('','확인 부탁드립니다')) $true
PasteTest '메일 인용부호 "> " 가 붙음'   ($base | ForEach-Object { if ($_ -like '-----*') { $_ } else { "> $_" } }) $true
PasteTest 'END 줄이 없지만 데이터는 온전' ($base[0..($base.Count-2)])                                            $true

Write-Host ''
Write-Host '  -- 실제로 데이터가 상한 경우 (반드시 거부돼야 함) --' -ForegroundColor DarkGray
PasteTest '뒤쪽 3줄 잘림'               ($base[0..($base.Count-4)] + '-----END FCRYPT MESSAGE-----')          $false
PasteTest '앞쪽 한 줄 누락'             (@($base[0]) + $base[2..($base.Count-1)])                             $false
PasteTest 'Base64 1글자 변조'           ($base | ForEach-Object { if ($_ -eq $base[3]) { $_.Remove(5,1).Insert(5, $(if ($_[5] -eq 'A') { 'B' } else { 'A' })) } else { $_ } }) $false


# ---------------------------------------------------------------- 4) 파일 종류별 왕복
Write-Host ''
Write-Host '  -- 파일 종류별 왕복 --' -ForegroundColor DarkGray
$kinds = @{
    '한글 문서.txt'      = [System.Text.Encoding]::GetEncoding(949).GetBytes("가나다라 CP949 텍스트`r`n" * 100)
    '데이터.bin'         = [byte[]](0..255)
    '빈파일.dat'         = New-Object byte[] 0
    'イメージ.json'      = $u8n.GetBytes('{"키":"값","배열":[1,2,3]}')
    '보고서 [최종].hwp'  = [byte[]](1..2000 | ForEach-Object { $_ % 256 })
}
foreach ($k in $kinds.Keys) {
    $p = Join-Path $ROOT $k
    [System.IO.File]::WriteAllBytes($p, $kinds[$k])
    $h = Hash $p
    $r1 = Run @{ Mode='Encrypt'; Path=$p }
    Remove-Item -LiteralPath $p -Force
    $r2 = Run @{ Mode='Decrypt'; Path=($p + '.enc.txt') }
    $good = ($r1.Rc -eq 0) -and ($r2.Rc -eq 0) -and (Test-Path -LiteralPath $p) -and ((Hash $p) -eq $h)
    Ok ('왕복: ' + $k) $good ''
}

Write-Host ''
Write-Host ('########## 간편 모드 결과: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
Remove-Item $ROOT -Recurse -Force -ErrorAction SilentlyContinue

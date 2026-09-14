$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

# netcus(회사 일간보고) 같은 '텍스트 통로'는 붙여넣기·HTML 렌더 과정에서
# 줄바꿈과 공백을 뭉갠다. base64 는 원래 공백과 무관하므로, 그런 훼손을 거쳐도
# 원본이 100% 복원돼야 한다. C#(GUI)·PS(엔진) 양쪽 + 교차로 확인한다.

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ENGINE  = Join-Path $ROOTDIR 'engine\filecrypt.ps1'
$EXE     = Join-Path $ROOTDIR 'gui\bin\Release\net48\FileCrypt.exe'
$WORK    = Join-Path $env:TEMP ('fc_netcus_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $WORK | Out-Null
$u8n = New-Object System.Text.UTF8Encoding($false)

$n = 0; $fail = 0
function Ok([string]$name, [bool]$cond, [string]$extra) {
    $script:n++
    if ($cond) { Write-Host ('  [PASS] {0}  {1}' -f $name.PadRight(50), $extra) -ForegroundColor Green }
    else       { Write-Host ('  [FAIL] {0}  {1}' -f $name.PadRight(50), $extra) -ForegroundColor Red; $script:fail++ }
}
function Sha([byte[]]$b) {
    $s = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($s.ComputeHash($b))).Replace('-','') } finally { $s.Dispose() }
}
function ShaFile([string]$p) { return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }

Write-Host ''
Write-Host '########## netcus 텍스트 통로 내성 ##########' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $EXE)) { Write-Host '  [FAIL] gui 빌드 없음' -ForegroundColor Red; exit 1 }
$exeCopy = Join-Path $WORK 'FileCrypt.exe'
Copy-Item -LiteralPath $EXE -Destination $exeCopy -Force
[void][Reflection.Assembly]::LoadFrom($exeCopy)
$FC = [FileCrypt.FileCryptCore]

# ---- netcus 가 텍스트에 가할 수 있는 훼손들 --------------------------------
$euckr = [System.Text.Encoding]::GetEncoding(51949)   # euc-kr(ks_c_5601) — netcus 페이지 인코딩
function Collapse-Newlines([string]$s) { return (($s -replace "`r`n", ' ') -replace "`n", ' ') }   # 줄바꿈 -> 공백(HTML 렌더)
function Strip-Whitespace([string]$s) { return ($s -replace '\s', '') }                              # 공백/줄바꿈 전부 제거(최악)
function EucKr-Round([string]$s)      { return $euckr.GetString($euckr.GetBytes($s)) }               # euc-kr 왕복

# ---- 표본: 한글 텍스트 + 압축 안 되는 바이너리(이미지 대용) ----------------
$textBytes = [System.Text.Encoding]::UTF8.GetBytes(("일간보고 원본 데이터. 과제 A 진행. 특수문자 !@#/+=`r`n둘째 줄 12345`r`n" * 60))
$textHash  = Sha $textBytes
$rand = New-Object System.Random 20260914
$blob = New-Object byte[] (350KB); $rand.NextBytes($blob)
$blobHash = Sha $blob

# ================================================================ C# (GUI 경로)
Write-Host ''
Write-Host '  -- C# (GUI가 쓰는 디코더) --' -ForegroundColor DarkGray

$cText = $FC::ToArmor($FC::Encrypt('report.txt', $textBytes), 76)

function CsDecodeOne([string]$armored, [string]$wantHash) {
    $blocks = $FC::ExtractBlocks($armored)
    if ($blocks.Count -eq 0) { return @{ Ok=$false; Why='블록 0개' } }
    try {
        $d = $FC::DecryptAll($blocks[0])[0]
        return @{ Ok = ((Sha $d.Data) -eq $wantHash); Why='해시' }
    } catch { return @{ Ok=$false; Why=$_.Exception.Message } }
}

$r = CsDecodeOne (Collapse-Newlines $cText) $textHash
Ok '통짜: 줄바꿈 전부 공백으로 뭉개짐' $r.Ok $r.Why
$r = CsDecodeOne (Strip-Whitespace $cText) $textHash
Ok '통짜: 공백/줄바꿈 전부 제거(최악)' $r.Ok $r.Why
$r = CsDecodeOne (EucKr-Round (Collapse-Newlines $cText)) $textHash
Ok '통짜: euc-kr 왕복 + 줄바꿈 뭉갬' $r.Ok $r.Why
$r = CsDecodeOne ("보고내용:`r`n" + (Collapse-Newlines $cText) + "`r`n끝.") $textHash
Ok '통짜: 앞뒤 잡텍스트 + 뭉갬' $r.Ok $r.Why

# LooksLikeArmor / HasParts 가 뭉개진 텍스트도 인식해야 UI 가 복원을 시도한다
Ok 'LooksLikeArmor: 뭉개져도 armor 로 인식' ($FC::LooksLikeArmor((Strip-Whitespace $cText))) ''

# 조각(split) — netcus 는 한 번에 못 넣으니 나눠 붙이는 게 실제 시나리오
$blobParts = $FC::ToArmorParts($FC::Encrypt('photos.bin', $blob), 60000, 76)
Ok '표본 조각 생성' ($blobParts.Count -ge 4) ('{0}조각' -f $blobParts.Count)

# 각 조각의 줄바꿈을 뭉개고, 순서를 뒤섞어 이어 붙인다
$mangled = @($blobParts | ForEach-Object { Collapse-Newlines $_ } | Sort-Object { Get-Random })
$joined  = ($mangled -join ' ')            # 조각 사이도 공백 하나로만
$pblocks = $FC::ExtractBlocks($joined)
$pOk = $false
if ($pblocks.Count -eq 1) { try { $pOk = ((Sha $FC::DecryptAll($pblocks[0])[0].Data) -eq $blobHash) } catch {} }
Ok '조각: 뭉개고 뒤섞어도 복원' $pOk ('블록 {0}개' -f $pblocks.Count)
Ok 'HasParts: 뭉개진 조각 인식' ($FC::HasParts($joined)) ''

# 조각이 모자라면(뭉갠 상태에서도) 무엇이 없는지 알려줘야 한다
$lack = (@($mangled[0], $mangled[1]) -join ' ')
$pend = $FC::InspectParts($lack)
Ok '조각 부족: 뭉개진 상태에서도 집계' ($pend.Count -ge 1 -and $pend[0].Missing.Count -ge 1) `
   ('{0}/{1} 모임' -f $pend[0].Have.Count, $pend[0].Total)

# ================================================================ PS 엔진 + 교차
Write-Host ''
Write-Host '  -- PS 엔진 <-> C# 교차 (뭉개진 텍스트) --' -ForegroundColor DarkGray

# (a) C# 통짜 -> 줄바꿈 뭉갬 -> 파일 -> PS 엔진 복호
$f1 = Join-Path $WORK 'whole_mangled.txt'
[System.IO.File]::WriteAllText($f1, (Collapse-Newlines $cText), $u8n)
$o1 = Join-Path $WORK 'ps_whole'
& $ENGINE -Mode Decrypt -Path $f1 -OutDir $o1 -Force -Quiet 2>$null | Out-Null
$g1 = Get-ChildItem -LiteralPath $o1 -File -Recurse | Select-Object -First 1
Ok 'C# 통짜(뭉갬) -> PS 복원' ($g1 -and ((ShaFile $g1.FullName) -eq $textHash)) $(if($g1){'복원됨'}else{'파일 없음'})

# (b) C# 조각 -> 줄바꿈 뭉갬 -> 한 파일로 합침 -> PS 엔진 복호
$f2 = Join-Path $WORK 'parts_mangled.txt'
[System.IO.File]::WriteAllText($f2, $joined, $u8n)
$o2 = Join-Path $WORK 'ps_parts'
& $ENGINE -Mode Decrypt -Path $f2 -OutDir $o2 -Force -Quiet 2>$null | Out-Null
$g2 = Get-ChildItem -LiteralPath $o2 -File -Recurse | Select-Object -First 1
Ok 'C# 조각(뭉갬) -> PS 복원' ($g2 -and ((ShaFile $g2.FullName) -eq $blobHash)) $(if($g2){'복원됨'}else{'파일 없음'})

# (c) PS 엔진이 만든 조각 -> 뭉갬 -> C# 복호
$srcFile = Join-Path $WORK 'src.bin'
[System.IO.File]::WriteAllBytes($srcFile, $blob)
# 엔진은 -Split 시 -Out 파일명의 어간으로 "어간 [1of N].txt" 조각들을 같은 폴더에 쓴다.
$psEnc = Join-Path $WORK 'psout.enc.txt'
& $ENGINE -Mode Encrypt -Path $srcFile -Out $psEnc -Armor -Width 76 -Split 60000 -Force -Quiet 2>$null | Out-Null
$psPieces = Get-ChildItem -LiteralPath $WORK -Filter 'psout [*of*].txt' | Sort-Object Name
$psMangled = @($psPieces | ForEach-Object { Collapse-Newlines ([System.IO.File]::ReadAllText($_.FullName)) })
$csBlocks = $FC::ExtractBlocks(($psMangled -join ' '))
$cOk = $false
if ($csBlocks.Count -eq 1) { try { $cOk = ((Sha $FC::DecryptAll($csBlocks[0])[0].Data) -eq $blobHash) } catch {} }
Ok 'PS 조각(뭉갬) -> C# 복원' $cOk ('조각 {0}개 -> 블록 {1}개' -f $psPieces.Count, $csBlocks.Count)

Write-Host ''
Write-Host ('########## netcus 내성: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor Cyan

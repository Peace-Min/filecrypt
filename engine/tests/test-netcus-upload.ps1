$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

# 사내 보고 시스템에 올리고/받아오는 '계획과 조립' 로직(NetcusPlan).
# 일간보고는 날짜당 칸이 하나라 조각 1개 = 날짜 1개다. 그 배치와 되모으기를 검증한다.
# 브라우저(WebView2)를 타는 실제 로그인/입력은 여기서 테스트하지 않는다 - 사이트가 있어야 한다.

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$EXE     = Join-Path $ROOTDIR 'gui\bin\Release\net48\FileCrypt.exe'
$WORK    = Join-Path $env:TEMP ('fc_ncup_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $WORK | Out-Null

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
function StrList($arr) {
    # PowerShell 의 object[] 는 IEnumerable<string> 으로 변환되지 않는다. 명시적으로 담아 준다.
    $l = New-Object 'System.Collections.Generic.List[string]'
    foreach ($a in $arr) { $l.Add([string]$a) }
    return ,$l
}

Write-Host ''
Write-Host '########## 보고 시스템 업로드 계획 (NetcusPlan) ##########' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $EXE)) { Write-Host '  [FAIL] gui 빌드 없음' -ForegroundColor Red; exit 1 }
$exeCopy = Join-Path $WORK 'FileCrypt.exe'
Copy-Item -LiteralPath $EXE -Destination $exeCopy -Force
[void][Reflection.Assembly]::LoadFrom($exeCopy)
$FC   = [FileCrypt.FileCryptCore]
$PLAN = [FileCrypt.NetcusPlan]

$start = [datetime]'2026-09-14'
$LIMIT = 200000

# ---------------------------------------------------------------- 표본
$small = [System.Text.Encoding]::UTF8.GetBytes(('일간보고 첨부 데이터. ' * 50))
$smallC = $FC::Encrypt('small.txt', $small)
$rand = New-Object System.Random 20260914
$big = New-Object byte[] (600KB); $rand.NextBytes($big)   # 압축 안 되는 것 = 여러 조각
$bigC = $FC::Encrypt('big.bin', $big)

# ================================================================ 1) 한도 안이면 하루만
$s1 = $PLAN::Build($smallC, $start, $LIMIT)
Ok '한도 안 -> 날짜 1개만 사용' ($s1.Count -eq 1) ('{0}일' -f $s1.Count)
Ok '  날짜가 지정한 시작일' ($s1[0].Date -eq $start) ($s1[0].Date.ToString('yyyy-MM-dd'))
Ok '  나누지 않아 통짜 블록' (($s1[0].Text -like '*BEGIN FCRYPT MESSAGE*') -and ($s1[0].Total -eq 1)) ''

# ================================================================ 2) 한도 초과 -> 연속 날짜에 하루 1조각
$s2 = $PLAN::Build($bigC, $start, $LIMIT)
Ok '한도 초과 -> 여러 날짜로 나뉨' ($s2.Count -ge 4) ('{0}조각/{0}일' -f $s2.Count)

$consecutive = $true
for ($i = 0; $i -lt $s2.Count; $i++) {
    if ($s2[$i].Date -ne $start.AddDays($i)) { $consecutive = $false; break }
}
Ok '  시작일부터 연속된 날짜' $consecutive ('{0} ~ {1}' -f $s2[0].Date.ToString('MM-dd'), $s2[$s2.Count-1].Date.ToString('MM-dd'))

$over = @($s2 | Where-Object { $_.Text.Length -gt $LIMIT })
Ok '  각 날짜 내용이 한도 이하' ($over.Count -eq 0) ('최대 {0:N0}자' -f (($s2 | ForEach-Object { $_.Text.Length } | Measure-Object -Maximum).Maximum))

$idxOk = $true
for ($i = 0; $i -lt $s2.Count; $i++) {
    if ($s2[$i].Index -ne ($i+1) -or $s2[$i].Total -ne $s2.Count) { $idxOk = $false; break }
}
Ok '  조각 번호가 1..N 로 매겨짐' $idxOk ''

# ================================================================ 2-2) 한도를 바꾸면 날짜 수가 달라져야 한다
# 실제로 겪은 결함: 메인 창 [조각내기] 를 20만 -> 50만으로 바꿔도 근태관리 창은
# 계속 같은 날짜 수를 보여줬다. 한도 값이 아예 전달되지 않고 있었다.
$d20 = $PLAN::Build($bigC, $start, 200000)
$d50 = $PLAN::Build($bigC, $start, 500000)
$d10 = $PLAN::Build($bigC, $start, 100000)
Ok '한도 20만 -> 50만 이면 날짜 수가 줄어든다' ($d50.Count -lt $d20.Count) `
   ('20만={0}일 / 50만={1}일' -f $d20.Count, $d50.Count)
Ok '한도 20만 -> 10만 이면 날짜 수가 늘어난다' ($d10.Count -gt $d20.Count) `
   ('20만={0}일 / 10만={1}일' -f $d20.Count, $d10.Count)

$over50 = @($d50 | Where-Object { $_.Text.Length -gt 500000 })
Ok '  50만 한도에서도 각 날짜가 한도 이하' ($over50.Count -eq 0) `
   ('최대 {0:N0}자' -f (($d50 | ForEach-Object { $_.Text.Length } | Measure-Object -Maximum).Maximum))

# 한도를 바꿔도 내용은 그대로 복원돼야 한다
$g50 = $PLAN::Gather((StrList @($d50 | ForEach-Object { $_.Text })))
$r50 = $FC::DecryptAll($FC::ExtractBlocks($g50.Text)[0])[0]
Ok '  한도를 바꿔도 원본 그대로 복원' ((Sha $r50.Data) -eq (Sha $big)) ''

# ================================================================ 3) 되모으기 - 순서 뒤섞어도 복원
$shuffled = @($s2 | Sort-Object { Get-Random } | ForEach-Object { $_.Text })
$g = $PLAN::Gather((StrList $shuffled))
Ok '뒤섞인 날짜 내용 -> 블록 완성' ($g.Ready -and $g.BlockCount -eq 1) ('블록 {0}개' -f $g.BlockCount)

$restored = $FC::DecryptAll($FC::ExtractBlocks($g.Text)[0])[0]
Ok '  원본과 해시 일치' ((Sha $restored.Data) -eq (Sha $big)) $restored.FileName

# ================================================================ 4) 하루가 비면 - 무엇이 없는지 알려준다
$lack = @($shuffled | Select-Object -First ($s2.Count - 1))
$g2 = $PLAN::Gather((StrList $lack))
Ok '조각 하나 빠짐 -> 복원 안 함' (-not $g2.Ready) ('블록 {0}개' -f $g2.BlockCount)
Ok '  없는 조각 번호를 집어냄' (($g2.Pending.Count -ge 1) -and ($g2.Pending[0].Missing.Count -eq 1)) `
   ('{0}/{1} 모임' -f $g2.Pending[0].Have.Count, $g2.Pending[0].Total)

# ================================================================ 5) 빈 날짜가 섞여도 무시
$withBlanks = @($shuffled[0], '', '   ', $shuffled[1])
foreach ($t in ($shuffled | Select-Object -Skip 2)) { $withBlanks += $t }
$g3 = $PLAN::Gather((StrList $withBlanks))
Ok '빈 날짜가 섞여도 복원' ($g3.Ready) ('내용 있는 날 {0}일' -f $g3.DaysWithContent)

# ================================================================ 6) 기존 내용 감지
$s4 = $PLAN::Build($smallC, $start, $LIMIT)
$s4[0].Existing = ''
Ok '빈 칸은 덮어쓰기 대상 아님' ($PLAN::Occupied($s4).Count -eq 0) ''
$s4[0].Existing = "오늘 한 일`r`n- 설계 검토"
$occ = $PLAN::Occupied($s4)
Ok '내용 있으면 덮어쓰기 대상으로 잡힘' ($occ.Count -eq 1) ''
$desc = $PLAN::DescribeOccupied($occ)
Ok '  어느 날짜가 막혔는지 문장으로' (($desc -like '*2026-09-14*') -and ($desc -like '*오늘 한 일*')) `
   ($PLAN::Preview($desc, 40))

# 공백만 있는 칸은 비어있는 것으로 본다
$s4[0].Existing = "   `r`n  "
Ok '공백뿐인 칸은 빈 칸 취급' ($PLAN::Occupied($s4).Count -eq 0) ''

# ================================================================ 7) 미리보기 / 날짜범위
Ok '미리보기: 줄바꿈을 한 줄로 접고 자름' `
   (($PLAN::Preview("첫 줄`r`n둘째 줄 매우 긴 내용입니다", 10)) -eq '첫 줄 둘째 줄 매…') `
   ($PLAN::Preview("첫 줄`r`n둘째 줄 매우 긴 내용입니다", 10))

$range = $PLAN::DateRange($start, 3)
Ok '날짜 범위 생성' (($range.Count -eq 3) -and ($range[2] -eq $start.AddDays(2))) `
   ('{0} ~ {1}' -f $range[0].ToString('MM-dd'), $range[2].ToString('MM-dd'))

# ================================================================ 8) 계획 설명
$d1 = $PLAN::Describe($s1)
$d2 = $PLAN::Describe($s2)
Ok '계획 설명(하루)'   ($d1 -like '*2026-09-14*') $d1
Ok '계획 설명(여러날)' (($d2 -like '*조각*') -and ($d2 -like '*~*')) $d2

Write-Host ''
Write-Host ('########## 업로드 계획: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor Cyan

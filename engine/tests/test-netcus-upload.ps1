. (Join-Path $PSScriptRoot '_common.ps1')

# 사내 보고 시스템에 올리고/받아오는 '계획과 조립' 로직(NetcusPlan).
# 일간보고는 날짜당 칸이 하나라 조각 1개 = 날짜 1개다. 그 배치와 되모으기를 검증한다.
# 브라우저(WebView2)를 타는 실제 로그인/입력은 여기서 테스트하지 않는다 - 사이트가 있어야 한다.

Start-Test -Tag ncup -Title '보고 시스템 업로드 계획 (NetcusPlan)' -Pad 50
Import-FileCrypt

function StrList($arr) {
    # PowerShell 의 object[] 는 IEnumerable<string> 으로 변환되지 않는다. 명시적으로 담아 준다.
    $l = New-Object 'System.Collections.Generic.List[string]'
    foreach ($a in $arr) { $l.Add([string]$a) }
    return ,$l
}

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

# ================================================================ 9) 작은 묶음 = 통짜 블록 1개 그대로
# 한도 안이면 조각을 만들지 않고 ToArmor 결과를 그대로 올린다(받아온 뒤 GUI 복원과 같은 모양).
$armorSmall = $FC::ToArmor($smallC, $FC::DefaultWidth)
Ok '작은 묶음 -> 슬롯 1개 = ToArmor 결과 그대로' (($s1.Count -eq 1) -and ($s1[0].Text -ceq $armorSmall)) `
   ('{0:N0}자' -f $armorSmall.Length)

# ================================================================ 10) 날짜 옮기기 (Redate)
# 시작 날짜만 바꿀 때 조각을 다시 만들지 않는다. 사이트에서 읽어 둔 기존 내용은 날짜가 바뀌면 무효.
Write-Host ''
Write-Host '  -- 날짜 옮기기 / 사이트 내용 비교 --' -ForegroundColor DarkGray
$rd = $PLAN::Build($bigC, $start, $LIMIT)
$textsBefore = @($rd | ForEach-Object { $_.Text })
foreach ($sl in $rd) { $sl.Existing = '읽어 둔 내용' }
$newStart = [datetime]'2026-10-05'
$PLAN::Redate($rd, $newStart)
$dateOk = $true; $exOk = $true; $txtOk = $true
for ($i = 0; $i -lt $rd.Count; $i++) {
    if ($rd[$i].Date -ne $newStart.AddDays($i)) { $dateOk = $false }
    if ($null -ne $rd[$i].Existing)             { $exOk = $false }
    if ($rd[$i].Text -cne $textsBefore[$i])     { $txtOk = $false }
}
Ok 'Redate: 새 시작일부터 연속 날짜' $dateOk ('{0} ~ {1}' -f $rd[0].Date.ToString('MM-dd'), $rd[$rd.Count-1].Date.ToString('MM-dd'))
Ok '  읽어 둔 기존 내용은 지워진다' $exOk ''
Ok '  조각 내용은 그대로' $txtOk ('{0}조각' -f $rd.Count)

# ================================================================ 11) 사이트 내용 비교 (SameContent)
# 사이트가 화면에 그리며 줄바꿈·공백을 바꾼다. 그 차이는 같은 것으로 봐야 이어 올리기/확인이 된다.
$exp = $s2[0].Text
Ok 'SameContent: 그대로면 같음' ($PLAN::SameContent($exp, $exp)) ''
Ok '  줄바꿈을 공백으로 뭉개도 같음' ($PLAN::SameContent((($exp -replace "`r`n", ' ') -replace "`n", ' '), $exp)) ''
Ok '  공백/줄바꿈을 전부 빼도 같음' ($PLAN::SameContent(($exp -replace '\s', ''), $exp)) ''
Ok '  제로폭 문자(U+200B/U+FEFF)가 섞여도 같음' `
   ($PLAN::SameContent(($exp.Insert(40, [string][char]0x200B).Insert(10, [string][char]0xFEFF)), $exp)) ''
$pos = [Math]::Min(200, $exp.Length - 1)
while ($exp[$pos] -notmatch '[A-Za-z0-9]') { $pos++ }
$chg = $exp.Remove($pos, 1).Insert($pos, $(if ($exp[$pos] -ceq 'A') { 'B' } else { 'A' }))
Ok '  한 글자 바뀌면 다름' (-not $PLAN::SameContent($chg, $exp)) ''
Ok '  null 이면 다름' ((-not $PLAN::SameContent($null, $exp)) -and (-not $PLAN::SameContent($exp, $null))) ''

# ================================================================ 12) 범위 나누기 (SplitRange)
# 한 번에 읽을 수 있는 날짜 수(MaxReadDays=31)를 넘는 가져오기는 여러 번에 나눠 읽는다.
Write-Host ''
Write-Host '  -- 받아오기: 범위 나누기 / 회신 해석 --' -ForegroundColor DarkGray
Ok 'MaxReadDays = 31' ($PLAN::MaxReadDays -eq 31) ''
function DaysOf($kv) { return [int](($kv.Value - $kv.Key).TotalDays) + 1 }
$from = [datetime]'2026-01-01'
$w60 = $PLAN::SplitRange($from, $from.AddDays(59), $PLAN::MaxReadDays)
Ok '60일 -> 2구간 (31 + 29)' `
   (($w60.Count -eq 2) -and ((DaysOf $w60[0]) -eq 31) -and ((DaysOf $w60[1]) -eq 29) -and `
    ($w60[0].Key -eq $from) -and ($w60[1].Key -eq $w60[0].Value.AddDays(1)) -and ($w60[1].Value -eq $from.AddDays(59))) `
   ($(($w60 | ForEach-Object { '{0:MM-dd}~{1:MM-dd}' -f $_.Key, $_.Value }) -join ', '))
$w31 = $PLAN::SplitRange($from, $from.AddDays(30), 31)
Ok '31일 -> 1구간' (($w31.Count -eq 1) -and ((DaysOf $w31[0]) -eq 31)) ('{0}구간' -f $w31.Count)
$wsw = $PLAN::SplitRange($from.AddDays(9), $from, 31)
Ok '끝이 앞이면 바꿔서 처리' (($wsw.Count -eq 1) -and ($wsw[0].Key -eq $from) -and ($wsw[0].Value -eq $from.AddDays(9))) `
   ('{0:MM-dd}~{1:MM-dd}' -f $wsw[0].Key, $wsw[0].Value)
$w1 = $PLAN::SplitRange($from, $from, 31)
Ok '하루 -> 1구간 (시작=끝)' (($w1.Count -eq 1) -and ($w1[0].Key -eq $from) -and ($w1[0].Value -eq $from)) ''

# ================================================================ 13) 범위 읽기 회신 해석 (ParseDaysReply)
$json = '{"ok":true,"days":[' +
        '{"date":"2026-09-14","ok":true,"content":"첫날 내용"},' +
        '{"date":"2026-09-15","ok":false,"content":""},' +
        '{"date":"2026-09-16","ok":true,"content":""}]}'
$days = $PLAN::ParseDaysReply($json)
Ok '회신: 정상 날짜만 담는다 (ok=false 인 날 제외)' `
   (($days.Count -eq 2) -and $days.ContainsKey([datetime]'2026-09-14') -and $days.ContainsKey([datetime]'2026-09-16') -and `
    (-not $days.ContainsKey([datetime]'2026-09-15'))) ('{0}일' -f $days.Count)
Ok '  내용이 그대로 / 빈 칸은 빈 문자열' `
   (($days[[datetime]'2026-09-14'] -eq '첫날 내용') -and ($days[[datetime]'2026-09-16'] -eq '')) ''

function ParseError([string]$j) {
    try { [void]$PLAN::ParseDaysReply($j); return $null }
    catch {
        $ex = $_.Exception; while ($ex.InnerException) { $ex = $ex.InnerException }
        return $ex
    }
}
$e1 = ParseError '{"ok":false,"error":"login"}'
Ok '회신 ok=false (login) -> 로그인 안내로 거부' `
   (($null -ne $e1) -and ($e1 -is [InvalidOperationException]) -and ($e1.Message -match '로그인')) `
   $(if ($e1) { $e1.Message } else { '통과해버림' })
$e2 = ParseError '{"ok":false,"error":"range"}'
Ok '회신 ok=false (range) -> 거부' `
   (($null -ne $e2) -and ($e2 -is [InvalidOperationException]) -and ($e2.Message -match 'range')) `
   $(if ($e2) { $e2.Message } else { '통과해버림' })
$e3 = ParseError ''
Ok '빈 회신 -> 거부' (($null -ne $e3) -and ($e3 -is [InvalidOperationException])) $(if ($e3) { $e3.Message } else { '통과해버림' })

# ================================================================ 14) 로그인 막힘 판정 (LooksAuthBlocked)
# 로그인 자체가 막혔을 때 더 두드리면 계정이 더 막히므로 멈춰야 한다.
Ok '막힘: 로그인 실패 문구' ($PLAN::LooksAuthBlocked($false, '로그인에 실패했습니다')) ''
Ok '  자격증명 문구' ($PLAN::LooksAuthBlocked($false, '저장된 자격증명이 없습니다')) ''
Ok '  세션 문구' ($PLAN::LooksAuthBlocked($false, '세션이 끊겼습니다')) ''
Ok '  password (대소문자 무관)' ($PLAN::LooksAuthBlocked($false, 'Invalid PASSWORD')) ''
Ok '안 막힘: 성공이면 문구와 무관' (-not $PLAN::LooksAuthBlocked($true, '로그인')) ''
Ok '  다른 실패 (시간 초과)' (-not $PLAN::LooksAuthBlocked($false, '시간 초과')) ''
Ok '  메시지 null' (-not $PLAN::LooksAuthBlocked($false, $null)) ''

# ================================================================ 주 52시간 날짜 고르기 (PickDates)
Write-Host ''
Write-Host '  -- 주 52시간 --' -ForegroundColor DarkGray
Ok '근무시간 표: 정근 8 / 휴가 0 / 반차 4 / 특근+2 = 2 / 야근+3 = 11 / 미선택 0' (($PLAN::Hours('1',0) -eq 8) -and ($PLAN::Hours('6',0) -eq 0) -and ($PLAN::Hours('12',0) -eq 4) -and ($PLAN::Hours('3',2) -eq 2) -and ($PLAN::Hours('2',3) -eq 11) -and ($PLAN::Hours('',0) -eq 0)) ''
Ok '주는 월요일부터 (일요일 -> 그 주 월요일)' (($PLAN::WeekStart([datetime]'2025-03-09') -eq [datetime]'2025-03-03') -and ($PLAN::WeekStart([datetime]'2025-03-03') -eq [datetime]'2025-03-03')) ''

function Site([datetime]$from, [int]$days, [hashtable]$preset) {
    # 빈 날들 + 미리 넣은 근태. WeekOthers 는 -1 (합계를 직접 계산하는 경로) 또는 사이트 값.
    $m = New-Object 'System.Collections.Generic.Dictionary[datetime,FileCrypt.NetcusPlan+DayInfo]'
    for ($i = 0; $i -lt $days; $i++) { $m[$from.AddDays($i)] = New-Object FileCrypt.NetcusPlan+DayInfo }
    if ($preset) { foreach ($k in $preset.Keys) { $x = $m[[datetime]$k]; $x.Status = $preset[$k][0]; $x.Overtime = $preset[$k][1] } }
    return ,$m
}
$sk = New-Object 'System.Collections.Generic.List[string]'
$p1 = $PLAN::PickDates([datetime]'2025-03-03', 8, (Site ([datetime]'2025-03-03') 21 $null), $sk)
Ok '빈 주 8조각: 월~토 6일 + 다음 월·화 (일요일 건너뜀)' ((($p1 | ForEach-Object { $_.ToString('MMdd') }) -join ',') -eq '0303,0304,0305,0306,0307,0308,0310,0311') (($p1 | ForEach-Object { $_.ToString('MMdd') }) -join ',')
Ok '  건너뛴 이유: 일요일 56시간' (($sk.Count -eq 1) -and ($sk[0] -match '2025-03-09\(일\).*56시간')) $sk[0]
$sk.Clear()
$p2 = $PLAN::PickDates([datetime]'2025-03-22', 2, (Site ([datetime]'2025-03-17') 21 @{ '2025-03-17' = @('2', 12); '2025-03-18' = @('1', 0); '2025-03-19' = @('1', 0); '2025-03-20' = @('1', 0); '2025-03-21' = @('1', 0) }), $sk)
Ok '월~금 야근 +12h(52h): 토·일 건너뛰고 월·화' ((($p2 | ForEach-Object { $_.ToString('MMdd') }) -join ',') -eq '0324,0325') ('건너뜀 {0}일' -f $sk.Count)
$sk.Clear()
$p3 = $PLAN::PickDates([datetime]'2025-03-18', 3, (Site ([datetime]'2025-03-17') 7 @{ '2025-03-17' = @('2', 12); '2025-03-18' = @('1', 0); '2025-03-19' = @('1', 0); '2025-03-20' = @('1', 0); '2025-03-21' = @('1', 0) }), $sk)
Ok '근태가 있는 날은 시간이 늘지 않으므로 52h 주에도 넣음' ((($p3 | ForEach-Object { $_.ToString('MMdd') }) -join ',') -eq '0318,0319,0320') ''
$s4 = Site ([datetime]'2025-03-03') 7 $null
foreach ($k in @($s4.Keys)) { $s4[$k].WeekOthers = 48 }   # 사이트가 "나머지 합계 48" 이라고 알려 준 경우
$sk.Clear()
$p4 = $PLAN::PickDates([datetime]'2025-03-05', 1, $s4, $sk)
Ok '사이트의 주간 합계(WeekOthers)를 그대로 씀: 48+8 > 52 -> 넣을 날 없음(더 읽어야 함)' ($null -eq $p4) ('건너뜀 {0}일' -f $sk.Count)
$w = $PLAN::ReadWindow([datetime]'2025-03-05', 3)
Ok '읽는 범위는 시작 주 월요일 ~ 끝 주 일요일' (($w.Key -eq [datetime]'2025-03-03') -and ($w.Value -eq [datetime]'2025-03-09')) ('{0:MM-dd} ~ {1:MM-dd}' -f $w.Key, $w.Value)
$ji = $PLAN::ParseDayInfos('{"ok":true,"error":"","days":[{"date":"2025-03-03","content":"x","ok":true,"status":"2","overtime":"3","weekOthers":27}]}')
$di = $ji[[datetime]'2025-03-03']
Ok '회신에서 근태·초과시간·주간 합계를 읽음' (($di.Status -eq '2') -and ($di.Overtime -eq 3) -and ($di.WeekOthers -eq 27) -and ($di.Content -eq 'x')) ''
$jo = $PLAN::ParseDayInfos('{"ok":true,"error":"","days":[{"date":"2025-03-03","content":"x","ok":true}]}')
Ok '  옛 회신(근태 정보 없음)도 읽힘 - 주간 합계는 -1' (($jo[[datetime]'2025-03-03'].WeekOthers -eq -1) -and ($jo[[datetime]'2025-03-03'].Status -eq '')) ''

Complete-Test

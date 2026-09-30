. (Join-Path $PSScriptRoot '_common.ps1')

# 근태관리 올리기·가져오기를 목업 사이트(tools\netcus-mock)에 실제 코드 전체로 돌린다.
#   FileCrypt.exe --netcus-selftest  ->  NetcusJobs -> NetcusGateway -> NetcusService -> WebView2 -> 목업
# 목업은 실제 사이트에서 떠 온 구조(login.htm / loginRSA.jsp / pjm_work_view.jsp / go=write)를 흉내 내고,
# 사이트에서는 일부러 만들 수 없는 상황(저장 잘림, 기록 실패, 세션 만료, 로그인 차단, 무응답)을 만든다.
# 실제 사이트에는 닿지 않는다 - 앱은 FILECRYPT_NETCUS_MOCK 가 있을 때 www.netcus.com 을 127.0.0.1 로 돌린다.

Start-Test -Tag ncmock -Title '근태관리 목업 사이트 (올리기·가져오기 전체 흐름)' -Pad 52
if (-not (Test-Path -LiteralPath $EXE)) { Ok 'gui 빌드 있음' $false $EXE; Complete-Test }

. (Join-Path $ROOTDIR 'tools\netcus-mock\Import-NetcusMock.ps1')
$mock = New-Object FileCryptMock.NetcusMock
$ID = 'tester'; $PW = 'T3st!pw'
$mock.AddUser($ID, $PW)
$mock.Start(0)
$env:FILECRYPT_NETCUS_MOCK = [string]$mock.Port
Note ('목업 https://www.netcus.com -> 127.0.0.1:{0}' -f $mock.Port)

$ACC = @{ id = $ID; pw = $PW }
function Run([hashtable]$sc) {
    $p = Join-Path $WORK ('sc_' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
    if (-not $sc.ContainsKey('account')) { $sc.account = $ACC }
    if (-not $sc.ContainsKey('submitTimeoutSec')) { $sc.submitTimeoutSec = 30 }
    $sc.result = $p + '.result.json'
    [IO.File]::WriteAllText($p, ($sc | ConvertTo-Json -Depth 6), $u8n)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $pr = Start-Process -FilePath $EXE -ArgumentList '--netcus-selftest', ('"' + $p + '"') -PassThru -Wait -WindowStyle Hidden
    $r = $null
    if (Test-Path -LiteralPath $sc.result) { $r = Get-Content -LiteralPath $sc.result -Raw -Encoding UTF8 | ConvertFrom-Json }
    if ($null -eq $r) { $r = [pscustomobject]@{ error = 'no-result' } }
    $r | Add-Member -NotePropertyName exitCode -NotePropertyValue $pr.ExitCode -Force
    $r | Add-Member -NotePropertyName seconds -NotePropertyValue ([Math]::Round($sw.Elapsed.TotalSeconds, 1)) -Force
    return $r
}
function RandFile([string]$name, [int]$bytes, [int]$seed) {
    $b = New-Object byte[] $bytes; (New-Object System.Random $seed).NextBytes($b)
    $f = Join-Path $WORK $name; [IO.File]::WriteAllBytes($f, $b); return $f
}
function Day([string]$d) { return $mock.GetDay($ID, [datetime]$d) }
function Strip([string]$s) { return ($s -replace '\s', '') }

try {
    # ================================================================ 0) 안전장치
    $keep = $env:FILECRYPT_NETCUS_MOCK; $env:FILECRYPT_NETCUS_MOCK = ''
    $r0 = Run @{ op = 'login' }
    $env:FILECRYPT_NETCUS_MOCK = $keep
    Ok '목업 포트 없이는 자가 테스트를 거부' (($r0.exitCode -eq 3) -and ($r0.exception -match 'FILECRYPT_NETCUS_MOCK')) ('exit=' + $r0.exitCode)
    Ok '  그때 로그인 시도조차 없음' ($mock.LoginPosts -eq 0) ('로그인 {0}회' -f $mock.LoginPosts)

    # ================================================================ 1) 로그인
    $r1 = Run @{ op = 'login' }
    Ok '로그인 (login.htm -> loginRSA.jsp -> 세션)' ($r1.login -eq $true) ('{0}s' -f $r1.seconds)
    Ok '  비밀번호는 암호화 칸으로만 감' ($mock.LoginFailures -eq 0) ''

    $r1b = Run @{ op = 'login'; account = @{ id = $ID; pw = 'wrong-pw' } }
    Ok '틀린 비밀번호 -> 로그인 실패로 판정' ($r1b.login -eq $false) ('실패 {0}회' -f $mock.LoginFailures)
    $r1c = Run @{ op = 'login' }   # 맞는 비밀번호로 다시 맞춰 둔다(자격증명 파일 갱신)
    Ok '  맞는 비밀번호로 다시 로그인' ($r1c.login -eq $true) ''

    # ================================================================ 2) 여러 조각 올리기
    $f2 = RandFile 'multi.bin' 6000 1
    $logins0 = $mock.LoginPosts
    $r2 = Run @{ op = 'upload'; files = @($f2); start = '2024-08-01'; chunk = 1000 }
    $n2 = [int]$r2.total
    Ok '여러 조각 올리기 -> 완료' ($r2.outcome -eq 'Done') ('{0}조각 / {1}s' -f $n2, $r2.seconds)
    Ok '  전부 기록' (($r2.written -eq $n2) -and ($n2 -ge 5)) ('기록 {0}' -f $r2.written)
    Ok '  다시 읽어 날짜마다 일치' ($r2.verified -eq $n2) ('확인 {0}' -f $r2.verified)
    Ok '  모아서 원본 컨테이너와 같음' ($r2.roundTrip -eq $true) ''
    $same = 0
    for ($i = 0; $i -lt $n2; $i++) {
        $d = ([datetime]'2024-08-01').AddDays($i).ToString('yyyy-MM-dd')
        if ((Strip (Day $d).Content) -eq (Strip $r2.slotTexts[$i])) { $same++ }
    }
    Ok '  목업에 실제로 저장된 내용 = 올린 조각' ($same -eq $n2) ('{0}/{1}' -f $same, $n2)
    Ok '  날짜마다 로그인하지 않음(세션 재사용)' (($mock.LoginPosts - $logins0) -le 3) ('{0}일에 로그인 {1}회' -f $n2, ($mock.LoginPosts - $logins0))

    # ================================================================ 3) 가져오기 + 지우기
    $out3 = Join-Path $WORK 'down3'
    $r3 = Run @{ op = 'download'; start = '2024-08-01'; days = $n2; outDir = $out3; clear = $true }
    $got = @($r3.written)
    Ok '가져오기 -> 원본 복원' (($r3.okCount -eq 1) -and ($got.Count -eq 1) -and ((ShaFile $got[0]) -eq (ShaFile $f2))) ('{0}s' -f $r3.seconds)
    Ok '  지우기: 전부 비움 확인' (($r3.clearTargets -eq $n2) -and ($r3.cleared -eq $n2)) ('{0}/{1}' -f $r3.cleared, $r3.clearTargets)
    # 비운 뒤 NetcusService 가 "내용이 있나" 를 끝까지(14×300ms) 기다리던 것 - 날짜당 4초 넘게 버렸다.
    Ok '  비우기가 날짜마다 헛기다리지 않음' ($r3.seconds -lt (10 + $n2 * 2)) ('{0}일 가져오기+비우기 {1}s' -f $n2, $r3.seconds)
    $empty = 0
    for ($i = 0; $i -lt $n2; $i++) { if ((Day ([datetime]'2024-08-01').AddDays($i).ToString('yyyy-MM-dd')).Content.Trim().Length -eq 0) { $empty++ } }
    Ok '  목업에서도 실제로 비어 있음' ($empty -eq $n2) ('{0}/{1}' -f $empty, $n2)

    # ================================================================ 4) 31일 넘는 범위 (나눠 읽기)
    $f4 = RandFile 'long.bin' 30000 4
    $r4 = Run @{ op = 'upload'; files = @($f4); start = '2024-06-01'; chunk = 1000 }
    $n4 = [int]$r4.total
    Ok '31일 넘게 올리기 (기존 내용 확인도 나눠 읽음)' (($r4.outcome -eq 'Done') -and ($n4 -gt 31) -and $r4.roundTrip) ('{0}일 / {1}s' -f $n4, $r4.seconds)
    $out4 = Join-Path $WORK 'down4'
    $r4b = Run @{ op = 'download'; start = '2024-06-01'; days = [Math]::Min(60, $n4 + 3); outDir = $out4 }
    $g4 = @($r4b.written)
    Ok '  31일 넘게 가져오기 -> 원본 복원' (($r4b.okCount -eq 1) -and ((ShaFile $g4[0]) -eq (ShaFile $f4))) ('{0}일 / {1}s' -f ([Math]::Min(60, $n4 + 3)), $r4b.seconds)

    # ================================================================ 5) 이미 내용이 있는 날짜
    $mock.SetDay($ID, [datetime]'2024-09-02', '6', 0, '원래 보고 내용 - 지우면 안 됨')
    $mock.SetDay($ID, [datetime]'2024-09-03', '2', 3, '야근 +3시간 인 날')
    $f5 = RandFile 'occ.bin' 2500 5
    $w5 = $mock.WriteLog.Count
    $r5 = Run @{ op = 'upload'; files = @($f5); start = '2024-09-01'; chunk = 1000; confirmOverwrite = $false }
    Ok '덮어쓰기를 거절 -> 아무것도 올리지 않음' (($r5.outcome -eq 'Cancelled') -and ($mock.WriteLog.Count -eq $w5)) ('기록 {0}건' -f ($mock.WriteLog.Count - $w5))
    Ok '  기존 내용 그대로' ((Day '2024-09-02').Content -eq '원래 보고 내용 - 지우면 안 됨') ''
    $r5b = Run @{ op = 'upload'; files = @($f5); start = '2024-09-01'; chunk = 1000; confirmOverwrite = $true }
    Ok '덮어쓰기를 허락 -> 올리고 확인' (($r5b.outcome -eq 'Done') -and $r5b.roundTrip) ''
    Ok '  덮어쓰기 전 내용을 백업' (($r5b.backupFile) -and ((Get-Content -LiteralPath $r5b.backupFile -Raw -Encoding UTF8) -match '원래 보고 내용')) ''
    Ok '  근태(휴가)는 그대로' ((Day '2024-09-02').Status -eq '6') ('status={0}' -f (Day '2024-09-02').Status)
    $d3 = Day '2024-09-03'
    Ok '  야근 +3시간 이던 날: 근태·초과시간 그대로' (($d3.Status -eq '2') -and ($d3.Overtime -eq 3)) ('근태={0} 초과={1}' -f $d3.Status, $d3.Overtime)
    Note ('빈 날짜의 근태는 {0} 으로 기록됨 (사이트가 근태 없이는 저장을 받지 않아 NetcusService 가 정근=1 을 싣는다)' -f (Day '2024-09-01').Status)

    # ================================================================ 6) 중간에 실패 -> 같은 창에서 다시 올리기
    $f6 = RandFile 'resume.bin' 4000 6
    [void]$mock.DropWritesOnceOn.Add('2024-10-03')
    $r6 = Run @{ op = 'upload'; files = @($f6); start = '2024-10-01'; chunk = 1000; attempts = 2 }
    $a6 = @($r6.attempts)
    Ok '3번째 날 기록 실패 -> 그 자리에서 멈춤' (($a6.Count -eq 2) -and ($a6[0].outcome -eq 'Aborted') -and ($a6[0].written -eq 2)) ('첫 시도 기록 {0}' -f $a6[0].written)
    Ok '  다시 올리기: 이미 올라간 2일은 건너뜀' (($a6[1].outcome -eq 'Done') -and ($a6[1].skipped -eq 2)) ('건너뜀 {0}' -f $a6[1].skipped)
    Ok '  결과는 원본과 같음' ($a6[1].roundTrip -eq $true) ''

    # ================================================================ 7) 사이트가 글을 잘라 저장
    $mock.TruncateContentAt = 500
    $f7 = RandFile 'trunc.bin' 2500 7
    $r7 = Run @{ op = 'upload'; files = @($f7); start = '2024-11-01'; chunk = 1000 }
    $mock.TruncateContentAt = 0
    Ok '잘려 저장됨 -> 저장 직후 검증은 통과하지만' ($r7.outcome -eq 'Done') ('기록 {0}' -f $r7.written)
    # 500자보다 짧은 마지막 조각만 온전하다. 나머지는 잘렸으니 일치하지 않아야 한다.
    Ok '  다시 읽는 확인이 잡아냄 (잘린 날짜 불일치, 원본과 다름)' (($r7.verified -lt $r7.total) -and ($r7.roundTrip -eq $false)) ('확인 {0}/{1}' -f $r7.verified, $r7.total)

    # ================================================================ 8) 읽는 도중 세션이 풀림
    $f8 = RandFile 'sess.bin' 4000 8
    $r8a = Run @{ op = 'upload'; files = @($f8); start = '2024-12-01'; chunk = 1000 }
    $mock.DropSessions(); $mock.ExpireSessionAfter = 3
    $r8 = Run @{ op = 'download'; start = '2024-12-01'; days = [int]$r8a.total; outDir = (Join-Path $WORK 'down8') }
    $mock.ExpireSessionAfter = -1
    Ok '읽다 세션 만료 -> 빈 칸으로 착각하지 않고 멈춤' (($r8.error -eq 'exception') -and ($r8.exception -match '로그인이 풀렸')) $r8.exception

    # ================================================================ 9) 로그인이 막힘
    $mock.DropSessions(); $mock.BlockLoginsAfter = $mock.LoginPosts
    $w9 = $mock.WriteLog.Count
    $r9 = Run @{ op = 'upload'; files = @($f8); start = '2025-01-01'; chunk = 1000 }
    $mock.BlockLoginsAfter = -1
    Ok '로그인 차단 -> 실패로 끝나고 아무것도 쓰지 않음' (($r9.error -eq 'exception') -and ($r9.exception -match '로그인') -and ($mock.WriteLog.Count -eq $w9)) $r9.exception

    # ================================================================ 10) 사이트 무응답
    $mock.HangOnPath = 'pjm_work_view.jsp'
    $r10 = Run @{ op = 'download'; start = '2024-12-01'; days = 2; outDir = (Join-Path $WORK 'down10'); readBaseSec = 5; readPerDaySec = 1 }
    $mock.HangOnPath = $null
    Ok '무응답 -> 시간 제한으로 끝남 (영원히 기다리지 않음)' (($r10.error -eq 'exception') -and ($r10.exception -match '응답이 없') -and ($r10.seconds -lt 60)) ('{0}s · {1}' -f $r10.seconds, $r10.exception)

    Note ('목업 요청 {0}건 · 로그인 POST {1}회(실패 {2}) · 기록 {3}건' -f $mock.Requests, $mock.LoginPosts, $mock.LoginFailures, $mock.WriteLog.Count)
}
finally {
    $mock.Dispose()
    $env:FILECRYPT_NETCUS_MOCK = ''
}
Complete-Test

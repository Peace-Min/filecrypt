. (Join-Path (Split-Path $PSScriptRoot -Parent) '_common.ps1')

# 파일 목록 기능(경로 입력 · 정렬 · 폴더 트리 · 아이콘)을 실제 창에서 누르며 확인한다.
# 기획: docs/specs/파일-목록-기능.md  ·  QA: docs/qa/파일-목록-QA.md (각 항목 이름 앞의 [FR-x] 가 기획 번호)
#
# UI 자동화(UIAutomation) + 키 입력(SendKeys)을 쓴다. 앱 창이 포커스를 가져가므로 도는 동안 입력하지 말 것.
# run-all.ps1 -UI 일 때만 돈다. 설정은 격리 폴더, 탐색기는 열지 않는다(FILECRYPT_NO_EXPLORER).

Start-Test -Tag uilist -Title 'UI: 파일 목록 (경로 입력·정렬·폴더 트리·아이콘)' -Pad 58
Import-FileCrypt
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class UiWin {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
}
'@
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$env:FILECRYPT_NO_EXPLORER = '1'

# ---------------------------------------------------------------- 표본
$proj = Join-Path $WORK '프로젝트'
foreach ($d in 'src\bin', 'doc') { New-Item -ItemType Directory -Force (Join-Path $proj $d) | Out-Null }
function Put([string]$rel, [int]$bytes) { $b = New-Object byte[] $bytes; (New-Object Random $bytes).NextBytes($b); [IO.File]::WriteAllBytes((Join-Path $proj $rel), $b) }
Put 'README.md' 1; Put 'src\App.cs' 3000; Put 'src\Util.cs' 50; Put 'doc\설계.docx' 400; Put 'doc\notes.txt' 20
Copy-Item "$env:WINDIR\System32\notepad.exe" (Join-Path $proj 'src\bin\app.exe')          # 파일마다 아이콘이 있는 종류
$other = Join-Path $WORK '따로'; New-Item -ItemType Directory -Force $other | Out-Null
[IO.File]::WriteAllText((Join-Path $other '추가.txt'), '추가')
$outDir = Join-Path $WORK 'out'; New-Item -ItemType Directory -Force $outDir | Out-Null

# ---------------------------------------------------------------- 도우미
$app = Start-Process -FilePath $EXE -PassThru
$deadline = (Get-Date).AddSeconds(15)
while ($app.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200; $app.Refresh() }
[void][UiWin]::ShowWindow($app.MainWindowHandle, 9); [void][UiWin]::MoveWindow($app.MainWindowHandle, 40, 40, 1000, 720, $true)
$win = $AE::FromHandle($app.MainWindowHandle)

function Find([string]$id) { $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function Text([string]$id) { $e = Find $id; if ($e) { $e.Current.Name } else { $null } }
function Click($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function SetText([string]$id, [string]$v) { (Find $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function BoxText { (Find 'TxtPaths').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Keys($target, [string]$keys) {
    # target: AutomationId 또는 요소. 창을 앞으로 가져온 뒤 그 요소에 포커스를 주고 키를 보낸다.
    $el = if ($target -is [string]) { Find $target } else { $target }
    [void][UiWin]::SetForegroundWindow($app.MainWindowHandle); $el.SetFocus(); Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait($keys)
}
function WaitFor([scriptblock]$cond, [int]$ms = 8000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) { if (& $cond) { return $sw.ElapsedMilliseconds }; Start-Sleep -Milliseconds 100 }
    return -1
}
function Header([string]$prefix) {
    $all = $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::HeaderItem)))
    foreach ($h in $all) { if ($h.Current.Name.StartsWith($prefix)) { return $h } }
}
# 목록 행: 각 행의 글자들(상태, 파일, 확장자, 크기, 위치) 순서대로
function Rows {
    $lv = Find 'LvItems'
    $items = $lv.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::DataItem)))
    foreach ($it in $items) {
        $texts = $it.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)))
        ,@($texts | ForEach-Object { $_.Current.Name })
    }
}
function RowNames { @(Rows | ForEach-Object { Split-Path ($_[1]) -Leaf }) }
function TreeNodes {
    $tv = Find 'TvFolders'
    if (-not $tv) { return @() }
    $nodes = $tv.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::TreeItem)))
    foreach ($n in $nodes) {
        $cb = $n.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::CheckBox)))
        $tx = $n.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)))
        [pscustomobject]@{ Label = $tx.Current.Name; Box = $cb; Item = $n
                           State = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString() }
    }
}
function Node([string]$labelPrefix) { TreeNodes | Where-Object { $_.Label.StartsWith($labelPrefix) } | Select-Object -First 1 }
function Toggle($node) { $node.Box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 300 }
function States { (TreeNodes | ForEach-Object { '{0}={1}' -f ($_.Label -replace '\s+\(\d+\)$', ''), $_.State }) -join ' ' }

try {
    # ================================================================ 시작
    Ok '[FR-3.1] 목록이 비면 폴더 패널 없음' ($null -eq (Find 'TvFolders')) ''
    Ok '[FR-1.1] 경로 칸이 비면 입력 예시가 보임' ((Text 'TxtPathsHint') -match '절대 경로') (Text 'TxtPathsHint')

    # ================================================================ 경로로 추가 (Enter)
    SetText 'TxtPaths' $proj
    Keys 'TxtPaths' '{ENTER}'
    $t = WaitFor { (Text 'TxtCount') -like '6개*' }
    Ok '[FR-1.2][FR-1.6] 폴더 경로 + Enter -> 하위 파일 6개 추가' ($t -ge 0) (Text 'TxtCount')
    Ok '  추가 뒤 칸이 비고 예시가 다시 보임' (((BoxText) -eq '') -and ((Text 'TxtPathsHint') -ne $null)) ''

    # ================================================================ 폴더 트리 모양
    $tn = @(TreeNodes)
    Ok '[FR-3.1] 파일을 넣으면 폴더 패널이 보임' ($tn.Count -gt 0) ('노드 {0}개' -f $tn.Count)
    Ok '[FR-3.2][FR-3.3] 맨 위는 "프로젝트 (6)" (앞의 공통 경로는 건너뜀)' ($tn[0].Label -match '^프로젝트\s+\(6\)$') $tn[0].Label
    Ok '[FR-3.3] 하위 폴더는 이름순 (doc, src, bin)' ((($tn | Select-Object -Skip 1 | ForEach-Object { $_.Label -replace '\s+\(\d+\)$', '' }) -join ',') -eq 'doc,src,bin') (States)
    Ok '  처음엔 전부 켜짐' (@($tn | Where-Object { $_.State -ne 'On' }).Count -eq 0) (States)
    $tip = $tn[0].Item.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)))
    Ok '[FR-3.3] 노드에 마우스를 올리면 전체 경로(툴팁 = 실제 경로)' ($tn[0].Item.Current.HelpText -eq $proj -or (Find 'TvFolders').FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::HelpTextProperty, $proj))) -ne $null) $proj

    # ================================================================ 아이콘
    $treeImgs = (Find 'TvFolders').FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Image)))
    Ok '[FR-4.1] 트리 노드마다 폴더 아이콘' ($treeImgs.Count -eq $tn.Count) ('{0}/{1}' -f $treeImgs.Count, $tn.Count)
    $rowItems = (Find 'LvItems').FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::DataItem)))
    $withImg = 0
    foreach ($ri in $rowItems) { if ($ri.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Image)))) { $withImg++ } }
    Ok '[FR-4.2] 목록 행마다 파일 종류 아이콘' ($withImg -eq $rowItems.Count) ('{0}/{1}' -f $withImg, $rowItems.Count)

    # ================================================================ 정렬
    Click (Header '확장자'); Start-Sleep -Milliseconds 300
    $ext = @(Rows | ForEach-Object { $_[2] })
    Ok '[FR-2.1][FR-2.2] 확장자 머리글 -> 오름차순' (($ext -join ',') -eq 'cs,cs,docx,exe,md,txt') ($ext -join ',')
    Ok '  같은 확장자끼리는 이름순 (App.cs, Util.cs)' (((RowNames)[0] -eq 'App.cs') -and ((RowNames)[1] -eq 'Util.cs')) ((RowNames) -join ',')
    Ok '[FR-2.3] 머리글에 ▲' ((Header '확장자').Current.Name -eq '확장자 ▲') (Header '확장자').Current.Name
    Click (Header '확장자'); Start-Sleep -Milliseconds 300
    $ext2 = @(Rows | ForEach-Object { $_[2] })
    Ok '[FR-2.2][FR-2.3] 다시 누르면 내림차순 ▼' ((($ext2 -join ',') -eq 'txt,md,exe,docx,cs,cs') -and ((Header '확장자').Current.Name -eq '확장자 ▼')) ($ext2 -join ',')
    Click (Header '크기'); Start-Sleep -Milliseconds 300
    Ok '[FR-2.4] 크기는 바이트로 비교 (1 B < 20 B < 50 B < 400 B < 3 KB < exe)' (((RowNames) -join ',') -eq 'README.md,notes.txt,Util.cs,설계.docx,App.cs,app.exe') ((RowNames) -join ',')
    Ok '  다른 머리글로 바꾸면 화살표도 옮겨감' (((Header '크기').Current.Name -eq '크기 ▲') -and ((Header '확장자').Current.Name -eq '확장자')) ''

    # ================================================================ 폴더 끄기
    Toggle (Node 'bin')
    Ok '[FR-3.4][FR-3.5] bin 끄기 -> bin ☐, src·맨 위 ■(섞임)' ((States) -eq '프로젝트=Indeterminate doc=On src=Indeterminate bin=Off') (States)
    $exeRow = Rows | Where-Object { $_[1] -like '*app.exe' } | Select-Object -First 1
    Ok '[FR-3.6] bin 의 파일은 목록에 남고 상태 "제외"' ($exeRow -and $exeRow[0] -eq '제외') ($exeRow -join ' | ')
    Ok '[FR-3.7] 파일 수: "6개 · 처리 5개 (제외 1개)"' ((Text 'TxtCount') -eq '6개 · 처리 5개 (제외 1개)') (Text 'TxtCount')
    Ok '[FR-3.7] 계획 줄도 5개 기준' ((Text 'TxtPlan') -like '파일 5개*') (Text 'TxtPlan')

    Toggle (Node 'src')
    Ok '[FR-3.5] 섞임(■)인 src 를 누르면 하위까지 전부 켜짐' ((States) -eq '프로젝트=On doc=On src=On bin=On') (States)
    Toggle (Node 'src')
    Ok '[FR-3.4] src 끄기 -> src 와 하위 bin 까지 꺼짐' (((States) -eq '프로젝트=Indeterminate doc=On src=Off bin=Off') -and ((Text 'TxtCount') -eq '6개 · 처리 3개 (제외 3개)')) ('{0} / {1}' -f (States), (Text 'TxtCount'))
    Toggle (Node 'src'); Toggle (Node 'bin')   # src 켜고 bin 만 다시 끈다

    # 키보드
    (Node 'doc').Item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Keys (Node 'doc').Item ' '
    Start-Sleep -Milliseconds 300
    Ok '[FR-3.12] 트리에서 doc 고르고 Space -> doc 꺼짐' (((Node 'doc').State -eq 'Off') -and ((Text 'TxtCount') -eq '6개 · 처리 3개 (제외 3개)')) ('{0} / {1}' -f (States), (Text 'TxtCount'))
    Keys (Node 'doc').Item ' '; Start-Sleep -Milliseconds 300
    Ok '  Space 다시 -> doc 켜짐' ((Node 'doc').State -eq 'On') (States)

    # 모두 / 해제
    Click ($win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, '해제'))))))
    Start-Sleep -Milliseconds 300
    Ok '[FR-3.9][FR-3.8] [해제] -> 전부 꺼짐, 실행 버튼 꺼짐, 안내 문구' ((-not (Find 'BtnRun').Current.IsEnabled) -and ((Text 'TxtPlan') -match '체크하세요') -and ((States) -eq '프로젝트=Off doc=Off src=Off bin=Off')) (Text 'TxtPlan')
    Ok '  근태관리로 올리기도 꺼짐' (-not (Find 'BtnNetcus').Current.IsEnabled) ''
    Click ($win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, '모두'))))))
    Start-Sleep -Milliseconds 300
    Ok '[FR-3.9] [모두] -> 전부 켜짐, 실행 버튼 켜짐' (((States) -eq '프로젝트=On doc=On src=On bin=On') -and (Find 'BtnRun').Current.IsEnabled) (States)
    Toggle (Node 'bin')

    # ================================================================ 경로 입력: 예외
    SetText 'TxtPaths' ('"' + (Join-Path $other '추가.txt') + '"')
    Click (Find 'BtnAddPaths')
    $t = WaitFor { (Text 'TxtCount') -like '7개*' }
    Ok '[FR-1.3] 따옴표로 감싼 경로 + [경로 추가]' ($t -ge 0) (Text 'TxtCount')
    Ok '[FR-3.10] 파일을 더 넣어 트리가 다시 만들어져도 bin 은 꺼진 채' (((Node 'bin').State -eq 'Off') -and ((Text 'TxtCount') -eq '7개 · 처리 6개 (제외 1개)')) ('{0} / {1}' -f (States), (Text 'TxtCount'))
    Ok '[FR-2.5] 나중에 넣은 파일도 정렬된 자리에 (크기순: 추가.txt 6 B 는 두 번째)' ((RowNames)[1] -eq '추가.txt') ((RowNames) -join ',')

    $missing = Join-Path $WORK '없는 파일.txt'
    SetText 'TxtPaths' ($missing + "`r`n" + (Join-Path $proj 'README.md'))
    Click (Find 'BtnAddPaths'); Start-Sleep -Milliseconds 800
    Ok '[FR-1.7] 없는 경로 -> 상태줄에 이유, 칸에는 그것만 남음' (((Text 'TxtStatus') -match '없는 경로 1개') -and ((BoxText) -eq $missing)) (Text 'TxtStatus')
    Ok '[FR-1.6] 이미 있는 파일(README.md)은 다시 넣지 않음' ((Text 'TxtCount') -like '7개*') (Text 'TxtCount')
    SetText 'TxtPaths' 'abc.txt'
    Click (Find 'BtnAddPaths'); Start-Sleep -Milliseconds 500
    Ok '[FR-1.5] 상대 경로 -> "절대 경로가 아님"' ((Text 'TxtStatus') -match '절대 경로가 아님') (Text 'TxtStatus')
    SetText 'TxtPaths' ''
    Keys 'TxtPaths' 'C:\a+{ENTER}C:\b'
    Start-Sleep -Milliseconds 300
    Ok '[FR-1.2] Shift+Enter 는 줄바꿈(추가하지 않음)' (((BoxText) -match "C:\\a\r?\nC:\\b") -and ((Text 'TxtCount') -like '7개*')) ((BoxText) -replace "`r?`n", ' ⏎ ')
    SetText 'TxtPaths' ''

    # ================================================================ 실제로 묶으면 제외가 빠지는가
    $clip = Find 'ChkClipboard'
    if ($clip.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') {
        $clip.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()   # 시험이 사용자의 클립보드를 덮지 않게
    }
    SetText 'TxtOutDir' $outDir
    Click (Find 'BtnRun')
    $busy = -not (Find 'TxtPaths').Current.IsEnabled
    $t = WaitFor { @(Get-ChildItem -LiteralPath $outDir -Filter '*.txt').Count -gt 0 -and (Find 'BtnRun').Current.IsEnabled } 20000
    $made = @(Get-ChildItem -LiteralPath $outDir -Filter '*.txt')
    Ok '[FR-3.7] [텍스트로 만들기] -> 결과 파일 1개' (($t -ge 0) -and ($made.Count -eq 1)) ($made | ForEach-Object Name)
    $names = @()
    if ($made.Count -eq 1) {
        foreach ($c in [FileCrypt.FileCryptCore]::ExtractBlocks([IO.File]::ReadAllText($made[0].FullName))) {
            foreach ($df in [FileCrypt.FileCryptCore]::DecryptAll($c)) { $names += (Split-Path $df.FileName -Leaf) }
        }
    }
    Ok '[FR-3.6][FR-3.7] 결과에는 켜진 6개만 - bin\app.exe 는 없음' (($names.Count -eq 6) -and ($names -notcontains 'app.exe') -and ($names -contains '추가.txt')) (($names | Sort-Object) -join ',')
    Note ('실행 직후 경로 칸 잠김: {0} (작업이 매우 짧으면 잠김을 못 볼 수 있다 - 아래 2,000개 시험에서 다시 본다)' -f $busy)

    # ================================================================ 전체 삭제 / 복원 탭
    Click (Find 'BtnClear'); Start-Sleep -Milliseconds 400
    Ok '[FR-3.1] [전체 삭제] -> 폴더 패널 숨김' ($null -eq (Find 'TvFolders')) ''
    SetText 'TxtPaths' $proj; Click (Find 'BtnAddPaths')
    [void](WaitFor { (Text 'TxtCount') -like '6개*' })
    Ok '[FR-3.10] [전체 삭제] 뒤 다시 넣으면 꺼 둔 폴더를 잊음(bin 켜짐)' ((Node 'bin').State -eq 'On') (States)
    (Find 'RbDec').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 400
    Ok '[FR-3.1] 복원 탭에는 폴더 패널 없음' ($null -eq (Find 'TvFolders')) ''
    (Find 'RbEnc').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 400
    Ok '  묶기 탭으로 돌아오면 다시 보이고 정렬도 그대로' (((Find 'TvFolders') -ne $null) -and ((Header '크기').Current.Name -eq '크기 ▲')) ''

    # ================================================================ 많이 넣었을 때 (NFR-1)
    Click (Find 'BtnClear'); Start-Sleep -Milliseconds 300
    $big = Join-Path $WORK '대량'
    for ($i = 0; $i -lt 20; $i++) {
        $d = Join-Path $big ('폴더{0:D2}' -f $i); New-Item -ItemType Directory -Force $d | Out-Null
        for ($j = 0; $j -lt 100; $j++) { [IO.File]::WriteAllText((Join-Path $d ('f{0:D3}.{1}' -f $j, @('cs', 'txt', 'md', 'json')[$j % 4])), ('x' * ($j + 1))) }
    }
    SetText 'TxtPaths' $big
    $sw = [Diagnostics.Stopwatch]::StartNew(); Click (Find 'BtnAddPaths')
    $t = WaitFor { (Text 'TxtCount') -like '2000개*' } 30000
    Ok '[NFR-1] 2,000개 넣기 (폴더 훑기 + 판별 + 트리)' (($t -ge 0) -and ($sw.ElapsedMilliseconds -lt 10000)) ('{0:N1}s' -f ($sw.ElapsedMilliseconds / 1000))
    $sw.Restart(); Toggle (Node '폴더07')
    $t = WaitFor { (Text 'TxtCount') -eq '2000개 · 처리 1900개 (제외 100개)' } 5000
    Ok '[NFR-1] 2,000개에서 폴더 하나 끄기' (($t -ge 0) -and ($sw.ElapsedMilliseconds -lt 1500)) ('{0} ms · {1}' -f $sw.ElapsedMilliseconds, (Text 'TxtCount'))
    $sw.Restart(); Click (Header '확장자')
    Ok '[NFR-1] 2,000개 정렬' ($sw.ElapsedMilliseconds -lt 1500) ('{0} ms' -f $sw.ElapsedMilliseconds)

    # 작업 중 잠금: 2,000개를 묶는 동안 경로 칸·트리가 잠기는가
    Get-ChildItem -LiteralPath $outDir | Remove-Item -Force
    Click (Find 'BtnRun')
    $locked = WaitFor { (-not (Find 'TxtPaths').Current.IsEnabled) -and (-not (Find 'TvFolders').Current.IsEnabled) } 3000
    Ok '[FR-1.8][FR-3.11] 작업 중에는 경로 칸·트리 잠김' ($locked -ge 0) $(if ($locked -ge 0) { '잠김 확인' } else { '작업이 너무 빨라 못 봄' })
    [void](WaitFor { (Find 'BtnRun').Current.IsEnabled } 20000)
    Ok '  끝나면 다시 풀림' ((Find 'TxtPaths').Current.IsEnabled -and (Find 'TvFolders').Current.IsEnabled) ''
}
catch {
    # 예상 못 한 오류로 중간에 멈추면 남은 항목을 못 본 것이다 - 실패로 센다(통과로 끝나지 않게).
    Ok ('시험 도중 오류로 중단: ' + $_.Exception.Message) $false ($_.InvocationInfo.PositionMessage -split "`n")[0]
}
finally {
    try { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } catch { }
    $env:FILECRYPT_NO_EXPLORER = ''
}
Complete-Test

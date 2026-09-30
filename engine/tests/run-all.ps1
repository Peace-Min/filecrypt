# 테스트 스위트 전부 실행.
#   powershell -NoProfile -ExecutionPolicy Bypass -File run-all.ps1            # 전부
#   ... run-all.ps1 -Quick                                                      # test-limits(오래 걸림) 빼고
#   ... run-all.ps1 -Only archive,split                                         # 골라서 (test- / .ps1 생략 가능)
#
# 스위트마다 따로 powershell.exe 를 띄운다. LoadFrom 으로 올린 exe/dll 은 프로세스가 끝날 때까지
# 잠기고, 스위트끼리 형식/변수가 섞이지 않게 하려는 것이다.
# 결과는 각 스위트의 요약 줄("##########  ...: n건 중 실패 f건 ##########")에서 읽는다.
[CmdletBinding()]
param(
    [switch]$Quick,
    [string[]]$Only
)

$ErrorActionPreference = 'Continue'
$here = $PSScriptRoot
$ps   = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'   # 64비트 5.1

$suites = @(Get-ChildItem -LiteralPath $here -Filter 'test-*.ps1' | Sort-Object Name)
if ($Only -and $Only.Count -gt 0) {
    $want = @{}
    foreach ($o in ($Only -split ',')) {
        $nm = $o.Trim()
        if (-not $nm) { continue }
        if ($nm.EndsWith('.ps1')) { $nm = $nm.Substring(0, $nm.Length - 4) }
        if (-not $nm.StartsWith('test-')) { $nm = 'test-' + $nm }
        $want[$nm.ToLowerInvariant()] = 1
    }
    $suites = @($suites | Where-Object { $want.ContainsKey($_.BaseName.ToLowerInvariant()) })
    $unknown = @($want.Keys | Where-Object { $k = $_; -not ($suites | Where-Object { $_.BaseName.ToLowerInvariant() -eq $k }) })
    if ($unknown.Count -gt 0) { Write-Host ('  없는 스위트: ' + ($unknown -join ', ')) -ForegroundColor Yellow }
}
if ($Quick) { $suites = @($suites | Where-Object { $_.BaseName -ne 'test-limits' }) }
if ($suites.Count -eq 0) { Write-Host '  실행할 스위트가 없습니다.' -ForegroundColor Red; exit 1 }

$log = Join-Path $env:TEMP ('fc_runall_{0}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$u8b = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($log, ('FileCrypt run-all {0}{1}' -f (Get-Date -Format 's'), "`r`n"), $u8b)

# 자식 출력의 한글(과 일본어 파일명)이 깨지지 않게 콘솔 코드페이지를 잠시 UTF-8 로. 끝나면 되돌린다.
$oldOut = $null
try { $oldOut = [Console]::OutputEncoding; [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { $oldOut = $null }

$rxSummary = [regex]'##########\s*(?<title>.+?):\s*(?<n>\d+)건 중 실패 (?<f>\d+)건\s*##########'
$rows = New-Object System.Collections.Generic.List[object]
$all = [Diagnostics.Stopwatch]::StartNew()

Write-Host ''
Write-Host ('########## FileCrypt 테스트 전체 실행 ({0}개{1}) ##########' -f $suites.Count, $(if ($Quick) { ', -Quick' } else { '' })) -ForegroundColor Cyan
try {
    foreach ($s in $suites) {
        Write-Host ('  {0,-24} ' -f $s.BaseName) -NoNewline
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $global:LASTEXITCODE = 0
        $out = @(& $ps -NoProfile -ExecutionPolicy Bypass -File $s.FullName 2>&1 | ForEach-Object { "$_" })
        $rc = $LASTEXITCODE
        $sec = $sw.Elapsed.TotalSeconds

        [System.IO.File]::AppendAllText($log, ("`r`n==================== {0} (rc={1}, {2:N1}s)`r`n" -f $s.Name, $rc, $sec) + ($out -join "`r`n") + "`r`n", $u8b)

        $passLines = @($out | Where-Object { $_ -match '^\s*\[PASS\]' }).Count
        $failLines = @($out | Where-Object { $_ -match '^\s*\[FAIL\]' })
        $m = $null
        foreach ($line in $out) { $mm = $rxSummary.Match($line); if ($mm.Success) { $m = $mm } }
        if ($m) {
            $cases = [int]$m.Groups['n'].Value
            $fails = [int]$m.Groups['f'].Value
            $note  = ''
        } else {
            # 요약 줄이 없다 = 중간에 죽었다. 찍힌 줄만이라도 센다.
            $cases = $passLines + $failLines.Count
            $fails = [Math]::Max(1, $failLines.Count)
            $note  = '요약 줄 없음(중단?)'
        }
        $bad = ($fails -gt 0) -or ($rc -ne 0) -or (-not $m)
        $rows.Add([pscustomobject]@{
            Suite = $s.BaseName; Cases = $cases; Pass = ($cases - $fails); Fail = $fails
            Sec = [Math]::Round($sec, 1); Exit = $rc; Bad = $bad; Note = $note
        })
        Write-Host ('{0,4}건  실패 {1,3}  {2,6:N1}s  rc={3}  {4}' -f $cases, $fails, $sec, $rc, $note) `
            -ForegroundColor $(if ($bad) { 'Red' } else { 'Green' })
        if ($bad) {
            foreach ($fl in ($failLines | Select-Object -First 15)) { Write-Host ('      ' + $fl.Trim()) -ForegroundColor Red }
            if (-not $m) {
                foreach ($tl in ($out | Select-Object -Last 8)) { Write-Host ('      | ' + $tl) -ForegroundColor DarkRed }
            }
        }
    }
} finally {
    if ($oldOut) { try { [Console]::OutputEncoding = $oldOut } catch { } }
}

# ---------------------------------------------------------------- 표
Write-Host ''
Write-Host ('  {0,-24} {1,6} {2,6} {3,6} {4,8} {5,5}' -f 'suite', 'cases', 'pass', 'fail', 'sec', 'exit')
Write-Host ('  ' + ('-' * 60))
foreach ($r in $rows) {
    Write-Host ('  {0,-24} {1,6} {2,6} {3,6} {4,8:N1} {5,5}  {6}' -f $r.Suite, $r.Cases, $r.Pass, $r.Fail, $r.Sec, $r.Exit, $r.Note) `
        -ForegroundColor $(if ($r.Bad) { 'Red' } else { 'Gray' })
}
Write-Host ('  ' + ('-' * 60))
$tCases = ($rows | Measure-Object Cases -Sum).Sum
$tFail  = ($rows | Measure-Object Fail -Sum).Sum
$badSuites = @($rows | Where-Object { $_.Bad })
Write-Host ('  {0,-24} {1,6} {2,6} {3,6} {4,8:N1}' -f ('total ({0})' -f $rows.Count), $tCases, ($tCases - $tFail), $tFail, $all.Elapsed.TotalSeconds) `
    -ForegroundColor $(if ($badSuites.Count -eq 0) { 'Green' } else { 'Red' })
Write-Host ''
if ($badSuites.Count -eq 0) {
    Write-Host ('########## 전체 통과: 스위트 {0}개 / {1}건 ##########' -f $rows.Count, $tCases) -ForegroundColor Green
} else {
    Write-Host ('########## 실패: 스위트 {0}개 ({1}) / 실패 {2}건 ##########' -f $badSuites.Count, (($badSuites | ForEach-Object { $_.Suite }) -join ', '), $tFail) -ForegroundColor Red
}
Write-Host ('  기록: {0}' -f $log) -ForegroundColor DarkGray
if ($badSuites.Count -gt 0) { exit 1 } else { exit 0 }

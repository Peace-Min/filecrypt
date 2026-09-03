$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$PS1  = Join-Path (Split-Path $PSScriptRoot -Parent) 'filecrypt.ps1'
$ROOT = Join-Path $env:TEMP 'fc_tests2'
if (Test-Path $ROOT) { Remove-Item $ROOT -Recurse -Force }
New-Item -ItemType Directory -Force $ROOT | Out-Null
$PW = 'P@ss 한글 #1!'
$fail = 0; $n = 0

function Invoke-FC([hashtable]$P) {
    $global:LASTEXITCODE = 0
    $out = & $PS1 @P *>&1
    return @{ Rc = $LASTEXITCODE; Out = (($out | Out-String).Trim()) }
}

function Check([string]$name, [string]$srcPath) {
    $script:n++
    # -Out 미지정: 기본 이름 규칙 (.enc 추가 / 제거) 경로까지 검증
    $e = Invoke-FC @{ Mode='Encrypt'; Path=$srcPath; Force=$true }
    if ($e.Rc -ne 0) {
        Write-Host ('  [FAIL] {0,-38} 암호화 rc={1}' -f $name, $e.Rc) -ForegroundColor Red
        Write-Host ('         {0}' -f ($e.Out -replace "`r?`n",' | ')) -ForegroundColor DarkRed
        $script:fail++; return
    }
    $encP = $srcPath + '.enc'
    if (-not (Test-Path -LiteralPath $encP)) {
        Write-Host ('  [FAIL] {0,-38} 기본 출력 파일이 없음' -f $name) -ForegroundColor Red
        $script:fail++; return
    }
    # 원본을 옆으로 치우고, 기본 이름 규칙으로 복원되게 함
    $keep = $srcPath + '.orig'
    Move-Item -LiteralPath $srcPath -Destination $keep -Force
    $d = Invoke-FC @{ Mode='Decrypt'; Path=$encP; Force=$true }
    if ($d.Rc -ne 0) {
        Write-Host ('  [FAIL] {0,-38} 복호화 rc={1}' -f $name, $d.Rc) -ForegroundColor Red
        Write-Host ('         {0}' -f ($d.Out -replace "`r?`n",' | ')) -ForegroundColor DarkRed
        $script:fail++; return
    }
    if (-not (Test-Path -LiteralPath $srcPath)) {
        Write-Host ('  [FAIL] {0,-38} 복원 파일이 원래 이름으로 안 나옴' -f $name) -ForegroundColor Red
        $script:fail++; return
    }
    $a = [System.IO.File]::ReadAllBytes($keep)
    $b = [System.IO.File]::ReadAllBytes($srcPath)
    $same = ($a.Length -eq $b.Length)
    if ($same) { for ($i=0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { $same=$false; break } } }
    if ($same) {
        Write-Host ('  [PASS] {0,-38} {1,12:N0} B  -> {2,10:N0} B' -f $name, $a.Length, (Get-Item -LiteralPath $encP).Length) -ForegroundColor Green
    } else {
        Write-Host ('  [FAIL] {0,-38} 바이트 불일치 (원본 {1}B / 복원 {2}B)' -f $name, $a.Length, $b.Length) -ForegroundColor Red
        $script:fail++
    }
}

Write-Host ''
Write-Host '########## 5. 기본 이름 규칙 + 까다로운 파일명 (-Out 미지정) ##########' -ForegroundColor Cyan

$u8n = New-Object System.Text.UTF8Encoding($false)
$mk = {
    param($name, $text)
    $p = Join-Path $ROOT $name
    [System.IO.File]::WriteAllText($p, $text, $u8n)
    return $p
}
Check '대괄호 [테스트] 포함'        (& $mk '보고서 [최종] v2.txt'      ("대괄호 파일명`n" * 50))
Check '와일드카드 * ? 유사 문자'     (& $mk "결과(1)+수정#2.txt"        ("특수문자 파일명`n" * 50))
Check '작은따옴표 포함'             (& $mk "it's a file.txt"          ("apostrophe`n" * 50))
Check '공백으로 시작/끝나는 이름'     (& $mk 'a  중간  공백  b.txt'      ("spaces`n" * 50))
Check '아주 긴 파일명 (150자)'       (& $mk (('가나다라마' * 30) + '.txt') ("long name`n" * 50))
Check '점으로 시작 (.gitignore 형)'  (& $mk '.hiddenfile'              ("hidden`n" * 50))
Check '대소문자 혼합 확장자'          (& $mk 'Report.XML'               ("<a>대소문자</a>`n" * 50))

Write-Host ''
Write-Host '########## 6. 실제 시스템 바이너리 라운드트립 ##########' -ForegroundColor Cyan
$bins = @(
    "$env:SystemRoot\System32\notepad.exe",
    "$env:SystemRoot\System32\shell32.dll",
    "$env:SystemRoot\System32\drivers\etc\hosts",
    "$env:SystemRoot\Web\Wallpaper\Windows\img0.jpg"
) | Where-Object { Test-Path -LiteralPath $_ }
foreach ($b in $bins) {
    $c = Join-Path $ROOT ([System.IO.Path]::GetFileName($b))
    Copy-Item -LiteralPath $b -Destination $c -Force
    Set-ItemProperty -LiteralPath $c -Name IsReadOnly -Value $false
    Check ('실파일: ' + [System.IO.Path]::GetFileName($b)) $c
}

Write-Host ''
Write-Host '########## 7. 하위 폴더 / 상대 경로 / 따옴표 입력 ##########' -ForegroundColor Cyan
$sub = Join-Path $ROOT '하위 폴더\깊은 경로'
New-Item -ItemType Directory -Force $sub | Out-Null
$deep = Join-Path $sub '깊은파일.xml'
[System.IO.File]::WriteAllText($deep, ("<x>깊은 경로 테스트</x>`n" * 200), $u8n)
Check '깊은 한글 경로' $deep

# 따옴표로 감싼 경로 (드래그&드롭 흉내)
$n++
$q = '"' + $deep + '"'
$e = Invoke-FC @{ Mode='Encrypt'; Path=$q; Out=(Join-Path $ROOT 'q.enc'); Force=$true }
$d = Invoke-FC @{ Mode='Decrypt'; Path=(Join-Path $ROOT 'q.enc'); Out=(Join-Path $ROOT 'q.out'); Force=$true }
$ok = ($e.Rc -eq 0 -and $d.Rc -eq 0 -and (Get-FileHash -LiteralPath $deep -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath (Join-Path $ROOT 'q.out') -Algorithm SHA256).Hash)
Write-Host ('  [{0}] 따옴표로 감싼 경로 입력 처리' -f $(if($ok){'PASS'}else{'FAIL'})) -ForegroundColor $(if($ok){'Green'}else{'Red'})
if (-not $ok) { $fail++ }

# 상대 경로
$n++
Push-Location $ROOT
$e = Invoke-FC @{ Mode='Encrypt'; Path='.\하위 폴더\깊은 경로\깊은파일.xml'; Out='.\rel.enc'; Force=$true }
$d = Invoke-FC @{ Mode='Decrypt'; Path='.\rel.enc'; Out='.\rel.out'; Force=$true }
Pop-Location
$ok = ($e.Rc -eq 0 -and $d.Rc -eq 0 -and (Get-FileHash -LiteralPath $deep -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath (Join-Path $ROOT 'rel.out') -Algorithm SHA256).Hash)
Write-Host ('  [{0}] 상대 경로 입력 처리' -f $(if($ok){'PASS'}else{'FAIL'})) -ForegroundColor $(if($ok){'Green'}else{'Red'})
if (-not $ok) { $fail++ }

Write-Host ''
Write-Host '########## 8. 덮어쓰기 회피 (-Force 없이) ##########' -ForegroundColor Cyan
$ov = Join-Path $ROOT 'ov.txt'
[System.IO.File]::WriteAllText($ov, "overwrite test`n", $u8n)
Invoke-FC @{ Mode='Encrypt'; Path=$ov } | Out-Null
Invoke-FC @{ Mode='Encrypt'; Path=$ov } | Out-Null
Invoke-FC @{ Mode='Encrypt'; Path=$ov } | Out-Null
$made = Get-ChildItem -LiteralPath $ROOT -Filter 'ov.txt*' | Select-Object -ExpandProperty Name
$ok = ($made -contains 'ov.txt.enc') -and ($made -contains 'ov.txt (1).enc') -and ($made -contains 'ov.txt (2).enc')
Write-Host ('  [{0}] 같은 이름 3회 암호화 -> {1}' -f $(if($ok){'PASS'}else{'FAIL'}), ($made -join ', ')) -ForegroundColor $(if($ok){'Green'}else{'Red'})
if (-not $ok) { $fail++ }
$n++

Write-Host ''
Write-Host ('########## 보충 테스트 결과: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })

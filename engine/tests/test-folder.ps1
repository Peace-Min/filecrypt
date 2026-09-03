$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ENGINE  = Join-Path $ROOTDIR 'engine\filecrypt.ps1'
$SIMPLE  = Join-Path $ROOTDIR 'engine\simple.ps1'
$EXE     = Join-Path $ROOTDIR 'gui\bin\Release\net48\FileCrypt.exe'
$KEY     = 'FileCrypt/default/v2/no-password'
$WORK    = Join-Path $env:TEMP ('fc_folder_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $WORK | Out-Null
$u8n = New-Object System.Text.UTF8Encoding($false)

$n = 0; $fail = 0
function Ok([string]$name, [bool]$cond, [string]$extra) {
    $script:n++
    if ($cond) { Write-Host ('  [PASS] {0}  {1}' -f $name.PadRight(48), $extra) -ForegroundColor Green }
    else       { Write-Host ('  [FAIL] {0}  {1}' -f $name.PadRight(48), $extra) -ForegroundColor Red; $script:fail++ }
}

Write-Host ''
Write-Host '########## 폴더 단위 처리 (구조 보존) ##########' -ForegroundColor Cyan

if (Test-Path -LiteralPath $EXE) {
    $exeCopy = Join-Path $WORK 'FileCrypt.exe'
    Copy-Item -LiteralPath $EXE -Destination $exeCopy -Force
    [void][Reflection.Assembly]::LoadFrom($exeCopy)
}

# ---------------------------------------------------------------- 표본 폴더
#   프로젝트/
#     README.md
#     src/App.cs
#     src/ui/MainWindow.xaml
#     src/ui/한글 뷰 [v2].xaml
#     doc/보고서/최종.txt
#   (src/ui/App.cs 와 src/App.cs 는 이름이 같다 -> 평평하게 풀면 충돌한다)
$proj = Join-Path $WORK '프로젝트'
$tree = @{
    'README.md'                   = "# 읽어보기`r`n"
    'src\App.cs'                  = "namespace A { class App { } }`r`n"
    'src\ui\App.cs'               = "namespace A.Ui { class App { } }`r`n"
    'src\ui\MainWindow.xaml'      = "<Window/>`r`n"
    'src\ui\한글 뷰 [v2].xaml'     = "<Window Title=`"한글`"/>`r`n"
    'doc\보고서\최종.txt'          = ("보고서 본문`r`n" * 50)
}
foreach ($k in $tree.Keys) {
    $p = Join-Path $proj $k
    New-Item -ItemType Directory -Force ([System.IO.Path]::GetDirectoryName($p)) | Out-Null
    [System.IO.File]::WriteAllText($p, $tree[$k], $u8n)
}
$expected = @{}
foreach ($k in $tree.Keys) {
    $expected['프로젝트\' + $k] = (Get-FileHash -LiteralPath (Join-Path $proj $k) -Algorithm SHA256).Hash
}
Ok '표본 트리 생성' ((Get-ChildItem -LiteralPath $proj -File -Recurse).Count -eq $tree.Count) ('{0}개 파일' -f $tree.Count)

# ================================================================ 1) PS 간편모드: 폴더 통째로
& $SIMPLE -Mode Encrypt -Path $proj *>&1 | Out-Null
$bundle = Get-ChildItem -LiteralPath $WORK -Filter 'FCRYPT 묶음*.txt' | Select-Object -First 1
Ok '폴더를 통째로 넣어 묶음 생성' ($null -ne $bundle) $(if ($bundle) { $bundle.Name } else { '없음' })

if ($bundle) {
    $out1 = Join-Path $WORK 'out_ps'
    New-Item -ItemType Directory -Force $out1 | Out-Null
    $text = [System.IO.File]::ReadAllText($bundle.FullName)

    # 블록별로 엔진 복호화 (구조 보존 확인)
    $blocks = @()
    $cur = $null
    foreach ($line in ($text -split "`r?`n")) {
        $t = $line.Trim()
        if ($t.StartsWith('-----BEGIN FCRYPT')) { $cur = New-Object System.Collections.Generic.List[string] }
        if ($null -ne $cur) { $cur.Add($line) }
        if ($t.StartsWith('-----END FCRYPT') -and $null -ne $cur) { $blocks += ,$cur.ToArray(); $cur = $null }
    }
    Ok '묶음 안 블록 개수' ($blocks.Count -eq $tree.Count) ('{0}개' -f $blocks.Count)

    $i = 0
    foreach ($b in $blocks) {
        $bf = Join-Path $WORK ('blk{0}.txt' -f $i); $i++
        [System.IO.File]::WriteAllLines($bf, $b, $u8n)
        & $ENGINE -Mode Decrypt -Path $bf -OutDir $out1 -Quiet 2>$null | Out-Null
    }

    $restored = Get-ChildItem -LiteralPath $out1 -File -Recurse
    Ok '복원 파일 개수' ($restored.Count -eq $tree.Count) ('{0}개' -f $restored.Count)

    $structOk = $true
    $hashOk = $true
    foreach ($rel in $expected.Keys) {
        $p = Join-Path $out1 $rel
        if (-not (Test-Path -LiteralPath $p)) { $structOk = $false; continue }
        if ((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne $expected[$rel]) { $hashOk = $false }
    }
    Ok '폴더 구조 그대로 복원 (하위 폴더 포함)' $structOk ''
    Ok '전 파일 해시 일치' $hashOk ''

    # 같은 이름 충돌이 (1) 로 밀리지 않았는지
    $dup = Get-ChildItem -LiteralPath $out1 -File -Recurse | Where-Object { $_.Name -match '\(\d+\)' }
    Ok '이름 충돌로 "(1)" 이 생기지 않음' ($dup.Count -eq 0) ('{0}개' -f $dup.Count)
}

# ================================================================ 2) C# 코어도 같은 결과인가
if ($null -ne ([System.Management.Automation.PSTypeName]'FileCrypt.FileCryptCore').Type) {
    $out2 = Join-Path $WORK 'out_cs'
    New-Item -ItemType Directory -Force $out2 | Out-Null

    $chunks = @()
    foreach ($k in $tree.Keys) {
        $full = Join-Path $proj $k
        $rel  = '프로젝트\' + $k
        $c = [FileCrypt.FileCryptCore]::Encrypt($rel, [System.IO.File]::ReadAllBytes($full))
        $chunks += [FileCrypt.FileCryptCore]::ToArmor($c, 100)
    }
    $csBundle = ($chunks -join "`r`n`r`n")
    $bl = [FileCrypt.FileCryptCore]::ExtractBlocks($csBundle)

    $allOk = $true
    foreach ($b in $bl) {
        $df = [FileCrypt.FileCryptCore]::Decrypt($b)
        $dest = [FileCrypt.FileCryptCore]::ResolveNonClobbering($out2, $df.FileName)
        [System.IO.File]::WriteAllBytes($dest, $df.Data)
    }
    foreach ($rel in $expected.Keys) {
        $p = Join-Path $out2 $rel
        if (-not (Test-Path -LiteralPath $p) -or (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne $expected[$rel]) { $allOk = $false }
    }
    Ok 'C# 코어도 폴더 구조 그대로 복원' $allOk ''

    # ---------------------------------------------------------- 3) 경로 탈출 차단
    Write-Host ''
    Write-Host '  -- 악의적 경로 차단 (컨테이너는 남이 만들 수 있다) --' -ForegroundColor DarkGray
    $jail = Join-Path $WORK 'jail'
    New-Item -ItemType Directory -Force $jail | Out-Null

    $evil = @(
        '..\..\..\Windows\System32\evil.dll',
        '..\바깥.txt',
        'C:\Windows\Temp\evil.txt',
        '\\서버\공유\evil.txt',
        '/etc/passwd',
        'a\..\..\b\escape.txt'
    )
    foreach ($e in $evil) {
        $safe = [FileCrypt.FileCryptCore]::SanitizeRelativePath($e)
        $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($jail, $safe))
        $root = [System.IO.Path]::GetFullPath($jail).TrimEnd('\') + '\'
        $inside = $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
        Ok ('탈출 차단: ' + $e) $inside ('-> ' + $safe)
    }
}

# ================================================================ 4) PS 엔진도 경로 탈출 차단
Write-Host ''
Write-Host '  -- PowerShell 엔진도 막는가 --' -ForegroundColor DarkGray
$jail2 = Join-Path $WORK 'jail_ps'
New-Item -ItemType Directory -Force $jail2 | Out-Null
$victim = Join-Path $WORK 'victim.txt'
[System.IO.File]::WriteAllText($victim, "원래 있던 파일`r`n", $u8n)
$victimHash = (Get-FileHash -LiteralPath $victim -Algorithm SHA256).Hash

$src = Join-Path $WORK 'payload.txt'
[System.IO.File]::WriteAllText($src, "덮어쓰기 시도`r`n", $u8n)
$evilTxt = Join-Path $WORK 'evil.enc.txt'
& $ENGINE -Mode Encrypt -Path $src -Name '..\victim.txt' -Out $evilTxt -Armor -Width 100 -Force -Quiet 2>$null | Out-Null

$global:LASTEXITCODE = 0
& $ENGINE -Mode Decrypt -Path $evilTxt -OutDir $jail2 -Quiet 2>$null | Out-Null
$stillSame = ((Get-FileHash -LiteralPath $victim -Algorithm SHA256).Hash -eq $victimHash)
$landedInside = (Get-ChildItem -LiteralPath $jail2 -File -Recurse).Count -ge 1
Ok '상위 폴더 덮어쓰기 시도 -> 원본 그대로' $stillSame ''
Ok '지정한 폴더 안에만 씀' $landedInside ''

Write-Host ''
Write-Host ('########## 폴더 테스트: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
Write-Host ('  작업 폴더: {0}' -f $WORK) -ForegroundColor DarkGray

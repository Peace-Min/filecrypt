<#
    FileCrypt 간편 모드 - 암호 없음 / 여러 파일 한 번에
      암호화: 파일 선택창(다중 선택) -> 텍스트 하나로 묶음 + 클립보드 자동 복사
      복호화: 클립보드/텍스트파일 안의 블록을 전부 찾아 -> 원본 파일명 그대로 전부 복원
#>
[CmdletBinding()]
param(
    [ValidateSet('Encrypt','Decrypt')]
    [string]$Mode = 'Encrypt',
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'
$ENGINE = Join-Path $PSScriptRoot 'filecrypt.ps1'

$WIDTH = 100

function Line { Write-Host ('-' * 66) -ForegroundColor DarkGray }
function Title([string]$t) {
    Write-Host ''
    Line
    Write-Host ("  $t") -ForegroundColor Cyan
    Line
}

function Get-CleanPath([string]$Raw) {
    if ($null -eq $Raw) { return '' }
    $p = $Raw.Trim()
    if ($p.Length -ge 2 -and (($p.StartsWith('"') -and $p.EndsWith('"')) -or ($p.StartsWith("'") -and $p.EndsWith("'")))) {
        $p = $p.Substring(1, $p.Length - 2)
    }
    return $p.Trim()
}

function Set-Clip([string]$Text) {
    try { Set-Clipboard -Value $Text -ErrorAction Stop; return $true } catch { }
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        [System.Windows.Forms.Clipboard]::SetText($Text)
        return $true
    } catch { return $false }
}

function Get-Clip {
    try { return (Get-Clipboard -Raw -ErrorAction Stop) } catch { }
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        return [System.Windows.Forms.Clipboard]::GetText()
    } catch { return '' }
}

# 파일 선택 대화상자. 다른 창 뒤로 숨지 않도록 TopMost 더미 폼을 소유자로 준다.
function Show-FilePicker([string]$Title, [bool]$Multi, [string]$Filter) {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
    } catch { return $null }

    $owner = New-Object System.Windows.Forms.Form
    $owner.TopMost       = $true
    $owner.ShowInTaskbar = $false
    $owner.Opacity       = 0
    $owner.StartPosition = 'CenterScreen'
    $owner.Size          = New-Object System.Drawing.Size(1, 1)

    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title       = $Title
    $dlg.Multiselect = $Multi
    $dlg.Filter      = $Filter
    $dlg.RestoreDirectory = $true

    try {
        $owner.Show()
        $owner.Activate()
        $result = $dlg.ShowDialog($owner)
    } finally {
        $owner.Close()
        $owner.Dispose()
    }
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) { return $null }
    return @($dlg.FileNames)
}

# 엔진을 조용히 호출하고 (종료코드, 마지막 출력경로, 모든 출력경로) 를 돌려준다.
function Invoke-Engine([hashtable]$P) {
    $global:LASTEXITCODE = 0
    $P['Quiet'] = $true
    $out = @(& $ENGINE @P 2>$null | Where-Object { $_ })
    return @{ Rc = $LASTEXITCODE; Out = ($out | Select-Object -Last 1); All = $out }
}

# 복원될 블록 수를 센다(폴더를 따로 만들지 정하려고). 해석·복원은 엔진이 한다 -
# 여기서 블록을 잘라 따로따로 엔진에 넘기면 조각(PART)이 한 조각씩 흩어져 "조각 부족" 으로 실패했다.
# 표식은 줄바꿈이 뭉개져 있어도 찾는다 (엔진의 표식 정규식과 같은 모양).
function Measure-Blocks([string]$Text) {
    $msg = [regex]::Matches($Text, '-{3,}\s*BEGIN\s*FCRYPT\s*MESSAGE\s*-{3,}', 'IgnoreCase').Count
    $ids = @{}
    foreach ($m in [regex]::Matches($Text, '-{3,}\s*BEGIN\s*FCRYPT\s*PART\s*\d+\s*/\s*\d+\s*([0-9a-fA-F]{8})', 'IgnoreCase')) {
        $ids[$m.Groups[1].Value.ToLowerInvariant()] = 1
    }
    return ($msg + $ids.Count)
}

function Format-Size([long]$b) {
    if ($b -ge 1MB) { return ('{0:N1} MB' -f ($b / 1MB)) }
    if ($b -ge 1KB) { return ('{0:N0} KB' -f ($b / 1KB)) }
    return ('{0:N0} B' -f $b)
}

# ================================================================ 암호화
function Invoke-EncryptSimple {
    Title '암호화  -  파일을 텍스트로 바꿉니다'

    # 1) 파일 결정: 인자로 받았으면 그대로, 아니면 선택창
    $files = @()
    if ($script:Path -and $script:Path.Count -gt 0) {
        foreach ($p in $script:Path) {
            $c = Get-CleanPath $p
            if (-not [string]::IsNullOrWhiteSpace($c)) { $files += $c }
        }
    }
    if ($files.Count -eq 0) {
        Write-Host '  파일 선택창을 엽니다. 여러 개 선택하려면 Ctrl 또는 Shift 를 누른 채 클릭하세요.' -ForegroundColor DarkGray
        $picked = Show-FilePicker '암호화할 파일 선택 (여러 개 선택 가능)' $true '모든 파일 (*.*)|*.*'
        if ($null -eq $picked) { Write-Host ''; Write-Host '  취소했습니다.' -ForegroundColor Yellow; return 2 }
        $files = $picked
    }

    # 폴더 하나만 준 경우: 엔진의 아카이브 모드로 넘긴다 (전체를 한 번에 압축 -> 훨씬 작다).
    if ($files.Count -eq 1 -and (Test-Path -LiteralPath $files[0] -PathType Container)) {
        $root   = (Resolve-Path -LiteralPath $files[0]).ProviderPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        $parent = [System.IO.Path]::GetDirectoryName($root)
        $all    = @(Get-ChildItem -LiteralPath $root -File -Recurse)   # 한 번만 훑는다
        $cnt    = $all.Count
        if ($cnt -eq 0) { Write-Host '  폴더에 파일이 없습니다.' -ForegroundColor Red; return 1 }
        $srcBytes = ($all | Measure-Object Length -Sum).Sum

        Write-Host ''
        Write-Host ('  폴더 통째로 처리합니다 (파일 {0}개). 하나로 묶는 중...' -f $cnt) -ForegroundColor DarkGray

        $dest = Join-Path $parent ('FCRYPT 묶음 {0}개 {1}.txt' -f $cnt, (Get-Date -Format 'yyyyMMdd-HHmmss'))
        $r = Invoke-Engine @{ Mode='Encrypt'; Folder=$root; Out=$dest; Armor=$true; Width=$WIDTH; Force=$true }
        if ($r.Rc -ne 0) { Write-Host ('  실패 (코드 {0})' -f $r.Rc) -ForegroundColor Red; return $r.Rc }

        $dest   = $r.Out
        $text   = [System.IO.File]::ReadAllText($dest)
        $lines  = ($text -split "`r?`n").Count
        $copied = Set-Clip $text

        Write-Host ''
        Write-Host ('  완료  -  파일 {0}개를 아카이브 1개로 묶었습니다.' -f $cnt) -ForegroundColor Green
        Write-Host ''
        Write-Host ('    폴더     {0}' -f [System.IO.Path]::GetFileName($root))
        Write-Host ('    텍스트   {0}' -f [System.IO.Path]::GetFileName($dest)) -ForegroundColor Cyan
        Write-Host ('    위치     {0}' -f [System.IO.Path]::GetDirectoryName($dest)) -ForegroundColor DarkGray
        Write-Host ''
        Write-Host ('    크기     {0}  ->  {1:N0} 자  ({2:N1}%)  /  {3}줄' -f `
            (Format-Size $srcBytes), $text.Length, ($text.Length / [double][Math]::Max(1,$srcBytes) * 100), $lines) -ForegroundColor Green
        Write-Host ''
        if ($copied) { Write-Host '    클립보드에 복사했습니다. 바로 Ctrl+V 하세요.' -ForegroundColor Green }
        else         { Write-Host '    클립보드 복사 실패 - 위 텍스트 파일을 열어 복사하세요.' -ForegroundColor Yellow }
        return 0
    }

    # 폴더를 주면 그 안의 파일을 전부, 폴더 구조를 보존한 상대 경로로 담는다.
    $valid = New-Object System.Collections.Generic.List[object]
    $baseDir = $null
    foreach ($f in $files) {
        if (Test-Path -LiteralPath $f -PathType Container) {
            $root = (Resolve-Path -LiteralPath $f).ProviderPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
            $parent = [System.IO.Path]::GetDirectoryName($root)
            if (-not $baseDir) { $baseDir = $parent }
            foreach ($sub in (Get-ChildItem -LiteralPath $root -File -Recurse)) {
                $rel = $sub.FullName
                if ($parent -and $rel.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) {
                    $rel = $rel.Substring($parent.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar)
                }
                $valid.Add(@{ Full = $sub.FullName; Rel = $rel })
            }
            continue
        }
        if (Test-Path -LiteralPath $f -PathType Leaf) {
            $full = (Resolve-Path -LiteralPath $f).ProviderPath
            $valid.Add(@{ Full = $full; Rel = [System.IO.Path]::GetFileName($full) })
            continue
        }
        Write-Host ('  건너뜀 (없음): {0}' -f $f) -ForegroundColor Yellow
    }
    if ($valid.Count -eq 0) { Write-Host '  처리할 파일이 없습니다.' -ForegroundColor Red; return 1 }

    # 2) 파일별 암호화
    Write-Host ''
    Write-Host ('  {0}개 파일 처리 중...' -f $valid.Count) -ForegroundColor DarkGray
    Write-Host ''

    $tmp = Join-Path $env:TEMP ('fcrypt_enc_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $tmp | Out-Null

    $chunks   = New-Object System.Collections.Generic.List[string]
    $srcBytes = 0
    $done     = 0
    $failed   = 0

    try {
        for ($i = 0; $i -lt $valid.Count; $i++) {
            $f  = $valid[$i].Full
            $rel = $valid[$i].Rel
            $bf = Join-Path $tmp ('b{0:d3}.txt' -f $i)
            $r  = Invoke-Engine @{ Mode='Encrypt'; Path=$f; Name=$rel; Out=$bf; Armor=$true; Width=$WIDTH; Force=$true }
            if ($r.Rc -ne 0) {
                Write-Host ('    [실패] {0}  (코드 {1})' -f $rel, $r.Rc) -ForegroundColor Red
                $failed++
                continue
            }
            $chunks.Add([System.IO.File]::ReadAllText($bf).TrimEnd())
            $len = ([System.IO.FileInfo]$f).Length
            $srcBytes += $len
            $done++
            Write-Host ('    [{0}/{1}] {2,-44} {3,10}' -f ($i+1), $valid.Count, $rel, (Format-Size $len)) -ForegroundColor Gray
        }
    } finally {
        # 파일마다 만든 임시 블록. 예전에는 지우지 않아 %TEMP% 에 계속 쌓였다.
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($done -eq 0) { Write-Host ''; Write-Host '  전부 실패했습니다.' -ForegroundColor Red; return 1 }

    # 3) 하나의 텍스트로 묶기
    $text = ($chunks -join "`r`n`r`n") + "`r`n"

    $firstDir = if ($baseDir) { $baseDir } else { [System.IO.Path]::GetDirectoryName($valid[0].Full) }
    if ($done -eq 1) {
        $dest = Join-Path $firstDir ([System.IO.Path]::GetFileName($valid[0].Full) + '.enc.txt')
    } else {
        $dest = Join-Path $firstDir ('FCRYPT 묶음 {0}개 {1}.txt' -f $done, (Get-Date -Format 'yyyyMMdd-HHmmss'))
    }
    for ($k = 1; (Test-Path -LiteralPath $dest) -and $k -lt 1000; $k++) {
        $dest = Join-Path $firstDir ('{0} ({1}){2}' -f [System.IO.Path]::GetFileNameWithoutExtension($dest), $k, [System.IO.Path]::GetExtension($dest))
    }
    [System.IO.File]::WriteAllText($dest, $text, (New-Object System.Text.UTF8Encoding($false)))
    $copied = Set-Clip $text

    $lines = ($text -split "`r?`n").Count

    Write-Host ''
    Write-Host ('  완료  -  {0}개 파일을 텍스트 하나로 묶었습니다.' -f $done) -ForegroundColor Green
    if ($failed -gt 0) { Write-Host ('  ({0}개 실패)' -f $failed) -ForegroundColor Red }
    Write-Host ''
    Write-Host ('    텍스트   {0}' -f [System.IO.Path]::GetFileName($dest)) -ForegroundColor Cyan
    Write-Host ('    위치     {0}' -f [System.IO.Path]::GetDirectoryName($dest)) -ForegroundColor DarkGray
    Write-Host ''
    # 원본을 글자수 세려고 다시 읽지 않는다 - 크기 비교는 바이트로 한다(GUI 결과줄과 같은 기준).
    Write-Host ('    크기     {0}  ->  {1:N0} 자  ({2:N1}%)  /  {3}줄' -f `
        (Format-Size $srcBytes), $text.Length, ($text.Length / [double][Math]::Max(1,$srcBytes) * 100), $lines) -ForegroundColor Green
    Write-Host ''
    if ($copied) { Write-Host '    클립보드에 복사했습니다. 바로 Ctrl+V 하세요.' -ForegroundColor Green }
    else         { Write-Host '    클립보드 복사 실패 - 위 텍스트 파일을 열어 복사하세요.' -ForegroundColor Yellow }
    return 0
}

# ================================================================ 복호화
function Invoke-DecryptSimple {
    Title '복호화  -  텍스트를 원래 파일로 되돌립니다'

    $text     = $null
    $fromClip = $false
    $srcFile  = $null

    if ($script:Path -and $script:Path.Count -gt 0) {
        $p = Get-CleanPath $script:Path[0]
        if (Test-Path -LiteralPath $p -PathType Leaf) { $srcFile = (Resolve-Path -LiteralPath $p).ProviderPath }
    }

    if ($null -eq $srcFile) {
        $clip = Get-Clip
        if ($clip -and $clip.IndexOf('FCRYPT', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $text = $clip
            $fromClip = $true
            Write-Host '  클립보드에서 찾았습니다.' -ForegroundColor Green
        }
    }

    if ($null -eq $text -and $null -eq $srcFile) {
        Write-Host '  클립보드에 FileCrypt 텍스트가 없습니다. 파일 선택창을 엽니다.' -ForegroundColor DarkGray
        $picked = Show-FilePicker '복호화할 텍스트 파일 선택' $false '텍스트 파일 (*.txt)|*.txt|모든 파일 (*.*)|*.*'
        if ($null -eq $picked) { Write-Host ''; Write-Host '  취소했습니다.' -ForegroundColor Yellow; return 2 }
        $srcFile = $picked[0]
    }

    if ($null -eq $text) { $text = [System.IO.File]::ReadAllText($srcFile) }

    $count = Measure-Blocks $text
    if ($count -eq 0) {
        Write-Host ''
        Write-Host '  FileCrypt 텍스트를 찾지 못했습니다.' -ForegroundColor Red
        Write-Host '  (-----BEGIN FCRYPT MESSAGE----- 로 시작하는 블록이 있어야 합니다)' -ForegroundColor DarkGray
        return 1
    }

    # 복원 위치: 파일에서 읽었으면 그 파일 옆, 클립보드면 바탕화면.
    # 블록이 여러 개면 폴더를 하나 만들어 그 안에 모은다.
    $baseDir = if ($fromClip) { [Environment]::GetFolderPath('Desktop') } else { [System.IO.Path]::GetDirectoryName($srcFile) }
    $outDir  = $baseDir
    if ($count -gt 1) {
        $outDir = Join-Path $baseDir ('FCRYPT 복원 {0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Force $outDir | Out-Null
    }

    Write-Host ''
    Write-Host ('  블록 {0}개를 찾았습니다. 복원 중...' -f $count) -ForegroundColor DarkGray
    Write-Host ''

    # 텍스트 전체를 엔진에 한 번 넘긴다. 엔진이 조각을 모으고, 블록이 여러 개여도 전부 푼다.
    # 클립보드 내용만 임시 파일로 쓰고 끝나면 지운다.
    $tmp = $null
    $inFile = $srcFile
    if ($fromClip) {
        $tmp = Join-Path $env:TEMP ('fcrypt_dec_' + [guid]::NewGuid().ToString('N') + '.txt')
        [System.IO.File]::WriteAllText($tmp, $text, (New-Object System.Text.UTF8Encoding($false)))
        $inFile = $tmp
    }
    try { $r = Invoke-Engine @{ Mode='Decrypt'; Path=$inFile; OutDir=$outDir } }
    finally { if ($tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } }

    $total = 0
    $files = @($r.All | Where-Object { [System.IO.File]::Exists($_) })
    for ($i = 0; $i -lt $files.Count; $i++) {
        $len = ([System.IO.FileInfo]$files[$i]).Length
        $total += $len
        Write-Host ('    [{0}/{1}] {2,-44} {3,10}' -f ($i+1), $files.Count, [System.IO.Path]::GetFileName($files[$i]), (Format-Size $len)) -ForegroundColor Gray
    }

    Write-Host ''
    if ($r.Rc -eq 0) {
        Write-Host ('  완료  -  {0}개 파일 복원' -f $files.Count) -ForegroundColor Green
    } elseif ($r.Rc -eq 4) {
        Write-Host ('  {0}개 복원 / 손상되었거나 이 도구로 만든 것이 아닌 블록이 있습니다' -f $files.Count) -ForegroundColor Yellow
    } elseif ($r.Rc -eq 5) {
        Write-Host ('  {0}개 복원 / 일부는 건너뛰었습니다 (경로 문제 또는 모자란 조각)' -f $files.Count) -ForegroundColor Yellow
    } else {
        Write-Host ('  실패 (코드 {0}) - 조각이 모자라거나 손상된 텍스트입니다' -f $r.Rc) -ForegroundColor Red
    }
    if ($files.Count -gt 0) {
        Write-Host ''
        Write-Host ('    위치   {0}' -f $outDir) -ForegroundColor Cyan
        Write-Host ('    합계   {0}' -f (Format-Size $total))
        Write-Host ''
        if ($r.Rc -eq 0) { Write-Host '    전부 원본과 100% 일치 (SHA-256 검증 통과)' -ForegroundColor Green }
    }
    if ($r.Rc -ne 0) { return $r.Rc }
    if ($files.Count -eq 0) { return 1 }
    return 0
}

if ($Mode -eq 'Encrypt') { $rc = Invoke-EncryptSimple } else { $rc = Invoke-DecryptSimple }
Write-Host ''
exit $rc

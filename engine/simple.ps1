<#
    FileCrypt 간편 모드 - 암호 없음
      암호화: 파일 경로만 입력 -> 텍스트 파일 생성 + 클립보드 자동 복사
      복호화: 클립보드에 붙어있는 텍스트를 자동 인식 -> 원본 파일명 그대로 복원
    묻는 것은 "파일 경로" 하나뿐이고, 복호화는 아무것도 묻지 않는다.
#>
[CmdletBinding()]
param(
    [ValidateSet('Encrypt','Decrypt')]
    [string]$Mode = 'Encrypt',
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$ENGINE = Join-Path $PSScriptRoot 'filecrypt.ps1'

# 고정키. 암호를 안 물어보기 위한 것이므로 비밀이 아니다.
# 용도: 눈으로 못 읽게 + 전송 중 훼손/변조 감지.
$KEY   = 'FileCrypt/default/v2/no-password'
$WIDTH = 100

function Line { Write-Host ('-' * 62) -ForegroundColor DarkGray }
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

# 엔진을 조용히 호출하고 (종료코드, 출력경로) 를 돌려준다.
function Invoke-Engine([hashtable]$P) {
    $global:LASTEXITCODE = 0
    $P['Quiet']    = $true
    $P['Password'] = $KEY
    $out = & $ENGINE @P 2>$null
    return @{ Rc = $LASTEXITCODE; Out = ($out | Select-Object -Last 1) }
}

# ================================================================ 암호화
function Invoke-EncryptSimple {
    Title '암호화  -  파일을 텍스트로 바꿉니다'

    $src = $script:Path
    if ([string]::IsNullOrWhiteSpace($src)) {
        Write-Host '  파일을 이 창에 드래그하거나 경로를 붙여넣고 Enter' -ForegroundColor DarkGray
        Write-Host ''
        $src = Get-CleanPath (Read-Host '  파일')
    } else {
        $src = Get-CleanPath $src
    }
    if ([string]::IsNullOrWhiteSpace($src)) { Write-Host '  취소했습니다.' -ForegroundColor Yellow; return 2 }
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
        Write-Host ('  파일을 찾을 수 없습니다: {0}' -f $src) -ForegroundColor Red; return 1
    }
    $src = (Resolve-Path -LiteralPath $src).ProviderPath

    Write-Host ''
    Write-Host '  처리 중...' -ForegroundColor DarkGray
    $r = Invoke-Engine @{ Mode='Encrypt'; Path=$src; Armor=$true; Width=$WIDTH }
    if ($r.Rc -ne 0) { Write-Host ('  실패 (코드 {0})' -f $r.Rc) -ForegroundColor Red; return $r.Rc }

    $dest   = $r.Out
    $text   = [System.IO.File]::ReadAllText($dest)
    $lines  = [System.IO.File]::ReadAllLines($dest).Count
    $copied = Set-Clip $text

    # 사용자가 실제로 붙여넣게 되는 "글자수" 기준으로 보여준다.
    $srcBytes = (Get-Item -LiteralPath $src).Length
    $srcChars = $null
    try {
        $t = [System.IO.File]::ReadAllText($src)
        if ($t.Length -gt 0) { $srcChars = $t.Length }
    } catch { }

    Write-Host ''
    Write-Host '  완료' -ForegroundColor Green
    Write-Host ''
    Write-Host ('    원본     {0}' -f [System.IO.Path]::GetFileName($src))
    Write-Host ('    텍스트   {0}' -f [System.IO.Path]::GetFileName($dest)) -ForegroundColor Cyan
    Write-Host ('    위치     {0}' -f [System.IO.Path]::GetDirectoryName($dest)) -ForegroundColor DarkGray
    Write-Host ''
    if ($null -ne $srcChars -and $srcChars -gt 0) {
        Write-Host ('    붙여넣을 글자수   {0:N0} 자  ->  {1:N0} 자   ({2:N1}% 감소, {3:N2}배)' -f `
            $srcChars, $text.Length, ((1 - $text.Length / [double]$srcChars) * 100), ($srcChars / [double]$text.Length)) -ForegroundColor Green
    } else {
        Write-Host ('    텍스트 글자수     {0:N0} 자' -f $text.Length) -ForegroundColor Green
    }
    Write-Host ('    파일 크기         {0:N0} B  ->  {1:N0} B   /  {2}줄' -f $srcBytes, (Get-Item -LiteralPath $dest).Length, $lines)
    Write-Host ''
    if ($copied) {
        Write-Host '    클립보드에 복사했습니다. 바로 Ctrl+V 하세요.' -ForegroundColor Green
    } else {
        Write-Host '    클립보드 복사 실패 - 위 텍스트 파일을 열어 복사하세요.' -ForegroundColor Yellow
    }
    return 0
}

# ================================================================ 복호화
function Invoke-DecryptSimple {
    Title '복호화  -  텍스트를 원래 파일로 되돌립니다'

    $srcFile  = $null
    $tempFile = $null

    if (-not [string]::IsNullOrWhiteSpace($script:Path)) {
        $p = Get-CleanPath $script:Path
        if (Test-Path -LiteralPath $p -PathType Leaf) { $srcFile = (Resolve-Path -LiteralPath $p).ProviderPath }
    }

    if ($null -eq $srcFile) {
        $clip = Get-Clip
        if ($clip -and $clip.Contains('-----BEGIN FCRYPT')) {
            $tempFile = Join-Path $env:TEMP ('fcrypt_clip_{0}.txt' -f ([guid]::NewGuid().ToString('N')))
            [System.IO.File]::WriteAllText($tempFile, $clip, (New-Object System.Text.UTF8Encoding($false)))
            $srcFile = $tempFile
            Write-Host '  클립보드에서 찾았습니다.' -ForegroundColor Green
        }
    }

    if ($null -eq $srcFile) {
        Write-Host '  클립보드에 FileCrypt 텍스트가 없습니다.' -ForegroundColor Yellow
        Write-Host '  텍스트 파일을 이 창에 드래그하거나 경로를 붙여넣고 Enter' -ForegroundColor DarkGray
        Write-Host ''
        $p = Get-CleanPath (Read-Host '  파일')
        if ([string]::IsNullOrWhiteSpace($p)) { Write-Host '  취소했습니다.' -ForegroundColor Yellow; return 2 }
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
            Write-Host ('  파일을 찾을 수 없습니다: {0}' -f $p) -ForegroundColor Red; return 1
        }
        $srcFile = (Resolve-Path -LiteralPath $p).ProviderPath
    }

    # 복원 위치: 파일에서 읽었으면 그 파일 옆, 클립보드에서 읽었으면 바탕화면
    $outDir = if ($tempFile) { [Environment]::GetFolderPath('Desktop') } else { [System.IO.Path]::GetDirectoryName($srcFile) }

    try {
        Write-Host ''
        Write-Host '  처리 중...' -ForegroundColor DarkGray
        $r = Invoke-Engine @{ Mode='Decrypt'; Path=$srcFile; OutDir=$outDir }

        if ($r.Rc -eq 4) {
            Write-Host ''
            Write-Host '  실패: 텍스트가 손상되었거나 이 도구로 만든 것이 아닙니다.' -ForegroundColor Red
            Write-Host '        붙여넣을 때 일부가 잘리지 않았는지 확인하세요.' -ForegroundColor DarkGray
            Write-Host '        (BEGIN 줄부터 END 줄까지 전부 있어야 합니다)' -ForegroundColor DarkGray
            return 4
        }
        if ($r.Rc -ne 0) {
            Write-Host ''
            Write-Host ('  실패 (코드 {0}). FileCrypt 텍스트가 맞는지 확인하세요.' -f $r.Rc) -ForegroundColor Red
            return $r.Rc
        }

        $dest = $r.Out
        Write-Host ''
        Write-Host '  완료' -ForegroundColor Green
        Write-Host ''
        Write-Host ('    복원 파일  {0}' -f [System.IO.Path]::GetFileName($dest)) -ForegroundColor Cyan
        Write-Host ('    위치       {0}' -f [System.IO.Path]::GetDirectoryName($dest)) -ForegroundColor DarkGray
        Write-Host ('    크기       {0:N0} B' -f (Get-Item -LiteralPath $dest).Length)
        Write-Host ''
        Write-Host '    원본과 100% 일치 (SHA-256 검증 통과)' -ForegroundColor Green
        return 0
    }
    finally {
        if ($tempFile -and (Test-Path -LiteralPath $tempFile)) { Remove-Item -LiteralPath $tempFile -Force }
    }
}

if ($Mode -eq 'Encrypt') { $rc = Invoke-EncryptSimple } else { $rc = Invoke-DecryptSimple }
Write-Host ''
exit $rc

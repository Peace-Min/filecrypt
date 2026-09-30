# 모든 테스트 스위트가 dot-source 하는 공통부.
#   . (Join-Path $PSScriptRoot '_common.ps1')
#   Start-Test -Tag arch -Title '아카이브 모드'
#   Import-FileCrypt          # C# 코어가 필요할 때만
#   Ok '이름' ($a -eq $b) '설명'
#   Complete-Test             # 요약 줄 + 작업 폴더 정리 + 종료 코드
#
# 주의: PowerShell 변수명은 대소문자를 가리지 않는다. 여기서 만든 이름($key 등)이
#       엔진 변수($KEY)와 겹치지 않게 하고, filecrypt.ps1 은 dot-source 하지 말 것(& 로 부른다).

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ENGINE  = Join-Path $ROOTDIR 'engine\filecrypt.ps1'
$SIMPLE  = Join-Path $ROOTDIR 'engine\simple.ps1'
$EXEDIR  = Join-Path $ROOTDIR 'gui\bin\Release\net48'
$EXE     = Join-Path $EXEDIR 'FileCrypt.exe'
$u8n     = New-Object System.Text.UTF8Encoding($false)

$script:n = 0; $script:fail = 0
$script:TEST_TITLE = ''
$script:PAD = 48
$script:WORK = $null

function Start-Test {
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Title,
        [int]$Pad = 48
    )
    $script:TEST_TITLE = $Title
    $script:PAD = $Pad
    $script:n = 0; $script:fail = 0

    # 스위트마다 새 작업 폴더. 같은 초에 다시 돌려 남은 게 있으면 지우고, 못 지우면 이름을 바꾼다.
    $w = Join-Path $env:TEMP ('fc_{0}_{1}' -f $Tag, (Get-Date -Format 'HHmmss'))
    if (Test-Path -LiteralPath $w) { Remove-Item -LiteralPath $w -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $w) { $w = $w + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 6) }
    New-Item -ItemType Directory -Force $w | Out-Null
    $script:WORK = $w

    # 설정·계정·기록이 사용자의 실제 %LOCALAPPDATA%\FileCrypt 로 가지 않게 한다.
    # AppConfig.Dir 은 처음 접근할 때 한 번 읽으므로 어셈블리를 쓰기 전에 정해 둬야 한다.
    $env:FILECRYPT_DATA_DIR = Join-Path $w 'appdata'

    Write-Host ''
    Write-Host ('########## {0} ##########' -f $Title) -ForegroundColor Cyan
}

function Ok([string]$name, [bool]$cond, [string]$extra) {
    $script:n++
    if ($cond) { Write-Host ('  [PASS] {0}  {1}' -f $name.PadRight($script:PAD), $extra) -ForegroundColor Green }
    else       { Write-Host ('  [FAIL] {0}  {1}' -f $name.PadRight($script:PAD), $extra) -ForegroundColor Red; $script:fail++ }
}

# 자기 형식으로 결과를 찍는 스위트(표 모양 등)가 개수만 올릴 때 쓴다.
function Tally([bool]$cond) {
    $script:n++
    if (-not $cond) { $script:fail++ }
}

function Note([string]$t) { Write-Host ('         ' + $t) -ForegroundColor DarkGray }

function Sha([byte[]]$b) {
    $s = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($s.ComputeHash($b))).Replace('-', '') } finally { $s.Dispose() }
}
function ShaFile([string]$p) { return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }

# 빌드 산출물을 복사해서 올린다. 원본을 LoadFrom 하면 파일이 잠겨 빌드를 막는다.
# NetcusPlan 이 System.Text.Json 을 쓰므로 옆의 dll 도 같이 가져가야 JSON 호출이 된다.
function Import-FileCrypt {
    if (-not (Test-Path -LiteralPath $EXE)) {
        Write-Host ('  [FAIL] gui 빌드 없음: {0}' -f $EXE) -ForegroundColor Red
        Write-Host '         gui 폴더에서 dotnet build -c Release 를 먼저 실행하세요.' -ForegroundColor DarkGray
        $script:n++; $script:fail++
        Complete-Test
    }
    $bin = Join-Path $script:WORK 'bin'
    New-Item -ItemType Directory -Force $bin | Out-Null
    Copy-Item -LiteralPath $EXE -Destination $bin -Force
    Get-ChildItem -LiteralPath $EXEDIR -Filter '*.dll' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $bin -Force
    }
    # dll 을 옆에 두는 것만으로는 부족하다. System.Text.Json 은 옛 버전 번호(예: Unsafe 4.0.4.1)로
    # 의존 dll 을 찾는데, exe.config 의 바인딩 리디렉션은 powershell.exe 안에서는 적용되지 않는다.
    # 그래서 이름만 맞으면 bin 의 dll 을 내주는 해결기를 건다(리디렉션과 같은 효과).
    if (-not ('FcTestResolver' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
public static class FcTestResolver
{
    static string _dir;
    public static void Install(string dir)
    {
        if (_dir != null) return;
        _dir = dir;
        AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
    }
    static Assembly OnResolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        string p = Path.Combine(_dir, name + ".dll");
        return File.Exists(p) ? Assembly.LoadFrom(p) : null;
    }
}
'@
    }
    [FcTestResolver]::Install($bin)
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'FileCrypt.exe'))
}

# LoadFrom 한 dll 은 프로세스가 끝날 때까지 잠겨 있다. 못 지운 것은 이 프로세스가 끝난 뒤 지운다.
function Remove-WorkDir {
    $w = $script:WORK
    if (-not $w -or -not (Test-Path -LiteralPath $w)) { return }
    try { Set-Location -LiteralPath $env:TEMP } catch { }
    Remove-Item -LiteralPath $w -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $w) {
        $q = $w.Replace("'", "''")
        $cmd = "Wait-Process -Id $PID -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; " +
               "Remove-Item -LiteralPath '$q' -Recurse -Force -ErrorAction SilentlyContinue"
        $enc = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($cmd))
        try {
            Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden `
                -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -EncodedCommand ' + $enc) | Out-Null
        } catch { }
    }
}

function Complete-Test {
    $bad = ($script:fail -gt 0) -or ($script:n -eq 0)   # 0건이면 아무것도 못 돌린 것
    Write-Host ''
    Write-Host ('########## {0}: {1}건 중 실패 {2}건 ##########' -f $script:TEST_TITLE, $script:n, $script:fail) `
        -ForegroundColor $(if ($bad) { 'Red' } else { 'Green' })
    if ($env:FC_KEEP_WORK -eq '1') {
        Write-Host ('  작업 폴더(보존): {0}' -f $script:WORK) -ForegroundColor DarkGray
    } else {
        Remove-WorkDir
    }
    if ($bad) { exit 1 } else { exit 0 }
}

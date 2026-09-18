$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

# WebView2 를 실제로 띄울 수 있는 상태인지 본다.
#
# 이 테스트가 있는 이유: AnyCPU 로 빌드하면 exe 는 64비트로 실행되는데 WebView2 의
# 네이티브 로더는 x86 만 출력에 복사돼, 계정 창에서 [로그인 확인] 을 누르는 순간
# 0x8007000B (BadImageFormat) 로 죽었다. 빌드 설정이 되돌아가면 여기서 잡는다.

$ROOTDIR = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$OUT     = Join-Path $ROOTDIR 'gui\bin\Release\net48'
$EXE     = Join-Path $OUT 'FileCrypt.exe'

$n = 0; $fail = 0
function Ok([string]$name, [bool]$cond, [string]$extra) {
    $script:n++
    if ($cond) { Write-Host ('  [PASS] {0}  {1}' -f $name.PadRight(50), $extra) -ForegroundColor Green }
    else       { Write-Host ('  [FAIL] {0}  {1}' -f $name.PadRight(50), $extra) -ForegroundColor Red; $script:fail++ }
}

# PE 헤더에서 아키텍처를 읽는다. .NET AnyCPU 도 0x014c 로 보이므로, 그걸
# 구분하려면 CLR 헤더의 32BITREQUIRED 플래그까지 봐야 한다(아래 Bitness).
function PeMachine([string]$f) {
    if (-not (Test-Path -LiteralPath $f)) { return 'MISSING' }
    $fs = [System.IO.File]::OpenRead($f)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Position = 0x3C; $pe = $br.ReadInt32()
        $fs.Position = $pe + 4; $m = $br.ReadUInt16()
        switch ($m) { 0x014C { 'x86' } 0x8664 { 'x64' } 0xAA64 { 'arm64' } default { ('0x{0:x}' -f $m) } }
    } finally { $fs.Dispose() }
}

Write-Host ''
Write-Host '########## WebView2 준비 상태 ##########' -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $EXE)) { Write-Host '  [FAIL] gui 빌드 없음' -ForegroundColor Red; exit 1 }

# ================================================================ 1) 아키텍처가 서로 맞는가
$exeArch    = PeMachine $EXE
$loaderPath = Join-Path $OUT 'WebView2Loader.dll'
$loaderArch = PeMachine $loaderPath

Ok 'exe 가 x64 로 고정돼 있다' ($exeArch -eq 'x64') $exeArch
Ok '네이티브 로더가 출력 폴더에 있다' ($loaderArch -ne 'MISSING') $loaderPath
Ok 'exe 와 로더의 아키텍처가 같다' ($exeArch -eq $loaderArch) ("exe=$exeArch / loader=$loaderArch")

# ================================================================ 2) 관리 어셈블리가 다 복사됐는가
foreach ($dll in 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll') {
    Ok ('  ' + $dll) (Test-Path -LiteralPath (Join-Path $OUT $dll)) ''
}

# ================================================================ 3) Evergreen 런타임이 깔려 있는가
$rtKeys = @(
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
)
$rtVer = $null
foreach ($k in $rtKeys) {
    if (Test-Path $k) { $v = (Get-ItemProperty $k -ErrorAction SilentlyContinue).pv; if ($v) { $rtVer = $v; break } }
}
Ok 'WebView2 Evergreen 런타임 설치됨' ($null -ne $rtVer) ($(if ($rtVer) { "v$rtVer" } else { '미설치 - 앱에서 브라우저 기능 못 씀' }))

# ================================================================ 4) 실제로 환경을 만들어 본다 (진짜 검증)
# 별도 64비트 STA 프로세스에서 CoreWebView2Environment 를 만들어 본다.
# 여기서 0x8007000B 가 나면 아키텍처가 어긋난 것이다.
$probe = Join-Path $env:TEMP ('fc_wv2probe_' + (Get-Date -Format 'HHmmss') + '.ps1')
$udf   = Join-Path $env:TEMP ('fc_wv2udf_' + (Get-Date -Format 'HHmmss'))
$body = @'
$ErrorActionPreference = 'Stop'
try {
    Add-Type -Path (Join-Path $env:FC_OUT 'Microsoft.Web.WebView2.Core.dll')
    $t = [Microsoft.Web.WebView2.Core.CoreWebView2Environment]::CreateAsync($null, $env:FC_UDF, $null)
    if (-not $t.Wait(60000)) { Write-Output 'TIMEOUT'; exit 1 }
    $env2 = $t.Result
    Write-Output ('OK ' + $env2.BrowserVersionString)
} catch {
    $ex = $_.Exception; while ($ex.InnerException) { $ex = $ex.InnerException }
    Write-Output ('ERR ' + $ex.Message)
    exit 1
}
'@
[System.IO.File]::WriteAllText($probe, $body, (New-Object System.Text.UTF8Encoding($true)))

$env:FC_OUT = $OUT
$env:FC_UDF = $udf
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'   # System32 = 64비트
$result = & $psExe -NoProfile -STA -ExecutionPolicy Bypass -File $probe 2>&1 | Out-String
$result = $result.Trim()

Ok '실제 WebView2 환경 생성 성공' ($result -like 'OK *') $result
if ($result -like '*0x8007000B*') {
    Write-Host '        ^ 32/64비트 불일치입니다. FileCrypt.csproj 의 PlatformTarget 을 확인하세요.' -ForegroundColor Yellow
}

Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $udf -Recurse -Force -ErrorAction SilentlyContinue

# ================================================================ 5) 인스톨러가 이 파일들을 같이 넣는가
# 개발 폴더에서는 되는데 설치본만 안 되는 함정을 막는다.
$iss = Join-Path $ROOTDIR 'installer\FileCrypt.iss'
if (Test-Path -LiteralPath $iss) {
    $issText = [System.IO.File]::ReadAllText($iss)

    # 빌드가 내놓은 DLL 이 하나라도 인스톨러에서 빠지면 '개발 폴더에서는 되는데 설치본만 죽는다'.
    # 이름을 일일이 적어 두면 의존성이 늘 때 또 놓치므로, 출력 폴더를 기준으로 검사한다.
    $outDlls = @(Get-ChildItem -LiteralPath $OUT -Filter '*.dll' | Select-Object -ExpandProperty Name)
    $wildcard = $issText -match [regex]::Escape('net48\*.dll')
    $missing = @()
    if (-not $wildcard) {
        foreach ($d in $outDlls) { if ($issText -notlike ('*' + $d + '*')) { $missing += $d } }
    }
    Ok '인스톨러가 빌드 DLL 을 전부 포함' ($missing.Count -eq 0) `
       ($(if ($wildcard) { "*.dll 로 일괄 포함 ($($outDlls.Count)개)" } else { '빠진 것: ' + ($missing -join ', ') }))
    Ok '인스톨러가 32비트 설치를 막는다' ($issText -like '*ArchitecturesAllowed*') ''
} else {
    Ok '인스톨러 스크립트 존재' $false $iss
}

Write-Host ''
Write-Host ('########## WebView2 준비 상태: {0}건 중 실패 {1}건 ##########' -f $n, $fail) -ForegroundColor Cyan

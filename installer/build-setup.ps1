<#
    setup.exe 만들기 - 버전 올리기 -> Release 빌드 -> Inno Setup 컴파일을 한 번에

    F9 로 FileCrypt.iss 만 컴파일하면 gui\bin\Release 에 남아 있던 옛 exe 가 그대로 들어간다.
    버전도 늘 같아서 설치본이 어느 빌드인지 알 수 없었다. 이 스크립트는 그 두 가지를 막는다.

      1) 커밋 안 된 수정이 있으면 멈춘다 (-AllowDirty 로 무시, 이때 빌드 표시에 "-dirty" 가 붙는다)
      2) gui\FileCrypt.csproj 의 <Version> 을 올리고 그 변경만 커밋한다
         -> exe 에 찍히는 커밋이 실제로 빌드한 코드와 같아진다
      3) 옛 exe 를 지우고 Release 로 새로 빌드한다
      4) 빌드된 exe 버전으로 setup.exe 를 만든다 -> installer\Output\FileCrypt-Setup-<버전>.exe

    예
      setup만들기.cmd                     2.0.0 -> 2.0.1
      setup만들기.cmd -Bump minor         2.0.1 -> 2.1.0
      setup만들기.cmd -Bump none          버전 그대로 다시 만들기
#>
[CmdletBinding()]
param(
    [ValidateSet('patch', 'minor', 'major', 'none')]
    [string]$Bump = 'patch',
    [switch]$AllowDirty,
    [switch]$NoCommit
)

$ErrorActionPreference = 'Stop'

$Root   = Split-Path $PSScriptRoot -Parent
$Proj   = Join-Path $Root 'gui\FileCrypt.csproj'
$Exe    = Join-Path $Root 'gui\bin\Release\net48\FileCrypt.exe'
$Iss    = Join-Path $PSScriptRoot 'FileCrypt.iss'

function Say([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Fail([string]$t) { Say ''; Say ('  [실패] ' + $t) Red; Say ''; exit 1 }

Say ''
Say '============================================================' DarkCyan
Say '  FileCrypt setup.exe 만들기' Cyan
Say '============================================================' DarkCyan
Say ''

# ---------------------------------------------------------------- git 상태
# 폐쇄망 PC 처럼 git 이 없으면 커밋 표시 없이 빌드만 한다.
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) { & git -C $Root rev-parse --git-dir *> $null; if ($LASTEXITCODE -ne 0) { $git = $null } }

$dirty = $false
if ($git) {
    $changes = @(& git -C $Root status --porcelain --untracked-files=no)
    if ($changes.Count -gt 0) {
        if (-not $AllowDirty) {
            Say '  커밋 안 된 수정이 있습니다:' Yellow
            $changes | ForEach-Object { Say ('    ' + $_) DarkGray }
            Fail '먼저 커밋하세요. 그대로 만들려면 -AllowDirty (빌드 표시에 -dirty 가 붙습니다).'
        }
        $dirty = $true
    }
} else {
    Say '  git 이 없어 커밋 표시 없이 빌드합니다.' Yellow
}

# ---------------------------------------------------------------- 버전 올리기
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$projText = [IO.File]::ReadAllText($Proj)
$m = [regex]::Match($projText, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if (-not $m.Success) { Fail 'FileCrypt.csproj 에서 <Version>x.y.z</Version> 를 찾지 못했습니다.' }

$maj = [int]$m.Groups[1].Value; $min = [int]$m.Groups[2].Value; $rev = [int]$m.Groups[3].Value
$old = '{0}.{1}.{2}' -f $maj, $min, $rev
switch ($Bump) {
    'major' { $maj++; $min = 0; $rev = 0 }
    'minor' { $min++; $rev = 0 }
    'patch' { $rev++ }
}
$ver = '{0}.{1}.{2}' -f $maj, $min, $rev

if ($ver -ne $old) {
    $projText = $projText.Substring(0, $m.Index) + "<Version>$ver</Version>" + $projText.Substring($m.Index + $m.Length)
    [IO.File]::WriteAllText($Proj, $projText, $utf8Bom)
    Say ('  버전       {0} -> {1}' -f $old, $ver) Green

    if ($git -and -not $NoCommit) {
        # csproj 만 커밋한다 (-AllowDirty 일 때 다른 수정까지 딸려 들어가지 않게).
        & git -C $Root commit -q -m "버전 $ver" -- 'gui/FileCrypt.csproj'
        if ($LASTEXITCODE -ne 0) { Fail '버전 커밋에 실패했습니다.' }
    } elseif ($git) {
        $dirty = $true   # 올린 버전을 커밋하지 않았으니 빌드한 코드는 HEAD 와 다르다
    }
} else {
    Say ('  버전       {0} (그대로)' -f $ver) Green
}

# ---------------------------------------------------------------- Release 빌드
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Fail 'dotnet SDK 가 없습니다. Visual Studio 로 Release 빌드한 뒤 FileCrypt.iss 를 F9 하세요.' }

$buildArgs = @('build', $Proj, '-c', 'Release', '--nologo', '-v', 'q')
if ($git) {
    $sha = (& git -C $Root rev-parse --short HEAD).Trim()
    if ($dirty) { $sha += '-dirty' }
    # SDK 가 스스로 넣는 커밋 값 대신 이걸 쓴다 - "-dirty" 를 붙일 수 있게.
    $buildArgs += "-p:SourceRevisionId=$sha"
}

# 옛 exe 가 남아 있으면 빌드가 실패해도 그걸 담아 버린다. 먼저 지운다.
if (Test-Path -LiteralPath $Exe) { Remove-Item -LiteralPath $Exe -Force }

Say '  빌드       Release ...' Gray
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Exe)) { Fail 'Release 빌드에 실패했습니다.' }

$vi = (Get-Item -LiteralPath $Exe).VersionInfo
$built = '{0}.{1}.{2}' -f $vi.FileMajorPart, $vi.FileMinorPart, $vi.FileBuildPart
if ($built -ne $ver) { Fail ('빌드된 exe 버전({0})이 csproj({1})와 다릅니다.' -f $built, $ver) }
Say ('  빌드 완료  {0}' -f $vi.ProductVersion) Green

# ---------------------------------------------------------------- setup.exe
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $iscc) { $c = Get-Command ISCC -ErrorAction SilentlyContinue; if ($c) { $iscc = $c.Source } }
if (-not $iscc) { Fail 'Inno Setup 6 (ISCC.exe) 이 없습니다. https://jrsoftware.org/isdl.php' }

Say '  setup.exe  컴파일 ...' Gray
& $iscc /Q "/DMyAppVersion=$ver" $Iss
if ($LASTEXITCODE -ne 0) { Fail 'Inno Setup 컴파일에 실패했습니다.' }

$out = Join-Path $PSScriptRoot ("Output\FileCrypt-Setup-{0}.exe" -f $ver)
if (-not (Test-Path -LiteralPath $out)) { Fail ('결과 파일이 없습니다: ' + $out) }

Say ''
Say ('  완료  {0}' -f $out) Green
Say ('        담긴 빌드 {0}' -f $vi.ProductVersion) DarkGray
if ($ver -ne $old -and $git -and -not $NoCommit) { Say ('        "버전 {0}" 을 커밋했습니다. 필요하면 push 하세요.' -f $ver) DarkGray }
Say ''
exit 0

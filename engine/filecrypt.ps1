<#
.SYNOPSIS
    FileCrypt v2 엔진 - 압축 + 인증 암호화 (PowerShell 5.1 / 오프라인 / 외부 의존성 없음)

.DESCRIPTION
    원본 바이트 + 원본 파일명을 하나의 페이로드로 묶어
    Deflate 압축 -> AES-256-CBC 암호화 -> HMAC-SHA256 인증 (Encrypt-then-MAC).
    원본 SHA-256 을 헤더에 기록해 복호화 후 자동 대조합니다.
    전 과정 무손실 -> 복호화 결과는 원본과 비트 단위로 동일합니다.

    보통은 이 파일을 직접 쓰지 않습니다. 상위 폴더의 암호화.cmd / 복호화.cmd 를 쓰세요.
#>
[CmdletBinding()]
param(
    [ValidateSet('Encrypt','Decrypt')]
    [string]$Mode,

    [string]$Path,
    [string]$Out,
    [string]$OutDir,
    # 컨테이너에 기록할 이름. 폴더 구조를 보존하려면 상대 경로를 준다 (예: "sub/a.cs").
    [string]$Name,
    # 폴더를 통째로 아카이브 1블록으로 만든다 (-Path 대신 사용).
    [string]$Folder,

    [ValidateSet('On','Off')]
    [string]$Compress = 'On',

    [switch]$Armor,
    # GUI(FileCryptCore.DefaultWidth)와 같은 줄폭. 예전에는 76 이라 같은 파일도 결과 모양이 달랐다.
    [int]$Width = 100,
    # 결과를 이 글자수 이하의 조각으로 나눈다 (0 = 나누지 않음).
    # 크기가 줄지는 않는다. 한 번에 붙여넣을 수 없는 채널로 옮기기 위한 것.
    [int]$Split = 0,
    [switch]$Force,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 상수
$MAGIC       = [System.Text.Encoding]::ASCII.GetBytes('FCRYPT01')
$VERSION     = 2
$HDR_SIZE    = 112
$OFF_VER     = 8
$OFF_FLAGS   = 9
$OFF_SALT    = 12
$OFF_IV      = 28
$OFF_ITER    = 44
$OFF_HASH    = 48
$OFF_HMAC    = 80
$FLAG_ZIP    = 1    # bit0 : Deflate 압축됨
$FLAG_KDF256 = 2    # bit1 : PBKDF2-HMAC-SHA256 (없으면 SHA1)
$FLAG_ARCHIVE = 4   # bit2 : 페이로드가 여러 파일을 담은 아카이브
$ARMOR_BEGIN = '-----BEGIN FCRYPT MESSAGE-----'
$ARMOR_END   = '-----END FCRYPT MESSAGE-----'
$PART_BEGIN  = '-----BEGIN FCRYPT PART '
$PART_END    = '-----END FCRYPT PART '

# netcus·메일·채팅처럼 줄바꿈/공백을 뭉개는 경로를 거쳐도 복원되도록,
# base64 에 없는 하이픈으로 표식을 찾아 표준 형태(각자 한 줄)로 되돌린다.
# C# FileCryptCore.NormalizeMarkers 와 동작이 같아야 한다(교차검증).
$RX_MARKER = New-Object System.Text.RegularExpressions.Regex(
    '-{3,}\s*(BEGIN|END)\s*FCRYPT\s*(MESSAGE|PART\s*\d+\s*/\s*\d+\s*[0-9a-fA-F]{8})\s*-{3,}',
    ([System.Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant'))
function ConvertTo-NormalizedMarkers([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    if ($Text.IndexOf('FCRYPT', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { return $Text }
    return $RX_MARKER.Replace($Text, {
        param($m)
        $kind = $m.Groups[1].Value.ToUpperInvariant()
        $spec = $m.Groups[2].Value
        if ($spec.StartsWith('PART', [System.StringComparison]::OrdinalIgnoreCase)) {
            $pm = [regex]::Match($spec, '(\d+)\s*/\s*(\d+)\s*([0-9a-fA-F]{8})')
            $canon = 'PART ' + $pm.Groups[1].Value + '/' + $pm.Groups[2].Value + ' ' + $pm.Groups[3].Value.ToLowerInvariant()
        } else { $canon = 'MESSAGE' }
        return "`r`n-----$kind FCRYPT $canon-----`r`n"
    })
}

# 이 도구에는 암호가 없다. 아래 키는 소스에 박혀 있고 공개돼 있다.
# 하는 일: 텍스트를 눈으로 못 읽게 만들기 + 전송 중 훼손/변조 감지.
# 안 하는 일: 내용 보호. 이 도구를 가진 사람은 누구나 연다.
#
# 공개된 키에 PBKDF2 스트레칭을 거는 것은 아무것도 지키지 않으면서
# 파일 수에 비례해 시간만 잡아먹으므로 반복은 1회다.
# (헤더의 반복 횟수 필드는 그대로 두어 예전에 만든 텍스트도 열린다)
$KEY        = 'FileCrypt/default/v2/no-password'
$ITERATIONS = 1

# ---------------------------------------------------------------- 유틸
function Write-Head([string]$Title) {
    if ($script:Quiet) { return }
    Write-Host ''
    Write-Host '============================================================' -ForegroundColor DarkCyan
    Write-Host ("  FileCrypt   -   {0}" -f $Title) -ForegroundColor Cyan
    Write-Host '============================================================' -ForegroundColor DarkCyan
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N2} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N2} KB' -f ($Bytes / 1KB)) }
    return ('{0} B' -f $Bytes)
}

function Get-CleanPath([string]$Raw) {
    if ($null -eq $Raw) { return '' }
    $p = $Raw.Trim()
    if ($p.Length -ge 2) {
        if (($p.StartsWith('"') -and $p.EndsWith('"')) -or ($p.StartsWith("'") -and $p.EndsWith("'"))) {
            $p = $p.Substring(1, $p.Length - 2)
        }
    }
    return $p.Trim()
}

function Read-FilePath([string]$Prompt) {
    for ($i = 0; $i -lt 5; $i++) {
        $raw = Read-Host $Prompt
        $p = Get-CleanPath $raw
        if ([string]::IsNullOrWhiteSpace($p)) {
            Write-Host '  [취소] 경로가 비었습니다.' -ForegroundColor Yellow
            return $null
        }
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            return (Resolve-Path -LiteralPath $p).ProviderPath
        }
        Write-Host ('  [오류] 파일을 찾을 수 없습니다: {0}' -f $p) -ForegroundColor Red
    }
    return $null
}

function New-Dir([string]$P) {
    if ($P -and -not [System.IO.Directory]::Exists($P)) { [void][System.IO.Directory]::CreateDirectory($P) }
}

function Get-RandomBytes([int]$Count) {
    $b = New-Object byte[] $Count
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    try { $rng.GetBytes($b) } finally { $rng.Dispose() }
    return ,$b
}

function Test-BytesEqual([byte[]]$A, [byte[]]$B) {
    if ($A.Length -ne $B.Length) { return $false }
    $diff = 0
    for ($i = 0; $i -lt $A.Length; $i++) { $diff = $diff -bor ($A[$i] -bxor $B[$i]) }
    return ($diff -eq 0)
}

function Get-Sha256Bytes([byte[]]$Data) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ,$sha.ComputeHash($Data) } finally { $sha.Dispose() }
}

function ConvertTo-HexString([byte[]]$Data) {
    return ([System.BitConverter]::ToString($Data)).Replace('-','').ToLowerInvariant()
}

# .NET API 는 PowerShell 의 현재 위치가 아니라 프로세스 작업 디렉터리를 쓰므로
# 상대 경로를 반드시 여기서 절대 경로로 확정한다.
function ConvertTo-AbsolutePath([string]$P) {
    if ([System.IO.Path]::IsPathRooted($P)) { return [System.IO.Path]::GetFullPath($P) }
    $cwd = (Get-Location -PSProvider FileSystem).ProviderPath
    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($cwd, $P))
}

# ---------------------------------------------------------------- 압축 (무손실)
function Compress-Bytes([byte[]]$Data) {
    $ms = New-Object System.IO.MemoryStream
    $ds = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionMode]::Compress, $true)
    try   { $ds.Write($Data, 0, $Data.Length) } finally { $ds.Dispose() }
    $out = $ms.ToArray()
    $ms.Dispose()
    return ,$out
}

function Expand-Bytes([byte[]]$Data) {
    $ms  = New-Object System.IO.MemoryStream(,$Data)
    $ds  = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
    $out = New-Object System.IO.MemoryStream
    try   { $ds.CopyTo($out) } finally { $ds.Dispose(); $ms.Dispose() }
    $r = $out.ToArray()
    $out.Dispose()
    return ,$r
}

# ---------------------------------------------------------------- 키 유도
function Test-Sha256Kdf {
    try {
        $s = New-Object byte[] 8
        $t = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
            [System.Text.Encoding]::UTF8.GetBytes('x'), $s, 1,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256)
        $t.Dispose()
        return $true
    } catch { return $false }
}
# 한 번만 확인한다(.NET 4.7.2 이상이면 늘 true).
$KDF256 = Test-Sha256Kdf

function Get-DerivedKeys([string]$Pw, [byte[]]$Salt, [int]$Iter, [bool]$UseSha256) {
    $pwBytes = [System.Text.Encoding]::UTF8.GetBytes($Pw)
    if ($UseSha256) {
        $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
            $pwBytes, $Salt, $Iter, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    } else {
        $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($pwBytes, $Salt, $Iter)
    }
    try { $material = $kdf.GetBytes(64) } finally { $kdf.Dispose() }
    $aesKey  = New-Object byte[] 32
    $hmacKey = New-Object byte[] 32
    [Array]::Copy($material, 0,  $aesKey,  0, 32)
    [Array]::Copy($material, 32, $hmacKey, 0, 32)
    [Array]::Clear($material, 0, $material.Length)
    return @{ Aes = $aesKey; Hmac = $hmacKey }
}

# ---------------------------------------------------------------- AES / HMAC
# AesCryptoServiceProvider = Windows CNG(AES-NI). AesManaged 보다 빠르고 FIPS 정책 PC 에서도 동작한다.
# 출력 바이트는 같다 (C# FileCryptCore 와 같은 선택).
# $Offset/$Count 로 컨테이너 안의 암호문을 복사 없이 바로 읽는다.
function Invoke-Aes([byte[]]$Data, [byte[]]$Key, [byte[]]$Iv, [bool]$Encrypting, [int]$Offset = 0, [int]$Count = -1) {
    if ($Count -lt 0) { $Count = $Data.Length - $Offset }
    $aes = New-Object System.Security.Cryptography.AesCryptoServiceProvider
    try {
        $aes.KeySize   = 256
        $aes.BlockSize = 128
        $aes.Mode      = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding   = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $Key
        $aes.IV  = $Iv
        if ($Encrypting) { $tr = $aes.CreateEncryptor() } else { $tr = $aes.CreateDecryptor() }
        try   { return ,$tr.TransformFinalBlock($Data, $Offset, $Count) }
        finally { $tr.Dispose() }
    } finally { $aes.Dispose() }
}

# HMAC = 헤더 0..79 + 암호문. 암호문은 $Cipher 의 $Offset 부터 끝까지(컨테이너를 그대로 넘길 수 있게).
function Get-HmacTag([byte[]]$Key, [byte[]]$Header, [byte[]]$Cipher, [int]$Offset = 0) {
    $h = New-Object System.Security.Cryptography.HMACSHA256(,$Key)
    try {
        $null = $h.TransformBlock($Header, 0, 80, $null, 0)
        $null = $h.TransformFinalBlock($Cipher, $Offset, $Cipher.Length - $Offset)
        return ,$h.Hash
    } finally { $h.Dispose() }
}

# ---------------------------------------------------------------- 텍스트(Base64) 포장
function ConvertTo-Armor([byte[]]$Data, [int]$LineWidth) {
    $b64 = [Convert]::ToBase64String($Data)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add($ARMOR_BEGIN)
    if ($LineWidth -le 0) {
        $lines.Add($b64)
    } else {
        for ($i = 0; $i -lt $b64.Length; $i += $LineWidth) {
            $n = [Math]::Min($LineWidth, $b64.Length - $i)
            $lines.Add($b64.Substring($i, $n))
        }
    }
    $lines.Add($ARMOR_END)
    return $lines.ToArray()
}

# 컨테이너를 여러 조각 텍스트로 나눈다.
#   -----BEGIN FCRYPT PART 3/8 a1b2c3d4-----
# 3/8 = 순서와 전체 개수, a1b2c3d4 = 묶음 식별자 (HMAC 앞 4바이트)
function ConvertTo-ArmorParts([byte[]]$Container, [int]$MaxChars, [int]$LineWidth) {
    if ($MaxChars -lt 1000) { $MaxChars = 1000 }
    $gid = ''
    for ($i = 0; $i -lt 4; $i++) { $gid += '{0:x2}' -f $Container[$OFF_HMAC + $i] }

    $b64 = [Convert]::ToBase64String($Container)
    $overhead = 140
    $avail = [Math]::Max(500, $MaxChars - $overhead)
    # 줄바꿈(CRLF)까지 계산에 넣지 않으면 지정 글자수를 넘긴다.
    $body = if ($LineWidth -gt 0) { [Math]::Max(400, [int]([long]$avail * $LineWidth / ($LineWidth + 2))) } else { $avail }
    $count = [Math]::Max(1, [int][Math]::Ceiling($b64.Length / [double]$body))

    $parts = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $count; $i++) {
        $start = $i * $body
        $len = [Math]::Min($body, $b64.Length - $start)
        $chunk = $b64.Substring($start, $len)
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append(('{0}{1}/{2} {3}-----' -f $PART_BEGIN, ($i+1), $count, $gid)).Append("`r`n")
        if ($LineWidth -le 0) { [void]$sb.Append($chunk).Append("`r`n") }
        else {
            for ($k = 0; $k -lt $chunk.Length; $k += $LineWidth) {
                [void]$sb.Append($chunk.Substring($k, [Math]::Min($LineWidth, $chunk.Length - $k))).Append("`r`n")
            }
        }
        [void]$sb.Append(('{0}{1}/{2} {3}-----' -f $PART_END, ($i+1), $count, $gid))
        $parts.Add($sb.ToString())
    }
    return ,$parts
}

# BEGIN 줄에서 "3/8 a1b2c3d4" 를 읽는다. 하이픈 개수나 공백이 달라져도 견디게 한다.
function Read-PartHeader([string]$Line) {
    $p = $Line.IndexOf($PART_BEGIN)
    if ($p -lt 0) { return $null }
    $rest = $Line.Substring($p + $PART_BEGIN.Length).Trim().TrimEnd('-').Trim()
    $sp = $rest.IndexOf(' ')
    if ($sp -le 0) { return $null }
    $nums = $rest.Substring(0, $sp)
    $tail = $rest.Substring($sp + 1).Trim()
    $slash = $nums.IndexOf('/')
    if ($slash -le 0) { return $null }
    $idx = 0; $tot = 0
    if (-not [int]::TryParse($nums.Substring(0, $slash), [ref]$idx)) { return $null }
    if (-not [int]::TryParse($nums.Substring($slash + 1), [ref]$tot)) { return $null }
    if ($idx -lt 1 -or $tot -lt 1 -or $idx -gt $tot) { return $null }
    $sp2 = $tail.IndexOf(' ')
    $id = if ($sp2 -gt 0) { $tail.Substring(0, $sp2) } else { $tail }
    if ([string]::IsNullOrWhiteSpace($id)) { return $null }
    return @{ Index = $idx; Total = $tot; Id = $id.Trim() }
}

# base64 가 아닌 글자(제로폭 문자, 인용부호 "> ", 공백 등). 블록 본문을 다 모은 뒤 한 번에 걷어낸다.
# 예전에는 글자 하나마다 PowerShell 함수를 불러(1회 수 µs) base64 1MB 에 수 초가 걸렸다.
# 실제 데이터가 상했다면 뒤의 HMAC 이 잡는다.
$RX_NOT_B64 = New-Object System.Text.RegularExpressions.Regex('[^A-Za-z0-9+/=]')

# 텍스트를 한 번 훑어 MESSAGE 블록과 PART 조각을 모은다. C# FileCryptCore.Parse 와 규칙이 같다.
#   Messages : MESSAGE 블록들의 base64 (나타난 순서)
#   Parts    : 묶음 id -> @{ Total; Chunks = @{ 번호 = base64 } }
# 표식은 무엇이든 지금 모으던 블록을 닫는다. MESSAGE 는 자기 END 로 닫힐 때(또는 텍스트 끝)만 살린다.
function Get-ArmorBlocks([string]$Text) {
    $messages = New-Object System.Collections.Generic.List[string]
    $parts = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    $result = @{ Messages = $messages; Parts = $parts }
    if ([string]::IsNullOrEmpty($Text)) { return $result }

    $Text = ConvertTo-NormalizedMarkers $Text
    $ord = [StringComparison]::Ordinal
    $state = 0          # 0 = 블록 밖, 1 = MESSAGE, 2 = PART
    $cur = $null; $hdr = $null

    $closePart = {
        if ($null -eq $cur -or $null -eq $hdr) { return }
        $b = $RX_NOT_B64.Replace($cur.ToString(), '')
        if ($b.Length -eq 0) { return }
        if (-not $parts.ContainsKey($hdr.Id)) { $parts[$hdr.Id] = @{ Total = $hdr.Total; Chunks = @{} } }
        if ($hdr.Total -gt $parts[$hdr.Id].Total) { $parts[$hdr.Id].Total = $hdr.Total }
        $parts[$hdr.Id].Chunks[$hdr.Index] = $b      # 같은 조각을 두 번 넣어도 괜찮다
    }
    $closeMessage = {
        $b = $RX_NOT_B64.Replace($cur.ToString(), '')
        if ($b.Length -gt 0) { $messages.Add($b) }
    }

    foreach ($raw in ($Text -split "`r`n|`r|`n")) {
        $t = $raw.Trim()
        if ($t.Length -eq 0) { continue }
        $pb = $t.IndexOf($PART_BEGIN, $ord) -ge 0
        $pe = (-not $pb) -and ($t.IndexOf($PART_END, $ord) -ge 0)
        $mb = (-not $pb) -and (-not $pe) -and $t.StartsWith('-----BEGIN FCRYPT', $ord)
        $me = (-not $pb) -and (-not $pe) -and $t.StartsWith('-----END FCRYPT', $ord)

        if ($pb -or $pe -or $mb -or $me) {
            if ($state -eq 2) { & $closePart }
            elseif ($state -eq 1 -and $me) { & $closeMessage }
            $state = 0; $cur = $null; $hdr = $null
            if ($pb) {
                $hdr = Read-PartHeader $t
                if ($hdr) { $state = 2; $cur = New-Object System.Text.StringBuilder }
            }
            elseif ($mb) { $state = 1; $cur = New-Object System.Text.StringBuilder }
            continue
        }
        if ($state -ne 0) { [void]$cur.Append($t) }
    }
    # END 없이 끝난 마지막 블록도 살린다 (데이터가 온전하면 복원됨).
    if ($state -eq 2) { & $closePart }
    elseif ($state -eq 1) { & $closeMessage }
    return $result
}
# 블록 앞에 인사말/본문 같은 잡담이 붙어 있어도 찾아내야 하므로
# 앞부분 넉넉히(64KB) 훑어서 BEGIN 표식을 찾는다.
function Test-IsArmorFile([string]$File) {
    $fs = [System.IO.File]::OpenRead($File)
    try {
        $want = [int][Math]::Min(65536, $fs.Length)
        if ($want -lt 20) { return $false }
        $buf = New-Object byte[] $want
        $read = 0
        while ($read -lt $want) {
            $k = $fs.Read($buf, $read, $want - $read)
            if ($k -le 0) { break }
            $read += $k
        }
        $head = [System.Text.Encoding]::ASCII.GetString($buf, 0, $read)
        # 공백/줄바꿈이 뭉개진 표식도 인식(C# LooksLikeArmor 와 대칭).
        return $RX_MARKER.IsMatch($head)
    } finally { $fs.Dispose() }
}

# 컨테이너에 적힌 이름은 남이 만들어 보낸 것일 수 있다.
# 드라이브 문자, 루트 슬래시, ".." 를 전부 걷어내 저장 폴더 밖으로 못 쓰게 만든다.
# 파일명에 못 쓰는 글자 전부를 한 문자 클래스로. 예전에는 경로 조각마다 41글자를 하나씩 Replace 했다.
$RX_INVALID_NAME = New-Object System.Text.RegularExpressions.Regex(
    '[' + ((([System.IO.Path]::GetInvalidFileNameChars()) | ForEach-Object { '\u{0:X4}' -f [int]$_ }) -join '') + ']')

function ConvertTo-SafeRelativePath([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return 'restored.bin' }
    $keep = New-Object System.Collections.Generic.List[string]
    foreach ($raw in ($Name -replace '\\', '/').Split('/')) {
        $seg = $raw.Trim()
        if ($seg.Length -eq 0) { continue }
        if ($seg -eq '.' -or $seg -eq '..') { continue }
        if ($seg.Contains(':')) { continue }
        $seg = $RX_INVALID_NAME.Replace($seg, '_')
        $seg = $seg.Trim().TrimEnd('.')
        if ($seg.Length -eq 0) { continue }
        $keep.Add($seg)
    }
    if ($keep.Count -eq 0) { return 'restored.bin' }
    if ($keep.Count -gt 32) { $keep = $keep.GetRange($keep.Count - 32, 32) }
    return ($keep -join [System.IO.Path]::DirectorySeparatorChar)
}

# 파일마다 불리므로 cmdlet(Test-Path/New-Item/Join-Path) 대신 .NET 호출만 쓴다.
# $Desired 는 절대 경로여야 한다(호출하는 쪽이 이미 확정해 넘긴다).
function Resolve-OutPath([string]$Desired, [bool]$AllowOverwrite) {
    if (-not [System.IO.Path]::IsPathRooted($Desired)) { $Desired = ConvertTo-AbsolutePath $Desired }
    $dir = [System.IO.Path]::GetDirectoryName($Desired)
    New-Dir $dir
    if ($AllowOverwrite -or -not [System.IO.File]::Exists($Desired)) { return $Desired }
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($Desired)
    $ext  = [System.IO.Path]::GetExtension($Desired)
    for ($i = 1; $i -lt 1000; $i++) {
        $cand = [System.IO.Path]::Combine($dir, ('{0} ({1}){2}' -f $stem, $i, $ext))
        if (-not [System.IO.File]::Exists($cand)) { return $cand }
    }
    throw '출력 파일 이름을 정할 수 없습니다.'
}

# 저장 폴더 안의 최종 경로. 밖으로 나가거나 너무 길면 예외 (C# ResolveNonClobbering 과 같은 규칙).
function Resolve-RestorePath([string]$Root, [string]$StoredName, [bool]$AllowOverwrite) {
    $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($Root, (ConvertTo-SafeRelativePath $StoredName)))
    if (-not $full.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('저장 폴더 밖으로 나가는 경로입니다: {0}' -f $StoredName)
    }
    if ($full.Length -ge 250) {
        throw ('경로가 너무 깁니다. 저장 폴더를 더 짧은 곳으로 지정하세요: {0}' -f $StoredName)
    }
    return (Resolve-OutPath $full $AllowOverwrite)
}

# ---------------------------------------------------------------- 아카이브 페이로드
#   [int32 개수]
#   반복: [uint16 이름길이][이름 UTF-8][int64 크기][내용]
# 항목별 해시는 두지 않는다. HMAC 이 이미 전체를 보증한다.
function New-ArchivePayload($Entries) {
    $ms = New-Object System.IO.MemoryStream
    $cnt = [BitConverter]::GetBytes([int]$Entries.Count)
    $ms.Write($cnt, 0, 4)
    foreach ($e in $Entries) {
        $nb = [System.Text.Encoding]::UTF8.GetBytes($e.Name)
        if ($nb.Length -gt 65535) { throw ('파일 이름이 너무 깁니다: {0}' -f $e.Name) }
        $ms.WriteByte([byte]($nb.Length -band 0xFF))
        $ms.WriteByte([byte](($nb.Length -shr 8) -band 0xFF))
        $ms.Write($nb, 0, $nb.Length)
        $len = [BitConverter]::GetBytes([long]$e.Data.Length)
        $ms.Write($len, 0, 8)
        if ($e.Data.Length -gt 0) { $ms.Write($e.Data, 0, $e.Data.Length) }
    }
    $out = $ms.ToArray(); $ms.Dispose()
    return ,$out
}

function Read-ArchivePayload([byte[]]$Payload) {
    $list = New-Object System.Collections.Generic.List[object]
    $pos = 0
    if ($Payload.Length -lt 4) { throw '아카이브가 손상되었습니다.' }
    $count = [BitConverter]::ToInt32($Payload, $pos); $pos += 4
    if ($count -lt 0 -or $count -gt 1000000) { throw ('아카이브 항목 수가 이상합니다: {0}' -f $count) }

    for ($i = 0; $i -lt $count; $i++) {
        if ($pos + 2 -gt $Payload.Length) { throw '아카이브가 잘렸습니다 (이름 길이).' }
        # PowerShell 에서 [byte] -shl 8 은 바이트 폭 안에서 연산해 항상 0 이 된다.
        # 직접 비트 연산하지 말고 BitConverter 를 쓴다. (이름이 255바이트를 넘으면 깨졌던 원인)
        $nl = [int][BitConverter]::ToUInt16($Payload, $pos); $pos += 2
        if ($pos + $nl -gt $Payload.Length) { throw '아카이브가 잘렸습니다 (이름).' }
        $nm = [System.Text.Encoding]::UTF8.GetString($Payload, $pos, $nl); $pos += $nl
        if ($pos + 8 -gt $Payload.Length) { throw '아카이브가 잘렸습니다 (크기).' }
        $dl = [BitConverter]::ToInt64($Payload, $pos); $pos += 8
        if ($dl -lt 0 -or $dl -gt [int]::MaxValue) { throw '아카이브 항목 크기가 이상합니다.' }
        if ($pos + $dl -gt $Payload.Length) { throw '아카이브가 잘렸습니다 (내용).' }
        $data = New-Object byte[] $dl
        if ($dl -gt 0) { [Array]::Copy($Payload, $pos, $data, 0, [int]$dl) }
        $pos += [int]$dl
        $list.Add(@{ Name = $nm; Data = $data })
    }
    return ,$list
}

# ================================================================ 암호화
function Invoke-EncryptMode {
    Write-Head '암호화 (Encrypt)'

    $archFlag = 0
    $fileCount = 1

    if (-not [string]::IsNullOrWhiteSpace($script:Folder)) {
        # ---- 폴더 통째로: 아카이브 1블록
        $src = (Resolve-Path -LiteralPath $script:Folder).ProviderPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        $parent = [System.IO.Path]::GetDirectoryName($src)
        $entries = New-Object System.Collections.Generic.List[object]
        $origLen = 0
        foreach ($f in (Get-ChildItem -LiteralPath $src -File -Recurse)) {
            $rel = $f.FullName
            if ($parent -and $rel.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) {
                $rel = $rel.Substring($parent.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar)
            }
            $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
            $origLen += $bytes.Length
            $entries.Add(@{ Name = $rel; Data = $bytes })
        }
        if ($entries.Count -eq 0) { throw '폴더에 파일이 없습니다.' }
        $fileCount = $entries.Count
        $payload   = New-ArchivePayload $entries
        $origHash  = Get-Sha256Bytes $payload
        $archFlag  = $FLAG_ARCHIVE
    }
    else {
        $src = $script:Path
        if ([string]::IsNullOrWhiteSpace($src)) {
            $src = Read-FilePath '  암호화할 파일 경로를 입력하세요 (드래그 & 드롭 가능)'
            if ($null -eq $src) { return 2 }
        } else {
            $src = Get-CleanPath $src
            if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { throw ('파일 없음: {0}' -f $src) }
            $src = (Resolve-Path -LiteralPath $src).ProviderPath
        }

        $plain    = [System.IO.File]::ReadAllBytes($src)
        $origLen  = $plain.Length
        $origHash = Get-Sha256Bytes $plain

        # 페이로드 = [이름길이 2B][원본 파일명 UTF-8][원본 바이트]
        # 파일명까지 암호화 대상에 포함시켜 붙여넣기만으로 원래 이름으로 복원되게 한다.
        $storeName = if (-not [string]::IsNullOrWhiteSpace($script:Name)) { $script:Name } else { [System.IO.Path]::GetFileName($src) }
        $nameBytes = [System.Text.Encoding]::UTF8.GetBytes($storeName)
        if ($nameBytes.Length -gt 65535) { throw '파일 이름이 너무 깁니다.' }
        $payload = New-Object byte[] (2 + $nameBytes.Length + $origLen)
        [Array]::Copy([BitConverter]::GetBytes([uint16]$nameBytes.Length), 0, $payload, 0, 2)
        [Array]::Copy($nameBytes, 0, $payload, 2, $nameBytes.Length)
        if ($origLen -gt 0) { [Array]::Copy($plain, 0, $payload, 2 + $nameBytes.Length, $origLen) }
    }

    $flags = $archFlag
    $body  = $payload
    if ($script:Compress -eq 'On') {
        $z = Compress-Bytes $payload
        if ($z.Length -lt $payload.Length) {
            $body  = $z
            $flags = $flags -bor $FLAG_ZIP
        } elseif (-not $script:Quiet) {
            Write-Host '  [정보] 압축 효과가 없어 원본을 그대로 암호화합니다.' -ForegroundColor DarkGray
        }
    }
    $bodyLen = $body.Length

    $useSha256 = $KDF256
    if ($useSha256) { $flags = $flags -bor $FLAG_KDF256 }

    $salt = Get-RandomBytes 16
    $iv   = Get-RandomBytes 16
    $keys = Get-DerivedKeys $KEY $salt $ITERATIONS $useSha256

    $cipher = Invoke-Aes $body $keys.Aes $iv $true

    $header = New-Object byte[] $HDR_SIZE
    [Array]::Copy($MAGIC, 0, $header, 0, 8)
    $header[$OFF_VER]   = [byte]$VERSION
    $header[$OFF_FLAGS] = [byte]$flags
    [Array]::Copy($salt, 0, $header, $OFF_SALT, 16)
    [Array]::Copy($iv,   0, $header, $OFF_IV,   16)
    [Array]::Copy([BitConverter]::GetBytes([int]$ITERATIONS), 0, $header, $OFF_ITER, 4)
    [Array]::Copy($origHash, 0, $header, $OFF_HASH, 32)

    $h80 = New-Object byte[] 80
    [Array]::Copy($header, 0, $h80, 0, 80)
    $mac = Get-HmacTag $keys.Hmac $h80 $cipher
    [Array]::Copy($mac, 0, $header, $OFF_HMAC, 32)

    $container = New-Object byte[] ($HDR_SIZE + $cipher.Length)
    [Array]::Copy($header, 0, $container, 0, $HDR_SIZE)
    [Array]::Copy($cipher, 0, $container, $HDR_SIZE, $cipher.Length)

    [Array]::Clear($keys.Aes, 0, 32)
    [Array]::Clear($keys.Hmac, 0, 32)

    $dest = $script:Out
    if ([string]::IsNullOrWhiteSpace($dest)) {
        if ($script:Armor) { $dest = $src + '.enc.txt' } else { $dest = $src + '.enc' }
    }
    $dest = Resolve-OutPath $dest ([bool]$script:Force)

    # ---- 조각내기
    if ($script:Armor -and $script:Split -gt 0) {
        $parts = ConvertTo-ArmorParts $container $script:Split $script:Width
        $dir  = [System.IO.Path]::GetDirectoryName($dest)
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($dest)
        if ($stem.ToLowerInvariant().EndsWith('.enc')) { $stem = $stem.Substring(0, $stem.Length - 4) }
        $written = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $parts.Count; $i++) {
            $pn = '{0} [{1}of{2}].txt' -f $stem, ($i + 1), $parts.Count
            $pp = Resolve-OutPath (Join-Path $dir $pn) ([bool]$script:Force)
            [System.IO.File]::WriteAllText($pp, ($parts[$i] + "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
            $written.Add($pp)
        }
        if ($script:Quiet) { $script:ResultPath = $written; return 0 }
        Write-Host ''
        Write-Host ('  [완료] 조각 {0}개로 나눴습니다.' -f $parts.Count) -ForegroundColor Green
        Write-Host ('    입력        : {0}' -f $src)
        Write-Host ('    위치        : {0}' -f $dir)
        foreach ($wp in $written) { Write-Host ('      {0}' -f [System.IO.Path]::GetFileName($wp)) -ForegroundColor Gray }
        Write-Host ('    조각당 한도 : {0:N0} 자' -f $script:Split)
        return 0
    }

    $armorLines = $null
    if ($script:Armor) {
        $armorLines = ConvertTo-Armor $container $script:Width
        [System.IO.File]::WriteAllLines($dest, $armorLines, (New-Object System.Text.UTF8Encoding($false)))
    } else {
        [System.IO.File]::WriteAllBytes($dest, $container)
    }
    $outLen = (Get-Item -LiteralPath $dest).Length

    if ($script:Quiet) {
        $script:ResultPath = $dest
        return 0
    }

    Write-Host ''
    if ($archFlag) { Write-Host ('  [완료] 아카이브 1블록으로 묶었습니다 (파일 {0}개).' -f $fileCount) -ForegroundColor Green }
    else           { Write-Host '  [완료] 암호화되었습니다.' -ForegroundColor Green }
    Write-Host ('    입력        : {0}' -f $src)
    Write-Host ('    출력        : {0}' -f $dest)
    Write-Host ('    원본 크기   : {0}' -f (Format-Size $origLen))
    if ($flags -band $FLAG_ZIP) {
        $ratio = 100 - [math]::Round(($bodyLen / [double]$payload.Length) * 100, 1)
        Write-Host ('    압축 후     : {0}   (-{1}%)' -f (Format-Size $bodyLen), $ratio) -ForegroundColor Green
    } else {
        Write-Host  '    압축        : 미사용'
    }
    if ($origLen -gt 0) {
        Write-Host ('    최종 크기   : {0}   (원본 대비 {1}%)' -f (Format-Size $outLen), [math]::Round(($outLen / [double]$origLen) * 100, 1))
    } else {
        Write-Host ('    최종 크기   : {0}   (빈 파일)' -f (Format-Size $outLen))
    }
    if ($null -ne $armorLines) {
        Write-Host ('    출력 형식   : 텍스트(Base64) / {0}줄 / 줄폭 {1}' -f $armorLines.Count, $script:Width) -ForegroundColor Green
    } else {
        Write-Host  '    출력 형식   : 바이너리'
    }
    Write-Host ('    원본 SHA256 : {0}' -f (ConvertTo-HexString $origHash)) -ForegroundColor DarkGray
    return 0
}

# ================================================================ 복호화
# 텍스트 하나에 블록이 여러 개(통짜 여러 개, 조각 묶음 여러 개, 섞임) 있어도 전부 복원한다.
# 예전에는 첫 조각 묶음 또는 첫 MESSAGE 블록 하나만 풀었다.
# 종료 코드: 0 전부 성공 · 4 손상된 블록이 있음 · 5 일부 파일/조각을 건너뜀
function Invoke-DecryptMode {
    Write-Head '복호화 (Decrypt)'

    $src = $script:Path
    if ([string]::IsNullOrWhiteSpace($src)) {
        $src = Read-FilePath '  복호화할 파일 경로를 입력하세요 (드래그 & 드롭 가능)'
        if ($null -eq $src) { return 2 }
    } else {
        $src = Get-CleanPath $src
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { throw ('파일 없음: {0}' -f $src) }
        $src = (Resolve-Path -LiteralPath $src).ProviderPath
    }

    $dir = if (-not [string]::IsNullOrWhiteSpace($script:OutDir)) { ConvertTo-AbsolutePath $script:OutDir }
           else { [System.IO.Path]::GetDirectoryName($src) }

    # 복원할 컨테이너 목록: @{ Bytes; Fmt }
    $containers = New-Object System.Collections.Generic.List[object]
    $rc = 0
    if (Test-IsArmorFile $src) {
        $blk = Get-ArmorBlocks ([System.IO.File]::ReadAllText($src))
        $incomplete = New-Object System.Collections.Generic.List[string]
        # 주의: PowerShell 변수명은 대소문자를 구분하지 않는다. 여기서 $key 라고 쓰면
        # 스크립트 전역의 고정키 $KEY 를 덮어써서 키 유도가 통째로 망가진다. 다른 이름을 쓸 것.
        foreach ($gid in @($blk.Parts.Keys)) {
            $g = $blk.Parts[$gid]
            $missing = @(1..$g.Total | Where-Object { -not $g.Chunks.ContainsKey($_) })
            if ($missing.Count -gt 0) {
                $incomplete.Add(('{0}/{1} 모임, 없는 것 {2}' -f ($g.Total - $missing.Count), $g.Total, ($missing -join ',')))
                continue
            }
            $joined = New-Object System.Text.StringBuilder
            for ($i = 1; $i -le $g.Total; $i++) { [void]$joined.Append($g.Chunks[$i]) }
            $containers.Add(@{ B64 = $joined.ToString(); Fmt = ('텍스트 조각 {0}개' -f $g.Total) })
        }
        foreach ($m in $blk.Messages) { $containers.Add(@{ B64 = $m; Fmt = '텍스트(Base64)' }) }

        if ($containers.Count -eq 0) {
            if ($incomplete.Count -gt 0) { throw ('조각이 모자랍니다: {0}' -f ($incomplete -join ' / ')) }
            throw 'ARMOR 헤더를 찾지 못했습니다.'
        }
        if ($incomplete.Count -gt 0) {
            $rc = 5
            if (-not $script:Quiet) { Write-Host ('  [경고] 다 모이지 않은 조각 묶음은 건너뜁니다: {0}' -f ($incomplete -join ' / ')) -ForegroundColor Yellow }
        }
    } else {
        $containers.Add(@{ Bytes = [System.IO.File]::ReadAllBytes($src); Fmt = '바이너리' })
    }

    $script:ResultPath = New-Object System.Collections.Generic.List[string]
    $damaged = $false; $errored = $false; $lastError = $null
    for ($ci = 0; $ci -lt $containers.Count; $ci++) {
        $c = $containers[$ci]
        try {
            $bytes = if ($c.ContainsKey('Bytes')) { $c.Bytes } else { [Convert]::FromBase64String($c.B64) }
            $r = Expand-Container $bytes $src $c.Fmt $dir
        } catch {
            # 블록 하나가 이상해도(컨테이너가 아님, 잘린 base64 등) 나머지 블록은 계속 복원한다.
            $lastError = $_.Exception.Message
            if (-not $script:Quiet -and $containers.Count -gt 1) {
                Write-Host ('  [실패] {0}번째 블록: {1}' -f ($ci + 1), $lastError) -ForegroundColor Red
            }
            $r = 1
        }
        if ($r -eq 4) { $damaged = $true }
        elseif ($r -eq 1) { $errored = $true }
        elseif ($r -eq 5 -and $rc -eq 0) { $rc = 5 }
    }
    # 4(인증 실패 = 손상/변조) > 1(형식 오류) > 5(일부 건너뜀) > 0
    if ($damaged) { return 4 }
    if ($errored) {
        # 블록이 하나뿐이면 예전처럼 그 오류를 그대로 알린다(main 이 [오류] 로 찍고 1 로 끝낸다).
        if ($containers.Count -eq 1) { throw $lastError }
        return 1
    }
    return $rc
}

# 컨테이너 하나를 풀어 $dir 에 쓴다. 쓴 경로는 $script:ResultPath 에 더한다.
function Expand-Container([byte[]]$container, [string]$src, [string]$fmt, [string]$dir) {
    if ($container.Length -lt $HDR_SIZE) { throw 'FileCrypt 컨테이너가 아닙니다 (파일이 너무 작음).' }
    for ($i = 0; $i -lt 8; $i++) {
        if ($container[$i] -ne $MAGIC[$i]) { throw 'FileCrypt 컨테이너가 아닙니다 (매직 불일치).' }
    }
    if ($container[$OFF_VER] -ne $VERSION) { throw ('지원하지 않는 컨테이너 버전: {0}' -f $container[$OFF_VER]) }

    $flags = [int]$container[$OFF_FLAGS]
    $salt = New-Object byte[] 16; [Array]::Copy($container, $OFF_SALT, $salt, 0, 16)
    $iv   = New-Object byte[] 16; [Array]::Copy($container, $OFF_IV,   $iv,   0, 16)
    $iter = [BitConverter]::ToInt32($container, $OFF_ITER)
    $origHash = New-Object byte[] 32; [Array]::Copy($container, $OFF_HASH, $origHash, 0, 32)
    $mac      = New-Object byte[] 32; [Array]::Copy($container, $OFF_HMAC, $mac,      0, 32)

    $useSha256 = [bool]($flags -band $FLAG_KDF256)
    if ($useSha256 -and -not $KDF256) {
        throw '이 파일은 PBKDF2-SHA256 으로 생성되었으나 현재 런타임이 지원하지 않습니다 (.NET Framework 4.7.2+ 필요).'
    }

    $keys = Get-DerivedKeys $KEY $salt $iter $useSha256

    # 헤더 0..79 와 암호문(112..)을 컨테이너에서 바로 읽는다(따로 복사하지 않음).
    $calc = Get-HmacTag $keys.Hmac $container $container $HDR_SIZE
    if (-not (Test-BytesEqual $calc $mac)) {
        [Array]::Clear($keys.Aes, 0, 32)
        [Array]::Clear($keys.Hmac, 0, 32)
        if (-not $script:Quiet) {
            Write-Host ''
            Write-Host '  [실패] 인증 검증(HMAC-SHA256) 불일치.' -ForegroundColor Red
            Write-Host '         데이터가 손상되었거나 이 도구로 만든 것이 아닙니다.' -ForegroundColor Red
        }
        return 4
    }

    $body = Invoke-Aes $container $keys.Aes $iv $false $HDR_SIZE ($container.Length - $HDR_SIZE)
    [Array]::Clear($keys.Aes, 0, 32)
    [Array]::Clear($keys.Hmac, 0, 32)

    if ($flags -band $FLAG_ZIP) { $payload = Expand-Bytes $body } else { $payload = $body }

    New-Dir $dir
    $root = [System.IO.Path]::GetFullPath($dir).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    # ---- 아카이브: 파일 여러 개
    if ($flags -band $FLAG_ARCHIVE) {
        if (-not (Test-BytesEqual (Get-Sha256Bytes $payload) $origHash)) {
            throw '복원했지만 아카이브 해시가 일치하지 않습니다.'
        }
        $items = Read-ArchivePayload $payload
        $written = New-Object System.Collections.Generic.List[string]
        $skipped = New-Object System.Collections.Generic.List[string]
        foreach ($it in $items) {
            try {
                $d = Resolve-RestorePath $root $it.Name ([bool]$script:Force)
                [System.IO.File]::WriteAllBytes($d, $it.Data)
                $written.Add($d)
            } catch {
                # 파일 하나가 실패해도 나머지는 계속 복원한다.
                $skipped.Add(('{0} : {1}' -f $it.Name, $_.Exception.Message))
            }
        }
        $script:ResultPath.AddRange($written)
        if (-not $script:Quiet) {
            Write-Host ''
            Write-Host ('  [완료] 아카이브에서 파일 {0}개를 복원했습니다.' -f $written.Count) -ForegroundColor Green
            Write-Host ('    입력          : {0}   ({1})' -f $src, $fmt)
            Write-Host ('    위치          : {0}' -f $dir)
            foreach ($w in $written) { Write-Host ('      {0}' -f [System.IO.Path]::GetFileName($w)) -ForegroundColor Gray }
            if ($skipped.Count -gt 0) {
                Write-Host ('    건너뜀        : {0}개' -f $skipped.Count) -ForegroundColor Yellow
                foreach ($sk in $skipped) { Write-Host ('      {0}' -f $sk) -ForegroundColor Yellow }
            }
            Write-Host '    무결성        : HMAC-SHA256 + 페이로드 SHA-256 검증 통과' -ForegroundColor Green
        }
        if ($skipped.Count -gt 0) { return 5 }
        return 0
    }

    if ($payload.Length -lt 2) { throw '페이로드가 손상되었습니다.' }
    $nameLen = [BitConverter]::ToUInt16($payload, 0)
    if ($payload.Length -lt (2 + $nameLen)) { throw '페이로드가 손상되었습니다 (이름 길이).' }
    $origName = [System.Text.Encoding]::UTF8.GetString($payload, 2, $nameLen)
    $plain = New-Object byte[] ($payload.Length - 2 - $nameLen)
    if ($plain.Length -gt 0) { [Array]::Copy($payload, 2 + $nameLen, $plain, 0, $plain.Length) }

    $newHash = Get-Sha256Bytes $plain
    $ok = Test-BytesEqual $newHash $origHash

    $dest = $script:Out
    if ([string]::IsNullOrWhiteSpace($dest)) {
        # 컨테이너에 기록된 원본 파일명으로 복원한다.
        $dest = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($root, (ConvertTo-SafeRelativePath $origName)))
        if (-not $dest.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('저장 폴더 밖으로 나가는 경로입니다: {0}' -f $origName)
        }
        # 암호문 파일 자신을 덮어쓰지 않도록 보호
        if ($dest -eq $src) { $dest = $dest + '.restored' }
    }
    $dest = Resolve-OutPath $dest ([bool]$script:Force)
    [System.IO.File]::WriteAllBytes($dest, $plain)

    if ($script:Quiet) {
        if ($ok) { $script:ResultPath.Add($dest); return 0 }
        return 5
    }

    Write-Host ''
    if ($ok) { Write-Host '  [완료] 복호화되었습니다.' -ForegroundColor Green }
    else     { Write-Host '  [경고] 복호화는 되었으나 원본 해시가 일치하지 않습니다!' -ForegroundColor Red }
    Write-Host ('    입력          : {0}   ({1})' -f $src, $fmt)
    Write-Host ('    원본 파일명   : {0}' -f $origName)
    Write-Host ('    출력          : {0}' -f $dest)
    Write-Host ('    복원 크기     : {0}' -f (Format-Size $plain.Length))
    if ($flags -band $FLAG_ZIP) { Write-Host '    압축 해제     : 예 (Deflate)' } else { Write-Host '    압축 해제     : 아니오' }
    Write-Host ('    기록된 SHA256 : {0}' -f (ConvertTo-HexString $origHash)) -ForegroundColor DarkGray
    Write-Host ('    복원된 SHA256 : {0}' -f (ConvertTo-HexString $newHash))  -ForegroundColor DarkGray
    if ($ok) {
        $script:ResultPath.Add($dest)
        Write-Host '    무결성        : 원본과 100% 일치 (비트 단위 동일)' -ForegroundColor Green
        return 0
    }
    return 5
}

# ================================================================ main
# Quiet 모드에서 결과 경로를 담아 두었다가 마지막에 한 번만 출력한다.
# (함수 안에서 Write-Output 하면 함수의 return 값과 섞인다)
$ResultPath = $null
$exitCode = 0
try {
    if ([string]::IsNullOrWhiteSpace($Mode)) {
        Write-Head '모드 선택'
        Write-Host '   1) 암호화 (Encrypt)'
        Write-Host '   2) 복호화 (Decrypt)'
        Write-Host ''
        $sel = Read-Host '  번호 선택'
        switch ($sel.Trim()) {
            '1'     { $Mode = 'Encrypt' }
            '2'     { $Mode = 'Decrypt' }
            default { Write-Host '  [취소]' -ForegroundColor Yellow; exit 2 }
        }
    }
    if ($Mode -eq 'Encrypt') { $exitCode = Invoke-EncryptMode } else { $exitCode = Invoke-DecryptMode }
}
catch {
    # Quiet 모드에서는 호출자(simple.ps1)가 자기 메시지를 내므로 조용히 종료코드만 남긴다.
    if (-not $Quiet) {
        Write-Host ''
        Write-Host ('  [오류] {0}' -f $_.Exception.Message) -ForegroundColor Red
    }
    $exitCode = 1
}
if (-not $Quiet) { Write-Host '' }
if ($Quiet -and $ResultPath) { Write-Output $ResultPath }
exit $exitCode

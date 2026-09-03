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
    [string]$Password,
    # 컨테이너에 기록할 이름. 폴더 구조를 보존하려면 상대 경로를 준다 (예: "sub/a.cs").
    [string]$Name,

    [ValidateSet('On','Off')]
    [string]$Compress = 'On',

    [switch]$Armor,
    [int]$Width = 76,
    [int]$Iterations = 200000,
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
$ARMOR_BEGIN = '-----BEGIN FCRYPT MESSAGE-----'
$ARMOR_END   = '-----END FCRYPT MESSAGE-----'

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

function Read-Secret([string]$Prompt) {
    $ss = Read-Host $Prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss)
    try   { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
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
function Invoke-Aes([byte[]]$Data, [byte[]]$Key, [byte[]]$Iv, [bool]$Encrypting) {
    $aes = New-Object System.Security.Cryptography.AesManaged
    try {
        $aes.KeySize   = 256
        $aes.BlockSize = 128
        $aes.Mode      = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding   = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $Key
        $aes.IV  = $Iv
        if ($Encrypting) { $tr = $aes.CreateEncryptor() } else { $tr = $aes.CreateDecryptor() }
        try   { return ,$tr.TransformFinalBlock($Data, 0, $Data.Length) }
        finally { $tr.Dispose() }
    } finally { $aes.Dispose() }
}

function Get-HmacTag([byte[]]$Key, [byte[]]$Header80, [byte[]]$Cipher) {
    $h = New-Object System.Security.Cryptography.HMACSHA256(,$Key)
    try {
        $null = $h.TransformBlock($Header80, 0, $Header80.Length, $null, 0)
        $null = $h.TransformFinalBlock($Cipher, 0, $Cipher.Length)
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

function ConvertFrom-Armor([string[]]$Lines) {
    $sb = New-Object System.Text.StringBuilder
    $inside = $false
    foreach ($l in $Lines) {
        $t = $l.Trim()
        if ($t.StartsWith('-----BEGIN FCRYPT')) { $inside = $true; continue }
        if ($t.StartsWith('-----END FCRYPT'))   { break }
        if (-not $inside -or $t.Length -eq 0) { continue }
        # 메일/채팅 클라이언트가 끼워넣는 제로폭 문자, 줄바꿈, 공백 등을 걸러낸다.
        # Base64 알파벳이 아닌 문자는 버린다. 실제 데이터가 상했다면 뒤의 HMAC 이 잡는다.
        foreach ($c in $t.ToCharArray()) {
            if (($c -ge 'A' -and $c -le 'Z') -or ($c -ge 'a' -and $c -le 'z') -or
                ($c -ge '0' -and $c -le '9') -or $c -eq '+' -or $c -eq '/' -or $c -eq '=') {
                $null = $sb.Append($c)
            }
        }
    }
    if (-not $inside) { throw 'ARMOR 헤더를 찾지 못했습니다.' }
    return ,[Convert]::FromBase64String($sb.ToString())
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
        return $head.Contains('-----BEGIN FCRYPT')
    } finally { $fs.Dispose() }
}

# 컨테이너에 적힌 이름은 남이 만들어 보낸 것일 수 있다.
# 드라이브 문자, 루트 슬래시, ".." 를 전부 걷어내 저장 폴더 밖으로 못 쓰게 만든다.
function ConvertTo-SafeRelativePath([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return 'restored.bin' }
    $invalid = [System.IO.Path]::GetInvalidFileNameChars()
    $keep = New-Object System.Collections.Generic.List[string]
    foreach ($raw in ($Name -replace '\\', '/').Split('/')) {
        $seg = $raw.Trim()
        if ($seg.Length -eq 0) { continue }
        if ($seg -eq '.' -or $seg -eq '..') { continue }
        if ($seg.Contains(':')) { continue }
        foreach ($bad in $invalid) { $seg = $seg.Replace($bad, '_') }
        $seg = $seg.Trim().TrimEnd('.')
        if ($seg.Length -eq 0) { continue }
        $keep.Add($seg)
    }
    if ($keep.Count -eq 0) { return 'restored.bin' }
    if ($keep.Count -gt 32) { $keep = $keep.GetRange($keep.Count - 32, 32) }
    return ($keep -join [System.IO.Path]::DirectorySeparatorChar)
}

function Resolve-OutPath([string]$Desired, [bool]$AllowOverwrite) {
    $Desired = ConvertTo-AbsolutePath $Desired
    $parent = [System.IO.Path]::GetDirectoryName($Desired)
    if ($parent -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    if (-not (Test-Path -LiteralPath $Desired)) { return $Desired }
    if ($AllowOverwrite) { return $Desired }
    $dir  = [System.IO.Path]::GetDirectoryName($Desired)
    $name = [System.IO.Path]::GetFileNameWithoutExtension($Desired)
    $ext  = [System.IO.Path]::GetExtension($Desired)
    for ($i = 1; $i -lt 1000; $i++) {
        $cand = Join-Path $dir ('{0} ({1}){2}' -f $name, $i, $ext)
        if (-not (Test-Path -LiteralPath $cand)) { return $cand }
    }
    throw '출력 파일 이름을 정할 수 없습니다.'
}

# ================================================================ 암호화
function Invoke-EncryptMode {
    Write-Head '암호화 (Encrypt)'

    $src = $script:Path
    if ([string]::IsNullOrWhiteSpace($src)) {
        $src = Read-FilePath '  암호화할 파일 경로를 입력하세요 (드래그 & 드롭 가능)'
        if ($null -eq $src) { return 2 }
    } else {
        $src = Get-CleanPath $src
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { throw ('파일 없음: {0}' -f $src) }
        $src = (Resolve-Path -LiteralPath $src).ProviderPath
    }

    $pw = $script:Password
    if ([string]::IsNullOrWhiteSpace($pw)) {
        $pw = Read-Secret '  암호 입력'
        if ([string]::IsNullOrWhiteSpace($pw)) { Write-Host '  [취소] 암호가 비었습니다.' -ForegroundColor Yellow; return 2 }
        $pw2 = Read-Secret '  암호 확인'
        if ($pw -cne $pw2) { Write-Host '  [오류] 두 암호가 일치하지 않습니다.' -ForegroundColor Red; return 3 }
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

    $flags = 0
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

    $useSha256 = Test-Sha256Kdf
    if ($useSha256) { $flags = $flags -bor $FLAG_KDF256 }

    $salt = Get-RandomBytes 16
    $iv   = Get-RandomBytes 16
    $keys = Get-DerivedKeys $pw $salt $script:Iterations $useSha256

    $cipher = Invoke-Aes $body $keys.Aes $iv $true

    $header = New-Object byte[] $HDR_SIZE
    [Array]::Copy($MAGIC, 0, $header, 0, 8)
    $header[$OFF_VER]   = [byte]$VERSION
    $header[$OFF_FLAGS] = [byte]$flags
    [Array]::Copy($salt, 0, $header, $OFF_SALT, 16)
    [Array]::Copy($iv,   0, $header, $OFF_IV,   16)
    [Array]::Copy([BitConverter]::GetBytes([int]$script:Iterations), 0, $header, $OFF_ITER, 4)
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
    Write-Host '  [완료] 암호화되었습니다.' -ForegroundColor Green
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
    Write-Host ''
    Write-Host '  * 암호를 분실하면 복구 방법이 없습니다.' -ForegroundColor Yellow
    return 0
}

# ================================================================ 복호화
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

    if (Test-IsArmorFile $src) {
        $container = ConvertFrom-Armor ([System.IO.File]::ReadAllLines($src))
        $fmt = '텍스트(Base64)'
    } else {
        $container = [System.IO.File]::ReadAllBytes($src)
        $fmt = '바이너리'
    }

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
    $cipher = New-Object byte[] ($container.Length - $HDR_SIZE)
    [Array]::Copy($container, $HDR_SIZE, $cipher, 0, $cipher.Length)

    $useSha256 = [bool]($flags -band $FLAG_KDF256)
    if ($useSha256 -and -not (Test-Sha256Kdf)) {
        throw '이 파일은 PBKDF2-SHA256 으로 생성되었으나 현재 런타임이 지원하지 않습니다 (.NET Framework 4.7.2+ 필요).'
    }

    $pw = $script:Password
    if ([string]::IsNullOrWhiteSpace($pw)) {
        $pw = Read-Secret '  암호 입력'
        if ([string]::IsNullOrWhiteSpace($pw)) { Write-Host '  [취소] 암호가 비었습니다.' -ForegroundColor Yellow; return 2 }
    }

    $keys = Get-DerivedKeys $pw $salt $iter $useSha256

    $h80 = New-Object byte[] 80
    [Array]::Copy($container, 0, $h80, 0, 80)
    $calc = Get-HmacTag $keys.Hmac $h80 $cipher
    if (-not (Test-BytesEqual $calc $mac)) {
        [Array]::Clear($keys.Aes, 0, 32)
        [Array]::Clear($keys.Hmac, 0, 32)
        if (-not $script:Quiet) {
            Write-Host ''
            Write-Host '  [실패] 인증 검증(HMAC-SHA256) 불일치.' -ForegroundColor Red
            Write-Host '         암호가 틀렸거나, 전송 중 파일이 손상/변조되었습니다.' -ForegroundColor Red
        }
        return 4
    }

    $body = Invoke-Aes $cipher $keys.Aes $iv $false
    [Array]::Clear($keys.Aes, 0, 32)
    [Array]::Clear($keys.Hmac, 0, 32)

    if ($flags -band $FLAG_ZIP) { $payload = Expand-Bytes $body } else { $payload = $body }

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
        $dir = if (-not [string]::IsNullOrWhiteSpace($script:OutDir)) {
            ConvertTo-AbsolutePath $script:OutDir
        } else {
            [System.IO.Path]::GetDirectoryName($src)
        }
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
        $safeName = ConvertTo-SafeRelativePath $origName
        $dest = Join-Path $dir $safeName
        $root = (ConvertTo-AbsolutePath $dir).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not (ConvertTo-AbsolutePath $dest).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('저장 폴더 밖으로 나가는 경로입니다: {0}' -f $origName)
        }
        # 암호문 파일 자신을 덮어쓰지 않도록 보호
        if ((ConvertTo-AbsolutePath $dest) -eq $src) { $dest = $dest + '.restored' }
    }
    $dest = Resolve-OutPath $dest ([bool]$script:Force)
    [System.IO.File]::WriteAllBytes($dest, $plain)

    if ($script:Quiet) {
        if ($ok) { $script:ResultPath = $dest; return 0 }
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

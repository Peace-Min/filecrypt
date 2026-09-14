# Length-probe files for a paste-only transport (e.g. a company report field).
#
# Makes real, decryptable FileCrypt blocks of exactly 2000 / 4000 / 8000 / 12000
# characters. Paste each one into the target field to find the largest size that
# fits, then copy it back out and decrypt it to confirm the round trip was lossless.
# The biggest size that both fits AND decrypts is your split size.
#
# ASCII only on purpose: this file must run correctly whether or not it has a BOM.
# (PowerShell 5.1 reads a BOM-less .ps1 as the ANSI codepage, which mangles non-ASCII.)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$EXE  = Join-Path $root 'gui\bin\Release\net48\FileCrypt.exe'
if (-not (Test-Path -LiteralPath $EXE)) { throw ('build not found: ' + $EXE) }

$OUT = Join-Path ([Environment]::GetFolderPath('Desktop')) 'netcus-length-tests'
New-Item -ItemType Directory -Force $OUT | Out-Null

$tmp = Join-Path $env:TEMP ('fc_len_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $tmp | Out-Null
$cp = Join-Path $tmp 'FileCrypt.exe'
Copy-Item -LiteralPath $EXE -Destination $cp -Force
[void][Reflection.Assembly]::LoadFrom($cp)

$FC   = [FileCrypt.FileCryptCore]
$W    = $FC::DefaultWidth
$u8n  = New-Object System.Text.UTF8Encoding($false)
$rand = New-Object System.Random 20260914

# Payload = a readable ASCII sentence + incompressible filler, so the armored
# length tracks the filler size predictably.
function ArmorText([int]$k, [string]$nm, [byte[]]$hd) {
    $filler = New-Object byte[] $k
    if ($k -gt 0) { $rand.NextBytes($filler) }
    $payload = New-Object byte[] ($hd.Length + $k)
    [Array]::Copy($hd, 0, $payload, 0, $hd.Length)
    if ($k -gt 0) { [Array]::Copy($filler, 0, $payload, $hd.Length, $k) }
    return $FC::ToArmor($FC::Encrypt($nm, $payload), $W)
}

Write-Host ''
Write-Host ('{0,-9} {1,-14} {2,-7} {3,-30} {4}' -f 'target', 'actual', 'diff', 'file', 'decode')
Write-Host ('-' * 76)

foreach ($N in 2000, 4000, 8000, 12000) {
    $nm = ('netcus-len-{0}.txt' -f $N)
    $hd = [System.Text.Encoding]::ASCII.GetBytes(
        ('netcus round-trip length test - target {0} chars. If FileCrypt decrypts this file with no error, the paste round-trip was lossless. ' -f $N))

    # Largest filler size whose armored output still fits in N characters.
    $lo = 0; $hi = $N; $bestK = 0
    while ($lo -le $hi) {
        $mid = [int](($lo + $hi) / 2)
        if ((ArmorText $mid $nm $hd).Length -le $N) { $bestK = $mid; $lo = $mid + 1 } else { $hi = $mid - 1 }
    }

    # The search and the final build draw different random bytes, so the result can
    # land a few characters over. Measure what was actually built and shrink if needed.
    $text = ArmorText $bestK $nm $hd
    while ($text.Length -gt $N -and $bestK -gt 0) {
        $bestK -= 4
        if ($bestK -lt 0) { $bestK = 0 }
        $text = ArmorText $bestK $nm $hd
    }

    $f = Join-Path $OUT ('netcus-length-{0}.txt' -f $N)
    [System.IO.File]::WriteAllText($f, $text, $u8n)

    $b = $FC::ExtractBlocks($text); $ok = $false
    if ($b.Count -eq 1) { try { $ok = ($FC::DecryptAll($b[0])[0].FileName -eq $nm) } catch { } }

    Write-Host ('{0,-9:N0} {1,-14:N0} {2,-7} {3,-30} {4}' -f `
        $N, $text.Length, ($text.Length - $N), ('netcus-length-{0}.txt' -f $N), $(if ($ok) { 'OK' } else { 'FAIL' }))
}

Write-Host ''
Write-Host ('saved: ' + $OUT)

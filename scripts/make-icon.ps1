# Generates installer/GameGallery.ico (multi-size, 32bpp DIB entries) from installer/AppIcon.png.
# 32bpp DIB entries (not PNG-in-ICO) are required: the .NET SDK ignores PNG entries in ApplicationIcon.
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Output
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root   = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $root 'installer\AppIcon.png' }
if (-not $Output) { $Output = Join-Path $root 'installer\GameGallery.ico' }

if (-not (Test-Path -LiteralPath $Source)) { throw "Source image not found: $Source" }

$src = [System.Drawing.Image]::FromFile($Source)
Write-Host ("source: {0} ({1}x{2}, {3})" -f (Split-Path -Leaf $Source), $src.Width, $src.Height, $src.PixelFormat)

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$dibs  = @()   # array of byte[], one per size

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $scale = [math]::Min($size / $src.Width, $size / $src.Height)
    $dw = [int][math]::Max(1, [math]::Round($src.Width * $scale))
    $dh = [int][math]::Max(1, [math]::Round($src.Height * $scale))
    $g.DrawImage($src, [int](($size - $dw) / 2), [int](($size - $dh) / 2), $dw, $dh)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER
    $bw.Write([uint32]40)
    $bw.Write([int32]$size)
    $bw.Write([int32]($size * 2))   # XOR + AND
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)
    $bw.Write([uint32]($size * $size * 4))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    # XOR bitmap: BGRA, bottom-up
    for ($y = 0; $y -lt $size; $y++) {
        $srcY = $size - 1 - $y
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $srcY)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }

    # AND mask: 1bpp, rows padded to 32 bits, all transparent (alpha carries the shape)
    $maskRow = [int]([math]::Floor(($size + 31) / 32) * 4)
    $zeros = New-Object byte[] $maskRow
    for ($y = 0; $y -lt $size; $y++) { $bw.Write($zeros, 0, $maskRow) }

    $bw.Flush()
    $data = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose(); $bmp.Dispose()

    if ($null -eq $data -or $data.Length -eq 0) { throw "DIB encoding produced no bytes for ${size}x${size}" }
    $dibs += , $data
    Write-Host ("  {0,3}x{0,-3} -> {1,7:N0} bytes" -f $size, $data.Length)
}

# ICONDIR + ICONDIRENTRYs + image data
$out = New-Object System.IO.MemoryStream
$fw = New-Object System.IO.BinaryWriter($out)
$fw.Write([uint16]0)                  # reserved
$fw.Write([uint16]1)                  # type: icon
$fw.Write([uint16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $d = $dibs[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $fw.Write([byte]$dim)             # width
    $fw.Write([byte]$dim)             # height
    $fw.Write([byte]0)                # palette colours
    $fw.Write([byte]0)                # reserved
    $fw.Write([uint16]1)              # planes
    $fw.Write([uint16]32)             # bpp
    $fw.Write([uint32]$d.Length)
    $fw.Write([uint32]$offset)
    $offset += $d.Length
}
for ($i = 0; $i -lt $sizes.Count; $i++) { $fw.Write($dibs[$i], 0, $dibs[$i].Length) }
$fw.Flush()
[System.IO.File]::WriteAllBytes($Output, $out.ToArray())
$fw.Dispose(); $out.Dispose(); $src.Dispose()

$len = (Get-Item -LiteralPath $Output).Length
Write-Host ("wrote {0} ({1:N1} KB, {2} sizes)" -f $Output, ($len / 1KB), $sizes.Count)

# Read back: verify every directory entry points at real bytes.
$bytes = [System.IO.File]::ReadAllBytes($Output)
$count = [BitConverter]::ToUInt16($bytes, 4)
$ok = $true
for ($i = 0; $i -lt $count; $i++) {
    $b = 6 + ($i * 16)
    $bpp   = [BitConverter]::ToUInt16($bytes, $b + 6)
    $bytesInEntry = [BitConverter]::ToUInt32($bytes, $b + 8)
    $off   = [BitConverter]::ToUInt32($bytes, $b + 12)
    $fits  = ($off + $bytesInEntry) -le $len
    if ((-not $fits) -or $bytesInEntry -eq 0) { $ok = $false }
    Write-Host ("  entry {0}: bpp={1} size={2:N0} offset={3} inFile={4}" -f $i, $bpp, $bytesInEntry, $off, $fits)
}
if (-not $ok) { throw "generated ICO failed read-back validation" }
if ($count -ne $sizes.Count) { throw "entry count mismatch: $count" }
Write-Host "ICO OK"

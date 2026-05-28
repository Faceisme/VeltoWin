# Extract PNGs from a macOS .icns and pack them into a Windows .ico.
# The .ico entries are written as classic 32-bit DIBs instead of PNG-compressed
# payloads because several Win32 icon-loading paths used by tray icons reject
# PNG-compressed ICO entries and silently fall back to the default app icon.
#
# Usage:
#   .\scripts\icns2ico.ps1 -InputIcns "...\Velto.icns" -OutputIco "...\Velto.ico"

param(
    [Parameter(Mandatory=$true)][string]$InputIcns,
    [Parameter(Mandatory=$true)][string]$OutputIco,
    [string]$OutputPng  # optional: also dump a 256x256 PNG for WPF Window.Icon
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function Read-BE32 {
    param([byte[]]$Buffer, [int]$Offset)
    return ([int]$Buffer[$Offset]   -shl 24) `
       -bor ([int]$Buffer[$Offset+1] -shl 16) `
       -bor ([int]$Buffer[$Offset+2] -shl  8) `
       -bor  [int]$Buffer[$Offset+3]
}

$icns = [System.IO.File]::ReadAllBytes($InputIcns)
$magic = [System.Text.Encoding]::ASCII.GetString($icns, 0, 4)
if ($magic -ne "icns") { throw "Not an ICNS file (magic=$magic)" }

$pngs = @{}
$pos = 8
while ($pos -lt $icns.Length - 8) {
    $type = [System.Text.Encoding]::ASCII.GetString($icns, $pos, 4)
    $len  = Read-BE32 $icns ($pos+4)
    if ($len -le 0) { break }
    $payloadStart = $pos + 8
    $payloadLen = $len - 8

    if ($payloadLen -ge 8 -and $icns[$payloadStart] -eq 0x89 -and
        $icns[$payloadStart+1] -eq 0x50 -and
        $icns[$payloadStart+2] -eq 0x4E -and
        $icns[$payloadStart+3] -eq 0x47) {
        $buf = New-Object byte[] $payloadLen
        [Array]::Copy($icns, $payloadStart, $buf, 0, $payloadLen)
        $pngs[$type] = $buf
    }
    $pos += $len
}

Write-Host "PNG entries found in ICNS: $($pngs.Keys -join ', ')"

$typeSize = @{
    'ic11' = 32;
    'ic12' = 64;
    'ic07' = 128;
    'ic13' = 256;
    'ic08' = 256;
    'ic14' = 512;
    'ic09' = 512;
    'ic10' = 1024;
}

$largest = $null
foreach ($t in 'ic10','ic09','ic14','ic08','ic13','ic07','ic12','ic11') {
    if ($pngs.ContainsKey($t)) { $largest = $pngs[$t]; break }
}
if (-not $largest) { throw "No PNG payload found in ICNS" }

$srcStream = New-Object System.IO.MemoryStream(,$largest)
$srcBitmap = [System.Drawing.Bitmap]::FromStream($srcStream)

function New-ResizedBitmap {
    param([System.Drawing.Bitmap]$Source, [int]$Size)
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($Source, 0, 0, $Size, $Size)
    $g.Dispose()
    return $bmp
}

function Bitmap-ToPngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

function Bitmap-ToIcoDibBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $size = $Bitmap.Width
    if ($Bitmap.Height -ne $size) { throw "ICO bitmap must be square" }

    $ms = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER. For ICO, biHeight is XOR bitmap height + AND mask height.
    $writer.Write([uint32]40)
    $writer.Write([int32]$size)
    $writer.Write([int32]($size * 2))
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]0)
    $writer.Write([uint32]($size * $size * 4))
    $writer.Write([int32]0)
    $writer.Write([int32]0)
    $writer.Write([uint32]0)
    $writer.Write([uint32]0)

    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $Bitmap.GetPixel($x, $y)
            $writer.Write([byte]$c.B)
            $writer.Write([byte]$c.G)
            $writer.Write([byte]$c.R)
            $writer.Write([byte]$c.A)
        }
    }

    # 1-bit AND mask, padded to 32-bit scanlines. Alpha already carries transparency.
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $emptyMask = New-Object byte[] ($maskStride * $size)
    $writer.Write($emptyMask)

    $writer.Flush()
    $bytes = $ms.ToArray()
    $writer.Dispose()
    $ms.Dispose()
    return ,$bytes
}

$targets = @(16, 32, 48, 64, 128, 256)
$entries = @()
foreach ($size in $targets) {
    $matched = $null
    foreach ($t in $typeSize.Keys) {
        if ($typeSize[$t] -eq $size -and $pngs.ContainsKey($t)) {
            $matched = $pngs[$t]; break
        }
    }
    if (-not $matched) {
        Write-Host "  ${size}px : downsampled from largest"
    } else {
        Write-Host "  ${size}px : native from ICNS"
    }

    if ($matched) {
        $matchedStream = New-Object System.IO.MemoryStream(,$matched)
        $matchedBitmap = [System.Drawing.Bitmap]::FromStream($matchedStream)
        if ($matchedBitmap.Width -eq $size -and $matchedBitmap.Height -eq $size) {
            $bmp = New-Object System.Drawing.Bitmap($matchedBitmap)
        } else {
            $bmp = New-ResizedBitmap -Source $matchedBitmap -Size $size
        }
        $matchedBitmap.Dispose()
        $matchedStream.Dispose()
    } else {
        $bmp = New-ResizedBitmap -Source $srcBitmap -Size $size
    }

    $entries += [pscustomobject]@{
        Size = $size
        IcoData = (Bitmap-ToIcoDibBytes -Bitmap $bmp)
        Png = $(if ($size -eq 256) { Bitmap-ToPngBytes -Bitmap $bmp } else { $null })
    }
    $bmp.Dispose()
}
$srcBitmap.Dispose()

$ms = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($ms)

$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$entries.Count)

$dataOffset = 6 + (16 * $entries.Count)
$dataChunks = @()
foreach ($e in $entries) {
    $w = $e.Size; $h = $e.Size
    $wByte = if ($w -ge 256) { [byte]0 } else { [byte]$w }
    $hByte = if ($h -ge 256) { [byte]0 } else { [byte]$h }
    $writer.Write($wByte)
    $writer.Write($hByte)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$e.IcoData.Length)
    $writer.Write([uint32]$dataOffset)
    $dataOffset += $e.IcoData.Length
    $dataChunks += ,$e.IcoData
}
foreach ($chunk in $dataChunks) {
    $writer.Write($chunk)
}
$writer.Flush()

$outDir = Split-Path -Parent $OutputIco
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
[System.IO.File]::WriteAllBytes($OutputIco, $ms.ToArray())
$writer.Dispose(); $ms.Dispose()

$sizesStr = ($entries | ForEach-Object { "$($_.Size)" }) -join ", "
$fileLen = (Get-Item $OutputIco).Length
Write-Host "Wrote: $OutputIco  ($fileLen bytes, sizes = $sizesStr)"

if ($OutputPng) {
    # WPF's BitmapDecoder rejects PNG-embedded ICO entries < 256 — known bug.
    # Save a standalone 256x256 PNG so XAML Icon / BitmapImage can load it.
    $png256 = ($entries | Where-Object { $_.Size -eq 256 } | Select-Object -First 1).Png
    if (-not $png256) { throw "No 256px entry to write as PNG" }
    $outDir = Split-Path -Parent $OutputPng
    if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    [System.IO.File]::WriteAllBytes($OutputPng, $png256)
    Write-Host "Wrote: $OutputPng  ($($png256.Length) bytes, 256x256)"
}

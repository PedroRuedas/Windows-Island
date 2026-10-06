# Draws the black-hole tray/app icon and writes a multi-size .ico (PNG-compressed entries).
#   powershell -ExecutionPolicy Bypass -File .\tools\generate-icon.ps1
param([string]$Output = (Join-Path $PSScriptRoot '..\src\WindowsIsland\Resources\blackhole.ico'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-BlackHole([int]$size) {
    # Draw big, then downscale: small sizes stay smooth.
    $s = 512
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $c = $s / 2

    $orange = [System.Drawing.Color]::FromArgb(255, 255, 159, 10)
    $pink   = [System.Drawing.Color]::FromArgb(255, 255, 55, 95)
    $purple = [System.Drawing.Color]::FromArgb(255, 191, 90, 242)

    # Soft violet halo.
    for ($i = 0; $i -lt 14; $i++) {
        $r = 214 - $i * 6
        $alpha = 10 + $i * 3
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alpha, 150, 70, 230))
        $g.FillEllipse($brush, $c - $r, $c - $r, 2 * $r, 2 * $r); $brush.Dispose()
    }

    $diskBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $s, 0), $orange, $purple)
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @($orange, $pink, $purple); $blend.Positions = @(0.0, 0.5, 1.0)
    $diskBrush.InterpolationColors = $blend

    # Accretion disk, slightly tilted and flat: back half first...
    $state = $g.Save()
    $g.TranslateTransform($c, $c); $g.RotateTransform(-12)
    $diskPen = New-Object System.Drawing.Pen($diskBrush, 24)
    $g.DrawEllipse($diskPen, -248, -46, 496, 92)
    $g.Restore($state)

    # ...the lensed photon ring: hot, bright light wrapped tightly around the horizon...
    $hot = [System.Drawing.Color]::FromArgb(255, 255, 224, 160)
    for ($i = 0; $i -lt 6; $i++) {
        $glowPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40, 255, 140, 60)), (44 - $i * 6)
        $g.DrawEllipse($glowPen, $c - 124, $c - 124, 248, 248); $glowPen.Dispose()
    }
    $ringBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, ($c - 130)), (New-Object System.Drawing.Point 0, ($c + 130)), $hot, $orange)
    $ringPen = New-Object System.Drawing.Pen($ringBrush, 16)
    $g.DrawEllipse($ringPen, $c - 122, $c - 122, 244, 244)

    # ...the event horizon...
    $g.FillEllipse([System.Drawing.Brushes]::Black, $c - 112, $c - 112, 224, 224)

    # ...and the front half of the disk crossing in front of it.
    $state = $g.Save()
    $g.TranslateTransform($c, $c); $g.RotateTransform(-12)
    $g.SetClip((New-Object System.Drawing.RectangleF -260, 0, 520, 70))
    $g.DrawEllipse($diskPen, -248, -46, 496, 92)
    $g.Restore($state)

    $g.Dispose(); $diskPen.Dispose(); $ringPen.Dispose(); $ringBrush.Dispose(); $diskBrush.Dispose()

    $out = New-Object System.Drawing.Bitmap $size, $size
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = 'HighQualityBicubic'; $g2.SmoothingMode = 'AntiAlias'; $g2.PixelOffsetMode = 'HighQuality'
    $g2.DrawImage($bmp, 0, 0, $size, $size)
    $g2.Dispose(); $bmp.Dispose()
    return $out
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
# Classic 32-bit DIB entry (BITMAPINFOHEADER + bottom-up BGRA + AND mask): readable by every Windows API.
function ConvertTo-Dib([System.Drawing.Bitmap]$bmp) {
    $n = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$n); $w.Write([int32]($n * 2))   # height covers XOR + AND
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0); $w.Write([uint32]0)
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $p = $bmp.GetPixel($x, $y)
            $w.Write([byte]$p.B); $w.Write([byte]$p.G); $w.Write([byte]$p.R); $w.Write([byte]$p.A)
        }
    }
    $maskRow = [int]([Math]::Ceiling($n / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $n)))   # all-zero AND mask: alpha channel decides
    $w.Flush()
    return $ms.ToArray()
}

# A typed list: a plain PowerShell array would flatten the byte[]s into one stream of bytes.
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) {
    $bmp = New-BlackHole $size
    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images.Add($ms.ToArray())
    } else {
        $images.Add((ConvertTo-Dib $bmp))
    }
    $bmp.Dispose()
}

New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
$file = [System.IO.File]::Create($Output)
$w = New-Object System.IO.BinaryWriter $file
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)   # ICONDIR
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {                                  # ICONDIRENTRY
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($png in $images) { $w.Write($png) }
$w.Close()
"Wrote $Output ($((Get-Item $Output).Length) bytes, sizes: $($sizes -join ', '))"

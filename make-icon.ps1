# Generates J-Rate.ico (photo + gold star). Run: powershell -ExecutionPolicy Bypass -File make-icon.ps1
param([string]$PreviewPng = "")
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Star([float]$cx, [float]$cy, [float]$outer, [float]$inner) {
    $pts = New-Object 'System.Drawing.PointF[]' 10
    for ($i = 0; $i -lt 10; $i++) {
        $r = if ($i % 2 -eq 0) { $outer } else { $inner }
        $a = (-90 + 36 * $i) * [Math]::PI / 180
        $pts[$i] = New-Object System.Drawing.PointF(($cx + $r * [Math]::Cos($a)), ($cy + $r * [Math]::Sin($a)))
    }
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddPolygon($pts)
    return $p
}

function C([string]$hex) { return [System.Drawing.ColorTranslator]::FromHtml($hex) }

# Everything is drawn in a 256x256 coordinate space and scaled down.
function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $small = $size -le 24

    # background tile
    $bg = New-RoundRect 6 6 244 244 52
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(0, 256)), (C '#3D5068'), (C '#1A222E'))
    $g.FillPath($bgBrush, $bg)

    # photo (white frame + landscape)
    $frame = New-RoundRect 30 44 176 140 14
    $g.FillPath([System.Drawing.Brushes]::White, $frame)
    $inner = New-Object System.Drawing.RectangleF(42, 56, 152, 116)
    $sky = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 56)), (New-Object System.Drawing.PointF(0, 172)), (C '#4AA8F0'), (C '#BFE4FF'))
    $g.FillRectangle($sky, $inner)
    if (-not $small) {
        $g.SetClip($inner)
        $sun = New-Object System.Drawing.SolidBrush (C '#FFF3B0')
        $g.FillEllipse($sun, 60, 68, 34, 34)
        $m1 = New-Object System.Drawing.SolidBrush (C '#2E7D5B')
        $m2 = New-Object System.Drawing.SolidBrush (C '#1F5E46')
        $g.FillPolygon($m2, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF(80, 172)), (New-Object System.Drawing.PointF(140, 92)), (New-Object System.Drawing.PointF(200, 172))))
        $g.FillPolygon($m1, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF(36, 172)), (New-Object System.Drawing.PointF(84, 116)), (New-Object System.Drawing.PointF(132, 172))))
        $g.ResetClip()
    }

    # gold star, bottom-right, with a dark outline so it separates from the photo
    $outer = if ($small) { 84 } else { 74 }
    $cx = if ($small) { 162 } else { 170 }
    $cy = if ($small) { 162 } else { 168 }
    $star = New-Star $cx $cy $outer ($outer * 0.42)
    $outlineW = if ($small) { 30 } else { 18 }
    $outline = New-Object System.Drawing.Pen((C '#1A222E'), $outlineW)
    $outline.LineJoin = 'Round'
    $g.DrawPath($outline, $star)
    $gold = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, ($cy - $outer))), (New-Object System.Drawing.PointF(0, ($cy + $outer))), (C '#FFE066'), (C '#FF9E00'))
    $g.FillPath($gold, $star)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewPng -and $s -eq 256) { $bmp.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($PreviewPng -and $s -le 48) { $bmp.Save(($PreviewPng -replace '\.png$', "_$s.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    $pngs += , $ms.ToArray()
}

# ICO container with PNG-compressed entries
$out = Join-Path $PSScriptRoot 'J-Rate.ico'
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"wrote $out"

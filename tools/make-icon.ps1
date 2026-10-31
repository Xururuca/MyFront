# Generates src\MyFront\app.ico: a bold black capital "M" on a yellow rounded tile,
# as PNG frames 16..256 px (Windows Vista+ icon format). Run: pwsh tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\MyFront\app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$tile = [Drawing.Color]::FromArgb(255, 255, 196, 0)
$ink = [Drawing.Color]::FromArgb(255, 20, 20, 22)

function New-Frame([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)

    # rounded tile
    $r = [Math]::Max(2, [int]($size * 0.2))
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($size - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($size - 2 * $r - 1, $size - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $size - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush $tile), $path)

    # the M, scaled to fill ~70% of the tile
    $font = New-Object Drawing.FontFamily 'Segoe UI Black'
    $m = New-Object Drawing.Drawing2D.GraphicsPath
    $m.AddString('M', $font, [int][Drawing.FontStyle]::Bold, 100, (New-Object Drawing.PointF 0, 0), [Drawing.StringFormat]::GenericTypographic)
    $b = $m.GetBounds()
    $target = $size * ($(if ($size -le 20) { 0.82 } else { 0.70 }))
    $scale = [Math]::Min($target / $b.Width, $target / $b.Height)
    $mx = New-Object Drawing.Drawing2D.Matrix
    $mx.Translate(($size - $b.Width * $scale) / 2 - $b.X * $scale, ($size - $b.Height * $scale) / 2 - $b.Y * $scale)
    $mx.Scale($scale, $scale)
    $m.Transform($mx)
    $g.FillPath((New-Object Drawing.SolidBrush $ink), $m)
    $g.Dispose()

    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$frames = $sizes | ForEach-Object { , (New-Frame $_) }
$fs = [IO.File]::Create($out)
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Dispose()
"wrote $out ($((Get-Item $out).Length) bytes, sizes $($sizes -join ','))"

# preview PNG for checking
$preview = New-Object Drawing.Bitmap 420, 260
$pg = [Drawing.Graphics]::FromImage($preview)
$pg.Clear([Drawing.Color]::White)
$pg.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(32, 32, 32))), 0, 130, 420, 130)
$x = 8
foreach ($i in 0..($sizes.Count - 1)) {
    if ($sizes[$i] -gt 64) { continue }
    $img = [Drawing.Image]::FromStream((New-Object IO.MemoryStream (, $frames[$i])))
    $pg.DrawImage($img, $x, 20); $pg.DrawImage($img, $x, 150)
    $x += $sizes[$i] + 12
}
$big = [Drawing.Image]::FromStream((New-Object IO.MemoryStream (, $frames[$sizes.IndexOf(128)])))
$pg.DrawImage($big, 280, 2, 128, 128)
$preview.Save((Join-Path $env:TEMP 'myfront-icon-preview.png'), [Drawing.Imaging.ImageFormat]::Png)

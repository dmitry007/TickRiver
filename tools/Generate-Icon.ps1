# Build-time vector drawing; no extra graphics libraries or runtime scripts required.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $PSScriptRoot '..\src\TickRiver\Assets'
[IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
$images = @()
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size * 4, $size * 4)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size * 4 / 256.0, $size * 4 / 256.0)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc(10, 10, 88, 88, 180, 90)
    $shape.AddArc(158, 10, 88, 88, 270, 90)
    $shape.AddArc(158, 158, 88, 88, 0, 90)
    $shape.AddArc(10, 158, 88, 88, 90, 90)
    $shape.CloseFigure()
    $fill = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Rectangle]::new(10, 10, 236, 236), [Drawing.ColorTranslator]::FromHtml('#009F9A'), [Drawing.ColorTranslator]::FromHtml('#006A78'), 60.0)
    $graphics.FillPath($fill, $shape)
    $pen = [Drawing.Pen]::new([Drawing.Color]::White, 13)
    $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawEllipse($pen, 64, 45, 128, 128)
    $graphics.DrawLines($pen, [Drawing.PointF[]]@([Drawing.PointF]::new(128, 73), [Drawing.PointF]::new(128, 109), [Drawing.PointF]::new(158, 91)))
    $wave = [Drawing.Drawing2D.GraphicsPath]::new()
    $wave.AddBezier(45, 205, 76, 182, 100, 225, 128, 205)
    $wave.AddBezier(128, 205, 158, 183, 180, 223, 211, 200)
    $graphics.DrawPath($pen, $wave)
    $small = [Drawing.Bitmap]::new($size, $size)
    $smallGraphics = [Drawing.Graphics]::FromImage($small)
    $smallGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $smallGraphics.DrawImage($bitmap, 0, 0, $size, $size)
    $stream = [IO.MemoryStream]::new()
    $small.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $images += ,$stream.ToArray()
    if ($size -eq 256) { $small.Save((Join-Path $assetDirectory 'TickRiver.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $smallGraphics.Dispose(); $small.Dispose()
    $wave.Dispose(); $pen.Dispose(); $fill.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [IO.File]::Create((Join-Path $assetDirectory 'TickRiver.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + $sizes.Count * 16
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length); $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $file.Dispose() }

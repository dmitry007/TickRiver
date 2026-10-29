param([Parameter(Mandatory = $true)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$logo = [Drawing.Image]::FromFile((Join-Path $PSScriptRoot '..\src\TickRiver\Assets\TickRiver.png'))
try {
    foreach ($kind in @('Dialog', 'Banner')) {
        $height = if ($kind -eq 'Dialog') { 312 } else { 58 }
        $bitmap = New-Object Drawing.Bitmap 493, $height
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([Drawing.Color]::White)
        if ($kind -eq 'Dialog') {
            $panel = New-Object Drawing.Rectangle 0, 0, 164, 312
            $fill = [Drawing.Drawing2D.LinearGradientBrush]::new($panel, [Drawing.ColorTranslator]::FromHtml('#006A78'), [Drawing.ColorTranslator]::FromHtml('#003E4B'), 90.0)
            $graphics.FillRectangle($fill, $panel); $fill.Dispose()
            $graphics.DrawImage($logo, 45, 35, 74, 74)
            $title = New-Object Drawing.Font 'Segoe UI', 19, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
            $body = New-Object Drawing.Font 'Segoe UI', 12, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
            $format = New-Object Drawing.StringFormat
            $format.Alignment = [Drawing.StringAlignment]::Center
            $graphics.DrawString('TickRiver', $title, [Drawing.Brushes]::White, [Drawing.RectangleF]::new(4, 124, 156, 30), $format)
            $graphics.DrawString("Your time,`nflowing freely.", $body, [Drawing.Brushes]::White, [Drawing.RectangleF]::new(10, 165, 144, 48), $format)
            $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(65, 89, 221, 211), 2)
            $graphics.SetClip($panel)
            for ($line = 0; $line -lt 5; $line++) {
                $path = New-Object Drawing.Drawing2D.GraphicsPath
                $y = 253 + $line * 13
                $path.AddBezier(-25, $y, 30, $y - 27, 108, $y + 32, 189, $y - 4)
                $graphics.DrawPath($pen, $path); $path.Dispose()
            }
            $graphics.ResetClip()
            $pen.Dispose(); $format.Dispose(); $title.Dispose(); $body.Dispose()
        } else {
            $graphics.DrawImage($logo, 438, 7, 43, 43)
            $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#DAE9E9'))
            $graphics.DrawLine($pen, 0, 57, 493, 57); $pen.Dispose()
        }
        try { $bitmap.Save((Join-Path $OutputDirectory "$kind.bmp"), [Drawing.Imaging.ImageFormat]::Bmp) }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $logo.Dispose() }

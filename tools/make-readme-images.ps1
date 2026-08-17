# Renders the tray icon states (and the app icon) as PNGs for the README.
# Run after changing the icon design:  .\tools\make-readme-images.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot 'docs\images'
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-GlyphPng([System.Drawing.Color]$color, [bool]$slash, [string]$path, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Same geometry as TrayIcon.CreateIcon, scaled from its 32px canvas.
    $k = $size / 32.0
    $fill = New-Object System.Drawing.SolidBrush($color)
    $g.FillEllipse($fill, 2 * $k, 2 * $k, 28 * $k, 28 * $k)

    $white = [System.Drawing.Brushes]::White
    $g.FillRectangle($white, 9 * $k, 13 * $k, 5 * $k, 6 * $k)
    $points = @(
        (New-Object System.Drawing.PointF((14 * $k), (13 * $k)))
        (New-Object System.Drawing.PointF((20 * $k), (8 * $k)))
        (New-Object System.Drawing.PointF((20 * $k), (24 * $k)))
        (New-Object System.Drawing.PointF((14 * $k), (19 * $k)))
    )
    $g.FillPolygon($white, $points)

    if ($slash) {
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, (3.5 * $k))
        $g.DrawLine($pen, 7 * $k, 25 * $k, 25 * $k, 7 * $k)
        $pen.Dispose()
    }

    $g.Dispose()
    $fill.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Wrote $path"
}

$green = [System.Drawing.Color]::FromArgb(0x2E, 0xA8, 0x5C)
$red   = [System.Drawing.Color]::FromArgb(0xD9, 0x3B, 0x30)
$grey  = [System.Drawing.Color]::FromArgb(0x8A, 0x8A, 0x8A)

New-GlyphPng $green $false (Join-Path $outDir 'icon-idle.png') 64
New-GlyphPng $red   $true  (Join-Path $outDir 'icon-muting.png') 64
New-GlyphPng $grey  $false (Join-Path $outDir 'icon-disabled.png') 64
New-GlyphPng $green $false (Join-Path $outDir 'app-icon.png') 128

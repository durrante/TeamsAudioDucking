# Generates src/TeamsAudioDucking/Assets/app.ico from the same speaker glyph
# the tray icon draws at runtime (green circle, white speaker).
# Run once (or after changing the design):  .\tools\make-icon.ps1
# Requires Windows PowerShell / PowerShell 7 on Windows (uses System.Drawing).

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot 'src\TeamsAudioDucking\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null
$icoPath = Join-Path $outDir 'app.ico'

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngBlobs = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Same geometry as TrayIcon.CreateIcon, scaled from its 32px canvas.
    $k = $s / 32.0
    $fill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x2E, 0xA8, 0x5C))
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

    $g.Dispose()
    $fill.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngBlobs += , $ms.ToArray()
    $ms.Dispose()
}

# Compose the .ico manually (PNG-compressed entries are valid for all sizes on Vista+).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0)              # reserved
$w.Write([uint16]1)              # type: icon
$w.Write([uint16]$sizes.Count)   # image count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $blob = $pngBlobs[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # width  (0 = 256)
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # height (0 = 256)
    $w.Write([byte]0)            # palette
    $w.Write([byte]0)            # reserved
    $w.Write([uint16]1)          # colour planes
    $w.Write([uint16]32)         # bits per pixel
    $w.Write([uint32]$blob.Length)
    $w.Write([uint32]$offset)
    $offset += $blob.Length
}
foreach ($blob in $pngBlobs) { $w.Write($blob) }

[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$w.Dispose()
$out.Dispose()

Write-Host "Wrote $icoPath ($((Get-Item $icoPath).Length) bytes, sizes: $($sizes -join ', '))"

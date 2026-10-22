# 生成多尺寸应用图标 src\GameWall\Assets\GameWall.ico
# 无需外部素材：用 GDI+ 现画一个「G」字母圆角方块，再手工拼成 ICO。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot 'src\GameWall\Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # 圆角矩形路径
    $radius = [int]($s * 0.22)
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    # 深色渐变底
    $start = New-Object System.Drawing.Point 0, 0
    $end = New-Object System.Drawing.Point $s, $s
    $dark = [System.Drawing.Color]::FromArgb(255, 32, 44, 66)
    $deeper = [System.Drawing.Color]::FromArgb(255, 14, 17, 22)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $start, $end, $dark, $deeper
    $g.FillPath($brush, $path)

    # 强调色描边
    $penWidth = [math]::Max(1, $s / 32)
    $accent = [System.Drawing.Color]::FromArgb(255, 79, 140, 255)
    $pen = New-Object System.Drawing.Pen $accent, $penWidth
    $g.DrawPath($pen, $path)

    # 字母 G
    $fontPx = [math]::Max(7, $s * 0.58)
    $font = New-Object System.Drawing.Font 'Segoe UI', $fontPx, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, ($s * 0.94)
    $g.DrawString('G', $font, [System.Drawing.Brushes]::White, $rect, $sf)

    $g.Dispose()
    $pen.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $font.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries += [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose()
    $bmp.Dispose()
}

# 手工拼 ICO：ICONDIR(6) + ICONDIRENTRY(16) * N + PNG 数据
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0)                    # reserved
$bw.Write([uint16]1)                    # type = icon
$bw.Write([uint16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    # 256 尺寸在目录里记作 0
    $dim = 0
    if ($e.Size -lt 256) { $dim = $e.Size }
    $bw.Write([byte]$dim)               # width
    $bw.Write([byte]$dim)               # height
    $bw.Write([byte]0)                  # color count
    $bw.Write([byte]0)                  # reserved
    $bw.Write([uint16]1)                # planes
    $bw.Write([uint16]32)               # bit count
    $bw.Write([uint32]$e.Bytes.Length)  # size in bytes
    $bw.Write([uint32]$offset)          # offset
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw.Write($e.Bytes) }
$bw.Flush()

$icoPath = Join-Path $outDir 'GameWall.ico'
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()

Write-Host "生成: $icoPath ($((Get-Item $icoPath).Length) 字节, $($entries.Count) 个尺寸)"

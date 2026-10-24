<#
  Gera o ícone do Cronos (ICO multi-tamanho + PNGs) desenhando com System.Drawing.
  Desenho original: quadrado arredondado em degradê índigo→ciano, avião de papel branco
  (enviar) e um relógio num selo (agendar). Não usa nenhum elemento da marca Instagram.

  Uso: powershell -File scripts\make-icon.ps1
#>
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$assets = Join-Path (Split-Path $PSScriptRoot) 'InstaPanel.App\Assets'

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  $p
}

function New-IconBitmap([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.TextRenderingHint = 'AntiAliasGridFit'
  $g.InterpolationMode = 'HighQualityBicubic'
  $g.Clear([System.Drawing.Color]::Transparent)

  # Fundo: quadrado arredondado com degradê diagonal
  $m = [Math]::Max(1, $size * 0.03)
  $body = New-RoundedRect $m $m ($size - 2 * $m) ($size - 2 * $m) ($size * 0.22)
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 79, 70, 229)), ([System.Drawing.Color]::FromArgb(255, 6, 182, 212))
  $g.FillPath($grad, $body)

  # Avião de papel (Segoe Fluent Icons "SendFill" E725)
  $white = [System.Drawing.Brushes]::White
  $fmt = New-Object System.Drawing.StringFormat
  $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
  $font = New-Object System.Drawing.Font 'Segoe Fluent Icons', ($size * 0.42), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
  $g.DrawString([string][char]0xE725, $font, $white, (New-Object System.Drawing.RectangleF ($size * -0.04), ($size * -0.04), $size, $size), $fmt)

  # Selo com relógio (agendar) no canto inferior direito — some nos tamanhos pequenos
  if ($size -ge 32) {
    $d = $size * 0.40
    $x = $size - $d - $size * 0.06; $y = $size - $d - $size * 0.06
    $g.FillEllipse($white, $x, $y, $d, $d)
    $clockFont = New-Object System.Drawing.Font 'Segoe Fluent Icons', ($d * 0.62), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $clockBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 79, 70, 229))
    $g.DrawString([string][char]0xE823, $clockFont, $clockBrush, (New-Object System.Drawing.RectangleF $x, ($y + $d * 0.02), $d, $d), $fmt)
  }
  $g.Dispose()
  $bmp
}

function Save-Png($bmp, $path) { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) }

# ICO com entradas PNG (formato aceito desde o Windows Vista)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
  $b = New-IconBitmap $s
  $ms = New-Object System.IO.MemoryStream
  $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
  , $ms.ToArray()
}
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $pngs[$i].Length
  $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
  $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$len); $w.Write([uint32]$offset)
  $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
[IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $ico.ToArray())

$b = New-IconBitmap 256; Save-Png $b (Join-Path $assets 'AppIcon-256.png'); $b.Dispose()
$b = New-IconBitmap 96; Save-Png $b (Join-Path $assets 'NotificationIcon.png'); $b.Dispose()
"ícones gerados em $assets"

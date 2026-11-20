# Draws srcScriberapp.ico (16..256 px): a blue tile with three indented outline rows.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dst  = Join-Path $root "src\Scriber\app.ico"

# Drawn on a 256x256 grid; the graphics object is scaled for each size.
function Draw($g) {
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 96
    $tile.AddArc(8, 8, $r, $r, 180, 90); $tile.AddArc(248 - $r, 8, $r, $r, 270, 90)
    $tile.AddArc(248 - $r, 248 - $r, $r, $r, 0, 90); $tile.AddArc(8, 248 - $r, $r, $r, 90, 90)
    $tile.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml("#2563eb"))), $tile)
    $white = [System.Drawing.Brushes]::White
    # each row: a bullet and a line, indented one step further
    foreach ($row in @(@(52, 70, 146), @(84, 116, 114), @(116, 162, 82))) {
        $x = $row[0]; $y = $row[1]; $w = $row[2]
        $g.FillEllipse($white, $x, $y, 24, 24)
        $g.FillRectangle($white, $x + 36, $y + 2, $w - 36, 20)
    }
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.ScaleTransform($s / 256, $s / 256)
    Draw $g
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    ,@($s, $ms.ToArray())
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $bytes = $f[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$bytes.Length); $w.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes($dst, $out.ToArray())
Write-Host "wrote $dst ($([math]::Round($out.Length/1KB,1)) KB, $($frames.Count) frames)"

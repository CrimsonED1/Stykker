# Erzeugt assets/app.ico: Raute (Diamond) in Cyan auf dunklem Grund, 16/32/48/256 px.
# Benötigt nur PowerShell + System.Drawing (Windows). Das Ergebnis wird committed.

param(
    [string]$OutPath = (Join-Path $PSScriptRoot "..\assets\app.ico")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 32, 48, 256)
$back  = [System.Drawing.Color]::FromArgb(255, 16, 20, 24)      # dunkler Grund
$diamond = [System.Drawing.Color]::FromArgb(255, 0, 229, 212)   # Cyan

function New-Bitmap([int]$px) {
    $bmp = New-Object System.Drawing.Bitmap($px, $px)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        # Hintergrund mit abgerundeten Ecken
        $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
        $r = [Math]::Max(2, [int]($px * 0.15))
        $bg.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
        $bg.AddArc($px - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
        $bg.AddArc($px - 2 * $r, $px - 2 * $r, 2 * $r, 2 * $r, 0, 90)
        $bg.AddArc(0, $px - 2 * $r, 2 * $r, 2 * $r, 90, 90)
        $bg.CloseFigure()
        $bgBrush = New-Object System.Drawing.SolidBrush($back)
        $g.FillPath($bgBrush, $bg)

        # Raute: ein um 45° gedrehtes Quadrat, zentriert
        $half = $px * 0.32
        $cx = $px / 2.0; $cy = $px / 2.0
        $p1 = New-Object System.Drawing.PointF($cx, ($cy - $half))
        $p2 = New-Object System.Drawing.PointF(($cx + $half), $cy)
        $p3 = New-Object System.Drawing.PointF($cx, ($cy + $half))
        $p4 = New-Object System.Drawing.PointF(($cx - $half), $cy)
        $pts = @($p1, $p2, $p3, $p4)
        $fill = New-Object System.Drawing.SolidBrush($diamond)
        $g.FillPolygon($fill, $pts)

        # dunkle Innenraute als Ausschnitt (Outline-Look)
        $inner = $px * 0.14
        $q1 = New-Object System.Drawing.PointF($cx, ($cy - $inner))
        $q2 = New-Object System.Drawing.PointF(($cx + $inner), $cy)
        $q3 = New-Object System.Drawing.PointF($cx, ($cy + $inner))
        $q4 = New-Object System.Drawing.PointF(($cx - $inner), $cy)
        $pts2 = @($q1, $q2, $q3, $q4)
        $hole = New-Object System.Drawing.SolidBrush($back)
        $g.FillPolygon($hole, $pts2)

        $bgBrush.Dispose(); $fill.Dispose(); $hole.Dispose(); $bg.Dispose()
    }
    finally { $g.Dispose() }
    return $bmp
}

function ConvertTo-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

$pngs = @{}
foreach ($s in $sizes) {
    $b = New-Bitmap $s
    $pngs[$s] = [byte[]](ConvertTo-PngBytes $b)
    $b.Dispose()
}

# ICO-Container zusammenbauen: Header + Verzeichnis + PNG-Blobs (PNG-Einträge sind seit Vista erlaubt)
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $bytes = $pngs[$s]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))   # Breite (0 = 256)
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))   # Höhe
    $bw.Write([byte]0)                                        # Farbpalette
    $bw.Write([byte]0)                                        # reserviert
    $bw.Write([uint16]1)                                      # Planes
    $bw.Write([uint16]32)                                     # Bits per Pixel
    $bw.Write([uint32]$bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach ($s in $sizes) { $bw.Write([byte[]]$pngs[$s]) }
$bw.Flush()

$dir = Split-Path -Parent $OutPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutPath, $ms.ToArray())
$bw.Close()
Write-Host "app.ico geschrieben: $OutPath ($((Get-Item $OutPath).Length) Bytes, $($sizes -join '/'))"
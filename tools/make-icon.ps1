# Draws the Window Groups mark - four tiles, the top-left one lit - at every size Windows asks
# for, and writes them into assets/app.ico as PNG-compressed icon entries.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out  = Join-Path (Join-Path $root 'assets') 'app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Draw([int]$px) {
    $bmp = New-Object System.Drawing.Bitmap $px, $px
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
    # On a plate of ink at the larger sizes, so it reads on a light Explorer background too.
    if ($px -ge 48) {
        $plate = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(10, 10, 10))
        $g.FillRectangle($plate, 0, 0, $px, $px)
        $inset = [math]::Round($px * 0.2)
    } else { $inset = 0 }
    $area = $px - 2 * $inset
    $gap  = [math]::Max(1, [math]::Round($area / 12))
    $cell = [math]::Floor(($area - $gap * 3) / 2)
    $lit   = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(37, 247, 22))
    $paper = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(244, 243, 240))
    $a = $inset + $gap; $b = $inset + $gap * 2 + $cell
    $g.FillRectangle($lit,   $a, $a, $cell, $cell)
    $g.FillRectangle($paper, $b, $a, $cell, $cell)
    $g.FillRectangle($paper, $a, $b, $cell, $cell)
    $g.FillRectangle($paper, $b, $b, $cell, $cell)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs  = @($sizes | ForEach-Object { ,(Draw $_) })

$fs = [System.IO.File]::Create($out)
$w  = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = [byte[]]$pngs[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$d.Length); $w.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $w.Write([byte[]]$d) }
$w.Close()
"wrote $out ($((Get-Item $out).Length) bytes, $($sizes.Count) sizes)"

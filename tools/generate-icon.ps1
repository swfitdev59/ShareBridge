# Regenerates the ShareBridge app icon: a suspension-bridge mark on the brand-blue
# gradient, packed as a multi-resolution .ico (16-256 px, PNG-compressed frames).
#
#   powershell -ExecutionPolicy Bypass -File tools/generate-icon.ps1 `
#       -Out src/ShareBridge.Wpf/Assets/appicon.ico
#
# Run with Windows PowerShell (System.Drawing / GDI+).

param(
    [string]$Out = "$PSScriptRoot\..\src\ShareBridge.Wpf\Assets\appicon.ico",
    [string]$PreviewPng)

Add-Type -AssemblyName System.Drawing

$Smoothing = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$Interp    = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$PixOffset = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$RoundCap  = [System.Drawing.Drawing2D.LineCap]::Round
$RoundJoin = [System.Drawing.Drawing2D.LineJoin]::Round

function New-BridgeBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = $Smoothing
    $g.InterpolationMode = $Interp
    $g.PixelOffsetMode = $PixOffset
    $g.Clear([System.Drawing.Color]::Transparent)

    $detailed = $s -ge 48

    # --- rounded-square background with a vertical accent gradient ----------
    $inset = [math]::Max(1.0, $s * 0.045)
    $rect = New-Object System.Drawing.RectangleF($inset, $inset, ($s - 2*$inset), ($s - 2*$inset))
    $radius = $s * 0.22
    $d = $radius * 2

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.Left, $rect.Top, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Top, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.Left, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $top = [System.Drawing.Color]::FromArgb(255, 96, 155, 255)   # #609BFF
    $bot = [System.Drawing.Color]::FromArgb(255, 44, 106, 214)   # #2C6AD6
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bot, 90.0)
    $g.FillPath($bgBrush, $path)

    # soft top highlight for a bit of depth
    if ($detailed) {
        $glowRect = New-Object System.Drawing.RectangleF($rect.Left, $rect.Top, $rect.Width, ($rect.Height * 0.55))
        $glow = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $glowRect,
            [System.Drawing.Color]::FromArgb(42, 255, 255, 255),
            [System.Drawing.Color]::FromArgb(0, 255, 255, 255), 90.0)
        $g.SetClip($path)
        $g.FillRectangle($glow, $glowRect)
        $g.ResetClip()
        $glow.Dispose()
    }

    # --- geometry of the bridge --------------------------------------------
    $white = [System.Drawing.Color]::White

    $deckY    = $s * 0.670
    $left     = $s * 0.150
    $right    = $s * 0.850
    $towerX1  = $s * 0.335
    $towerX2  = $s * 0.665
    $towerTop = $s * 0.250
    $deckH    = [math]::Max(2.0, $s * 0.058)
    $towerW   = [math]::Max(2.0, $s * 0.052)

    # main suspension cables
    $cableW = [math]::Max(1.6, $s * 0.042)
    $penCable = New-Object System.Drawing.Pen($white, $cableW)
    $penCable.StartCap = $RoundCap; $penCable.EndCap = $RoundCap; $penCable.LineJoin = $RoundJoin

    $midDip = $deckY - ($s * 0.150)
    $cable = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cable.AddBezier(
        $left, $deckY,
        $left + ($towerX1-$left)*0.45, ($deckY - $s*0.02),
        $towerX1 - ($towerX1-$left)*0.30, ($towerTop + $s*0.02),
        $towerX1, $towerTop)
    $cable.StartFigure()
    $cable.AddBezier(
        $towerX1, $towerTop,
        $towerX1 + ($towerX2-$towerX1)*0.28, $midDip,
        $towerX2 - ($towerX2-$towerX1)*0.28, $midDip,
        $towerX2, $towerTop)
    $cable.StartFigure()
    $cable.AddBezier(
        $towerX2, $towerTop,
        $towerX2 + ($right-$towerX2)*0.30, ($towerTop + $s*0.02),
        $right - ($right-$towerX2)*0.45, ($deckY - $s*0.02),
        $right, $deckY)
    $g.DrawPath($penCable, $cable)

    # suspenders (vertical hangers) across the centre span — detail sizes only
    if ($detailed) {
        $penHang = New-Object System.Drawing.Pen($white, [math]::Max(1.0, $s*0.017))
        $penHang.StartCap = $RoundCap; $penHang.EndCap = $RoundCap
        $count = 4
        for ($i = 1; $i -le $count; $i++) {
            $t = $i / ($count + 1.0)
            $hx = $towerX1 + ($towerX2 - $towerX1) * $t
            $u = ($t - 0.5) * 2.0
            $hy = $midDip + ($towerTop - $midDip) * ($u * $u)
            $g.DrawLine($penHang, $hx, $hy, $hx, $deckY)
        }
        $penHang.Dispose()
    }

    # deck (roadway)
    $penDeck = New-Object System.Drawing.Pen($white, $deckH)
    $penDeck.StartCap = $RoundCap; $penDeck.EndCap = $RoundCap
    $g.DrawLine($penDeck, $left, $deckY, $right, $deckY)

    # two towers standing on the deck
    $penTower = New-Object System.Drawing.Pen($white, $towerW)
    $penTower.StartCap = $RoundCap; $penTower.EndCap = $RoundCap
    $g.DrawLine($penTower, $towerX1, $towerTop, $towerX1, ($deckY + $deckH*0.10))
    $g.DrawLine($penTower, $towerX2, $towerTop, $towerX2, ($deckY + $deckH*0.10))

    # water ripples beneath the deck
    if ($detailed) {
        $penWave = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 255, 255, 255), [math]::Max(1.0, $s*0.020))
        $penWave.StartCap = $RoundCap; $penWave.EndCap = $RoundCap
        foreach ($wy in @(($s*0.805), ($s*0.878))) {
            $wave = New-Object System.Drawing.Drawing2D.GraphicsPath
            $wave.AddBezier($left, $wy, ($s*0.34), ($wy - $s*0.03), ($s*0.42), ($wy + $s*0.03), ($s*0.55), $wy)
            $wave.StartFigure()
            $wave.AddBezier(($s*0.55), $wy, ($s*0.66), ($wy - $s*0.03), ($s*0.74), ($wy + $s*0.03), $right, $wy)
            $g.DrawPath($penWave, $wave)
            $wave.Dispose()
        }
        $penWave.Dispose()
    }

    $penCable.Dispose(); $penDeck.Dispose(); $penTower.Dispose(); $bgBrush.Dispose()
    $cable.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

# --- render each size and pack into a single .ico (PNG-compressed entries) ---
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = New-BridgeBitmap $sz
    if ($PreviewPng -and $sz -eq 256) {
        $bmp.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,($ms.ToArray())
    $bmp.Dispose(); $ms.Dispose()
}

$fs = New-Object System.IO.FileStream($Out, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Flush(); $bw.Close(); $fs.Close()
Write-Output "wrote $Out ($((Get-Item $Out).Length) bytes, $($sizes.Count) sizes)"

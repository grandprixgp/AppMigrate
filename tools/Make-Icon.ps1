#requires -Version 5.1
<#
.SYNOPSIS
    Generates the AppMigrate application icon and preview PNG.

.DESCRIPTION
    Draws a vector-style mark with System.Drawing, then writes a multi-size ICO
    with PNG-compressed frames. Run from the repository root:
        powershell -ExecutionPolicy Bypass -File .\tools\Make-Icon.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assetDir = Join-Path $root 'src\AppMigrate\Assets'
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null
$pngPath = Join-Path $assetDir 'AppMigrate.png'
$icoPath = Join-Path $assetDir 'AppMigrate.ico'

function New-RoundedPath {
    param([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $Width - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $Width - $d, $Y + $Height - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $Height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    $size = 1024
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Main rounded tile with a deep navy-to-teal gradient.
    $tilePath = New-RoundedPath 32 32 960 960 190
    $tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        (New-Object System.Drawing.Point 60, 40),
        (New-Object System.Drawing.Point 970, 980),
        ([System.Drawing.Color]::FromArgb(255, 10, 26, 51)),
        ([System.Drawing.Color]::FromArgb(255, 14, 124, 134))
    )
    $g.FillPath($tileBrush, $tilePath)

    # Two app tiles.
    $leftPath = New-RoundedPath 132 430 350 350 66
    $leftBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 243, 251, 255))
    $g.FillPath($leftBrush, $leftPath)

    $rightPath = New-RoundedPath 542 430 350 350 66
    $rightBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 101, 230, 242))
    $g.FillPath($rightBrush, $rightPath)

    # Transfer arrow above the tiles.
    $arrowPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 248, 253, 255)), 64
    $arrowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($arrowPen, 190, 254, 764, 254)
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $head.AddPolygon(@(
        (New-Object System.Drawing.Point 742, 142),
        (New-Object System.Drawing.Point 902, 254),
        (New-Object System.Drawing.Point 742, 366)
    ))
    $arrowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 248, 253, 255))
    $g.FillPath($arrowBrush, $head)

    $g.Dispose()
    $tilePath.Dispose(); $tileBrush.Dispose(); $leftPath.Dispose(); $leftBrush.Dispose()
    $rightPath.Dispose(); $rightBrush.Dispose()
    $arrowPen.Dispose(); $head.Dispose(); $arrowBrush.Dispose()
    return $bmp
}

function Resize-Bitmap {
    param([System.Drawing.Bitmap]$Source, [int]$Size)
    $target = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($target)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($Source, 0, 0, $Size, $Size)
    $g.Dispose()
    return $target
}

function Write-Ico {
    param([System.Drawing.Bitmap]$Source, [string]$Path, [int[]]$Sizes)
    $frames = @()
    foreach ($size in $Sizes) {
        $resized = Resize-Bitmap -Source $Source -Size $size
        $ms = New-Object System.IO.MemoryStream
        $resized.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
        $ms.Dispose(); $resized.Dispose()
    }

    $stream = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$frames.Count)
        $offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$frame.Bytes.Length)
            $writer.Write([UInt32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write($frame.Bytes) }
    }
    finally {
        $writer.Dispose(); $stream.Dispose()
    }
}

$master = New-IconBitmap
$master.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Ico -Source $master -Path $icoPath -Sizes @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$master.Dispose()

Write-Host "Wrote $pngPath"
Write-Host "Wrote $icoPath"

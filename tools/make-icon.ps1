# Generates the app artwork:
#   assets/castbridge.ico  - a valid multi-size icon for the executable and the Desktop shortcut
#   assets/castbridge.png  - a 64px PNG, because WPF cannot decode .ico files with BitmapImage
#
# The mark is the familiar cast symbol: a screen outline whose lower-left corner carries the dot and
# the concentric arcs that the signal radiates from. It is drawn from primitives rather than copied
# from an asset, and it is our own drawing of the symbol rather than Google's logo file - if this app
# is ever distributed, the Google Cast brand guidelines are the thing to read first.
#
# Sizes 16/20/24 draw two arcs instead of three (see New-IconBitmap), because at those sizes the full
# set merges into a smear. They are in the file so Explorer and the shortcut render crisply at 100%,
# 125% and 150% display scaling instead of asking Windows to resample the 32px frame.
#
# An ICO image is not a bare bitmap: every entry must start with a 40-byte BITMAPINFOHEADER whose
# height is twice the icon height (colour rows plus the AND mask). Writing pixel data on its own
# produces a file Windows mostly tolerates and WPF refuses.
param(
    [string]$IcoOutput = (Join-Path $PSScriptRoot '..\assets\castbridge.ico'),
    [string]$PngOutput = (Join-Path $PSScriptRoot '..\assets\castbridge.png'),
    [string]$PreviewOutput = (Join-Path $PSScriptRoot '..\docs\icon-preview.png')
)

Add-Type -AssemblyName System.Drawing

$accent = [System.Drawing.Color]::FromArgb(255, 76, 141, 255)
$glyph = [System.Drawing.Color]::White

function New-RoundedRectPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $w - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $w - $diameter, $y + $h - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $h - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

# The cast screen, left open at its lower-left corner so the arcs can radiate from there.
function New-CastScreenPath {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath

    # The corner sits at (13,49); the outline stops short of it on both edges so the arcs land in
    # the gap rather than on the frame.
    $path.AddLine(13, 28, 13, 20)
    $path.AddArc(13, 15, 10, 10, 180, 90)     # top-left corner
    $path.AddLine(18, 15, 46, 15)
    $path.AddArc(41, 15, 10, 10, 270, 90)     # top-right corner
    $path.AddLine(51, 20, 51, 44)
    $path.AddArc(41, 39, 10, 10, 0, 90)       # bottom-right corner
    $path.AddLine(46, 49, 36, 49)

    return $path
}

# Draws the mark on a transparent square. The design is laid out on a 64px grid and scaled, so every
# size gets the same artwork.
function New-IconBitmap([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform($size / 64.0, $size / 64.0)

    $tile = New-RoundedRectPath 0 0 64 64 14
    $tileBrush = New-Object System.Drawing.SolidBrush $accent
    $graphics.FillPath($tileBrush, $tile)

    $glyphBrush = New-Object System.Drawing.SolidBrush $glyph
    $framePen = New-Object System.Drawing.Pen $glyph, ([single]4.5)
    $framePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $screen = New-CastScreenPath
    $graphics.DrawPath($framePen, $screen)

    # Dot and arcs share one centre: the corner the screen leaves open.
    $cornerX = 13.0
    $cornerY = 49.0
    $graphics.FillEllipse($glyphBrush, ($cornerX - 3.2), ($cornerY - 3.2), 6.4, 6.4)

    # Three arcs a third of a pixel apart at 16px smear into one blob, so the small sizes drop the
    # outermost arc and keep the spacing that makes them read as ripples.
    $radii = 8.0, 14.0, 20.0
    if ($size -lt 32) { $radii = 8.0, 14.0 }

    $arcPen = New-Object System.Drawing.Pen $glyph, ([single]4.5)
    $arcPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arcPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($radius in $radii) {
        $graphics.DrawArc($arcPen, ($cornerX - $radius), ($cornerY - $radius), ($radius * 2), ($radius * 2), 270, 90)
    }

    $arcPen.Dispose()
    $framePen.Dispose()
    $glyphBrush.Dispose()
    $tileBrush.Dispose()
    $graphics.Dispose()
    return $bitmap
}

# One ICO directory entry: BITMAPINFOHEADER, bottom-up colour rows, then the AND mask.
function New-IconImageBytes([int]$size) {
    $bitmap = New-IconBitmap $size

    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $locked = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $locked.Stride
    $pixels = New-Object byte[] ($stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    $bitmap.UnlockBits($locked)
    $bitmap.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream

    $writer.Write([UInt32] 40)                  # biSize
    $writer.Write([Int32] $size)                # biWidth
    $writer.Write([Int32] ($size * 2))          # biHeight: colour rows plus mask
    $writer.Write([UInt16] 1)                   # biPlanes
    $writer.Write([UInt16] 32)                  # biBitCount
    $writer.Write([UInt32] 0)                   # biCompression: BI_RGB
    $writer.Write([UInt32] ($stride * $size))   # biSizeImage
    $writer.Write([Int32] 0)                    # biXPelsPerMeter
    $writer.Write([Int32] 0)                    # biYPelsPerMeter
    $writer.Write([UInt32] 0)                   # biClrUsed
    $writer.Write([UInt32] 0)                   # biClrImportant

    for ($y = $size - 1; $y -ge 0; $y--) {
        $writer.Write($pixels, $y * $stride, $stride)
    }

    # The AND mask is still part of the format even when it is fully transparent. Rows are 1bpp,
    # padded to a 4-byte boundary.
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $writer.Write((New-Object byte[] ($maskStride * $size)))
    $writer.Flush()

    $bytes = $stream.ToArray()
    $writer.Dispose()
    $stream.Dispose()
    return $bytes
}

# A typed list, not @(): PowerShell flattens an array of byte arrays into one long byte array, and
# then every directory entry ends up with a zero length image.
$sizes = @(16, 20, 24, 32, 48, 64)
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) { $images.Add((New-IconImageBytes $size)) }

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $stream

$writer.Write([UInt16] 0)                       # reserved
$writer.Write([UInt16] 1)                       # type: icon
$writer.Write([UInt16] $sizes.Count)            # image count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $writer.Write([Byte] $size)                 # width
    $writer.Write([Byte] $size)                 # height
    $writer.Write([Byte] 0)                     # palette colours
    $writer.Write([Byte] 0)                     # reserved
    $writer.Write([UInt16] 1)                   # colour planes
    $writer.Write([UInt16] 32)                  # bits per pixel
    $writer.Write([UInt32] $images[$i].Length)
    $writer.Write([UInt32] $offset)
    $offset += $images[$i].Length
}

foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()

foreach ($path in @($IcoOutput, $PngOutput, $PreviewOutput)) {
    $directory = Split-Path -Parent $path
    if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
}

[System.IO.File]::WriteAllBytes($IcoOutput, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

$png = New-IconBitmap 64
$png.Save($PngOutput, [System.Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()

# A side-by-side preview at tray sizes and large, so the drawing can be checked without installing it.
$preview = New-Object System.Drawing.Bitmap 560, 256, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$canvas = [System.Drawing.Graphics]::FromImage($preview)
$canvas.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$canvas.Clear([System.Drawing.Color]::FromArgb(255, 32, 34, 40))

$large = New-IconBitmap 192
$canvas.DrawImage($large, 24, 32, 192, 192)

$tray16 = New-IconBitmap 16
$canvas.DrawImage($tray16, 240, 32, 64, 64)   # 16px shown at 4x, as the taskbar renders it
$tray24 = New-IconBitmap 24
$canvas.DrawImage($tray24, 328, 32, 72, 72)
$tray32 = New-IconBitmap 32
$canvas.DrawImage($tray32, 424, 32, 96, 96)

$large.Dispose()
$tray16.Dispose()
$tray24.Dispose()
$tray32.Dispose()
$canvas.Dispose()
$preview.Save($PreviewOutput, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

Write-Output "wrote $IcoOutput ($((Get-Item $IcoOutput).Length) bytes, $($sizes -join '/'))"
Write-Output "wrote $PngOutput ($((Get-Item $PngOutput).Length) bytes)"
Write-Output "wrote $PreviewOutput"

# Captures the whole primary screen. The app's windows are topmost, so a full grab is enough to see
# them, and it avoids the P/Invoke type loading that makes per-window capture brittle in PowerShell.
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\docs\app-screen.png')
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bitmap.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()

Write-Output "saved $Output ($($bounds.Width)x$($bounds.Height))"

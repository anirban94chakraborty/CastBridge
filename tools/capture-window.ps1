# Captures a window belonging to a process and saves it to a PNG.
#
# Process.MainWindowHandle is not usable here: the popup sets ShowInTaskbar=false and Topmost=true,
# which makes that heuristic return 0 even though the window is plainly on screen. So this enumerates
# every top-level window the process owns instead.
param(
    [string]$ProcessName = 'CastBridge',
    [string]$Output = (Join-Path $PSScriptRoot '..\docs\app-window.png')
)

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class WindowCapture
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);

    public struct Rect { public int Left, Top, Right, Bottom; }

    delegate bool EnumProc(IntPtr handle, IntPtr param);

    // Written in C# 5 syntax on purpose: Windows PowerShell 5.1 compiles Add-Type with an old
    // compiler, so "out var" and expression-bodied members do not build.
    public static List<IntPtr> OwnedBy(uint target)
    {
        List<IntPtr> found = new List<IntPtr>();
        EnumWindows(delegate(IntPtr handle, IntPtr param)
        {
            uint owner;
            GetWindowThreadProcessId(handle, out owner);
            if (owner == target && IsWindowVisible(handle))
                found.Add(handle);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static Rect Bounds(IntPtr handle)
    {
        Rect rect;
        GetWindowRect(handle, out rect);
        return rect;
    }

    public static string Title(IntPtr handle)
    {
        var builder = new StringBuilder(GetWindowTextLength(handle) + 1);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }
}
'@

$process = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
$handles = [WindowCapture]::OwnedBy([uint32]$process.Id)

Write-Output "$($process.Name) pid $($process.Id) owns $($handles.Count) visible window(s)"
if ($handles.Count -eq 0) { exit 1 }

$chosen = $null
foreach ($handle in $handles) {
    $bounds = [WindowCapture]::Bounds($handle)
    $width = $bounds.Right - $bounds.Left
    $height = $bounds.Bottom - $bounds.Top
    $title = [WindowCapture]::Title($handle)
    Write-Output ("  '$title' ${width}x${height} at $($bounds.Left),$($bounds.Top)")
    if ($null -eq $chosen -and $width -gt 100) { $chosen = $handle }
}

if ($null -eq $chosen) { $chosen = $handles[0] }

[WindowCapture]::SetForegroundWindow($chosen) | Out-Null
Start-Sleep -Milliseconds 900

$bounds = [WindowCapture]::Bounds($chosen)
$width = $bounds.Right - $bounds.Left
$height = $bounds.Bottom - $bounds.Top
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)

$directory = Split-Path -Parent $Output
if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
$bitmap.Save($Output)
$graphics.Dispose()
$bitmap.Dispose()

Write-Output "saved $Output (${width}x${height})"
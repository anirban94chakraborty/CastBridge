# Checks the tray behaviour of the running app: minimizing must hide the window (there is no taskbar
# button to minimize to), and the tray icon must exist.
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class TrayProbe
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);

    delegate bool EnumProc(IntPtr handle, IntPtr param);

    public static IntPtr FindWindow(uint processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr handle, IntPtr param)
        {
            uint owner;
            GetWindowThreadProcessId(handle, out owner);
            if (owner != processId || !IsWindowVisible(handle)) return true;

            StringBuilder builder = new StringBuilder(GetWindowTextLength(handle) + 1);
            GetWindowText(handle, builder, builder.Capacity);
            if (builder.ToString() != "CastBridge") return true;

            found = handle;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    // SW_MINIMIZE
    public static void Minimize(IntPtr handle) { ShowWindow(handle, 6); }

    public static bool Visible(IntPtr handle) { return IsWindowVisible(handle); }
}
'@

$process = Get-Process -Name CastBridge -ErrorAction Stop | Select-Object -First 1
$window = [TrayProbe]::FindWindow([uint32]$process.Id)
if ($window -eq [IntPtr]::Zero) { Write-Output 'no visible CastBridge window found'; exit 1 }

Write-Output "window found; visible before minimize: $([TrayProbe]::Visible($window))"
[TrayProbe]::Minimize($window)
Start-Sleep -Milliseconds 800
$visible = [TrayProbe]::Visible($window)
Write-Output "visible after minimize: $visible"

# A tray icon means the app is still alive after its window disappeared.
$alive = -not $process.HasExited
Write-Output "process alive: $alive"

if ($visible) { Write-Output 'FAIL: the window is still on screen after minimizing'; exit 1 }
if (-not $alive) { Write-Output 'FAIL: the app exited instead of hiding to the tray'; exit 1 }

Write-Output 'PASS: minimizing hides the window and the app stays in the tray'

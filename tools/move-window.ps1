# Moves the app's window to a given spot on the primary display. Useful before capturing a
# screenshot when the window opened on another monitor.
param(
    [string]$ProcessName = 'CastBridge',
    [int]$X = 60,
    [int]$Y = 60
)

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class WindowMover
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);

    public struct Rect { public int Left, Top, Right, Bottom; }

    delegate bool EnumProc(IntPtr handle, IntPtr param);

    // C# 5 syntax: Windows PowerShell 5.1 uses an older compiler.
    public static IntPtr Find(uint target, string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr handle, IntPtr param)
        {
            uint owner;
            GetWindowThreadProcessId(handle, out owner);
            if (owner != target || !IsWindowVisible(handle)) return true;

            StringBuilder builder = new StringBuilder(GetWindowTextLength(handle) + 1);
            GetWindowText(handle, builder, builder.Capacity);
            if (builder.ToString() != title) return true;

            found = handle;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static void Move(IntPtr handle, int x, int y)
    {
        Rect rect;
        GetWindowRect(handle, out rect);
        SetWindowPos(handle, IntPtr.Zero, x, y, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x0040);
    }
}
'@

$process = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
$window = [WindowMover]::Find([uint32]$process.Id, 'CastBridge')
if ($window -eq [IntPtr]::Zero) { Write-Output 'no visible CastBridge window found'; exit 1 }

[WindowMover]::Move($window, $X, $Y)
Start-Sleep -Milliseconds 500
Write-Output "moved the CastBridge window to $X,$Y"

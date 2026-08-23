param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("TitleBarDrag", "Resize", "XButtons")]
    [string] $Action
)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class ProGpuWindowsInput
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    public static extern void mouse_event(
        uint flags,
        uint dx,
        uint dy,
        uint data,
        UIntPtr extraInfo);

    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;
    public const uint XDown = 0x0080;
    public const uint XUp = 0x0100;
}
"@

# Match the target's per-monitor-v2 coordinate space before reading its
# physical window rectangle or injecting absolute pointer coordinates.
[void] [ProGpuWindowsInput]::SetProcessDpiAwarenessContext([IntPtr] (-4))

function Get-TargetProcess {
    $process = Get-Process -Name "AvaloniaSilkNetInputHarness" `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($null -eq $process) {
        throw "AvaloniaSilkNetInputHarness has no visible top-level window."
    }

    return $process
}

function Get-WindowRect([IntPtr] $window) {
    $rect = New-Object ProGpuWindowsInput+Rect
    if (-not [ProGpuWindowsInput]::GetWindowRect($window, [ref] $rect)) {
        throw "GetWindowRect failed."
    }

    return $rect
}

function Send-MouseButton([uint32] $down, [uint32] $up, [uint32] $data) {
    [ProGpuWindowsInput]::mouse_event($down, 0, 0, $data, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [ProGpuWindowsInput]::mouse_event($up, 0, 0, $data, [UIntPtr]::Zero)
}

$target = Get-TargetProcess
$window = [IntPtr] $target.MainWindowHandle
[void] [ProGpuWindowsInput]::SetForegroundWindow($window)
Start-Sleep -Milliseconds 100
$before = Get-WindowRect $window

switch ($Action) {
    "TitleBarDrag" {
        $dpi = [ProGpuWindowsInput]::GetDpiForWindow($window)
        $x = $before.Left + [int] (($before.Right - $before.Left) / 2)
        $y = $before.Top + [int] (22 * $dpi / 96)
        [void] [ProGpuWindowsInput]::SetCursorPos($x, $y)
        [ProGpuWindowsInput]::mouse_event(
            [ProGpuWindowsInput]::LeftDown,
            0,
            0,
            0,
            [UIntPtr]::Zero)
        for ($step = 1; $step -le 20; $step++) {
            [void] [ProGpuWindowsInput]::SetCursorPos(
                $x + (15 * $step),
                $y + (8 * $step))
            Start-Sleep -Milliseconds 20
        }
        [ProGpuWindowsInput]::mouse_event(
            [ProGpuWindowsInput]::LeftUp,
            0,
            0,
            0,
            [UIntPtr]::Zero)
    }
    "Resize" {
        $x = $before.Right - 3
        $y = $before.Bottom - 3
        [void] [ProGpuWindowsInput]::SetCursorPos($x, $y)
        [ProGpuWindowsInput]::mouse_event(
            [ProGpuWindowsInput]::LeftDown,
            0,
            0,
            0,
            [UIntPtr]::Zero)
        for ($step = 1; $step -le 20; $step++) {
            [void] [ProGpuWindowsInput]::SetCursorPos(
                $x + (20 * $step),
                $y + (12 * $step))
            Start-Sleep -Milliseconds 25
        }
        [ProGpuWindowsInput]::mouse_event(
            [ProGpuWindowsInput]::LeftUp,
            0,
            0,
            0,
            [UIntPtr]::Zero)
    }
    "XButtons" {
        $x = $before.Left + [int] (($before.Right - $before.Left) / 2)
        $y = $before.Top + [int] (($before.Bottom - $before.Top) / 2)
        [void] [ProGpuWindowsInput]::SetCursorPos($x, $y)
        Send-MouseButton `
            ([ProGpuWindowsInput]::XDown) `
            ([ProGpuWindowsInput]::XUp) `
            1
        Send-MouseButton `
            ([ProGpuWindowsInput]::XDown) `
            ([ProGpuWindowsInput]::XUp) `
            2
    }
}

Start-Sleep -Milliseconds 200
$after = $null
try {
    $after = Get-WindowRect $window
}
catch {
    # The telemetry harness exits immediately after all expected events are
    # observed, so a successful injection can legitimately close the target.
}
$afterResult = if ($null -eq $after) {
    $null
}
else {
    [ordered]@{
        Left = $after.Left
        Top = $after.Top
        Width = $after.Right - $after.Left
        Height = $after.Bottom - $after.Top
    }
}
[ordered]@{
    Action = $Action
    Before = [ordered]@{
        Left = $before.Left
        Top = $before.Top
        Width = $before.Right - $before.Left
        Height = $before.Bottom - $before.Top
    }
    After = $afterResult
    TargetExited = $null -eq $after
} | ConvertTo-Json -Depth 3

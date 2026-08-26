param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("TitleBarDrag", "Resize", "ResizeGrip", "Input", "XButtons", "Touch")]
    [string] $Action,
    [string] $OutputPath = "",
    [string] $ReadyPath = "",
    [int] $WaitSeconds = 45
)

$ErrorActionPreference = "Stop"
trap {
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        New-Item -ItemType Directory -Force (Split-Path $OutputPath) |
            Out-Null
        [ordered]@{
            Action = $Action
            Error = ($_ | Out-String).Trim()
        } | ConvertTo-Json -Depth 3 |
            Set-Content -Path $OutputPath -Encoding UTF8
    }
    exit 1
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class ProGpuWindowsInput
{
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr state);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointerInfo
    {
        public uint PointerType;
        public uint PointerId;
        public uint FrameId;
        public uint PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr WindowTarget;
        public NativePoint PixelLocation;
        public NativePoint HimetricLocation;
        public NativePoint PixelLocationRaw;
        public NativePoint HimetricLocationRaw;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointerTouchInfo
    {
        public PointerInfo PointerInfo;
        public uint TouchFlags;
        public uint TouchMask;
        public Rect Contact;
        public Rect ContactRaw;
        public uint Orientation;
        public uint Pressure;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct PointerTypeInfo
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public PointerTouchInfo TouchInfo;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowCallback callback,
        IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr window,
        StringBuilder className,
        int capacity);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

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

    [DllImport("user32.dll")]
    public static extern void keybd_event(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(
        uint pointerType,
        uint maxCount,
        uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectSyntheticPointerInput(
        IntPtr device,
        ref PointerTypeInfo pointerInfo,
        uint count);

    [DllImport("user32.dll")]
    private static extern void DestroySyntheticPointerDevice(
        IntPtr device);

    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;
    public const uint RightDown = 0x0008;
    public const uint RightUp = 0x0010;
    public const uint MiddleDown = 0x0020;
    public const uint MiddleUp = 0x0040;
    public const uint XDown = 0x0080;
    public const uint XUp = 0x0100;
    public const uint Wheel = 0x0800;
    public const uint NoMove = 0x0002;
    public const uint NoZOrder = 0x0004;
    public const uint NoActivate = 0x0010;
    public const uint KeyUp = 0x0002;
    public const byte Escape = 0x1b;
    public const byte Shift = 0x10;
    public const byte Control = 0x11;
    public const byte A = 0x41;
    public const byte K = 0x4b;

    public static void InjectTouchTap(int x, int y)
    {
        IntPtr device = CreateSyntheticPointerDevice(2, 1, 3);
        if (device == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "CreateSyntheticPointerDevice failed: " +
                Marshal.GetLastWin32Error());
        }
        try
        {
            var contact = CreateTouchContact(x, y);
            contact.PointerInfo.PointerFlags =
                0x00000001 | 0x00000002 | 0x00000004 |
                0x00002000 | 0x00004000 | 0x00010000;
            InjectContact(device, contact);
            Thread.Sleep(60);

            contact.PointerInfo.PointerFlags =
                0x00000002 | 0x00000004 |
                0x00002000 | 0x00004000 | 0x00020000;
            contact.PointerInfo.PixelLocation.X += 12;
            contact.PointerInfo.PixelLocation.Y += 8;
            contact.Contact = ContactRect(
                contact.PointerInfo.PixelLocation.X,
                contact.PointerInfo.PixelLocation.Y);
            contact.ContactRaw = contact.Contact;
            InjectContact(device, contact);
            Thread.Sleep(60);

            contact.PointerInfo.PointerFlags = 0x00040000;
            InjectContact(device, contact);
        }
        finally
        {
            DestroySyntheticPointerDevice(device);
        }
    }

    private static PointerTouchInfo CreateTouchContact(int x, int y)
    {
        var point = new NativePoint { X = x, Y = y };
        var contact = new PointerTouchInfo();
        contact.PointerInfo.PointerType = 2;
        contact.PointerInfo.PointerId = 1;
        contact.PointerInfo.PixelLocation = point;
        contact.PointerInfo.PixelLocationRaw = point;
        contact.TouchMask = 0x00000001 | 0x00000002 | 0x00000004;
        contact.Contact = ContactRect(x, y);
        contact.ContactRaw = contact.Contact;
        contact.Orientation = 90;
        contact.Pressure = 512;
        return contact;
    }

    private static Rect ContactRect(int x, int y)
    {
        return new Rect
        {
            Left = x - 4,
            Top = y - 4,
            Right = x + 4,
            Bottom = y + 4
        };
    }

    private static void InjectContact(
        IntPtr device,
        PointerTouchInfo contact)
    {
        var pointer = new PointerTypeInfo
        {
            Type = 2,
            TouchInfo = contact
        };
        if (!InjectSyntheticPointerInput(device, ref pointer, 1))
        {
            throw new InvalidOperationException(
                "InjectSyntheticPointerInput failed: " +
                Marshal.GetLastWin32Error());
        }
    }

    public static IntPtr FindLargestVisibleWindow(int processId)
    {
        IntPtr largestWindow = IntPtr.Zero;
        long largestArea = 0;
        IntPtr largestGlfwWindow = IntPtr.Zero;
        long largestGlfwArea = 0;
        EnumWindows(
            (window, state) =>
            {
                uint owner;
                Rect rect;
                GetWindowThreadProcessId(window, out owner);
                if (owner != (uint) processId || !IsWindowVisible(window) ||
                    !GetWindowRect(window, out rect))
                {
                    return true;
                }

                long width = Math.Max(0, rect.Right - rect.Left);
                long height = Math.Max(0, rect.Bottom - rect.Top);
                long area = width * height;
                if (area > largestArea)
                {
                    largestArea = area;
                    largestWindow = window;
                }

                var className = new StringBuilder(64);
                if (GetClassName(window, className, className.Capacity) > 0 &&
                    className.ToString().StartsWith(
                        "GLFW",
                        StringComparison.Ordinal) &&
                    area > largestGlfwArea)
                {
                    largestGlfwArea = area;
                    largestGlfwWindow = window;
                }

                return true;
            },
            IntPtr.Zero);
        return largestGlfwWindow != IntPtr.Zero
            ? largestGlfwWindow
            : largestWindow;
    }
}
"@

# Match the target's per-monitor-v2 coordinate space before reading its
# physical window rectangle or injecting absolute pointer coordinates.
[void] [ProGpuWindowsInput]::SetProcessDpiAwarenessContext([IntPtr] (-4))

function Get-TargetProcess {
    $process = Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProcessName -eq "AvaloniaSilkNetInputHarness"
        } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($null -eq $process) {
        throw "AvaloniaSilkNetInputHarness is not running."
    }

    $window = [ProGpuWindowsInput]::FindLargestVisibleWindow($process.Id)
    if ($window -eq [IntPtr]::Zero) {
        throw "AvaloniaSilkNetInputHarness has no visible top-level window."
    }

    return [ordered]@{
        Process = $process
        Window = $window
    }
}

function Wait-TargetProcess {
    $deadline = (Get-Date).AddSeconds([Math]::Max(1, $WaitSeconds))
    do {
        try {
            $target = Get-TargetProcess
            if (-not [string]::IsNullOrWhiteSpace($ReadyPath) -and
                -not (Test-Path $ReadyPath)) {
                throw "The input harness is not ready."
            }
            return $target
        }
        catch {
            if ((Get-Date) -ge $deadline) {
                throw
            }
            Start-Sleep -Milliseconds 250
        }
    } while ($true)
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

function Send-Key([byte] $virtualKey) {
    [ProGpuWindowsInput]::keybd_event(
        $virtualKey,
        0,
        0,
        [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [ProGpuWindowsInput]::keybd_event(
        $virtualKey,
        0,
        [ProGpuWindowsInput]::KeyUp,
        [UIntPtr]::Zero)
}

$target = Wait-TargetProcess
$window = [IntPtr] $target.Window
[void] [ProGpuWindowsInput]::SetForegroundWindow($window)
Start-Sleep -Milliseconds 100
[ProGpuWindowsInput]::keybd_event(
    [ProGpuWindowsInput]::Escape,
    0,
    0,
    [UIntPtr]::Zero)
[ProGpuWindowsInput]::keybd_event(
    [ProGpuWindowsInput]::Escape,
    0,
    [ProGpuWindowsInput]::KeyUp,
    [UIntPtr]::Zero)
[ProGpuWindowsInput]::mouse_event(
    [ProGpuWindowsInput]::LeftUp,
    0,
    0,
    0,
    [UIntPtr]::Zero)
$before = Get-WindowRect $window
$hoverWindow = [IntPtr]::Zero

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
        for ($step = 1; $step -le 20; $step++) {
            if (-not [ProGpuWindowsInput]::SetWindowPos(
                $window,
                [IntPtr]::Zero,
                0,
                0,
                ($before.Right - $before.Left) + (20 * $step),
                ($before.Bottom - $before.Top) + (12 * $step),
                [ProGpuWindowsInput]::NoMove -bor
                    [ProGpuWindowsInput]::NoZOrder -bor
                    [ProGpuWindowsInput]::NoActivate)) {
                if ($null -eq (Get-Process -Id $target.Process.Id `
                    -ErrorAction SilentlyContinue)) {
                    break
                }
                throw "SetWindowPos failed during native resize."
            }
            Start-Sleep -Milliseconds 25
        }
    }
    "ResizeGrip" {
        # Stay inside the 48-logical-pixel Avalonia grip while avoiding the
        # invisible Win32 resize border, which can consume the mouse message
        # before the custom decoration role sees it at high DPI.
        $x = $before.Right - 60
        $y = $before.Bottom - 60
        [void] [ProGpuWindowsInput]::SetCursorPos($x, $y)
        Start-Sleep -Milliseconds 100
        $point = New-Object ProGpuWindowsInput+NativePoint
        $point.X = $x
        $point.Y = $y
        $hoverWindow = [ProGpuWindowsInput]::WindowFromPoint($point)
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
    "Input" {
        Send-Key ([ProGpuWindowsInput]::A)

        [ProGpuWindowsInput]::keybd_event(
            [ProGpuWindowsInput]::Control,
            0,
            0,
            [UIntPtr]::Zero)
        [ProGpuWindowsInput]::keybd_event(
            [ProGpuWindowsInput]::Shift,
            0,
            0,
            [UIntPtr]::Zero)
        Send-Key ([ProGpuWindowsInput]::K)
        [ProGpuWindowsInput]::keybd_event(
            [ProGpuWindowsInput]::Shift,
            0,
            [ProGpuWindowsInput]::KeyUp,
            [UIntPtr]::Zero)
        [ProGpuWindowsInput]::keybd_event(
            [ProGpuWindowsInput]::Control,
            0,
            [ProGpuWindowsInput]::KeyUp,
            [UIntPtr]::Zero)

        $x = $before.Left + [int] (($before.Right - $before.Left) / 2)
        $y = $before.Top + [int] (($before.Bottom - $before.Top) * 3 / 4)
        [void] [ProGpuWindowsInput]::SetCursorPos($x, $y)
        Start-Sleep -Milliseconds 100
        Send-MouseButton `
            ([ProGpuWindowsInput]::LeftDown) `
            ([ProGpuWindowsInput]::LeftUp) `
            0
        Send-MouseButton `
            ([ProGpuWindowsInput]::RightDown) `
            ([ProGpuWindowsInput]::RightUp) `
            0
        Send-MouseButton `
            ([ProGpuWindowsInput]::MiddleDown) `
            ([ProGpuWindowsInput]::MiddleUp) `
            0
        Send-MouseButton `
            ([ProGpuWindowsInput]::XDown) `
            ([ProGpuWindowsInput]::XUp) `
            1
        Send-MouseButton `
            ([ProGpuWindowsInput]::XDown) `
            ([ProGpuWindowsInput]::XUp) `
            2
        [ProGpuWindowsInput]::mouse_event(
            [ProGpuWindowsInput]::Wheel,
            0,
            0,
            120,
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
    "Touch" {
        $x = $before.Left + [int] (($before.Right - $before.Left) / 2)
        $y = $before.Top + [int] (($before.Bottom - $before.Top) * 3 / 4)
        [ProGpuWindowsInput]::InjectTouchTap($x, $y)
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
$result = [ordered]@{
    Action = $Action
    WindowHandle = $window.ToInt64()
    HoverWindowHandle = $hoverWindow.ToInt64()
    Before = [ordered]@{
        Left = $before.Left
        Top = $before.Top
        Width = $before.Right - $before.Left
        Height = $before.Bottom - $before.Top
    }
    After = $afterResult
    TargetExited = $null -eq $after
} | ConvertTo-Json -Depth 3
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    New-Item -ItemType Directory -Force (Split-Path $OutputPath) |
        Out-Null
    Set-Content -Path $OutputPath -Value $result -Encoding UTF8
}
$result

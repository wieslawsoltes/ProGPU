using System;
using Windows.Foundation.Metadata;

namespace Microsoft.UI;

[ContractVersion(WindowsAppSdkContract.Name, WindowsAppSdkContract.Version1_4)]
public delegate void ClosableNotifierHandler();

[ContractVersion(WindowsAppSdkContract.Name, WindowsAppSdkContract.Version1_4)]
public interface IClosableNotifier
{
    bool IsClosed { get; }

    event ClosableNotifierHandler? Closed;

    event ClosableNotifierHandler? FrameworkClosed;
}

[ContractVersion(WindowsAppSdkContract.Name, WindowsAppSdkContract.Version1)]
public sealed class ColorHelper
{
    private ColorHelper()
    {
    }

    public static Windows.UI.Color FromArgb(
        byte a,
        byte r,
        byte g,
        byte b) =>
        Windows.UI.Color.FromArgb(a, r, g, b);
}

[ContractVersion(WindowsAppSdkContract.Name, WindowsAppSdkContract.Version1)]
public struct DisplayId : IEquatable<DisplayId>
{
    public ulong Value;

    public DisplayId(ulong _Value)
    {
        Value = _Value;
    }

    public readonly bool Equals(DisplayId other) => Value == other.Value;

    public override readonly bool Equals(object? obj) =>
        obj is DisplayId other && Equals(other);

    public override readonly int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(DisplayId x, DisplayId y) => x.Equals(y);

    public static bool operator !=(DisplayId x, DisplayId y) => !x.Equals(y);
}

[ContractVersion(WindowsAppSdkContract.Name, WindowsAppSdkContract.Version1)]
public struct IconId : IEquatable<IconId>
{
    public ulong Value;

    public IconId(ulong _Value)
    {
        Value = _Value;
    }

    public readonly bool Equals(IconId other) => Value == other.Value;

    public override readonly bool Equals(object? obj) =>
        obj is IconId other && Equals(other);

    public override readonly int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(IconId x, IconId y) => x.Equals(y);

    public static bool operator !=(IconId x, IconId y) => !x.Equals(y);
}

[ContractVersion(WindowsAppSdkContract.Name, WindowsAppSdkContract.Version1)]
public struct WindowId : IEquatable<WindowId>
{
    public ulong Value;

    public WindowId(ulong _Value)
    {
        Value = _Value;
    }

    public readonly bool Equals(WindowId other) => Value == other.Value;

    public override readonly bool Equals(object? obj) =>
        obj is WindowId other && Equals(other);

    public override readonly int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(WindowId x, WindowId y) => x.Equals(y);

    public static bool operator !=(WindowId x, WindowId y) => !x.Equals(y);
}

public class Win32Interop
{
    public static DisplayId GetDisplayIdFromMonitor(IntPtr hmonitor) =>
        new(PackHandle(hmonitor));

    public static IconId GetIconIdFromIcon(IntPtr hicon) =>
        new(PackHandle(hicon));

    public static WindowId GetWindowIdFromWindow(IntPtr hwnd) =>
        new(PackHandle(hwnd));

    public static IntPtr GetIconFromIconId(IconId iconId) =>
        UnpackHandle(iconId.Value);

    public static IntPtr GetMonitorFromDisplayId(DisplayId displayId) =>
        UnpackHandle(displayId.Value);

    public static IntPtr GetWindowFromWindowId(WindowId windowId) =>
        UnpackHandle(windowId.Value);

    private static ulong PackHandle(IntPtr handle) =>
        unchecked((ulong)handle.ToInt64());

    private static IntPtr UnpackHandle(ulong value) =>
        new(unchecked((long)value));
}

internal static class WindowsAppSdkContract
{
    public const string Name =
        "Microsoft.Foundation.WindowsAppSDKContract";
    public const uint Version1 = 0x00010000;
    public const uint Version1_4 = 0x00010004;
}

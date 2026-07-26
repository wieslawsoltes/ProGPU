using ProGPU.Backend.Dawn;
using Xunit;

namespace Avalonia.ProGpu.UnitTests;

public sealed class DawnNativeWindowSourceTests
{
    [Theory]
    [InlineData("HWND", true, false, DawnNativeWindowKind.Win32)]
    [InlineData("XID", false, true, DawnNativeWindowKind.Xlib)]
    public void TypedAvaloniaHandleMapsToNativeDawnBackend(
        string descriptor,
        bool isWindows,
        bool isLinux,
        DawnNativeWindowKind expected)
    {
        Assert.True(
            DawnNativeWindowSource.TryGetKind(
                descriptor,
                isWindows,
                isLinux,
                out DawnNativeWindowKind actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("HWND", false, true)]
    [InlineData("XID", true, false)]
    [InlineData("NSWindow", false, false)]
    [InlineData("", true, false)]
    public void MismatchedOrUnknownHandleIsRejected(
        string descriptor,
        bool isWindows,
        bool isLinux)
    {
        Assert.False(
            DawnNativeWindowSource.TryGetKind(
                descriptor,
                isWindows,
                isLinux,
                out _));
    }
}

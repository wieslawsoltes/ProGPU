using Microsoft.UI;
using Xunit;

namespace ProGPU.Tests;

public sealed class MicrosoftUiFoundationTests
{
    [Fact]
    public void IdentifierValuesPreserveEqualityAndHashSemantics()
    {
        var display = new DisplayId(0x1234);
        var icon = new IconId(0x1234);
        var window = new WindowId(0x1234);

        Assert.Equal(new DisplayId(0x1234), display);
        Assert.Equal(new IconId(0x1234), icon);
        Assert.Equal(new WindowId(0x1234), window);
        Assert.True(display == new DisplayId(0x1234));
        Assert.True(icon != new IconId(0x4321));
        Assert.Equal(window.GetHashCode(), new WindowId(0x1234).GetHashCode());

        display.Value = 0x4321;
        Assert.Equal(0x4321UL, display.Value);
    }

    [Fact]
    public void Win32InteropRoundTripsTypedHandleBits()
    {
        var handle = new IntPtr(0x1234_5678);

        Assert.Equal(
            handle,
            Win32Interop.GetMonitorFromDisplayId(
                Win32Interop.GetDisplayIdFromMonitor(handle)));
        Assert.Equal(
            handle,
            Win32Interop.GetIconFromIconId(
                Win32Interop.GetIconIdFromIcon(handle)));
        Assert.Equal(
            handle,
            Win32Interop.GetWindowFromWindowId(
                Win32Interop.GetWindowIdFromWindow(handle)));
    }

    [Fact]
    public void Win32InteropPreservesNullHandle()
    {
        Assert.Equal(default, Win32Interop.GetDisplayIdFromMonitor(IntPtr.Zero));
        Assert.Equal(default, Win32Interop.GetIconIdFromIcon(IntPtr.Zero));
        Assert.Equal(default, Win32Interop.GetWindowIdFromWindow(IntPtr.Zero));
        Assert.Equal(IntPtr.Zero, Win32Interop.GetWindowFromWindowId(default));
    }

    [Fact]
    public void ColorHelperPreservesArgbChannels()
    {
        var color = ColorHelper.FromArgb(0x11, 0x22, 0x33, 0x44);

        Assert.Equal(0x11, color.A);
        Assert.Equal(0x22, color.R);
        Assert.Equal(0x33, color.G);
        Assert.Equal(0x44, color.B);
    }

    [Fact]
    public void ClosableNotifierContractExposesApplicationAndFrameworkEvents()
    {
        var notifier = new TestClosableNotifier();
        var notifications = new List<string>();
        notifier.Closed += () => notifications.Add("application");
        notifier.FrameworkClosed += () => notifications.Add("framework");

        notifier.Close();

        Assert.True(notifier.IsClosed);
        Assert.Equal(["framework", "application"], notifications);
    }

    private sealed class TestClosableNotifier : IClosableNotifier
    {
        public bool IsClosed { get; private set; }

        public event ClosableNotifierHandler? Closed;

        public event ClosableNotifierHandler? FrameworkClosed;

        public void Close()
        {
            IsClosed = true;
            FrameworkClosed?.Invoke();
            Closed?.Invoke();
        }
    }
}

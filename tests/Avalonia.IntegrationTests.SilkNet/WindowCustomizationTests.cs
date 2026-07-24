using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.SilkNet;
using Avalonia.Threading;
using Xunit;

namespace Avalonia.IntegrationTests.SilkNet;

public sealed class WindowCustomizationTests
{
    [Fact]
    public Task AppliesExtendedChromeAndBestAvailableBackdrop()
    {
        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = new Window
            {
                Width = 420,
                Height = 300,
                Background = Brushes.Transparent,
                ExtendClientAreaToDecorationsHint = true,
                ExtendClientAreaTitleBarHeightHint = 44,
                TransparencyLevelHint =
                [
                    WindowTransparencyLevel.Mica,
                    WindowTransparencyLevel.AcrylicBlur,
                    WindowTransparencyLevel.Blur,
                    WindowTransparencyLevel.Transparent
                ]
            };

            try
            {
                window.Show();
                await window.WhenLoadedAsync();

                var impl = Assert.IsType<WindowImpl>(window.PlatformImpl);
                Assert.NotEqual(IntPtr.Zero, impl.Handle.Handle);
                Assert.True(impl.IsClientAreaExtendedToDecorations);

                if (OperatingSystem.IsMacOS())
                {
                    Assert.Equal("NSWindow", impl.Handle.HandleDescriptor);
                    Assert.False(impl.NeedsManagedDecorations);
                    Assert.Equal(44, impl.ExtendedMargins.Top);
                    Assert.Equal(WindowTransparencyLevel.Mica, impl.TransparencyLevel);

                    window.WindowDecorations = WindowDecorations.BorderOnly;
                    Assert.Equal(0, impl.ExtendedMargins.Top);
                    window.WindowDecorations = WindowDecorations.Full;
                    Assert.Equal(44, impl.ExtendedMargins.Top);
                }
                else if (OperatingSystem.IsWindows())
                {
                    Assert.Equal("HWND", impl.Handle.HandleDescriptor);
                    Assert.True(impl.NeedsManagedDecorations);
                    Assert.Equal(0, impl.ExtendedMargins.Top);
                }
                else if (OperatingSystem.IsLinux())
                {
                    Assert.True(impl.NeedsManagedDecorations);
                    Assert.Equal(0, impl.ExtendedMargins.Top);
                    Assert.Contains(
                        impl.TransparencyLevel,
                        new[]
                        {
                            WindowTransparencyLevel.AcrylicBlur,
                            WindowTransparencyLevel.Transparent
                        });
                }

                window.CanResize = false;
                window.CanMinimize = false;
                window.CanMaximize = false;
                Assert.False(impl.AllowedWindowActions.HasFlag(
                    Avalonia.Controls.Platform.PlatformAllowedWindowActions.Minimize));
                Assert.False(impl.AllowedWindowActions.HasFlag(
                    Avalonia.Controls.Platform.PlatformAllowedWindowActions.Maximize));
            }
            finally
            {
                window.Close();
                if (window.PlatformImpl is WindowImpl impl)
                {
                    await impl.DisposedTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        });
    }
}

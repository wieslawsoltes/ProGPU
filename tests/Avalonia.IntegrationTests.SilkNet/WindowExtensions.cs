using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.SilkNet;
using Xunit;

namespace Avalonia.IntegrationTests.SilkNet;

internal static class WindowExtensions
{
    public static Task WhenLoadedAsync(this Window window)
    {
        if (window.IsLoaded)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource();
        window.Loaded += OnLoaded;
        return tcs.Task;

        void OnLoaded(object? sender, RoutedEventArgs e)
        {
            window.Loaded -= OnLoaded;
            tcs.TrySetResult();
        }
    }

    public static Screen GetScreenAtIndex(this Window window, int index)
        => window.Screens.All[index];

    public static PixelSize GetSilkNetClientSize(this Window window)
    {
        var impl = (WindowImpl)window.PlatformImpl!;
        Assert.NotNull(impl);
        return new PixelSize((int)impl.SilkWindow.FramebufferSize.X, (int)impl.SilkWindow.FramebufferSize.Y);
    }

    public static PixelRect GetSilkNetWindowBounds(this Window window)
    {
        var impl = (WindowImpl)window.PlatformImpl!;
        Assert.NotNull(impl);
        return new PixelRect(
            new PixelPoint(impl.SilkWindow.Position.X, impl.SilkWindow.Position.Y),
            new PixelSize((int)impl.SilkWindow.Size.X, (int)impl.SilkWindow.Size.Y)
        );
    }
}

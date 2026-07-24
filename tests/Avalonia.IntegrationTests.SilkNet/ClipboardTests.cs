using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Xunit;

namespace Avalonia.IntegrationTests.SilkNet;

public class ClipboardTests : IDisposable
{
    private Window? _window;

    [Fact]
    public Task Clipboard_Can_Copy_And_Paste_Text()
    {
        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            _window = new Window
            {
                Width = 100,
                Height = 100,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            _window.Show();
            await _window.WhenLoadedAsync();

            var clipboard = AvaloniaLocator.Current.GetService<IClipboard>();
            Assert.NotNull(clipboard);

            string testText = "Hello from Silk.NET Clipboard Integration Test!";
            await clipboard.SetTextAsync(testText);

            string? pastedText = await clipboard.TryGetTextAsync();
            Assert.Equal(testText, pastedText);
        });
    }

    public void Dispose()
    {
        var window = _window;
        if (window != null)
        {
            var impl = window.PlatformImpl as Avalonia.SilkNet.WindowImpl;
            Dispatcher.UIThread.Post(() => window.Close());
            if (impl != null)
            {
                impl.DisposedTask.Wait(3000);
            }
            _window = null;
        }
        System.Threading.Thread.Sleep(200);
    }
}

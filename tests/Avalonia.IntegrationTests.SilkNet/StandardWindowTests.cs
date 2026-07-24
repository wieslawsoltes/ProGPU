using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Avalonia.IntegrationTests.SilkNet;

public abstract class StandardWindowTests : IDisposable
{
    private const int ClientWidth = 200;
    private const int ClientHeight = 200;

    private Window? _window;

    private Window Window
    {
        get
        {
            Assert.NotNull(_window);
            return _window;
        }
    }

    protected abstract WindowDecorations Decorations { get; }

    protected abstract bool HasCaption { get; }

    public static MatrixTheoryData<int, WindowState, bool> States
        => new(
            Enumerable.Range(0, 1),
            Enum.GetValues<WindowState>(),
            [true, false]);

    private async Task InitWindowAsync(int screenIndex, WindowState state, bool canResize)
    {
        Assert.Null(_window);

        _window = new Window
        {
            CanResize = canResize,
            WindowState = state,
            WindowDecorations = Decorations,
            ExtendClientAreaToDecorationsHint = false,
            Width = ClientWidth,
            Height = ClientHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Border
            {
                Background = Brushes.DodgerBlue,
                BorderBrush = Brushes.Yellow,
                BorderThickness = new Thickness(1)
            }
        };

        var screens = _window.Screens;
        var allScreens = screens.All;
        var screenCenter = allScreens[screenIndex].Bounds.Center;
        _window.Position = new PixelPoint(screenCenter.X - ClientWidth / 2, screenCenter.Y - ClientHeight / 2);

        _window.Show();

        await Window.WhenLoadedAsync();
    }

    [Theory]
    [MemberData(nameof(States))]
    public Task Maximized_State_Fills_Screen_Working_Area(int screenIndex, WindowState initialState, bool canResize)
    {
        if (System.OperatingSystem.IsMacOS() && initialState == WindowState.FullScreen)
        {
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await InitWindowAsync(screenIndex, initialState, canResize);

            if (initialState != WindowState.Maximized)
            {
                Window.WindowState = WindowState.Maximized;
                await Task.Delay(200);
            }

            var clientSize = Window.GetSilkNetClientSize();
            var screenWorkingArea = Window.GetScreenAtIndex(screenIndex).WorkingArea;

            bool hasCaption = HasCaption;
            if (hasCaption)
            {
                Assert.Equal(screenWorkingArea.Size.Width, clientSize.Width);
                Assert.True(clientSize.Height < screenWorkingArea.Size.Height);
            }
            else
                Assert.Equal(screenWorkingArea.Size, clientSize);
        });
    }

    [Theory]
    [MemberData(nameof(States))]
    public Task FullScreen_State_Fills_Screen(int screenIndex, WindowState initialState, bool canResize)
    {
        if (System.OperatingSystem.IsMacOS())
        {
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await InitWindowAsync(screenIndex, initialState, canResize);

            if (initialState != WindowState.FullScreen)
            {
                Window.WindowState = WindowState.FullScreen;
                await Task.Delay(200);
            }

            var clientSize = Window.GetSilkNetClientSize();
            var screenBounds = Window.GetScreenAtIndex(screenIndex).Bounds;
            Assert.Equal(screenBounds.Width, clientSize.Width);
            Assert.Equal(screenBounds.Height, clientSize.Height);

            var windowBounds = Window.GetSilkNetWindowBounds();
            Assert.Equal(screenBounds, windowBounds);
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
                Assert.True(impl.DisposedTask.Wait(3000), "Timed out waiting for the native window to dispose.");
            }
            _window = null;
        }
        System.Threading.Thread.Sleep(200);
    }

    public sealed class DecorationsFull : StandardWindowTests
    {
        protected override WindowDecorations Decorations
            => WindowDecorations.Full;

        protected override bool HasCaption
            => true;
    }

    public sealed class DecorationsBorderOnly : StandardWindowTests
    {
        protected override WindowDecorations Decorations
            => WindowDecorations.BorderOnly;

        protected override bool HasCaption
            => false;
    }

    public sealed class DecorationsNone : StandardWindowTests
    {
        protected override WindowDecorations Decorations
            => WindowDecorations.None;

        protected override bool HasCaption
            => false;
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Avalonia.IntegrationTests.SilkNet;

public abstract class ExtendClientAreaWindowTests : IDisposable
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
            ExtendClientAreaToDecorationsHint = true,
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

        var screenCenter = _window.Screens.All[screenIndex].Bounds.Center;
        _window.Position = new PixelPoint(screenCenter.X - ClientWidth / 2, screenCenter.Y - ClientHeight / 2);

        _window.Show();

        await Window.WhenLoadedAsync();
    }

    [Theory(Skip = "Client area extension is not supported by Silk.NET/GLFW backend")]
    [MemberData(nameof(States))]
    public Task Normal_State_Respects_Client_Size(int screenIndex, WindowState initialState, bool canResize)
    {
        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await InitWindowAsync(screenIndex, initialState, canResize);

            if (initialState != WindowState.Normal)
            {
                Window.WindowState = WindowState.Normal;
                await Task.Delay(200);
            }

            var expected = PixelSize.FromSize(new Size(ClientWidth, ClientHeight), Window.RenderScaling);
            var clientSize = Window.GetSilkNetClientSize();
            Assert.Equal(expected, clientSize);

            VerifyNormalState(canResize);
        });
    }

    protected abstract void VerifyNormalState(bool canResize);

    [Theory(Skip = "Client area extension is not supported by Silk.NET/GLFW backend")]
    [MemberData(nameof(States))]
    public Task Maximized_State_Fills_Screen_Working_Area(int screenIndex, WindowState initialState, bool canResize)
    {
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
            Assert.Equal(screenWorkingArea.Size, clientSize);

            VerifyMaximizedState();
        });
    }

    protected abstract void VerifyMaximizedState();

    [Theory(Skip = "Client area extension is not supported by Silk.NET/GLFW backend")]
    [MemberData(nameof(States))]
    public Task FullScreen_State_Fills_Screen(int screenIndex, WindowState initialState, bool canResize)
    {
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

            AssertNoTitleBar();
        });
    }

    protected void AssertHasBorder()
    {
        var clientSize = Window.GetSilkNetClientSize();
        var windowBounds = Window.GetSilkNetWindowBounds();
        Assert.NotEqual(clientSize.Width, windowBounds.Width);
        Assert.NotEqual(clientSize.Height, windowBounds.Height);
    }

    protected void AssertNoBorder()
    {
        var clientSize = Window.GetSilkNetClientSize();
        var windowBounds = Window.GetSilkNetWindowBounds();
        Assert.Equal(clientSize.Width, windowBounds.Width);
        Assert.Equal(clientSize.Height, windowBounds.Height);
    }

    protected (double TitleBarHeight, double ButtonsHeight) GetTitleBarInfo()
    {
        var host = Window.GetVisualParent();
        if (host == null)
            host = Window;

        host.GetLayoutManager()!.ExecuteLayoutPass();

        var titlebar = host.GetVisualDescendants().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == "AvaloniaTitleBar");
        var closeButton = host.GetVisualDescendants().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == "Close");
        return (
            titlebar?.IsEffectivelyVisible == true ? titlebar.Bounds.Height : 0,
            closeButton?.IsEffectivelyVisible == true ? closeButton.Bounds.Height : 0);
    }

    private void AssertNoTitleBar()
    {
        var (titleBarHeight, buttonsHeight) = GetTitleBarInfo();
        Assert.Equal(0, titleBarHeight);
        Assert.Equal(0, buttonsHeight);
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

    public sealed class DecorationsFull : ExtendClientAreaWindowTests
    {
        protected override WindowDecorations Decorations
            => WindowDecorations.Full;

        protected override void VerifyNormalState(bool canResize)
        {
            AssertHasBorder();
            AssertLargeTitleBarWithButtons();
        }

        protected override void VerifyMaximizedState()
            => AssertLargeTitleBarWithButtons();

        private void AssertLargeTitleBarWithButtons()
        {
            var (titleBarHeight, buttonsHeight) = GetTitleBarInfo();
            Assert.True(titleBarHeight > 20);
            Assert.True(buttonsHeight > 20);
        }
    }

    public sealed class DecorationsBorderOnly : ExtendClientAreaWindowTests
    {
        protected override WindowDecorations Decorations
            => WindowDecorations.BorderOnly;

        protected override void VerifyNormalState(bool canResize)
        {
            AssertHasBorder();
            AssertNoTitleBar();
        }

        protected override void VerifyMaximizedState()
            => AssertNoTitleBar();
    }

    public sealed class DecorationsNone : ExtendClientAreaWindowTests
    {
        protected override WindowDecorations Decorations
            => WindowDecorations.None;

        protected override void VerifyNormalState(bool canResize)
        {
            AssertNoBorder();
            AssertNoTitleBar();
        }

        protected override void VerifyMaximizedState()
            => AssertNoTitleBar();
    }
}

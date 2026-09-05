using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Scene;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Samples.Suntrail.Presentation;
using Windows.Devices.Input;
using Xunit;

namespace ProGPU.Samples.Suntrail.Tests;

public sealed class MenuInputTests
{
    [Theory]
    [InlineData(PointerDeviceType.Mouse)]
    [InlineData(PointerDeviceType.Touch)]
    public void PrimaryActionKeepsGameColorsWhenReshownAfterClick(PointerDeviceType device)
    {
        var oldApp = Application.Current;
        var oldTheme = ThemeManager.CurrentTheme;
        var oldInput = InputSystem.Current;
        try
        {
            Application.Current = new App();
            var view = new GameView();
            InputSystem.Current = InputSystem.CreateExternalState(view);
            void Layout() { view.Measure(new(1440, 900)); view.Arrange(new Rect(0, 0, 1440, 900)); }
            Layout();
            var button = Descendants(view).OfType<Button>().Single(b => b.Content is TextBlock t && t.Text.StartsWith("Begin adventure"));
            var point = Vector2.Transform(button.Size / 2, button.GetGlobalCoordinateTransformMatrix());
            void Send(PointerInputKind kind) => InputSystem.InjectPointer(new(kind, 77, device, point, 1_000_000,
                IsInContact: kind == PointerInputKind.Pressed,
                IsLeftButtonPressed: device == PointerDeviceType.Mouse && kind == PointerInputKind.Pressed));
            Send(PointerInputKind.Moved);
            Send(PointerInputKind.Pressed);
            Send(PointerInputKind.Released);
            Assert.Equal(GameMode.Playing, view.Surface.Session.Mode);
            Assert.False(button.IsPressed);
            view.OnKeyDown(new() { Key = Silk.NET.Input.Key.Escape });
            Layout();
            Assert.Equal(GameMode.Paused, view.Surface.Session.Mode);
            Assert.Equal("Back to the trail   →", Assert.IsType<TextBlock>(button.Content).Text);
            Assert.Equal("Back to the trail   →", Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button));
            // The mouse remains over the reappearing menu. Its scoped colors must
            // win over the original Fluent dictionary in hover and pressed states.
            button.OnPointerEntered(new());
            var presenter = button.GetTemplateChild("ContentPresenter") as ContentPresenter;
            Assert.Same(Application.Current.Resources["SuntrailGold"], presenter?.Background ?? button.GetCurrentBackground());
            Assert.Same(Application.Current.Resources["SuntrailInk"], presenter?.Foreground ?? button.GetCurrentForeground());
            button.OnPointerPressed(new());
            Assert.Same(Application.Current.Resources["SuntrailGold"], presenter?.Background ?? button.GetCurrentBackground());
            button.OnPointerCanceled(new());
            Assert.False(button.IsPressed);
            point = Vector2.Transform(button.Size / 2, button.GetGlobalCoordinateTransformMatrix());
            Send(PointerInputKind.Moved);
            Send(PointerInputKind.Pressed);
            Send(PointerInputKind.Released);
            Assert.Equal(GameMode.Playing, view.Surface.Session.Mode);
        }
        finally { InputSystem.Current = oldInput; Application.Current = oldApp; ThemeManager.CurrentTheme = oldTheme; }
    }

    private static IEnumerable<FrameworkElement> Descendants(FrameworkElement root)
    {
        yield return root;
        foreach (var child in root.Children.OfType<FrameworkElement>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}

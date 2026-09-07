using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Devices.Input;
using ProGPU.Scene;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Samples.Suntrail.Presentation;
using Xunit;
using Key = Silk.NET.Input.Key;

namespace ProGPU.Samples.Suntrail.Tests;

public sealed class TouchControlsTests : IDisposable
{
    private readonly Application _previous = Application.Current;
    public TouchControlsTests() => Application.Current = new App();
    public void Dispose() => Application.Current = _previous;

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(12)]
    public void TouchAndKeyboardHaveIdenticalFullJumpAt12030And10Fps(int stepsPerFrame)
    {
        var keyboard = new GameView(); var touch = new GameView();
        foreach (var view in new[] { keyboard, touch })
        {
            view.OnKeyDown(new() { Key = Key.Enter });
            for (int i = 0; i < 120; i++) view.UpdateAnimations(GameSession.StepSeconds);
            Assert.True(view.Surface.Session.Grounded);
        }
        var controls = (Grid)touch.Children.Last();
        var jump = (FrameworkElement)((StackPanel)controls.Children[1]).Children[1];
        keyboard.OnKeyDown(new() { Key = Key.Space });
        jump.OnPointerPressed(Touch(7, default));
        // This assertion precedes the first frame: the old touch ordering failed here.
        Assert.True(touch.Surface.Input.JumpHeld);
        Assert.True(touch.Surface.Input.JumpPressed);
        float top = touch.Surface.Session.Position.Y;
        for (int tick = 0; tick < 120; tick += stepsPerFrame)
        {
            keyboard.UpdateAnimations(GameSession.StepSeconds * stepsPerFrame);
            touch.UpdateAnimations(GameSession.StepSeconds * stepsPerFrame);
            Assert.Equal(keyboard.Surface.Session.Position, touch.Surface.Session.Position);
            Assert.Equal(keyboard.Surface.Session.Velocity, touch.Surface.Session.Velocity);
            top = Math.Min(top, touch.Surface.Session.Position.Y);
        }
        Assert.True(top < 415, "A held jump must clear a 130-unit ledge.");
        jump.OnPointerReleased(Touch(7, default));
        Assert.False(touch.Surface.Input.JumpHeld);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void StickUsesDeadzoneAndOuterSprintAndIgnoresOtherFingers(bool floating)
    {
        var stick = new TouchStick { Floating = floating };
        stick.Measure(new(300, 160)); stick.Arrange(new Rect(0, 0, 300, 160));
        float origin = floating ? 150 : 76;
        stick.OnPointerPressed(Touch(10, new(origin, 80)));
        stick.OnPointerMoved(Touch(10, new(origin + 5, 80)));
        Assert.Equal(0, stick.Axis); Assert.False(stick.Sprint);
        stick.OnPointerMoved(Touch(10, new(origin + 24, 80)));
        Assert.InRange(stick.Axis, .40f, .42f); Assert.False(stick.Sprint);
        stick.OnPointerMoved(Touch(11, new(0, 80)));
        Assert.InRange(stick.Axis, .40f, .42f);
        stick.OnPointerReleased(Touch(11, default));
        Assert.True(stick.Axis > 0);
        stick.OnPointerMoved(Touch(10, new(origin + 80, 80)));
        Assert.Equal(1, stick.Axis); Assert.True(stick.Sprint);
        stick.OnPointerCanceled(Touch(10, default));
        Assert.Equal(0, stick.Axis); Assert.False(stick.Sprint);
    }

    [Fact]
    public void IndependentFingersCanMoveAndJumpAndSettingsClearHeldInput()
    {
        var view = new GameView(touchOptions: 10);
        view.OnKeyDown(new() { Key = Key.Enter });
        var controls = (Grid)view.Children.Last();
        var right = (FrameworkElement)((StackPanel)controls.Children[0]).Children[1];
        var jump = (FrameworkElement)((StackPanel)controls.Children[1]).Children[1];
        right.OnPointerPressed(Touch(10, default)); jump.OnPointerPressed(Touch(20, default));
        Assert.Equal(1, view.Surface.Input.Move); Assert.True(view.Surface.Input.JumpHeld);
        jump.OnPointerReleased(Touch(10, default));
        Assert.True(view.Surface.Input.JumpHeld);
        right.OnPointerReleased(Touch(10, default));
        Assert.Equal(0, view.Surface.Input.Move); Assert.True(view.Surface.Input.JumpHeld);
        view.ApplyTouchOptions(12);
        Assert.Equal(default, view.Surface.Input);
        Assert.Equal(TouchLayout.FloatingStick, view.ControlLayout);
        view.ApplyTouchOptions(3); Assert.Equal(12, view.TouchOptions);
        view.ApplyTouchOptions(9); Assert.Equal(9, view.TouchOptions);
    }

    [Theory]
    [InlineData(12)] [InlineData(13)]
    public void PhoneRoutedDragMovesThumbAndPlayerWhileAnotherFingerJumps(int options)
    {
        var previous = InputSystem.Current;
        var view = new GameView(touchOptions: options);
        InputSystem.Current = InputSystem.CreateExternalState(view);
        try
        {
            view.SetSafeArea(new Thickness(59, 0, 59, 21));
            view.OnKeyDown(new() { Key = Key.Enter });
            void Layout() { view.Measure(new(932, 430)); view.Arrange(new Rect(0, 0, 932, 430)); }
            Layout();
            var controls = (Grid)view.Children.Last();
            var stick = (TouchStick)controls.Children[2];
            var thumb = (Border)stick.Children[1];
            var jump = (FrameworkElement)((StackPanel)controls.Children[1]).Children[1];
            Vector2 Screen(FrameworkElement element, Vector2 point) =>
                Vector2.Transform(point, element.GetGlobalCoordinateTransformMatrix());
            var start = Screen(stick, new(76, 80));
            Assert.Same(stick, InputSystem.HitTest(start));
            void Send(PointerInputKind kind, uint id, Vector2 point) => InputSystem.InjectPointer(new(
                kind, id, PointerDeviceType.Touch, point, 1_000_000,
                IsInContact: kind is PointerInputKind.Pressed or PointerInputKind.Moved));
            Send(PointerInputKind.Pressed, 10, start);
            Layout();
            var restingThumb = thumb.Offset;
            Send(PointerInputKind.Moved, 10, start + new Vector2(48, 18));
            Layout();
            Assert.Equal(1, view.Surface.Input.Move);
            Assert.True(thumb.Offset.X > restingThumb.X + 30, "The rendered thumb must follow the drag.");
            Assert.True(thumb.Offset.Y > restingThumb.Y + 10, "Visual feedback follows vertical finger motion too.");
            float playerX = view.Surface.Session.Position.X;
            for (int tick = 0; tick < 24; tick++) view.UpdateAnimations(GameSession.StepSeconds);
            Assert.True(view.Surface.Session.Position.X > playerX + 10);
            var jumpPoint = Screen(jump, jump.Size / 2);
            Send(PointerInputKind.Pressed, 20, jumpPoint);
            Assert.True(view.Surface.Input.JumpHeld);
            Assert.Equal(1, view.Surface.Input.Move);
            // Captured motion continues beyond the joystick's hit rectangle.
            Send(PointerInputKind.Moved, 10, new(600, 120));
            Assert.Equal(1, view.Surface.Input.Move);
            Send(PointerInputKind.Released, 20, jumpPoint);
            Assert.False(view.Surface.Input.JumpHeld);
            Send(PointerInputKind.Canceled, 10, start);
            Layout();
            Assert.Equal(0, view.Surface.Input.Move);
            Assert.Equal(((Border)stick.Children[0]).Offset + new Vector2(32), thumb.Offset);
        }
        finally { InputSystem.Current = previous; }
    }

    private static PointerRoutedEventArgs Touch(uint id, Vector2 point) => new()
    {
        Pointer = new Pointer(id, Windows.Devices.Input.PointerDeviceType.Touch, true),
        ScreenPosition = point, Position = point
    };
}

using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.Voxel;
using ProGPU.Voxel.WinUI;
using ProGPU.WinUI.Input;
using Xunit;

namespace ProGPU.Tests;

public sealed class RelativePointerCaptureTests
{
    [Fact]
    public void VoxelGameUsesRelativeMouseUntilEscapeReleasesCapture()
    {
        var previousState = InputSystem.Current;
        var game = new VoxelGameView();
        var state = InputSystem.CreateExternalState(game);
        var acquireCount = 0;
        var releaseCount = 0;
        var stateChangeCount = 0;
        try
        {
            InputSystem.Current = state;
            RelativePointerCapture.ConfigureHost(
                state,
                () =>
                {
                    acquireCount++;
                    return true;
                },
                () => releaseCount++);
            game.MouseLookActiveChanged += (_, _) => stateChangeCount++;

            game.OnPointerPressed(new PointerRoutedEventArgs
            {
                IsLeftButtonPressed = true
            });

            Assert.True(game.IsMouseLookActive);
            Assert.True(game.IsFocused);
            Assert.Equal(1, acquireCount);
            Assert.Equal(1, stateChangeCount);

            var initialYaw = game.Player.Yaw;
            var initialPitch = game.Player.Pitch;
            RelativePointerCapture.InjectHostMovement(new Vector2(20f, -10f));
            Assert.Equal(initialYaw - 20f * game.MouseSensitivity, game.Player.Yaw, 5);
            Assert.Equal(initialPitch + 10f * game.MouseSensitivity, game.Player.Pitch, 5);

            game.OnPointerWheelChanged(new PointerRoutedEventArgs { WheelDelta = 1f });
            Assert.Equal(VoxelBlock.Water, game.SelectedBlock);
            game.OnPointerWheelChanged(new PointerRoutedEventArgs { WheelDelta = -1f });
            Assert.Equal(VoxelBlock.Grass, game.SelectedBlock);

            game.OnKeyDown(new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.Escape });
            Assert.False(game.IsMouseLookActive);
            Assert.Equal(1, releaseCount);
            Assert.Equal(2, stateChangeCount);
        }
        finally
        {
            RelativePointerCapture.ClearHost(state);
            InputSystem.Current = previousState;
        }
    }

    [Fact]
    public void FocusLossReleasesRelativePointerAndStopsMovement()
    {
        var previousState = InputSystem.Current;
        var owner = new Microsoft.UI.Xaml.Controls.Border();
        var state = InputSystem.CreateExternalState(owner);
        var movement = Vector2.Zero;
        var releaseCount = 0;
        try
        {
            InputSystem.Current = state;
            RelativePointerCapture.ConfigureHost(state, () => true, () => releaseCount++);
            Assert.True(RelativePointerCapture.TryAcquire(owner, delta => movement += delta));

            RelativePointerCapture.InjectHostMovement(new Vector2(3f, 4f));
            Assert.Equal(new Vector2(3f, 4f), movement);

            InputSystem.InjectFocusLost();
            Assert.False(RelativePointerCapture.IsCaptured(owner));
            Assert.Equal(1, releaseCount);

            RelativePointerCapture.InjectHostMovement(new Vector2(10f, 10f));
            Assert.Equal(new Vector2(3f, 4f), movement);
        }
        finally
        {
            RelativePointerCapture.ClearHost(state);
            InputSystem.Current = previousState;
        }
    }

    [Fact]
    public void HostInitiatedLossDoesNotRequestSecondPlatformRelease()
    {
        var previousState = InputSystem.Current;
        var owner = new Microsoft.UI.Xaml.Controls.Border();
        var state = InputSystem.CreateExternalState(owner);
        var releaseCount = 0;
        try
        {
            InputSystem.Current = state;
            RelativePointerCapture.ConfigureHost(state, () => true, () => releaseCount++);
            Assert.True(RelativePointerCapture.TryAcquire(owner, _ => { }));

            RelativePointerCapture.NotifyHostCaptureLost();

            Assert.False(RelativePointerCapture.IsCaptured(owner));
            Assert.Equal(0, releaseCount);
        }
        finally
        {
            RelativePointerCapture.ClearHost(state);
            InputSystem.Current = previousState;
        }
    }
}

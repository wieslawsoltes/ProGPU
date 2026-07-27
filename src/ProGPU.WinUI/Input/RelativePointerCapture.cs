using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Silk.NET.Input;

namespace ProGPU.WinUI.Input;

/// <summary>
/// ProGPU host extension for first-person relative mouse input. This deliberately
/// lives outside Microsoft.UI.Xaml so the projected WinUI API surface stays compatible.
/// </summary>
public static class RelativePointerCapture
{
    public static bool TryAcquire(
        FrameworkElement owner,
        Action<Vector2> movement,
        Action<bool>? captureChanged = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(movement);
        var windowState = InputSystem.Current;
        var state = windowState.RelativePointerCapture;
        if (ReferenceEquals(state.Owner, owner))
        {
            state.Movement = movement;
            state.CaptureChanged = captureChanged;
            return true;
        }
        if (state.Owner != null) ReleaseCurrent();

        var acquired = false;
        if (state.HostAcquire != null)
        {
            try
            {
                acquired = state.HostAcquire();
            }
            catch
            {
                acquired = false;
            }
        }
        else if (windowState.InputContext != null)
        {
            foreach (var mouse in windowState.InputContext.Mice)
            {
                try
                {
                    var cursor = mouse.Cursor;
                    var mode = cursor.IsSupported(CursorMode.Raw)
                        ? CursorMode.Raw
                        : cursor.IsSupported(CursorMode.Disabled)
                            ? CursorMode.Disabled
                            : (CursorMode?)null;
                    if (!mode.HasValue) continue;
                    state.CursorModes[mouse] = cursor.CursorMode;
                    cursor.CursorMode = mode.Value;
                    acquired = true;
                }
                catch
                {
                    state.CursorModes.Remove(mouse);
                }
            }
        }

        if (!acquired) return false;
        state.Owner = owner;
        state.Movement = movement;
        state.CaptureChanged = captureChanged;
        state.HasLastPlatformPosition = false;
        captureChanged?.Invoke(true);
        owner.Invalidate();
        return true;
    }

    public static bool IsCaptured(FrameworkElement owner) =>
        ReferenceEquals(InputSystem.Current.RelativePointerCapture.Owner, owner);

    public static void Release(FrameworkElement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (IsCaptured(owner)) ReleaseCurrent();
    }

    public static void ConfigureHost(
        WindowInputState windowState,
        Func<bool> acquire,
        Action release)
    {
        ArgumentNullException.ThrowIfNull(windowState);
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(release);
        windowState.RelativePointerCapture.HostAcquire = acquire;
        windowState.RelativePointerCapture.HostRelease = release;
    }

    public static void ClearHost(WindowInputState windowState)
    {
        ArgumentNullException.ThrowIfNull(windowState);
        var previous = InputSystem.Current;
        try
        {
            InputSystem.Current = windowState;
            ReleaseCurrent();
        }
        finally
        {
            InputSystem.Current = previous;
        }
        windowState.RelativePointerCapture.HostAcquire = null;
        windowState.RelativePointerCapture.HostRelease = null;
    }

    public static void InjectHostMovement(Vector2 movement)
    {
        if (!float.IsFinite(movement.X) || !float.IsFinite(movement.Y)) return;
        InputSystem.Current.RelativePointerCapture.Movement?.Invoke(movement);
    }

    public static void NotifyHostCaptureLost()
    {
        var state = InputSystem.Current.RelativePointerCapture;
        var owner = state.Owner;
        if (owner == null) return;
        var captureChanged = state.CaptureChanged;
        ClearLogicalCapture(state);
        RestoreCursorModes(state);
        captureChanged?.Invoke(false);
        owner.Invalidate();
    }

    internal static bool ProcessPlatformMouseMove(WindowInputState windowState, Vector2 position)
    {
        var state = windowState.RelativePointerCapture;
        if (state.Owner == null) return false;
        if (state.HasLastPlatformPosition)
        {
            state.Movement?.Invoke(position - state.LastPlatformPosition);
        }
        state.LastPlatformPosition = position;
        state.HasLastPlatformPosition = true;
        return true;
    }

    internal static void ReleaseCurrent()
    {
        var state = InputSystem.Current.RelativePointerCapture;
        var owner = state.Owner;
        if (owner == null) return;
        var captureChanged = state.CaptureChanged;
        ClearLogicalCapture(state);
        if (state.HostRelease != null)
        {
            try
            {
                state.HostRelease();
            }
            catch
            {
                // The host may already have released the capture.
            }
        }
        RestoreCursorModes(state);
        captureChanged?.Invoke(false);
        owner.Invalidate();
    }

    private static void ClearLogicalCapture(RelativePointerCaptureState state)
    {
        state.Owner = null;
        state.Movement = null;
        state.CaptureChanged = null;
        state.HasLastPlatformPosition = false;
    }

    private static void RestoreCursorModes(RelativePointerCaptureState state)
    {
        foreach (var pair in state.CursorModes)
        {
            try
            {
                pair.Key.Cursor.CursorMode = pair.Value;
            }
            catch
            {
                // The mouse may have been disconnected with the window unfocused.
            }
        }
        state.CursorModes.Clear();
    }
}

internal sealed class RelativePointerCaptureState
{
    public FrameworkElement? Owner;
    public Action<Vector2>? Movement;
    public Action<bool>? CaptureChanged;
    public Func<bool>? HostAcquire;
    public Action? HostRelease;
    public Dictionary<IMouse, CursorMode> CursorModes { get; } = new();
    public Vector2 LastPlatformPosition;
    public bool HasLastPlatformPosition;
}

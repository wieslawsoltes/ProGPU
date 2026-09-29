using Silk.NET.Input;
using Silk.NET.Windowing;

namespace ProGPU.Backend;

/// <summary>Creates input using the actual window provider, never a native-handle guess.</summary>
public static class NativeWindowInput
{
    /// <summary>
    /// Sets mouse pass-through on the actual owned provider. This is independent
    /// of enabled state: enabling an input-transparent popup must not admit input.
    /// Requires an initialized live window; rejection requires caller cleanup.
    /// </summary>
    public static unsafe void SetInputTransparent(IWindow window, bool transparent)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window is CocoaPopupWindow popup)
        {
            if (!popup.SetInputTransparent(transparent))
                throw new PlatformNotSupportedException("The owned popup rejected mouse pass-through.");
            return;
        }

        if (!window.IsInitialized || window.IsClosing)
            throw new InvalidOperationException("Mouse pass-through requires a live initialized window.");
        var native = (Silk.NET.GLFW.WindowHandle*)(window.Native?.Glfw ?? IntPtr.Zero);
        if (native == null)
            throw new PlatformNotSupportedException("The window provider has no mouse pass-through contract.");
        try
        {
            // GLFW 3.4's public attribute is absent from Silk's generated enum.
            const int mousePassthrough = 0x0002000D;
            var glfw = Silk.NET.GLFW.GlfwProvider.GLFW.Value;
            glfw.SetWindowAttrib(native, (Silk.NET.GLFW.WindowAttributeSetter)mousePassthrough, transparent);
            if (glfw.GetWindowAttrib(native, (Silk.NET.GLFW.WindowAttributeGetter)mousePassthrough) != transparent)
                throw new PlatformNotSupportedException("The native host rejected mouse pass-through.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or Silk.NET.GLFW.GlfwException or TypeInitializationException)
        {
            throw new PlatformNotSupportedException("The active windowing backend does not provide mouse pass-through.", exception);
        }
    }

    /// <summary>
    /// An owned popup has one live input context. Dispose it before replacing it.
    /// Other window providers retain Silk's existing input selection.
    /// </summary>
    public static IInputContext CreateInput(IView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view is CocoaPopupWindow popup ? popup.CreateInput() : InputWindowExtensions.CreateInput(view);
    }
}

public enum NativePointerEventKind { Move, Drag, Down, Up, Enter, Leave, Scroll, Cancel }
public enum NativePointerScrollUnit { Lines, Points }
public enum NativePointerScrollProtocol { Unspecified, AppKit }
[Flags]
public enum NativePointerModifiers
{
    None = 0, Shift = 1, Control = 2, Alt = 4, Super = 8,
    CapsLock = 16, NumericPad = 32, Help = 64, Function = 128
}

/// <summary>
/// Native view-point input, independent of framebuffer DPI. Buttons use native
/// zero-based left/right/middle/extra order; non-button events use -1. Scroll
/// direction is already resolved by the OS. Phase fields retain native values.
/// Policy cancellation retains the last observed position/time, not a fake up.
/// </summary>
public readonly record struct NativePointerEvent(
    NativePointerEventKind Kind, double X, double Y, double Timestamp,
    int Button, int ClickCount, NativePointerModifiers Modifiers,
    double ScrollX = 0, double ScrollY = 0,
    NativePointerScrollUnit ScrollUnit = NativePointerScrollUnit.Lines,
    uint ScrollPhase = 0, uint MomentumPhase = 0)
{
    // Non-positional to retain the original constructor and deconstruction ABI.
    // Raw phase values alone do not establish their platform semantics.
    public NativePointerScrollProtocol ScrollProtocol { get; init; }
}

/// <summary>
/// Optional lossless pointer transport beside Silk's float mouse events. Source
/// adapters subscribe to one stream, not both. CurrentEvent is scoped to delivery;
/// modifiers must not be substituted with later global keyboard polling. Handle
/// Cancel by releasing source pressed/capture state without activating a click.
/// Silk scroll values retain the event's native units too: never interpret precise
/// points as wheel lines or multiply them unconditionally by a wheel-notch constant.
/// </summary>
public interface INativePointerInputContext
{
    NativePointerEvent? CurrentEvent { get; }
    ulong InputGeneration { get; }
    event Action<NativePointerEvent>? PointerEvent;
}

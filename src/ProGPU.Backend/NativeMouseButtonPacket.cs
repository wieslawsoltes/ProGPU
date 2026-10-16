namespace ProGPU.Backend;

/// <summary>
/// The position carried by an original native client mouse-button message.
/// Coordinates are signed client desktop units, independent of framebuffer DPI.
/// Kind is Down or Up; Button uses zero-based left/right/middle/extra order.
/// This is available only during synchronous delivery of that message, not a
/// cursor query, cached movement, complete pointer stream or synthetic release.
/// </summary>
public readonly record struct NativeMouseButtonPacket(
    NativePointerEventKind Kind, int Button, NativeWindowPoint Position);

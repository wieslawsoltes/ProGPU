namespace ProGPU.Wpf.Interop;

public enum PortablePointerEventKind { Move, Drag, Down, Up, Enter, Leave, Scroll, Cancel }
public enum PortablePointerScrollUnit { Lines, Points }
public enum PortablePointerScrollProtocol { Unspecified, AppKit }

[Flags]
public enum PortablePointerModifiers
{
    None = 0, Shift = 1, Control = 2, Alt = 4, Super = 8,
    CapsLock = 16, NumericPad = 32, Help = 64, Function = 128
}

/// <summary>
/// Immutable native pointer input. Coordinates and point scroll deltas share the
/// receiving source's client frame; line deltas are not scaled. Timestamp is the
/// original monotonic native time in seconds, not the later dispatch time.
/// Buttons retain native zero-based left/right/middle/extra order, or -1 when absent.
/// Phase fields retain native flags; providers must explicitly admit their semantics.
/// </summary>
public sealed class PortablePointerInput
{
    public PortablePointerInput(PortablePointerEventKind kind, double x, double y,
        double timestamp, int button, int clickCount, PortablePointerModifiers modifiers,
        double scrollX = 0, double scrollY = 0,
        PortablePointerScrollUnit scrollUnit = PortablePointerScrollUnit.Lines,
        uint scrollPhase = 0, uint momentumPhase = 0)
        : this(kind, PortablePointerScrollProtocol.Unspecified, x, y, timestamp, button, clickCount,
            modifiers, scrollX, scrollY, scrollUnit, scrollPhase, momentumPhase)
    {
    }

    public PortablePointerInput(PortablePointerEventKind kind, PortablePointerScrollProtocol scrollProtocol,
        double x, double y, double timestamp, int button, int clickCount, PortablePointerModifiers modifiers,
        double scrollX = 0, double scrollY = 0,
        PortablePointerScrollUnit scrollUnit = PortablePointerScrollUnit.Lines,
        uint scrollPhase = 0, uint momentumPhase = 0)
    {
        if (kind is < PortablePointerEventKind.Move or > PortablePointerEventKind.Cancel)
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentException("Pointer coordinates must be finite.");
        if (!double.IsFinite(timestamp) || timestamp < 0)
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (clickCount < 0) throw new ArgumentOutOfRangeException(nameof(clickCount));
        if (((int)modifiers & ~255) != 0) throw new ArgumentOutOfRangeException(nameof(modifiers));
        bool buttonEvent = kind is PortablePointerEventKind.Down or PortablePointerEventKind.Up or PortablePointerEventKind.Drag;
        if (buttonEvent ? button is < 0 or >= 64 : button != -1 || clickCount != 0)
            throw new ArgumentException("Pointer button identity does not match the event kind.");
        if (!double.IsFinite(scrollX) || !double.IsFinite(scrollY))
            throw new ArgumentException("Scroll deltas must be finite.");
        if (scrollUnit is not (PortablePointerScrollUnit.Lines or PortablePointerScrollUnit.Points))
            throw new ArgumentOutOfRangeException(nameof(scrollUnit));
        if (scrollProtocol is not (PortablePointerScrollProtocol.Unspecified or PortablePointerScrollProtocol.AppKit))
            throw new ArgumentOutOfRangeException(nameof(scrollProtocol));
        if (kind != PortablePointerEventKind.Scroll &&
            (scrollX != 0 || scrollY != 0 || scrollUnit != PortablePointerScrollUnit.Lines || scrollPhase != 0 || momentumPhase != 0 ||
             scrollProtocol != PortablePointerScrollProtocol.Unspecified))
            throw new ArgumentException("Only scroll events can carry scroll metadata.");

        Kind = kind; X = x; Y = y; Timestamp = timestamp;
        Button = button; ClickCount = clickCount; Modifiers = modifiers;
        ScrollX = scrollX; ScrollY = scrollY; ScrollUnit = scrollUnit;
        ScrollPhase = scrollPhase; MomentumPhase = momentumPhase;
        ScrollProtocol = scrollProtocol;
    }

    public PortablePointerEventKind Kind { get; }
    public double X { get; }
    public double Y { get; }
    public double Timestamp { get; }
    public int Button { get; }
    public int ClickCount { get; }
    public PortablePointerModifiers Modifiers { get; }
    public double ScrollX { get; }
    public double ScrollY { get; }
    public PortablePointerScrollUnit ScrollUnit { get; }
    public uint ScrollPhase { get; }
    public uint MomentumPhase { get; }
    public PortablePointerScrollProtocol ScrollProtocol { get; }

    public PortablePointerInput WithCoordinates(double x, double y, double scrollX, double scrollY) =>
        new(Kind, ScrollProtocol, x, y, Timestamp, Button, ClickCount, Modifiers,
            scrollX, scrollY, ScrollUnit, ScrollPhase, MomentumPhase);
}

/// <summary>
/// Optional source capability, separate from legacy wheel/key transport. Hosts
/// must fail explicitly if absent, never drop metadata into a legacy event.
/// shortcutModifiers uses the existing portable Shift/Control/Alt/Super bits
/// after host shortcut policy; input.Modifiers retains the original native flags.
/// A true return admits delivery; handled is the source's independent result.
/// </summary>
public interface IPortableNativePointerInputService
{
    bool TryProcessNativePointerInputEvent(object window, PortablePointerInput input,
        int shortcutModifiers, out bool handled);

    bool TryProcessPresentationSourceNativePointerInputEvent(object presentationSource,
        PortablePointerInput input, int shortcutModifiers, out bool handled);
}

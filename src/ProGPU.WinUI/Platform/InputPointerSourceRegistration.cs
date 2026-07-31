using Microsoft.UI.Input;

namespace ProGPU.WinUI.Platform;

public enum InputPointerSourceEventKind
{
    CaptureLost,
    Entered,
    Exited,
    Moved,
    Pressed,
    Released,
    RoutedAway,
    RoutedReleased,
    RoutedTo,
    WheelChanged
}

public static class InputPointerSourceRegistration
{
    public static bool Raise(
        InputPointerSource source,
        InputPointerSourceEventKind kind,
        PointerEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(args);
        return source.RaiseExternal(kind, args);
    }
}

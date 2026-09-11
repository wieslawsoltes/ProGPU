using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal enum CocoaMenuCommand { None, Minimize, Zoom, Close }

[Flags]
internal enum CocoaMenuActions { None = 0, Minimize = 1, Zoom = 2, Close = 4 }

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CocoaMenuPoint(double X, double Y);

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CocoaMenuRect(double X, double Y, double Width, double Height);

internal readonly record struct CocoaMenuOwner(nint Window, nint View, nint Delegate);

internal struct CocoaMenuSelection
{
    internal nint Target, MinimizeItem, ZoomItem, CloseItem;
    internal CocoaMenuCommand Command;

    // The reverse native action records identity only: no native calls, allocation,
    // managed callbacks or exceptions can escape into AppKit's tracking loop.
    internal void Select(nint target, nint item)
    {
        if (target == 0 || target != Target || item == 0 || Command != CocoaMenuCommand.None) return;
        if (item == MinimizeItem) Command = CocoaMenuCommand.Minimize;
        else if (item == ZoomItem) Command = CocoaMenuCommand.Zoom;
        else if (item == CloseItem) Command = CocoaMenuCommand.Close;
    }
}

internal static unsafe class CocoaMenuSelectionContext
{
    [ThreadStatic] private static nint t_selection;

    // Caller-owned stack storage remains live throughout synchronous tracking.
    internal static Scope Enter(CocoaMenuSelection* selection) => new((nint)selection);

    internal static void Record(nint target, nint item)
    {
        var selection = (CocoaMenuSelection*)t_selection;
        if (selection != null) selection->Select(target, item);
    }

    internal ref struct Scope
    {
        private readonly nint _previous;
        private bool _disposed;
        internal Scope(nint selection)
        { _previous = t_selection; _disposed = false; t_selection = selection; }
        public void Dispose()
        {
            if (_disposed) return;
            t_selection = _previous;
            _disposed = true;
        }
    }
}

internal interface ICocoaSystemMenuOperations
{
    bool TryRetainOwner(nint window, out CocoaMenuOwner owner, out CocoaMenuActions actions);
    bool TryGetPrimaryScreen(out CocoaMenuRect frame);
    bool TryTrack(in CocoaMenuOwner owner, CocoaMenuActions actions, CocoaMenuPoint point, out CocoaMenuCommand command);
    bool TryRevalidateOwner(in CocoaMenuOwner owner, out CocoaMenuActions actions);
    void Perform(nint window, CocoaMenuCommand command);
    void ReleaseOwner(in CocoaMenuOwner owner);
}

internal static class CocoaSystemMenu
{
    internal static bool TryMapDesktopPoint(NativeWindowPoint point, CocoaMenuRect primary, out CocoaMenuPoint native)
    {
        native = default;
        if (!double.IsFinite(primary.X) || !double.IsFinite(primary.Y) ||
            !double.IsFinite(primary.Width) || !double.IsFinite(primary.Height) ||
            primary.Width <= 0 || primary.Height <= 0) return false;
        double x = primary.X + point.X, y = primary.Y + primary.Height - point.Y;
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        native = new(x, y);
        return true;
    }

    internal static bool IsAllowed(CocoaMenuActions actions, CocoaMenuCommand command) => command switch
    {
        CocoaMenuCommand.Minimize => (actions & CocoaMenuActions.Minimize) != 0,
        CocoaMenuCommand.Zoom => (actions & CocoaMenuActions.Zoom) != 0,
        CocoaMenuCommand.Close => (actions & CocoaMenuActions.Close) != 0,
        _ => false
    };

    internal static bool Show<T>(nint window, NativeWindowPoint point, ref T api)
        where T : ICocoaSystemMenuOperations
    {
        if (window == 0 || !api.TryRetainOwner(window, out var owner, out var initialActions)) return false;
        try
        {
            if (initialActions == CocoaMenuActions.None ||
                !api.TryGetPrimaryScreen(out var primary) || !TryMapDesktopPoint(point, primary, out var native) ||
                !api.TryTrack(in owner, initialActions, native, out var command)) return false;
            // AppKit's false result includes cancellation; no selected action is
            // dispatched. It does not by itself prove that menu pixels appeared.
            if (command == CocoaMenuCommand.None) return true;
            if (!IsAllowed(initialActions, command) || !api.TryRevalidateOwner(in owner, out var currentActions) ||
                !IsAllowed(currentActions, command)) return false;
            api.Perform(owner.Window, command);
            return true;
        }
        finally { api.ReleaseOwner(in owner); }
    }
}

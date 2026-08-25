using Avalonia.Controls;
using Silk.NET.Maths;

namespace Avalonia.SilkNet;

/// <summary>
/// Correlates a native size callback with the Avalonia resize request that
/// caused it. GLFW doesn't include a reason in its callback contract.
/// </summary>
internal struct SilkNetResizeReasonTracker
{
    private Vector2D<int>? _expectedSize;
    private WindowResizeReason _reason;

    internal void Begin(
        Vector2D<int> expectedSize,
        WindowResizeReason reason)
    {
        _expectedSize = expectedSize;
        _reason = reason;
    }

    internal WindowResizeReason Resolve(Vector2D<int> actualSize)
    {
        if (_expectedSize is not { } expectedSize)
            return WindowResizeReason.User;

        _expectedSize = null;
        return expectedSize == actualSize
            ? _reason
            : WindowResizeReason.User;
    }

    internal void Cancel() => _expectedSize = null;
}

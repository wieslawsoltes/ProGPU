using Silk.NET.Windowing;

namespace ProGPU.Backend;

/// <summary>Provider-aware native window retirement for source event loops.</summary>
public static class NativeWindowLifetime
{
    /// <summary>
    /// Requests disposal without polling native events. Returns false while an
    /// owned Cocoa popup still retains its native panel/view through an active
    /// callback, initialization or render lease. The host must retain the window
    /// and retry on its creating thread after native polling and GPU-surface
    /// retirement, including when disposal throws. Do not spin or unregister the
    /// retirement owner until this returns true. Other providers retain their
    /// existing Dispose semantics and are never probed through native handles.
    /// </summary>
    public static bool TryDispose(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispose();
        return window is not CocoaPopupWindow popup || popup.IsReleased;
    }
}

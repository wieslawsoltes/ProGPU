namespace ProGPU.Backend;

/// <summary>Platform system-menu presentation for an existing native window.</summary>
public static class NativeWindowSystemMenu
{
    /// <summary>
    /// Presents the owner window's system menu at native desktop coordinates.
    /// The caller must keep its native window/display live on the window thread.
    /// Win32 uses a modal loop which may reenter callbacks, then posts selection.
    /// Cocoa offers AppKit Minimize/Zoom/Close actions on the main thread, with
    /// native owner/capability revalidation after tracking. Coordinates use the
    /// top-left desktop convention, independent of framebuffer backing pixels.
    /// X11 requires a managed client and advertised window-manager support; true
    /// means an asynchronous request was submitted, not that the menu was shown.
    /// Unsupported kinds and rejected native operations return false. The caller
    /// keeps ownership of its window. Temporary menu resources and retained owner
    /// references are scoped to this call and are never transferred to callers.
    /// </summary>
    public static bool TryShow(NativeWindowHandle owner, NativeWindowPoint desktopPosition)
    {
        if (!owner.IsValid) return false;
        if (OperatingSystem.IsWindows() && owner.Kind == NativeWindowKind.Win32)
            return Win32NativeWindowPlatform.TryShowSystemMenu(owner.Handle, desktopPosition);
        if (OperatingSystem.IsLinux() && owner.Kind == NativeWindowKind.X11)
            return X11NativeSystemMenu.TryShow(owner, desktopPosition);
        if (OperatingSystem.IsMacOS() && owner.Kind == NativeWindowKind.Cocoa)
            return CocoaNativeSystemMenu.TryShow(owner.Handle, desktopPosition);
        return false;
    }
}

namespace ProGPU.Backend;

/// <summary>Platform system-menu presentation for an existing native window.</summary>
public static class NativeWindowSystemMenu
{
    /// <summary>
    /// Presents the owner window's system menu at native desktop coordinates.
    /// The caller must keep its native window/display live on the window thread.
    /// Win32 uses a modal loop which may reenter callbacks, then posts selection.
    /// X11 requires a managed client and advertised window-manager support; true
    /// means an asynchronous request was submitted, not that the menu was shown.
    /// Unsupported kinds and rejected native operations return false. No window
    /// or menu handle is retained, destroyed, replaced or transferred to callers.
    /// </summary>
    public static bool TryShow(NativeWindowHandle owner, NativeWindowPoint desktopPosition)
    {
        if (!owner.IsValid) return false;
        if (OperatingSystem.IsWindows() && owner.Kind == NativeWindowKind.Win32)
            return Win32NativeWindowPlatform.TryShowSystemMenu(owner.Handle, desktopPosition);
        if (OperatingSystem.IsLinux() && owner.Kind == NativeWindowKind.X11)
            return X11NativeSystemMenu.TryShow(owner, desktopPosition);
        return false;
    }
}

namespace ProGPU.Backend;

/// <summary>Platform system-menu presentation for an existing native window.</summary>
public static class NativeWindowSystemMenu
{
    /// <summary>
    /// Displays the Win32 owner window's existing system menu at native desktop
    /// coordinates. Must run on the window thread. The modal native menu loop
    /// may reenter application callbacks; selected commands are posted afterward.
    /// Unsupported kinds and rejected native operations return false. No window
    /// or menu handle is retained, destroyed, replaced or transferred to callers.
    /// </summary>
    public static bool TryShow(NativeWindowHandle owner, NativeWindowPoint desktopPosition)
    {
        if (!OperatingSystem.IsWindows() || owner.Kind != NativeWindowKind.Win32 || !owner.IsValid)
            return false;
        return Win32NativeWindowPlatform.TryShowSystemMenu(owner.Handle, desktopPosition);
    }
}

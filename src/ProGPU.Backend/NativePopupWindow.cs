namespace ProGPU.Backend;

/// <summary>Native ownership for an already-created, hidden popup surface.</summary>
public static class NativePopupWindow
{
    /// <summary>
    /// Configures same-thread Win32 top-level windows as owner and nonactivating
    /// popup. No managed handle ownership is retained. One native subclass lives
    /// until window destruction; this assembly must outlive the window.
    /// Failure leaves the popup hidden; the
    /// caller must destroy it rather than show a partially configured surface.
    /// Unsupported native window kinds return false without native calls.
    /// </summary>
    public static bool TryConfigureOwner(NativeWindowHandle owner, NativeWindowHandle popup)
    {
        if (!OperatingSystem.IsWindows() || owner.Kind != NativeWindowKind.Win32 ||
            popup.Kind != NativeWindowKind.Win32 || !owner.IsValid || !popup.IsValid ||
            owner.Handle == popup.Handle) return false;
        return Win32NativeWindowPlatform.TryConfigurePopupOwner(owner.Handle, popup.Handle);
    }
}

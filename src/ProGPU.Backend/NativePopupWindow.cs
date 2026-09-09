namespace ProGPU.Backend;

/// <summary>Native ownership for an already-created, hidden popup surface.</summary>
public static class NativePopupWindow
{
    /// <summary>
    /// Configures same-thread Win32 top-level windows as owner and nonactivating
    /// popup, or main-thread Cocoa host windows as parent and hidden child.
    /// Cocoa child ownership does not grant AppKit modal-session admission.
    /// No managed handle ownership is retained. On Win32 one native subclass
    /// lives until window destruction; this assembly must outlive the window.
    /// Failure leaves the popup hidden; the
    /// caller must destroy it rather than show a partially configured surface.
    /// Unsupported native window kinds return false without native calls.
    /// </summary>
    public static bool TryConfigureOwner(NativeWindowHandle owner, NativeWindowHandle popup)
    {
        if (owner.Kind != popup.Kind || !owner.IsValid || !popup.IsValid ||
            owner.Handle == popup.Handle) return false;
        if (OperatingSystem.IsWindows() && owner.Kind == NativeWindowKind.Win32)
            return Win32NativeWindowPlatform.TryConfigurePopupOwner(owner.Handle, popup.Handle);
        if (OperatingSystem.IsMacOS() && owner.Kind == NativeWindowKind.Cocoa)
            return CocoaNativePopupWindow.TryConfigureOwner(owner.Handle, popup.Handle);
        return false;
    }
}

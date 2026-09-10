namespace ProGPU.Backend;

/// <summary>Native ownership for an already-created, hidden popup surface.</summary>
public static class NativePopupWindow
{
    /// <summary>
    /// Configures same-thread Win32 top-level windows as owner and nonactivating
    /// popup, main-thread Cocoa host windows as parent and hidden child, or
    /// same-display X11 windows as transient owner and override-redirect popup.
    /// Cocoa child ownership does not grant AppKit modal-session admission.
    /// No managed handle ownership is retained. On Win32 one native subclass
    /// lives until window destruction; this assembly must outlive the window.
    /// Failure leaves the popup hidden; the
    /// caller must destroy it rather than show a partially configured surface.
    /// Unsupported native window kinds return false without native calls.
    /// X11 handles/display are borrowed live host identities, not arbitrary XIDs;
    /// the caller serializes this operation with destruction on the display's
    /// owning host thread. X11 success confirms hidden server-side owner, type
    /// and override-redirect state, not window-manager modality or presentation.
    /// </summary>
    public static bool TryConfigureOwner(NativeWindowHandle owner, NativeWindowHandle popup)
    {
        if (owner.Kind != popup.Kind || !owner.IsValid || !popup.IsValid ||
            owner.Handle == popup.Handle) return false;
        if (OperatingSystem.IsWindows() && owner.Kind == NativeWindowKind.Win32)
            return Win32NativeWindowPlatform.TryConfigurePopupOwner(owner.Handle, popup.Handle);
        if (OperatingSystem.IsMacOS() && owner.Kind == NativeWindowKind.Cocoa)
            return CocoaNativePopupWindow.TryConfigureOwner(owner.Handle, popup.Handle);
        if (OperatingSystem.IsLinux() && owner.Kind == NativeWindowKind.X11 &&
            owner.Display != 0 && owner.Display == popup.Display)
            return X11NativeWindowPlatform.TryConfigurePopupOwner(owner.Display,
                (nuint)owner.Handle, (nuint)popup.Handle);
        return false;
    }
}

namespace ProGPU.Backend;

/// <summary>Native ownership for an already-created, hidden popup surface.</summary>
public static class NativePopupWindow
{
    /// <summary>
    /// Configures same-thread Win32 top-level windows as owner and nonactivating
    /// popup, or
    /// same-display X11 windows as transient owner and override-redirect popup.
    /// Cocoa returns false because attachment shows the child; use
    /// TryPrepareOwner followed by TryShowOwned. Child ownership does not grant
    /// AppKit modal-session admission.
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
            owner.Handle == popup.Handle)
        {
            if (OperatingSystem.IsWindows() &&
                (owner.Kind == NativeWindowKind.Win32 || popup.Kind == NativeWindowKind.Win32))
                Win32NativeWindowPlatform.TracePopupOwnerRejection(
                    owner.Handle, popup.Handle,
                    Win32PopupConfigurationFailure.InvalidIdentity);
            return false;
        }
        if (OperatingSystem.IsWindows() && owner.Kind == NativeWindowKind.Win32)
            return Win32NativeWindowPlatform.TryConfigurePopupOwner(owner.Handle, popup.Handle);
        if (OperatingSystem.IsMacOS() && owner.Kind == NativeWindowKind.Cocoa)
            return false; // AppKit attachment orders the child in; use prepare/show.
        if (OperatingSystem.IsLinux() && owner.Kind == NativeWindowKind.X11 &&
            owner.Display != 0 && owner.Display == popup.Display)
            return X11NativeWindowPlatform.TryConfigurePopupOwner(owner.Display,
                (nuint)owner.Handle, (nuint)popup.Handle);
        return false;
    }

    /// <summary>Admits a hidden popup. Cocoa defers native attachment to ShowOwned;
    /// other platforms complete their hidden owner configuration here.</summary>
    public static bool TryPrepareOwner(NativeWindowHandle owner, NativeWindowHandle popup)
    {
        if (OperatingSystem.IsMacOS() && owner.Kind == NativeWindowKind.Cocoa &&
            popup.Kind == owner.Kind && owner.IsValid && popup.IsValid && owner.Handle != popup.Handle)
            return CocoaNativePopupWindow.TryConfigureOwner(owner.Handle, popup.Handle, prepareOnly: true);
        return TryConfigureOwner(owner, popup);
    }

    /// <summary>Shows an admitted popup through the host's nonactivating operation.
    /// Cocoa revalidates the live hidden host, attaches (which orders it in), then
    /// verifies ownership around the callback. False or an exception requires
    /// caller disposal; never show an unowned replacement. Call again after Hide.</summary>
    public static bool TryShowOwned(NativeWindowHandle owner, NativeWindowHandle popup, Action showWithoutActivation)
    {
        ArgumentNullException.ThrowIfNull(showWithoutActivation);
        if (OperatingSystem.IsMacOS() && owner.Kind == NativeWindowKind.Cocoa &&
            popup.Kind == owner.Kind && owner.IsValid && popup.IsValid && owner.Handle != popup.Handle)
            return CocoaNativePopupWindow.TryConfigureOwner(owner.Handle, popup.Handle, show: showWithoutActivation);
        if (!TryConfigureOwner(owner, popup)) return false;
        showWithoutActivation();
        return true;
    }
}

using Silk.NET.Windowing;

namespace ProGPU.Backend;

/// <summary>Native ownership for an already-created, hidden popup surface.</summary>
public static class NativePopupWindow
{
    /// <summary>
    /// Reports the session-owned pointer gate only for an actual live owned Cocoa
    /// popup and its exact attached input context, on the creating thread. This
    /// includes hidden ownerless preparation; Bind/Show retain their separate
    /// live-owner checks. It does not claim current input permission, renderer
    /// qualification, source scroll support or automatic source modal admission.
    /// A foreign provider or a detached/replaced context never supplies this proof.
    /// </summary>
    public static bool SupportsModalInput(IWindow popup, Silk.NET.Input.IInputContext input)
    {
        ArgumentNullException.ThrowIfNull(popup);
        ArgumentNullException.ThrowIfNull(input);
        return popup is CocoaPopupWindow owned && owned.SupportsModalInput(input);
    }

    /// <summary>
    /// Creates a source-scheduled Cocoa popup whose hidden panel and render view
    /// may exist before its owner is known. No native input or Show is admitted
    /// until a live owner is bound with TryBindCocoaOwner. The callback must wake
    /// the actual source dispatcher; this adapter does not poll native events.
    /// Options and render-view retirement requirements match CreateOwnedCocoaWindow.
    /// </summary>
    public static IWindow CreateCocoaPopupWindow(WindowOptions options, Action wakeHost)
    {
        ArgumentNullException.ThrowIfNull(wakeHost);
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Owned Cocoa popup windows require macOS.");
        return new CocoaPopupWindow(options, wakeHost, (bounds, transparent) =>
        {
            if (!CocoaOwnedPopupSurface.TryCreateUnbound(bounds, transparent, out var surface))
                throw new PlatformNotSupportedException("The hidden Cocoa popup could not be created.");
            return surface!;
        });
    }

    /// <summary>
    /// Binds or clears an initialized, hidden source-scheduled Cocoa popup's
    /// native owner without replacing its panel, view or rendering device. Empty
    /// ownership forbids Show/input. A rejected or throwing native setup requires
    /// the source host to dispose the rejected popup. The fixed-owner factory
    /// accepts only its original owner. Other window providers are not adopted.
    /// </summary>
    public static bool TryBindCocoaOwner(IWindow popup, NativeWindowHandle owner)
    {
        ArgumentNullException.ThrowIfNull(popup);
        return popup is CocoaPopupWindow window && window.BindOwner(owner);
    }

    /// <summary>
    /// Creates an explicit owned Cocoa popup adapter. Initialize creates its hidden
    /// native panel on the AppKit main thread; this method does not show or focus
    /// it. Options must be untitled, borderless, hidden, NoAPI and host-scheduled.
    /// Attach input through NativeWindowInput and preserve its native pointer
    /// units/cancellation. Use the owner's shared render device and dispose the GPU
    /// surface before the window. If disposal occurs during a callback, retain the
    /// adapter and drain DoEvents after native polling until IsInitialized is false.
    /// This does not enable automatic modal sessions or source-framework admission.
    /// </summary>
    public static IWindow CreateOwnedCocoaWindow(IWindow owner, WindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Owned Cocoa popup windows require macOS.");
        var ownerHandle = GlfwNativeWindowPlatform.ResolveWindowHandle(owner);
        if (!owner.IsInitialized || owner.IsClosing || ownerHandle.Kind != NativeWindowKind.Cocoa || !ownerHandle.IsValid)
            throw new InvalidOperationException("An initialized, live Cocoa owner is required.");
        return new CocoaPopupWindow(owner, ownerHandle, options, owner.ContinueEvents, (bounds, transparent) =>
        {
            if (!owner.IsInitialized || owner.IsClosing ||
                GlfwNativeWindowPlatform.ResolveWindowHandle(owner) != ownerHandle)
                throw new InvalidOperationException("The popup's source owner changed before native initialization.");
            if (!CocoaOwnedPopupSurface.TryCreate(ownerHandle, bounds, transparent, out var surface))
                throw new PlatformNotSupportedException("The owned Cocoa popup could not be created for this native owner.");
            return surface!;
        });
    }

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

    /// <summary>
    /// Prepares the actual popup provider while hidden. Owned Cocoa panels bind
    /// their retained source owner; other providers keep native-handle admission.
    /// A rejected or throwing setup requires caller disposal, never a fallback.
    /// </summary>
    public static bool TryPrepareOwner(NativeWindowHandle owner, IWindow popup)
    {
        ArgumentNullException.ThrowIfNull(popup);
        if (popup is CocoaPopupWindow owned)
            return owner.IsValid && !owned.IsVisible && owned.BindOwner(owner);
        return TryPrepareOwner(owner, GlfwNativeWindowPlatform.ResolveWindowHandle(popup));
    }

    /// <summary>
    /// Shows through the actual provider, retaining an owned panel across the
    /// source callback. The callback must call ShowWithoutActivation after any
    /// source renderer preparation. It must not change ownership or reenter Show.
    /// False or an exception requires caller disposal. No native events are polled.
    /// </summary>
    public static bool TryShowOwned(NativeWindowHandle owner, IWindow popup, Action showWithoutActivation)
    {
        ArgumentNullException.ThrowIfNull(popup);
        ArgumentNullException.ThrowIfNull(showWithoutActivation);
        return popup is CocoaPopupWindow owned
            ? owned.ShowOwned(owner, showWithoutActivation)
            : TryShowOwned(owner, GlfwNativeWindowPlatform.ResolveWindowHandle(popup), showWithoutActivation);
    }

    /// <summary>
    /// Uses the owned panel's checked visibility or a live GLFW provider's
    /// nonactivating visibility. Call only inside admitted TryShowOwned setup;
    /// this operation alone does not establish ordinary native popup ownership.
    /// </summary>
    public static unsafe void ShowWithoutActivation(IWindow popup)
    {
        ArgumentNullException.ThrowIfNull(popup);
        if (popup is CocoaPopupWindow owned)
        {
            owned.IsVisible = true;
            return;
        }

        if (!popup.IsInitialized || popup.IsClosing)
            throw new InvalidOperationException("Popup display requires a live initialized window.");
        var native = (Silk.NET.GLFW.WindowHandle*)(popup.Native?.Glfw ?? IntPtr.Zero);
        if (native == null)
            throw new PlatformNotSupportedException("The window provider has no nonactivating visibility contract.");
        var glfw = Silk.NET.GLFW.GlfwProvider.GLFW.Value;
        bool previous = glfw.GetWindowAttrib(native, Silk.NET.GLFW.WindowAttributeGetter.FocusOnShow);
        Exception? failure = null;
        try
        {
            glfw.SetWindowAttrib(native, Silk.NET.GLFW.WindowAttributeSetter.FocusOnShow, false);
            if (glfw.GetWindowAttrib(native, Silk.NET.GLFW.WindowAttributeGetter.FocusOnShow))
                throw new PlatformNotSupportedException("The native host rejected nonactivating visibility.");
            popup.IsVisible = true;
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            try
            {
                if (popup.IsInitialized && popup.Native?.Glfw == (nint)native)
                    glfw.SetWindowAttrib(native, Silk.NET.GLFW.WindowAttributeSetter.FocusOnShow, previous);
            }
            catch (Exception cleanup) when (failure is not null)
            {
                failure.Data["PopupVisibilityRestoration"] = cleanup;
            }
        }
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

using System.Runtime.ExceptionServices;

namespace ProGPU.Backend;

public sealed partial class NativeWindowModalSession
{
    // The window owns registration; this index must not keep a source host alive.
    // Only actual owned providers register, never handles classified as providers.
    [ThreadStatic] private static List<WeakReference<CocoaPopupWindow>>? s_popups;
    [ThreadStatic] private static ulong s_popupInputRevision;
    [ThreadStatic] private static ExceptionDispatchInfo? s_popupInputFailure;
    [ThreadStatic] private static bool s_popupWakePending;

    internal static bool IsInputPolicyTransitioning => s_transitioning;
    internal static bool HasHealthyPopupInputPolicy => s_popupInputFailure == null;

    internal static bool AllowsPopupInput(NativeWindowHandle owner) =>
        s_popupInputFailure == null && owner.Kind == NativeWindowKind.Cocoa && owner.IsValid &&
        (s_current == null || s_current._operations?.Window == owner.Handle);

    internal static void RegisterPopupInput(CocoaPopupWindow window)
    {
        (s_popups ??= []).Add(new(window));
        window.SynchronizeModalInputPolicy(s_popupInputRevision);
    }

    internal static void UnregisterPopupInput(CocoaPopupWindow window)
    {
        s_popups?.RemoveAll(reference => !reference.TryGetTarget(out var target) || ReferenceEquals(target, window));
    }

    private static void PublishPopupInputPolicy()
    {
        // Every stack transition has a distinct publication, even two frames for
        // the same window. Rollback derives admission from the restored stack,
        // not a saved boolean which could overwrite a new owner/context intent.
        s_popupInputRevision = checked(s_popupInputRevision + 1);
        if (s_popups == null) return;
        s_popupWakePending = true;
        var snapshot = s_popups.ToArray();
        Exception? failure = null;
        foreach (var reference in snapshot)
        {
            if (!reference.TryGetTarget(out var window)) continue;
            try { window.SynchronizeModalInputPolicy(s_popupInputRevision); }
            catch (Exception error)
            {
                failure ??= error;
                if (!ReferenceEquals(failure, error)) CocoaPopupFailure.AttachCleanup(failure, "ModalPopupInput", error);
            }
        }
        s_popups.RemoveAll(reference => !reference.TryGetTarget(out _));
        if (failure != null)
        {
            FailPopupInputPolicy(failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void FailPopupInputPolicy(Exception failure)
    {
        // End or identity retirement may already have changed AppKit state.
        // Losing the stack query is not evidence that input may resume. This
        // creating-thread fault is terminal; do not retry an uncertain token.
        s_popupInputFailure ??= ExceptionDispatchInfo.Capture(failure);
        s_popupInputRevision = checked(s_popupInputRevision + 1);
        if (s_popups == null) return;
        s_popupWakePending = true;
        foreach (var reference in s_popups.ToArray())
        {
            if (!reference.TryGetTarget(out var window)) continue;
            try { window.SynchronizeModalInputPolicy(s_popupInputRevision); }
            catch (Exception cleanup) { CocoaPopupFailure.AttachCleanup(failure, "ModalPopupInputBlocking", cleanup); }
        }
    }

    private static void WakePopupInputConsumers()
    {
        // Wake only after native transitions. A dispatcher may synchronously
        // drain or enter another session; each window consumes its pending bit
        // before the callback, so newer publications are never cleared here.
        if (s_transitioning || !s_popupWakePending || s_popups == null) return;
        s_popupWakePending = false;
        Exception? failure = null;
        foreach (var reference in s_popups.ToArray())
        {
            if (!reference.TryGetTarget(out var window)) continue;
            try { window.WakeModalInputConsumer(); }
            catch (Exception error)
            {
                failure ??= error;
                if (!ReferenceEquals(failure, error)) CocoaPopupFailure.AttachCleanup(failure, "ModalPopupInputWake", error);
            }
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

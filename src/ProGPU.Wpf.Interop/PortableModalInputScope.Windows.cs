namespace ProGPU.Wpf.Interop;

public sealed partial class PortableModalInputScope
{
    [ThreadStatic] private static List<WeakReference<WindowRegistration>>? s_windows;
    [ThreadStatic] private static bool s_publishing;
    [ThreadStatic] private static bool s_policyUncertain;

    /// <summary>Whether the last publication to registered native gates succeeded.</summary>
    public static bool IsNativeInputPolicySynchronized => !s_policyUncertain;

    /// <summary>
    /// Registers a native surface under its source-owned input identity. Popups
    /// use the identity of their actual owning source window. The callback applies
    /// an independent input gate, never an application IsEnabled assignment.
    /// New windows immediately receive the current policy, including during a dialog.
    /// Release on the registering thread; disposal publishes unrestricted admission.
    /// </summary>
    public static IDisposable RegisterWindow(object owningWindow, Action<bool> setInputAllowed)
    {
        ArgumentNullException.ThrowIfNull(owningWindow);
        ArgumentNullException.ThrowIfNull(setInputAllowed);
        var registration = new WindowRegistration(owningWindow, setInputAllowed);
        registration.Link = new(registration);
        var windows = s_windows ??= [];
        windows.RemoveAll(static entry => !entry.TryGetTarget(out _));
        windows.Add(registration.Link);
        try { registration.Publish(); }
        catch (Exception failure)
        {
            try { registration.Dispose(); }
            catch (Exception rollback) { throw new AggregateException(failure, rollback); }
            throw;
        }
        return registration;
    }

    private static void EnsureNotPublishing()
    {
        if (s_publishing)
            throw new InvalidOperationException("A modal scope cannot change during native input-policy publication.");
    }

    private static void PublishInputPolicy()
    {
        // Dialog boundaries, not event/frame hot paths. A bounded snapshot lets
        // callbacks create or dispose surfaces without invalidating traversal.
        s_policyUncertain = true;
        s_windows?.RemoveAll(static entry => !entry.TryGetTarget(out _));
        if (s_windows is not { Count: > 0 }) { s_policyUncertain = false; return; }
        var snapshot = s_windows.ToArray();
        List<Exception>? failures = null;
        s_publishing = true;
        try
        {
            foreach (var entry in snapshot)
            {
                if (!entry.TryGetTarget(out var window)) continue;
                try { window.Publish(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
        }
        finally { s_publishing = false; }
        if (failures != null) throw new AggregateException("Native modal input publication failed.", failures);
        s_policyUncertain = false;
    }

    private sealed class WindowRegistration(object window, Action<bool> callback) : IDisposable
    {
        private readonly int _threadId = Environment.CurrentManagedThreadId;
        private object? _window = window;
        private Action<bool>? _callback = callback;
        internal WeakReference<WindowRegistration>? Link;

        public void Publish()
        {
            if (_callback is { } callback) Invoke(callback, AllowsInput(_window));
        }

        private static void Invoke(Action<bool> callback, bool allowed)
        {
            bool publishing = s_publishing;
            s_publishing = true;
            try { callback(allowed); }
            finally { s_publishing = publishing; }
        }

        public void Dispose()
        {
            if (_threadId != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Native input registration must be released on its owning thread.");
            if (_callback == null) return;
            var callback = _callback;
            s_windows!.Remove(Link!);
            if (s_windows.Count == 0) s_windows = null;
            _callback = null;
            _window = null;
            Invoke(callback, true);
        }
    }
}

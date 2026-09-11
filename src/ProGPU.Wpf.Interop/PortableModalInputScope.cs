namespace ProGPU.Wpf.Interop;

/// <summary>
/// Source-owned, thread-bound input admission for nested portable dialogs.
/// Callers resolve a surface to its real owning window before checking it.
/// Registered surfaces can apply native input gates; the policy never mutates
/// application enabled intent. Platform completeness remains a host capability.
/// </summary>
public sealed partial class PortableModalInputScope : IDisposable
{
    [ThreadStatic] private static PortableModalInputScope? s_current;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private PortableModalInputScope? _previous;
    private object? _window;
    private bool _disposed;
    private bool _nativeReleaseRequested;
    private bool _nativeReleaseCompleted;
    private IDisposable? _afterRelease;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _releaseFailure;

    private PortableModalInputScope(object window)
    {
        _window = window;
        _previous = s_current;
        s_current = this;
    }

    public static bool IsActive => s_current != null;
    public bool IsCurrent => !_disposed && ReferenceEquals(s_current, this);
    public bool IsReleased => _disposed;

    /// <summary>Unknown ownership is rejected while a modal scope is active.</summary>
    public static bool AllowsInput(object? owningWindow) =>
        s_current == null || ReferenceEquals(s_current._window, owningWindow);

    /// <summary>Enter before showing the dialog; dispose when its source ends modality.</summary>
    public static PortableModalInputScope Enter(object window)
    {
        ArgumentNullException.ThrowIfNull(window);
        EnsureNotPublishing();
        s_current?._releaseFailure?.Throw();
        var scope = new PortableModalInputScope(window);
        try
        {
            PublishInputPolicy();
            return scope;
        }
        catch (Exception failure)
        {
            s_current = scope._previous;
            scope._previous = null;
            scope._window = null;
            scope._disposed = true;
            try { PublishInputPolicy(); }
            catch (Exception rollback) { throw new AggregateException(failure, rollback); }
            throw;
        }
    }

    /// <summary>
    /// Transfers source cleanup to native completion. The request must invoke its
    /// completion on this thread only after native modality has ended, immediately
    /// when no native session exists. Completion may be deferred; source gates stay
    /// closed until this scope and its children are ready. Repeated requests do not
    /// transfer another cleanup. Cleanup runs once after gate publication, even if
    /// publication failed, and must consult IsNativeInputPolicySynchronized before
    /// restoring focus. A failed native request is explicit, never an early exit.
    /// </summary>
    public void ReleaseAfterNative(Action<Action> requestNativeRelease, IDisposable afterRelease)
    {
        ArgumentNullException.ThrowIfNull(requestNativeRelease);
        ArgumentNullException.ThrowIfNull(afterRelease);
        EnsureOwningThread();
        EnsureNotPublishing();
        _releaseFailure?.Throw();
        if (_disposed || _nativeReleaseRequested) return;
        _nativeReleaseRequested = true;
        _afterRelease = afterRelease;
        try { requestNativeRelease(CompleteNativeRelease); }
        catch (Exception failure)
        {
            if (!_disposed)
                _releaseFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure);
            throw;
        }
    }

    private void CompleteNativeRelease()
    {
        EnsureOwningThread();
        EnsureNotPublishing();
        _releaseFailure?.Throw();
        if (_nativeReleaseCompleted) return;
        _nativeReleaseCompleted = true;
        DrainReleasedScopes(null);
    }

    private void EnsureOwningThread()
    {
        if (_threadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("A modal input scope must be released on its owning thread.");
    }

    public void Dispose()
    {
        EnsureOwningThread();
        if (_disposed) return;
        EnsureNotPublishing();
        _releaseFailure?.Throw();
        if (_nativeReleaseRequested && !_nativeReleaseCompleted)
            throw new InvalidOperationException("Native modal completion must precede source input release.");
        if (!ReferenceEquals(s_current, this))
            throw new InvalidOperationException("Modal input scopes must be released in reverse entry order.");
        DrainReleasedScopes(this);
    }

    private static void DrainReleasedScopes(PortableModalInputScope? immediate)
    {
        List<Exception>? failures = null;
        while (s_current is { } current && (ReferenceEquals(current, immediate) ||
            (current._nativeReleaseCompleted && current._releaseFailure == null)))
        {
            immediate = null;
            s_current = current._previous;
            current._previous = null;
            current._window = null;
            current._disposed = true;
            IDisposable? afterRelease = current._afterRelease;
            current._afterRelease = null;
            try { PublishInputPolicy(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            try { afterRelease?.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures != null) throw new AggregateException("Modal input release failed.", failures);
    }
}

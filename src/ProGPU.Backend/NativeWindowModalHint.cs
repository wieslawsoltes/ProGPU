namespace ProGPU.Backend;

/// <summary>
/// A host-thread lease for an advisory window-manager modal hint. This is not
/// native input suppression or an event-pump session. The host must release it
/// before hiding/destroying the borrowed native window and retain source gates.
/// </summary>
public sealed class NativeWindowModalHint : IDisposable
{
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly bool _changed;
    private INativeWindowModalHintOperations? _operations;

    private NativeWindowModalHint(INativeWindowModalHintOperations operations, bool changed)
    {
        _operations = operations;
        _changed = changed;
    }

    public bool IsReleased => _operations == null;

    internal static bool TryAcquire(INativeWindowModalHintOperations operations,
        out NativeWindowModalHint? hint)
    {
        ArgumentNullException.ThrowIfNull(operations);
        hint = null;
        if (!operations.TryRead(out bool wasModal)) return false;
        // Allocate before publishing native state. Preserve a preexisting modal
        // bit instead of claiming ownership of another caller's hint.
        var candidate = new NativeWindowModalHint(operations, !wasModal);
        hint = candidate;
        if (!wasModal)
        {
            try
            {
                if (operations.TrySet(true)) return true;
            }
            catch (Exception failure)
            {
                try { candidate.Dispose(); hint = null; }
                catch (Exception rollback) { throw new AggregateException(failure, rollback); }
                throw;
            }
            // An unmapped-property update can precede a rejected WM message.
            // Undo just our bit; preserve the live lease if rollback also fails.
            candidate.Dispose();
            hint = null;
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_threadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("A native modal hint must be released on its host thread.");
        if (_operations is not { } operations) return;
        if (_changed && !operations.TrySet(false))
            throw new InvalidOperationException("The window manager modal-hint release was not submitted.");
        _operations = null;
    }
}

internal interface INativeWindowModalHintOperations
{
    bool TryRead(out bool isModal);
    bool TrySet(bool modal);
}

// WM property notifications are asynchronous. A new dialog invocation must not
// mistake our still-visible, pending-removal bit for an externally owned hint.
internal struct NativeModalHintSubmission
{
    private bool? _pending;
    public void Submitted(bool modal, bool awaitWindowManager = true) =>
        _pending = awaitWindowManager ? modal : null;
    public bool Observe(bool modal)
    {
        if (_pending is not { } requested) return modal;
        if (modal == requested) _pending = null;
        return requested;
    }
}

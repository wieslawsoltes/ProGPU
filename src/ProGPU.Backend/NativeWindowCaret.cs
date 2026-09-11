namespace ProGPU.Backend;

/// <summary>Thread-bound, hidden Win32 caret mirror for a caller-owned native window.</summary>
public sealed class NativeWindowCaret : IDisposable
{
    [ThreadStatic] private static NativeWindowCaret? s_current;
    [ThreadStatic] private static bool s_mutating;
    private readonly INativeWindowCaretOperations _operations;
    private readonly nint _window;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private object? _owner;
    private int _width, _height;
    private bool _disposed;

    internal NativeWindowCaret(nint window, INativeWindowCaretOperations operations)
    {
        _window = window;
        _operations = operations;
    }

    /// <summary>
    /// Does not create/show a window or caret. Dispose before destroying the live
    /// native window. Other platforms have no Win32 queue caret and return false.
    /// </summary>
    public static bool TryCreate(NativeWindowHandle window, out NativeWindowCaret? caret)
    {
        caret = null;
        if (!OperatingSystem.IsWindows() || window.Kind != NativeWindowKind.Win32 || !window.IsValid)
            return false;
        var operations = new Win32CaretOperations();
        if (!operations.IsLocalWindow(window.Handle)) return false;
        caret = new NativeWindowCaret(window.Handle, operations);
        return true;
    }

    /// <summary>
    /// Synchronizes metadata only: never calls ShowCaret or allocates a GDI bitmap.
    /// O(1) time/storage, allocation-free after construction. Ordered OS ownership
    /// operations have no independent SIMD lanes and are not rendering fallbacks.
    /// A false result leaves no claim of successful native synchronization.
    /// </summary>
    public bool TryUpdate(object owner, int x, int y, int width, int height)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(owner);
        using var mutation = new MutationScope();
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!_operations.IsLocalWindow(_window) || !_operations.TryGetCaretWindow(out nint current)) return false;
        if (!ReferenceEquals(s_current, this) || !ReferenceEquals(_owner, owner) ||
            current != _window || width != _width || height != _height)
        {
            if (!_operations.Create(_window, width, height)) return false;
            // CreateCaret replaces the previous queue caret. Publish ownership
            // only after success; late release from the previous editor is inert.
            if (s_current is { } previous) previous._owner = null;
            s_current = this;
            _owner = owner;
            _width = width;
            _height = height;
        }
        return _operations.SetPosition(x, y);
    }

    public void Release(object owner)
    {
        VerifyThread();
        if (_disposed || !ReferenceEquals(_owner, owner)) return;
        using var mutation = new MutationScope();
        ReleaseCurrent();
    }

    private void ReleaseCurrent()
    {
        if (ReferenceEquals(s_current, this))
        {
            if (!_operations.TryGetCaretWindow(out nint current))
                throw new InvalidOperationException("Cannot query native caret ownership for release.");
            // Another native control or window destruction may have replaced it.
            if (current == _window && !_operations.Destroy())
                throw new InvalidOperationException("The native caret could not be released.");
            s_current = null;
        }
        _owner = null;
    }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed) return;
        using var mutation = new MutationScope();
        ReleaseCurrent();
        _disposed = true;
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Native caret operations require the owning window thread.");
    }

    private readonly ref struct MutationScope
    {
        public MutationScope()
        {
            if (s_mutating) throw new InvalidOperationException("Native caret ownership cannot change reentrantly.");
            s_mutating = true;
        }
        public void Dispose() => s_mutating = false;
    }
}

internal interface INativeWindowCaretOperations
{
    bool IsLocalWindow(nint window);
    bool TryGetCaretWindow(out nint window);
    bool Create(nint window, int width, int height);
    bool SetPosition(int x, int y);
    bool Destroy();
}

namespace ProGPU.Wpf.Interop;

/// <summary>
/// Source-owned, thread-bound input admission for nested portable dialogs.
/// Callers resolve a surface to its real owning window before checking it.
/// This does not mutate application enabled state or configure native modality.
/// </summary>
public sealed class PortableModalInputScope : IDisposable
{
    [ThreadStatic] private static PortableModalInputScope? s_current;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private PortableModalInputScope? _previous;
    private object? _window;
    private bool _disposed;

    private PortableModalInputScope(object window)
    {
        _window = window;
        _previous = s_current;
        s_current = this;
    }

    public static bool IsActive => s_current != null;

    /// <summary>Unknown ownership is rejected while a modal scope is active.</summary>
    public static bool AllowsInput(object? owningWindow) =>
        s_current == null || ReferenceEquals(s_current._window, owningWindow);

    /// <summary>Enter before showing the dialog; dispose after its invocation ends.</summary>
    public static PortableModalInputScope Enter(object window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new PortableModalInputScope(window);
    }

    public void Dispose()
    {
        if (_threadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("A modal input scope must be released on its owning thread.");
        if (_disposed) return;
        if (!ReferenceEquals(s_current, this))
            throw new InvalidOperationException("Modal input scopes must be released in reverse entry order.");
        s_current = _previous;
        _previous = null;
        _window = null;
        _disposed = true;
    }
}

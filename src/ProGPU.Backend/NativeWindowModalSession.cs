namespace ProGPU.Backend;

/// <summary>
/// A host-owned native modal event session. Currently supported by AppKit only.
/// All native event polling on the owning thread must call TryPumpEvents before
/// its ordinary platform poll. Rendering and dispatcher work remain host-owned.
/// </summary>
public sealed class NativeWindowModalSession : IDisposable
{
    [ThreadStatic] private static NativeWindowModalSession? s_current;
    [ThreadStatic] private static bool s_transitioning;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private INativeModalSessionOperations? _operations;
    private NativeWindowModalSession? _previous;
    private nint _session;
    private int _pollDepth;
    private bool _releaseRequested;

    internal const int ContinueResponse = -1002;

    private NativeWindowModalSession(INativeModalSessionOperations operations)
    {
        _operations = operations;
    }

    public static bool IsActive => s_current != null;
    public bool IsReleased => _operations == null;
    public nint LastResponse { get; private set; } = ContinueResponse;

    /// <summary>
    /// Whether a session on this thread still borrows this host window. Native
    /// host destruction must wait even after release was requested in a callback.
    /// </summary>
    public static bool RetainsWindow(NativeWindowHandle window)
    {
        if (window.Kind != NativeWindowKind.Cocoa || !window.IsValid) return false;
        for (var current = s_current; current != null; current = current._previous)
            if (current._operations?.Window == window.Handle) return true;
        return false;
    }

    /// <summary>
    /// Begins for a live, already-visible native window on its platform thread.
    /// The caller must keep its host alive until IsReleased, including across
    /// callbacks. Other modal/native popup admission is a separate host contract;
    /// this API does not turn GLFW NSWindows into modal-capable NSPanels.
    /// Unsupported platforms and foreign AppKit modal sessions reject explicitly.
    /// </summary>
    public static bool TryBegin(NativeWindowHandle window, out NativeWindowModalSession? session)
    {
        session = null;
        EnsureNotTransitioning();
        if (!OperatingSystem.IsMacOS() || window.Kind != NativeWindowKind.Cocoa || !window.IsValid)
            return false;
        var operations = CocoaNativeModalSession.TryCreate(window.Handle,
            s_current?._operations?.Window ?? 0);
        return operations != null && TryBegin(operations, out session);
    }

    // Takes ownership even on rejection. The typed seam also supplies lifecycle
    // fixtures without loading AppKit or manufacturing native window pointers.
    internal static bool TryBegin(INativeModalSessionOperations operations, out NativeWindowModalSession? session)
    {
        ArgumentNullException.ThrowIfNull(operations);
        session = null;
        bool ownsTransition = false;
        NativeWindowModalSession? candidate = null;
        try
        {
            EnsureNotTransitioning();
            candidate = new NativeWindowModalSession(operations);
            s_transitioning = true;
            ownsTransition = true;
            // Native begin may publish activation callbacks. Own polling and
            // host lifetime before entering AppKit, not after it returns.
            candidate._previous = s_current;
            s_current = candidate;
            candidate._session = operations.Begin();
            if (candidate._session == 0) return false;
            session = candidate;
            return true;
        }
        finally
        {
            try
            {
                if (session == null)
                {
                    if (candidate != null)
                    {
                        s_current = candidate._previous;
                        candidate._previous = null;
                        candidate._operations = null;
                    }
                    operations.Dispose();
                }
            }
            finally { if (ownsTransition) s_transitioning = false; }
        }
    }

    /// <summary>
    /// Returns false only when no native modal session owns this thread's poll.
    /// True forbids an additional GLFW/default event poll. Reentrant polls of the
    /// same session are already being serviced; a newly nested session may poll.
    /// A stopped native session is an explicit failure, not a renderer fallback.
    /// </summary>
    public static bool TryPumpEvents()
    {
        if (s_transitioning) return true; // Native begin/end already owns dispatch.
        var current = s_current;
        if (current == null) return false;
        if (current._pollDepth != 0 || current._releaseRequested) return true;
        current._pollDepth++;
        try
        {
            current.LastResponse = current._operations!.Poll(current._session);
            if (current.LastResponse != ContinueResponse && !current._releaseRequested)
                throw new InvalidOperationException($"Native modal session stopped with response {current.LastResponse} before its host released it.");
        }
        finally
        {
            current._pollDepth--;
            DrainReleasedSessions();
        }
        return true;
    }

    /// <summary>
    /// Requests release on the creating thread. End is deferred until native
    /// polling returns and nested sessions end; it never frees an active native
    /// session from inside that session's event callback. Repeated calls are safe.
    /// </summary>
    public void Dispose()
    {
        if (_threadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("A native modal session must be released on its creating thread.");
        if (IsReleased) return;
        EnsureNotTransitioning();
        _releaseRequested = true;
        DrainReleasedSessions();
    }

    private static void DrainReleasedSessions()
    {
        while (s_current is { _releaseRequested: true, _pollDepth: 0 } current)
        {
            var operations = current._operations!;
            s_transitioning = true;
            try { operations.End(current._session); }
            finally
            {
                s_current = current._previous;
                current._previous = null;
                current._session = 0;
                current._operations = null;
                try { operations.Dispose(); }
                finally { s_transitioning = false; }
            }
        }
    }

    private static void EnsureNotTransitioning()
    {
        if (s_transitioning)
            throw new InvalidOperationException("Native modal-session lifetime cannot reenter during begin/end.");
    }
}

internal interface INativeModalSessionOperations : IDisposable
{
    nint Window { get; }
    nint Begin();
    nint Poll(nint session);
    void End(nint session);
}

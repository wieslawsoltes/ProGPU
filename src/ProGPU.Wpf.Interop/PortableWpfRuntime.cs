namespace ProGPU.Wpf.Interop;

/// <summary>Source-built WPF media transport, independent of the ProGPU renderer mode.</summary>
public enum PortableWpfMediaBackend
{
    WindowsMil = 1,
    Portable = 2
}

/// <summary>
/// Startup-only media selection shared by source-built WPF and its host. Configure
/// before constructing WPF objects; the first media/lock/input consumer freezes the choice.
/// Selecting Portable does not create a device or choose managed versus native ProGPU.
/// </summary>
public static class PortableWpfRuntime
{
    private static readonly PortableWpfMediaBackendSelection Selection = new(OperatingSystem.IsWindows());

    /// <summary>The configured choice; reading diagnostics does not freeze selection.</summary>
    public static PortableWpfMediaBackend ConfiguredMediaBackend => Selection.ConfiguredBackend;

    public static bool IsMediaBackendFrozen => Selection.IsFrozen;

    /// <summary>
    /// Selects the media transport. A different selection after first use throws;
    /// repeating the same selection is allowed. Windows MIL is Windows-only.
    /// </summary>
    public static void SelectMediaBackend(PortableWpfMediaBackend backend) => Selection.Select(backend);

    /// <summary>
    /// Acquires the immutable choice for media-resource ownership. There is no reset
    /// after shutdown: surviving resources and other dispatchers retain that domain.
    /// </summary>
    public static PortableWpfMediaBackend GetMediaBackendAndFreeze() => Selection.GetAndFreeze();
}

internal sealed class PortableWpfMediaBackendSelection
{
    private const int Frozen = 4;
    private readonly object _gate = new();
    private readonly bool _windowsMilAvailable;
    private int _state;

    internal PortableWpfMediaBackendSelection(bool windowsMilAvailable)
    {
        _windowsMilAvailable = windowsMilAvailable;
        _state = (int)(windowsMilAvailable ? PortableWpfMediaBackend.WindowsMil : PortableWpfMediaBackend.Portable);
    }

    internal PortableWpfMediaBackend ConfiguredBackend => (PortableWpfMediaBackend)(Volatile.Read(ref _state) & ~Frozen);
    internal bool IsFrozen => (Volatile.Read(ref _state) & Frozen) != 0;

    internal void Select(PortableWpfMediaBackend backend)
    {
        if (backend is not PortableWpfMediaBackend.WindowsMil and not PortableWpfMediaBackend.Portable)
            throw new ArgumentOutOfRangeException(nameof(backend));
        if (backend == PortableWpfMediaBackend.WindowsMil && !_windowsMilAvailable)
            throw new PlatformNotSupportedException("Windows MIL media transport is only available on Windows.");

        lock (_gate)
        {
            if ((_state & Frozen) != 0 && ConfiguredBackend != backend)
                throw new InvalidOperationException("WPF media backend selection is frozen. Select the portable backend before constructing WPF media objects.");
            Volatile.Write(ref _state, (_state & Frozen) | (int)backend);
        }
    }

    internal PortableWpfMediaBackend GetAndFreeze()
    {
        int state = Volatile.Read(ref _state);
        if ((state & Frozen) == 0)
        {
            lock (_gate)
            {
                state = _state | Frozen;
                Volatile.Write(ref _state, state);
            }
        }
        return (PortableWpfMediaBackend)(state & ~Frozen);
    }
}

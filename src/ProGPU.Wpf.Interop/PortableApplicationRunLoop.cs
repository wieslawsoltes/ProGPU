namespace ProGPU.Wpf.Interop;

/// <summary>Source-owned lifetime and native-host selection for one application thread.</summary>
public interface IPortableApplicationRunLoopSource
{
    bool IsShutdownRequested { get; }
    object? FindRunHost();
    bool IsHostAlive(object host);
    void RunHost(object host);
    /// <summary>Block on source dispatcher work until a host or shutdown is available.</summary>
    void WaitForHost();
}

/// <summary>
/// Keeps application lifetime independent of whichever native window currently
/// owns event polling. Sources retain their own shutdown mode and window identities.
/// </summary>
public static class PortableApplicationRunLoop
{
    /// <remarks>
    /// Synchronous, thread-bound borrowed callbacks; no pointers or hosts are retained.
    /// O(H) transitions for H host handoffs, plus source selection/dispatch work,
    /// O(1) storage. This ordered lifetime state machine is not a compute fallback.
    /// A host must return only after it retires or source shutdown is requested.
    /// A hostless wait must block until there is progress; it must not busy-poll.
    /// Callback failures propagate without selecting a different renderer or shutdown.
    /// </remarks>
    public static void Run<TSource>(ref TSource source)
        where TSource : struct, IPortableApplicationRunLoopSource
    {
        while (!source.IsShutdownRequested)
        {
            object? host = source.FindRunHost();
            if (source.IsShutdownRequested) return;
            if (host is null)
            {
                source.WaitForHost();
                if (source.IsShutdownRequested) return;
                host = source.FindRunHost();
                if (source.IsShutdownRequested) return;
                if (host is null)
                    throw new InvalidOperationException("The portable application wait returned without a host or shutdown.");
            }

            // Selection can run source callbacks: recheck lifetime before entering.
            if (!source.IsHostAlive(host))
                throw new InvalidOperationException("The portable application selected a retired host.");
            source.RunHost(host);
            if (!source.IsShutdownRequested && source.IsHostAlive(host))
                throw new InvalidOperationException("The portable host loop returned while its window and application were still live.");
        }
    }
}

namespace ProGPU.Wpf.Interop;

/// <summary>
/// Optional source-owned scheduling for a portable window's input callbacks.
/// </summary>
/// <remarks>
/// A window activation registrar may implement this capability separately from
/// <see cref="IPortableWindowActivationServiceRegistrar"/>. Its existing
/// <c>TryBeginInvokeInput</c> contract is unchanged.
/// </remarks>
public interface IPortableWindowInputDispatcher
{
    /// <summary>
    /// Attempts to asynchronously queue <paramref name="callback"/> at the
    /// actual owning window dispatcher's Input priority.
    /// </summary>
    /// <param name="window">The original source window identity, not a native handle.</param>
    /// <param name="callback">The callback whose queue ownership is being offered.</param>
    /// <returns>
    /// <see langword="true"/> when the source dispatcher accepted the callback;
    /// <see langword="false"/> when it neither invokes nor retains the callback.
    /// </returns>
    /// <remarks>
    /// Accepted work is always queued, even on the owning thread; this method
    /// never invokes the callback inline. Acceptance is not proof of execution:
    /// queued work remains subject to the source dispatcher's shutdown policy.
    /// The source provider validates window ownership and disposed/shutdown state.
    /// Missing capability or rejection does not authorize reflection, synchronous
    /// invocation, a thread-pool substitute, or a private host-queue fallback.
    /// </remarks>
    bool TryPostInput(object window, Action callback);
}

using System.Runtime.ExceptionServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// Serializes channel retirement without discarding a failed import's exact
/// ownership. It does not synchronize ordinary channel operations.
/// </summary>
internal static class NativeMilChannelRetirement
{
    internal static nint GetHandle(ref nint handle, ref int disposeState, object owner)
    {
        // A retained handle after failure is teardown-only, never readmission.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, owner);
        nint current = Volatile.Read(ref handle);
        ObjectDisposedException.ThrowIf(current == 0, owner);
        return current;
    }

    /// <remarks>
    /// The callback must be the direct consuming blittable void import, with no
    /// fallible managed work after dispatch. Only the listed import failures
    /// establish that native entry did not occur. All other faults have unknown
    /// completion and must never cause a second dispatch against this handle.
    /// </remarks>
    internal static void Dispose(object gate, ref nint handle, ref int disposeState,
        ref bool destroying, ref Exception? unknownCompletion, Action<nint> destroy)
    {
        lock (gate)
        {
            Volatile.Write(ref disposeState, 1);
            if (handle == 0 || destroying)
                return;
            if (unknownCompletion is not null)
                ExceptionDispatchInfo.Throw(unknownCompletion);

            destroying = true;
            try
            {
                destroy(handle);
                // No fallible work between the consuming import and retirement.
                Volatile.Write(ref handle, 0);
            }
            catch (Exception failure) when (failure is DllNotFoundException
                or EntryPointNotFoundException or BadImageFormatException)
            {
                // Binding failed before entry: keep exact ownership for retry.
                throw;
            }
            catch (Exception failure)
            {
                // A void ABI cannot prove completion for an unclassified fault.
                // Preserve the original failure without allocating cleanup data.
                unknownCompletion = failure;
                throw;
            }
            finally
            {
                destroying = false;
            }
        }
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

internal delegate NativeHintedGlyphResource HintedResourceReflow(nint resource, int inputStart, float maximumWidth,
    float dpiScale, NativeHintedProjectionPolicy projection, NativeHintedCoverage coverage, bool nominalMetrics);

public sealed unsafe partial class NativeHintedGlyphResource
{
    private static readonly HintedResourceReflow NativeReflow = ReflowNative;
    private readonly HintedResourceReflow _reflow = NativeReflow;
    // Intrusive links need no allocation during failed publication. These
    // results were never returned to a caller; this exact owner retries them.
    private NativeHintedGlyphResource? _reflowRetirementHead, _reflowRetirementNext;
    private bool _drainingReflows;

    /// <summary>
    /// Places a suffix at an exact original shaped cluster boundary, retaining
    /// the complete original logical glyph/font/run generation. No shaping,
    /// hinting, source prefix repair or mutable context access is repeated.
    /// The returned owner has independent native positioning, writer frames and
    /// interaction, with this resource's DPI, projection, coverage and nominal
    /// metric preparation. Zero width retains native unbounded semantics.
    /// Nested reflow cannot move before this view's first source input.
    /// This does not admit collapse, Display policy or precise-double metrics.
    /// </summary>
    public NativeHintedGlyphResource Reflow(int inputStart, float maximumWidth)
    {
        if (inputStart < 0) throw new ArgumentOutOfRangeException(nameof(inputStart));
        if (!float.IsFinite(maximumWidth) || maximumWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        nint handle;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _handle == 0, this);
            _uses = checked(_uses + 1);
            handle = _handle;
        }
        NativeHintedGlyphResource? result = null;
        Exception? primary = null;
        try
        {
            result = _reflow(handle, inputStart, maximumWidth, DpiScale, Projection, Coverage, _nominalMetrics.HasValue)
                ?? throw new InvalidOperationException("Native hinted reflow returned no owned generation.");
            return result;
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { EndImport(); }
            catch (Exception cleanup)
            {
                if (primary is not null) AttachCleanup(primary, cleanup);
                else
                {
                    // Publication failed at retirement: do not lose the newly
                    // owned result, and leave the exact old owner retryable.
                    try { if (result is not null) RetireUnpublishedReflow(result); }
                    catch (Exception resultCleanup) { AttachCleanup(cleanup, resultCleanup); }
                    throw;
                }
            }
        }
    }

    private void RetireUnpublishedReflow(NativeHintedGlyphResource result)
    {
        lock (_gate)
        {
            result._reflowRetirementNext = _reflowRetirementHead;
            _reflowRetirementHead = result;
            DrainReflowRetirements();
        }
    }

    // Caller owns _gate. Drain all owners, retaining exact failures rather
    // than relying on finalization or losing siblings after the first error.
    private void DrainReflowRetirements()
    {
        if (_drainingReflows) return;
        _drainingReflows = true;
        Exception? primary = null;
        try
        {
            NativeHintedGlyphResource? previous = null, current = _reflowRetirementHead;
            while (current is not null)
            {
                var next = current._reflowRetirementNext;
                try
                {
                    current.Dispose();
                    if (previous is null) _reflowRetirementHead = next;
                    else previous._reflowRetirementNext = next;
                    current._reflowRetirementNext = null;
                }
                catch (Exception error)
                {
                    if (primary is null) primary = error;
                    else AttachCleanup(primary, error);
                    previous = current;
                }
                current = next;
            }
        }
        finally { _drainingReflows = false; }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        try { primary.Data["HintedReflowCleanupFailure"] = cleanup; }
        catch { /* Preserve the original operation/retirement error. */ }
    }

    private static NativeHintedGlyphResource ReflowNative(nint original, int inputStart, float maximumWidth,
        float dpiScale, NativeHintedProjectionPolicy projection, NativeHintedCoverage coverage, bool nominalMetrics)
    {
        nint continuation = 0;
        try
        {
            NativeHintedParagraph.ThrowForStatus(NativeMethods.ReflowHintedGlyphResource(original, inputStart,
                maximumWidth, &continuation), "retained paragraph continuation");
            if (continuation == 0) throw new InvalidOperationException("Native hinted reflow returned no owner.");
            return new NativeHintedGlyphResource(continuation, dpiScale, projection, coverage, nominalMetrics);
        }
        catch (Exception error)
        {
            if (continuation != 0)
                try { NativeMethods.DestroyHintedGlyphResource(continuation); }
                catch (Exception cleanup) { AttachCleanup(error, cleanup); }
            throw;
        }
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_glyph_resource_reflow")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ReflowHintedGlyphResource(nint resource,
        int inputStart, float maximumWidth, nint* continuation);
}

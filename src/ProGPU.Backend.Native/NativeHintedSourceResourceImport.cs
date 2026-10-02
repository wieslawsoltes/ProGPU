using System.Buffers;
using System.Runtime.ExceptionServices;

namespace ProGPU.Backend.Native;

internal unsafe delegate NativeMilStatus NativeSourceGlyphImport(nint channel, byte* batch, nuint batchSize,
    NativeMethods.HintedGlyphResourceInput* resources, uint resourceCount,
    NativeMilHintedGlyphBinding* bindings, uint bindingCount, uint* indices, uint indexCount);

// One synchronous flat crossing, shared by both providers. No producer handle
// crosses the library boundary, and no raw/source subset commits separately.
internal static unsafe class NativeHintedSourceResourceImport
{
    internal static NativeMilStatus Apply(nint channel, ReadOnlySpan<byte> batch,
        ReadOnlySpan<NativeHintedGlyphResource> resources, ReadOnlySpan<NativeMilHintedGlyphBinding> bindings,
        ReadOnlySpan<uint> indices, NativeSourceGlyphImport apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        NativeMethods.HintedGlyphResourceView[]? rasterViews = null;
        NativeMethods.HintedSourceGlyphResourceView[]? sourceViews = null;
        NativeMethods.HintedGlyphResourceInput[]? inputs = null;
        NativeHintedGlyphResource[]? owners = null;
        int acquired = 0;
        Exception? primaryError = null;
        try
        {
            int capacity = Math.Max(1, resources.Length);
            rasterViews = ArrayPool<NativeMethods.HintedGlyphResourceView>.Shared.Rent(capacity);
            sourceViews = ArrayPool<NativeMethods.HintedSourceGlyphResourceView>.Shared.Rent(capacity);
            inputs = ArrayPool<NativeMethods.HintedGlyphResourceInput>.Shared.Rent(capacity);
            owners = ArrayPool<NativeHintedGlyphResource>.Shared.Rent(capacity);
            for (int i = 0; i < resources.Length; i++)
            {
                var owner = resources[i];
                ArgumentNullException.ThrowIfNull(owner);
                rasterViews[i] = owner.AcquireForImport();
                owners[i] = owner;
                acquired++;
                sourceViews[i] = owner.SourceViewWhileRetained() ?? default;
            }
            fixed (NativeMethods.HintedGlyphResourceView* raster = rasterViews)
            fixed (NativeMethods.HintedSourceGlyphResourceView* source = sourceViews)
            fixed (NativeMethods.HintedGlyphResourceInput* input = inputs)
            fixed (byte* bytes = batch)
            fixed (NativeMilHintedGlyphBinding* binding = bindings)
            fixed (uint* positioned = indices)
            {
                for (int i = 0; i < resources.Length; i++)
                    input[i] = new()
                    {
                        AbiVersion = NativeMethods.AbiVersion,
                        StructSize = (uint)sizeof(NativeMethods.HintedGlyphResourceInput), Version = 2,
                        Raster = (nuint)(raster + i), Source = owners[i].HasSourceGeometry ? (nuint)(source + i) : 0
                    };
                NativeMilStatus status = apply(channel, bytes, (nuint)batch.Length, input, checked((uint)resources.Length),
                    binding, checked((uint)bindings.Length), positioned, checked((uint)indices.Length));
                if (status != NativeMilStatus.Success)
                    throw new NativeMilException(status, $"The atomic source glyph MIL update was rejected with {status}.");
                return status;
            }
        }
        catch (Exception error) { primaryError = error; throw; }
        finally
        {
            Exception? cleanupError = null;
            if (owners is not null)
            {
                for (int i = acquired - 1; i >= 0; i--)
                    try { owners[i].EndImport(); }
                    catch (Exception error) { cleanupError ??= error; }
                ArrayPool<NativeHintedGlyphResource>.Shared.Return(owners, clearArray: true);
            }
            if (inputs is not null) ArrayPool<NativeMethods.HintedGlyphResourceInput>.Shared.Return(inputs, clearArray: true);
            if (sourceViews is not null) ArrayPool<NativeMethods.HintedSourceGlyphResourceView>.Shared.Return(sourceViews, clearArray: true);
            if (rasterViews is not null) ArrayPool<NativeMethods.HintedGlyphResourceView>.Shared.Return(rasterViews, clearArray: true);
            if (cleanupError is not null)
            {
                if (primaryError is null) ExceptionDispatchInfo.Capture(cleanupError).Throw();
                else try { primaryError.Data["HintedSourceResourceCleanupFailure"] = cleanupError; } catch { }
            }
        }
    }
}

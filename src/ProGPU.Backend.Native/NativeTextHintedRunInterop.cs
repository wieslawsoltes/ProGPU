using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// One owned hinted shaping generation. Final glyph metrics are signed 26.6
/// device units in the existing Y-down wire convention. Original captured
/// outlines retain their Y-up coordinates and phase; descriptor indices pair
/// every positioned glyph with its exact outline slot, including repeated IDs.
/// This explicit run does not enable source Display formatting.
/// </summary>
public sealed unsafe class NativeHintedTextRun : IDisposable
{
    private readonly NativeTextContextOwner _owner = null!;

    internal NativeHintedTextRun(nint handle)
    {
        uint glyphCount = 0;
        NativeHintedBatchCounts outlineCounts = default;
        NativeRendererStatus status = NativeMethods.GetHintedRunCounts(handle, &glyphCount, &outlineCounts);
        if (status != NativeRendererStatus.Success)
            throw new InvalidOperationException($"Native hinted run counts failed with {status}.");
        GlyphCount = glyphCount;
        OutlineCounts = outlineCounts;
        // The capturing caller still owns the raw handle if construction fails.
        _owner = new NativeTextContextOwner(handle, NativeMethods.DestroyHintedRun, nameof(NativeHintedTextRun));
    }

    public uint GlyphCount { get; }
    public NativeHintedBatchCounts OutlineCounts { get; }

    /// <summary>
    /// Copies all final glyphs and their original captured descriptor slots in
    /// one atomic native operation. Every unused destination slot is untouched.
    /// </summary>
    public void CopyGlyphsTo(Span<NativeTextShapingGlyph> glyphs, Span<uint> descriptorIndices)
    {
        using var use = _owner.Acquire();
        if ((uint)glyphs.Length < GlyphCount || (uint)descriptorIndices.Length < GlyphCount)
            throw new ArgumentException("Buffers must cover every retained hinted glyph and descriptor index.");
        fixed (NativeTextShapingGlyph* glyphPointer = glyphs)
        fixed (uint* descriptorPointer = descriptorIndices)
        {
            NativeRendererStatus status = NativeMethods.CopyHintedRunGlyphs(use.Handle,
                glyphPointer, checked((uint)glyphs.Length), descriptorPointer, checked((uint)descriptorIndices.Length));
            if (status != NativeRendererStatus.Success)
                throw new ArgumentException($"Native hinted run transfer rejected buffers with {status}.");
        }
    }

    /// <summary>
    /// Copies the same generation's complete original-order captured outlines,
    /// including separate auxiliary slots used by device space positioning.
    /// This does not execute fonts, reshape text, allocate or submit GPU work.
    /// </summary>
    public void CopyOutlinesTo(Span<NativeHintedGlyph> glyphs, Span<NativeHintedPoint> points,
        Span<byte> tags, Span<int> contourEnds)
    {
        using var use = _owner.Acquire();
        if ((uint)glyphs.Length < OutlineCounts.Glyphs || (uint)points.Length < OutlineCounts.Points ||
            (uint)tags.Length < OutlineCounts.Points || (uint)contourEnds.Length < OutlineCounts.Contours)
            throw new ArgumentException("Buffers must cover the complete retained hinted outlines.");
        fixed (NativeHintedGlyph* glyphPointer = glyphs)
        fixed (NativeHintedPoint* pointPointer = points)
        fixed (byte* tagPointer = tags)
        fixed (int* contourPointer = contourEnds)
        {
            NativeRendererStatus status = NativeMethods.CopyHintedRunOutlines(use.Handle,
                glyphPointer, checked((uint)glyphs.Length), pointPointer, checked((uint)points.Length),
                tagPointer, checked((uint)tags.Length), contourPointer, checked((uint)contourEnds.Length));
            if (status != NativeRendererStatus.Success)
                throw new ArgumentException($"Native hinted outline transfer rejected buffers with {status}.");
        }
    }

    ~NativeHintedTextRun() => _owner?.Dispose();

    public void Dispose()
    {
        _owner.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed unsafe partial class NativeTextShapingContext
{
    /// <summary>
    /// Shapes through the original native pipeline with a single post-GSUB
    /// hinted capture, before GPOS and device fallback positioning. The owned
    /// generation survives context retirement and cache eviction. Input font,
    /// face and normalization resources must be empty: this context owns those
    /// resources and fontIndex selects its exact primary/fallback source.
    /// </summary>
    public NativeHintedTextRun ShapeHintedRun(in NativeTextShapeInput input, uint fontIndex,
        uint xPixelsPerEm26_6, uint yPixelsPerEm26_6, NativeFontHintInterpreter interpreter,
        uint xPhase26_6 = 0, uint yPhase26_6 = 0, ReadOnlySpan<int> variationCoordinates16_16 = default)
    {
        using var use = _owner.Acquire();
        ValidateHintedShapeResources(in input);
        NativeMethods.HintedFontRequest hinting = new()
        {
            AbiVersion = NativeMethods.AbiVersion,
            StructSize = (uint)sizeof(NativeMethods.HintedFontRequest),
            FontIndex = fontIndex,
            XPixelsPerEm266 = xPixelsPerEm26_6,
            YPixelsPerEm266 = yPixelsPerEm26_6,
            Interpreter = (uint)interpreter,
            XPhase266 = xPhase26_6,
            YPhase266 = yPhase26_6,
            VariationCount = checked((uint)variationCoordinates16_16.Length),
        };
        nint run = 0;
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* preContext = input.PreContext)
        fixed (NativeTextScalar* postContext = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        fixed (int* variations = variationCoordinates16_16)
        {
            NativeTextShapeRequest shaping = NativeTextShapingInterop.CreateRequest(in input,
                null, scalars, preContext, postContext, features, coordinates, null, includeOwnedResources: false);
            NativeRendererStatus status = NativeMethods.ShapeHintedTextRun(use.Handle, &hinting, variations, &shaping, &run);
            if (status != NativeRendererStatus.Success)
            {
                if (status == NativeRendererStatus.Unsupported)
                    throw new NotSupportedException("The selected native hinted shaping path is unavailable or unsupported.");
                if (status == NativeRendererStatus.InvalidArgument)
                    throw new ArgumentException("The native hinted shaping request is invalid.");
                throw new InvalidOperationException($"Native hinted shaping failed with {status}.");
            }
        }
        try { return new NativeHintedTextRun(run); }
        catch
        {
            NativeMethods.DestroyHintedRun(run);
            throw;
        }
    }

    internal static void ValidateHintedShapeResources(in NativeTextShapeInput input)
    {
        if (!input.FontData.IsEmpty || input.FaceIndex != 0 || !input.NormalizationData.IsEmpty)
            throw new ArgumentException("Hinted context shaping cannot replace its owned font, collection face or normalization resources.", nameof(input));
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_shape_hinted_run")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ShapeHintedTextRun(nint context, HintedFontRequest* hinting,
        int* variationCoordinates16_16, NativeTextShapeRequest* shaping, nint* run);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_run_get_counts")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetHintedRunCounts(nint run, uint* glyphCount, NativeHintedBatchCounts* outlineCounts);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_run_copy_glyphs")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus CopyHintedRunGlyphs(nint run,
        NativeTextShapingGlyph* glyphs, uint glyphCapacity, uint* descriptorIndices, uint descriptorCapacity);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_run_copy_outlines")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus CopyHintedRunOutlines(nint run,
        NativeHintedGlyph* glyphs, uint glyphCapacity, NativeHintedPoint* points, uint pointCapacity,
        byte* tags, uint tagCapacity, int* contourEnds, uint contourCapacity);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_run_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyHintedRun(nint run);
}

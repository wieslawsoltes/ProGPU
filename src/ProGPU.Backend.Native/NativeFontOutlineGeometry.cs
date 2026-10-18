using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public partial struct NativeFontOutlineCounts { }
public partial struct NativeFontOutlineDescriptor { }

/// <summary>
/// Immutable physical Y-up geometry retaining its original font batch. This
/// resource survives batch/context disposal and does not choose source layout,
/// positions, paint, contrast or a target. Copies never execute fonts or GPU work.
/// </summary>
public sealed unsafe class NativeFontOutlineGeometry : IDisposable
{
    private static readonly Action<nint> NativeDestroy = NativeMethods.DestroyFontOutlineGeometry;
    private readonly NativeTextContextOwner _owner = null!;

    internal NativeFontOutlineGeometry(nint handle)
    {
        NativeFontOutlineCounts counts = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.GetFontOutlineGeometryCounts(handle, &counts),
            "font outline geometry counts");
        Counts = counts;
        _owner = new NativeTextContextOwner(handle, NativeDestroy, nameof(NativeFontOutlineGeometry));
    }

    public NativeFontOutlineCounts Counts { get; }

    /// <summary>
    /// Transfers complete geometry and source-indexed mappings in one call.
    /// Every unused destination slot is preserved; no output changes on rejection.
    /// Descriptors retain duplicate glyph IDs and use uint.MaxValue for no ink.
    /// </summary>
    public void CopyTo(Span<NativeGlyphOutline> outlines, Span<NativePathSegment> segments,
        Span<NativeFontOutlineDescriptor> descriptors)
    {
        using var use = _owner.Acquire();
        if ((uint)outlines.Length < Counts.Outlines || (uint)segments.Length < Counts.Segments ||
            (uint)descriptors.Length < Counts.Descriptors)
            throw new ArgumentException("Buffers must cover the complete font outline geometry.");
        fixed (NativeGlyphOutline* outlinePointer = outlines)
        fixed (NativePathSegment* segmentPointer = segments)
        fixed (NativeFontOutlineDescriptor* descriptorPointer = descriptors)
            NativeHintedParagraph.ThrowForStatus(NativeMethods.CopyFontOutlineGeometry(use.Handle,
                outlinePointer, checked((uint)outlines.Length), segmentPointer, checked((uint)segments.Length),
                descriptorPointer, checked((uint)descriptors.Length)), "font outline geometry copy");
    }

    ~NativeFontOutlineGeometry() => _owner?.Dispose();

    public void Dispose()
    {
        _owner.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed unsafe partial class NativeHintedFontBatch
{
    /// <summary>
    /// Prepares all original descriptors through the shared contour writer,
    /// retaining this batch independently. No shaping or font execution occurs.
    /// Vector coverage is explicit and does not claim Microsoft raster parity.
    /// </summary>
    public NativeFontOutlineGeometry CreateGeometry(
        NativeHintedProjectionPolicy projection = NativeHintedProjectionPolicy.Automatic,
        NativeHintedCoverage coverage = NativeHintedCoverage.Strict)
    {
        using var use = _owner.Acquire();
        nint geometry = 0;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.CreateFontOutlineGeometry(use.Handle,
            (uint)projection, (uint)coverage, &geometry), "font outline geometry preparation");
        try { return new NativeFontOutlineGeometry(geometry); }
        catch
        {
            NativeMethods.DestroyFontOutlineGeometry(geometry);
            throw;
        }
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_batch_create_geometry")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus CreateFontOutlineGeometry(nint batch, uint projection,
        uint coverage, nint* geometry);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_font_outline_geometry_get_counts")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetFontOutlineGeometryCounts(nint geometry, NativeFontOutlineCounts* counts);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_font_outline_geometry_copy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus CopyFontOutlineGeometry(nint geometry,
        NativeGlyphOutline* outlines, uint outlineCapacity, NativePathSegment* segments, uint segmentCapacity,
        NativeFontOutlineDescriptor* descriptors, uint descriptorCapacity);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_font_outline_geometry_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyFontOutlineGeometry(nint geometry);
}

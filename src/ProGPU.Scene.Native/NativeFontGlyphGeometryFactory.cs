using System.Numerics;
using ProGPU.Backend.Native;
using ProGPU.Text;

namespace ProGPU.Scene.Native;

public static partial class NativeHintedGlyphGeometryFactory
{
    /// <summary>
    /// Imports every captured font descriptor at its caller-owned original
    /// logical baseline position. No layout, advance, cluster or bidi metadata
    /// is inferred. The result owns an independent native geometry generation;
    /// disposing the batch/context cannot invalidate a recorded picture.
    /// </summary>
    public static HintedGlyphGeometry CreateFontBatch(NativeHintedFontBatch batch,
        ReadOnlySpan<Vector2> positions, float dpiScale,
        NativeHintedProjectionPolicy projection = NativeHintedProjectionPolicy.Automatic,
        NativeHintedCoverage coverage = NativeHintedCoverage.Strict)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!float.IsFinite(dpiScale) || dpiScale <= 0f || !float.IsFinite(1f / dpiScale))
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        if (positions.Length > 1 << 24 || (uint)positions.Length != batch.Counts.Glyphs)
            throw new ArgumentException("Positions must cover every original font descriptor.", nameof(positions));
        // Freeze mutable caller storage before native preparation or publication.
        Vector2[] originalPositions = positions.ToArray();
        foreach (Vector2 position in originalPositions)
            if (!Finite(position)) throw new ArgumentException("Original positions must be finite.", nameof(positions));
        NativeFontOutlineGeometry owner = batch.CreateGeometry(projection, coverage);
        try
        {
            NativeFontOutlineCounts counts = owner.Counts;
            Require(counts.Descriptors == originalPositions.Length && counts.Outlines <= 1 << 20 &&
                counts.Segments <= 1 << 24);
            var nativeOutlines = new NativeGlyphOutline[checked((int)counts.Outlines)];
            var nativeSegments = new NativePathSegment[checked((int)counts.Segments)];
            var descriptors = new NativeFontOutlineDescriptor[originalPositions.Length];
            owner.CopyTo(nativeOutlines, nativeSegments, descriptors);
            ValidateGeometry(nativeOutlines, nativeSegments);
            ConvertGeometry(nativeOutlines, nativeSegments, out var outlines, out var segments);
            var placements = new FontGlyphPlacement[descriptors.Length];
            for (int i = 0; i < descriptors.Length; i++)
            {
                ref readonly NativeFontOutlineDescriptor descriptor = ref descriptors[i];
                Require(descriptor.OutlineIndex == uint.MaxValue || descriptor.OutlineIndex < counts.Outlines);
                placements[i] = new(checked((uint)i), descriptor.GlyphIndex, descriptor.OutlineIndex, originalPositions[i]);
            }
            return HintedGlyphGeometry.FromFontPlacements(dpiScale, outlines, segments, placements, owner);
        }
        catch (Exception failure)
        {
            try { owner.Dispose(); }
            catch (Exception cleanup)
            {
                try { failure.Data["NativeFontGeometryCleanupFailure"] = cleanup; }
                catch { /* Preserve the original publication failure. */ }
            }
            throw;
        }
    }
}

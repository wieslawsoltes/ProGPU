using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;

// Run with each configured compute/raster/SIMD/scalar implementation. The
// uncached render is an independent resource-rebuild oracle, not a UV snapshot.
internal static class GlyphRasterRetentionQualification
{
    internal static void Run(NativeCompositor renderer, GpuTexture target,
        GpuComputeExecutionPath rasterizationPath)
    {
        NativePathSegment[] segments =
        [
            new(NativePathSegmentKind.Line, new(0, 0), new(12, 0)),
            new(NativePathSegmentKind.Quadratic, new(12, 0), new(18, 7), new(12, 14)),
            new(NativePathSegmentKind.Line, new(12, 14), new(0, 14)),
            new(NativePathSegmentKind.Line, new(0, 14), new(0, 0))
        ];
        NativeGlyphOutline[] outlines = [new(0, 4, new(0, 0), new(18, 14), 1)];
        NativePositionedGlyph[] glyphs =
            [new(0, new(30, 50), Vector2.UnitX, Vector2.UnitY, new(1, 0, 0, 1))];
        uint revision = 20;

        void Check(string label, NativeGlyphOutline[] nextOutlines,
            NativePathSegment[] nextSegments, NativePositionedGlyph[] nextGlyphs,
            float dpi, bool reuse)
        {
            var seed = renderer.RenderGlyphs(target, 1, outlines, segments, glyphs,
                Vector4.Zero, contentRevision: revision++);
            var stable = renderer.RenderGlyphs(target, 1, outlines, segments, glyphs,
                Vector4.Zero, contentRevision: revision - 1);
            if (stable.RasterizedGlyphCount != 0 || stable.OutlineUploadBytes != 0 ||
                stable.CoverageStagingBytes != 0 || stable.InstanceUploadBytes != 0)
            {
                throw new InvalidOperationException("Stable glyph replay uploaded retained resources.");
            }
            var changed = renderer.RenderGlyphs(target, dpi, nextOutlines,
                nextSegments, nextGlyphs, Vector4.Zero, contentRevision: revision++);
            if (reuse
                ? changed.RasterizedGlyphCount != 0 || changed.OutlineUploadBytes != 0 ||
                  changed.CoverageStagingBytes != 0 ||
                  changed.AtlasGeneration != seed.AtlasGeneration ||
                  changed.InstanceUploadBytes == 0
                : changed.RasterizedGlyphCount != nextOutlines.Length)
            {
                throw new InvalidOperationException($"Glyph raster retention failed: {label}: {changed}.");
            }
            byte[] retained = target.ReadPixels();
            var fresh = renderer.RenderGlyphs(target, dpi, nextOutlines,
                nextSegments, nextGlyphs, Vector4.Zero, contentRevision: 0);
            byte[] reference = target.ReadPixels();
            if (fresh.RasterizedGlyphCount != nextOutlines.Length ||
                !retained.AsSpan().SequenceEqual(reference))
            {
                throw new InvalidOperationException($"Glyph raster pixels differ from uncached rendering: {label}.");
            }
            if (nextGlyphs.Length != 0 && !reference.Any(static value => value != 0))
            {
                throw new InvalidOperationException($"Glyph raster qualification drew no ink: {label}.");
            }
        }

        Check("revision", outlines, segments, glyphs, 1, reuse: true);
        NativePositionedGlyph[] moved =
            [new(0, new(47, 58), Vector2.UnitX, Vector2.UnitY, new(0, 1, 0, 0.75f))];
        Check("placement and paint", outlines, segments, moved, 1, reuse: true);
        Check("instance count", outlines, segments, [glyphs[0], moved[0]], 1, reuse: true);
        Check("DPI", outlines, segments, glyphs, 2, reuse: false);
        Check("phase", [new(0, 4, new(0, 0), new(18, 14), 1, 0.25f)],
            segments, glyphs, 1, reuse: false);
        Check("scale", [new(0, 4, new(0, 0), new(18, 14), 1.5f)],
            segments, glyphs, 1, reuse: false);
        Check("bounds", [new(0, 4, new(-1, -1), new(19, 15), 1)],
            segments, glyphs, 1, reuse: false);
        NativePathSegment[] changedSegments = (NativePathSegment[])segments.Clone();
        changedSegments[1] = new(NativePathSegmentKind.Quadratic,
            new(12, 0), new(10, 7), new(12, 14));
        Check("segment bytes", outlines, changedSegments, glyphs, 1, reuse: false);
        Check("empty", [], [], [], 1, reuse: false);
        Check("after empty", outlines, segments, moved, 1, reuse: true);
        bool rejected = false;
        try
        {
            renderer.RenderGlyphs(target, 1,
                [new(0, 4, new(0, 0), new(18, 14), float.NaN)],
                segments, glyphs, Vector4.Zero, contentRevision: revision++);
        }
        catch (NativeRendererException error) when (error.Status == NativeRendererStatus.InvalidArgument)
        {
            rejected = true;
        }
        if (!rejected)
        {
            throw new InvalidOperationException("Malformed raster input was accepted by the retained cache.");
        }
        Check("after rejected input", outlines, segments, moved, 1, reuse: true);
        Console.Error.WriteLine("Glyph raster retention: 11 exact uncached pixel comparisons, stable replay and rejected-input recovery passed.");
        VerifyExactRasterSharing(renderer, target, segments, rasterizationPath);
    }

    private static void VerifyExactRasterSharing(NativeCompositor renderer, GpuTexture target,
        NativePathSegment[] seedSegments, GpuComputeExecutionPath rasterizationPath)
    {
        NativePositionedGlyph[] glyphs =
        [
            new(0, new(30, 50), Vector2.UnitX, Vector2.UnitY, new(1, 0, 0, 0.7f)),
            new(1, new(37, 52), Vector2.UnitX, Vector2.UnitY, new(0, 1, 0, 0.6f)),
            new(2, new(44, 54), Vector2.UnitX, Vector2.UnitY, new(0, 0, 1, 0.8f)),
            new(0, new(60, 60), Vector2.UnitX, Vector2.UnitY, new(1, 1, 0, 0.5f))
        ];
        for (int variant = 0; variant < 6; variant++)
        {
            NativePathSegment[] segments = [.. seedSegments, .. seedSegments, .. seedSegments];
            NativeGlyphOutline[] outlines =
            [
                new(0, 4, new(0, 0), new(18, 14), 1),
                new(4, 4, new(0, 0), new(18, 14), 1),
                new(8, 4, new(0, 0), new(18, 14), 1)
            ];
            if (variant == 1) outlines[1] = new(4, 4, new(0, 0), new(18, 14), 1, 0.25f);
            if (variant == 2) outlines[1] = new(4, 4, new(0, 0), new(18, 14), 1.25f);
            if (variant == 3) outlines[1] = new(4, 4, new(-1, 0), new(18, 14), 1);
            if (variant == 4) segments[4] = new(NativePathSegmentKind.Line,
                seedSegments[0].P0, seedSegments[0].P1, new(100, 0));
            if (variant == 5) outlines[2] = outlines[0];
            uint expectedRasters = variant is 0 or 5 ? 1U : 2U;
            uint revision = (uint)(100 + variant * 2);
            var actual = renderer.RenderGlyphs(target, 1, outlines, segments, glyphs,
                Vector4.Zero, contentRevision: revision);
            byte[] pixels = target.ReadPixels();
            var warm = renderer.RenderGlyphs(target, 1, outlines, segments, glyphs,
                Vector4.Zero, contentRevision: revision);
            bool usesStaging = rasterizationPath != GpuComputeExecutionPath.RasterShader;
            if (actual.RasterizedGlyphCount != expectedRasters ||
                (usesStaging ? actual.CoverageStagingBytes == 0 : actual.CoverageStagingBytes != 0) ||
                warm.RasterizedGlyphCount != 0 || warm.CoverageStagingBytes != 0 ||
                warm.OutlineUploadBytes != 0 || warm.InstanceUploadBytes != 0 ||
                !pixels.AsSpan().SequenceEqual(target.ReadPixels()))
                throw new InvalidOperationException($"Exact glyph raster sharing work/retention failed: {variant}.");

            // Independent raster jobs: distinct unused LINE control points change
            // byte identity without changing the mathematical coverage predicate.
            NativePathSegment[] independentSegments = (NativePathSegment[])segments.Clone();
            NativeGlyphOutline[] independentOutlines = (NativeGlyphOutline[])outlines.Clone();
            for (int index = 0; index < 3; index++)
            {
                var outline = outlines[index];
                independentOutlines[index] = new((nuint)(index * 4), 4, outline.Minimum,
                    outline.Maximum, outline.RasterScale, outline.SubpixelX);
                var line = independentSegments[index * 4];
                independentSegments[index * 4] = new(line.Kind, line.P0, line.P1, new(200 + index, 0));
            }
            var independent = renderer.RenderGlyphs(target, 1, independentOutlines,
                independentSegments, glyphs, Vector4.Zero, contentRevision: 0);
            if (independent.RasterizedGlyphCount != 3 ||
                (!usesStaging && independent.CoverageStagingBytes != 0) ||
                !pixels.AsSpan().SequenceEqual(target.ReadPixels()) ||
                !pixels.Any(static value => value != 0) ||
                (expectedRasters == 1 && independent.CoverageStagingBytes != actual.CoverageStagingBytes * 3))
                throw new InvalidOperationException($"Exact glyph raster sharing changed independent pixels: {variant}.");
            if (variant == 0)
            {
                NativeGlyphOutline[] invalid = (NativeGlyphOutline[])outlines.Clone();
                invalid[2] = new(8, 4, new(0, 0), new(18, 14), float.NaN);
                bool rejected = false;
                try
                {
                    renderer.RenderGlyphs(target, 1, invalid, segments, glyphs,
                        Vector4.Zero, contentRevision: 0);
                }
                catch (NativeRendererException error) when (error.Status == NativeRendererStatus.InvalidArgument)
                {
                    rejected = true;
                }
                var recovered = renderer.RenderGlyphs(target, 1, outlines, segments,
                    glyphs, Vector4.Zero, contentRevision: 0);
                if (!rejected || recovered.RasterizedGlyphCount != 1 ||
                    !pixels.AsSpan().SequenceEqual(target.ReadPixels()))
                    throw new InvalidOperationException("Glyph sharing admitted invalid late input or stale recovery.");
            }
        }
        Console.Error.WriteLine("Glyph raster sharing: 6 independent exact-pixel comparisons and exact unique-job counts passed.");
    }
}

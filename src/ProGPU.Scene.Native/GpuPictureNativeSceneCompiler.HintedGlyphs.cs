using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Backend.Native;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Scene.Native;

public static partial class GpuPictureNativeSceneCompiler
{
    private static bool TryAppendHintedGlyphs(
        GpuPicture picture, in RenderCommand command, Matrix3x2 transform,
        NativePictureCompileOptions options, StateSnapshot currentState,
        List<StateSnapshot> states, List<StateMaskProgram> stateMasks,
        ulong sceneId, ulong generation, PictureMaskCompileContext pictureMaskContext,
        NativeCompiledPicture.SourceTransaction sourceTransaction,
        List<NativeAnalyticPrimitive> analytics, List<uint> analyticBrushIndices,
        List<NativeGeometryPrimitive> geometry, List<uint> geometryBrushIndices,
        List<NativeSceneGlyphOutline> outlines, List<NativePathSegment> segments,
        List<NativePositionedGlyph> glyphs, List<NativeSceneTextStyle> styles,
        List<ExternalImageDraw> externalImages, List<Batch> batches,
        List<Operation> operations, NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        if (!HintedGlyphCommandGeometry.TryValidate(command, options.DpiScale, transform,
                out int start, out int count))
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        if (command.Brush is null || !float.IsFinite(command.Brush.Opacity) ||
            command.Brush.Opacity is < 0f or > 1f)
        {
            error = NativePictureCompileError.UnsupportedBrush;
            return false;
        }
        if (command.TextRenderingMode is not (TextRenderingMode.Grayscale or TextRenderingMode.Aliased))
        {
            error = NativePictureCompileError.InvalidArgument;
            return false;
        }
        try
        {
            if (!sourceTransaction.TryRetain(command.HintedGlyphGeometry!))
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
            if (!TryPrepareHintedGlyphDraw(command, transform, start, count,
                    out NativeSceneGlyphOutline[] drawOutlines,
                    out NativePathSegment[] drawSegments,
                    out NativePositionedGlyph[] drawGlyphs,
                    out NativeImageRect bounds,
                    out Rect storageBounds))
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
            if (drawGlyphs.Length == 0) return true;
            if (command.Brush is SolidColorBrush solid)
            {
                if (!IsFinite(solid.Color))
                {
                    error = NativePictureCompileError.UnsupportedBrush;
                    return false;
                }
                Vector4 color = solid.Color;
                color.W *= solid.Opacity;
                uint styleIndex = RegisterTextStyle(styles, color,
                    ToNativeTextRenderingMode(command.TextRenderingMode));
                int outlineStart = outlines.Count, segmentStart = segments.Count, glyphStart = glyphs.Count;
                outlines.AddRange(drawOutlines);
                segments.AddRange(drawSegments);
                glyphs.AddRange(drawGlyphs);
                batches.Add(new Batch
                {
                    Kind = BatchKind.Glyph, Start = outlineStart, Count = drawOutlines.Length,
                    AuxiliaryStart = segmentStart, AuxiliaryCount = drawSegments.Length,
                    SecondaryStart = glyphStart, SecondaryCount = drawGlyphs.Length,
                    Bounds = bounds, StyleIndex = styleIndex
                });
                operations.Add(new Operation(OperationKind.Draw, batches.Count - 1));
                return true;
            }

            // Canonical spatial-brush route: one original white glyph scene
            // owns coverage, followed by one original rectangle/image paint.
            // Source Rect remains the caller's ink/domain metadata. Private
            // storage and paint extents cover the canonical padded raster quad.
            // This is the same route used by append_brushed_glyph_run_core.
            if (!pictureMaskContext.TryAllocate(sceneId, out ulong maskSceneId, out _) ||
                !TryBuildHintedCoveragePicture(drawOutlines, drawSegments, drawGlyphs,
                    bounds, command.TextRenderingMode, options.DpiScale, maskSceneId, generation,
                    out NativeCompiledPicture? coverage))
            {
                error = NativePictureCompileError.CapacityExceeded;
                return false;
            }
            // White coverage owns only copied GPU bytes; the parent transaction
            // owns the original generation. Dispose the candidate on every path.
            using NativeCompiledPicture coverageCandidate = coverage!;
            var mask = new NativeSceneLayerPictureMask(0, checked((uint)coverage!.Length),
                bounds, Matrix3x2.Identity);
            if (!TryAppendHintedCoverageMask(currentState, stateMasks, mask, coverage,
                    out StateSnapshot paintState, out error))
                return false;
            int stateIndex = states.Count;
            states.Add(paintState);
            operations.Add(new Operation(OperationKind.Save, StateIndex: stateIndex));
            RenderCommand paint = command;
            paint.Type = RenderCommandType.DrawRect;
            paint.Rect = storageBounds;
            paint.Pen = null;
            paint.IsEdgeAliased = true; // The white glyph mask alone supplies coverage.
            bool appended = paint.Brush is GpuTextureBrush textureBrush
                ? TryAppendHintedTexturePaint(picture, textureBrush, command.Rect, storageBounds, transform,
                    externalImages, batches, operations, options, out error)
                : TryAppendAnalyticPrimitive(paint, NativeAnalyticPrimitiveKind.Rectangle,
                    paint.Rect, 0f, transform, analytics, analyticBrushIndices,
                    geometry, geometryBrushIndices, batches, operations, materials, out error);
            if (!appended) return false;
            operations.Add(new Operation(OperationKind.Restore));
            return true;
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            error = NativePictureCompileError.CapacityExceeded;
            return false;
        }
    }

    private static bool TryPrepareHintedGlyphDraw(in RenderCommand command,
        Matrix3x2 transform, int start, int count,
        out NativeSceneGlyphOutline[] outlines, out NativePathSegment[] segments,
        out NativePositionedGlyph[] glyphs, out NativeImageRect bounds,
        out Rect storageBounds)
    {
        outlines = []; segments = []; glyphs = []; bounds = default; storageBounds = default;
        HintedGlyphGeometry retained = command.HintedGlyphGeometry!;
        ReadOnlySpan<HintedGlyphOccurrence> owners = retained.RenderOccurrences;
        int inkCount = 0;
        for (int i = start; i < start + count; i++)
            if (owners[i].OutlineIndex != uint.MaxValue) inkCount++;
        if (inkCount == 0) return true;
        if (!HintedGlyphCommandGeometry.TryGetRasterBounds(command, out storageBounds, out bool hasInk) || !hasInk)
            return false;
        ReadOnlySpan<GpuGlyphRecord> sourceOutlines = retained.RenderOutlines;
        ReadOnlySpan<GpuSegment> sourceSegments = retained.RenderSegments;
        outlines = new NativeSceneGlyphOutline[sourceOutlines.Length];
        segments = new NativePathSegment[sourceSegments.Length];
        glyphs = new NativePositionedGlyph[inkCount];
        for (int i = 0; i < outlines.Length; i++)
        {
            ref readonly GpuGlyphRecord o = ref sourceOutlines[i];
            outlines[i] = new NativeSceneGlyphOutline(o.StartSegment, o.SegmentCount,
                new Vector2(o.MinX, o.MinY), new Vector2(o.MaxX, o.MaxY), 1f, 0f);
        }
        for (int i = 0; i < segments.Length; i++)
        {
            ref readonly GpuSegment s = ref sourceSegments[i];
            segments[i] = new NativePathSegment(s.P0, s.P1, s.P2, s.P3,
                (NativePathSegmentKind)s.SegmentType, s.Pad0, s.Pad1, s.Pad2);
        }
        int destination = 0;
        Vector2 translation = new(transform.M31, transform.M32);
        for (int i = start; i < start + count; i++)
        {
            ref readonly HintedGlyphOccurrence owner = ref owners[i];
            if (owner.OutlineIndex == uint.MaxValue) continue;
            Vector2 origin = owner.Position + command.Position + translation;
            if (!IsFinite(origin)) return false;
            // Original physical Y-up outline bytes are untouched. Native glyph
            // execution flips Y exactly once; no second snap or raster scale.
            glyphs[destination++] = new NativePositionedGlyph(owner.OutlineIndex,
                origin, Vector2.UnitX, Vector2.UnitY, Vector4.One, 1f);
        }
        bounds = TransformBounds(storageBounds, transform);
        return float.IsFinite(bounds.X) && float.IsFinite(bounds.Y) &&
            float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height) &&
            float.IsFinite(bounds.X + bounds.Width) && float.IsFinite(bounds.Y + bounds.Height);
    }

    private static bool TryAppendHintedTexturePaint(GpuPicture picture,
        GpuTextureBrush brush, Rect sourceBounds, Rect storageBounds, Matrix3x2 sourceTransform,
        List<ExternalImageDraw> externalImages, List<Batch> batches,
        List<Operation> operations, NativePictureCompileOptions options,
        out NativePictureCompileError error)
    {
        bool originalSnap = brush.ExtendToFillBounds && brush.SnapToPixels;
        if (!brush.TryCreateTextureCommand(originalSnap ? sourceBounds : storageBounds,
                out RenderCommand textureCommand))
        {
            error = NativePictureCompileError.UnsupportedBrush;
            return false;
        }
        Matrix3x2 transform = sourceTransform;
        if (originalSnap)
        {
            // Native snap_semantic_image_point and the canonical managed image
            // path both round original world endpoints to even. Expanding and
            // then snapping storage would change the original sampling map.
            try
            {
                if (!TexturePaintMapping.TryExtendSnappedTextureCommand(textureCommand,
                        storageBounds, new Matrix4x4(sourceTransform), options.DpiScale,
                        out textureCommand))
                {
                    error = NativePictureCompileError.None;
                    return true; // The original snapped destination has no area.
                }
            }
            catch (InvalidOperationException)
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
        }
        if (!brush.ExtendToFillBounds)
        {
            // The original bounded brush remains bounded by its own authored
            // destination and mapping, not by the tight source ink rectangle.
            // Extended brushes instead extrapolate the ORIGINAL source/dest
            // mapping across storageBounds, retaining sampler addressing.
            // Brush mapping uses Vector2.Transform's six original components,
            // not RenderCommand's default-matrix-as-identity convention. The
            // other Matrix4x4 components do not enter that canonical mapping.
            Matrix4x4 mapping = textureCommand.Transform;
            var brushTransform = new Matrix3x2(mapping.M11, mapping.M12,
                mapping.M21, mapping.M22, mapping.M41, mapping.M42);
            if (!IsFinite(brushTransform))
            {
                error = NativePictureCompileError.UnsupportedTransform;
                return false;
            }
            // Compute exact float-product determinant in double: no epsilon or
            // float underflow may collapse a genuine tiny invertible mapping.
            double determinant = (double)brushTransform.M11 * brushTransform.M22 -
                (double)brushTransform.M12 * brushTransform.M21;
            if (determinant == 0d)
            {
                error = NativePictureCompileError.None;
                return true; // Original finite singular destination draws no ink.
            }
            transform = brushTransform * sourceTransform;
            if (!IsFinite(transform))
            {
                error = NativePictureCompileError.UnsupportedTransform;
                return false;
            }
            // ExtendToFillBounds controls paint extent, not sampler admission.
            // Preserve explicitly authored addressing/out-of-image source
            // coordinates through the existing native extended-source wire
            // contract. This decision never derives from raster storage padding.
            Rect source = textureCommand.SrcRect;
            if (textureCommand.TextureAddressModeU != TextureAddressMode.Clamp ||
                textureCommand.TextureAddressModeV != TextureAddressMode.Clamp ||
                source.X < 0f || source.Y < 0f ||
                source.Right > textureCommand.Texture!.Width ||
                source.Bottom > textureCommand.Texture.Height)
                textureCommand.AllowExtendedTextureSourceRect = true;
        }
        return TryAppendExternalImage(picture, textureCommand, transform, options.DpiScale,
            externalImages, batches, operations, out error);
    }

    private static bool TryAppendHintedCoverageMask(StateSnapshot current,
        List<StateMaskProgram> programs, in NativeSceneLayerPictureMask mask,
        NativeCompiledPicture coverage, out StateSnapshot next,
        out NativePictureCompileError error)
    {
        next = current; error = NativePictureCompileError.None;
        StateMaskProgram program;
        if (current.MaskIndex < 0)
            program = new StateMaskProgram(mask, coverage);
        else
        {
            StateMaskProgram active = programs[current.MaskIndex];
            var node = new PictureMaskNode(active.PictureMasks, mask, coverage);
            uint components = checked((uint)(active.BrushMasks?.Count ?? 0) +
                (uint)(active.GeometryMasks?.Count ?? 0) + (uint)node.Count +
                (active.VectorMask is null ? 0U : 1U));
            if (components > NativeSceneLayerCompositeMask.MaximumComponentCount)
            {
                error = NativePictureCompileError.CapacityExceeded;
                return false;
            }
            if (active.Kind == StateMaskProgramKind.Analytic &&
                !TryCompileVectorMaskChain(active.VectorMask!, out error))
                return false;
            program = new StateMaskProgram(active.VectorMask, active.BrushMasks,
                active.GeometryMasks, node);
        }
        int maskIndex = programs.Count;
        programs.Add(program);
        next = current with { MaskIndex = maskIndex };
        return true;
    }

    private static bool TryBuildHintedCoveragePicture(NativeSceneGlyphOutline[] outlines,
        NativePathSegment[] segments, NativePositionedGlyph[] glyphs,
        NativeImageRect bounds, TextRenderingMode mode, float dpiScale,
        ulong sceneId, ulong generation, out NativeCompiledPicture? picture)
    {
        picture = null;
        int arena = checked(outlines.Length * Unsafe.SizeOf<NativeSceneGlyphOutline>() +
            segments.Length * Unsafe.SizeOf<NativePathSegment>() +
            glyphs.Length * Unsafe.SizeOf<NativePositionedGlyph>() +
            Unsafe.SizeOf<NativeSceneTextStyle>() + NativeSceneGlyphDrawSize + 64);
        byte[] storage = new byte[NativeSceneStreamBuilder.GetRequiredBufferSize(1, 2, arena)];
        var builder = new NativeSceneStreamBuilder(storage, sceneId, generation, 1, 2);
        NativeSceneTextStyle[] styles = [new NativeSceneTextStyle(Vector4.One,
            mode == TextRenderingMode.Aliased ? NativeSceneTextRenderingMode.Aliased :
                NativeSceneTextRenderingMode.Grayscale)];
        if (!builder.TryAddGlyphResource(1, generation, outlines, segments, out uint resourceIndex) ||
            !builder.TryAddTextStyleResource(2, generation, styles, out uint styleIndex) ||
            !builder.TryDrawGlyphRun(1, resourceIndex, bounds, glyphs, styleIndex, 0) ||
            !builder.TryBuild(out ReadOnlySpan<byte> stream))
            return false;
        picture = new NativeCompiledPicture(storage, stream.Length, sceneId, generation, dpiScale,
            sourceCommandCount: 1, nativeCommandCount: 1, nativeDrawCount: 1,
            analyticPrimitiveCount: 0, geometryPrimitiveCount: 0, pathCount: 0, pathSegmentCount: 0,
            pointBatchCount: 0, pointCount: 0, vertexMeshCount: 0, meshVertexCount: 0, meshIndexCount: 0,
            strokeCount: 0, strokePointCount: 0, strokeDoubleCount: 0,
            glyphOutlineCount: outlines.Length, glyphSegmentCount: segments.Length,
            colorGlyphBitmapCount: 0, colorGlyphPixelBytes: 0, positionedGlyphCount: glyphs.Length,
            textStyleCount: 1, line3DCount: 0, brushCount: 0, gradientStopCount: 0, externalImages: []);
        return true;
    }
}

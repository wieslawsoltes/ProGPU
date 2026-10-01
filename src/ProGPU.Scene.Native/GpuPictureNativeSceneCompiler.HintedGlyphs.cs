using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Scene.Native;

public static partial class GpuPictureNativeSceneCompiler
{
    private static bool TryAppendHintedGlyphs(
        GpuPicture picture, in RenderCommand command, Matrix3x2 transform,
        NativePictureCompileOptions options, StateSnapshot currentState,
        NativeCompiledPicture.SourceTransaction sourceTransaction,
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
                    out _))
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

            NativeSceneGlyphPaint paint;
            int imageIndex = -1;
            if (command.Brush is GpuTextureBrush textureBrush)
            {
                if (!TryCreateHintedTexturePaint(picture, textureBrush, command.Rect, transform,
                        options.DpiScale, out paint, out ExternalImageDraw image, out bool hasPaint, out error))
                    return false;
                if (!hasPaint) return true;
                imageIndex = externalImages.Count;
                externalImages.Add(image);
            }
            else
            {
                if (!materials.TryRegister(command.Brush, out uint brushIndex, out error)) return false;
                paint = new NativeSceneGlyphPaint(NativeSceneGlyphPaint.Material, brushIndex, 0,
                    new Vector4(transform.M31, transform.M32, 0f, 0f));
            }
            int outlineBase = outlines.Count, segmentBase = segments.Count, glyphBase = glyphs.Count;
            outlines.AddRange(drawOutlines);
            segments.AddRange(drawSegments);
            glyphs.AddRange(drawGlyphs);
            batches.Add(new Batch
            {
                Kind = BatchKind.GlyphPaint, Start = outlineBase, Count = drawOutlines.Length,
                AuxiliaryStart = segmentBase, AuxiliaryCount = drawSegments.Length,
                SecondaryStart = glyphBase, SecondaryCount = drawGlyphs.Length,
                Bounds = bounds, GlyphPaint = paint, PaintImageIndex = imageIndex,
                GlyphPaintMode = ToNativeTextRenderingMode(command.TextRenderingMode)
            });
            // Ordinary paint keeps the original instance batch. Only a
            // destination-reading composite needs a command per occurrence.
            if (HintedPaintRequiresDestinationComposite(currentState.BlendMode))
                for (int index = 0; index < drawGlyphs.Length; index++)
                    operations.Add(new Operation(OperationKind.Draw, batches.Count - 1,
                        PositionedGlyphIndex: checked(glyphBase + index)));
            else operations.Add(new Operation(OperationKind.Draw, batches.Count - 1));
            return true;
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            error = NativePictureCompileError.CapacityExceeded;
            return false;
        }
    }

    private static bool HintedPaintRequiresDestinationComposite(GpuBlendMode blendMode) => blendMode is
        GpuBlendMode.Multiply or GpuBlendMode.Screen or GpuBlendMode.Darken or GpuBlendMode.Lighten or
        GpuBlendMode.Exclusion or GpuBlendMode.Overlay or GpuBlendMode.ColorDodge or GpuBlendMode.ColorBurn or
        GpuBlendMode.HardLight or GpuBlendMode.SoftLight or GpuBlendMode.Difference or GpuBlendMode.Hue or
        GpuBlendMode.Saturation or GpuBlendMode.Color or GpuBlendMode.Luminosity;

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

    private static bool TryCreateHintedTexturePaint(GpuPicture picture,
        GpuTextureBrush brush, Rect sourceBounds, Matrix3x2 sourceTransform, float dpiScale,
        out NativeSceneGlyphPaint paint, out ExternalImageDraw image, out bool hasPaint,
        out NativePictureCompileError error)
    {
        paint = default; image = default; hasPaint = false;
        if (!brush.TryCreateTextureCommand(sourceBounds, out RenderCommand textureCommand))
        {
            error = NativePictureCompileError.UnsupportedBrush;
            return false;
        }
        Matrix3x2 transform = sourceTransform;
        if (!brush.ExtendToFillBounds)
        {
            Matrix4x4 mapping = textureCommand.Transform;
            var brushTransform = new Matrix3x2(mapping.M11, mapping.M12,
                mapping.M21, mapping.M22, mapping.M41, mapping.M42);
            if (!IsFinite(brushTransform))
            {
                error = NativePictureCompileError.UnsupportedTransform;
                return false;
            }
            // Retain the original exact singular no-ink policy, never epsilon.
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
            Rect source = textureCommand.SrcRect;
            if (textureCommand.TextureAddressModeU != TextureAddressMode.Clamp ||
                textureCommand.TextureAddressModeV != TextureAddressMode.Clamp ||
                source.X < 0f || source.Y < 0f ||
                source.Right > textureCommand.Texture!.Width ||
                source.Bottom > textureCommand.Texture.Height)
                textureCommand.AllowExtendedTextureSourceRect = true;
        }
        Rect destination = textureCommand.Rect;
        Vector2 p0 = Vector2.Transform(new Vector2(destination.X, destination.Y), transform);
        Vector2 p1 = Vector2.Transform(new Vector2(destination.Right, destination.Y), transform);
        Vector2 p2 = Vector2.Transform(new Vector2(destination.Right, destination.Bottom), transform);
        Vector2 p3 = Vector2.Transform(new Vector2(destination.X, destination.Bottom), transform);
        if (textureCommand.SnapTextureToPixels)
        {
            p0 = TexturePaintMapping.SnapPoint(p0, dpiScale); p1 = TexturePaintMapping.SnapPoint(p1, dpiScale);
            p2 = TexturePaintMapping.SnapPoint(p2, dpiScale); p3 = TexturePaintMapping.SnapPoint(p3, dpiScale);
        }
        if (!IsFinite(p0) || !IsFinite(p1) || !IsFinite(p2) || !IsFinite(p3))
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        if (brush.ExtendToFillBounds && (p0.X == p2.X || p0.Y == p2.Y))
        {
            error = NativePictureCompileError.None;
            return true; // The original snapped map collapsed, not its storage.
        }

        // Reuse the original IMAGE validator/binding metadata without publishing
        // an image draw, mask scene or mutable producer owner to the real stream.
        var images = new List<ExternalImageDraw>();
        var imageBatches = new List<Batch>();
        var imageOperations = new List<Operation>();
        if (!TryAppendExternalImage(picture, textureCommand, transform, dpiScale,
                images, imageBatches, imageOperations, out error)) return false;
        image = images[0];
        GpuTexture texture = image.Texture;
        uint flags = (uint)textureCommand.TextureSamplingMode << NativeSceneGlyphPaint.SamplingModeShift;
        if (texture.AlphaMode == GpuTextureAlphaMode.Premultiplied) flags |= NativeSceneGlyphPaint.PremultipliedTexture;
        if (!brush.ExtendToFillBounds) flags |= NativeSceneGlyphPaint.BoundedTexture;
        if (textureCommand.TextureSamplingMode == TextureSamplingMode.Cubic) flags |= NativeSceneGlyphPaint.CubicTexture;
        Rect sourceRect = textureCommand.SrcRect;
        paint = new NativeSceneGlyphPaint(NativeSceneGlyphPaint.Texture, 0, flags,
            new Vector4(sourceTransform.M31, sourceTransform.M32, textureCommand.TextureOpacity, 0f),
            new Vector4(sourceRect.X / texture.Width, sourceRect.Y / texture.Height,
                sourceRect.Right / texture.Width, sourceRect.Bottom / texture.Height),
            new Vector4(p0, p1.X, p1.Y), new Vector4(p2, p3.X, p3.Y),
            new Vector4(0f, .5f, (float)textureCommand.TextureAddressModeU, (float)textureCommand.TextureAddressModeV));
        hasPaint = true;
        return true;
    }
}

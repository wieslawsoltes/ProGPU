using System.Numerics;
using ProGPU.Backend.Native;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Scene.Native;

public static partial class GpuPictureNativeSceneCompiler
{
    private const float MinimumGlyphAtlasRasterSize = 4f;
    private const float MaximumGlyphAtlasRasterSize = 128f;
    private const float GlyphBoundsPadding = 4f;
    private const float VectorGlyphPhaseCount = 128f;

    private readonly record struct GlyphOutlineKey(
        ushort GlyphIndex,
        uint RasterScaleBits,
        uint SubpixelBits);

    /// <summary>
    /// Lowers an already-shaped managed glyph run without repeating character
    /// mapping or OpenType shaping. The source of truth is the immutable glyph
    /// index and position arrays recorded by ProGPU's managed text stack.
    /// </summary>
    private static bool TryAppendGlyphRun(
        in RenderCommand command,
        Matrix3x2 transform,
        float targetDpiScale,
        List<NativeScenePathFill> paths,
        List<NativePathSegment> pathSegments,
        List<uint> pathBrushIndices,
        List<NativeSceneGlyphOutline> outlines,
        List<NativePathSegment> segments,
        List<NativePositionedGlyph> glyphs,
        List<NativeSceneTextStyle> styles,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        TtfFont? font = command.Font;
        ushort[]? glyphIndices = command.GlyphIndices;
        Vector2[]? glyphPositions = command.GlyphPositions;
        if (font is null || glyphIndices is null || glyphPositions is null ||
            font.UnitsPerEm == 0 || !float.IsFinite(command.FontSize) ||
            command.FontSize <= 0f || !float.IsFinite(targetDpiScale) ||
            targetDpiScale <= 0f || !IsFinite(command.Position))
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        if (command.Brush is not SolidColorBrush solid ||
            !IsFinite(solid.Color) || !float.IsFinite(solid.Opacity) ||
            solid.Opacity is < 0f or > 1f)
        {
            error = NativePictureCompileError.UnsupportedBrush;
            return false;
        }
        if (command.TextRenderingMode is < TextRenderingMode.Grayscale or
                > TextRenderingMode.ClearType ||
            command.TextHintingMode is < TextHintingMode.Auto or
                > TextHintingMode.Animated)
        {
            error = NativePictureCompileError.UnsupportedCommand;
            return false;
        }

        int rangeStart = command.GlyphRangeCount > 0
            ? command.GlyphRangeStart
            : 0;
        int rangeCount = command.GlyphRangeCount > 0
            ? command.GlyphRangeCount
            : Math.Min(glyphIndices.Length, glyphPositions.Length);
        if (rangeStart < 0 || rangeCount <= 0 ||
            rangeStart > glyphIndices.Length - rangeCount ||
            rangeStart > glyphPositions.Length - rangeCount)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }

        Matrix3x2 activeTransform = transform;
        if (MathF.Abs(command.Rotation) > 0.0001f)
        {
            if (!float.IsFinite(command.Rotation))
            {
                error = NativePictureCompileError.UnsupportedTransform;
                return false;
            }
            activeTransform =
                Matrix3x2.CreateTranslation(-command.Position) *
                Matrix3x2.CreateRotation(command.Rotation) *
                Matrix3x2.CreateTranslation(command.Position) *
                transform;
        }
        if (!TransformMetrics.TryGetStrokeScale(
                ToMatrix4x4(activeTransform),
                out float transformScale))
        {
            error = NativePictureCompileError.UnsupportedTransform;
            return false;
        }

        float fontScaleX = command.HasFontTransform
            ? command.FontTransform.X
            : 1f;
        float fontSkewX = command.HasFontTransform
            ? command.FontTransform.Y
            : 0f;
        if (!float.IsFinite(fontScaleX) ||
            MathF.Abs(fontScaleX) <= 0.000001f ||
            !float.IsFinite(fontSkewX))
        {
            error = NativePictureCompileError.UnsupportedTransform;
            return false;
        }

        float targetRasterSize = Math.Clamp(
            command.FontSize * targetDpiScale * transformScale,
            MinimumGlyphAtlasRasterSize,
            MaximumGlyphAtlasRasterSize);
        float rasterScale = targetRasterSize / font.UnitsPerEm;
        float atlasToLogicalScale =
            command.FontSize * targetDpiScale / targetRasterSize;
        float italicSkew = (command.IsItalic ? 0.22f : 0f) - fontSkewX;
        float nativeItalicSkew = italicSkew / fontScaleX;
        float boldOffset = command.FontSize * 0.035f;
        Vector2 basisX = new(
            activeTransform.M11 * fontScaleX,
            activeTransform.M12 * fontScaleX);
        Vector2 basisY = new(activeTransform.M21, activeTransform.M22);
        bool transformedPlacement =
            MathF.Abs(activeTransform.M12) > 0.0001f ||
            MathF.Abs(activeTransform.M21) > 0.0001f ||
            activeTransform.M11 < 0f || activeTransform.M22 < 0f ||
            MathF.Abs(fontSkewX) > 0.0001f || fontScaleX < 0f;

        var localOutlines = new List<NativeSceneGlyphOutline>();
        var localSegments = new List<NativePathSegment>();
        var localGlyphs = new List<NativePositionedGlyph>(
            checked(rangeCount * (command.IsBold ? 2 : 1)));
        var outlineIndices = new Dictionary<GlyphOutlineKey, uint>();
        NativeImageRect bounds = default;
        bool hasBounds = false;
        TextRenderingMode textRenderingMode = command.TextRenderingMode;
        Vector4 glyphStyleColor = solid.Color;
        glyphStyleColor.W *= solid.Opacity;

        bool FlushGlyphChunk()
        {
            if (localGlyphs.Count == 0)
            {
                return true;
            }

            uint styleIndex = RegisterTextStyle(
                styles,
                glyphStyleColor,
                ToNativeTextRenderingMode(textRenderingMode));
            int outlineStart = outlines.Count;
            int segmentBase = segments.Count;
            int glyphStart = glyphs.Count;
            outlines.AddRange(localOutlines);
            segments.AddRange(localSegments);
            glyphs.AddRange(localGlyphs);
            batches.Add(new Batch
            {
                Kind = BatchKind.Glyph,
                Start = outlineStart,
                Count = localOutlines.Count,
                AuxiliaryStart = segmentBase,
                AuxiliaryCount = localSegments.Count,
                SecondaryStart = glyphStart,
                SecondaryCount = localGlyphs.Count,
                Bounds = bounds,
                StyleIndex = styleIndex
            });
            operations.Add(new Operation(OperationKind.Draw, batches.Count - 1));
            localOutlines.Clear();
            localSegments.Clear();
            localGlyphs.Clear();
            outlineIndices.Clear();
            bounds = default;
            hasBounds = false;
            return true;
        }

        int rangeEnd = rangeStart + rangeCount;
        for (int sourceIndex = rangeStart; sourceIndex < rangeEnd; sourceIndex++)
        {
            ushort glyphIndex = glyphIndices[sourceIndex];
            Vector2 sourcePosition = glyphPositions[sourceIndex];
            if (!IsFinite(sourcePosition))
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
            List<FontColorLayer>? colorLayers = font.HasColorGlyphs
                ? font.GetColorLayers(glyphIndex)
                : null;
            if (colorLayers is { Count: > 0 })
            {
                if (!FlushGlyphChunk())
                {
                    return false;
                }
                if (!TryAppendColorGlyphLayers(
                        colorLayers,
                        font,
                        command,
                        sourcePosition,
                        italicSkew,
                        fontScaleX,
                        activeTransform,
                        paths,
                        pathSegments,
                        pathBrushIndices,
                        batches,
                        operations,
                        materials,
                        out error))
                {
                    return false;
                }
                continue;
            }
            if (font.HasBitmapGlyphs &&
                font.TryGetBitmapGlyph(glyphIndex, targetRasterSize, out _))
            {
                error = NativePictureCompileError.UnsupportedCommand;
                return false;
            }
            if (command.UseVectorGlyphRendering ||
                (font.HasCffOutlines && !command.PreferGlyphAtlas))
            {
                if (!FlushGlyphChunk())
                {
                    return false;
                }
                if (!TryAppendVectorGlyph(
                        font,
                        glyphIndex,
                        command,
                        sourcePosition,
                        italicSkew,
                        fontScaleX,
                        activeTransform,
                        paths,
                        pathSegments,
                        pathBrushIndices,
                        batches,
                        operations,
                        materials,
                        out error))
                {
                    return false;
                }
                continue;
            }

            Vector2 transformedPosition = Vector2.Transform(
                sourcePosition + command.Position,
                activeTransform);
            (byte subpixelIndex, Vector2 snappedPosition) = ResolveGlyphPlacement(
                transformedPosition,
                targetDpiScale,
                targetRasterSize,
                transformedPlacement,
                command.TextHintingMode);
            float subpixel = subpixelIndex * 0.25f;
            var key = new GlyphOutlineKey(
                glyphIndex,
                BitConverter.SingleToUInt32Bits(rasterScale),
                BitConverter.SingleToUInt32Bits(subpixel));
            if (!outlineIndices.TryGetValue(key, out uint outlineIndex))
            {
                PathGeometry? outline = font.GetGlyphOutline(glyphIndex);
                if (outline is null)
                {
                    continue;
                }
                (_, GpuPathSegment[] compiledSegments) =
                    PathAtlas.CompileFillPath(
                        outline,
                        out float minimumX,
                        out float minimumY,
                        out float maximumX,
                        out float maximumY);
                if (compiledSegments.Length == 0 ||
                    !float.IsFinite(minimumX) ||
                    !float.IsFinite(minimumY) ||
                    !float.IsFinite(maximumX) ||
                    !float.IsFinite(maximumY) ||
                    maximumX <= minimumX || maximumY <= minimumY)
                {
                    continue;
                }
                int segmentStart = localSegments.Count;
                for (int segmentIndex = 0;
                     segmentIndex < compiledSegments.Length;
                     segmentIndex++)
                {
                    ref readonly GpuPathSegment source =
                        ref compiledSegments[segmentIndex];
                    if (source.SegmentType >
                            (uint)NativePathSegmentKind.Cubic ||
                        !IsFinite(source.P0) || !IsFinite(source.P1) ||
                        !IsFinite(source.P2) || !IsFinite(source.P3) ||
                        source.Pad0 != 0U || source.Pad1 != 0U ||
                        source.Pad2 != 0U)
                    {
                        error = NativePictureCompileError.UnsupportedCommand;
                        return false;
                    }
                    localSegments.Add(new NativePathSegment(
                        (NativePathSegmentKind)source.SegmentType,
                        source.P0,
                        source.P1,
                        source.P2,
                        source.P3));
                }
                outlineIndex = checked((uint)localOutlines.Count);
                outlineIndices.Add(key, outlineIndex);
                localOutlines.Add(new NativeSceneGlyphOutline(
                    checked((ulong)segmentStart),
                    checked((ulong)compiledSegments.Length),
                    new Vector2(minimumX, minimumY),
                    new Vector2(maximumX, maximumY),
                    rasterScale,
                    subpixel));
            }

            NativeSceneGlyphOutline glyphOutline =
                localOutlines[checked((int)outlineIndex)];
            int passCount = command.IsBold ? 2 : 1;
            for (int pass = 0; pass < passCount; pass++)
            {
                float nativeBoldOffset = pass * boldOffset / fontScaleX;
                localGlyphs.Add(new NativePositionedGlyph(
                    outlineIndex,
                    snappedPosition,
                    basisX,
                    basisY,
                    Vector4.One,
                    atlasToLogicalScale,
                    nativeBoldOffset,
                    nativeItalicSkew));
                NativeImageRect glyphBounds = CalculateGlyphBounds(
                    glyphOutline.Minimum,
                    glyphOutline.Maximum,
                    command.FontSize / font.UnitsPerEm,
                    snappedPosition,
                    new Vector2(activeTransform.M11, activeTransform.M12),
                    basisY,
                    fontScaleX,
                    italicSkew,
                    pass * boldOffset,
                    targetDpiScale);
                bounds = hasBounds ? Union(bounds, glyphBounds) : glyphBounds;
                hasBounds = true;
            }
        }

        return FlushGlyphChunk();
    }

    private static bool TryAppendColorGlyphLayers(
        List<FontColorLayer> colorLayers,
        TtfFont font,
        in RenderCommand textCommand,
        Vector2 sourcePosition,
        float italicSkew,
        float scaleX,
        Matrix3x2 activeTransform,
        List<NativeScenePathFill> paths,
        List<NativePathSegment> pathSegments,
        List<uint> pathBrushIndices,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        Vector2 position = sourcePosition + textCommand.Position;
        float emScale = textCommand.FontSize / font.UnitsPerEm;
        for (int layerIndex = 0; layerIndex < colorLayers.Count; layerIndex++)
        {
            FontColorLayer layer = colorLayers[layerIndex];
            PathGeometry? outline =
                layer.Geometry ?? font.GetGlyphOutline(layer.GlyphId);
            if (outline is null)
            {
                continue;
            }

            ResolveVectorGlyphPlacement(
                outline,
                position,
                emScale,
                italicSkew,
                scaleX,
                layer.UsesSvgCoordinates,
                activeTransform,
                out PathGeometry positionedOutline,
                out Matrix3x2 placementTransform,
                out Vector2 brushPosition);
            var pathCommand = new RenderCommand
            {
                Type = RenderCommandType.DrawPath,
                Path = positionedOutline,
                Brush = CreatePositionedColorLayerBrush(
                    layer,
                    emScale,
                    brushPosition),
                IsEdgeAliased =
                    textCommand.TextRenderingMode == TextRenderingMode.Aliased,
                PathSampleGrid =
                    textCommand.TextRenderingMode == TextRenderingMode.Aliased
                        ? PathAtlas.StandardCoverageSampleGrid
                        : PathAtlas.HighPrecisionCoverageSampleGrid
            };
            if (!TryAppendPathFill(
                    pathCommand,
                    placementTransform,
                    paths,
                    pathSegments,
                    pathBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error))
            {
                return false;
            }
        }

        error = NativePictureCompileError.None;
        return true;
    }

    private static bool TryAppendVectorGlyph(
        TtfFont font,
        ushort glyphIndex,
        in RenderCommand textCommand,
        Vector2 sourcePosition,
        float italicSkew,
        float scaleX,
        Matrix3x2 activeTransform,
        List<NativeScenePathFill> paths,
        List<NativePathSegment> pathSegments,
        List<uint> pathBrushIndices,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        PathGeometry? outline = font.GetGlyphOutline(glyphIndex);
        if (outline is null)
        {
            error = NativePictureCompileError.None;
            return true;
        }

        float emScale = textCommand.FontSize / font.UnitsPerEm;
        float boldOffset = textCommand.FontSize * 0.035f;
        int passCount = textCommand.IsBold ? 2 : 1;
        for (int pass = 0; pass < passCount; pass++)
        {
            Vector2 position = sourcePosition + textCommand.Position +
                new Vector2(pass * boldOffset, 0f);
            ResolveVectorGlyphPlacement(
                outline,
                position,
                emScale,
                italicSkew,
                scaleX,
                usesSvgCoordinates: false,
                activeTransform,
                out PathGeometry positionedOutline,
                out Matrix3x2 placementTransform,
                out _);
            var pathCommand = new RenderCommand
            {
                Type = RenderCommandType.DrawPath,
                Path = positionedOutline,
                Brush = textCommand.Brush,
                IsEdgeAliased =
                    textCommand.TextRenderingMode == TextRenderingMode.Aliased,
                PathSampleGrid =
                    textCommand.TextRenderingMode == TextRenderingMode.Aliased
                        ? PathAtlas.StandardCoverageSampleGrid
                        : PathAtlas.HighPrecisionCoverageSampleGrid
            };
            if (!TryAppendPathFill(
                    pathCommand,
                    placementTransform,
                    paths,
                    pathSegments,
                    pathBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error))
            {
                return false;
            }
        }

        error = NativePictureCompileError.None;
        return true;
    }

    private static void ResolveVectorGlyphPlacement(
        PathGeometry outline,
        Vector2 position,
        float emScale,
        float italicSkew,
        float scaleX,
        bool usesSvgCoordinates,
        Matrix3x2 activeTransform,
        out PathGeometry positionedOutline,
        out Matrix3x2 placementTransform,
        out Vector2 brushPosition)
    {
        Vector2 integralPosition = new(
            MathF.Floor(position.X),
            MathF.Floor(position.Y));
        Vector2 fractionalPosition = position - integralPosition;
        Vector2 rasterPhase = new(
            QuantizeVectorGlyphPhase(fractionalPosition.X),
            QuantizeVectorGlyphPhase(fractionalPosition.Y));
        positionedOutline = CreatePositionedGlyphOutline(
            outline,
            emScale,
            rasterPhase,
            italicSkew,
            usesSvgCoordinates,
            scaleX);
        brushPosition = rasterPhase;
        placementTransform = Matrix3x2.CreateTranslation(
            position - rasterPhase) * activeTransform;
    }

    private static PathGeometry CreatePositionedGlyphOutline(
        PathGeometry outline,
        float emScale,
        Vector2 position,
        float italicSkew,
        bool usesSvgCoordinates,
        float scaleX)
    {
        float orientedItalicSkew = usesSvgCoordinates
            ? -italicSkew
            : italicSkew;
        Vector2 TransformPoint(Vector2 point) => new(
            position.X +
                (point.X * scaleX + point.Y * orientedItalicSkew) * emScale,
            position.Y + point.Y * emScale *
                (usesSvgCoordinates ? 1f : -1f));

        var transformedOutline = new PathGeometry { FillRule = outline.FillRule };
        for (int figureIndex = 0;
             figureIndex < outline.Figures.Count;
             figureIndex++)
        {
            PathFigure figure = outline.Figures[figureIndex];
            var transformedFigure = new PathFigure(
                TransformPoint(figure.StartPoint),
                figure.IsClosed)
            {
                IsFilled = figure.IsFilled,
                StrokeStartLineCap = figure.StrokeStartLineCap,
                StrokeEndLineCap = figure.StrokeEndLineCap
            };
            for (int segmentIndex = 0;
                 segmentIndex < figure.Segments.Count;
                 segmentIndex++)
            {
                switch (figure.Segments[segmentIndex])
                {
                    case LineSegment line:
                        transformedFigure.Segments.Add(new LineSegment(
                            TransformPoint(line.Point),
                            line.IsSmoothJoin,
                            line.IsStroked));
                        break;
                    case QuadraticBezierSegment quadratic:
                        transformedFigure.Segments.Add(
                            new QuadraticBezierSegment(
                                TransformPoint(quadratic.ControlPoint),
                                TransformPoint(quadratic.Point),
                                quadratic.IsSmoothJoin,
                                quadratic.IsStroked));
                        break;
                    case CubicBezierSegment cubic:
                        transformedFigure.Segments.Add(new CubicBezierSegment(
                            TransformPoint(cubic.ControlPoint1),
                            TransformPoint(cubic.ControlPoint2),
                            TransformPoint(cubic.Point),
                            cubic.IsSmoothJoin,
                            cubic.IsStroked));
                        break;
                    case ArcSegment arc:
                        transformedFigure.Segments.Add(new ArcSegment(
                            TransformPoint(arc.Point),
                            new Vector2(
                                MathF.Abs(arc.Size.X * emScale * scaleX),
                                MathF.Abs(arc.Size.Y * emScale)),
                            arc.RotationAngle,
                            arc.IsLargeArc,
                            arc.SweepDirection,
                            arc.IsSmoothJoin,
                            arc.IsStroked));
                        break;
                }
            }
            transformedOutline.Figures.Add(transformedFigure);
        }
        return transformedOutline;
    }

    private static Brush CreatePositionedColorLayerBrush(
        FontColorLayer layer,
        float emScale,
        Vector2 position)
    {
        if (layer.Brush is null)
        {
            return new SolidColorBrush(layer.Color);
        }
        if (!layer.UsesSvgCoordinates)
        {
            return layer.Brush;
        }

        Vector2 PositionPoint(Vector2 point) => position + point * emScale;
        return layer.Brush switch
        {
            SolidColorBrush solid => new SolidColorBrush(solid.Color)
            {
                Opacity = solid.Opacity
            },
            LinearGradientBrush linear => new LinearGradientBrush(
                PositionPoint(linear.StartPoint),
                PositionPoint(linear.EndPoint),
                linear.Stops)
            {
                Opacity = linear.Opacity,
                SpreadMethod = linear.SpreadMethod,
                ColorInterpolationMode = linear.ColorInterpolationMode
            },
            RadialGradientBrush radial => new RadialGradientBrush(
                PositionPoint(radial.Center),
                PositionPoint(radial.GradientOrigin),
                radial.RadiusX * emScale,
                radial.RadiusY * emScale,
                radial.Stops)
            {
                Opacity = radial.Opacity,
                SpreadMethod = radial.SpreadMethod,
                ColorInterpolationMode = radial.ColorInterpolationMode
            },
            _ => layer.Brush
        };
    }

    private static float QuantizeVectorGlyphPhase(float value)
    {
        float quantized = MathF.Round(value * VectorGlyphPhaseCount) /
            VectorGlyphPhaseCount;
        return quantized >= 1f ? 0f : quantized;
    }

    private static uint RegisterTextStyle(
        List<NativeSceneTextStyle> styles,
        Vector4 color,
        NativeSceneTextRenderingMode renderingMode)
    {
        for (int index = 0; index < styles.Count; index++)
        {
            NativeSceneTextStyle style = styles[index];
            if (style.Color == color && style.TextRenderingMode == renderingMode)
            {
                return checked((uint)index);
            }
        }
        uint result = checked((uint)styles.Count);
        styles.Add(new NativeSceneTextStyle(color, renderingMode));
        return result;
    }

    private static NativeSceneTextRenderingMode ToNativeTextRenderingMode(
        TextRenderingMode mode) => mode switch
        {
            TextRenderingMode.Aliased => NativeSceneTextRenderingMode.Aliased,
            TextRenderingMode.ClearType => NativeSceneTextRenderingMode.ClearType,
            _ => NativeSceneTextRenderingMode.Grayscale
        };

    private static (byte SubpixelIndex, Vector2 Position) ResolveGlyphPlacement(
        Vector2 transformedPosition,
        float dpiScale,
        float rasterFontSize,
        bool transformedPlacement,
        TextHintingMode hintingMode)
    {
        if (hintingMode == TextHintingMode.Animated)
        {
            return (0, transformedPosition);
        }
        Vector2 physical = transformedPosition * dpiScale;
        if (!transformedPlacement && rasterFontSize <= 24f)
        {
            float integerX = MathF.Floor(physical.X);
            int phase = (int)MathF.Round((physical.X - integerX) * 4f);
            if (phase == 4)
            {
                phase = 0;
                integerX += 1f;
            }
            return (
                checked((byte)phase),
                new Vector2(integerX, MathF.Round(physical.Y)) / dpiScale);
        }
        if (!transformedPlacement)
        {
            return (
                0,
                new Vector2(
                    MathF.Round(physical.X),
                    MathF.Round(physical.Y)) / dpiScale);
        }
        return (0, transformedPosition);
    }

    private static NativeImageRect CalculateGlyphBounds(
        Vector2 minimum,
        Vector2 maximum,
        float emScale,
        Vector2 origin,
        Vector2 basisX,
        Vector2 basisY,
        float fontScaleX,
        float italicSkew,
        float boldOffset,
        float dpiScale)
    {
        Span<Vector2> corners = stackalloc Vector2[4]
        {
            new(minimum.X, minimum.Y),
            new(maximum.X, minimum.Y),
            new(maximum.X, maximum.Y),
            new(minimum.X, maximum.Y)
        };
        for (int index = 0; index < corners.Length; index++)
        {
            Vector2 point = corners[index] * emScale;
            float x = point.X * fontScaleX - point.Y * italicSkew + boldOffset;
            corners[index] = origin + x * basisX + point.Y * basisY;
        }
        float minimumX = corners[0].X;
        float minimumY = corners[0].Y;
        float maximumX = minimumX;
        float maximumY = minimumY;
        for (int index = 1; index < corners.Length; index++)
        {
            minimumX = MathF.Min(minimumX, corners[index].X);
            minimumY = MathF.Min(minimumY, corners[index].Y);
            maximumX = MathF.Max(maximumX, corners[index].X);
            maximumY = MathF.Max(maximumY, corners[index].Y);
        }
        float padding = GlyphBoundsPadding / dpiScale;
        return new NativeImageRect(
            minimumX - padding,
            minimumY - padding,
            maximumX - minimumX + padding * 2f,
            maximumY - minimumY + padding * 2f);
    }
}

using System.Numerics;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.CAD;

/// <summary>Exact retained-outline selection for TrueType and SHX TEXT.</summary>
/// <remarks>
/// Shaped glyph identity and position stay retained in the snapshot. Selection
/// traverses the same cached analytic outlines used by rendering and never
/// expands a glyph run into retained per-glyph commands. Polynomial and
/// rational-conic queries reuse the bounded Bernstein root isolation used by
/// spline selection. For G glyphs and S outline segments, work is O(G * S * R)
/// for bounded root work R, stack storage is O(1), and warm queries allocate no
/// managed memory. Numerically unresolved geometry returns a typed unsupported
/// result instead of accepting flattened or advance-box approximations.
/// </remarks>
internal static class CadTextSelection
{
    private const double PlaneToleranceFactor = 1.4210854715202004e-14;

    public static CadPointHitResult HitTestTextPoint(
        CadDocumentSnapshot snapshot,
        in CadTextPrimitive text,
        CadPoint3D point,
        double tolerance)
    {
        double minimum = double.PositiveInfinity;
        bool hasGeometry = false;
        ReadOnlySpan<ushort> indices = snapshot.TextGlyphIndices.Span;
        ReadOnlySpan<Vector2> positions = snapshot.TextGlyphPositions.Span;
        ReadOnlySpan<TtfFont> fonts = snapshot.TextFonts.Span;
        ReadOnlySpan<CadTextGlyphRun> runs = snapshot.TextGlyphRuns.Span.Slice(
            text.RunOffset,
            text.RunCount);

        for (int runIndex = 0; runIndex < runs.Length; runIndex++)
        {
            CadTextGlyphRun run = runs[runIndex];
            TtfFont font = fonts[run.FontIndex];
            if (font.UnitsPerEm == 0)
            {
                return UnsupportedPoint();
            }
            double scale = 1.0 / font.UnitsPerEm;
            int end = checked(run.GlyphOffset + run.GlyphCount);
            for (int glyphIndex = run.GlyphOffset; glyphIndex < end; glyphIndex++)
            {
                ushort retainedGlyphIndex = indices[glyphIndex];
                if (font.HasColorGlyphs && font.HasColorLayers(retainedGlyphIndex))
                {
                    return UnsupportedPoint();
                }
                PathGeometry? outline = font.GetGlyphOutline(retainedGlyphIndex);
                if (outline is null)
                {
                    if (font.HasBitmapGlyphs &&
                        font.TryGetBitmapGlyph(retainedGlyphIndex, 16.0f, out _))
                    {
                        return UnsupportedPoint();
                    }
                    continue;
                }
                Vector2 position = positions[glyphIndex];
                var glyphPlacement = new PathPlacement(
                    text.Origin,
                    text.XAxis,
                    text.YAxis,
                    position.X,
                    position.Y,
                    scale,
                    -scale);
                if (!TryPointPath(
                        outline,
                        glyphPlacement,
                        point,
                        filled: true,
                        ref minimum,
                        ref hasGeometry))
                {
                    return UnsupportedPoint();
                }
            }
        }

        ReadOnlySpan<CadTextDecoration> decorations =
            snapshot.TextDecorations.Span.Slice(
                text.DecorationOffset,
                text.DecorationCount);
        var textPlacement = new PathPlacement(
            text.Origin,
            text.XAxis,
            text.YAxis,
            0.0,
            0.0,
            1.0,
            1.0);
        for (int i = 0; i < decorations.Length; i++)
        {
            if (!TryPointRectangle(
                    decorations[i].X,
                    decorations[i].Y,
                    decorations[i].Width,
                    decorations[i].Height,
                    textPlacement,
                    point,
                    ref minimum))
            {
                return UnsupportedPoint();
            }
            hasGeometry = true;
        }

        return FromPoint(minimum, tolerance, hasGeometry);
    }

    public static CadPointHitResult HitTestShxPoint(
        CadDocumentSnapshot snapshot,
        in CadShxTextPrimitive text,
        CadPoint3D point,
        double tolerance)
    {
        double minimum = double.PositiveInfinity;
        bool hasGeometry = false;
        ReadOnlySpan<CadShxGlyphInstance> glyphs =
            snapshot.ShxGlyphInstances.Span.Slice(text.GlyphOffset, text.GlyphCount);
        for (int i = 0; i < glyphs.Length; i++)
        {
            CadShxGlyphInstance glyph = glyphs[i];
            if (!glyph.Glyph.HasGeometry)
            {
                continue;
            }
            var placement = new PathPlacement(
                text.Origin,
                text.XAxis,
                text.YAxis,
                glyph.X,
                glyph.Y,
                1.0,
                1.0);
            if (!TryPointPath(
                    glyph.Glyph.Path,
                    placement,
                    point,
                    filled: false,
                    ref minimum,
                    ref hasGeometry))
            {
                return UnsupportedPoint();
            }
        }

        ReadOnlySpan<CadShxDecorationSegment> decorations =
            snapshot.ShxDecorationSegments.Span.Slice(
                text.DecorationOffset,
                text.DecorationCount);
        for (int i = 0; i < decorations.Length; i++)
        {
            CadShxDecorationSegment decoration = decorations[i];
            CadPoint3D start = Transform(
                text.Origin,
                text.XAxis,
                text.YAxis,
                decoration.StartX,
                decoration.StartY);
            CadPoint3D end = Transform(
                text.Origin,
                text.XAxis,
                text.YAxis,
                decoration.EndX,
                decoration.EndY);
            Span<CadHomogeneousPoint> line = stackalloc CadHomogeneousPoint[2];
            line[0] = Homogeneous(start);
            line[1] = Homogeneous(end);
            if (!CadSplineSelection.TryDistanceToBezier(line, point, out double distance))
            {
                return UnsupportedPoint();
            }
            minimum = Math.Min(minimum, distance);
            hasGeometry = true;
        }
        return FromPoint(minimum, tolerance, hasGeometry);
    }

    public static CadBoundsHitResult HitTestTextBounds(
        CadDocumentSnapshot snapshot,
        in CadTextPrimitive text,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode) =>
        HitTextBoundsCore(snapshot, text, bounds, mode);

    public static CadPointHitResult HitTestMTextPoint(
        CadDocumentSnapshot snapshot,
        in CadMTextPrimitive text,
        CadPoint3D point,
        double tolerance)
    {
        double minimum = double.PositiveInfinity;
        bool hasGeometry = false;
        ReadOnlySpan<ushort> indices = snapshot.TextGlyphIndices.Span;
        ReadOnlySpan<Vector2> positions = snapshot.TextGlyphPositions.Span;
        ReadOnlySpan<TtfFont> fonts = snapshot.TextFonts.Span;
        ReadOnlySpan<CadMTextGlyphRun> runs = snapshot.MTextGlyphRuns.Span.Slice(
            text.RunOffset,
            text.RunCount);
        for (int runIndex = 0; runIndex < runs.Length; runIndex++)
        {
            CadMTextGlyphRun run = runs[runIndex];
            TtfFont font = fonts[run.FontIndex];
            if (font.UnitsPerEm == 0) return UnsupportedPoint();
            double scale = run.FontSize / font.UnitsPerEm;
            int end = checked(run.GlyphOffset + run.GlyphCount);
            for (int glyphIndex = run.GlyphOffset; glyphIndex < end; glyphIndex++)
            {
                ushort glyphId = indices[glyphIndex];
                if (font.HasColorGlyphs && font.HasColorLayers(glyphId)) return UnsupportedPoint();
                PathGeometry? outline = font.GetGlyphOutline(glyphId);
                if (outline is null)
                {
                    if (font.HasBitmapGlyphs && font.TryGetBitmapGlyph(glyphId, run.FontSize, out _))
                        return UnsupportedPoint();
                    continue;
                }
                Vector2 position = positions[glyphIndex];
                var placement = new PathPlacement(
                    text.Origin,
                    text.XAxis,
                    text.YAxis,
                    position.X,
                    position.Y,
                    scale * run.WidthScale,
                    -scale,
                    scale * run.SkewX);
                if (!TryPointPath(outline, placement, point, true, ref minimum, ref hasGeometry))
                    return UnsupportedPoint();
            }
        }

        var entityPlacement = new PathPlacement(
            text.Origin, text.XAxis, text.YAxis, 0.0, 0.0, 1.0, 1.0);
        if (!TryPointMTextRectangles(snapshot.MTextBackgrounds.Span.Slice(
                text.BackgroundOffset, text.BackgroundCount), entityPlacement, point, ref minimum, ref hasGeometry) ||
            !TryPointMTextRectangles(snapshot.MTextDecorations.Span.Slice(
                text.DecorationOffset, text.DecorationCount), entityPlacement, point, ref minimum, ref hasGeometry))
        {
            return UnsupportedPoint();
        }
        ReadOnlySpan<CadMTextStroke> strokes = snapshot.MTextStrokes.Span.Slice(
            text.StrokeOffset, text.StrokeCount);
        for (int index = 0; index < strokes.Length; index++)
        {
            if (!TryCreateMTextStrokePlacement(text, strokes[index], out PathPlacement placement, out double length))
                return UnsupportedPoint();
            if (!TryPointRectangle(0.0, -0.5, length, 1.0, placement, point, ref minimum))
                return UnsupportedPoint();
            hasGeometry = true;
        }
        return FromPoint(minimum, tolerance, hasGeometry);
    }

    public static CadBoundsHitResult HitTestMTextBounds(
        CadDocumentSnapshot snapshot,
        in CadMTextPrimitive text,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode)
    {
        if (bounds.IsEmpty) return BoundsMiss();
        bool hasGeometry = false;
        ReadOnlySpan<ushort> indices = snapshot.TextGlyphIndices.Span;
        ReadOnlySpan<Vector2> positions = snapshot.TextGlyphPositions.Span;
        ReadOnlySpan<TtfFont> fonts = snapshot.TextFonts.Span;
        ReadOnlySpan<CadMTextGlyphRun> runs = snapshot.MTextGlyphRuns.Span.Slice(
            text.RunOffset, text.RunCount);
        for (int runIndex = 0; runIndex < runs.Length; runIndex++)
        {
            CadMTextGlyphRun run = runs[runIndex];
            TtfFont font = fonts[run.FontIndex];
            if (font.UnitsPerEm == 0) return BoundsUnsupported();
            double scale = run.FontSize / font.UnitsPerEm;
            int end = checked(run.GlyphOffset + run.GlyphCount);
            for (int glyphIndex = run.GlyphOffset; glyphIndex < end; glyphIndex++)
            {
                ushort glyphId = indices[glyphIndex];
                if (font.HasColorGlyphs && font.HasColorLayers(glyphId)) return BoundsUnsupported();
                PathGeometry? outline = font.GetGlyphOutline(glyphId);
                if (outline is null)
                {
                    if (font.HasBitmapGlyphs && font.TryGetBitmapGlyph(glyphId, run.FontSize, out _))
                        return BoundsUnsupported();
                    continue;
                }
                Vector2 position = positions[glyphIndex];
                var placement = new PathPlacement(
                    text.Origin, text.XAxis, text.YAxis,
                    position.X, position.Y,
                    scale * run.WidthScale, -scale, scale * run.SkewX);
                if (!TryBoundsPath(outline, placement, bounds, mode, true, out bool hit, out bool pathGeometry))
                    return BoundsUnsupported();
                hasGeometry |= pathGeometry;
                if (mode == CadBoundsSelectionMode.Crossing && hit) return BoundsHit();
                if (mode == CadBoundsSelectionMode.Window && pathGeometry && !hit) return BoundsMiss();
            }
        }

        var entityPlacement = new PathPlacement(
            text.Origin, text.XAxis, text.YAxis, 0.0, 0.0, 1.0, 1.0);
        CadBoundsHitResult rectangleResult = TestMTextRectangles(
            snapshot.MTextBackgrounds.Span.Slice(text.BackgroundOffset, text.BackgroundCount));
        if (rectangleResult.Status == CadBoundsHitStatus.UnsupportedGeometry) return rectangleResult;
        if (mode == CadBoundsSelectionMode.Crossing && rectangleResult.Status == CadBoundsHitStatus.Hit) return rectangleResult;
        if (mode == CadBoundsSelectionMode.Window && rectangleResult.Status == CadBoundsHitStatus.Miss && text.BackgroundCount > 0) return rectangleResult;
        hasGeometry |= text.BackgroundCount > 0;
        rectangleResult = TestMTextRectangles(
            snapshot.MTextDecorations.Span.Slice(text.DecorationOffset, text.DecorationCount));
        if (rectangleResult.Status == CadBoundsHitStatus.UnsupportedGeometry) return rectangleResult;
        if (mode == CadBoundsSelectionMode.Crossing && rectangleResult.Status == CadBoundsHitStatus.Hit) return rectangleResult;
        if (mode == CadBoundsSelectionMode.Window && rectangleResult.Status == CadBoundsHitStatus.Miss && text.DecorationCount > 0) return rectangleResult;
        hasGeometry |= text.DecorationCount > 0;

        ReadOnlySpan<CadMTextStroke> strokes = snapshot.MTextStrokes.Span.Slice(
            text.StrokeOffset, text.StrokeCount);
        for (int index = 0; index < strokes.Length; index++)
        {
            if (!TryCreateMTextStrokePlacement(text, strokes[index], out PathPlacement placement, out double length) ||
                !TryBoundsRectangle(0.0, -0.5, length, 1.0, placement, bounds, mode, out bool hit))
                return BoundsUnsupported();
            hasGeometry = true;
            if (mode == CadBoundsSelectionMode.Crossing && hit) return BoundsHit();
            if (mode == CadBoundsSelectionMode.Window && !hit) return BoundsMiss();
        }
        return mode == CadBoundsSelectionMode.Window && hasGeometry ? BoundsHit() : BoundsMiss();

        CadBoundsHitResult TestMTextRectangles(ReadOnlySpan<CadMTextRectangle> rectangles)
        {
            bool any = false;
            for (int index = 0; index < rectangles.Length; index++)
            {
                CadMTextRectangle rectangle = rectangles[index];
                if (!TryBoundsRectangle(
                        rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height,
                        entityPlacement, bounds, mode, out bool hit))
                    return BoundsUnsupported();
                any = true;
                if (mode == CadBoundsSelectionMode.Crossing && hit) return BoundsHit();
                if (mode == CadBoundsSelectionMode.Window && !hit) return BoundsMiss();
            }
            return mode == CadBoundsSelectionMode.Window && any ? BoundsHit() : BoundsMiss();
        }
    }

    private static bool TryPointMTextRectangles(
        ReadOnlySpan<CadMTextRectangle> rectangles,
        in PathPlacement placement,
        CadPoint3D point,
        ref double minimum,
        ref bool hasGeometry)
    {
        for (int index = 0; index < rectangles.Length; index++)
        {
            CadMTextRectangle rectangle = rectangles[index];
            if (!TryPointRectangle(
                    rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height,
                    placement, point, ref minimum)) return false;
            hasGeometry = true;
        }
        return true;
    }

    private static bool TryCreateMTextStrokePlacement(
        in CadMTextPrimitive text,
        in CadMTextStroke stroke,
        out PathPlacement placement,
        out double length)
    {
        double dx = stroke.EndX - stroke.StartX;
        double dy = stroke.EndY - stroke.StartY;
        length = Math.Sqrt((dx * dx) + (dy * dy));
        if (!(length > 0.0) || !double.IsFinite(length) ||
            !(stroke.Thickness > 0.0f) || !float.IsFinite(stroke.Thickness))
        {
            placement = default;
            return false;
        }
        dx /= length;
        dy /= length;
        CadPoint3D strokeOrigin = Transform(
            text.Origin, text.XAxis, text.YAxis, stroke.StartX, stroke.StartY);
        CadPoint3D along = (text.XAxis * dx) + (text.YAxis * dy);
        CadPoint3D across = ((text.XAxis * -dy) + (text.YAxis * dx)) * stroke.Thickness;
        placement = new PathPlacement(strokeOrigin, along, across, 0.0, 0.0, 1.0, 1.0);
        return placement.IsFiniteAndNonDegenerate;
    }

    public static CadBoundsHitResult HitTestShxBounds(
        CadDocumentSnapshot snapshot,
        in CadShxTextPrimitive text,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode)
    {
        if (bounds.IsEmpty)
        {
            return BoundsMiss();
        }
        bool hasGeometry = false;
        ReadOnlySpan<CadShxGlyphInstance> glyphs =
            snapshot.ShxGlyphInstances.Span.Slice(text.GlyphOffset, text.GlyphCount);
        for (int i = 0; i < glyphs.Length; i++)
        {
            CadShxGlyphInstance glyph = glyphs[i];
            if (!glyph.Glyph.HasGeometry)
            {
                continue;
            }
            var placement = new PathPlacement(
                text.Origin,
                text.XAxis,
                text.YAxis,
                glyph.X,
                glyph.Y,
                1.0,
                1.0);
            if (!TryBoundsPath(
                    glyph.Glyph.Path,
                    placement,
                    bounds,
                    mode,
                    filled: false,
                    out bool hit,
                    out bool pathHasGeometry))
            {
                return BoundsUnsupported();
            }
            hasGeometry |= pathHasGeometry;
            if (mode == CadBoundsSelectionMode.Crossing && hit)
            {
                return BoundsHit();
            }
            if (mode == CadBoundsSelectionMode.Window && pathHasGeometry && !hit)
            {
                return BoundsMiss();
            }
        }

        ReadOnlySpan<CadShxDecorationSegment> decorations =
            snapshot.ShxDecorationSegments.Span.Slice(
                text.DecorationOffset,
                text.DecorationCount);
        for (int i = 0; i < decorations.Length; i++)
        {
            CadShxDecorationSegment decoration = decorations[i];
            Span<CadHomogeneousPoint> line = stackalloc CadHomogeneousPoint[2];
            line[0] = Homogeneous(Transform(
                text.Origin, text.XAxis, text.YAxis,
                decoration.StartX, decoration.StartY));
            line[1] = Homogeneous(Transform(
                text.Origin, text.XAxis, text.YAxis,
                decoration.EndX, decoration.EndY));
            if (!CadSplineSelection.TryTestBezierBounds(line, bounds, mode, out bool hit))
            {
                return BoundsUnsupported();
            }
            hasGeometry = true;
            if (mode == CadBoundsSelectionMode.Crossing && hit)
            {
                return BoundsHit();
            }
            if (mode == CadBoundsSelectionMode.Window && !hit)
            {
                return BoundsMiss();
            }
        }

        return mode == CadBoundsSelectionMode.Window && hasGeometry
            ? BoundsHit()
            : BoundsMiss();
    }

    private static CadBoundsHitResult HitTextBoundsCore(
        CadDocumentSnapshot snapshot,
        in CadTextPrimitive text,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode)
    {
        if (bounds.IsEmpty)
        {
            return BoundsMiss();
        }
        bool hasGeometry = false;
        ReadOnlySpan<ushort> indices = snapshot.TextGlyphIndices.Span;
        ReadOnlySpan<Vector2> positions = snapshot.TextGlyphPositions.Span;
        ReadOnlySpan<TtfFont> fonts = snapshot.TextFonts.Span;
        ReadOnlySpan<CadTextGlyphRun> runs = snapshot.TextGlyphRuns.Span.Slice(
            text.RunOffset,
            text.RunCount);
        for (int runIndex = 0; runIndex < runs.Length; runIndex++)
        {
            CadTextGlyphRun run = runs[runIndex];
            TtfFont font = fonts[run.FontIndex];
            if (font.UnitsPerEm == 0)
            {
                return BoundsUnsupported();
            }
            double scale = 1.0 / font.UnitsPerEm;
            int end = checked(run.GlyphOffset + run.GlyphCount);
            for (int glyphIndex = run.GlyphOffset; glyphIndex < end; glyphIndex++)
            {
                ushort retainedGlyphIndex = indices[glyphIndex];
                if (font.HasColorGlyphs && font.HasColorLayers(retainedGlyphIndex))
                {
                    return BoundsUnsupported();
                }
                PathGeometry? outline = font.GetGlyphOutline(retainedGlyphIndex);
                if (outline is null)
                {
                    if (font.HasBitmapGlyphs &&
                        font.TryGetBitmapGlyph(retainedGlyphIndex, 16.0f, out _))
                    {
                        return BoundsUnsupported();
                    }
                    continue;
                }
                Vector2 position = positions[glyphIndex];
                var glyphPlacement = new PathPlacement(
                    text.Origin, text.XAxis, text.YAxis,
                    position.X, position.Y, scale, -scale);
                if (!TryBoundsPath(
                        outline,
                        glyphPlacement,
                        bounds,
                        mode,
                        filled: true,
                        out bool hit,
                        out bool pathHasGeometry))
                {
                    return BoundsUnsupported();
                }
                hasGeometry |= pathHasGeometry;
                if (mode == CadBoundsSelectionMode.Crossing && hit)
                {
                    return BoundsHit();
                }
                if (mode == CadBoundsSelectionMode.Window && pathHasGeometry && !hit)
                {
                    return BoundsMiss();
                }
            }
        }

        ReadOnlySpan<CadTextDecoration> decorations =
            snapshot.TextDecorations.Span.Slice(
                text.DecorationOffset,
                text.DecorationCount);
        var textPlacement = new PathPlacement(
            text.Origin, text.XAxis, text.YAxis, 0.0, 0.0, 1.0, 1.0);
        for (int i = 0; i < decorations.Length; i++)
        {
            if (!TryBoundsRectangle(
                    decorations[i].X,
                    decorations[i].Y,
                    decorations[i].Width,
                    decorations[i].Height,
                    textPlacement,
                    bounds,
                    mode,
                    out bool hit))
            {
                return BoundsUnsupported();
            }
            hasGeometry = true;
            if (mode == CadBoundsSelectionMode.Crossing && hit)
            {
                return BoundsHit();
            }
            if (mode == CadBoundsSelectionMode.Window && !hit)
            {
                return BoundsMiss();
            }
        }
        return mode == CadBoundsSelectionMode.Window && hasGeometry
            ? BoundsHit()
            : BoundsMiss();
    }

    private static bool TryPointPath(
        PathGeometry path,
        in PathPlacement placement,
        CadPoint3D point,
        bool filled,
        ref double minimum,
        ref bool hasGeometry)
    {
        if (path.IsCombined || !placement.IsFiniteAndNonDegenerate)
        {
            return false;
        }
        Vector2 local = default;
        double planeDistance = double.PositiveInfinity;
        if (filled && !TryProjectedLocalPoint(placement, point, out local, out planeDistance))
        {
            return false;
        }

        int winding = 0;
        int parity = 0;
        for (int figureIndex = 0; figureIndex < path.Figures.Count; figureIndex++)
        {
            PathFigure figure = path.Figures[figureIndex];
            Vector2 current = figure.StartPoint;
            for (int segmentIndex = 0; segmentIndex < figure.Segments.Count; segmentIndex++)
            {
                PathSegment segment = figure.Segments[segmentIndex];
                bool selectable = filled ? figure.IsFilled : segment.IsStroked;
                if (selectable && !TryPointSegment(
                        current,
                        segment,
                        placement,
                        point,
                        filled ? local : default,
                        filled && figure.IsFilled,
                        ref minimum,
                        ref winding,
                        ref parity))
                {
                    return false;
                }
                if (selectable)
                {
                    hasGeometry = true;
                }
                current = EndPoint(segment);
            }
            if (figure.IsClosed && current != figure.StartPoint &&
                (filled ? figure.IsFilled : true))
            {
                if (!TryPointLine(
                        current,
                        figure.StartPoint,
                        placement,
                        point,
                        filled ? local : default,
                        filled && figure.IsFilled,
                        ref minimum,
                        ref winding,
                        ref parity))
                {
                    return false;
                }
                hasGeometry = true;
            }
        }
        if (filled)
        {
            bool inside = path.FillRule == FillRule.EvenOdd
                ? (parity & 1) != 0
                : winding != 0;
            if (inside)
            {
                minimum = Math.Min(minimum, planeDistance);
            }
        }
        return true;
    }

    private static bool TryPointSegment(
        Vector2 start,
        PathSegment segment,
        in PathPlacement placement,
        CadPoint3D point,
        Vector2 local,
        bool countFill,
        ref double minimum,
        ref int winding,
        ref int parity)
    {
        switch (segment)
        {
            case LineSegment line:
                return TryPointLine(start, line.Point, placement, point, local,
                    countFill, ref minimum, ref winding, ref parity);
            case QuadraticBezierSegment quadratic:
                {
                    Span<Vector2> controls = stackalloc Vector2[3]
                    {
                    start, quadratic.ControlPoint, quadratic.Point,
                };
                    return TryPointBezier(controls, placement, point, local, countFill,
                        ref minimum, ref winding, ref parity);
                }
            case CubicBezierSegment cubic:
                {
                    Span<Vector2> controls = stackalloc Vector2[4]
                    {
                    start, cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point,
                };
                    return TryPointBezier(controls, placement, point, local, countFill,
                        ref minimum, ref winding, ref parity);
                }
            case ArcSegment arc:
                return TryPointArc(start, arc, placement, point, local, countFill,
                    ref minimum, ref winding, ref parity);
            default:
                return false;
        }
    }

    private static bool TryPointLine(
        Vector2 start,
        Vector2 end,
        in PathPlacement placement,
        CadPoint3D point,
        Vector2 local,
        bool countFill,
        ref double minimum,
        ref int winding,
        ref int parity)
    {
        Span<Vector2> controls = stackalloc Vector2[2] { start, end };
        return TryPointBezier(controls, placement, point, local, countFill,
            ref minimum, ref winding, ref parity);
    }

    private static bool TryPointBezier(
        ReadOnlySpan<Vector2> controls,
        in PathPlacement placement,
        CadPoint3D point,
        Vector2 local,
        bool countFill,
        ref double minimum,
        ref int winding,
        ref int parity)
    {
        Span<CadHomogeneousPoint> world = stackalloc CadHomogeneousPoint[4];
        for (int i = 0; i < controls.Length; i++)
        {
            world[i] = Homogeneous(placement.Transform(controls[i]));
        }
        if (!CadSplineSelection.TryDistanceToBezier(world[..controls.Length], point, out double distance))
        {
            return false;
        }
        minimum = Math.Min(minimum, distance);
        return !countFill || TryAccumulatePolynomialWinding(
            controls, local, ref winding, ref parity);
    }

    private static bool TryPointArc(
        Vector2 start,
        ArcSegment arc,
        in PathPlacement placement,
        CadPoint3D point,
        Vector2 local,
        bool countFill,
        ref double minimum,
        ref int winding,
        ref int parity)
    {
        Span<RationalPiece2D> pieces = stackalloc RationalPiece2D[4];
        if (!TryCreateArcPieces(start, arc, pieces, out int count))
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            RationalPiece2D piece = pieces[i];
            Span<CadHomogeneousPoint> world = stackalloc CadHomogeneousPoint[3];
            placement.Transform(piece, world);
            if (!CadSplineSelection.TryDistanceToBezier(world, point, out double distance))
            {
                return false;
            }
            minimum = Math.Min(minimum, distance);
            if (countFill && !TryAccumulateRationalWinding(piece, local, ref winding, ref parity))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryBoundsPath(
        PathGeometry path,
        in PathPlacement placement,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode,
        bool filled,
        out bool hit,
        out bool hasGeometry)
    {
        hit = mode == CadBoundsSelectionMode.Window;
        hasGeometry = false;
        if (path.IsCombined || !placement.IsFiniteAndNonDegenerate)
        {
            return false;
        }
        for (int figureIndex = 0; figureIndex < path.Figures.Count; figureIndex++)
        {
            PathFigure figure = path.Figures[figureIndex];
            Vector2 current = figure.StartPoint;
            for (int segmentIndex = 0; segmentIndex < figure.Segments.Count; segmentIndex++)
            {
                PathSegment segment = figure.Segments[segmentIndex];
                bool selectable = filled ? figure.IsFilled : segment.IsStroked;
                if (selectable)
                {
                    if (!TryBoundsSegment(current, segment, placement, bounds, mode, out bool segmentHit))
                    {
                        return false;
                    }
                    hasGeometry = true;
                    if (mode == CadBoundsSelectionMode.Crossing && segmentHit)
                    {
                        hit = true;
                        return true;
                    }
                    if (mode == CadBoundsSelectionMode.Window && !segmentHit)
                    {
                        hit = false;
                        return true;
                    }
                }
                current = EndPoint(segment);
            }
            if (figure.IsClosed && current != figure.StartPoint &&
                (filled ? figure.IsFilled : true))
            {
                Span<Vector2> controls = stackalloc Vector2[2] { current, figure.StartPoint };
                if (!TryBoundsBezier(controls, placement, bounds, mode, out bool closeHit))
                {
                    return false;
                }
                hasGeometry = true;
                if (mode == CadBoundsSelectionMode.Crossing && closeHit)
                {
                    hit = true;
                    return true;
                }
                if (mode == CadBoundsSelectionMode.Window && !closeHit)
                {
                    hit = false;
                    return true;
                }
            }
        }
        if (filled && mode == CadBoundsSelectionMode.Crossing && hasGeometry &&
            TryPlaneBoxHasFilledSample(path, placement, bounds, out bool contains))
        {
            hit = contains;
        }
        else if (filled && mode == CadBoundsSelectionMode.Crossing && hasGeometry)
        {
            return false;
        }
        return true;
    }

    private static bool TryBoundsSegment(
        Vector2 start,
        PathSegment segment,
        in PathPlacement placement,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode,
        out bool hit)
    {
        switch (segment)
        {
            case LineSegment line:
                {
                    Span<Vector2> controls = stackalloc Vector2[2] { start, line.Point };
                    return TryBoundsBezier(controls, placement, bounds, mode, out hit);
                }
            case QuadraticBezierSegment quadratic:
                {
                    Span<Vector2> controls = stackalloc Vector2[3]
                    {
                    start, quadratic.ControlPoint, quadratic.Point,
                };
                    return TryBoundsBezier(controls, placement, bounds, mode, out hit);
                }
            case CubicBezierSegment cubic:
                {
                    Span<Vector2> controls = stackalloc Vector2[4]
                    {
                    start, cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point,
                };
                    return TryBoundsBezier(controls, placement, bounds, mode, out hit);
                }
            case ArcSegment arc:
                {
                    Span<RationalPiece2D> pieces = stackalloc RationalPiece2D[4];
                    if (!TryCreateArcPieces(start, arc, pieces, out int count))
                    {
                        hit = false;
                        return false;
                    }
                    bool all = true;
                    for (int i = 0; i < count; i++)
                    {
                        Span<CadHomogeneousPoint> world = stackalloc CadHomogeneousPoint[3];
                        placement.Transform(pieces[i], world);
                        if (!CadSplineSelection.TryTestBezierBounds(world, bounds, mode, out bool pieceHit))
                        {
                            hit = false;
                            return false;
                        }
                        if (mode == CadBoundsSelectionMode.Crossing && pieceHit)
                        {
                            hit = true;
                            return true;
                        }
                        all &= pieceHit;
                    }
                    hit = mode == CadBoundsSelectionMode.Window && all;
                    return true;
                }
            default:
                hit = false;
                return false;
        }
    }

    private static bool TryBoundsBezier(
        ReadOnlySpan<Vector2> controls,
        in PathPlacement placement,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode,
        out bool hit)
    {
        Span<CadHomogeneousPoint> world = stackalloc CadHomogeneousPoint[4];
        for (int i = 0; i < controls.Length; i++)
        {
            world[i] = Homogeneous(placement.Transform(controls[i]));
        }
        return CadSplineSelection.TryTestBezierBounds(
            world[..controls.Length], bounds, mode, out hit);
    }

    private static bool TryAccumulatePolynomialWinding(
        ReadOnlySpan<Vector2> points,
        Vector2 query,
        ref int winding,
        ref int parity)
    {
        int degree = points.Length - 1;
        Span<double> coefficients = stackalloc double[4];
        for (int i = 0; i <= degree; i++)
        {
            coefficients[i] = points[i].Y - query.Y;
        }
        Span<double> roots = stackalloc double[3];
        if (!CadBernsteinPolynomial.TryCollectRoots(
                coefficients[..(degree + 1)], roots[..degree], out int count))
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            double t = roots[i];
            Vector2 value = Evaluate(points, t);
            double derivativeY = EvaluateDerivativeY(points, t);
            if (value.X > query.X && IncludeCrossing(t, derivativeY))
            {
                winding += derivativeY > 0.0 ? 1 : -1;
                parity ^= 1;
            }
        }
        return true;
    }

    private static bool TryAccumulateRationalWinding(
        in RationalPiece2D piece,
        Vector2 query,
        ref int winding,
        ref int parity)
    {
        Span<double> coefficients = stackalloc double[3]
        {
            piece.P0.Y - (query.Y * piece.W0),
            piece.P1.Y - (query.Y * piece.W1),
            piece.P2.Y - (query.Y * piece.W2),
        };
        Span<double> roots = stackalloc double[2];
        if (!CadBernsteinPolynomial.TryCollectRoots(coefficients, roots, out int count))
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            double t = roots[i];
            if (!piece.TryEvaluate(t, out Vector2 value, out Vector2 derivative))
            {
                return false;
            }
            if (value.X > query.X && IncludeCrossing(t, derivative.Y))
            {
                winding += derivative.Y > 0.0 ? 1 : -1;
                parity ^= 1;
            }
        }
        return true;
    }

    private static bool IncludeCrossing(double parameter, double derivativeY)
    {
        if (!double.IsFinite(derivativeY) || Math.Abs(derivativeY) <= 1e-14)
        {
            return false;
        }
        return derivativeY > 0.0
            ? parameter >= 0.0 && parameter < 1.0
            : parameter > 0.0 && parameter <= 1.0;
    }

    private static bool TryPlaneBoxHasFilledSample(
        PathGeometry path,
        in PathPlacement placement,
        CadBounds3D bounds,
        out bool contains)
    {
        contains = false;
        CadPoint3D normal = CadPoint3D.Cross(placement.XAxis, placement.YAxis);
        double normalLength = normal.Length;
        if (!(normalLength > 0.0) || !double.IsFinite(normalLength))
        {
            return false;
        }
        double coordinateScale = Math.Max(1.0,
            Math.Max(bounds.Min.Length, bounds.Max.Length));
        double epsilon = coordinateScale * normalLength * PlaneToleranceFactor;
        Span<CadPoint3D> vertices = stackalloc CadPoint3D[8];
        FillBoundsVertices(bounds, vertices);
        Span<double> distances = stackalloc double[8];
        for (int i = 0; i < vertices.Length; i++)
        {
            distances[i] = CadPoint3D.Dot(vertices[i] - placement.Origin, normal);
            if (Math.Abs(distances[i]) <= epsilon &&
                TryPathContainsWorldPoint(path, placement, vertices[i], out bool inside) && inside)
            {
                contains = true;
                return true;
            }
        }
        ReadOnlySpan<byte> edges =
        [
            0,1, 2,3, 4,5, 6,7,
            0,2, 1,3, 4,6, 5,7,
            0,4, 1,5, 2,6, 3,7,
        ];
        for (int i = 0; i < edges.Length; i += 2)
        {
            int a = edges[i];
            int b = edges[i + 1];
            double da = distances[a];
            double db = distances[b];
            if ((da < -epsilon && db > epsilon) || (da > epsilon && db < -epsilon))
            {
                double t = da / (da - db);
                CadPoint3D sample = vertices[a] + ((vertices[b] - vertices[a]) * t);
                if (!TryPathContainsWorldPoint(path, placement, sample, out bool inside))
                {
                    return false;
                }
                if (inside)
                {
                    contains = true;
                    return true;
                }
            }
        }
        return true;
    }

    private static bool TryPathContainsWorldPoint(
        PathGeometry path,
        in PathPlacement placement,
        CadPoint3D point,
        out bool inside)
    {
        inside = false;
        if (!TryProjectedLocalPoint(placement, point, out Vector2 local, out _))
        {
            return false;
        }
        int winding = 0;
        int parity = 0;
        for (int figureIndex = 0; figureIndex < path.Figures.Count; figureIndex++)
        {
            PathFigure figure = path.Figures[figureIndex];
            if (!figure.IsFilled)
            {
                continue;
            }
            Vector2 current = figure.StartPoint;
            for (int i = 0; i < figure.Segments.Count; i++)
            {
                PathSegment segment = figure.Segments[i];
                bool ok = segment switch
                {
                    LineSegment line => AccumulateLine(current, line.Point),
                    QuadraticBezierSegment quadratic => AccumulateQuadratic(current, quadratic),
                    CubicBezierSegment cubic => AccumulateCubic(current, cubic),
                    ArcSegment arc => AccumulateArc(current, arc),
                    _ => false,
                };
                if (!ok)
                {
                    return false;
                }
                current = EndPoint(segment);
            }
            if (figure.IsClosed && current != figure.StartPoint &&
                !AccumulateLine(current, figure.StartPoint))
            {
                return false;
            }
        }
        inside = path.FillRule == FillRule.EvenOdd ? (parity & 1) != 0 : winding != 0;
        return true;

        bool AccumulateLine(Vector2 start, Vector2 end)
        {
            Span<Vector2> values = stackalloc Vector2[2] { start, end };
            return TryAccumulatePolynomialWinding(values, local, ref winding, ref parity);
        }
        bool AccumulateQuadratic(Vector2 start, QuadraticBezierSegment segment)
        {
            Span<Vector2> values = stackalloc Vector2[3]
                { start, segment.ControlPoint, segment.Point };
            return TryAccumulatePolynomialWinding(values, local, ref winding, ref parity);
        }
        bool AccumulateCubic(Vector2 start, CubicBezierSegment segment)
        {
            Span<Vector2> values = stackalloc Vector2[4]
                { start, segment.ControlPoint1, segment.ControlPoint2, segment.Point };
            return TryAccumulatePolynomialWinding(values, local, ref winding, ref parity);
        }
        bool AccumulateArc(Vector2 start, ArcSegment segment)
        {
            Span<RationalPiece2D> pieces = stackalloc RationalPiece2D[4];
            if (!TryCreateArcPieces(start, segment, pieces, out int count))
            {
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                if (!TryAccumulateRationalWinding(pieces[i], local, ref winding, ref parity))
                {
                    return false;
                }
            }
            return true;
        }
    }

    private static bool TryPointRectangle(
        double x,
        double y,
        double width,
        double height,
        in PathPlacement placement,
        CadPoint3D point,
        ref double minimum)
    {
        Span<Vector2> corners = stackalloc Vector2[4]
        {
            new((float)x, (float)y),
            new((float)(x + width), (float)y),
            new((float)(x + width), (float)(y + height)),
            new((float)x, (float)(y + height)),
        };
        if (!TryProjectedLocalPoint(placement, point, out Vector2 local, out double planeDistance))
        {
            return false;
        }
        int winding = 0;
        int parity = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!TryPointLine(corners[i], corners[(i + 1) & 3], placement, point,
                    local, true, ref minimum, ref winding, ref parity))
            {
                return false;
            }
        }
        if (winding != 0)
        {
            minimum = Math.Min(minimum, planeDistance);
        }
        return true;
    }

    private static bool TryBoundsRectangle(
        double x,
        double y,
        double width,
        double height,
        in PathPlacement placement,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode,
        out bool hit)
    {
        Span<Vector2> corners = stackalloc Vector2[4]
        {
            new((float)x, (float)y),
            new((float)(x + width), (float)y),
            new((float)(x + width), (float)(y + height)),
            new((float)x, (float)(y + height)),
        };
        bool all = true;
        for (int i = 0; i < 4; i++)
        {
            Span<Vector2> edge = stackalloc Vector2[2]
                { corners[i], corners[(i + 1) & 3] };
            if (!TryBoundsBezier(edge, placement, bounds, mode, out bool edgeHit))
            {
                hit = false;
                return false;
            }
            if (mode == CadBoundsSelectionMode.Crossing && edgeHit)
            {
                hit = true;
                return true;
            }
            all &= edgeHit;
        }
        if (mode == CadBoundsSelectionMode.Window)
        {
            hit = all;
            return true;
        }
        // A box-plane slice wholly inside the rectangle has no boundary crossing.
        return TryPlaneBoxHasRectangleSample(
            placement,
            bounds,
            Math.Min(x, x + width),
            Math.Max(x, x + width),
            Math.Min(y, y + height),
            Math.Max(y, y + height),
            out hit);
    }

    private static bool TryPlaneBoxHasRectangleSample(
        in PathPlacement placement,
        CadBounds3D bounds,
        double minimumX,
        double maximumX,
        double minimumY,
        double maximumY,
        out bool contains)
    {
        contains = false;
        PathPlacement placementValue = placement;
        CadPoint3D normal = CadPoint3D.Cross(placement.XAxis, placement.YAxis);
        double normalLength = normal.Length;
        if (!(normalLength > 0.0) || !double.IsFinite(normalLength))
        {
            return false;
        }
        double coordinateScale = Math.Max(1.0,
            Math.Max(bounds.Min.Length, bounds.Max.Length));
        double epsilon = coordinateScale * normalLength * PlaneToleranceFactor;
        Span<CadPoint3D> vertices = stackalloc CadPoint3D[8];
        FillBoundsVertices(bounds, vertices);
        Span<double> distances = stackalloc double[8];
        for (int i = 0; i < vertices.Length; i++)
        {
            distances[i] = CadPoint3D.Dot(vertices[i] - placement.Origin, normal);
            if (Math.Abs(distances[i]) <= epsilon && Contains(vertices[i]))
            {
                contains = true;
                return true;
            }
        }
        ReadOnlySpan<byte> edges =
        [
            0,1, 2,3, 4,5, 6,7,
            0,2, 1,3, 4,6, 5,7,
            0,4, 1,5, 2,6, 3,7,
        ];
        for (int i = 0; i < edges.Length; i += 2)
        {
            int a = edges[i];
            int b = edges[i + 1];
            double da = distances[a];
            double db = distances[b];
            if ((da < -epsilon && db > epsilon) || (da > epsilon && db < -epsilon))
            {
                double t = da / (da - db);
                CadPoint3D sample = vertices[a] + ((vertices[b] - vertices[a]) * t);
                if (Contains(sample))
                {
                    contains = true;
                    return true;
                }
            }
        }
        return true;

        bool Contains(CadPoint3D sample)
        {
            if (!TryProjectedLocalPoint(placementValue, sample, out Vector2 local, out _))
            {
                return false;
            }
            return local.X >= minimumX && local.X <= maximumX &&
                local.Y >= minimumY && local.Y <= maximumY;
        }
    }

    private static bool TryProjectedLocalPoint(
        in PathPlacement placement,
        CadPoint3D point,
        out Vector2 local,
        out double planeDistance)
    {
        CadPoint3D delta = point - placement.Origin;
        double xx = CadPoint3D.Dot(placement.XAxis, placement.XAxis);
        double xy = CadPoint3D.Dot(placement.XAxis, placement.YAxis);
        double yy = CadPoint3D.Dot(placement.YAxis, placement.YAxis);
        double determinant = (xx * yy) - (xy * xy);
        if (!double.IsFinite(determinant) || determinant <= 0.0)
        {
            local = default;
            planeDistance = double.NaN;
            return false;
        }
        double dx = CadPoint3D.Dot(delta, placement.XAxis);
        double dy = CadPoint3D.Dot(delta, placement.YAxis);
        double u = ((dx * yy) - (dy * xy)) / determinant;
        double v = ((dy * xx) - (dx * xy)) / determinant;
        double rawY = (v - placement.OffsetY) / placement.ScaleY;
        double rawX = (u - placement.OffsetX - (rawY * placement.ShearX)) /
            placement.ScaleX;
        CadPoint3D projected = placement.Origin +
            (placement.XAxis * u) + (placement.YAxis * v);
        planeDistance = (point - projected).Length;
        if (!double.IsFinite(rawX) || !double.IsFinite(rawY) ||
            !double.IsFinite(planeDistance) ||
            rawX < float.MinValue || rawX > float.MaxValue ||
            rawY < float.MinValue || rawY > float.MaxValue)
        {
            local = default;
            return false;
        }
        local = new Vector2((float)rawX, (float)rawY);
        return true;
    }

    private static bool TryCreateArcPieces(
        Vector2 start,
        ArcSegment arc,
        Span<RationalPiece2D> destination,
        out int count)
    {
        count = 0;
        double rx = Math.Abs(arc.Size.X);
        double ry = Math.Abs(arc.Size.Y);
        if (!(rx > 0.0) || !(ry > 0.0) || !double.IsFinite(rx) || !double.IsFinite(ry))
        {
            return false;
        }
        double phi = arc.RotationAngle * (Math.PI / 180.0);
        double cosPhi = Math.Cos(phi);
        double sinPhi = Math.Sin(phi);
        double dx = (start.X - arc.Point.X) * 0.5;
        double dy = (start.Y - arc.Point.Y) * 0.5;
        double xPrime = (cosPhi * dx) + (sinPhi * dy);
        double yPrime = (-sinPhi * dx) + (cosPhi * dy);
        double radiiScale =
            ((xPrime * xPrime) / (rx * rx)) + ((yPrime * yPrime) / (ry * ry));
        if (radiiScale > 1.0)
        {
            double scale = Math.Sqrt(radiiScale);
            rx *= scale;
            ry *= scale;
        }
        double numerator = Math.Max(0.0,
            (rx * rx * ry * ry) - (rx * rx * yPrime * yPrime) -
            (ry * ry * xPrime * xPrime));
        double denominator =
            (rx * rx * yPrime * yPrime) + (ry * ry * xPrime * xPrime);
        if (!(denominator > 0.0))
        {
            return false;
        }
        bool sweepClockwise = arc.SweepDirection == SweepDirection.Clockwise;
        double sign = arc.IsLargeArc == sweepClockwise ? -1.0 : 1.0;
        double coefficient = sign * Math.Sqrt(numerator / denominator);
        double cxPrime = coefficient * ((rx * yPrime) / ry);
        double cyPrime = coefficient * (-(ry * xPrime) / rx);
        double centerX = (cosPhi * cxPrime) - (sinPhi * cyPrime) +
            ((start.X + arc.Point.X) * 0.5);
        double centerY = (sinPhi * cxPrime) + (cosPhi * cyPrime) +
            ((start.Y + arc.Point.Y) * 0.5);
        double startAngle = Math.Atan2(
            (yPrime - cyPrime) / ry,
            (xPrime - cxPrime) / rx);
        double endAngle = Math.Atan2(
            (-yPrime - cyPrime) / ry,
            (-xPrime - cxPrime) / rx);
        double sweep = endAngle - startAngle;
        if (sweepClockwise && sweep < 0.0)
        {
            sweep += Math.PI * 2.0;
        }
        else if (!sweepClockwise && sweep > 0.0)
        {
            sweep -= Math.PI * 2.0;
        }
        count = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / (Math.PI * 0.5)), 1, 4);
        double step = sweep / count;
        for (int i = 0; i < count; i++)
        {
            double a0 = startAngle + (step * i);
            double a1 = a0 + step;
            double middle = (a0 + a1) * 0.5;
            double weight = Math.Cos(step * 0.5);
            if (!(weight > 0.0))
            {
                return false;
            }
            Vector2 p0 = EllipsePoint(centerX, centerY, rx, ry, cosPhi, sinPhi, a0);
            Vector2 p2 = EllipsePoint(centerX, centerY, rx, ry, cosPhi, sinPhi, a1);
            double ux = (rx * Math.Cos(middle) * cosPhi) -
                (ry * Math.Sin(middle) * sinPhi);
            double uy = (rx * Math.Cos(middle) * sinPhi) +
                (ry * Math.Sin(middle) * cosPhi);
            destination[i] = new RationalPiece2D(
                p0,
                new Vector2(
                    checked((float)((centerX * weight) + ux)),
                    checked((float)((centerY * weight) + uy))),
                p2,
                1.0,
                weight,
                1.0);
        }
        return true;
    }

    private static Vector2 EllipsePoint(
        double centerX,
        double centerY,
        double rx,
        double ry,
        double cosPhi,
        double sinPhi,
        double angle) =>
        new(
            checked((float)(centerX + (rx * Math.Cos(angle) * cosPhi) -
                (ry * Math.Sin(angle) * sinPhi))),
            checked((float)(centerY + (rx * Math.Cos(angle) * sinPhi) +
                (ry * Math.Sin(angle) * cosPhi))));

    private static Vector2 Evaluate(ReadOnlySpan<Vector2> points, double t)
    {
        Span<Vector2> work = stackalloc Vector2[4];
        points.CopyTo(work);
        float parameter = (float)t;
        for (int level = 1; level < points.Length; level++)
        {
            for (int i = 0; i < points.Length - level; i++)
            {
                work[i] = Vector2.Lerp(work[i], work[i + 1], parameter);
            }
        }
        return work[0];
    }

    private static double EvaluateDerivativeY(ReadOnlySpan<Vector2> points, double t)
    {
        int degree = points.Length - 1;
        Span<Vector2> derivative = stackalloc Vector2[3];
        for (int i = 0; i < degree; i++)
        {
            derivative[i] = degree * (points[i + 1] - points[i]);
        }
        return Evaluate(derivative[..degree], t).Y;
    }

    private static Vector2 EndPoint(PathSegment segment) => segment switch
    {
        LineSegment line => line.Point,
        QuadraticBezierSegment quadratic => quadratic.Point,
        CubicBezierSegment cubic => cubic.Point,
        ArcSegment arc => arc.Point,
        _ => default,
    };

    private static CadPoint3D Transform(
        CadPoint3D origin,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        double x,
        double y) => origin + (xAxis * x) + (yAxis * y);

    private static CadHomogeneousPoint Homogeneous(CadPoint3D point) =>
        new(point.X, point.Y, point.Z, 1.0);

    private static void FillBoundsVertices(CadBounds3D bounds, Span<CadPoint3D> values)
    {
        for (int i = 0; i < 8; i++)
        {
            values[i] = new CadPoint3D(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
        }
    }

    private static CadPointHitResult FromPoint(double distance, double tolerance, bool hasGeometry) =>
        new(
            hasGeometry && distance <= tolerance
                ? CadPointHitStatus.Hit
                : CadPointHitStatus.Miss,
            hasGeometry ? distance : double.PositiveInfinity);

    private static CadPointHitResult UnsupportedPoint() =>
        new(CadPointHitStatus.UnsupportedGeometry, double.NaN);

    private static CadBoundsHitResult BoundsHit() => new(CadBoundsHitStatus.Hit);
    private static CadBoundsHitResult BoundsMiss() => new(CadBoundsHitStatus.Miss);
    private static CadBoundsHitResult BoundsUnsupported() =>
        new(CadBoundsHitStatus.UnsupportedGeometry);

    private readonly record struct PathPlacement(
        CadPoint3D Origin,
        CadPoint3D XAxis,
        CadPoint3D YAxis,
        double OffsetX,
        double OffsetY,
        double ScaleX,
        double ScaleY,
        double ShearX = 0.0)
    {
        public bool IsFiniteAndNonDegenerate =>
            AreFinite(Origin) && AreFinite(XAxis) && AreFinite(YAxis) &&
            double.IsFinite(OffsetX) && double.IsFinite(OffsetY) &&
            double.IsFinite(ScaleX) && double.IsFinite(ScaleY) && double.IsFinite(ShearX) &&
            ScaleX != 0.0 && ScaleY != 0.0 &&
            CadPoint3D.Cross(XAxis, YAxis).Length > 0.0;

        public CadPoint3D Transform(Vector2 point) =>
            Origin +
            (XAxis * (OffsetX + (point.X * ScaleX) + (point.Y * ShearX))) +
            (YAxis * (OffsetY + (point.Y * ScaleY)));

        public void Transform(in RationalPiece2D piece, Span<CadHomogeneousPoint> values)
        {
            values[0] = TransformHomogeneous(piece.P0, piece.W0);
            values[1] = TransformHomogeneous(piece.P1, piece.W1);
            values[2] = TransformHomogeneous(piece.P2, piece.W2);
        }

        private CadHomogeneousPoint TransformHomogeneous(Vector2 point, double weight)
        {
            CadPoint3D homogeneous =
                (Origin * weight) +
                (XAxis * ((OffsetX * weight) + (point.X * ScaleX) + (point.Y * ShearX))) +
                (YAxis * ((OffsetY * weight) + (point.Y * ScaleY)));
            return new CadHomogeneousPoint(
                homogeneous.X, homogeneous.Y, homogeneous.Z, weight);
        }

        private static bool AreFinite(CadPoint3D point) =>
            double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
    }

    private readonly record struct RationalPiece2D(
        Vector2 P0,
        Vector2 P1,
        Vector2 P2,
        double W0,
        double W1,
        double W2)
    {
        public bool TryEvaluate(double t, out Vector2 point, out Vector2 derivative)
        {
            double oneMinus = 1.0 - t;
            double b0 = oneMinus * oneMinus;
            double b1 = 2.0 * oneMinus * t;
            double b2 = t * t;
            double x = (b0 * P0.X) + (b1 * P1.X) + (b2 * P2.X);
            double y = (b0 * P0.Y) + (b1 * P1.Y) + (b2 * P2.Y);
            double w = (b0 * W0) + (b1 * W1) + (b2 * W2);
            double dx = 2.0 * (((1.0 - t) * (P1.X - P0.X)) + (t * (P2.X - P1.X)));
            double dy = 2.0 * (((1.0 - t) * (P1.Y - P0.Y)) + (t * (P2.Y - P1.Y)));
            double dw = 2.0 * (((1.0 - t) * (W1 - W0)) + (t * (W2 - W1)));
            if (!(w > 0.0) || !double.IsFinite(w))
            {
                point = default;
                derivative = default;
                return false;
            }
            point = new Vector2((float)(x / w), (float)(y / w));
            derivative = new Vector2(
                (float)(((dx * w) - (x * dw)) / (w * w)),
                (float)(((dy * w) - (y * dw)) / (w * w)));
            return float.IsFinite(point.X) && float.IsFinite(point.Y) &&
                float.IsFinite(derivative.X) && float.IsFinite(derivative.Y);
        }
    }
}

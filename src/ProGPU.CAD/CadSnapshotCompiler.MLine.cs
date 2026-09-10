using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ProGPU.Text;
using System.Numerics;

namespace ProGPU.CAD;

public sealed partial class CadSnapshotCompiler
{
    /// <summary>
    /// Lowers the persisted MLINE parameterization, rather than regenerating
    /// offsets from the center path. For V vertices, E style elements, and P
    /// persisted cut parameters, capture is O(V * E + P) time and storage.
    /// </summary>
    private static CadEntityHeader CompileMLine(
        MLine source,
        ulong rootHandle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int entityStyleIndex,
        CadResolvedStyle entityStyle,
        Layer effectiveLayer,
        CadSnapshotOptions options,
        List<CadMLinePrimitive> primitives,
        List<CadMLineElementPath> elementPaths,
        List<CadMLineStroke> strokes,
        List<CadMLineFillTriangle> fillTriangles,
        List<CadStrokeStyle> styles,
        Dictionary<CadStrokeStyle, int> styleIndices,
        List<CadLineTypePattern> lineTypePatterns,
        Dictionary<string, int> lineTypePatternIndices,
        List<CadLineTypeElement> lineTypeElements,
        List<CadLineTypeTextResource> lineTypeTextResources,
        List<CadLineTypeShapeResource> lineTypeShapeResources,
        List<CadTextGlyphRun> textGlyphRuns,
        List<ushort> textGlyphIndices,
        List<Vector2> textGlyphPositions,
        List<TtfFont> textFonts,
        Dictionary<TtfFont, int> textFontIndices,
        List<CadShxGlyphInstance> shxGlyphInstances,
        ICadShxFontResolver? shxFontResolver,
        string drawingCodePage)
    {
        MLineStyle style = source.Style ?? throw new ArgumentException("MLINE has no style.");
        MLineStyle.Element[] elements = style.Elements.ToArray();
        if (source.Vertices.Count < 2 || elements.Length == 0)
        {
            throw new ArgumentException("MLINE requires at least two vertices and one style element.");
        }

        bool closed = (source.Flags & MLineFlags.Closed) != 0;
        int sourceSegmentCount = closed ? source.Vertices.Count : source.Vertices.Count - 1;
        int strokeStart = strokes.Count;
        int elementPathStart = elementPaths.Count;
        int fillStart = fillTriangles.Count;
        int primitiveIndex = primitives.Count;
        CadBounds3D bounds = CadBounds3D.Empty;

        int[] elementStyleIndices = new int[elements.Length];
        for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
        {
            MLineStyle.Element element = elements[elementIndex];
            ACadSharp.Color color = element.Color.IsByLayer
                ? effectiveLayer.Color
                : element.Color.IsByBlock
                    ? entityStyle.Color
                    : element.Color;
            color = ResolveBackgroundAdaptiveColor(color, options.DrawingBackgroundColor);
            LineType lineType = element.LineType is null || element.LineType.Name.Equals(
                    LineType.ByLayerName,
                    StringComparison.OrdinalIgnoreCase)
                ? effectiveLayer.LineType
                : element.LineType.Name.Equals(
                    LineType.ByBlockName,
                    StringComparison.OrdinalIgnoreCase)
                    ? entityStyle.LineType
                    : element.LineType;
            elementStyleIndices[elementIndex] = InternStyle(
                entityStyle with { Color = color, LineType = lineType },
                styles,
                styleIndices,
                lineTypePatterns,
                lineTypePatternIndices,
                lineTypeElements,
                lineTypeTextResources,
                lineTypeShapeResources,
                textGlyphRuns,
                textGlyphIndices,
                textGlyphPositions,
                textFonts,
                textFontIndices,
                shxGlyphInstances,
                shxFontResolver,
                options,
                drawingCodePage);
        }

        ACadSharp.Color fillColor = style.FillColor.IsByLayer
            ? effectiveLayer.Color
            : style.FillColor.IsByBlock
                ? entityStyle.Color
                : style.FillColor;
        fillColor = ResolveBackgroundAdaptiveColor(fillColor, options.DrawingBackgroundColor);
        var retainedFillColor = new CadColor32(
            fillColor.R,
            fillColor.G,
            fillColor.B,
            styles[entityStyleIndex].Alpha);

        try
        {
            for (int vertexIndex = 0; vertexIndex < source.Vertices.Count; vertexIndex++)
            {
                if (source.Vertices[vertexIndex].Segments.Count != elements.Length)
                {
                    throw new ArgumentException(
                        "Every MLINE vertex must contain one parameter set per style element.");
                }
            }

            for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
            {
                int elementStrokeStart = strokes.Count;
                double pathLength = 0.0;
                for (int vertexIndex = 0; vertexIndex < sourceSegmentCount; vertexIndex++)
                {
                    MLine.Vertex startVertex = source.Vertices[vertexIndex];
                    MLine.Vertex endVertex = source.Vertices[(vertexIndex + 1) % source.Vertices.Count];
                    CadPoint3D direction = ToPoint(startVertex.Direction).Normalize();
                    CadPoint3D startPosition = ToPoint(startVertex.Position);
                    CadPoint3D endPosition = ToPoint(endVertex.Position);
                    CadPoint3D startMiter = ToPoint(startVertex.Miter);
                    CadPoint3D endMiter = ToPoint(endVertex.Miter);
                    EnsureFinite(startPosition);
                    EnsureFinite(endPosition);
                    EnsureFinite(startMiter);
                    EnsureFinite(endMiter);
                    MLine.Vertex.Segment startParameters = startVertex.Segments[elementIndex];
                    MLine.Vertex.Segment endParameters = endVertex.Segments[elementIndex];
                    ValidateMLineParameters(startParameters.Parameters, requireStart: true);
                    ValidateMLineParameters(endParameters.Parameters, requireStart: false);
                    CadPoint3D startAnchor = startPosition +
                        (startMiter * startParameters.Parameters[0]);
                    CadPoint3D endAnchor = endPosition +
                        (endMiter * endParameters.Parameters[0]);
                    double terminal = CadPoint3D.Dot(endAnchor - startAnchor, direction);
                    if (!double.IsFinite(terminal) || terminal < 0.0)
                    {
                        throw new ArgumentException(
                            "MLINE element direction does not reach its next miter intersection.");
                    }

                    AppendMLineIntervals(
                        startAnchor,
                        direction,
                        terminal,
                        startParameters.Parameters,
                        pathLength,
                        transform,
                        hasTransform,
                        options.MaxMLineStrokes,
                        strokes,
                        ref bounds);
                    CadPoint3D worldStartAnchor = hasTransform
                        ? transform.TransformPoint(startAnchor)
                        : startAnchor;
                    CadPoint3D worldEndAnchor = hasTransform
                        ? transform.TransformPoint(endAnchor)
                        : endAnchor;
                    double worldLength = (worldEndAnchor - worldStartAnchor).Length;
                    if (!double.IsFinite(worldLength))
                    {
                        throw new ArithmeticException("MLINE path length exceeds the supported numeric range.");
                    }
                    pathLength += worldLength;
                }

                elementPaths.Add(new CadMLineElementPath(
                    elementStrokeStart,
                    strokes.Count - elementStrokeStart,
                    elementStyleIndices[elementIndex],
                    pathLength,
                    closed));
            }

            for (int vertexIndex = 0; vertexIndex < sourceSegmentCount; vertexIndex++)
            {
                if ((style.Flags & MLineStyleFlags.FillOn) != 0)
                {
                    MLine.Vertex startVertex = source.Vertices[vertexIndex];
                    MLine.Vertex endVertex = source.Vertices[(vertexIndex + 1) % source.Vertices.Count];
                    AppendMLineFill(
                        startVertex,
                        endVertex,
                        elements,
                        ToPoint(startVertex.Direction).Normalize(),
                        transform,
                        hasTransform,
                        retainedFillColor,
                        options.MaxMLineFillTriangles,
                        fillTriangles,
                        ref bounds);
                }
            }

            if (strokes.Count == strokeStart && fillTriangles.Count == fillStart)
            {
                throw new ArgumentException("MLINE contains no visible retained geometry.");
            }

            primitives.Add(new CadMLinePrimitive(
                elementPathStart,
                elementPaths.Count - elementPathStart,
                strokeStart,
                strokes.Count - strokeStart,
                fillStart,
                fillTriangles.Count - fillStart));
            return new CadEntityHeader(
                rootHandle,
                CadEntityKind.MLine,
                layerIndex,
                entityStyleIndex,
                primitiveIndex,
                bounds);
        }
        catch
        {
            if (elementPaths.Count > elementPathStart)
            {
                elementPaths.RemoveRange(elementPathStart, elementPaths.Count - elementPathStart);
            }
            if (strokes.Count > strokeStart)
            {
                strokes.RemoveRange(strokeStart, strokes.Count - strokeStart);
            }
            if (fillTriangles.Count > fillStart)
            {
                fillTriangles.RemoveRange(fillStart, fillTriangles.Count - fillStart);
            }
            throw;
        }
    }

    private static void AppendMLineIntervals(
        CadPoint3D anchor,
        CadPoint3D direction,
        double terminal,
        IReadOnlyList<double> parameters,
        double pathBase,
        CadAffineTransform3D transform,
        bool hasTransform,
        int limit,
        List<CadMLineStroke> destination,
        ref CadBounds3D bounds)
    {
        double origin = parameters[1];
        double intervalStart = origin;
        bool draws = true;
        for (int index = 2; index < parameters.Count; index++)
        {
            double endpoint = origin + parameters[index];
            if (endpoint < intervalStart)
            {
                throw new ArgumentException("MLINE cut parameters must be nondecreasing.");
            }
            if (draws && endpoint > intervalStart)
            {
                AppendMLineStroke(anchor, direction, intervalStart, endpoint, pathBase,
                    transform, hasTransform, limit, destination, ref bounds);
            }
            intervalStart = endpoint;
            draws = !draws;
        }
        if (draws && terminal > intervalStart)
        {
            AppendMLineStroke(anchor, direction, intervalStart, terminal, pathBase,
                transform, hasTransform, limit, destination, ref bounds);
        }
    }

    private static void AppendMLineStroke(
        CadPoint3D anchor,
        CadPoint3D direction,
        double startDistance,
        double endDistance,
        double pathBase,
        CadAffineTransform3D transform,
        bool hasTransform,
        int limit,
        List<CadMLineStroke> destination,
        ref CadBounds3D bounds)
    {
        if (destination.Count >= limit)
        {
            throw new CadSnapshotExpansionLimitException(
                $"MLINE stroke count exceeds the configured limit of {limit}.");
        }
        CadPoint3D start = anchor + (direction * startDistance);
        CadPoint3D end = anchor + (direction * endDistance);
        CadPoint3D pathAnchor = anchor;
        if (hasTransform)
        {
            start = transform.TransformPoint(start);
            end = transform.TransformPoint(end);
            pathAnchor = transform.TransformPoint(pathAnchor);
        }
        EnsureFinite(start);
        EnsureFinite(end);
        destination.Add(new CadMLineStroke(
            start,
            end,
            pathBase + (start - pathAnchor).Length,
            pathBase + (end - pathAnchor).Length));
        bounds = bounds.Include(start).Include(end);
    }

    private static void AppendMLineFill(
        MLine.Vertex startVertex,
        MLine.Vertex endVertex,
        MLineStyle.Element[] elements,
        CadPoint3D direction,
        CadAffineTransform3D transform,
        bool hasTransform,
        CadColor32 color,
        int limit,
        List<CadMLineFillTriangle> destination,
        ref CadBounds3D bounds)
    {
        int low = 0;
        int high = 0;
        for (int index = 1; index < elements.Length; index++)
        {
            if (elements[index].Offset < elements[low].Offset) low = index;
            if (elements[index].Offset > elements[high].Offset) high = index;
        }
        if (low == high)
        {
            return;
        }

        MLine.Vertex.Segment lowStart = startVertex.Segments[low];
        MLine.Vertex.Segment highStart = startVertex.Segments[high];
        MLine.Vertex.Segment lowEnd = endVertex.Segments[low];
        MLine.Vertex.Segment highEnd = endVertex.Segments[high];
        CadPoint3D startPosition = ToPoint(startVertex.Position);
        CadPoint3D endPosition = ToPoint(endVertex.Position);
        CadPoint3D startMiter = ToPoint(startVertex.Miter);
        CadPoint3D endMiter = ToPoint(endVertex.Miter);
        bool hasLowCuts = lowStart.AreaFillParameters.Count != 0;
        bool hasHighCuts = highStart.AreaFillParameters.Count != 0;
        if (hasLowCuts != hasHighCuts)
        {
            throw new ArgumentException("MLINE outer fill boundaries must both provide cuts or both omit them.");
        }
        IReadOnlyList<double> lowParameters = hasLowCuts
            ? lowStart.AreaFillParameters
            : lowStart.Parameters;
        IReadOnlyList<double> highParameters = hasHighCuts
            ? highStart.AreaFillParameters
            : highStart.Parameters;
        ValidateMLineParameters(lowParameters, requireStart: true);
        ValidateMLineParameters(highParameters, requireStart: true);
        if (hasLowCuts &&
            (lowEnd.AreaFillParameters.Count == 0 || highEnd.AreaFillParameters.Count == 0))
        {
            throw new ArgumentException("MLINE fill cuts must continue through the next vertex.");
        }
        IReadOnlyList<double> lowEndParameters = hasLowCuts
            ? lowEnd.AreaFillParameters
            : lowEnd.Parameters;
        IReadOnlyList<double> highEndParameters = hasHighCuts
            ? highEnd.AreaFillParameters
            : highEnd.Parameters;
        ValidateMLineParameters(lowEndParameters, requireStart: false);
        ValidateMLineParameters(highEndParameters, requireStart: false);
        CadPoint3D lowAnchor = startPosition + (startMiter * lowParameters[0]);
        CadPoint3D highAnchor = startPosition + (startMiter * highParameters[0]);
        CadPoint3D lowTerminal = endPosition + (endMiter * lowEndParameters[0]);
        CadPoint3D highTerminal = endPosition + (endMiter * highEndParameters[0]);
        double lowLength = CadPoint3D.Dot(lowTerminal - lowAnchor, direction);
        double highLength = CadPoint3D.Dot(highTerminal - highAnchor, direction);

        if (!hasLowCuts)
        {
            AppendMLineQuad(
                lowAnchor + (direction * lowParameters[1]),
                highAnchor + (direction * highParameters[1]),
                highTerminal,
                lowTerminal,
                transform,
                hasTransform,
                color,
                limit,
                destination,
                ref bounds);
            return;
        }
        if (lowParameters.Count != highParameters.Count)
        {
            throw new ArgumentException("MLINE outer fill boundaries must have matching cut counts.");
        }

        double lowOrigin = lowParameters[1];
        double highOrigin = highParameters[1];
        double lowStartDistance = lowOrigin;
        double highStartDistance = highOrigin;
        bool fills = true;
        for (int index = 2; index <= lowParameters.Count; index++)
        {
            double lowEndDistance = index < lowParameters.Count
                ? lowOrigin + lowParameters[index]
                : lowLength;
            double highEndDistance = index < highParameters.Count
                ? highOrigin + highParameters[index]
                : highLength;
            if (fills && lowEndDistance > lowStartDistance && highEndDistance > highStartDistance)
            {
                AppendMLineQuad(
                    lowAnchor + (direction * lowStartDistance),
                    highAnchor + (direction * highStartDistance),
                    highAnchor + (direction * highEndDistance),
                    lowAnchor + (direction * lowEndDistance),
                    transform,
                    hasTransform,
                    color,
                    limit,
                    destination,
                    ref bounds);
            }
            lowStartDistance = lowEndDistance;
            highStartDistance = highEndDistance;
            fills = !fills;
        }
    }

    private static void AppendMLineQuad(
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third,
        CadPoint3D fourth,
        CadAffineTransform3D transform,
        bool hasTransform,
        CadColor32 color,
        int limit,
        List<CadMLineFillTriangle> destination,
        ref CadBounds3D bounds)
    {
        if (destination.Count > limit - 2)
        {
            throw new CadSnapshotExpansionLimitException(
                $"MLINE fill triangle count exceeds the configured limit of {limit}.");
        }
        if (hasTransform)
        {
            first = transform.TransformPoint(first);
            second = transform.TransformPoint(second);
            third = transform.TransformPoint(third);
            fourth = transform.TransformPoint(fourth);
        }
        EnsureFinite(first);
        EnsureFinite(second);
        EnsureFinite(third);
        EnsureFinite(fourth);
        destination.Add(new CadMLineFillTriangle(first, second, third, color));
        destination.Add(new CadMLineFillTriangle(first, third, fourth, color));
        bounds = bounds.Include(first).Include(second).Include(third).Include(fourth);
    }

    private static void ValidateMLineParameters(
        IReadOnlyList<double> parameters,
        bool requireStart)
    {
        int minimum = requireStart ? 2 : 1;
        if (parameters.Count < minimum)
        {
            throw new ArgumentException(
                $"MLINE parameter list requires at least {minimum} value(s).");
        }
        for (int index = 0; index < parameters.Count; index++)
        {
            if (!double.IsFinite(parameters[index]))
            {
                throw new ArgumentException("MLINE parameters must be finite.");
            }
        }
    }
}

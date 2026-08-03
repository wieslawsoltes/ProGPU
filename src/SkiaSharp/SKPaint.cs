#pragma warning disable CS0618 // The shim internally composes its official legacy SKPath contract.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Scene;
using ProGPU.Vector;

namespace SkiaSharp;

public enum SKPaintStyle
{
    Fill = 0,
    Stroke = 1,
    StrokeAndFill = 2,
}

public partial class SKPaint : SKObject
{
    [ThreadStatic]
    private static List<Vector2>? s_strokePointCache;

    private const float HairlineStrokeWidth = 1f;
    private SKShader? _shader;
    private SKBlender? _blender;
    private SKBlendMode _blendMode = SKBlendMode.SrcOver;
    private SKPathEffect? _pathEffect;
    private float _strokeWidth;
    private SolidColorBrush? _retainedSolidBrush;
    private SKColor _retainedSolidBrushColor;
    private Pen? _retainedScaledPen;
    private Pen? _retainedLocalPen;

    public SKPaintStyle Style { get; set; } = SKPaintStyle.Fill;
    public SKColor Color { get; set; } = SKColors.Black;
    public SKColorF ColorF
    {
        get => new(Color.R / 255f, Color.G / 255f, Color.B / 255f, Color.A / 255f);
        set => Color = new SKColor(
            ToColorByte(value.R),
            ToColorByte(value.G),
            ToColorByte(value.B),
            ToColorByte(value.A));
    }
    public bool IsStroke
    {
        get => Style != SKPaintStyle.Fill;
        set => Style = value ? SKPaintStyle.Stroke : SKPaintStyle.Fill;
    }
    public float StrokeWidth
    {
        get => _strokeWidth;
        set => _strokeWidth = value >= 0f ? value : 0f;
    }
    public float StrokeMiter { get; set; } = 4f;
    public SKStrokeCap StrokeCap { get; set; } = SKStrokeCap.Butt;
    public SKStrokeJoin StrokeJoin { get; set; } = SKStrokeJoin.Miter;
    public SKShader? Shader
    {
        get => _shader;
        set
        {
            if (ReferenceEquals(_shader, value))
            {
                return;
            }

            value?.AddReference();
            _shader?.ReleaseReference();
            _shader = value;
        }
    }
    public SKColorFilter? ColorFilter { get; set; }
    public SKImageFilter? ImageFilter { get; set; }
    public SKPathEffect? PathEffect
    {
        get => _pathEffect;
        set
        {
            if (ReferenceEquals(_pathEffect, value))
            {
                return;
            }

            value?.AddReference();
            _pathEffect?.ReleaseReference();
            _pathEffect = value;
        }
    }
    public SKBlender? Blender
    {
        get
        {
            if (_blender == null && _blendMode != SKBlendMode.SrcOver)
            {
                _blender = SKBlender.CreateBlendMode(_blendMode);
            }

            return _blender;
        }
        set
        {
            _blender = value;
            _blendMode = value != null && value.TryGetBlendMode(out var mode)
                ? mode
                : SKBlendMode.SrcOver;
        }
    }
    public SKBlendMode BlendMode
    {
        get => _blendMode;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _blendMode = value;
            _blender = null;
        }
    }

    internal bool HasArithmeticBlender => _blender?.IsArithmetic == true;
    public bool IsAntialias
    {
        get => _isAntialias;
        set
        {
            _isAntialias = value;
            UpdateLegacyFontEdging();
        }
    }
    [Obsolete("Use SKFont.Typeface instead.", true)]
    public SKTypeface Typeface
    {
        get => _legacyFont.Typeface;
        set => _legacyFont.Typeface = value;
    }
    [Obsolete("Use SKFont.Size instead.", true)]
    public float TextSize
    {
        get => _legacyFont.Size;
        set => _legacyFont.Size = value;
    }

    public SKPaint Clone()
    {
        var clone = new SKPaint
        {
            Style = Style,
            Color = Color,
            StrokeWidth = StrokeWidth,
            StrokeMiter = StrokeMiter,
            StrokeCap = StrokeCap,
            StrokeJoin = StrokeJoin,
            Shader = Shader,
            ColorFilter = ColorFilter,
            ImageFilter = ImageFilter,
            PathEffect = PathEffect,
            IsAntialias = IsAntialias,
            IsDither = IsDither,
            MaskFilter = MaskFilter,
        };
        clone._blender = _blender;
        clone._blendMode = _blendMode;
        clone.CopyLegacyTextStateFrom(this);
        return clone;
    }

    public bool GetFastBounds(SKRect bounds, out SKRect fastBounds)
    {
        if (!IsFinite(bounds))
        {
            fastBounds = bounds;
            return false;
        }

        var outset = 0f;
        if (Style != SKPaintStyle.Fill)
        {
            var strokeRadius = StrokeWidth == 0f ? 0.5f : StrokeWidth * 0.5f;
            if (!float.IsFinite(strokeRadius))
            {
                fastBounds = bounds;
                return false;
            }

            outset = StrokeJoin == SKStrokeJoin.Miter
                ? strokeRadius * Math.Max(1f, StrokeMiter)
                : strokeRadius;
        }

        if (MaskFilter is { Kind: SKMaskFilter.MaskFilterKind.Blur } maskFilter)
        {
            outset += 3f * maskFilter.Sigma;
        }
        else if (MaskFilter is { Kind: SKMaskFilter.MaskFilterKind.Shader })
        {
            fastBounds = bounds;
            return false;
        }

        if (ImageFilter != null)
        {
            fastBounds = bounds;
            return false;
        }

        fastBounds = new SKRect(
            bounds.Left - outset,
            bounds.Top - outset,
            bounds.Right + outset,
            bounds.Bottom + outset);
        return true;
    }

    private static bool IsFinite(SKRect rect) =>
        float.IsFinite(rect.Left) &&
        float.IsFinite(rect.Top) &&
        float.IsFinite(rect.Right) &&
        float.IsFinite(rect.Bottom);

    public Brush? ToBrush()
    {
        if (Style == SKPaintStyle.Stroke) return null;

        return ToFillBrush();
    }

    internal Brush ToFillBrush()
    {
        if (Shader != null)
        {
            return ApplyMaskFilter(ApplyPaintAlphaToShaderBrush(
                SKShader.ApplyColorFilter(Shader.ToBrush(), ColorFilter),
                Color));
        }

        var color = GetFilteredColor();
        var c = new Vector4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);
        return ApplyMaskFilter(new SolidColorBrush(c));
    }

    // Ordinary Avalonia drawing repeatedly records the same mutable SKPaint.
    // Reuse only the package-private retained resources for the immutable,
    // effect-free solid-color case. Public conversion methods continue to
    // return independent mutable objects. Lookup is allocation-free O(1).
    internal Brush? ToRetainedBrush()
    {
        if (Style == SKPaintStyle.Stroke)
        {
            return null;
        }

        return TryGetRetainedSolidBrush(out var brush)
            ? brush
            : ToFillBrush();
    }

    public Pen? ToPen()
    {
        return ToPen(1f);
    }

    public Pen? ToPen(float strokeScale)
    {
        if (Style == SKPaintStyle.Fill) return null;

        var scaledStrokeWidth = ScaleStrokeWidth(StrokeWidth, strokeScale);
        Brush penBrush;
        if (Shader != null)
        {
            penBrush = ApplyPaintAlphaToShaderBrush(
                SKShader.ApplyColorFilter(Shader.ToBrush(), ColorFilter),
                Color);
        }
        else
        {
            var color = GetFilteredColor();
            var c = new Vector4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);
            penBrush = new SolidColorBrush(c);
        }
        var (dashArray, dashOffset) = MapDashEffect(PathEffect, scaledStrokeWidth);

        penBrush = ApplyMaskFilter(penBrush);

        return new Pen(
            penBrush,
            scaledStrokeWidth,
            MapStrokeJoin(StrokeJoin),
            StrokeMiter,
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            dashArray,
            dashOffset);
    }

    internal Pen? ToRetainedPen(float strokeScale)
    {
        if (Style == SKPaintStyle.Fill)
        {
            return null;
        }

        var scaledStrokeWidth = ScaleStrokeWidth(StrokeWidth, strokeScale);
        if (!TryGetRetainedSolidBrush(out var brush))
        {
            return ToPen(strokeScale);
        }

        return GetOrCreateRetainedPen(
            ref _retainedScaledPen,
            brush,
            scaledStrokeWidth);
    }

    internal Pen? ToLocalPen(float strokeScale)
    {
        if (Style == SKPaintStyle.Fill) return null;

        var localStrokeWidth = StrokeWidth;
        if (localStrokeWidth == 0f)
        {
            localStrokeWidth = float.IsFinite(strokeScale) && strokeScale > 0f
                ? HairlineStrokeWidth / strokeScale
                : HairlineStrokeWidth;
        }

        if (TryGetRetainedSolidBrush(out var retainedBrush))
        {
            return GetOrCreateRetainedPen(
                ref _retainedLocalPen,
                retainedBrush,
                localStrokeWidth);
        }

        Brush penBrush;
        if (Shader != null)
        {
            penBrush = ApplyPaintAlphaToShaderBrush(
                SKShader.ApplyColorFilter(Shader.ToBrush(), ColorFilter),
                Color);
        }
        else
        {
            var color = GetFilteredColor();
            penBrush = new SolidColorBrush(new Vector4(
                color.R / 255.0f,
                color.G / 255.0f,
                color.B / 255.0f,
                color.A / 255.0f));
        }

        var (dashArray, dashOffset) = MapDashEffect(PathEffect, localStrokeWidth);
        penBrush = ApplyMaskFilter(penBrush);
        return new Pen(
            penBrush,
            localStrokeWidth,
            MapStrokeJoin(StrokeJoin),
            StrokeMiter,
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            dashArray,
            dashOffset);
    }

    private bool TryGetRetainedSolidBrush(out SolidColorBrush brush)
    {
        if (Shader != null ||
            ColorFilter != null ||
            MaskFilter != null ||
            PathEffect != null)
        {
            brush = null!;
            return false;
        }

        if (_retainedSolidBrush == null ||
            _retainedSolidBrushColor != Color)
        {
            var color = Color;
            _retainedSolidBrush = new SolidColorBrush(new Vector4(
                color.R / 255.0f,
                color.G / 255.0f,
                color.B / 255.0f,
                color.A / 255.0f));
            _retainedSolidBrushColor = color;
        }

        brush = _retainedSolidBrush;
        return true;
    }

    private Pen GetOrCreateRetainedPen(
        ref Pen? cached,
        SolidColorBrush brush,
        float strokeWidth)
    {
        var lineJoin = MapStrokeJoin(StrokeJoin);
        var lineCap = MapStrokeCap(StrokeCap);
        if (cached == null ||
            !ReferenceEquals(cached.Brush, brush) ||
            cached.Thickness != strokeWidth ||
            cached.LineJoin != lineJoin ||
            cached.MiterLimit != StrokeMiter ||
            cached.StartLineCap != lineCap ||
            cached.EndLineCap != lineCap ||
            cached.DashCap != lineCap)
        {
            cached = new Pen(
                brush,
                strokeWidth,
                lineJoin,
                StrokeMiter,
                lineCap,
                lineCap,
                lineCap);
        }

        return cached;
    }

    internal Pen ToPen(Brush brush, float strokeScale)
    {
        var scaledStrokeWidth = ScaleStrokeWidth(StrokeWidth, strokeScale);
        var (dashArray, dashOffset) = MapDashEffect(PathEffect, scaledStrokeWidth);
        return new Pen(
            ApplyMaskFilter(brush),
            scaledStrokeWidth,
            MapStrokeJoin(StrokeJoin),
            StrokeMiter,
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            dashArray,
            dashOffset);
    }

    private Brush ApplyMaskFilter(Brush brush) =>
        MaskFilter == null
            ? brush
            : new SKMaskFilterBrush(brush, MaskFilter);

    public void Reset()
    {
        Style = SKPaintStyle.Fill;
        Color = SKColors.Black;
        StrokeWidth = 0f;
        StrokeMiter = 4f;
        StrokeCap = SKStrokeCap.Butt;
        StrokeJoin = SKStrokeJoin.Miter;
        Shader = null;
        ColorFilter = null;
        ImageFilter = null;
        PathEffect = null;
        Blender = null;
        MaskFilter = null;
        ResetLegacyTextState();
    }

    protected override void Dispose(bool disposing)
    {
        Shader = null;
        PathEffect = null;
        Blender = null;
        _legacyFont.Dispose();
        base.Dispose(disposing);
    }

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }

    public SKPath? GetFillPath(SKPath src) => GetFillPath(src, 1f);

    public SKPath? GetFillPath(SKPath src, float resScale)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (TryCreateFillPath(src, NormalizeResolutionScale(resScale), out var result))
        {
            return result;
        }

        result.Dispose();
        return null;
    }

    public SKPath? GetFillPath(SKPath src, SKRect cullRect) =>
        GetFillPath(src, cullRect, 1f);

    public SKPath? GetFillPath(SKPath src, SKRect cullRect, float resScale) =>
        GetFillPath(src, resScale);

    public SKPath? GetFillPath(SKPath src, SKMatrix matrix) =>
        GetFillPath(src, GetResolutionScale(matrix));

    public SKPath? GetFillPath(SKPath src, SKRect cullRect, SKMatrix matrix) =>
        GetFillPath(src, GetResolutionScale(matrix));

    [Obsolete("Use the SKPathBuilder overload instead.")]
    public bool GetFillPath(SKPath src, SKPath dst) => GetFillPath(src, dst, 1f);

    [Obsolete("Use the SKPathBuilder overload instead.")]
    public bool GetFillPath(SKPath src, SKPath dst, float resScale)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        var normalizedScale = NormalizeResolutionScale(resScale);
        if (!ReferenceEquals(src, dst) &&
            PathEffect is not { IsDash: false } &&
            (Style != SKPaintStyle.Stroke || StrokeWidth != 0f))
        {
            dst.Reset();
            return TryPopulateFillPath(src, normalizedScale, dst);
        }

        if (!TryCreateFillPath(src, normalizedScale, out var result))
        {
            result.Dispose();
            return false;
        }

        dst.ReplaceWithOwned(result);
        result.Dispose();

        return true;
    }

    [Obsolete("Use the SKPathBuilder overload instead.")]
    public bool GetFillPath(SKPath src, SKPath dst, SKRect cullRect) =>
        GetFillPath(src, dst, 1f);

    [Obsolete("Use the SKPathBuilder overload instead.")]
    public bool GetFillPath(SKPath src, SKPath dst, SKRect cullRect, float resScale) =>
        GetFillPath(src, dst, resScale);

    [Obsolete("Use the SKPathBuilder overload instead.")]
    public bool GetFillPath(SKPath src, SKPath dst, SKMatrix matrix) =>
        GetFillPath(src, dst, GetResolutionScale(matrix));

    [Obsolete("Use the SKPathBuilder overload instead.")]
    public bool GetFillPath(SKPath src, SKPath dst, SKRect cullRect, SKMatrix matrix) =>
        GetFillPath(src, dst, GetResolutionScale(matrix));

    public bool GetFillPath(SKPath src, SKPathBuilder dst) => GetFillPath(src, dst, 1f);

    public bool GetFillPath(SKPath src, SKPathBuilder dst, float resScale)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);

        if (!TryCreateFillPath(src, NormalizeResolutionScale(resScale), out var result))
        {
            result.Dispose();
            dst.ReplaceWith(new SKPath(src));
            return false;
        }

        using (result)
        {
            dst.FillType = result.FillType;
            dst.AddPath(result);
        }

        return true;
    }

    public bool GetFillPath(SKPath src, SKPathBuilder dst, SKRect cullRect) =>
        GetFillPath(src, dst, 1f);

    public bool GetFillPath(SKPath src, SKPathBuilder dst, SKRect cullRect, float resScale) =>
        GetFillPath(src, dst, resScale);

    public bool GetFillPath(SKPath src, SKPathBuilder dst, SKMatrix matrix) =>
        GetFillPath(src, dst, GetResolutionScale(matrix));

    public bool GetFillPath(SKPath src, SKPathBuilder dst, SKRect cullRect, SKMatrix matrix) =>
        GetFillPath(src, dst, GetResolutionScale(matrix));

    private bool TryCreateFillPath(SKPath source, float resScale, out SKPath destination)
    {
        destination = new SKPath();
        return TryPopulateFillPath(source, resScale, destination);
    }

    private bool TryPopulateFillPath(SKPath source, float resScale, SKPath destination)
    {
        if (PathEffect is { IsDash: false } materializedEffect)
        {
            var applied = materializedEffect.TryApply(
                source,
                resScale,
                out var effectedPath,
                out var paintAdjustment);
            if (applied)
            {
                using (effectedPath)
                using (var paint = Clone())
                {
                    paint.PathEffect = null;
                    paintAdjustment.Apply(paint);
                    if (!paint.TryCreateFillPath(effectedPath, resScale, out var result))
                    {
                        result.Dispose();
                        return false;
                    }

                    destination.ReplaceWithOwned(result);
                    result.Dispose();
                    return true;
                }
            }
            effectedPath.Dispose();
        }

        if (Style == SKPaintStyle.Fill)
        {
            destination.FillType = source.FillType;
            destination.AddPath(source);
            return true;
        }

        if (Style == SKPaintStyle.StrokeAndFill)
        {
            destination.AddPath(source);
        }

        if (StrokeWidth == 0f)
        {
            return Style == SKPaintStyle.StrokeAndFill;
        }

        if (!float.IsFinite(StrokeWidth))
        {
            return true;
        }

        var halfWidth = StrokeWidth / 2f;
        foreach (var figure in source.Geometry.Figures)
        {
            if (TryAddOvalStroke(destination, figure, halfWidth))
            {
                continue;
            }

            var points = FlattenFigure(figure, resScale);
            try
            {
                RemoveConsecutiveDuplicatePoints(points);
                if (figure.IsClosed &&
                    points.Count > 1 &&
                    Vector2.DistanceSquared(points[0], points[^1]) <= 0.0000001f)
                {
                    points.RemoveAt(points.Count - 1);
                }

                if (!figure.IsClosed && figure.Segments.Count > 0 && IsDegenerateFigure(points))
                {
                    if (StrokeCap == SKStrokeCap.Round)
                    {
                        destination.AddCircle(figure.StartPoint.X, figure.StartPoint.Y, halfWidth);
                    }
                    else if (StrokeCap == SKStrokeCap.Square)
                    {
                        destination.AddRect(new SKRect(
                            figure.StartPoint.X - halfWidth,
                            figure.StartPoint.Y - halfWidth,
                            figure.StartPoint.X + halfWidth,
                            figure.StartPoint.Y + halfWidth));
                    }

                    continue;
                }

                if (PathEffect is { IsDash: true, Intervals.Length: > 0 } dashEffect)
                {
                    AddDashedStrokeSegments(destination, points, figure.IsClosed, halfWidth, dashEffect);
                    continue;
                }

                for (var i = 1; i < points.Count; i++)
                {
                    AddStrokeSegment(destination, points[i - 1], points[i], halfWidth);
                }

                if (figure.IsClosed && points.Count > 1)
                {
                    AddStrokeSegment(destination, points[^1], points[0], halfWidth);
                }

                AddStrokeJoins(destination, points, figure.IsClosed, halfWidth * 2f);
                if (!figure.IsClosed)
                {
                    AddStrokeCaps(destination, points, halfWidth * 2f);
                }
            }
            finally
            {
                ReturnStrokePoints(points);
            }
        }

        return true;
    }

    private static float NormalizeResolutionScale(float resScale) =>
        float.IsFinite(resScale) && resScale > 1f ? resScale : 1f;

    private static float GetResolutionScale(SKMatrix matrix) =>
        NormalizeResolutionScale(TransformMetrics.GetStrokeScale(matrix.ToMatrix4x4()));

    internal static void NormalizeStrokeWinding(SKPath source, SKPath stroke)
    {
        var desiredWinding = GetDominantWinding(source.Geometry.Figures);
        var figures = stroke.Geometry.Figures;
        for (var index = 0; index < figures.Count; index++)
        {
            var points = FlattenFigure(figures[index]);
            try
            {
                var winding = GetSignedArea(points);
                if (MathF.Abs(winding) > 0.0001f && MathF.Sign(winding) != desiredWinding)
                {
                    figures[index] = ReverseFigure(figures[index]);
                }
            }
            finally
            {
                ReturnStrokePoints(points);
            }
        }
    }

    private static int GetDominantWinding(IReadOnlyList<PathFigure> figures)
    {
        var dominantArea = 0f;
        for (var index = 0; index < figures.Count; index++)
        {
            var points = FlattenFigure(figures[index]);
            try
            {
                var area = GetSignedArea(points);
                if (MathF.Abs(area) > MathF.Abs(dominantArea))
                {
                    dominantArea = area;
                }
            }
            finally
            {
                ReturnStrokePoints(points);
            }
        }

        return dominantArea < 0f ? -1 : 1;
    }

    private static float GetSignedArea(IReadOnlyList<Vector2> points)
    {
        if (points.Count < 3)
        {
            return 0f;
        }

        var twiceArea = 0f;
        var previous = points[^1];
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            twiceArea += previous.X * current.Y - current.X * previous.Y;
            previous = current;
        }

        return twiceArea * 0.5f;
    }

    private static PathFigure ReverseFigure(PathFigure source)
    {
        var segments = source.Segments;
        if (segments.Count == 0)
        {
            return source;
        }

        var segmentStarts = new Vector2[segments.Count];
        var current = source.StartPoint;
        for (var index = 0; index < segments.Count; index++)
        {
            segmentStarts[index] = current;
            current = GetSegmentEnd(segments[index]);
        }

        var reversed = new PathFigure(current, source.IsClosed)
        {
            IsFilled = source.IsFilled
        };
        for (var index = segments.Count - 1; index >= 0; index--)
        {
            var endpoint = segmentStarts[index];
            switch (segments[index])
            {
                case LineSegment line:
                    reversed.Segments.Add(new LineSegment(
                        endpoint,
                        line.IsSmoothJoin,
                        line.IsStroked));
                    break;
                case QuadraticBezierSegment quadratic:
                    reversed.Segments.Add(new QuadraticBezierSegment(
                        quadratic.ControlPoint,
                        endpoint,
                        quadratic.IsSmoothJoin,
                        quadratic.IsStroked));
                    break;
                case CubicBezierSegment cubic:
                    reversed.Segments.Add(new CubicBezierSegment(
                        cubic.ControlPoint2,
                        cubic.ControlPoint1,
                        endpoint,
                        cubic.IsSmoothJoin,
                        cubic.IsStroked));
                    break;
                case ArcSegment arc:
                    reversed.Segments.Add(new ArcSegment(
                        endpoint,
                        arc.Size,
                        arc.RotationAngle,
                        arc.IsLargeArc,
                        arc.SweepDirection == SweepDirection.Clockwise
                            ? SweepDirection.Counterclockwise
                            : SweepDirection.Clockwise,
                        arc.IsSmoothJoin,
                        arc.IsStroked));
                    break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported stroke path segment '{segments[index].GetType().FullName}'.");
            }
        }

        return reversed;
    }

    private static Vector2 GetSegmentEnd(PathSegment segment) => segment switch
    {
        LineSegment line => line.Point,
        QuadraticBezierSegment quadratic => quadratic.Point,
        CubicBezierSegment cubic => cubic.Point,
        ArcSegment arc => arc.Point,
        _ => throw new NotSupportedException(
            $"Unsupported stroke path segment '{segment.GetType().FullName}'.")
    };

    private static void RemoveConsecutiveDuplicatePoints(List<Vector2> points)
    {
        var writeIndex = 1;
        for (var readIndex = 1; readIndex < points.Count; readIndex++)
        {
            if (Vector2.DistanceSquared(points[writeIndex - 1], points[readIndex]) > 0.0000001f)
            {
                points[writeIndex++] = points[readIndex];
            }
        }

        if (writeIndex < points.Count)
        {
            points.RemoveRange(writeIndex, points.Count - writeIndex);
        }
    }

    private void AddStrokeJoins(
        SKPath destination,
        List<Vector2> points,
        bool isClosed,
        float strokeWidth)
    {
        var pointCount = points.Count;
        if (pointCount < 3)
        {
            return;
        }

        var first = isClosed ? 0 : 1;
        var end = isClosed ? pointCount : pointCount - 1;
        var lineJoin = MapStrokeJoin(StrokeJoin);
        Span<StrokeJoinTriangle> triangles = stackalloc StrokeJoinTriangle[StrokeJoinGeometry.MaxTrianglesPerJoin];
        for (var index = first; index < end; index++)
        {
            var previous = points[index == 0 ? pointCount - 1 : index - 1];
            var current = points[index];
            var next = points[index + 1 == pointCount ? 0 : index + 1];
            var triangleCount = StrokeJoinGeometry.WriteLineJoin(
                triangles,
                lineJoin,
                strokeWidth,
                StrokeMiter,
                previous,
                current,
                next);
            AddStrokeTriangles(destination, triangles[..triangleCount]);
        }
    }

    private void AddStrokeCaps(
        SKPath destination,
        List<Vector2> points,
        float strokeWidth)
    {
        if (points.Count < 2 || StrokeCap == SKStrokeCap.Butt)
        {
            return;
        }

        var lineCap = MapStrokeCap(StrokeCap);
        Span<StrokeJoinTriangle> triangles = stackalloc StrokeJoinTriangle[StrokeCapGeometry.MaxTrianglesPerCap];
        if (TryFindDistinctPoint(points, 0, 1, out var firstNeighbor))
        {
            var triangleCount = StrokeCapGeometry.WriteLineCap(
                triangles,
                lineCap,
                strokeWidth,
                points[0],
                firstNeighbor,
                isStart: true);
            AddStrokeTriangles(destination, triangles[..triangleCount]);
        }

        if (TryFindDistinctPoint(points, points.Count - 1, -1, out var lastNeighbor))
        {
            var triangleCount = StrokeCapGeometry.WriteLineCap(
                triangles,
                lineCap,
                strokeWidth,
                lastNeighbor,
                points[^1],
                isStart: false);
            AddStrokeTriangles(destination, triangles[..triangleCount]);
        }
    }

    private static bool TryFindDistinctPoint(
        IReadOnlyList<Vector2> points,
        int originIndex,
        int step,
        out Vector2 point)
    {
        var origin = points[originIndex];
        for (var index = originIndex + step;
             index >= 0 && index < points.Count;
             index += step)
        {
            point = points[index];
            if (Vector2.DistanceSquared(origin, point) > 0.0000001f)
            {
                return true;
            }
        }

        point = default;
        return false;
    }

    private static void AddStrokeTriangles(
        SKPath destination,
        ReadOnlySpan<StrokeJoinTriangle> triangles)
    {
        destination.AddTriangles(triangles);
    }

    private static bool IsDegenerateFigure(IReadOnlyList<Vector2> points)
    {
        var start = points[0];
        for (var i = 1; i < points.Count; i++)
        {
            if (Vector2.DistanceSquared(start, points[i]) > 0.0000001f)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryAddOvalStroke(SKPath destination, PathFigure figure, float halfWidth)
    {
        if (!figure.IsClosed || figure.Segments.Count != 2
            || figure.Segments[0] is not ArcSegment first
            || figure.Segments[1] is not ArcSegment second
            || !first.IsLargeArc
            || !second.IsLargeArc
            || first.SweepDirection != second.SweepDirection
            || MathF.Abs(first.RotationAngle) > 0.0001f
            || MathF.Abs(second.RotationAngle) > 0.0001f
            || Vector2.DistanceSquared(first.Size, second.Size) > 0.0001f
            || Vector2.DistanceSquared(second.Point, figure.StartPoint) > 0.0001f
            || MathF.Abs(MathF.Abs(first.Point.X - figure.StartPoint.X) - 2f * first.Size.X) > 0.0001f
            || MathF.Abs(first.Point.Y - figure.StartPoint.Y) > 0.0001f)
        {
            return false;
        }

        var center = (figure.StartPoint + first.Point) / 2f;
        var radiusX = MathF.Abs(first.Size.X);
        var radiusY = MathF.Abs(first.Size.Y);
        var direction = first.SweepDirection == SweepDirection.Clockwise
            ? SKPathDirection.Clockwise
            : SKPathDirection.CounterClockwise;
        destination.AddOval(
            new SKRect(
                center.X - radiusX - halfWidth,
                center.Y - radiusY - halfWidth,
                center.X + radiusX + halfWidth,
                center.Y + radiusY + halfWidth),
            direction);

        var innerRadiusX = radiusX - halfWidth;
        var innerRadiusY = radiusY - halfWidth;
        if (innerRadiusX > 0f && innerRadiusY > 0f)
        {
            destination.AddOval(
                new SKRect(
                    center.X - innerRadiusX,
                    center.Y - innerRadiusY,
                    center.X + innerRadiusX,
                    center.Y + innerRadiusY),
                direction == SKPathDirection.Clockwise
                    ? SKPathDirection.CounterClockwise
                    : SKPathDirection.Clockwise);
        }

        return true;
    }

    private void AddDashedStrokeSegments(
        SKPath destination,
        List<Vector2> points,
        bool isClosed,
        float halfWidth,
        SKPathEffect pathEffect)
    {
        if (points.Count < 2)
        {
            return;
        }

        var intervals = pathEffect.Intervals;
        var patternLength = 0f;
        for (var i = 0; i < intervals.Length; i++)
        {
            if (float.IsFinite(intervals[i]) && intervals[i] > 0f)
            {
                patternLength += intervals[i];
            }
        }

        if (patternLength <= 0f)
        {
            return;
        }

        var phase = pathEffect.Phase % patternLength;
        if (phase < 0f)
        {
            phase += patternLength;
        }

        var patternIndex = 0;
        while (phase >= intervals[patternIndex] && intervals[patternIndex] > 0f)
        {
            phase -= intervals[patternIndex];
            patternIndex = (patternIndex + 1) % intervals.Length;
        }

        var remainingInPattern = MathF.Max(0f, intervals[patternIndex] - phase);
        var segmentCount = isClosed ? points.Count : points.Count - 1;
        for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            var start = points[segmentIndex];
            var end = points[(segmentIndex + 1) % points.Count];
            var delta = end - start;
            var length = delta.Length();
            if (!float.IsFinite(length) || length <= 0.0001f)
            {
                continue;
            }

            var direction = delta / length;
            var distance = 0f;
            while (distance < length - 0.0001f)
            {
                if (remainingInPattern <= 0.0001f)
                {
                    AdvanceDashPattern(intervals, ref patternIndex, ref remainingInPattern);
                }

                var step = MathF.Min(remainingInPattern, length - distance);
                if ((patternIndex & 1) == 0 && step > 0.0001f)
                {
                    var dashStart = start + direction * distance;
                    var dashEnd = start + direction * (distance + step);
                    if (StrokeCap == SKStrokeCap.Square)
                    {
                        dashStart -= direction * halfWidth;
                        dashEnd += direction * halfWidth;
                    }

                    AddStrokeSegment(destination, dashStart, dashEnd, halfWidth);
                    if (StrokeCap == SKStrokeCap.Round)
                    {
                        destination.AddCircle(dashStart.X, dashStart.Y, halfWidth);
                        destination.AddCircle(dashEnd.X, dashEnd.Y, halfWidth);
                    }
                }

                distance += step;
                remainingInPattern -= step;
            }
        }
    }

    private static void AdvanceDashPattern(
        float[] intervals,
        ref int patternIndex,
        ref float remainingInPattern)
    {
        for (var i = 0; i < intervals.Length; i++)
        {
            patternIndex = (patternIndex + 1) % intervals.Length;
            remainingInPattern = intervals[patternIndex];
            if (remainingInPattern > 0.0001f)
            {
                return;
            }
        }
    }

    private static List<Vector2> FlattenFigure(PathFigure figure, float resScale = 1f)
    {
        // Subdivide only until the control polygon is within 1/4 logical pixel
        // of its chord. Higher device resolution tightens that tolerance while
        // the fixed depth keeps adversarial curves bounded to O(2^8) spans.
        var tolerance = 0.25f / NormalizeResolutionScale(resScale);
        var toleranceSquared = tolerance * tolerance;
        const int maximumDepth = 8;
        var result = s_strokePointCache ?? new List<Vector2>();
        s_strokePointCache = null;
        result.Clear();
        result.Add(figure.StartPoint);
        var current = figure.StartPoint;
        foreach (var segment in figure.Segments)
        {
            switch (segment)
            {
                case LineSegment line:
                    result.Add(line.Point);
                    current = line.Point;
                    break;
                case QuadraticBezierSegment quadratic:
                    FlattenQuadratic(
                        result,
                        current,
                        quadratic.ControlPoint,
                        quadratic.Point,
                        toleranceSquared,
                        maximumDepth);

                    current = quadratic.Point;
                    break;
                case CubicBezierSegment cubic:
                    FlattenCubic(
                        result,
                        current,
                        cubic.ControlPoint1,
                        cubic.ControlPoint2,
                        cubic.Point,
                        toleranceSquared,
                        maximumDepth);

                    current = cubic.Point;
                    break;
                case ArcSegment arc:
                    var arcPoints = ArcSegmentGeometry.FlattenArc(current, arc, MathF.PI / 24f);
                    for (var i = 1; i < arcPoints.Length; i++)
                    {
                        result.Add(arcPoints[i]);
                    }

                    current = arc.Point;
                    break;
            }
        }

        return result;
    }

    private static void FlattenQuadratic(
        List<Vector2> destination,
        Vector2 point0,
        Vector2 point1,
        Vector2 point2,
        float toleranceSquared,
        int depthRemaining)
    {
        if (depthRemaining == 0 ||
            IsQuadraticFlat(point0, point1, point2, toleranceSquared))
        {
            destination.Add(point2);
            return;
        }

        var point01 = (point0 + point1) * 0.5f;
        var point12 = (point1 + point2) * 0.5f;
        var midpoint = (point01 + point12) * 0.5f;
        FlattenQuadratic(
            destination,
            point0,
            point01,
            midpoint,
            toleranceSquared,
            depthRemaining - 1);
        FlattenQuadratic(
            destination,
            midpoint,
            point12,
            point2,
            toleranceSquared,
            depthRemaining - 1);
    }

    private static void FlattenCubic(
        List<Vector2> destination,
        Vector2 point0,
        Vector2 point1,
        Vector2 point2,
        Vector2 point3,
        float toleranceSquared,
        int depthRemaining)
    {
        if (depthRemaining == 0 ||
            IsCubicFlat(point0, point1, point2, point3, toleranceSquared))
        {
            destination.Add(point3);
            return;
        }

        var point01 = (point0 + point1) * 0.5f;
        var point12 = (point1 + point2) * 0.5f;
        var point23 = (point2 + point3) * 0.5f;
        var point012 = (point01 + point12) * 0.5f;
        var point123 = (point12 + point23) * 0.5f;
        var midpoint = (point012 + point123) * 0.5f;
        FlattenCubic(
            destination,
            point0,
            point01,
            point012,
            midpoint,
            toleranceSquared,
            depthRemaining - 1);
        FlattenCubic(
            destination,
            midpoint,
            point123,
            point23,
            point3,
            toleranceSquared,
            depthRemaining - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsQuadraticFlat(
        Vector2 point0,
        Vector2 point1,
        Vector2 point2,
        float toleranceSquared)
    {
        var chordX = point2.X - point0.X;
        var chordY = point2.Y - point0.Y;
        var chordLengthSquared = chordX * chordX + chordY * chordY;
        if (chordLengthSquared <= 0.0000001f)
        {
            return Vector2.DistanceSquared(point1, point0) <= toleranceSquared;
        }

        var controlX = point1.X - point0.X;
        var controlY = point1.Y - point0.Y;
        var projection = controlX * chordX + controlY * chordY;
        var cross = chordX * controlY - chordY * controlX;
        return projection >= 0f &&
               projection <= chordLengthSquared &&
               cross * cross <= toleranceSquared * chordLengthSquared;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCubicFlat(
        Vector2 point0,
        Vector2 point1,
        Vector2 point2,
        Vector2 point3,
        float toleranceSquared)
    {
        var chordX = point3.X - point0.X;
        var chordY = point3.Y - point0.Y;
        var chordLengthSquared = chordX * chordX + chordY * chordY;
        if (chordLengthSquared <= 0.0000001f)
        {
            return MathF.Max(
                Vector2.DistanceSquared(point1, point0),
                Vector2.DistanceSquared(point2, point0)) <= toleranceSquared;
        }

        var firstX = point1.X - point0.X;
        var firstY = point1.Y - point0.Y;
        var secondX = point2.X - point0.X;
        var secondY = point2.Y - point0.Y;
        var firstProjection = firstX * chordX + firstY * chordY;
        var secondProjection = secondX * chordX + secondY * chordY;
        var firstCross = chordX * firstY - chordY * firstX;
        var secondCross = chordX * secondY - chordY * secondX;
        var scaledTolerance = toleranceSquared * chordLengthSquared;
        return firstProjection >= 0f &&
               firstProjection <= secondProjection &&
               secondProjection <= chordLengthSquared &&
               firstCross * firstCross <= scaledTolerance &&
               secondCross * secondCross <= scaledTolerance;
    }

    private static void ReturnStrokePoints(List<Vector2> points)
    {
        points.Clear();
        if (s_strokePointCache is null && points.Capacity <= 4_096)
        {
            s_strokePointCache = points;
        }
    }

    private static void AddStrokeSegment(SKPath path, Vector2 start, Vector2 end, float halfWidth)
    {
        var direction = end - start;
        if (direction.LengthSquared() <= 0.0000001f)
        {
            return;
        }

        direction = Vector2.Normalize(direction);
        var normal = new Vector2(-direction.Y, direction.X) * halfWidth;
        path.MoveTo(start.X + normal.X, start.Y + normal.Y);
        path.LineTo(end.X + normal.X, end.Y + normal.Y);
        path.LineTo(end.X - normal.X, end.Y - normal.Y);
        path.LineTo(start.X - normal.X, start.Y - normal.Y);
        path.Close();
    }

    private static byte ToColorByte(float value)
    {
        return (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
    }

    private SKColor GetFilteredColor()
    {
        return ColorFilter?.Apply(Color) ?? Color;
    }

    private static Brush ApplyPaintAlphaToShaderBrush(Brush brush, SKColor paintColor)
    {
        brush.Opacity *= paintColor.A / 255.0f;
        return brush;
    }

    private static float ScaleStrokeWidth(float strokeWidth, float strokeScale)
    {
        if (strokeWidth == 0f)
        {
            return HairlineStrokeWidth;
        }

        if (!float.IsFinite(strokeScale) || strokeScale <= 0f)
        {
            return strokeWidth;
        }

        return strokeWidth * strokeScale;
    }

    private static PenLineCap MapStrokeCap(SKStrokeCap cap)
    {
        return cap switch
        {
            SKStrokeCap.Round => PenLineCap.Round,
            SKStrokeCap.Square => PenLineCap.Square,
            _ => PenLineCap.Flat
        };
    }

    private static PenLineJoin MapStrokeJoin(SKStrokeJoin join)
    {
        return join switch
        {
            SKStrokeJoin.Round => PenLineJoin.Round,
            SKStrokeJoin.Bevel => PenLineJoin.Bevel,
            _ => PenLineJoin.Miter
        };
    }

    private static (double[]? DashArray, double DashOffset) MapDashEffect(SKPathEffect? pathEffect, float strokeWidth)
    {
        if (pathEffect == null || !pathEffect.IsDash)
        {
            return (null, 0.0);
        }

        if (!float.IsFinite(strokeWidth) || strokeWidth <= 0f)
        {
            throw new NotSupportedException("Dash path effects require a positive finite stroke width.");
        }

        if (pathEffect.Intervals.Length == 0 || (pathEffect.Intervals.Length % 2) != 0)
        {
            throw new NotSupportedException("Dash path effects require an even number of intervals.");
        }

        var dashArray = new double[pathEffect.Intervals.Length];
        for (var i = 0; i < pathEffect.Intervals.Length; i++)
        {
            var interval = pathEffect.Intervals[i];
            if (!float.IsFinite(interval) || interval < 0f)
            {
                throw new NotSupportedException("Dash path effect intervals must be finite and non-negative.");
            }

            dashArray[i] = interval / strokeWidth;
        }

        if (!float.IsFinite(pathEffect.Phase))
        {
            throw new NotSupportedException("Dash path effect phase must be finite.");
        }

        return (dashArray, pathEffect.Phase / strokeWidth);
    }
}

public partial class SKShader : SKObject
{
    private enum ShaderDataKind : byte
    {
        None,
        Brush,
        Gradient,
        Picture,
        Image,
        Composed,
        PerlinNoise,
        LocalMatrix,
        ColorFilter,
        RuntimeEffect,
    }

    [ThreadStatic]
    private static SKColor[]? s_lastGradientColors;
    [ThreadStatic]
    private static float[]? s_lastGradientPositions;
    [ThreadStatic]
    private static GradientStopStorage? s_lastColorGradientStops;
    [ThreadStatic]
    private static SKColorF[]? s_lastGradientColorsF;
    [ThreadStatic]
    private static float[]? s_lastGradientPositionsF;
    [ThreadStatic]
    private static GradientStopStorage? s_lastColorFGradientStops;
    // UI themes commonly reuse a small transform palette across multiple gradient
    // kinds. Share immutable inverse storage so each shader retains one reference
    // instead of nine floats; the per-thread cache is strictly bounded.
    private const int GradientTransformCacheCapacity = 8;
    private static readonly GradientTransformStorage s_identityGradientTransform =
        new(SKMatrix.Identity, SKMatrix.Identity);
    [ThreadStatic]
    private static GradientTransformStorage?[]? s_gradientTransformCache;
    [ThreadStatic]
    private static int s_gradientTransformCacheCursor;
    private readonly object? _data;
    private readonly ShaderDataKind _dataKind;
    private int _referenceCount = 1;

    private SKShader(Func<Brush> brushCreator)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _data = brushCreator;
        _dataKind = ShaderDataKind.Brush;
    }

    private SKShader()
        : base(SKObjectHandle.Create(), owns: true)
    {
        _dataKind = ShaderDataKind.Gradient;
    }

    private SKShader(PictureShaderData picture)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _data = picture;
        _dataKind = ShaderDataKind.Picture;
    }

    private SKShader(ImageShaderData image)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _data = image;
        _dataKind = ShaderDataKind.Image;
    }

    private SKShader(ComposedShaderData composed)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _data = composed;
        _dataKind = ShaderDataKind.Composed;
    }

    private SKShader(PerlinNoiseShaderData perlinNoise)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _data = perlinNoise;
        _dataKind = ShaderDataKind.PerlinNoise;
    }

    private SKShader(LocalMatrixShaderData localMatrix)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _data = localMatrix;
        _dataKind = ShaderDataKind.LocalMatrix;
    }

    private SKShader(ColorFilterShaderData colorFilter)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _data = colorFilter;
        _dataKind = ShaderDataKind.ColorFilter;
    }

    private SKShader(SKRuntimeEffectInstance runtimeEffect)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _data = runtimeEffect;
        _dataKind = ShaderDataKind.RuntimeEffect;
    }

    public Brush ToBrush()
    {
        if (_dataKind == ShaderDataKind.LocalMatrix)
        {
            var localMatrix = (LocalMatrixShaderData)_data!;
            var brush = localMatrix.Shader.ToBrush();
            return ApplyCoordinateTransform(brush, localMatrix.Inverse, localMatrix.IsInvertible)
                ? brush
                : new SolidColorBrush(Vector4.Zero);
        }

        if (_dataKind == ShaderDataKind.ColorFilter)
        {
            var colorFilter = (ColorFilterShaderData)_data!;
            return ApplyColorFilter(colorFilter.Shader.ToBrush(), colorFilter.Filter);
        }

        if (_dataKind == ShaderDataKind.Brush)
        {
            return ((Func<Brush>)_data!)();
        }

        if (_dataKind == ShaderDataKind.Gradient)
        {
            return ((GradientShaderData)this).ToBrushCore();
        }

        if (_dataKind == ShaderDataKind.PerlinNoise)
        {
            var perlinNoise = (PerlinNoiseShaderData)_data!;
            return new PerlinNoiseBrush(
                perlinNoise.IsTurbulence,
                new Vector2(perlinNoise.BaseFrequencyX, perlinNoise.BaseFrequencyY),
                perlinNoise.NumOctaves,
                perlinNoise.Seed,
                new Vector2(perlinNoise.TileSize.X, perlinNoise.TileSize.Y));
        }

        if (_dataKind is ShaderDataKind.Picture or ShaderDataKind.Image or ShaderDataKind.Composed)
        {
            throw new NotSupportedException("Picture shaders are rendered by SKCanvas and cannot be converted to a vector brush.");
        }

        throw new NotSupportedException("The shader cannot be converted to a vector brush.");
    }

    internal PictureShaderData? Picture =>
        _dataKind == ShaderDataKind.Picture ? (PictureShaderData)_data! : null;
    internal ImageShaderData? Image =>
        _dataKind == ShaderDataKind.Image ? (ImageShaderData)_data! : null;
    internal ComposedShaderData? Composed =>
        _dataKind == ShaderDataKind.Composed ? (ComposedShaderData)_data! : null;
    internal PerlinNoiseShaderData? PerlinNoise =>
        _dataKind == ShaderDataKind.PerlinNoise ? (PerlinNoiseShaderData)_data! : null;
    internal LocalMatrixShaderData? LocalMatrix =>
        _dataKind == ShaderDataKind.LocalMatrix ? (LocalMatrixShaderData)_data! : null;
    internal ColorFilterShaderData? ColorFilter =>
        _dataKind == ShaderDataKind.ColorFilter ? (ColorFilterShaderData)_data! : null;
    internal SKRuntimeEffectInstance? RuntimeEffect =>
        _dataKind == ShaderDataKind.RuntimeEffect ? (SKRuntimeEffectInstance)_data! : null;

    internal static SKShader CreateRuntime(SKRuntimeEffectInstance runtimeEffect) => new(runtimeEffect);

    internal static SKShader CreatePicture(
        GpuPicture picture,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKFilterMode filterMode,
        SKMatrix localMatrix,
        SKRect tileRect)
    {
        return new SKShader(new PictureShaderData(
            picture,
            tileModeX,
            tileModeY,
            filterMode,
            localMatrix,
            tileRect));
    }

    internal static SKShader CreateRetainedImage(
        SKImage image,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKMatrix localMatrix)
    {
        return CreateRetainedImage(image, tileModeX, tileModeY, localMatrix, SKSamplingOptions.Default);
    }

    internal static SKShader CreateRetainedImage(
        SKImage image,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKMatrix localMatrix,
        SKSamplingOptions sampling,
        bool isRaw = false)
    {
        return new SKShader(new ImageShaderData(image, tileModeX, tileModeY, localMatrix, sampling, isRaw));
    }

    internal void AddReference()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(SKShader));
        }

        checked
        {
            _referenceCount++;
        }
    }

    internal void ReleaseReference()
    {
        if (_referenceCount <= 0)
        {
            return;
        }

        _referenceCount--;
        if (_referenceCount == 0)
        {
            switch (_dataKind)
            {
                case ShaderDataKind.Picture:
                    ((PictureShaderData)_data!).Dispose();
                    break;
                case ShaderDataKind.Image:
                    ((ImageShaderData)_data!).Dispose();
                    break;
                case ShaderDataKind.Composed:
                    ((ComposedShaderData)_data!).Dispose();
                    break;
                case ShaderDataKind.LocalMatrix:
                    ((LocalMatrixShaderData)_data!).Dispose();
                    break;
                case ShaderDataKind.ColorFilter:
                    ((ColorFilterShaderData)_data!).Dispose();
                    break;
            }
        }
    }

    public static SKShader CreateColor(SKColor color)
    {
        var value = new Vector4(
            color.R / 255.0f,
            color.G / 255.0f,
            color.B / 255.0f,
            color.A / 255.0f);
        return new SKShader(() => new SolidColorBrush(value));
    }

    public static SKShader CreateColor(SKColorF color, SKColorSpace colorspace)
    {
        ArgumentNullException.ThrowIfNull(colorspace);
        var value = new Vector4(
            Math.Clamp(color.R, 0f, 1f),
            Math.Clamp(color.G, 0f, 1f),
            Math.Clamp(color.B, 0f, 1f),
            Math.Clamp(color.A, 0f, 1f));
        return new SKShader(() => new SolidColorBrush(value));
    }

    public static SKShader CreateColor(SKColor color, SKColorSpace colorSpace)
    {
        ArgumentNullException.ThrowIfNull(colorSpace);
        return CreateColor(color);
    }

    public static SKShader CreatePicture(
        SKPicture src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKMatrix localMatrix,
        SKRect tile)
    {
        ArgumentNullException.ThrowIfNull(src);
        return src.ToShader(tmx, tmy, localMatrix, tile);
    }

    public static SKShader CreateLinearGradient(
        SKPoint start,
        SKPoint end,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode)
        => new LinearGradientShaderData(
            start,
            end,
            GetGradientStops(colors, colorPos),
            MapTileMode(mode),
            GradientColorInterpolationMode.SRgbLinearInterpolation,
            s_identityGradientTransform);

    public static SKShader CreateLinearGradient(
        SKPoint start,
        SKPoint end,
        SKColorF[] colors,
        SKColorSpace colorspace,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        return CreateLinearGradient(start, end, colors, colorspace, colorPos, mode, SKMatrix.Identity);
    }

    public static SKShader CreateLinearGradient(
        SKPoint start,
        SKPoint end,
        SKColorF[] colors,
        SKColorSpace colorspace,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        var stops = GetGradientStops(colors, colorPos);
        var interpolationMode = colorspace?.IsLinear == true
            ? GradientColorInterpolationMode.ScRgbLinearInterpolation
            : GradientColorInterpolationMode.SRgbLinearInterpolation;
        if (!TryGetShaderCoordinateTransform(localMatrix, out var coordinateTransform))
        {
            return CreateEmpty();
        }
        return new LinearGradientShaderData(
            start,
            end,
            stops,
            MapTileMode(mode),
            interpolationMode,
            coordinateTransform);
    }

    public static SKShader CreateLinearGradient(
        SKPoint start,
        SKPoint end,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        var stops = GetGradientStops(colors, colorPos);
        if (!TryGetShaderCoordinateTransform(localMatrix, out var coordinateTransform))
        {
            return CreateEmpty();
        }
        return new LinearGradientShaderData(
            start,
            end,
            stops,
            MapTileMode(mode),
            GradientColorInterpolationMode.SRgbLinearInterpolation,
            coordinateTransform);
    }

    public static SKShader CreateRadialGradient(
        SKPoint center,
        float radius,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode)
        => new RadialGradientShaderData(
            center,
            radius,
            GetGradientStops(colors, colorPos),
            MapTileMode(mode),
            GradientColorInterpolationMode.SRgbLinearInterpolation,
            s_identityGradientTransform);

    public static SKShader CreateRadialGradient(
        SKPoint center,
        float radius,
        SKColorF[] colors,
        SKColorSpace colorspace,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        return CreateRadialGradient(center, radius, colors, colorspace, colorPos, mode, SKMatrix.Identity);
    }

    public static SKShader CreateRadialGradient(
        SKPoint center,
        float radius,
        SKColorF[] colors,
        SKColorSpace colorspace,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        var stops = GetGradientStops(colors, colorPos);
        var interpolationMode = colorspace?.IsLinear == true
            ? GradientColorInterpolationMode.ScRgbLinearInterpolation
            : GradientColorInterpolationMode.SRgbLinearInterpolation;
        if (!TryGetShaderCoordinateTransform(localMatrix, out var coordinateTransform))
        {
            return CreateEmpty();
        }
        return new RadialGradientShaderData(
            center,
            radius,
            stops,
            MapTileMode(mode),
            interpolationMode,
            coordinateTransform);
    }

    public static SKShader CreateRadialGradient(
        SKPoint center,
        float radius,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        var stops = GetGradientStops(colors, colorPos);
        if (!TryGetShaderCoordinateTransform(localMatrix, out var coordinateTransform))
        {
            return CreateEmpty();
        }
        return new RadialGradientShaderData(
            center,
            radius,
            stops,
            MapTileMode(mode),
            GradientColorInterpolationMode.SRgbLinearInterpolation,
            coordinateTransform);
    }

    public static SKShader CreateTwoPointConicalGradient(
        SKPoint start,
        float startRadius,
        SKPoint end,
        float endRadius,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode)
        => new TwoPointConicalGradientShaderData(
            start,
            startRadius,
            end,
            endRadius,
            GetGradientStops(colors, colorPos),
            MapTileMode(mode),
            GradientColorInterpolationMode.SRgbLinearInterpolation,
            s_identityGradientTransform);

    public static SKShader CreateTwoPointConicalGradient(
        SKPoint start,
        float startRadius,
        SKPoint end,
        float endRadius,
        SKColorF[] colors,
        SKColorSpace colorspace,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        return CreateTwoPointConicalGradient(
            start,
            startRadius,
            end,
            endRadius,
            colors,
            colorspace,
            colorPos,
            mode,
            SKMatrix.Identity);
    }

    public static SKShader CreateTwoPointConicalGradient(
        SKPoint start,
        float startRadius,
        SKPoint end,
        float endRadius,
        SKColorF[] colors,
        SKColorSpace colorspace,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        var stops = GetGradientStops(colors, colorPos);
        var interpolationMode = colorspace?.IsLinear == true
            ? GradientColorInterpolationMode.ScRgbLinearInterpolation
            : GradientColorInterpolationMode.SRgbLinearInterpolation;
        if (!TryGetShaderCoordinateTransform(localMatrix, out var coordinateTransform))
        {
            return CreateEmpty();
        }
        return new TwoPointConicalGradientShaderData(
            start,
            startRadius,
            end,
            endRadius,
            stops,
            MapTileMode(mode),
            interpolationMode,
            coordinateTransform);
    }

    public static SKShader CreatePerlinNoiseFractalNoise(
        float baseFrequencyX,
        float baseFrequencyY,
        int numOctaves,
        float seed,
        SKPointI tileSize) =>
        new(new PerlinNoiseShaderData(
            false,
            baseFrequencyX,
            baseFrequencyY,
            numOctaves,
            seed,
            tileSize));

    public static SKShader CreatePerlinNoiseTurbulence(
        float baseFrequencyX,
        float baseFrequencyY,
        int numOctaves,
        float seed,
        SKPointI tileSize) =>
        new(new PerlinNoiseShaderData(
            true,
            baseFrequencyX,
            baseFrequencyY,
            numOctaves,
            seed,
            tileSize));

    public static SKShader CreateTwoPointConicalGradient(
        SKPoint start,
        float startRadius,
        SKPoint end,
        float endRadius,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        var stops = GetGradientStops(colors, colorPos);
        if (!TryGetShaderCoordinateTransform(localMatrix, out var coordinateTransform))
        {
            return CreateEmpty();
        }
        return new TwoPointConicalGradientShaderData(
            start,
            startRadius,
            end,
            endRadius,
            stops,
            MapTileMode(mode),
            GradientColorInterpolationMode.SRgbLinearInterpolation,
            coordinateTransform);
    }

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColor[] colors,
        float[]? colorPos,
        SKMatrix localMatrix)
    {
        return CreateSweepGradient(
            center,
            colors,
            colorPos,
            SKShaderTileMode.Clamp,
            0f,
            360f,
            localMatrix);
    }

    public static SKShader CreateBitmap(
        SKBitmap src,
        SKShaderTileMode tmx,
        SKShaderTileMode tmy)
    {
        ArgumentNullException.ThrowIfNull(src);
        return CreateRetainedImage(SKImage.FromBitmap(src), tmx, tmy, SKMatrix.Identity);
    }

    public static SKShader CreateCompose(SKShader shaderA, SKShader shaderB)
    {
        return CreateCompose(shaderA, shaderB, SKBlendMode.SrcOver);
    }

    public SKShader WithColorFilter(SKColorFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return new SKShader(new ColorFilterShaderData(this, filter));
    }

    public SKShader WithLocalMatrix(SKMatrix localMatrix)
    {
        return new SKShader(new LocalMatrixShaderData(this, localMatrix));
    }

    protected override void Dispose(bool disposing)
    {
        if (this is not GradientShaderData)
        {
            ReleaseReference();
        }
        base.Dispose(disposing);
    }

    internal sealed class PictureShaderData : IDisposable
    {
        public PictureShaderData(
            GpuPicture picture,
            SKShaderTileMode tileModeX,
            SKShaderTileMode tileModeY,
            SKFilterMode filterMode,
            SKMatrix localMatrix,
            SKRect tileRect)
        {
            Picture = picture;
            TileModeX = tileModeX;
            TileModeY = tileModeY;
            FilterMode = filterMode;
            LocalMatrix = localMatrix;
            TileRect = tileRect;
        }

        public GpuPicture Picture { get; }
        public SKShaderTileMode TileModeX { get; }
        public SKShaderTileMode TileModeY { get; }
        public SKFilterMode FilterMode { get; }
        public SKMatrix LocalMatrix { get; }
        public SKRect TileRect { get; }

        public void Dispose()
        {
            Picture.Dispose();
        }
    }

    internal sealed class ImageShaderData : IDisposable
    {
        public ImageShaderData(
            SKImage image,
            SKShaderTileMode tileModeX,
            SKShaderTileMode tileModeY,
            SKMatrix localMatrix)
            : this(image, tileModeX, tileModeY, localMatrix, SKSamplingOptions.Default)
        {
        }

        public ImageShaderData(
            SKImage image,
            SKShaderTileMode tileModeX,
            SKShaderTileMode tileModeY,
            SKMatrix localMatrix,
            SKSamplingOptions sampling,
            bool isRaw = false)
        {
            Image = image;
            TileModeX = tileModeX;
            TileModeY = tileModeY;
            LocalMatrix = localMatrix;
            Sampling = sampling;
            IsRaw = isRaw;
        }

        public SKImage Image { get; }
        public SKShaderTileMode TileModeX { get; }
        public SKShaderTileMode TileModeY { get; }
        public SKMatrix LocalMatrix { get; }
        public SKSamplingOptions Sampling { get; }
        public bool IsRaw { get; }
        public SKRect TileRect => new(0f, 0f, Image.Width, Image.Height);

        public void Dispose()
        {
            Image.Dispose();
        }
    }

    internal sealed class ComposedShaderData : IDisposable
    {
        public ComposedShaderData(
            SKShader destination,
            SKShader source,
            SKBlendMode? blendMode,
            SKBlender.ArithmeticBlend? arithmetic)
        {
            Destination = destination;
            Source = source;
            BlendMode = blendMode;
            Arithmetic = arithmetic;
            Destination.AddReference();
            Source.AddReference();
        }

        public SKShader Destination { get; }
        public SKShader Source { get; }
        public SKBlendMode? BlendMode { get; }
        public SKBlender.ArithmeticBlend? Arithmetic { get; }

        public void Dispose()
        {
            Destination.ReleaseReference();
            Source.ReleaseReference();
        }
    }

    internal sealed class LocalMatrixShaderData : IDisposable
    {
        public LocalMatrixShaderData(SKShader shader, SKMatrix matrix)
        {
            Shader = shader;
            Matrix = matrix;
            var matrix4x4 = matrix.ToMatrix4x4();
            IsInvertible = Matrix4x4.Invert(matrix4x4, out var inverse) && IsFinite(inverse);
            Inverse = IsInvertible ? inverse : Matrix4x4.Identity;
            Shader.AddReference();
        }

        public SKShader Shader { get; }
        public SKMatrix Matrix { get; }
        public Matrix4x4 Inverse { get; }
        public bool IsInvertible { get; }

        public void Dispose() => Shader.ReleaseReference();
    }

    internal sealed class ColorFilterShaderData : IDisposable
    {
        public ColorFilterShaderData(SKShader shader, SKColorFilter filter)
        {
            Shader = shader;
            Filter = filter;
            Shader.AddReference();
        }

        public SKShader Shader { get; }
        public SKColorFilter Filter { get; }

        public void Dispose() => Shader.ReleaseReference();
    }

    private static GradientStopStorage GetGradientStops(SKColor[] colors, float[]? positions)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ValidateGradientPositions(colors.Length, positions);
        if (colors.Length <= 3 &&
            ReferenceEquals(colors, s_lastGradientColors) &&
            ReferenceEquals(positions, s_lastGradientPositions) &&
            s_lastColorGradientStops is { } cached &&
            cached.MatchesRaw(colors, positions))
        {
            return cached;
        }

        var replacement = new GradientStopStorage(colors, positions);
        if (colors.Length <= 3)
        {
            s_lastGradientColors = colors;
            s_lastGradientPositions = positions;
            s_lastColorGradientStops = replacement;
        }
        return replacement;
    }

    private static GradientStopStorage GetGradientStops(SKColorF[] colors, float[]? positions)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ValidateGradientPositions(colors.Length, positions);
        if (colors.Length <= 3 &&
            ReferenceEquals(colors, s_lastGradientColorsF) &&
            ReferenceEquals(positions, s_lastGradientPositionsF) &&
            s_lastColorFGradientStops is { } cached &&
            cached.MatchesRaw(colors, positions))
        {
            return cached;
        }

        var replacement = new GradientStopStorage(colors, positions);
        if (colors.Length <= 3)
        {
            s_lastGradientColorsF = colors;
            s_lastGradientPositionsF = positions;
            s_lastColorFGradientStops = replacement;
        }
        return replacement;
    }

    private static void ValidateGradientPositions(int colorCount, float[]? positions)
    {
        if (positions is not null && positions.Length != colorCount)
        {
            throw new ArgumentException(
                "The number of colors must match the number of color positions.",
                nameof(positions));
        }
    }

    private sealed class GradientStopStorage
    {
        private readonly GradientStop _stop0;
        private readonly GradientStop _stop1;
        private readonly GradientStop _stop2;
        private readonly GradientStop[]? _overflowStops;
        private readonly SKColor _rawColor0;
        private readonly SKColor _rawColor1;
        private readonly SKColor _rawColor2;
        private readonly SKColorF _rawColorF0;
        private readonly SKColorF _rawColorF1;
        private readonly SKColorF _rawColorF2;
        private readonly bool _usesFloatColors;
        private readonly Vector3 _rawPositions;
        private readonly bool _hasExplicitPositions;

        public GradientStopStorage(SKColor[] colors, float[]? positions)
        {
            Count = colors.Length;
            _hasExplicitPositions = positions is not null;
            _rawPositions = CapturePositions(positions);
            if (colors.Length > 3)
            {
                _overflowStops = new GradientStop[colors.Length];
                for (var index = 0; index < colors.Length; index++)
                    _overflowStops[index] = CreateStop(colors[index], GetOffset(index, colors.Length, positions));
                return;
            }

            if (colors.Length > 0)
            {
                _rawColor0 = colors[0];
                _stop0 = CreateStop(colors[0], GetOffset(0, colors.Length, positions));
            }
            if (colors.Length > 1)
            {
                _rawColor1 = colors[1];
                _stop1 = CreateStop(colors[1], GetOffset(1, colors.Length, positions));
            }
            if (colors.Length > 2)
            {
                _rawColor2 = colors[2];
                _stop2 = CreateStop(colors[2], GetOffset(2, colors.Length, positions));
            }
        }

        public GradientStopStorage(SKColorF[] colors, float[]? positions)
        {
            Count = colors.Length;
            _usesFloatColors = true;
            _hasExplicitPositions = positions is not null;
            _rawPositions = CapturePositions(positions);
            if (colors.Length > 3)
            {
                _overflowStops = new GradientStop[colors.Length];
                for (var index = 0; index < colors.Length; index++)
                    _overflowStops[index] = CreateStop(colors[index], GetOffset(index, colors.Length, positions));
                return;
            }

            if (colors.Length > 0)
            {
                _rawColorF0 = colors[0];
                _stop0 = CreateStop(colors[0], GetOffset(0, colors.Length, positions));
            }
            if (colors.Length > 1)
            {
                _rawColorF1 = colors[1];
                _stop1 = CreateStop(colors[1], GetOffset(1, colors.Length, positions));
            }
            if (colors.Length > 2)
            {
                _rawColorF2 = colors[2];
                _stop2 = CreateStop(colors[2], GetOffset(2, colors.Length, positions));
            }
        }

        public int Count { get; }

        public bool MatchesRaw(SKColor[] colors, float[]? positions)
        {
            if (_usesFloatColors || colors.Length != Count || positions is not null && positions.Length != Count)
                return false;
            return (Count < 1 || colors[0] == _rawColor0) &&
                   (Count < 2 || colors[1] == _rawColor1) &&
                   (Count < 3 || colors[2] == _rawColor2) &&
                   PositionsMatch(positions);
        }

        public bool MatchesRaw(SKColorF[] colors, float[]? positions)
        {
            if (!_usesFloatColors || colors.Length != Count || positions is not null && positions.Length != Count)
                return false;
            return (Count < 1 || RawColorEquals(in colors[0], in _rawColorF0)) &&
                   (Count < 2 || RawColorEquals(in colors[1], in _rawColorF1)) &&
                   (Count < 3 || RawColorEquals(in colors[2], in _rawColorF2)) &&
                   PositionsMatch(positions);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RawColorEquals(in SKColorF left, in SKColorF right) =>
            Unsafe.As<SKColorF, Vector4>(ref Unsafe.AsRef(in left)) ==
            Unsafe.As<SKColorF, Vector4>(ref Unsafe.AsRef(in right));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool PositionsMatch(float[]? positions)
        {
            if (!_hasExplicitPositions)
                return positions is null;
            if (positions is null)
                return false;
            return Count switch
            {
                0 => true,
                1 => positions[0] == _rawPositions.X,
                2 => new Vector2(positions[0], positions[1]) == new Vector2(_rawPositions.X, _rawPositions.Y),
                _ => new Vector3(positions[0], positions[1], positions[2]) == _rawPositions,
            };
        }

        private static Vector3 CapturePositions(float[]? positions) => positions?.Length switch
        {
            null or 0 => default,
            1 => new Vector3(positions[0], 0f, 0f),
            2 => new Vector3(positions[0], positions[1], 0f),
            _ => new Vector3(positions[0], positions[1], positions[2]),
        };

        public GradientStop[] Copy()
        {
            if (_overflowStops is { } overflowStops)
                return (GradientStop[])overflowStops.Clone();

            var result = new GradientStop[Count];
            if (Count > 0)
                result[0] = _stop0;
            if (Count > 1)
                result[1] = _stop1;
            if (Count > 2)
                result[2] = _stop2;
            return result;
        }

        public Vector4 AverageColor()
        {
            if (Count == 0)
                return Vector4.Zero;

            var first = Get(0);
            var previousOffset = Math.Clamp(first.Offset, 0f, 1f);
            var average = first.Color * previousOffset;
            for (var index = 1; index < Count; index++)
            {
                var previous = Get(index - 1);
                var current = Get(index);
                var offset = Math.Clamp(current.Offset, previousOffset, 1f);
                average += (previous.Color + current.Color) * (0.5f * (offset - previousOffset));
                previousOffset = offset;
            }

            average += Get(Count - 1).Color * (1f - previousOffset);
            return Vector4.Clamp(average, Vector4.Zero, Vector4.One);
        }

        private GradientStop Get(int index) => _overflowStops is { } overflowStops
            ? overflowStops[index]
            : index switch
            {
                0 => _stop0,
                1 => _stop1,
                2 => _stop2,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };

        private static float GetOffset(int index, int count, float[]? positions) =>
            positions is not null
                ? positions[index]
                : count <= 1
                    ? 0f
                    : (float)index / (count - 1);

        private static GradientStop CreateStop(SKColor color, float offset) => new(
            new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f),
            offset);

        private static GradientStop CreateStop(SKColorF color, float offset) => new(
            new Vector4(
                Math.Clamp(color.R, 0f, 1f),
                Math.Clamp(color.G, 0f, 1f),
                Math.Clamp(color.B, 0f, 1f),
                Math.Clamp(color.A, 0f, 1f)),
            offset);
    }

    private sealed class GradientTransformStorage
    {
        public GradientTransformStorage(SKMatrix localMatrix, SKMatrix coordinateTransform)
        {
            LocalMatrix = localMatrix;
            CoordinateTransform = coordinateTransform;
        }

        public SKMatrix LocalMatrix { get; }
        public SKMatrix CoordinateTransform { get; }
    }

    // Most UI gradients contain two or three stops. The bounded per-thread lookup
    // shares immutable compact stop storage only while the caller's inputs remain
    // unchanged; larger gradients keep an owned overflow array. Typed descriptors
    // avoid the closure and delegate previously retained by every shader. ToBrush
    // still returns a fresh array at the public mutation boundary.
    private abstract class GradientShaderData : SKShader
    {
        private readonly GradientStopStorage _stops;
        private readonly GradientTransformStorage _coordinateTransform;
        private readonly byte _options;

        protected GradientShaderData(
            GradientStopStorage stops,
            GradientSpreadMethod spreadMethod,
            GradientColorInterpolationMode interpolationMode,
            GradientTransformStorage coordinateTransform)
        {
            _options = PackOptions(spreadMethod, interpolationMode);
            _coordinateTransform = coordinateTransform;
            _stops = stops;
        }

        protected GradientSpreadMethod SpreadMethod => (GradientSpreadMethod)(_options & 0x3);
        protected GradientColorInterpolationMode InterpolationMode =>
            (GradientColorInterpolationMode)((_options >> 2) & 0x1);
        protected Matrix4x4 CoordinateTransform =>
            _coordinateTransform.CoordinateTransform.ToMatrix4x4();

        public abstract Brush ToBrushCore();

        protected GradientStop[] CopyStops() => _stops.Copy();

        public Vector4 AverageColor() => _stops.AverageColor();

        private static byte PackOptions(
            GradientSpreadMethod spreadMethod,
            GradientColorInterpolationMode interpolationMode) =>
            (byte)((byte)spreadMethod | ((byte)interpolationMode << 2));

    }

    private sealed class LinearGradientShaderData : GradientShaderData
    {
        private readonly Vector2 _start;
        private readonly Vector2 _end;

        public LinearGradientShaderData(
            SKPoint start,
            SKPoint end,
            GradientStopStorage stops,
            GradientSpreadMethod spreadMethod,
            GradientColorInterpolationMode interpolationMode,
            GradientTransformStorage coordinateTransform)
            : base(stops, spreadMethod, interpolationMode, coordinateTransform)
        {
            _start = new Vector2(start.X, start.Y);
            _end = new Vector2(end.X, end.Y);
        }

        public override Brush ToBrushCore() => new LinearGradientBrush(_start, _end, CopyStops())
        {
            SpreadMethod = SpreadMethod,
            ColorInterpolationMode = InterpolationMode,
            CoordinateTransform = CoordinateTransform,
        };
    }

    private sealed class RadialGradientShaderData : GradientShaderData
    {
        private readonly Vector2 _center;
        private readonly float _radius;

        public RadialGradientShaderData(
            SKPoint center,
            float radius,
            GradientStopStorage stops,
            GradientSpreadMethod spreadMethod,
            GradientColorInterpolationMode interpolationMode,
            GradientTransformStorage coordinateTransform)
            : base(stops, spreadMethod, interpolationMode, coordinateTransform)
        {
            _center = new Vector2(center.X, center.Y);
            _radius = radius;
        }

        public override Brush ToBrushCore() => new RadialGradientBrush(_center, _radius, CopyStops())
        {
            SpreadMethod = SpreadMethod,
            ColorInterpolationMode = InterpolationMode,
            CoordinateTransform = CoordinateTransform,
        };
    }

    private sealed class TwoPointConicalGradientShaderData : GradientShaderData
    {
        private readonly Vector2 _start;
        private readonly Vector2 _end;
        private readonly float _startRadius;
        private readonly float _endRadius;

        public TwoPointConicalGradientShaderData(
            SKPoint start,
            float startRadius,
            SKPoint end,
            float endRadius,
            GradientStopStorage stops,
            GradientSpreadMethod spreadMethod,
            GradientColorInterpolationMode interpolationMode,
            GradientTransformStorage coordinateTransform)
            : base(stops, spreadMethod, interpolationMode, coordinateTransform)
        {
            _start = new Vector2(start.X, start.Y);
            _startRadius = startRadius;
            _end = new Vector2(end.X, end.Y);
            _endRadius = endRadius;
        }

        public override Brush ToBrushCore() => new TwoPointConicalGradientBrush(
            _start,
            _startRadius,
            _end,
            _endRadius,
            CopyStops())
        {
            SpreadMethod = SpreadMethod,
            ColorInterpolationMode = InterpolationMode,
            CoordinateTransform = CoordinateTransform,
        };
    }

    private sealed class SweepGradientShaderData : GradientShaderData
    {
        private readonly Vector2 _center;
        private readonly float _startAngle;
        private readonly float _endAngle;

        public SweepGradientShaderData(
            SKPoint center,
            GradientStopStorage stops,
            GradientSpreadMethod spreadMethod,
            GradientColorInterpolationMode interpolationMode,
            GradientTransformStorage coordinateTransform,
            float startAngle,
            float endAngle)
            : base(stops, spreadMethod, interpolationMode, coordinateTransform)
        {
            _center = new Vector2(center.X, center.Y);
            _startAngle = startAngle;
            _endAngle = endAngle;
        }

        public override Brush ToBrushCore() => new SweepGradientBrush(_center, CopyStops())
        {
            StartAngle = _startAngle,
            EndAngle = _endAngle,
            SpreadMethod = SpreadMethod,
            ColorInterpolationMode = InterpolationMode,
            CoordinateTransform = CoordinateTransform,
        };
    }

    internal sealed record PerlinNoiseShaderData(
        bool IsTurbulence,
        float BaseFrequencyX,
        float BaseFrequencyY,
        int NumOctaves,
        float Seed,
        SKPointI TileSize);

    internal static Brush ApplyColorFilter(Brush brush, SKColorFilter? colorFilter)
    {
        if (colorFilter == null)
        {
            return brush;
        }

        switch (brush)
        {
            case SolidColorBrush solid:
                solid.Color = ApplyColorFilter(solid.Color, colorFilter);
                break;
            case LinearGradientBrush linear:
                ApplyColorFilter(linear.Stops, colorFilter);
                break;
            case RadialGradientBrush radial:
                ApplyColorFilter(radial.Stops, colorFilter);
                break;
            case TwoPointConicalGradientBrush conical:
                ApplyColorFilter(conical.Stops, colorFilter);
                break;
            case SweepGradientBrush sweep:
                ApplyColorFilter(sweep.Stops, colorFilter);
                break;
        }

        return brush;
    }

    internal static bool ApplyLocalMatrix(Brush brush, SKMatrix localMatrix)
    {
        if (brush is SolidColorBrush)
        {
            return true;
        }

        var matrix = localMatrix.ToMatrix4x4();
        if (!Matrix4x4.Invert(matrix, out var inverse) || !IsFinite(inverse))
        {
            return false;
        }

        return ApplyCoordinateTransform(brush, inverse, isInvertible: true);
    }

    private static bool ApplyCoordinateTransform(
        Brush brush,
        Matrix4x4 inverse,
        bool isInvertible)
    {
        if (brush is SolidColorBrush)
        {
            return true;
        }

        if (!isInvertible)
        {
            return false;
        }

        switch (brush)
        {
            case LinearGradientBrush linear:
                linear.CoordinateTransform = inverse * linear.CoordinateTransform;
                break;
            case RadialGradientBrush radial:
                radial.CoordinateTransform = inverse * radial.CoordinateTransform;
                break;
            case TwoPointConicalGradientBrush conical:
                conical.CoordinateTransform = inverse * conical.CoordinateTransform;
                break;
            case SweepGradientBrush sweep:
                sweep.CoordinateTransform = inverse * sweep.CoordinateTransform;
                break;
            case PerlinNoiseBrush perlin:
                perlin.CoordinateTransform = inverse * perlin.CoordinateTransform;
                break;
        }

        return true;
    }

    private static void ApplyColorFilter(GradientStop[] stops, SKColorFilter colorFilter)
    {
        for (var i = 0; i < stops.Length; i++)
        {
            var stop = stops[i];
            stop.Color = ApplyColorFilter(stop.Color, colorFilter);
            stops[i] = stop;
        }
    }

    private static Vector4 ApplyColorFilter(Vector4 color, SKColorFilter colorFilter)
    {
        var filtered = colorFilter.Apply(new SKColor(
            ToByte(color.X),
            ToByte(color.Y),
            ToByte(color.Z),
            ToByte(color.W)));
        return new Vector4(
            filtered.R / 255f,
            filtered.G / 255f,
            filtered.B / 255f,
            filtered.A / 255f);
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
    }

    private static GradientSpreadMethod MapTileMode(SKShaderTileMode mode)
    {
        return mode switch
        {
            SKShaderTileMode.Clamp => GradientSpreadMethod.Pad,
            SKShaderTileMode.Repeat => GradientSpreadMethod.Repeat,
            SKShaderTileMode.Mirror => GradientSpreadMethod.Reflect,
            SKShaderTileMode.Decal => GradientSpreadMethod.Decal,
            _ => GradientSpreadMethod.Pad
        };
    }

    private static bool TryGetShaderCoordinateTransform(
        SKMatrix localMatrix,
        out GradientTransformStorage coordinateTransform)
    {
        if (localMatrix.IsIdentity)
        {
            coordinateTransform = s_identityGradientTransform;
            return true;
        }

        var cache = s_gradientTransformCache;
        if (cache is not null)
        {
            for (var index = 0; index < cache.Length; index++)
            {
                if (cache[index] is { } cached && cached.LocalMatrix == localMatrix)
                {
                    coordinateTransform = cached;
                    return true;
                }
            }
        }

        if (!localMatrix.TryInvert(out var inverse))
        {
            coordinateTransform = s_identityGradientTransform;
            return false;
        }

        coordinateTransform = new GradientTransformStorage(localMatrix, inverse);
        cache ??= s_gradientTransformCache =
            new GradientTransformStorage?[GradientTransformCacheCapacity];
        cache[s_gradientTransformCacheCursor] = coordinateTransform;
        s_gradientTransformCacheCursor =
            (s_gradientTransformCacheCursor + 1) % GradientTransformCacheCapacity;
        return true;
    }

    private static bool IsFinite(Matrix4x4 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
        float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
        float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
        float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) &&
        float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
        float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) &&
        float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);
}

public partial class SKColorFilter : SKObject
{
    internal SKColor Color { get; }
    internal SKBlendMode Mode { get; }
    private readonly byte[]? _alphaTable;
    private readonly byte[]? _redTable;
    private readonly byte[]? _greenTable;
    private readonly byte[]? _blueTable;
    private readonly float[]? _colorMatrix;
    private readonly bool _lumaColor;
    private readonly bool _isBlendColor;
    private readonly SKRuntimeEffectInstance? _runtimeEffect;

    private SKColorFilter(SKColor color, SKBlendMode mode)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _kind = ColorFilterKind.Blend;
        Color = color;
        Mode = mode;
        _isBlendColor = true;
    }

    private SKColorFilter(byte[] alpha, byte[] red, byte[] green, byte[] blue)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _kind = ColorFilterKind.Table;
        _alphaTable = alpha;
        _redTable = red;
        _greenTable = green;
        _blueTable = blue;
    }

    private SKColorFilter(float[] colorMatrix)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _kind = ColorFilterKind.ColorMatrix;
        _colorMatrix = colorMatrix;
    }

    private SKColorFilter(bool lumaColor)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _kind = ColorFilterKind.Luma;
        _lumaColor = lumaColor;
    }

    private SKColorFilter(SKRuntimeEffectInstance runtimeEffect)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _kind = ColorFilterKind.RuntimeEffect;
        _runtimeEffect = runtimeEffect;
    }

    internal SKRuntimeEffectInstance? RuntimeEffect => _runtimeEffect;

    internal static SKColorFilter CreateRuntime(SKRuntimeEffectInstance runtimeEffect) => new(runtimeEffect);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    internal float[]? ColorMatrix => _kind == ColorFilterKind.ColorMatrix ? _colorMatrix : null;
    internal bool IsLumaColor => _kind == ColorFilterKind.Luma && _lumaColor;

    internal bool TryGetBlendColor(out SKColor color, out SKBlendMode mode)
    {
        color = Color;
        mode = Mode;
        return _kind == ColorFilterKind.Blend && _isBlendColor;
    }

    internal bool TryGetColorTables(
        out ReadOnlyMemory<byte> alpha,
        out ReadOnlyMemory<byte> red,
        out ReadOnlyMemory<byte> green,
        out ReadOnlyMemory<byte> blue)
    {
        if (_kind == ColorFilterKind.Table &&
            _alphaTable != null && _redTable != null && _greenTable != null && _blueTable != null)
        {
            alpha = _alphaTable;
            red = _redTable;
            green = _greenTable;
            blue = _blueTable;
            return true;
        }

        alpha = default;
        red = default;
        green = default;
        blue = default;
        return false;
    }

    internal bool TryGetImageEffectColorMatrix(out ImageEffectColorMatrix matrix)
    {
        if (_kind == ColorFilterKind.ColorMatrix && _colorMatrix != null)
        {
            matrix = new ImageEffectColorMatrix(
                new Vector4(_colorMatrix[0], _colorMatrix[1], _colorMatrix[2], _colorMatrix[3]),
                new Vector4(_colorMatrix[5], _colorMatrix[6], _colorMatrix[7], _colorMatrix[8]),
                new Vector4(_colorMatrix[10], _colorMatrix[11], _colorMatrix[12], _colorMatrix[13]),
                new Vector4(_colorMatrix[15], _colorMatrix[16], _colorMatrix[17], _colorMatrix[18]),
                new Vector4(_colorMatrix[4], _colorMatrix[9], _colorMatrix[14], _colorMatrix[19]));
            return true;
        }

        matrix = default;
        return false;
    }

    public static SKColorFilter CreateBlendMode(SKColor c, SKBlendMode mode)
    {
        if (mode < SKBlendMode.Clear || mode > SKBlendMode.Luminosity)
        {
            return null!;
        }

        if (mode == SKBlendMode.Clear)
        {
            c = SKColor.Empty;
            mode = SKBlendMode.Src;
        }
        else if (mode == SKBlendMode.SrcOver)
        {
            mode = c.A switch
            {
                0 => SKBlendMode.Dst,
                255 => SKBlendMode.Src,
                _ => mode,
            };
        }

        if (IsNoOpBlendColorFilter(c.A, mode))
        {
            return null!;
        }

        return new SKColorFilter(c, mode);
    }

    public static SKColorFilter CreateTable(byte[] tableA, byte[] tableR, byte[] tableG, byte[] tableB)
    {
        ArgumentNullException.ThrowIfNull(tableA);
        ArgumentNullException.ThrowIfNull(tableR);
        ArgumentNullException.ThrowIfNull(tableG);
        ArgumentNullException.ThrowIfNull(tableB);
        return CreateTable(tableA.AsSpan(), tableR.AsSpan(), tableG.AsSpan(), tableB.AsSpan());
    }

    public static SKColorFilter CreateColorMatrix(float[] matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        return CreateColorMatrix(matrix.AsSpan());
    }

    public static SKColorFilter CreateLumaColor() => new(lumaColor: true);

    internal SKColor Apply(SKColor destination)
    {
        if (_kind is ColorFilterKind.Compose or
            ColorFilterKind.Lerp or
            ColorFilterKind.HslaColorMatrix or
            ColorFilterKind.HighContrast or
            ColorFilterKind.Overdraw)
        {
            return ApplyRetainedFilter(destination);
        }

        if (_colorMatrix != null)
        {
            var red = destination.R / 255f;
            var green = destination.G / 255f;
            var blue = destination.B / 255f;
            var alpha = destination.A / 255f;
            return new SKColor(
                ToByte(red * _colorMatrix[0] + green * _colorMatrix[1] + blue * _colorMatrix[2] + alpha * _colorMatrix[3] + _colorMatrix[4]),
                ToByte(red * _colorMatrix[5] + green * _colorMatrix[6] + blue * _colorMatrix[7] + alpha * _colorMatrix[8] + _colorMatrix[9]),
                ToByte(red * _colorMatrix[10] + green * _colorMatrix[11] + blue * _colorMatrix[12] + alpha * _colorMatrix[13] + _colorMatrix[14]),
                ToByte(red * _colorMatrix[15] + green * _colorMatrix[16] + blue * _colorMatrix[17] + alpha * _colorMatrix[18] + _colorMatrix[19]));
        }

        if (_lumaColor)
        {
            var luma = ToByte(
                (destination.R / 255f * 0.2126f +
                 destination.G / 255f * 0.7152f +
                 destination.B / 255f * 0.0722f) *
                (destination.A / 255f));
            return new SKColor(0, 0, 0, luma);
        }

        if (_alphaTable != null && _redTable != null && _greenTable != null && _blueTable != null)
        {
            return new SKColor(
                _redTable[destination.R],
                _greenTable[destination.G],
                _blueTable[destination.B],
                _alphaTable[destination.A]);
        }

        var source = ToPremultiplied(Color);
        var dest = ToPremultiplied(destination);
        var result = Mode switch
        {
            SKBlendMode.Clear => Vector4.Zero,
            SKBlendMode.Src => source,
            SKBlendMode.Dst => dest,
            SKBlendMode.SrcOver => SourceOver(source, dest),
            SKBlendMode.DstOver => SourceOver(dest, source),
            SKBlendMode.SrcIn => source * dest.W,
            SKBlendMode.DstIn => dest * source.W,
            SKBlendMode.SrcOut => source * (1f - dest.W),
            SKBlendMode.DstOut => dest * (1f - source.W),
            SKBlendMode.SrcATop => (source * dest.W) + (dest * (1f - source.W)),
            SKBlendMode.DstATop => (dest * source.W) + (source * (1f - dest.W)),
            SKBlendMode.Xor => (source * (1f - dest.W)) + (dest * (1f - source.W)),
            SKBlendMode.Plus => Vector4.Min(source + dest, Vector4.One),
            SKBlendMode.Modulate => source * dest,
            SKBlendMode.Multiply => BlendSeparable(source, dest, static (s, d) => s * d),
            SKBlendMode.Screen => BlendSeparable(source, dest, static (s, d) => s + d - (s * d)),
            SKBlendMode.Overlay => BlendSeparable(source, dest, Overlay),
            SKBlendMode.Darken => BlendSeparable(source, dest, static (s, d) => MathF.Min(s, d)),
            SKBlendMode.Lighten => BlendSeparable(source, dest, static (s, d) => MathF.Max(s, d)),
            SKBlendMode.ColorDodge => BlendSeparable(source, dest, ColorDodge),
            SKBlendMode.ColorBurn => BlendSeparable(source, dest, ColorBurn),
            SKBlendMode.HardLight => BlendSeparable(source, dest, HardLight),
            SKBlendMode.SoftLight => BlendSeparable(source, dest, SoftLight),
            SKBlendMode.Difference => BlendSeparable(source, dest, static (s, d) => MathF.Abs(d - s)),
            SKBlendMode.Exclusion => BlendSeparable(source, dest, static (s, d) => s + d - (2f * s * d)),
            SKBlendMode.Hue => BlendNonSeparable(
                source,
                dest,
                static (s, d) => SetLuminosity(SetSaturation(s, Saturation(d)), Luminosity(d))),
            SKBlendMode.Saturation => BlendNonSeparable(
                source,
                dest,
                static (s, d) => SetLuminosity(SetSaturation(d, Saturation(s)), Luminosity(d))),
            SKBlendMode.Color => BlendNonSeparable(
                source,
                dest,
                static (s, d) => SetLuminosity(s, Luminosity(d))),
            SKBlendMode.Luminosity => BlendNonSeparable(
                source,
                dest,
                static (s, d) => SetLuminosity(d, Luminosity(s))),
            _ => SourceOver(source, dest)
        };

        return FromPremultiplied(result);
    }

    private static Vector4 ToPremultiplied(SKColor color)
    {
        var alpha = color.A / 255f;
        return new Vector4(
            color.R / 255f * alpha,
            color.G / 255f * alpha,
            color.B / 255f * alpha,
            alpha);
    }

    private static SKColor FromPremultiplied(Vector4 color)
    {
        var alpha = Clamp01(color.W);
        if (alpha <= 0f)
        {
            return SKColor.Empty;
        }

        return new SKColor(
            ToByte(color.X / alpha),
            ToByte(color.Y / alpha),
            ToByte(color.Z / alpha),
            ToByte(alpha));
    }

    private static Vector4 SourceOver(Vector4 source, Vector4 dest)
    {
        return source + (dest * (1f - source.W));
    }

    private static Vector4 BlendSeparable(Vector4 source, Vector4 dest, Func<float, float, float> blend)
    {
        var sourceAlpha = source.W;
        var destAlpha = dest.W;
        var alpha = sourceAlpha + destAlpha - (sourceAlpha * destAlpha);
        var rgb = new Vector3(
            BlendComponent(source.X, dest.X, sourceAlpha, destAlpha, blend),
            BlendComponent(source.Y, dest.Y, sourceAlpha, destAlpha, blend),
            BlendComponent(source.Z, dest.Z, sourceAlpha, destAlpha, blend));
        return new Vector4(rgb, alpha);
    }

    private static float BlendComponent(float source, float dest, float sourceAlpha, float destAlpha, Func<float, float, float> blend)
    {
        var straightSource = sourceAlpha > 0f ? source / sourceAlpha : 0f;
        var straightDest = destAlpha > 0f ? dest / destAlpha : 0f;
        return (source * (1f - destAlpha))
            + (dest * (1f - sourceAlpha))
            + (sourceAlpha * destAlpha * blend(straightSource, straightDest));
    }

    private static Vector4 BlendNonSeparable(
        Vector4 source,
        Vector4 dest,
        Func<Vector3, Vector3, Vector3> blend)
    {
        var sourceAlpha = source.W;
        var destAlpha = dest.W;
        var alpha = sourceAlpha + destAlpha - (sourceAlpha * destAlpha);
        var straightSource = sourceAlpha > 0f
            ? new Vector3(source.X, source.Y, source.Z) / sourceAlpha
            : Vector3.Zero;
        var straightDest = destAlpha > 0f
            ? new Vector3(dest.X, dest.Y, dest.Z) / destAlpha
            : Vector3.Zero;
        var rgb = (new Vector3(source.X, source.Y, source.Z) * (1f - destAlpha))
            + (new Vector3(dest.X, dest.Y, dest.Z) * (1f - sourceAlpha))
            + (sourceAlpha * destAlpha * blend(straightSource, straightDest));
        return new Vector4(rgb, alpha);
    }

    private static float Overlay(float source, float dest) =>
        dest <= 0.5f
            ? 2f * source * dest
            : 1f - (2f * (1f - source) * (1f - dest));

    private static float ColorDodge(float source, float dest)
    {
        if (dest <= 0f)
        {
            return 0f;
        }

        return source >= 1f
            ? 1f
            : MathF.Min(1f, dest / (1f - source));
    }

    private static float ColorBurn(float source, float dest)
    {
        if (dest >= 1f)
        {
            return 1f;
        }

        return source <= 0f
            ? 0f
            : 1f - MathF.Min(1f, (1f - dest) / source);
    }

    private static float HardLight(float source, float dest) =>
        source <= 0.5f
            ? 2f * source * dest
            : 1f - (2f * (1f - source) * (1f - dest));

    private static float SoftLight(float source, float dest)
    {
        if (source <= 0.5f)
        {
            return dest - ((1f - (2f * source)) * dest * (1f - dest));
        }

        var softenedDest = dest <= 0.25f
            ? (((16f * dest) - 12f) * dest + 4f) * dest
            : MathF.Sqrt(dest);
        return dest + (((2f * source) - 1f) * (softenedDest - dest));
    }

    private static float Luminosity(Vector3 color) =>
        (0.3f * color.X) + (0.59f * color.Y) + (0.11f * color.Z);

    private static float Saturation(Vector3 color) =>
        MathF.Max(color.X, MathF.Max(color.Y, color.Z)) -
        MathF.Min(color.X, MathF.Min(color.Y, color.Z));

    private static Vector3 SetSaturation(Vector3 color, float saturation)
    {
        var minimum = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
        var maximum = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        if (maximum <= minimum)
        {
            return Vector3.Zero;
        }

        return (color - new Vector3(minimum)) * (saturation / (maximum - minimum));
    }

    private static Vector3 SetLuminosity(Vector3 color, float luminosity)
    {
        var delta = luminosity - Luminosity(color);
        return ClipColor(color + new Vector3(delta));
    }

    private static Vector3 ClipColor(Vector3 color)
    {
        var luminosity = Luminosity(color);
        var minimum = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
        if (minimum < 0f)
        {
            color = new Vector3(luminosity) +
                ((color - new Vector3(luminosity)) * (luminosity / (luminosity - minimum)));
        }

        var maximum = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        if (maximum > 1f)
        {
            color = new Vector3(luminosity) +
                ((color - new Vector3(luminosity)) * ((1f - luminosity) / (maximum - luminosity)));
        }

        return Vector3.Clamp(color, Vector3.Zero, Vector3.One);
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp(MathF.Round(Clamp01(value) * 255f), 0f, 255f);
    }

    private static float Clamp01(float value)
    {
        return Math.Clamp(value, 0f, 1f);
    }
}

public partial class SKImageFilter : SKObject
{
    internal enum FilterKind
    {
        Blur,
        Compose,
        DropShadow,
        Arithmetic,
        BlendMode,
        ColorFilter,
        Dilate,
        DisplacementMap,
        DistantLitDiffuse,
        DistantLitSpecular,
        Erode,
        Image,
        Magnifier,
        MatrixTransform,
        MatrixConvolution,
        Merge,
        Offset,
        Shader,
        Picture,
        PointLitDiffuse,
        PointLitSpecular,
        SpotLitDiffuse,
        SpotLitSpecular,
        Tile,
    }

    private SKImageFilter(FilterKind kind, object? parameters, SKImageFilter? input, SKRect? cropRect)
        : base(SKObjectHandle.Create(), owns: true)
    {
        Kind = kind;
        Parameters = parameters;
        Input = input;
        CropRect = cropRect;
    }

    internal FilterKind Kind { get; }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
    internal object? Parameters { get; }
    internal SKImageFilter? Input { get; }
    internal SKRect? CropRect { get; }

    public bool IsBlur => Kind == FilterKind.Blur;
    public bool IsDropShadow => Kind == FilterKind.DropShadow;
    public float SigmaX => Parameters switch
    {
        BlurData blur => blur.SigmaX,
        DropShadowData shadow => shadow.SigmaX,
        _ => 0f,
    };
    public float SigmaY => Parameters switch
    {
        BlurData blur => blur.SigmaY,
        DropShadowData shadow => shadow.SigmaY,
        _ => 0f,
    };
    public float Dx => Parameters is DropShadowData shadow ? shadow.Dx : 0f;
    public float Dy => Parameters is DropShadowData shadow ? shadow.Dy : 0f;
    public SKColor ShadowColor => Parameters is DropShadowData shadow ? shadow.Color : SKColor.Empty;

    public static SKImageFilter CreateBlur(float sigmaX, float sigmaY, SKImageFilter? input) =>
        new(FilterKind.Blur, new BlurData(sigmaX, sigmaY, SKShaderTileMode.Decal), input, null);

    public static SKImageFilter CreateBlur(
        float sigmaX,
        float sigmaY,
        SKShaderTileMode tileMode,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.Blur, new BlurData(sigmaX, sigmaY, tileMode), input, cropRect);

    public static SKImageFilter CreateDropShadow(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color) =>
        CreateDropShadow(dx, dy, sigmaX, sigmaY, color, null, null);

    public static SKImageFilter CreateDropShadow(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color,
        SKImageFilter? input) =>
        CreateDropShadow(dx, dy, sigmaX, sigmaY, color, input, null);

    public static SKImageFilter CreateDropShadow(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color,
        SKImageFilter? input,
        SKRect? cropRect) =>
        new(FilterKind.DropShadow, new DropShadowData(dx, dy, sigmaX, sigmaY, color, ShadowOnly: false), input, cropRect);

    public static SKImageFilter CreateDropShadowOnly(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color) =>
        CreateDropShadowOnly(dx, dy, sigmaX, sigmaY, color, null, null);

    public static SKImageFilter CreateDropShadowOnly(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color,
        SKImageFilter? input) =>
        CreateDropShadowOnly(dx, dy, sigmaX, sigmaY, color, input, null);

    public static SKImageFilter CreateDropShadowOnly(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color,
        SKImageFilter? input,
        SKRect? cropRect) =>
        new(FilterKind.DropShadow, new DropShadowData(dx, dy, sigmaX, sigmaY, color, ShadowOnly: true), input, cropRect);

    public static SKImageFilter CreateArithmetic(
        float k1,
        float k2,
        float k3,
        float k4,
        bool enforcePremultipliedColor,
        SKImageFilter? background,
        SKImageFilter? foreground = null,
        SKRect? cropRect = null) =>
        new(FilterKind.Arithmetic, new ArithmeticData(k1, k2, k3, k4, enforcePremultipliedColor, background, foreground), null, cropRect);

    public static SKImageFilter CreateBlendMode(
        SKBlendMode mode,
        SKImageFilter? background,
        SKImageFilter? foreground = null,
        SKRect? cropRect = null) =>
        new(FilterKind.BlendMode, new BlendModeData(mode, null, background, foreground), null, cropRect);

    public static SKImageFilter CreateColorFilter(
        SKColorFilter cf,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.ColorFilter, cf ?? throw new ArgumentNullException(nameof(cf)), input, cropRect);

    public static SKImageFilter CreateDilate(
        float radiusX,
        float radiusY,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.Dilate, new MorphologyData(radiusX, radiusY), input, cropRect);

    public static SKImageFilter CreateDisplacementMapEffect(
        SKColorChannel xChannelSelector,
        SKColorChannel yChannelSelector,
        float scale,
        SKImageFilter displacement,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(
            FilterKind.DisplacementMap,
            new DisplacementData(
                xChannelSelector,
                yChannelSelector,
                scale,
                displacement ?? throw new ArgumentNullException(nameof(displacement))),
            input,
            cropRect);

    public static SKImageFilter CreateDistantLitDiffuse(
        SKPoint3 direction,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.DistantLitDiffuse, new DistantLightData(direction, lightColor, surfaceScale, kd, 0f), input, cropRect);

    public static SKImageFilter CreateDistantLitSpecular(
        SKPoint3 direction,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.DistantLitSpecular, new DistantLightData(direction, lightColor, surfaceScale, ks, shininess), input, cropRect);

    public static SKImageFilter CreateErode(
        float radiusX,
        float radiusY,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.Erode, new MorphologyData(radiusX, radiusY), input, cropRect);

    public static SKImageFilter CreateImage(
        SKImage image,
        SKRect src,
        SKRect dst,
        SKSamplingOptions sampling)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new SKImageFilter(
            FilterKind.Image,
            new ImageData(image, src, dst, sampling),
            null,
            null);
    }

    public static SKImageFilter CreateOffset(
        float dx,
        float dy,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.Offset, new OffsetData(dx, dy), input, cropRect);

    public static SKImageFilter CreateShader(SKShader? shader, bool dither, SKRect? cropRect = null) =>
        new(FilterKind.Shader, new ShaderData(shader, dither), null, cropRect);

    public static SKImageFilter CreatePicture(SKPicture picture, SKRect cropRect) =>
        new(FilterKind.Picture, new PictureData(picture ?? throw new ArgumentNullException(nameof(picture)), cropRect), null, null);

    public static SKImageFilter CreatePointLitDiffuse(
        SKPoint3 location,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.PointLitDiffuse, new PointLightData(location, lightColor, surfaceScale, kd, 0f), input, cropRect);

    public static SKImageFilter CreatePointLitSpecular(
        SKPoint3 location,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.PointLitSpecular, new PointLightData(location, lightColor, surfaceScale, ks, shininess), input, cropRect);

    public static SKImageFilter CreateSpotLitDiffuse(
        SKPoint3 location,
        SKPoint3 target,
        float specularExponent,
        float cutoffAngle,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.SpotLitDiffuse, new SpotLightData(location, target, specularExponent, cutoffAngle, lightColor, surfaceScale, kd, 0f), input, cropRect);

    public static SKImageFilter CreateSpotLitSpecular(
        SKPoint3 location,
        SKPoint3 target,
        float specularExponent,
        float cutoffAngle,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.SpotLitSpecular, new SpotLightData(location, target, specularExponent, cutoffAngle, lightColor, surfaceScale, ks, shininess), input, cropRect);

    public static SKImageFilter CreateTile(SKRect src, SKRect dst, SKImageFilter? input) =>
        new(
            FilterKind.Tile,
            new TileData(src, dst),
            input ?? throw new ArgumentNullException(nameof(input)),
            null);

    private static SKImageFilter CreateMatrixConvolutionCore(
        SKSizeI kernelSize,
        ReadOnlySpan<float> kernel,
        float gain,
        float bias,
        SKPointI kernelOffset,
        SKShaderTileMode tileMode,
        bool convolveAlpha,
        SKImageFilter? input,
        SKRect? cropRect)
    {
        var requiredLength = checked(kernelSize.Width * kernelSize.Height);
        if (kernel.Length != requiredLength)
        {
            throw new ArgumentException(
                "Kernel length must match the dimensions of the kernel size (Width * Height).",
                nameof(kernel));
        }

        return new SKImageFilter(
            FilterKind.MatrixConvolution,
            new MatrixConvolutionData(
                kernelSize,
                kernel.ToArray(),
                gain,
                bias,
                kernelOffset,
                tileMode,
                convolveAlpha),
            input,
            cropRect);
    }

    private static SKImageFilter CreateMergeCore(
        ReadOnlySpan<SKImageFilter> filters,
        SKRect? cropRect)
    {
        var copy = new SKImageFilter?[filters.Length];
        for (var index = 0; index < filters.Length; index++)
        {
            copy[index] = filters[index];
        }

        return new SKImageFilter(FilterKind.Merge, copy, null, cropRect);
    }

    private static SKImageFilter CreateMergeCore(
        SKImageFilter? first,
        SKImageFilter? second,
        SKRect? cropRect) =>
        new(FilterKind.Merge, new SKImageFilter?[] { first, second }, null, cropRect);

    internal sealed record BlurData(float SigmaX, float SigmaY, SKShaderTileMode TileMode);
    internal sealed record ComposeData(SKImageFilter Outer, SKImageFilter Inner);
    internal sealed record DropShadowData(float Dx, float Dy, float SigmaX, float SigmaY, SKColor Color, bool ShadowOnly);
    internal sealed record ArithmeticData(float K1, float K2, float K3, float K4, bool EnforcePremultipliedColor, SKImageFilter? Background, SKImageFilter? Foreground);
    internal sealed record BlendModeData(SKBlendMode? Mode, SKBlender? Blender, SKImageFilter? Background, SKImageFilter? Foreground);
    internal sealed record MorphologyData(float RadiusX, float RadiusY);
    internal sealed record DisplacementData(SKColorChannel XChannel, SKColorChannel YChannel, float Scale, SKImageFilter Displacement);
    internal sealed record DistantLightData(SKPoint3 Direction, SKColor Color, float SurfaceScale, float Constant, float Shininess);
    internal sealed record ImageData(SKImage Image, SKRect Source, SKRect Destination, SKSamplingOptions Sampling);
    internal sealed record MagnifierData(SKRect LensBounds, float ZoomAmount, float Inset, SKSamplingOptions Sampling);
    internal sealed record MatrixTransformData(SKMatrix Matrix, SKSamplingOptions Sampling);
    internal sealed record MatrixConvolutionData(SKSizeI KernelSize, float[] Kernel, float Gain, float Bias, SKPointI KernelOffset, SKShaderTileMode TileMode, bool ConvolveAlpha);
    internal sealed record OffsetData(float Dx, float Dy);
    internal sealed record ShaderData(SKShader? Shader, bool Dither);
    internal sealed record PictureData(SKPicture Picture, SKRect TargetRect);
    internal sealed record PointLightData(SKPoint3 Location, SKColor Color, float SurfaceScale, float Constant, float Shininess);
    internal sealed record SpotLightData(SKPoint3 Location, SKPoint3 Target, float SpecularExponent, float CutoffAngle, SKColor Color, float SurfaceScale, float Constant, float Shininess);
    internal sealed record TileData(SKRect Source, SKRect Destination);
}

public enum SKBlurStyle
{
    Normal = 0,
    Solid = 1,
    Outer = 2,
    Inner = 3,
}

using System;
using System.Numerics;

#nullable enable

namespace ProGPU.Vector;

/// <summary>
/// Specifies how a source rectangle is deformed into a destination quadrilateral.
/// </summary>
public enum PathWarpMode
{
    Perspective = 0,
    Bilinear = 1,
}

/// <summary>
/// Deforms flattened retained paths without depending on a renderer backend.
/// </summary>
public static class PathWarpGeometry
{
    private const float Epsilon = 0.000001f;
    private const int MaximumSubdivisionDepth = 16;

    /// <summary>
    /// Maps a flattened path from a source rectangle into a three- or four-point destination.
    /// </summary>
    /// <remarks>
    /// Destination points are ordered upper-left, upper-right, lower-left, and optionally
    /// lower-right. A three-point destination implies a parallelogram. The source must contain
    /// line segments only; curves should be flattened by the owning public API so its flatness
    /// contract remains authoritative.
    /// </remarks>
    public static bool TryCreateWarpedPath(
        PathGeometry source,
        ReadOnlySpan<Vector2> destinationPoints,
        Vector2 sourceOrigin,
        Vector2 sourceSize,
        PathWarpMode mode,
        float flatness,
        out PathGeometry warpedPath)
    {
        ArgumentNullException.ThrowIfNull(source);

        warpedPath = new PathGeometry { FillRule = source.FillRule };
        if (source.IsCombined ||
            destinationPoints.Length is not (3 or 4) ||
            mode is not PathWarpMode.Perspective and not PathWarpMode.Bilinear ||
            !IsFinite(sourceOrigin) ||
            !IsFinite(sourceSize) ||
            MathF.Abs(sourceSize.X) <= Epsilon ||
            MathF.Abs(sourceSize.Y) <= Epsilon ||
            !float.IsFinite(flatness) ||
            flatness <= 0f)
        {
            return false;
        }

        Span<Vector2> quadrilateral = stackalloc Vector2[4];
        destinationPoints.CopyTo(quadrilateral);
        if (destinationPoints.Length == 3)
        {
            quadrilateral[3] = quadrilateral[1] + quadrilateral[2] - quadrilateral[0];
        }

        for (int index = 0; index < quadrilateral.Length; index++)
        {
            if (!IsFinite(quadrilateral[index]))
            {
                return false;
            }
        }

        if (!WarpTransform.TryCreate(quadrilateral, sourceOrigin, sourceSize, mode, out WarpTransform transform))
        {
            return false;
        }

        foreach (PathFigure sourceFigure in source.Figures)
        {
            if (!transform.TryMap(sourceFigure.StartPoint, out Vector2 warpedStart))
            {
                return false;
            }

            var warpedFigure = new PathFigure(warpedStart, sourceFigure.IsClosed)
            {
                IsFilled = sourceFigure.IsFilled,
                StrokeStartLineCap = sourceFigure.StrokeStartLineCap,
                StrokeEndLineCap = sourceFigure.StrokeEndLineCap,
            };

            Vector2 sourceCurrent = sourceFigure.StartPoint;
            Vector2 warpedCurrent = warpedStart;
            foreach (PathSegment sourceSegment in sourceFigure.Segments)
            {
                if (sourceSegment is not LineSegment line ||
                    !transform.TryMap(line.Point, out Vector2 warpedEnd) ||
                    !TryAppendSegment(
                        warpedFigure,
                        sourceCurrent,
                        line.Point,
                        warpedCurrent,
                        warpedEnd,
                        line.IsStroked,
                        line.IsSmoothJoin,
                        includeEnd: true,
                        transform,
                        flatness,
                        depth: 0))
                {
                    return false;
                }

                sourceCurrent = line.Point;
                warpedCurrent = warpedEnd;
            }

            if (sourceFigure.IsClosed &&
                Vector2.DistanceSquared(sourceCurrent, sourceFigure.StartPoint) > Epsilon * Epsilon &&
                !TryAppendSegment(
                    warpedFigure,
                    sourceCurrent,
                    sourceFigure.StartPoint,
                    warpedCurrent,
                    warpedStart,
                    isStroked: true,
                    isSmoothJoin: true,
                    includeEnd: false,
                    transform,
                    flatness,
                    depth: 0))
            {
                return false;
            }

            warpedPath.Figures.Add(warpedFigure);
        }

        return true;
    }

    private static bool TryAppendSegment(
        PathFigure output,
        Vector2 sourceStart,
        Vector2 sourceEnd,
        Vector2 warpedStart,
        Vector2 warpedEnd,
        bool isStroked,
        bool isSmoothJoin,
        bool includeEnd,
        WarpTransform transform,
        float flatness,
        int depth)
    {
        if (transform.Mode == PathWarpMode.Bilinear && depth < MaximumSubdivisionDepth)
        {
            Vector2 sourceMiddle = (sourceStart + sourceEnd) * 0.5f;
            if (!transform.TryMap(sourceMiddle, out Vector2 warpedMiddle))
            {
                return false;
            }

            Vector2 chordMiddle = (warpedStart + warpedEnd) * 0.5f;
            if (Vector2.DistanceSquared(warpedMiddle, chordMiddle) > flatness * flatness)
            {
                return TryAppendSegment(
                        output,
                        sourceStart,
                        sourceMiddle,
                        warpedStart,
                        warpedMiddle,
                        isStroked,
                        isSmoothJoin: true,
                        includeEnd: true,
                        transform,
                        flatness,
                        depth + 1) &&
                    TryAppendSegment(
                        output,
                        sourceMiddle,
                        sourceEnd,
                        warpedMiddle,
                        warpedEnd,
                        isStroked,
                        isSmoothJoin,
                        includeEnd,
                        transform,
                        flatness,
                        depth + 1);
            }
        }

        if (includeEnd)
        {
            output.Segments.Add(new LineSegment(warpedEnd, isSmoothJoin, isStroked));
        }

        return true;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private readonly struct WarpTransform
    {
        private readonly Vector2 _sourceOrigin;
        private readonly Vector2 _inverseSourceSize;
        private readonly Vector2 _upperLeft;
        private readonly Vector2 _upperRight;
        private readonly Vector2 _lowerLeft;
        private readonly Vector2 _lowerRight;
        private readonly float _a;
        private readonly float _b;
        private readonly float _c;
        private readonly float _d;
        private readonly float _e;
        private readonly float _f;
        private readonly float _g;
        private readonly float _h;

        private WarpTransform(
            ReadOnlySpan<Vector2> quadrilateral,
            Vector2 sourceOrigin,
            Vector2 sourceSize,
            PathWarpMode mode,
            float a,
            float b,
            float c,
            float d,
            float e,
            float f,
            float g,
            float h)
        {
            _sourceOrigin = sourceOrigin;
            _inverseSourceSize = new Vector2(1f / sourceSize.X, 1f / sourceSize.Y);
            _upperLeft = quadrilateral[0];
            _upperRight = quadrilateral[1];
            _lowerLeft = quadrilateral[2];
            _lowerRight = quadrilateral[3];
            Mode = mode;
            _a = a;
            _b = b;
            _c = c;
            _d = d;
            _e = e;
            _f = f;
            _g = g;
            _h = h;
        }

        public PathWarpMode Mode { get; }

        public static bool TryCreate(
            ReadOnlySpan<Vector2> quadrilateral,
            Vector2 sourceOrigin,
            Vector2 sourceSize,
            PathWarpMode mode,
            out WarpTransform transform)
        {
            float a = 0f;
            float b = 0f;
            float c = 0f;
            float d = 0f;
            float e = 0f;
            float f = 0f;
            float g = 0f;
            float h = 0f;

            if (mode == PathWarpMode.Perspective)
            {
                Vector2 upperLeft = quadrilateral[0];
                Vector2 upperRight = quadrilateral[1];
                Vector2 lowerLeft = quadrilateral[2];
                Vector2 lowerRight = quadrilateral[3];
                float dx1 = upperRight.X - lowerRight.X;
                float dx2 = lowerLeft.X - lowerRight.X;
                float dx3 = upperLeft.X - upperRight.X + lowerRight.X - lowerLeft.X;
                float dy1 = upperRight.Y - lowerRight.Y;
                float dy2 = lowerLeft.Y - lowerRight.Y;
                float dy3 = upperLeft.Y - upperRight.Y + lowerRight.Y - lowerLeft.Y;

                if (MathF.Abs(dx3) <= Epsilon && MathF.Abs(dy3) <= Epsilon)
                {
                    a = upperRight.X - upperLeft.X;
                    b = lowerLeft.X - upperLeft.X;
                    c = upperLeft.X;
                    d = upperRight.Y - upperLeft.Y;
                    e = lowerLeft.Y - upperLeft.Y;
                    f = upperLeft.Y;
                }
                else
                {
                    float denominator = (dx1 * dy2) - (dx2 * dy1);
                    if (!float.IsFinite(denominator) || MathF.Abs(denominator) <= Epsilon)
                    {
                        transform = default;
                        return false;
                    }

                    g = ((dx3 * dy2) - (dx2 * dy3)) / denominator;
                    h = ((dx1 * dy3) - (dx3 * dy1)) / denominator;
                    a = upperRight.X - upperLeft.X + (g * upperRight.X);
                    b = lowerLeft.X - upperLeft.X + (h * lowerLeft.X);
                    c = upperLeft.X;
                    d = upperRight.Y - upperLeft.Y + (g * upperRight.Y);
                    e = lowerLeft.Y - upperLeft.Y + (h * lowerLeft.Y);
                    f = upperLeft.Y;
                }
            }

            transform = new WarpTransform(
                quadrilateral,
                sourceOrigin,
                sourceSize,
                mode,
                a,
                b,
                c,
                d,
                e,
                f,
                g,
                h);
            return true;
        }

        public bool TryMap(Vector2 point, out Vector2 result)
        {
            float u = (point.X - _sourceOrigin.X) * _inverseSourceSize.X;
            float v = (point.Y - _sourceOrigin.Y) * _inverseSourceSize.Y;
            if (!float.IsFinite(u) || !float.IsFinite(v))
            {
                result = default;
                return false;
            }

            if (Mode == PathWarpMode.Bilinear)
            {
                float inverseU = 1f - u;
                float inverseV = 1f - v;
                result =
                    (_upperLeft * (inverseU * inverseV)) +
                    (_upperRight * (u * inverseV)) +
                    (_lowerLeft * (inverseU * v)) +
                    (_lowerRight * (u * v));
                return IsFinite(result);
            }

            float denominator = (_g * u) + (_h * v) + 1f;
            if (!float.IsFinite(denominator) || MathF.Abs(denominator) <= Epsilon)
            {
                result = default;
                return false;
            }

            result = new Vector2(
                ((_a * u) + (_b * v) + _c) / denominator,
                ((_d * u) + (_e * v) + _f) / denominator);
            return IsFinite(result);
        }
    }
}

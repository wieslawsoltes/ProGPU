using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Avalonia.Media;
using Avalonia.Platform;
using ProGPU.Scene;
using PathGeometry = ProGPU.Vector.PathGeometry;
using PathSegment = ProGPU.Vector.PathSegment;
using LineSegment = ProGPU.Vector.LineSegment;
using QuadraticBezierSegment = ProGPU.Vector.QuadraticBezierSegment;
using CubicBezierSegment = ProGPU.Vector.CubicBezierSegment;
using PathFigure = ProGPU.Vector.PathFigure;
using ProGpuPenLineCap = ProGPU.Vector.PenLineCap;
using ProGpuPenLineJoin = ProGPU.Vector.PenLineJoin;

namespace Avalonia.ProGpu
{
    internal abstract class GeometryImpl : IGeometryImpl
    {
        private RenderCommandGeometryCache? _renderCommandGeometryCache;

        public abstract ProGPU.Vector.PathGeometry Path { get; }

        protected void InvalidateCaches()
        {
            Volatile.Write(ref _renderCommandGeometryCache, null);
        }

        internal RenderCommandGeometryCache GetRenderCommandGeometryCache()
        {
            var cache = Volatile.Read(ref _renderCommandGeometryCache);
            if (cache != null)
            {
                return cache;
            }

            var created = RenderCommandGeometryCache.ForPath(Path);
            return Interlocked.CompareExchange(
                ref _renderCommandGeometryCache,
                created,
                null) ?? created;
        }

        public Rect Bounds => CalculateBounds(Path);

        public double ContourLength => CalculateLength(Path);

        public bool FillContains(Point point)
        {
            return PathContains(Path, point, Avalonia.Media.FillRule.EvenOdd);
        }

        public bool StrokeContains(IPen? pen, Point point)
        {
            double threshold = (pen?.Thickness ?? 1.0) / 2.0;
            return DistanceToPath(Path, point) <= threshold;
        }

        public IGeometryImpl? Intersect(IGeometryImpl geometry)
        {
            return this;
        }

        public Rect GetRenderBounds(IPen? pen)
        {
            var bounds = Bounds;
            if (pen == null || pen.Thickness <= 0 || !double.IsFinite(pen.Thickness))
            {
                return bounds;
            }

            return TryCalculateOpenPolylineStrokeBounds(Path, pen, out var strokeBounds)
                ? strokeBounds
                : bounds.Inflate(pen.Thickness / 2.0);
        }

        private static bool TryCalculateOpenPolylineStrokeBounds(
            PathGeometry path,
            IPen pen,
            out Rect bounds)
        {
            bounds = default;
            if (path.IsCombined || pen.DashStyle?.Dashes is { Count: > 0 })
            {
                return false;
            }

            float thickness = (float)pen.Thickness;
            float radius = thickness * 0.5f;
            var lineJoin = pen.LineJoin switch
            {
                PenLineJoin.Bevel => ProGpuPenLineJoin.Bevel,
                PenLineJoin.Round => ProGpuPenLineJoin.Round,
                _ => ProGpuPenLineJoin.Miter
            };
            var lineCap = pen.LineCap switch
            {
                PenLineCap.Round => ProGpuPenLineCap.Round,
                PenLineCap.Square => ProGpuPenLineCap.Square,
                _ => ProGpuPenLineCap.Flat
            };

            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            bool hasBounds = false;

            void Include(Vector2 point)
            {
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                {
                    return;
                }

                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
                hasBounds = true;
            }

            void IncludeTriangles(ProGPU.Vector.StrokeJoinTriangle[] triangles)
            {
                for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex++)
                {
                    var triangle = triangles[triangleIndex];
                    Include(triangle.P0);
                    Include(triangle.P1);
                    Include(triangle.P2);
                }
            }

            for (int figureIndex = 0; figureIndex < path.Figures.Count; figureIndex++)
            {
                var figure = path.Figures[figureIndex];
                if (figure.IsClosed || figure.Segments.Count == 0)
                {
                    return false;
                }

                var points = new Vector2[figure.Segments.Count + 1];
                var lines = new LineSegment[figure.Segments.Count];
                points[0] = figure.StartPoint;

                for (int segmentIndex = 0; segmentIndex < figure.Segments.Count; segmentIndex++)
                {
                    if (figure.Segments[segmentIndex] is not LineSegment { IsStroked: true } line)
                    {
                        return false;
                    }

                    var direction = line.Point - points[segmentIndex];
                    float length = direction.Length();
                    if (!float.IsFinite(length) || length <= 0.0001f)
                    {
                        return false;
                    }

                    lines[segmentIndex] = line;
                    points[segmentIndex + 1] = line.Point;
                    var normal = new Vector2(-direction.Y, direction.X) * (radius / length);
                    Include(points[segmentIndex] - normal);
                    Include(points[segmentIndex] + normal);
                    Include(line.Point - normal);
                    Include(line.Point + normal);
                }

                for (int pointIndex = 1; pointIndex < points.Length - 1; pointIndex++)
                {
                    IncludeTriangles(ProGPU.Vector.StrokeJoinGeometry.CreateLineJoin(
                        lineJoin,
                        thickness,
                        (float)pen.MiterLimit,
                        points[pointIndex - 1],
                        points[pointIndex],
                        points[pointIndex + 1],
                        lines[pointIndex].IsSmoothJoin));
                }

                IncludeTriangles(ProGPU.Vector.StrokeCapGeometry.CreateLineCap(
                    lineCap,
                    thickness,
                    points[0],
                    points[1],
                    isStart: true));
                IncludeTriangles(ProGPU.Vector.StrokeCapGeometry.CreateLineCap(
                    lineCap,
                    thickness,
                    points[^2],
                    points[^1],
                    isStart: false));
            }

            if (!hasBounds)
            {
                return false;
            }

            bounds = new Rect(min.X, min.Y, max.X - min.X, max.Y - min.Y);
            return true;
        }

        public IGeometryImpl GetWidenedGeometry(IPen pen)
        {
            return this;
        }

        public ITransformedGeometryImpl WithTransform(Matrix transform)
        {
            return new TransformedGeometryImpl(this, transform);
        }

        public bool TryGetPointAtDistance(double distance, out Point point)
        {
            point = new Point();
            double accum = 0;
            foreach (var figure in Path.Figures)
            {
                var curr = figure.StartPoint;
                foreach (var seg in figure.Segments)
                {
                    var pts = FlattenSegment(curr, seg);
                    foreach (var pt in pts)
                    {
                        double len = (pt - curr).Length();
                        if (accum + len >= distance)
                        {
                            double ratio = (distance - accum) / len;
                            var interp = curr + (float)ratio * (pt - curr);
                            point = new Point(interp.X, interp.Y);
                            return true;
                        }
                        accum += len;
                        curr = pt;
                    }
                }
            }
            return false;
        }

        public bool TryGetPointAndTangentAtDistance(double distance, out Point point, out Point tangent)
        {
            point = new Point();
            tangent = new Point(1, 0);
            double accum = 0;
            foreach (var figure in Path.Figures)
            {
                var curr = figure.StartPoint;
                foreach (var seg in figure.Segments)
                {
                    var pts = FlattenSegment(curr, seg);
                    foreach (var pt in pts)
                    {
                        double len = (pt - curr).Length();
                        if (accum + len >= distance)
                        {
                            double ratio = (distance - accum) / len;
                            var interp = curr + (float)ratio * (pt - curr);
                            point = new Point(interp.X, interp.Y);
                            var dir = Vector2.Normalize(pt - curr);
                            tangent = new Point(dir.X, dir.Y);
                            return true;
                        }
                        accum += len;
                        curr = pt;
                    }
                }
            }
            return false;
        }

        public bool TryGetSegment(double startDistance, double stopDistance, bool startOnBeginFigure,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IGeometryImpl? segmentGeometry)
        {
            segmentGeometry = this;
            return true;
        }

        public static Avalonia.Rect CalculateBounds(ProGPU.Vector.PathGeometry path)
        {
            return path.TryGetBounds(out var min, out var max)
                ? new Avalonia.Rect(min.X, min.Y, max.X - min.X, max.Y - min.Y)
                : default;
        }

        public static double CalculateLength(ProGPU.Vector.PathGeometry path)
        {
            double length = 0;
            foreach (var figure in path.Figures)
            {
                var curr = figure.StartPoint;
                foreach (var seg in figure.Segments)
                {
                    var pts = FlattenSegment(curr, seg);
                    foreach (var pt in pts)
                    {
                        length += (pt - curr).Length();
                        curr = pt;
                    }
                }
            }
            return length;
        }

        private static List<Vector2> FlattenSegment(Vector2 start, ProGPU.Vector.PathSegment segment)
        {
            var list = new List<Vector2>();
            if (segment is LineSegment line)
            {
                list.Add(line.Point);
            }
            else if (segment is QuadraticBezierSegment quad)
            {
                for (int i = 1; i <= 8; i++)
                {
                    float t = i / 8f;
                    float u = 1 - t;
                    var pt = u * u * start + 2 * u * t * quad.ControlPoint + t * t * quad.Point;
                    list.Add(pt);
                }
            }
            else if (segment is CubicBezierSegment cubic)
            {
                for (int i = 1; i <= 8; i++)
                {
                    float t = i / 8f;
                    float u = 1 - t;
                    var pt = u * u * u * start + 3 * u * u * t * cubic.ControlPoint1 + 3 * u * t * t * cubic.ControlPoint2 + t * t * t * cubic.Point;
                    list.Add(pt);
                }
            }
            return list;
        }

        public static bool PathContains(ProGPU.Vector.PathGeometry path, Point point, Avalonia.Media.FillRule fillRule)
        {
            int windingNumber = 0;
            int crossCount = 0;
            float px = (float)point.X;
            float py = (float)point.Y;

            foreach (var figure in path.Figures)
            {
                var curr = figure.StartPoint;
                var figureLines = new List<(Vector2 A, Vector2 B)>();

                foreach (var seg in figure.Segments)
                {
                    var pts = FlattenSegment(curr, seg);
                    foreach (var pt in pts)
                    {
                        figureLines.Add((curr, pt));
                        curr = pt;
                    }
                }

                if (figure.IsClosed && curr != figure.StartPoint)
                {
                    figureLines.Add((curr, figure.StartPoint));
                }

                foreach (var line in figureLines)
                {
                    var a = line.A;
                    var b = line.B;

                    bool upward = (a.Y <= py && b.Y > py);
                    bool downward = (a.Y > py && b.Y <= py);

                    if (upward || downward)
                    {
                        float t = (py - a.Y) / (b.Y - a.Y);
                        float xIntersect = a.X + t * (b.X - a.X);

                        if (px < xIntersect)
                        {
                            crossCount++;
                            if (upward) windingNumber++;
                            else windingNumber--;
                        }
                    }
                }
            }

            if (fillRule == Avalonia.Media.FillRule.EvenOdd)
            {
                return (crossCount % 2) != 0;
            }
            else
            {
                return windingNumber != 0;
            }
        }

        private static double DistanceToPath(ProGPU.Vector.PathGeometry path, Point point)
        {
            double minDistance = double.MaxValue;
            Vector2 p = new Vector2((float)point.X, (float)point.Y);

            foreach (var figure in path.Figures)
            {
                var curr = figure.StartPoint;
                var figureLines = new List<(Vector2 A, Vector2 B)>();

                foreach (var seg in figure.Segments)
                {
                    var pts = FlattenSegment(curr, seg);
                    foreach (var pt in pts)
                    {
                        figureLines.Add((curr, pt));
                        curr = pt;
                    }
                }

                if (figure.IsClosed && curr != figure.StartPoint)
                {
                    figureLines.Add((curr, figure.StartPoint));
                }

                foreach (var line in figureLines)
                {
                    var a = line.A;
                    var b = line.B;

                    float l2 = Vector2.DistanceSquared(a, b);
                    float t = 0;
                    if (l2 > 0)
                    {
                        t = Math.Max(0, Math.Min(1, Vector2.Dot(p - a, b - a) / l2));
                    }
                    var projection = a + t * (b - a);
                    double dist = Vector2.Distance(p, projection);
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                    }
                }
            }

            return minDistance;
        }
    }
}

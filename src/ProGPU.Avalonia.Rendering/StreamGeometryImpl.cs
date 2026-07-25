using System;
using System.Numerics;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Platform;
using PathGeometry = ProGPU.Vector.PathGeometry;
using PathFigure = ProGPU.Vector.PathFigure;
using LineSegment = ProGPU.Vector.LineSegment;
using QuadraticBezierSegment = ProGPU.Vector.QuadraticBezierSegment;
using CubicBezierSegment = ProGPU.Vector.CubicBezierSegment;
using ArcSegment = ProGPU.Vector.ArcSegment;

namespace Avalonia.ProGpu
{
    internal class StreamGeometryImpl : GeometryImpl, IStreamGeometryImpl
    {
        public override ProGPU.Vector.PathGeometry Path { get; }

        public StreamGeometryImpl()
        {
            Path = new ProGPU.Vector.PathGeometry
            {
                FillRule = ProGPU.Vector.FillRule.EvenOdd
            };
        }

        public StreamGeometryImpl(ProGPU.Vector.PathGeometry path)
        {
            Path = path ?? new ProGPU.Vector.PathGeometry();
        }



        public IStreamGeometryImpl Clone()
        {
            var clonedPath = new ProGPU.Vector.PathGeometry
            {
                FillRule = Path.FillRule
            };
            foreach (var figure in Path.Figures)
            {
                var clonedFigure = new ProGPU.Vector.PathFigure(figure.StartPoint, figure.IsClosed)
                {
                    IsFilled = figure.IsFilled
                };
                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line)
                    {
                        clonedFigure.Segments.Add(new LineSegment(line.Point, line.IsSmoothJoin, line.IsStroked));
                    }
                    else if (segment is QuadraticBezierSegment quad)
                    {
                        clonedFigure.Segments.Add(new QuadraticBezierSegment(
                            quad.ControlPoint,
                            quad.Point,
                            quad.IsSmoothJoin,
                            quad.IsStroked));
                    }
                    else if (segment is CubicBezierSegment cubic)
                    {
                        clonedFigure.Segments.Add(new CubicBezierSegment(
                            cubic.ControlPoint1,
                            cubic.ControlPoint2,
                            cubic.Point,
                            cubic.IsSmoothJoin,
                            cubic.IsStroked));
                    }
                    else if (segment is ArcSegment arc)
                    {
                        clonedFigure.Segments.Add(new ArcSegment(
                            arc.Point,
                            arc.Size,
                            arc.RotationAngle,
                            arc.IsLargeArc,
                            arc.SweepDirection,
                            arc.IsSmoothJoin,
                            arc.IsStroked));
                    }
                }
                clonedPath.Figures.Add(clonedFigure);
            }
            return new StreamGeometryImpl(clonedPath);
        }

        public IStreamGeometryContextImpl Open()
        {
            return new StreamContext(this);
        }

        private class StreamContext : IStreamGeometryContextImpl
        {
            private readonly StreamGeometryImpl _geometryImpl;
            private ProGPU.Vector.PathFigure? _currentFigure;

            public StreamContext(StreamGeometryImpl geometryImpl)
            {
                _geometryImpl = geometryImpl;
                _geometryImpl.InvalidateCaches();
                _geometryImpl.Path.Figures.Clear();
            }

            public void BeginFigure(Point startPoint, bool isFilled = true)
            {
                _geometryImpl.InvalidateCaches();
                _currentFigure = new ProGPU.Vector.PathFigure(new Vector2((float)startPoint.X, (float)startPoint.Y))
                {
                    IsFilled = isFilled
                };
                _geometryImpl.Path.Figures.Add(_currentFigure);
            }

            public void EndFigure(bool isClosed)
            {
                if (_currentFigure != null)
                {
                    _geometryImpl.InvalidateCaches();
                    _currentFigure.IsClosed = isClosed;
                }
            }

            public void SetFillRule(Avalonia.Media.FillRule fillRule)
            {
                _geometryImpl.InvalidateCaches();
                _geometryImpl.Path.FillRule = fillRule == Avalonia.Media.FillRule.EvenOdd
                    ? ProGPU.Vector.FillRule.EvenOdd
                    : ProGPU.Vector.FillRule.Nonzero;
            }

            public void LineTo(Point point, bool isStroked = true)
            {
                if (_currentFigure == null) return;
                _geometryImpl.InvalidateCaches();
                _currentFigure.Segments.Add(new LineSegment(
                    new Vector2((float)point.X, (float)point.Y),
                    isStroked: isStroked));
            }

            private Point CurrentPoint
            {
                get
                {
                    if (_currentFigure == null) return default;
                    if (_currentFigure.Segments.Count == 0)
                    {
                        return new Point(_currentFigure.StartPoint.X, _currentFigure.StartPoint.Y);
                    }
                    var lastSegment = _currentFigure.Segments[^1];
                    if (lastSegment is LineSegment line) return new Point(line.Point.X, line.Point.Y);
                    if (lastSegment is QuadraticBezierSegment quad) return new Point(quad.Point.X, quad.Point.Y);
                    if (lastSegment is CubicBezierSegment cubic) return new Point(cubic.Point.X, cubic.Point.Y);
                    if (lastSegment is ArcSegment arc) return new Point(arc.Point.X, arc.Point.Y);
                    return default;
                }
            }

            public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, Avalonia.Media.SweepDirection sweepDirection, bool isStroked = true)
            {
                if (_currentFigure == null) return;
                _geometryImpl.InvalidateCaches();
                var endPoint = new Vector2((float)point.X, (float)point.Y);
                var radii = new Vector2((float)size.Width, (float)size.Height);
                var direction = sweepDirection == Avalonia.Media.SweepDirection.Clockwise
                    ? ProGPU.Vector.SweepDirection.Clockwise
                    : ProGPU.Vector.SweepDirection.Counterclockwise;

                _currentFigure.Segments.Add(new ArcSegment(
                    endPoint,
                    radii,
                    (float)rotationAngle,
                    isLargeArc,
                    direction,
                    isStroked: isStroked));
            }

#if AVALONIA11
            public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, Avalonia.Media.SweepDirection sweepDirection) =>
                ArcTo(point, size, rotationAngle, isLargeArc, sweepDirection, true);
#endif

            public void CubicBezierTo(Point point1, Point point2, Point point3, bool isStroked = true)
            {
                if (_currentFigure == null) return;
                _geometryImpl.InvalidateCaches();
                _currentFigure.Segments.Add(new CubicBezierSegment(
                    new Vector2((float)point1.X, (float)point1.Y),
                    new Vector2((float)point2.X, (float)point2.Y),
                    new Vector2((float)point3.X, (float)point3.Y),
                    isStroked: isStroked
                ));
            }

#if AVALONIA11
            public void CubicBezierTo(Point point1, Point point2, Point point3) =>
                CubicBezierTo(point1, point2, point3, true);
#endif

            public void QuadraticBezierTo(Point point1, Point point2, bool isStroked = true)
            {
                if (_currentFigure == null) return;
                _geometryImpl.InvalidateCaches();
                _currentFigure.Segments.Add(new QuadraticBezierSegment(
                    new Vector2((float)point1.X, (float)point1.Y),
                    new Vector2((float)point2.X, (float)point2.Y),
                    isStroked: isStroked
                ));
            }

#if AVALONIA11
            public void QuadraticBezierTo(Point point1, Point point2) =>
                QuadraticBezierTo(point1, point2, true);

            public void LineTo(Point point) => LineTo(point, true);
#endif

            public void Dispose()
            {
            }
        }
    }
}

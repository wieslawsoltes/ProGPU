using System;
using System.Collections.Generic;
using System.Numerics;

namespace System.Windows.Media;

public abstract class PathSegment
{
}

public class LineSegment : PathSegment
{
    public Vector2 Point { get; set; }

    public LineSegment() { }
    public LineSegment(Vector2 point) { Point = point; }
}

public class QuadraticBezierSegment : PathSegment
{
    public Vector2 Point1 { get; set; }
    public Vector2 Point2 { get; set; }

    public QuadraticBezierSegment() { }
    public QuadraticBezierSegment(Vector2 point1, Vector2 point2)
    {
        Point1 = point1;
        Point2 = point2;
    }
}

public class BezierSegment : PathSegment
{
    public Vector2 Point1 { get; set; }
    public Vector2 Point2 { get; set; }
    public Vector2 Point3 { get; set; }

    public BezierSegment() { }
    public BezierSegment(Vector2 point1, Vector2 point2, Vector2 point3)
    {
        Point1 = point1;
        Point2 = point2;
        Point3 = point3;
    }
}

public class ArcSegment : PathSegment
{
    public Vector2 Point { get; set; }
    public Vector2 Size { get; set; }
    public float RotationAngle { get; set; }
    public bool IsLargeArc { get; set; }
    public SweepDirection SweepDirection { get; set; }

    public ArcSegment() { }
}

public class PathFigure
{
    public Vector2 StartPoint { get; set; }
    public List<PathSegment> Segments { get; } = new();
    public bool IsClosed { get; set; }
    public bool IsFilled { get; set; } = true;
}

public enum FillRule
{
    EvenOdd = 0,
    Nonzero = 1
}

public enum SweepDirection
{
    Counterclockwise = 0,
    Clockwise = 1
}

public class PathGeometry : Geometry
{
    public FillRule FillRule { get; set; } = FillRule.EvenOdd;
    public List<PathFigure> Figures { get; } = new();

    public static PathGeometry Parse(string pathData)
    {
        var geom = new PathGeometry();
        if (string.IsNullOrWhiteSpace(pathData)) return geom;

        var internalGeom = ProGPU.Vector.PathGeometry.Parse(pathData);
        foreach (var fig in internalGeom.Figures)
        {
            var figure = new PathFigure
            {
                StartPoint = new Vector2(fig.StartPoint.X, fig.StartPoint.Y),
                IsClosed = fig.IsClosed,
                IsFilled = fig.IsFilled
            };
            foreach (var seg in fig.Segments)
            {
                if (seg is ProGPU.Vector.LineSegment line)
                {
                    figure.Segments.Add(new LineSegment(new Vector2(line.Point.X, line.Point.Y)));
                }
                else if (seg is ProGPU.Vector.QuadraticBezierSegment quad)
                {
                    figure.Segments.Add(new QuadraticBezierSegment(
                        new Vector2(quad.ControlPoint.X, quad.ControlPoint.Y),
                        new Vector2(quad.Point.X, quad.Point.Y)));
                }
                else if (seg is ProGPU.Vector.CubicBezierSegment cubic)
                {
                    figure.Segments.Add(new BezierSegment(
                        new Vector2(cubic.ControlPoint1.X, cubic.ControlPoint1.Y),
                        new Vector2(cubic.ControlPoint2.X, cubic.ControlPoint2.Y),
                        new Vector2(cubic.Point.X, cubic.Point.Y)));
                }
            }
            geom.Figures.Add(figure);
        }
        return geom;
    }

    public override void Draw(ProGPU.Scene.DrawingContext context, ProGPU.Vector.Brush? fill, ProGPU.Vector.Pen? pen)
    {
        var internalGeom = new ProGPU.Vector.PathGeometry();
        var mat = Transform != null ? Transform.Value : Matrix4x4.Identity;
        foreach (var fig in Figures)
        {
            var start = Vector2.Transform(fig.StartPoint, mat);
            var figure = new ProGPU.Vector.PathFigure
            {
                StartPoint = start,
                IsClosed = fig.IsClosed,
                IsFilled = fig.IsFilled
            };
            foreach (var seg in fig.Segments)
            {
                if (seg is LineSegment line)
                {
                    figure.Segments.Add(new ProGPU.Vector.LineSegment(Vector2.Transform(line.Point, mat)));
                }
                else if (seg is QuadraticBezierSegment quad)
                {
                    figure.Segments.Add(new ProGPU.Vector.QuadraticBezierSegment(
                        Vector2.Transform(quad.Point1, mat),
                        Vector2.Transform(quad.Point2, mat)));
                }
                else if (seg is BezierSegment cubic)
                {
                    figure.Segments.Add(new ProGPU.Vector.CubicBezierSegment(
                        Vector2.Transform(cubic.Point1, mat),
                        Vector2.Transform(cubic.Point2, mat),
                        Vector2.Transform(cubic.Point3, mat)));
                }
                else if (seg is ArcSegment arc)
                {
                    figure.Segments.Add(new ProGPU.Vector.LineSegment(Vector2.Transform(arc.Point, mat)));
                }
            }
            internalGeom.Figures.Add(figure);
        }
        context.DrawPath(fill, pen, internalGeom);
    }

    public override Rect Bounds
    {
        get
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            var mat = Transform != null ? Transform.Value : Matrix4x4.Identity;

            void Update(Vector2 pt)
            {
                var p = Vector2.Transform(pt, mat);
                minX = MathF.Min(minX, p.X);
                minY = MathF.Min(minY, p.Y);
                maxX = MathF.Max(maxX, p.X);
                maxY = MathF.Max(maxY, p.Y);
            }

            foreach (var fig in Figures)
            {
                Update(fig.StartPoint);
                foreach (var seg in fig.Segments)
                {
                    if (seg is LineSegment line) Update(line.Point);
                    else if (seg is QuadraticBezierSegment quad) { Update(quad.Point1); Update(quad.Point2); }
                    else if (seg is BezierSegment cubic) { Update(cubic.Point1); Update(cubic.Point2); Update(cubic.Point3); }
                    else if (seg is ArcSegment arc) Update(arc.Point);
                }
            }

            if (minX == float.MaxValue) return Rect.Empty;
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }
}

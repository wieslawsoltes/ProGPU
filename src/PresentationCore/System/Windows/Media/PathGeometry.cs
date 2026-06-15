using System;
using System.Collections.Generic;
using System.Numerics;

namespace System.Windows.Media;

public abstract class PathSegment
{
    public bool IsSmoothJoin { get; set; }
}

public class LineSegment : PathSegment
{
    public Vector2 Point { get; set; }

    public LineSegment() { }
    public LineSegment(Vector2 point, bool isSmoothJoin = false)
    {
        Point = point;
        IsSmoothJoin = isSmoothJoin;
    }
}

public class QuadraticBezierSegment : PathSegment
{
    public Vector2 Point1 { get; set; }
    public Vector2 Point2 { get; set; }

    public QuadraticBezierSegment() { }
    public QuadraticBezierSegment(Vector2 point1, Vector2 point2, bool isSmoothJoin = false)
    {
        Point1 = point1;
        Point2 = point2;
        IsSmoothJoin = isSmoothJoin;
    }
}

public class BezierSegment : PathSegment
{
    public Vector2 Point1 { get; set; }
    public Vector2 Point2 { get; set; }
    public Vector2 Point3 { get; set; }

    public BezierSegment() { }
    public BezierSegment(Vector2 point1, Vector2 point2, Vector2 point3, bool isSmoothJoin = false)
    {
        Point1 = point1;
        Point2 = point2;
        Point3 = point3;
        IsSmoothJoin = isSmoothJoin;
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

    public ArcSegment(
        Vector2 point,
        Vector2 size,
        float rotationAngle,
        bool isLargeArc,
        SweepDirection sweepDirection,
        bool isSmoothJoin = false)
    {
        Point = point;
        Size = size;
        RotationAngle = rotationAngle;
        IsLargeArc = isLargeArc;
        SweepDirection = sweepDirection;
        IsSmoothJoin = isSmoothJoin;
    }
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
                    figure.Segments.Add(new LineSegment(new Vector2(line.Point.X, line.Point.Y), line.IsSmoothJoin));
                }
                else if (seg is ProGPU.Vector.QuadraticBezierSegment quad)
                {
                    figure.Segments.Add(new QuadraticBezierSegment(
                        new Vector2(quad.ControlPoint.X, quad.ControlPoint.Y),
                        new Vector2(quad.Point.X, quad.Point.Y),
                        quad.IsSmoothJoin));
                }
                else if (seg is ProGPU.Vector.CubicBezierSegment cubic)
                {
                    figure.Segments.Add(new BezierSegment(
                        new Vector2(cubic.ControlPoint1.X, cubic.ControlPoint1.Y),
                        new Vector2(cubic.ControlPoint2.X, cubic.ControlPoint2.Y),
                        new Vector2(cubic.Point.X, cubic.Point.Y),
                        cubic.IsSmoothJoin));
                }
                else if (seg is ProGPU.Vector.ArcSegment arc)
                {
                    figure.Segments.Add(new ArcSegment(
                        new Vector2(arc.Point.X, arc.Point.Y),
                        new Vector2(arc.Size.X, arc.Size.Y),
                        arc.RotationAngle,
                        arc.IsLargeArc,
                        (SweepDirection)(int)arc.SweepDirection,
                        arc.IsSmoothJoin));
                }
            }
            geom.Figures.Add(figure);
        }
        return geom;
    }

    public override void Draw(ProGPU.Scene.DrawingContext context, ProGPU.Vector.Brush? fill, ProGPU.Vector.Pen? pen)
    {
        _ = TryGetPathGeometry(out var strokePath, out var mat);

        var hasUnfilledFigures = false;
        if (fill != null)
        {
            foreach (var figure in Figures)
            {
                if (!figure.IsFilled)
                {
                    hasUnfilledFigures = true;
                    break;
                }
            }
        }

        if (!hasUnfilledFigures)
        {
            if (fill != null || pen != null)
            {
                GeometryPathHelper.DrawPath(context, fill, pen, strokePath, mat);
            }

            return;
        }

        if (fill != null)
        {
            var fillPath = ToProGpuPathGeometry(includeUnfilledFigures: false);
            if (fillPath.Figures.Count > 0)
            {
                GeometryPathHelper.DrawPath(context, fill, null, fillPath, mat);
            }
        }

        if (pen != null)
        {
            GeometryPathHelper.DrawPath(context, null, pen, strokePath, mat);
        }
    }

    internal override bool TryGetPathGeometry(out ProGPU.Vector.PathGeometry path, out Matrix4x4 transform)
    {
        path = ToProGpuPathGeometry();
        transform = Transform != null ? Transform.Value : Matrix4x4.Identity;
        return true;
    }

    internal ProGPU.Vector.PathGeometry ToProGpuPathGeometry(bool includeUnfilledFigures = true)
    {
        var internalGeom = new ProGPU.Vector.PathGeometry();
        foreach (var fig in Figures)
        {
            if (!includeUnfilledFigures && !fig.IsFilled)
            {
                continue;
            }

            var figure = new ProGPU.Vector.PathFigure
            {
                StartPoint = fig.StartPoint,
                IsClosed = fig.IsClosed,
                IsFilled = fig.IsFilled
            };
            foreach (var seg in fig.Segments)
            {
                if (seg is LineSegment line)
                {
                    figure.Segments.Add(new ProGPU.Vector.LineSegment(line.Point, line.IsSmoothJoin));
                }
                else if (seg is QuadraticBezierSegment quad)
                {
                    figure.Segments.Add(new ProGPU.Vector.QuadraticBezierSegment(
                        quad.Point1,
                        quad.Point2,
                        quad.IsSmoothJoin));
                }
                else if (seg is BezierSegment cubic)
                {
                    figure.Segments.Add(new ProGPU.Vector.CubicBezierSegment(
                        cubic.Point1,
                        cubic.Point2,
                        cubic.Point3,
                        cubic.IsSmoothJoin));
                }
                else if (seg is ArcSegment arc)
                {
                    figure.Segments.Add(ToVectorArcSegment(arc));
                }
            }
            internalGeom.Figures.Add(figure);
        }

        return internalGeom;
    }

    public override Rect Bounds
    {
        get
        {
            _ = TryGetPathGeometry(out var path, out var transform);
            return GeometryPathHelper.GetBounds(path, transform);
        }
    }

    private static ProGPU.Vector.ArcSegment ToVectorArcSegment(ArcSegment arc)
    {
        return new ProGPU.Vector.ArcSegment(
            arc.Point,
            arc.Size,
            arc.RotationAngle,
            arc.IsLargeArc,
            (ProGPU.Vector.SweepDirection)(int)arc.SweepDirection,
            arc.IsSmoothJoin);
    }
}

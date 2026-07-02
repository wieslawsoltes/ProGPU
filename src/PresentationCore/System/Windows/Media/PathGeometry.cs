using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;

namespace System.Windows.Media;

public abstract class PathSegment
{
    public bool IsSmoothJoin { get; set; }
    public bool IsStroked { get; set; } = true;
}

public class LineSegment : PathSegment
{
    public Point Point { get; set; }

    public LineSegment() { }

    public LineSegment(Point point, bool isStroked)
    {
        Point = point;
        IsStroked = isStroked;
    }

    public LineSegment(Vector2 point)
        : this(new Point(point.X, point.Y), isStroked: true)
    {
    }

    public LineSegment(Vector2 point, bool isSmoothJoin, bool isStroked)
        : this(new Point(point.X, point.Y), isStroked)
    {
        IsSmoothJoin = isSmoothJoin;
    }
}

public class QuadraticBezierSegment : PathSegment
{
    public Point Point1 { get; set; }
    public Point Point2 { get; set; }

    public QuadraticBezierSegment() { }

    public QuadraticBezierSegment(Point point1, Point point2, bool isStroked)
    {
        Point1 = point1;
        Point2 = point2;
        IsStroked = isStroked;
    }

    public QuadraticBezierSegment(Vector2 point1, Vector2 point2)
        : this(new Point(point1.X, point1.Y), new Point(point2.X, point2.Y), isStroked: true)
    {
    }

    public QuadraticBezierSegment(Vector2 point1, Vector2 point2, bool isSmoothJoin, bool isStroked)
        : this(new Point(point1.X, point1.Y), new Point(point2.X, point2.Y), isStroked)
    {
        IsSmoothJoin = isSmoothJoin;
    }
}

public class BezierSegment : PathSegment
{
    public Point Point1 { get; set; }
    public Point Point2 { get; set; }
    public Point Point3 { get; set; }

    public BezierSegment() { }

    public BezierSegment(Point point1, Point point2, Point point3, bool isStroked)
    {
        Point1 = point1;
        Point2 = point2;
        Point3 = point3;
        IsStroked = isStroked;
    }

    public BezierSegment(Vector2 point1, Vector2 point2, Vector2 point3)
        : this(new Point(point1.X, point1.Y), new Point(point2.X, point2.Y), new Point(point3.X, point3.Y), isStroked: true)
    {
    }

    public BezierSegment(Vector2 point1, Vector2 point2, Vector2 point3, bool isSmoothJoin, bool isStroked)
        : this(new Point(point1.X, point1.Y), new Point(point2.X, point2.Y), new Point(point3.X, point3.Y), isStroked)
    {
        IsSmoothJoin = isSmoothJoin;
    }
}

public class ArcSegment : PathSegment
{
    public Point Point { get; set; }
    public Size Size { get; set; }
    public double RotationAngle { get; set; }
    public bool IsLargeArc { get; set; }
    public SweepDirection SweepDirection { get; set; }

    public ArcSegment() { }

    public ArcSegment(
        Point point,
        Size size,
        double rotationAngle,
        bool isLargeArc,
        SweepDirection sweepDirection,
        bool isStroked)
    {
        Point = point;
        Size = size;
        RotationAngle = rotationAngle;
        IsLargeArc = isLargeArc;
        SweepDirection = sweepDirection;
        IsStroked = isStroked;
    }

    public ArcSegment(
        Vector2 point,
        Vector2 size,
        float rotationAngle,
        bool isLargeArc,
        SweepDirection sweepDirection,
        bool isSmoothJoin = false,
        bool isStroked = true)
        : this(new Point(point.X, point.Y), new Size(Math.Abs(size.X), Math.Abs(size.Y)), rotationAngle, isLargeArc, sweepDirection, isStroked)
    {
        IsSmoothJoin = isSmoothJoin;
    }
}

public class PathFigure
{
    public PathFigure()
    {
    }

    public PathFigure(Point startPoint)
    {
        StartPoint = startPoint;
    }

    public PathFigure(Point start, IEnumerable<PathSegment> segments, bool closed)
    {
        StartPoint = start;
        Segments = new PathSegmentCollection(segments);
        IsClosed = closed;
    }

    public PathFigure(Vector2 startPoint)
        : this(new Point(startPoint.X, startPoint.Y))
    {
    }

    public Point StartPoint { get; set; }
    public PathSegmentCollection Segments { get; set; } = new();
    public bool IsClosed { get; set; }
    public bool IsFilled { get; set; } = true;
}

public class PathSegmentCollection : List<PathSegment>
{
    public PathSegmentCollection()
    {
    }

    public PathSegmentCollection(IEnumerable<PathSegment> collection)
        : base(collection)
    {
    }

    public PathSegmentCollection(int capacity)
        : base(capacity)
    {
    }
}

public class PathFigureCollection : List<PathFigure>
{
    public PathFigureCollection()
    {
    }

    public PathFigureCollection(IEnumerable<PathFigure> collection)
        : base(collection)
    {
    }

    public PathFigureCollection(int capacity)
        : base(capacity)
    {
    }
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
    public PathGeometry()
    {
    }

    public PathGeometry(IEnumerable<PathFigure> figures)
    {
        Figures = new PathFigureCollection(figures);
    }

    public PathGeometry(IEnumerable<PathFigure> figures, FillRule fillRule, Transform? transform)
    {
        Figures = new PathFigureCollection(figures);
        FillRule = fillRule;
        Transform = transform;
    }

    public FillRule FillRule { get; set; } = FillRule.EvenOdd;
    public PathFigureCollection Figures { get; set; } = new();

    public static new PathGeometry Parse(string pathData)
    {
        var geom = new PathGeometry();
        if (string.IsNullOrWhiteSpace(pathData)) return geom;

        var trimmedPathData = pathData.TrimStart();
        if (TryReadFillRulePrefix(trimmedPathData, out var fillRule, out var geometryData))
        {
            geom.FillRule = fillRule;
            trimmedPathData = geometryData;
        }

        var internalGeom = ProGPU.Vector.PathGeometry.Parse(trimmedPathData);
        foreach (var fig in internalGeom.Figures)
        {
            var figure = new PathFigure
            {
                StartPoint = ToPoint(fig.StartPoint),
                IsClosed = fig.IsClosed,
                IsFilled = fig.IsFilled
            };
            foreach (var seg in fig.Segments)
            {
                if (seg is ProGPU.Vector.LineSegment line)
                {
                    figure.Segments.Add(new LineSegment(ToPoint(line.Point), line.IsStroked)
                    {
                        IsSmoothJoin = line.IsSmoothJoin
                    });
                }
                else if (seg is ProGPU.Vector.QuadraticBezierSegment quad)
                {
                    figure.Segments.Add(new QuadraticBezierSegment(ToPoint(quad.ControlPoint), ToPoint(quad.Point), quad.IsStroked)
                    {
                        IsSmoothJoin = quad.IsSmoothJoin
                    });
                }
                else if (seg is ProGPU.Vector.CubicBezierSegment cubic)
                {
                    figure.Segments.Add(new BezierSegment(
                        ToPoint(cubic.ControlPoint1),
                        ToPoint(cubic.ControlPoint2),
                        ToPoint(cubic.Point),
                        cubic.IsStroked)
                    {
                        IsSmoothJoin = cubic.IsSmoothJoin
                    });
                }
                else if (seg is ProGPU.Vector.ArcSegment arc)
                {
                    figure.Segments.Add(new ArcSegment(
                        ToPoint(arc.Point),
                        ToSize(arc.Size),
                        arc.RotationAngle,
                        arc.IsLargeArc,
                        (SweepDirection)(int)arc.SweepDirection,
                        arc.IsStroked)
                    {
                        IsSmoothJoin = arc.IsSmoothJoin
                    });
                }
            }
            geom.Figures.Add(figure);
        }
        return geom;
    }

    private static bool TryReadFillRulePrefix(string pathData, out FillRule fillRule, out string geometryData)
    {
        fillRule = default;
        geometryData = pathData;

        if (pathData.Length < 2 || (pathData[0] != 'F' && pathData[0] != 'f'))
        {
            return false;
        }

        var ruleIndex = 1;
        while (ruleIndex < pathData.Length && (char.IsWhiteSpace(pathData[ruleIndex]) || pathData[ruleIndex] == ','))
        {
            ruleIndex++;
        }

        if (ruleIndex >= pathData.Length || (pathData[ruleIndex] != '0' && pathData[ruleIndex] != '1'))
        {
            return false;
        }

        var afterRuleIndex = ruleIndex + 1;
        if (pathData.Length > afterRuleIndex && !char.IsWhiteSpace(pathData[afterRuleIndex]) && pathData[afterRuleIndex] != ',')
        {
            return false;
        }

        fillRule = pathData[ruleIndex] == '1' ? FillRule.Nonzero : FillRule.EvenOdd;
        geometryData = pathData[afterRuleIndex..].TrimStart(' ', '\t', '\r', '\n', ',');
        return true;
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
        var internalGeom = new ProGPU.Vector.PathGeometry
        {
            FillRule = ToVectorFillRule(FillRule)
        };
        foreach (var fig in Figures)
        {
            if (!includeUnfilledFigures && !fig.IsFilled)
            {
                continue;
            }

            var figure = new ProGPU.Vector.PathFigure
            {
                StartPoint = ToVector2(fig.StartPoint),
                IsClosed = fig.IsClosed,
                IsFilled = fig.IsFilled
            };
            foreach (var seg in fig.Segments)
            {
                if (seg is LineSegment line)
                {
                    figure.Segments.Add(new ProGPU.Vector.LineSegment(ToVector2(line.Point), line.IsSmoothJoin, line.IsStroked));
                }
                else if (seg is QuadraticBezierSegment quad)
                {
                    figure.Segments.Add(new ProGPU.Vector.QuadraticBezierSegment(
                        ToVector2(quad.Point1),
                        ToVector2(quad.Point2),
                        quad.IsSmoothJoin,
                        quad.IsStroked));
                }
                else if (seg is BezierSegment cubic)
                {
                    figure.Segments.Add(new ProGPU.Vector.CubicBezierSegment(
                        ToVector2(cubic.Point1),
                        ToVector2(cubic.Point2),
                        ToVector2(cubic.Point3),
                        cubic.IsSmoothJoin,
                        cubic.IsStroked));
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
            ToVector2(arc.Point),
            ToVector2(arc.Size),
            (float)arc.RotationAngle,
            arc.IsLargeArc,
            (ProGPU.Vector.SweepDirection)(int)arc.SweepDirection,
            arc.IsSmoothJoin,
            arc.IsStroked);
    }

    private static Point ToPoint(Vector2 point)
    {
        return new Point(point.X, point.Y);
    }

    private static Size ToSize(Vector2 size)
    {
        return new Size(Math.Abs(size.X), Math.Abs(size.Y));
    }

    private static Vector2 ToVector2(Point point)
    {
        return new Vector2((float)point.X, (float)point.Y);
    }

    private static Vector2 ToVector2(Size size)
    {
        return new Vector2((float)size.Width, (float)size.Height);
    }

    private static ProGPU.Vector.FillRule ToVectorFillRule(FillRule fillRule)
    {
        return fillRule == FillRule.EvenOdd
            ? ProGPU.Vector.FillRule.EvenOdd
            : ProGPU.Vector.FillRule.Nonzero;
    }
}

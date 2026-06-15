using Microsoft.UI.Xaml;
using ProGPU.Vector;
using ProGPU.Scene;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.UI.Xaml.Media;

public enum SweepDirection
{
    Counterclockwise = 0,
    Clockwise = 1
}

public enum FillRule
{
    EvenOdd = 0,
    Nonzero = 1
}

public abstract class PathSegment : DependencyObject
{
}

public class LineSegment : PathSegment
{
    public static readonly DependencyProperty PointProperty =
        DependencyProperty.Register("Point", typeof(Vector2), typeof(LineSegment), new PropertyMetadata(Vector2.Zero));

    public Vector2 Point
    {
        get => (Vector2)(GetValue(PointProperty) ?? Vector2.Zero);
        set => SetValue(PointProperty, value);
    }

    public LineSegment() { }
    public LineSegment(Vector2 point) { Point = point; }
}

public class QuadraticBezierSegment : PathSegment
{
    public static readonly DependencyProperty Point1Property =
        DependencyProperty.Register("Point1", typeof(Vector2), typeof(QuadraticBezierSegment), new PropertyMetadata(Vector2.Zero));

    public Vector2 Point1
    {
        get => (Vector2)(GetValue(Point1Property) ?? Vector2.Zero);
        set => SetValue(Point1Property, value);
    }

    public static readonly DependencyProperty Point2Property =
        DependencyProperty.Register("Point2", typeof(Vector2), typeof(QuadraticBezierSegment), new PropertyMetadata(Vector2.Zero));

    public Vector2 Point2
    {
        get => (Vector2)(GetValue(Point2Property) ?? Vector2.Zero);
        set => SetValue(Point2Property, value);
    }

    public QuadraticBezierSegment() { }
    public QuadraticBezierSegment(Vector2 point1, Vector2 point2) { Point1 = point1; Point2 = point2; }
}

public class BezierSegment : PathSegment
{
    public static readonly DependencyProperty Point1Property =
        DependencyProperty.Register("Point1", typeof(Vector2), typeof(BezierSegment), new PropertyMetadata(Vector2.Zero));

    public Vector2 Point1
    {
        get => (Vector2)(GetValue(Point1Property) ?? Vector2.Zero);
        set => SetValue(Point1Property, value);
    }

    public static readonly DependencyProperty Point2Property =
        DependencyProperty.Register("Point2", typeof(Vector2), typeof(BezierSegment), new PropertyMetadata(Vector2.Zero));

    public Vector2 Point2
    {
        get => (Vector2)(GetValue(Point2Property) ?? Vector2.Zero);
        set => SetValue(Point2Property, value);
    }

    public static readonly DependencyProperty Point3Property =
        DependencyProperty.Register("Point3", typeof(Vector2), typeof(BezierSegment), new PropertyMetadata(Vector2.Zero));

    public Vector2 Point3
    {
        get => (Vector2)(GetValue(Point3Property) ?? Vector2.Zero);
        set => SetValue(Point3Property, value);
    }

    public BezierSegment() { }
    public BezierSegment(Vector2 point1, Vector2 point2, Vector2 point3) { Point1 = point1; Point2 = point2; Point3 = point3; }
}

public class ArcSegment : PathSegment
{
    public static readonly DependencyProperty PointProperty =
        DependencyProperty.Register("Point", typeof(Vector2), typeof(ArcSegment), new PropertyMetadata(Vector2.Zero));

    public Vector2 Point
    {
        get => (Vector2)(GetValue(PointProperty) ?? Vector2.Zero);
        set => SetValue(PointProperty, value);
    }

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register("Size", typeof(Vector2), typeof(ArcSegment), new PropertyMetadata(Vector2.Zero));

    public Vector2 Size
    {
        get => (Vector2)(GetValue(SizeProperty) ?? Vector2.Zero);
        set => SetValue(SizeProperty, value);
    }

    public static readonly DependencyProperty RotationAngleProperty =
        DependencyProperty.Register("RotationAngle", typeof(float), typeof(ArcSegment), new PropertyMetadata(0f));

    public float RotationAngle
    {
        get => (float)(GetValue(RotationAngleProperty) ?? 0f);
        set => SetValue(RotationAngleProperty, value);
    }

    public static readonly DependencyProperty IsLargeArcProperty =
        DependencyProperty.Register("IsLargeArc", typeof(bool), typeof(ArcSegment), new PropertyMetadata(false));

    public bool IsLargeArc
    {
        get => (bool)(GetValue(IsLargeArcProperty) ?? false);
        set => SetValue(IsLargeArcProperty, value);
    }

    public static readonly DependencyProperty SweepDirectionProperty =
        DependencyProperty.Register("SweepDirection", typeof(SweepDirection), typeof(ArcSegment), new PropertyMetadata(SweepDirection.Counterclockwise));

    public SweepDirection SweepDirection
    {
        get => (SweepDirection)(GetValue(SweepDirectionProperty) ?? SweepDirection.Counterclockwise);
        set => SetValue(SweepDirectionProperty, value);
    }

    public ArcSegment() { }

    public ArcSegment(
        Vector2 point,
        Vector2 size,
        float rotationAngle,
        bool isLargeArc,
        SweepDirection sweepDirection)
    {
        Point = point;
        Size = size;
        RotationAngle = rotationAngle;
        IsLargeArc = isLargeArc;
        SweepDirection = sweepDirection;
    }
}

public class PathFigure : DependencyObject
{
    public static readonly DependencyProperty StartPointProperty =
        DependencyProperty.Register("StartPoint", typeof(Vector2), typeof(PathFigure), new PropertyMetadata(Vector2.Zero));

    public Vector2 StartPoint
    {
        get => (Vector2)(GetValue(StartPointProperty) ?? Vector2.Zero);
        set => SetValue(StartPointProperty, value);
    }

    public List<PathSegment> Segments { get; } = new();

    public static readonly DependencyProperty IsClosedProperty =
        DependencyProperty.Register("IsClosed", typeof(bool), typeof(PathFigure), new PropertyMetadata(false));

    public bool IsClosed
    {
        get => (bool)(GetValue(IsClosedProperty) ?? false);
        set => SetValue(IsClosedProperty, value);
    }

    public static readonly DependencyProperty IsFilledProperty =
        DependencyProperty.Register("IsFilled", typeof(bool), typeof(PathFigure), new PropertyMetadata(true));

    public bool IsFilled
    {
        get => (bool)(GetValue(IsFilledProperty) ?? true);
        set => SetValue(IsFilledProperty, value);
    }

    public PathFigure() { }
    public PathFigure(Vector2 startPoint, bool isClosed = false)
    {
        StartPoint = startPoint;
        IsClosed = isClosed;
    }
}

public class PathGeometry : Geometry
{
    public static readonly DependencyProperty FillRuleProperty =
        DependencyProperty.Register("FillRule", typeof(FillRule), typeof(PathGeometry), new PropertyMetadata(FillRule.EvenOdd));

    public FillRule FillRule
    {
        get => (FillRule)(GetValue(FillRuleProperty) ?? FillRule.EvenOdd);
        set => SetValue(FillRuleProperty, value);
    }

    public List<PathFigure> Figures { get; } = new();

    public static PathGeometry Parse(string svgPathData)
    {
        var geom = new PathGeometry();
        if (string.IsNullOrWhiteSpace(svgPathData)) return geom;

        // Parse using lower-level SVG parser
        var internalGeom = ProGPU.Vector.PathGeometry.Parse(svgPathData);
        foreach (var internalFig in internalGeom.Figures)
        {
            var fig = new PathFigure
            {
                StartPoint = internalFig.StartPoint,
                IsClosed = internalFig.IsClosed,
                IsFilled = internalFig.IsFilled
            };

            foreach (var internalSeg in internalFig.Segments)
            {
                if (internalSeg is ProGPU.Vector.LineSegment line)
                {
                    fig.Segments.Add(new LineSegment(line.Point));
                }
                else if (internalSeg is ProGPU.Vector.QuadraticBezierSegment quad)
                {
                    fig.Segments.Add(new QuadraticBezierSegment(quad.ControlPoint, quad.Point));
                }
                else if (internalSeg is ProGPU.Vector.CubicBezierSegment cubic)
                {
                    fig.Segments.Add(new BezierSegment(cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point));
                }
                else if (internalSeg is ProGPU.Vector.ArcSegment arc)
                {
                    fig.Segments.Add(new ArcSegment(
                        arc.Point,
                        arc.Size,
                        arc.RotationAngle,
                        arc.IsLargeArc,
                        (SweepDirection)(int)arc.SweepDirection));
                }
            }

            geom.Figures.Add(fig);
        }

        return geom;
    }

    public ProGPU.Vector.PathGeometry GetTransformedInternalGeometry()
    {
        var geom = new ProGPU.Vector.PathGeometry
        {
            FillRule = ToVectorFillRule(FillRule)
        };
        foreach (var figure in Figures)
        {
            var sourceCurrentPoint = figure.StartPoint;
            var newFigure = new ProGPU.Vector.PathFigure
            {
                StartPoint = TransformPoint(figure.StartPoint),
                IsClosed = figure.IsClosed,
                IsFilled = figure.IsFilled
            };

            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line)
                {
                    newFigure.Segments.Add(new ProGPU.Vector.LineSegment(TransformPoint(line.Point)));
                    sourceCurrentPoint = line.Point;
                }
                else if (segment is QuadraticBezierSegment quad)
                {
                    newFigure.Segments.Add(new ProGPU.Vector.QuadraticBezierSegment(
                        TransformPoint(quad.Point1),
                        TransformPoint(quad.Point2)));
                    sourceCurrentPoint = quad.Point2;
                }
                else if (segment is BezierSegment cubic)
                {
                    newFigure.Segments.Add(new ProGPU.Vector.CubicBezierSegment(
                        TransformPoint(cubic.Point1),
                        TransformPoint(cubic.Point2),
                        TransformPoint(cubic.Point3)));
                    sourceCurrentPoint = cubic.Point3;
                }
                else if (segment is ArcSegment arc)
                {
                    if (ProGPU.Vector.ArcSegmentGeometry.TryTransformArcSegment(
                            sourceCurrentPoint,
                            ToVectorArcSegment(arc),
                            EffectiveTransform,
                            out _,
                            out var transformedArc))
                    {
                        newFigure.Segments.Add(transformedArc);
                    }
                    else
                    {
                        newFigure.Segments.Add(new ProGPU.Vector.LineSegment(TransformPoint(arc.Point)));
                    }

                    sourceCurrentPoint = arc.Point;
                }
            }

            geom.Figures.Add(newFigure);
        }
        return geom;
    }

    public override void Draw(DrawingContext context, Brush? fill, Pen? pen)
    {
        var internalGeom = GetTransformedInternalGeometry();
        context.DrawPath(fill, pen, internalGeom);
    }

    public override Rect Bounds
    {
        get
        {
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            void UpdateBounds(Vector2 p)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }

            foreach (var figure in Figures)
            {
                var sourceCurrentPoint = figure.StartPoint;
                UpdateBounds(TransformPoint(figure.StartPoint));

                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line)
                    {
                        var pt = TransformPoint(line.Point);
                        UpdateBounds(pt);
                        sourceCurrentPoint = line.Point;
                    }
                    else if (segment is QuadraticBezierSegment quad)
                    {
                        var p1 = TransformPoint(quad.Point1);
                        var p2 = TransformPoint(quad.Point2);
                        UpdateBounds(p1);
                        UpdateBounds(p2);
                        sourceCurrentPoint = quad.Point2;
                    }
                    else if (segment is BezierSegment cubic)
                    {
                        var p1 = TransformPoint(cubic.Point1);
                        var p2 = TransformPoint(cubic.Point2);
                        var p3 = TransformPoint(cubic.Point3);
                        UpdateBounds(p1);
                        UpdateBounds(p2);
                        UpdateBounds(p3);
                        sourceCurrentPoint = cubic.Point3;
                    }
                    else if (segment is ArcSegment arc)
                    {
                        if (ProGPU.Vector.ArcSegmentGeometry.TryTransformArcSegment(
                                sourceCurrentPoint,
                                ToVectorArcSegment(arc),
                                EffectiveTransform,
                                out var transformedStart,
                                out var transformedArc) &&
                            ProGPU.Vector.ArcSegmentGeometry.TryGetArcBounds(
                                transformedStart,
                                transformedArc,
                                out var arcMin,
                                out var arcMax))
                        {
                            UpdateBounds(arcMin);
                            UpdateBounds(arcMax);
                        }
                        else
                        {
                            UpdateBounds(TransformPoint(arc.Point));
                        }

                        sourceCurrentPoint = arc.Point;
                    }
                }

                if (figure.IsClosed)
                {
                    UpdateBounds(TransformPoint(figure.StartPoint));
                }
            }

            if (minX == float.MaxValue) return Rect.Empty;
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }

    private static ProGPU.Vector.ArcSegment ToVectorArcSegment(ArcSegment arc)
    {
        return new ProGPU.Vector.ArcSegment(
            arc.Point,
            arc.Size,
            arc.RotationAngle,
            arc.IsLargeArc,
            (ProGPU.Vector.SweepDirection)(int)arc.SweepDirection);
    }

    private static ProGPU.Vector.FillRule ToVectorFillRule(FillRule fillRule)
    {
        return fillRule == FillRule.EvenOdd
            ? ProGPU.Vector.FillRule.EvenOdd
            : ProGPU.Vector.FillRule.Nonzero;
    }
}

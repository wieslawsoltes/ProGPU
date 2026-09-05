using System;

namespace ProGPU.Wpf.Interop;

public interface IPortableGeometryPathSource
{
    bool TryGetPortableGeometryPath(out PortableGeometryPath path);
}

public interface IPortablePrimitiveGeometrySource
{
    bool TryGetPortablePrimitiveGeometry(out PortablePrimitiveGeometry geometry);
}

public enum PortablePrimitiveGeometryKind
{
    Line = 0,
    Rectangle = 1,
    Ellipse = 2
}

public readonly struct PortablePrimitiveGeometry
{
    private PortablePrimitiveGeometry(
        PortablePrimitiveGeometryKind kind,
        PortablePoint point1,
        PortablePoint point2,
        PortableRect rect,
        double radiusX,
        double radiusY,
        PortableMatrix3x2 transform)
    {
        Kind = kind;
        Point1 = point1;
        Point2 = point2;
        Rect = rect;
        RadiusX = radiusX;
        RadiusY = radiusY;
        Transform = transform;
    }

    public PortablePrimitiveGeometryKind Kind { get; }

    public PortablePoint Point1 { get; }

    public PortablePoint Point2 { get; }

    public PortableRect Rect { get; }

    public double RadiusX { get; }

    public double RadiusY { get; }

    public PortableMatrix3x2 Transform { get; }

    public static PortablePrimitiveGeometry Line(
        PortablePoint startPoint,
        PortablePoint endPoint,
        PortableMatrix3x2 transform) => new(
            PortablePrimitiveGeometryKind.Line,
            startPoint,
            endPoint,
            PortableRect.Empty,
            0.0,
            0.0,
            transform);

    public static PortablePrimitiveGeometry Rectangle(
        PortableRect rect,
        double radiusX,
        double radiusY,
        PortableMatrix3x2 transform) => new(
            PortablePrimitiveGeometryKind.Rectangle,
            default,
            default,
            rect,
            radiusX,
            radiusY,
            transform);

    public static PortablePrimitiveGeometry Ellipse(
        PortablePoint center,
        double radiusX,
        double radiusY,
        PortableMatrix3x2 transform) => new(
            PortablePrimitiveGeometryKind.Ellipse,
            center,
            default,
            PortableRect.Empty,
            radiusX,
            radiusY,
            transform);
}

public enum PortableGeometryPathKind
{
    Path = 0,
    Combined = 1
}

public enum PortableFillRule
{
    EvenOdd = 0,
    Nonzero = 1
}

public enum PortableSweepDirection
{
    Counterclockwise = 0,
    Clockwise = 1
}

public enum PortablePathSegmentKind
{
    Line = 0,
    QuadraticBezier = 1,
    CubicBezier = 2,
    Arc = 3
}

public sealed class PortableGeometryPath
{
    public PortableGeometryPathKind Kind { get; set; }

    public PortableFillRule FillRule { get; set; } = PortableFillRule.Nonzero;

    public PortableMatrix3x2 Transform { get; set; } = PortableMatrix3x2.Identity;

    public PortableRect Bounds { get; set; } = PortableRect.Empty;

    public PortablePathFigure[] Figures { get; set; } = Array.Empty<PortablePathFigure>();

    public PortableGeometryPath? PathA { get; set; }

    public PortableGeometryPath? PathB { get; set; }

    public int CombineOperation { get; set; }
}

public sealed class PortablePathFigure
{
    public PortablePoint StartPoint { get; set; }

    public bool IsClosed { get; set; }

    public bool IsFilled { get; set; } = true;

    public PortablePathSegment[] Segments { get; set; } = Array.Empty<PortablePathSegment>();
}

public readonly struct PortablePathSegment
{
    private PortablePathSegment(
        PortablePathSegmentKind kind,
        PortablePoint point1,
        PortablePoint point2,
        PortablePoint point3,
        PortableSize size,
        double rotationAngle,
        bool isLargeArc,
        PortableSweepDirection sweepDirection,
        bool isSmoothJoin,
        bool isStroked)
    {
        Kind = kind;
        Point1 = point1;
        Point2 = point2;
        Point3 = point3;
        Size = size;
        RotationAngle = rotationAngle;
        IsLargeArc = isLargeArc;
        SweepDirection = sweepDirection;
        IsSmoothJoin = isSmoothJoin;
        IsStroked = isStroked;
    }

    public PortablePathSegmentKind Kind { get; }

    public PortablePoint Point1 { get; }

    public PortablePoint Point2 { get; }

    public PortablePoint Point3 { get; }

    public PortableSize Size { get; }

    public double RotationAngle { get; }

    public bool IsLargeArc { get; }

    public PortableSweepDirection SweepDirection { get; }

    public bool IsSmoothJoin { get; }

    public bool IsStroked { get; }

    public static PortablePathSegment Line(PortablePoint point, bool isSmoothJoin, bool isStroked)
    {
        return new PortablePathSegment(
            PortablePathSegmentKind.Line,
            point,
            default,
            default,
            default,
            0.0,
            false,
            PortableSweepDirection.Counterclockwise,
            isSmoothJoin,
            isStroked);
    }

    public static PortablePathSegment QuadraticBezier(PortablePoint point1, PortablePoint point2, bool isSmoothJoin, bool isStroked)
    {
        return new PortablePathSegment(
            PortablePathSegmentKind.QuadraticBezier,
            point1,
            point2,
            default,
            default,
            0.0,
            false,
            PortableSweepDirection.Counterclockwise,
            isSmoothJoin,
            isStroked);
    }

    public static PortablePathSegment CubicBezier(PortablePoint point1, PortablePoint point2, PortablePoint point3, bool isSmoothJoin, bool isStroked)
    {
        return new PortablePathSegment(
            PortablePathSegmentKind.CubicBezier,
            point1,
            point2,
            point3,
            default,
            0.0,
            false,
            PortableSweepDirection.Counterclockwise,
            isSmoothJoin,
            isStroked);
    }

    public static PortablePathSegment Arc(
        PortablePoint point,
        PortableSize size,
        double rotationAngle,
        bool isLargeArc,
        PortableSweepDirection sweepDirection,
        bool isSmoothJoin,
        bool isStroked)
    {
        return new PortablePathSegment(
            PortablePathSegmentKind.Arc,
            point,
            default,
            default,
            size,
            rotationAngle,
            isLargeArc,
            sweepDirection,
            isSmoothJoin,
            isStroked);
    }
}

public readonly struct PortablePoint
{
    public PortablePoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; }

    public double Y { get; }
}

public readonly struct PortableSize
{
    public PortableSize(double width, double height)
    {
        Width = width;
        Height = height;
    }

    public double Width { get; }

    public double Height { get; }
}

public readonly struct PortableRect
{
    public PortableRect(double x, double y, double width, double height, bool isEmpty = false)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        IsEmpty = isEmpty;
    }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public bool IsEmpty { get; }

    public static PortableRect Empty { get; } = new(0, 0, 0, 0, isEmpty: true);
}

public readonly struct PortableMatrix3x2
{
    public PortableMatrix3x2(
        double m11,
        double m12,
        double m21,
        double m22,
        double offsetX,
        double offsetY)
    {
        M11 = m11;
        M12 = m12;
        M21 = m21;
        M22 = m22;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public double M11 { get; }

    public double M12 { get; }

    public double M21 { get; }

    public double M22 { get; }

    public double OffsetX { get; }

    public double OffsetY { get; }

    public bool IsIdentity =>
        M11 == 1.0
        && M12 == 0.0
        && M21 == 0.0
        && M22 == 1.0
        && OffsetX == 0.0
        && OffsetY == 0.0;

    public static PortableMatrix3x2 Identity { get; } = new(1.0, 0.0, 0.0, 1.0, 0.0, 0.0);
}

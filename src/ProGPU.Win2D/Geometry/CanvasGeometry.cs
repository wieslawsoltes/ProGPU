using System.Numerics;
using ProGPU.Vector;
using Windows.Foundation;

namespace Microsoft.Graphics.Canvas.Geometry;

public enum CanvasGeometryCombine
{
    Union = 0,
    Intersect = 1,
    Xor = 2,
    Exclude = 3
}

public sealed class CanvasGeometry : IDisposable
{
    private bool _isDisposed;

    private CanvasGeometry(CanvasDevice device, PathGeometry path)
    {
        Device = device;
        Path = path;
    }

    public CanvasDevice Device { get; }

    internal PathGeometry Path
    {
        get
        {
            ThrowIfDisposed();
            return field;
        }
    }

    public static float DefaultFlatteningTolerance => 0.25f;

    public static CanvasGeometry CreateRectangle(
        ICanvasResourceCreator resourceCreator,
        Rect rectangle) =>
        CreateRectangle(
            resourceCreator,
            (float)rectangle.X,
            (float)rectangle.Y,
            (float)rectangle.Width,
            (float)rectangle.Height);

    public static CanvasGeometry CreateRectangle(
        ICanvasResourceCreator resourceCreator,
        float x,
        float y,
        float width,
        float height)
    {
        CanvasDevice device = GetDevice(resourceCreator);
        ValidateRectangle(x, y, width, height);
        return new CanvasGeometry(
            device,
            PrimitivePathGeometry.CreateRectangle(x, y, width, height));
    }

    public static CanvasGeometry CreateRoundedRectangle(
        ICanvasResourceCreator resourceCreator,
        Rect rectangle,
        float radiusX,
        float radiusY) =>
        CreateRoundedRectangle(
            resourceCreator,
            (float)rectangle.X,
            (float)rectangle.Y,
            (float)rectangle.Width,
            (float)rectangle.Height,
            radiusX,
            radiusY);

    public static CanvasGeometry CreateRoundedRectangle(
        ICanvasResourceCreator resourceCreator,
        float x,
        float y,
        float width,
        float height,
        float radiusX,
        float radiusY)
    {
        CanvasDevice device = GetDevice(resourceCreator);
        ValidateRectangle(x, y, width, height);
        ValidateRadii(radiusX, radiusY);
        return new CanvasGeometry(
            device,
            PrimitivePathGeometry.CreateRoundedRectangle(
                x,
                y,
                width,
                height,
                radiusX,
                radiusY));
    }

    public static CanvasGeometry CreateEllipse(
        ICanvasResourceCreator resourceCreator,
        Vector2 centerPoint,
        float radiusX,
        float radiusY) =>
        CreateEllipse(
            resourceCreator,
            centerPoint.X,
            centerPoint.Y,
            radiusX,
            radiusY);

    public static CanvasGeometry CreateEllipse(
        ICanvasResourceCreator resourceCreator,
        float x,
        float y,
        float radiusX,
        float radiusY)
    {
        CanvasDevice device = GetDevice(resourceCreator);
        ValidateFinite(x, y);
        ValidateRadii(radiusX, radiusY);
        return new CanvasGeometry(
            device,
            PrimitivePathGeometry.CreateEllipse(
                new Vector2(x, y),
                radiusX,
                radiusY));
    }

    public static CanvasGeometry CreateCircle(
        ICanvasResourceCreator resourceCreator,
        Vector2 centerPoint,
        float radius) =>
        CreateEllipse(resourceCreator, centerPoint, radius, radius);

    public static CanvasGeometry CreateCircle(
        ICanvasResourceCreator resourceCreator,
        float x,
        float y,
        float radius) =>
        CreateEllipse(resourceCreator, x, y, radius, radius);

    public static CanvasGeometry CreatePath(CanvasPathBuilder pathBuilder)
    {
        ArgumentNullException.ThrowIfNull(pathBuilder);
        return new CanvasGeometry(
            pathBuilder.Device,
            pathBuilder.CloseAndTakePath());
    }

    public static CanvasGeometry CreatePolygon(
        ICanvasResourceCreator resourceCreator,
        Vector2[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        CanvasDevice device = GetDevice(resourceCreator);
        var path = new PathGeometry();
        if (points.Length == 0)
        {
            return new CanvasGeometry(device, path);
        }

        ValidateFinite(points[0].X, points[0].Y);
        var figure = new PathFigure(points[0], isClosed: true);
        for (int index = 1; index < points.Length; index++)
        {
            ValidateFinite(points[index].X, points[index].Y);
            figure.Segments.Add(new LineSegment(points[index]));
        }
        path.Figures.Add(figure);
        return new CanvasGeometry(device, path);
    }

    public static CanvasGeometry CreateGroup(
        ICanvasResourceCreator resourceCreator,
        CanvasGeometry[] geometries) =>
        CreateGroup(
            resourceCreator,
            geometries,
            CanvasFilledRegionDetermination.Alternate);

    public static CanvasGeometry CreateGroup(
        ICanvasResourceCreator resourceCreator,
        CanvasGeometry[] geometries,
        CanvasFilledRegionDetermination filledRegionDetermination)
    {
        ArgumentNullException.ThrowIfNull(geometries);
        CanvasDevice device = GetDevice(resourceCreator);
        var path = new PathGeometry
        {
            FillRule = filledRegionDetermination switch
            {
                CanvasFilledRegionDetermination.Alternate => FillRule.EvenOdd,
                CanvasFilledRegionDetermination.Winding => FillRule.Nonzero,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(filledRegionDetermination))
            }
        };
        for (int geometryIndex = 0;
             geometryIndex < geometries.Length;
             geometryIndex++)
        {
            CanvasGeometry geometry = geometries[geometryIndex] ??
                throw new ArgumentException(
                    "Canvas geometry groups cannot contain null entries.",
                    nameof(geometries));
            geometry.ValidateDevice(device);
            PathGeometry addition = ClonePath(geometry.Path);
            for (int figureIndex = 0;
                 figureIndex < addition.Figures.Count;
                 figureIndex++)
            {
                path.Figures.Add(addition.Figures[figureIndex]);
            }
        }
        return new CanvasGeometry(device, path);
    }

    public CanvasGeometry CombineWith(
        CanvasGeometry otherGeometry,
        Matrix3x2 otherGeometryTransform,
        CanvasGeometryCombine combine) =>
        CombineWith(
            otherGeometry,
            otherGeometryTransform,
            combine,
            DefaultFlatteningTolerance);

    public CanvasGeometry CombineWith(
        CanvasGeometry otherGeometry,
        Matrix3x2 otherGeometryTransform,
        CanvasGeometryCombine combine,
        float flatteningTolerance)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(otherGeometry);
        otherGeometry.ValidateDevice(Device);
        ValidateMatrix(otherGeometryTransform);
        if (!float.IsFinite(flatteningTolerance) || flatteningTolerance <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(flatteningTolerance));
        }

        int operation = combine switch
        {
            CanvasGeometryCombine.Exclude => 0,
            CanvasGeometryCombine.Intersect => 1,
            CanvasGeometryCombine.Union => 2,
            CanvasGeometryCombine.Xor => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(combine))
        };
        PathGeometry left = Path;
        PathGeometry right = otherGeometryTransform == Matrix3x2.Identity
            ? otherGeometry.Path
            : otherGeometry.Path.CreateTransformed(
                ToMatrix4x4(otherGeometryTransform));
        var combined = new PathGeometry
        {
            IsCombined = true,
            PathA = left,
            PathB = right,
            Op = operation,
            FillRule = left.FillRule == FillRule.EvenOdd ||
                right.FillRule == FillRule.EvenOdd
                ? FillRule.EvenOdd
                : left.FillRule
        };
        return new CanvasGeometry(Device, combined);
    }

    public CanvasGeometry Transform(Matrix3x2 transform)
    {
        ThrowIfDisposed();
        ValidateMatrix(transform);
        return new CanvasGeometry(
            Device,
            Path.CreateTransformed(ToMatrix4x4(transform)));
    }

    public void Dispose()
    {
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    internal void ValidateDevice(CanvasDevice requiredDevice)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(Device, requiredDevice))
        {
            throw new ArgumentException(
                "Canvas geometry resources must belong to the drawing-session device.");
        }
    }

    internal static PathGeometry ClonePath(PathGeometry source)
    {
        var clone = new PathGeometry { FillRule = source.FillRule };
        if (source.IsCombined)
        {
            throw new NotSupportedException(
                "Appending combined Canvas geometry is not yet supported by the portable path builder.");
        }

        for (int figureIndex = 0; figureIndex < source.Figures.Count; figureIndex++)
        {
            PathFigure sourceFigure = source.Figures[figureIndex];
            var figure = new PathFigure(sourceFigure.StartPoint, sourceFigure.IsClosed)
            {
                IsFilled = sourceFigure.IsFilled,
                StrokeStartLineCap = sourceFigure.StrokeStartLineCap,
                StrokeEndLineCap = sourceFigure.StrokeEndLineCap
            };
            for (int segmentIndex = 0;
                 segmentIndex < sourceFigure.Segments.Count;
                 segmentIndex++)
            {
                figure.Segments.Add(CloneSegment(
                    sourceFigure.Segments[segmentIndex]));
            }
            clone.Figures.Add(figure);
        }
        return clone;
    }

    private static PathSegment CloneSegment(PathSegment segment) =>
        segment switch
        {
            LineSegment line => new LineSegment(
                line.Point,
                line.IsSmoothJoin,
                line.IsStroked),
            QuadraticBezierSegment quadratic => new QuadraticBezierSegment(
                quadratic.ControlPoint,
                quadratic.Point,
                quadratic.IsSmoothJoin,
                quadratic.IsStroked),
            CubicBezierSegment cubic => new CubicBezierSegment(
                cubic.ControlPoint1,
                cubic.ControlPoint2,
                cubic.Point,
                cubic.IsSmoothJoin,
                cubic.IsStroked),
            ArcSegment arc => new ArcSegment(
                arc.Point,
                arc.Size,
                arc.RotationAngle,
                arc.IsLargeArc,
                arc.SweepDirection,
                arc.IsSmoothJoin,
                arc.IsStroked),
            _ => throw new NotSupportedException(
                $"Unsupported portable path segment {segment.GetType().Name}.")
        };

    private static CanvasDevice GetDevice(
        ICanvasResourceCreator resourceCreator)
    {
        ArgumentNullException.ThrowIfNull(resourceCreator);
        return resourceCreator.Device;
    }

    private static void ValidateRectangle(
        float x,
        float y,
        float width,
        float height)
    {
        ValidateFinite(x, y);
        ValidateFinite(width, height);
        if (width < 0f || height < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
    }

    private static void ValidateRadii(float radiusX, float radiusY)
    {
        ValidateFinite(radiusX, radiusY);
        if (radiusX < 0f || radiusY < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusX));
        }
    }

    private static void ValidateFinite(float first, float second)
    {
        if (!float.IsFinite(first) || !float.IsFinite(second))
        {
            throw new ArgumentOutOfRangeException(nameof(first));
        }
    }

    private static void ValidateMatrix(in Matrix3x2 value)
    {
        if (!float.IsFinite(value.M11) || !float.IsFinite(value.M12) ||
            !float.IsFinite(value.M21) || !float.IsFinite(value.M22) ||
            !float.IsFinite(value.M31) || !float.IsFinite(value.M32))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static Matrix4x4 ToMatrix4x4(in Matrix3x2 value) =>
        new(
            value.M11, value.M12, 0f, 0f,
            value.M21, value.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            value.M31, value.M32, 0f, 1f);

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(CanvasGeometry));
        }
    }
}

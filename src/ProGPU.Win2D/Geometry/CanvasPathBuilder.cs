using System.Numerics;
using ProGPU.Vector;

namespace Microsoft.Graphics.Canvas.Geometry;

public enum CanvasArcSize
{
    Small = 0,
    Large = 1
}

public enum CanvasFigureFill
{
    Default = 0,
    DoesNotAffectFills = 1
}

public enum CanvasFigureLoop
{
    Open = 0,
    Closed = 1
}

[Flags]
public enum CanvasFigureSegmentOptions
{
    None = 0,
    ForceUnstroked = 1,
    ForceRoundLineJoin = 2
}

public enum CanvasFilledRegionDetermination
{
    Alternate = 0,
    Winding = 1
}

public enum CanvasSweepDirection
{
    CounterClockwise = 0,
    Clockwise = 1
}

public sealed class CanvasPathBuilder : IDisposable
{
    private PathGeometry? _path = new();
    private PathFigure? _figure;
    private CanvasFigureSegmentOptions _segmentOptions;
    private bool _beginFigureOccurred;

    public CanvasPathBuilder(ICanvasResourceCreator resourceCreator)
    {
        ArgumentNullException.ThrowIfNull(resourceCreator);
        Device = resourceCreator.Device;
    }

    internal CanvasDevice Device { get; }

    public void BeginFigure(Vector2 startPoint) =>
        BeginFigure(startPoint, CanvasFigureFill.Default);

    public void BeginFigure(
        Vector2 startPoint,
        CanvasFigureFill figureFill) =>
        BeginFigure(startPoint.X, startPoint.Y, figureFill);

    public void BeginFigure(float startX, float startY) =>
        BeginFigure(startX, startY, CanvasFigureFill.Default);

    public void BeginFigure(
        float startX,
        float startY,
        CanvasFigureFill figureFill)
    {
        PathGeometry path = GetPath();
        ValidateFinite(startX, startY);
        if (_figure is not null)
        {
            throw new InvalidOperationException(
                "EndFigure must be called before beginning another figure.");
        }
        if (!Enum.IsDefined(figureFill))
        {
            throw new ArgumentOutOfRangeException(nameof(figureFill));
        }

        _figure = new PathFigure(new Vector2(startX, startY))
        {
            IsFilled = figureFill == CanvasFigureFill.Default
        };
        path.Figures.Add(_figure);
        _beginFigureOccurred = true;
    }

    public void AddArc(
        Vector2 endPoint,
        float radiusX,
        float radiusY,
        float rotationAngle,
        CanvasSweepDirection sweepDirection,
        CanvasArcSize arcSize)
    {
        ValidateFinite(endPoint.X, endPoint.Y);
        ValidateRadii(radiusX, radiusY);
        if (!float.IsFinite(rotationAngle))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationAngle));
        }
        if (!Enum.IsDefined(sweepDirection))
        {
            throw new ArgumentOutOfRangeException(nameof(sweepDirection));
        }
        if (!Enum.IsDefined(arcSize))
        {
            throw new ArgumentOutOfRangeException(nameof(arcSize));
        }

        AddSegment(new ArcSegment(
            endPoint,
            new Vector2(radiusX, radiusY),
            rotationAngle * 180f / MathF.PI,
            arcSize == CanvasArcSize.Large,
            sweepDirection == CanvasSweepDirection.Clockwise
                ? SweepDirection.Clockwise
                : SweepDirection.Counterclockwise,
            IsSmoothJoin,
            IsStroked));
    }

    public void AddArc(
        Vector2 centerPoint,
        float radiusX,
        float radiusY,
        float startAngle,
        float sweepAngle)
    {
        ValidateFinite(centerPoint.X, centerPoint.Y);
        ValidateRadii(radiusX, radiusY);
        ValidateFinite(startAngle, sweepAngle);
        PathFigure figure = GetFigure();

        bool isFullCircle =
            MathF.Abs(sweepAngle) >= MathF.Tau - float.Epsilon;
        if (isFullCircle)
        {
            sweepAngle = MathF.CopySign(MathF.PI, sweepAngle);
        }

        Vector2 startPoint = new(
            centerPoint.X + MathF.Cos(startAngle) * radiusX,
            centerPoint.Y + MathF.Sin(startAngle) * radiusY);
        Vector2 endPoint = new(
            centerPoint.X + MathF.Cos(startAngle + sweepAngle) * radiusX,
            centerPoint.Y + MathF.Sin(startAngle + sweepAngle) * radiusY);

        figure.Segments.Add(new LineSegment(
            startPoint,
            IsSmoothJoin,
            IsStroked));
        ArcSegment arc = new(
            endPoint,
            new Vector2(radiusX, radiusY),
            0f,
            MathF.Abs(sweepAngle) > MathF.PI,
            sweepAngle >= 0f
                ? SweepDirection.Clockwise
                : SweepDirection.Counterclockwise,
            IsSmoothJoin,
            IsStroked);
        figure.Segments.Add(arc);
        if (isFullCircle)
        {
            figure.Segments.Add(new ArcSegment(
                startPoint,
                arc.Size,
                arc.RotationAngle,
                arc.IsLargeArc,
                arc.SweepDirection,
                arc.IsSmoothJoin,
                arc.IsStroked));
        }
    }

    public void AddCubicBezier(
        Vector2 controlPoint1,
        Vector2 controlPoint2,
        Vector2 endPoint)
    {
        ValidateFinite(controlPoint1.X, controlPoint1.Y);
        ValidateFinite(controlPoint2.X, controlPoint2.Y);
        ValidateFinite(endPoint.X, endPoint.Y);
        AddSegment(new CubicBezierSegment(
            controlPoint1,
            controlPoint2,
            endPoint,
            IsSmoothJoin,
            IsStroked));
    }

    public void AddLine(Vector2 endPoint) =>
        AddLine(endPoint.X, endPoint.Y);

    public void AddLine(float x, float y)
    {
        ValidateFinite(x, y);
        AddSegment(new LineSegment(
            new Vector2(x, y),
            IsSmoothJoin,
            IsStroked));
    }

    public void AddQuadraticBezier(
        Vector2 controlPoint,
        Vector2 endPoint)
    {
        ValidateFinite(controlPoint.X, controlPoint.Y);
        ValidateFinite(endPoint.X, endPoint.Y);
        AddSegment(new QuadraticBezierSegment(
            controlPoint,
            endPoint,
            IsSmoothJoin,
            IsStroked));
    }

    public void SetFilledRegionDetermination(
        CanvasFilledRegionDetermination filledRegionDetermination)
    {
        PathGeometry path = GetPath();
        if (_beginFigureOccurred)
        {
            throw new InvalidOperationException(
                "Filled-region determination must be set before the first figure.");
        }
        path.FillRule = filledRegionDetermination switch
        {
            CanvasFilledRegionDetermination.Alternate => FillRule.EvenOdd,
            CanvasFilledRegionDetermination.Winding => FillRule.Nonzero,
            _ => throw new ArgumentOutOfRangeException(
                nameof(filledRegionDetermination))
        };
    }

    public void SetSegmentOptions(
        CanvasFigureSegmentOptions figureSegmentOptions)
    {
        _ = GetPath();
        const CanvasFigureSegmentOptions supported =
            CanvasFigureSegmentOptions.ForceUnstroked |
            CanvasFigureSegmentOptions.ForceRoundLineJoin;
        if ((figureSegmentOptions & ~supported) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(figureSegmentOptions));
        }
        _segmentOptions = figureSegmentOptions;
    }

    public void EndFigure(CanvasFigureLoop figureLoop)
    {
        PathFigure figure = GetFigure();
        figure.IsClosed = figureLoop switch
        {
            CanvasFigureLoop.Open => false,
            CanvasFigureLoop.Closed => true,
            _ => throw new ArgumentOutOfRangeException(nameof(figureLoop))
        };
        _figure = null;
    }

    public void AddGeometry(CanvasGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        PathGeometry path = GetPath();
        if (_figure is not null)
        {
            throw new InvalidOperationException(
                "Canvas geometry cannot be appended in the middle of a figure.");
        }
        geometry.ValidateDevice(Device);
        PathGeometry addition = CanvasGeometry.ClonePath(geometry.Path);
        for (int index = 0; index < addition.Figures.Count; index++)
        {
            path.Figures.Add(addition.Figures[index]);
        }
    }

    public void Dispose()
    {
        _path = null;
        _figure = null;
        GC.SuppressFinalize(this);
    }

    internal PathGeometry CloseAndTakePath()
    {
        PathGeometry path = GetPath();
        if (_figure is not null)
        {
            throw new InvalidOperationException(
                "EndFigure must be called before creating CanvasGeometry.");
        }
        _path = null;
        return path;
    }

    private bool IsStroked =>
        (_segmentOptions & CanvasFigureSegmentOptions.ForceUnstroked) == 0;

    private bool IsSmoothJoin =>
        (_segmentOptions & CanvasFigureSegmentOptions.ForceRoundLineJoin) != 0;

    private void AddSegment(PathSegment segment) =>
        GetFigure().Segments.Add(segment);

    private PathFigure GetFigure()
    {
        _ = GetPath();
        return _figure ?? throw new InvalidOperationException(
            "BeginFigure must be called before adding path data.");
    }

    private PathGeometry GetPath() =>
        _path ?? throw new ObjectDisposedException(nameof(CanvasPathBuilder));

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
}

using System.Globalization;

namespace ProGPU.CAD;

/// <summary>The construction used by one regular POLYGON command.</summary>
public enum CadPolygonAuthoringMode : byte
{
    Inscribed = 0,
    Circumscribed = 1,
    Edge = 2,
}

/// <summary>The exact point expected by the current POLYGON prompt.</summary>
public enum CadPolygonAuthoringInputKind : byte
{
    CenterPoint = 0,
    FirstEdgePoint = 1,
    RadiusPoint = 2,
    SecondEdgePoint = 3,
}

/// <summary>A bounded invariant POLYGON side count.</summary>
public readonly record struct CadPolygonSideCount
{
    public const int Minimum = 3;
    public const int Maximum = 1024;
    public const int MaximumCodeUnits = 4;

    public int Value { get; }

    public CadPolygonSideCount(int value)
    {
        if (value is < Minimum or > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"A POLYGON side count must be from {Minimum} through {Maximum}.");
        }
        Value = value;
    }

    public static bool TryParse(
        string? text,
        out CadPolygonSideCount sideCount)
    {
        sideCount = default;
        if (text is null)
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.IsEmpty ||
            span.Length > MaximumCodeUnits ||
            !int.TryParse(
                span,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int value) ||
            value is < Minimum or > Maximum)
        {
            return false;
        }

        sideCount = new CadPolygonSideCount(value);
        return true;
    }
}

/// <summary>Immutable analytic definition of one regular plan polygon.</summary>
public readonly record struct CadPolygonAuthoringSnapshot
{
    private const double TwoPi = Math.PI * 2.0;

    public CadPolygonAuthoringMode Mode { get; }

    public int SideCount { get; }

    public CadPoint3D Center { get; }

    public double Circumradius { get; }

    public double Apothem => Circumradius * Math.Cos(Math.PI / SideCount);

    public double EdgeLength =>
        2.0 * Circumradius * Math.Sin(Math.PI / SideCount);

    public double FirstVertexAngle { get; }

    public double StepAngle => TwoPi / SideCount;

    public CadPolygonAuthoringSnapshot(
        CadPolygonAuthoringMode mode,
        int sideCount,
        CadPoint3D center,
        double circumradius,
        double firstVertexAngle)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (sideCount is < CadPolygonSideCount.Minimum or
            > CadPolygonSideCount.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(sideCount));
        }
        if (!IsFinite(center))
        {
            throw new ArgumentException(
                "A POLYGON center must contain finite WCS coordinates.",
                nameof(center));
        }
        if (!double.IsFinite(circumradius) ||
            circumradius <= 0.0 ||
            circumradius > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(circumradius),
                circumradius,
                "A POLYGON circumradius must be finite, positive, and renderable as a retained float vector.");
        }
        if (!double.IsFinite(firstVertexAngle))
        {
            throw new ArgumentOutOfRangeException(nameof(firstVertexAngle));
        }

        Mode = mode;
        SideCount = sideCount;
        Center = center;
        Circumradius = circumradius;
        FirstVertexAngle = NormalizeAngle(firstVertexAngle);
    }

    public CadPoint3D VertexAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            index,
            SideCount);
        double angle = FirstVertexAngle + (index * StepAngle);
        return new CadPoint3D(
            Center.X + (Circumradius * Math.Cos(angle)),
            Center.Y + (Circumradius * Math.Sin(angle)),
            Center.Z);
    }

    /// <summary>
    /// Materializes the exact closed zero-bulge LWPOLYLINE only at commit.
    /// </summary>
    public CadPolylineAuthoringSnapshot CreatePolylineSnapshot()
    {
        var points = new CadPoint3D[SideCount];
        var bulges = new double[SideCount];
        for (int index = 0; index < SideCount; index++)
        {
            points[index] = VertexAt(index);
        }
        return new CadPolylineAuthoringSnapshot(
            points,
            bulges,
            isClosed: true);
    }

    private static double NormalizeAngle(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>Bounded host-neutral state for one regular POLYGON command.</summary>
/// <remarks>
/// Prompt storage and solving are O(1). Vertex materialization is deferred to
/// the final document command and is O(N) for the bounded side count N.
/// </remarks>
public sealed class CadPolygonAuthoringSession
{
    private readonly int _sideCount;
    private CadPoint3D _firstPoint;
    private bool _hasFirstPoint;

    public CadPolygonAuthoringMode Mode { get; }

    public int SideCount => _sideCount;

    public CadPolygonAuthoringInputKind InputKind => !_hasFirstPoint
        ? Mode == CadPolygonAuthoringMode.Edge
            ? CadPolygonAuthoringInputKind.FirstEdgePoint
            : CadPolygonAuthoringInputKind.CenterPoint
        : Mode == CadPolygonAuthoringMode.Edge
            ? CadPolygonAuthoringInputKind.SecondEdgePoint
            : CadPolygonAuthoringInputKind.RadiusPoint;

    public int AcceptedInputCount => _hasFirstPoint ? 1 : 0;

    public int PointCount => AcceptedInputCount;

    public CadPoint3D? FirstPoint => _hasFirstPoint ? _firstPoint : null;

    public CadPoint3D? CurrentPoint => FirstPoint;

    public CadPoint3D? AcquisitionBasePoint => FirstPoint;

    public bool AcceptsScalarInput =>
        _hasFirstPoint && Mode != CadPolygonAuthoringMode.Edge;

    public CadPolygonAuthoringSession(
        int sideCount,
        CadPolygonAuthoringMode mode)
    {
        _ = new CadPolygonSideCount(sideCount);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        _sideCount = sideCount;
        Mode = mode;
    }

    public bool CanAcceptPoint(CadPoint3D point) =>
        TryProcessPoint(
            point,
            acceptFirst: false,
            out _,
            out _,
            out _);

    public bool TryPreviewPoint(
        CadPoint3D point,
        out CadPolygonAuthoringSnapshot snapshot)
    {
        bool accepted = TryProcessPoint(
            point,
            acceptFirst: false,
            out snapshot,
            out bool completed,
            out _);
        return accepted && completed;
    }

    /// <summary>
    /// Accepts a point. The final point solves without mutating prompt state so
    /// publication preflight remains recoverable.
    /// </summary>
    public bool TryAcceptPoint(
        CadPoint3D point,
        out CadPolygonAuthoringSnapshot snapshot,
        out bool completed,
        out string? errorMessage) =>
        TryProcessPoint(
            point,
            acceptFirst: true,
            out snapshot,
            out completed,
            out errorMessage);

    public bool CanAcceptRadius(
        double radius,
        CadPoint3D bottomDirection) =>
        TryCreateFromRadius(
            radius,
            bottomDirection,
            out _,
            out _);

    /// <summary>
    /// Uses a positive numeric radius and the negative current snap-Y direction
    /// so the resulting bottom edge follows the current snap rotation.
    /// </summary>
    public bool TryCreateFromRadius(
        double radius,
        CadPoint3D bottomDirection,
        out CadPolygonAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        snapshot = default;
        if (!AcceptsScalarInput)
        {
            errorMessage = "The current POLYGON prompt does not accept a numeric radius.";
            return false;
        }
        if (!double.IsFinite(radius) || radius <= 0.0)
        {
            errorMessage = "A POLYGON radius must be finite and positive.";
            return false;
        }
        if (!TryGetUnit2D(bottomDirection, out CadPoint3D unit))
        {
            errorMessage = "The current snap rotation does not define a finite plan direction.";
            return false;
        }

        double halfStep = Math.PI / _sideCount;
        double circumradius = Mode == CadPolygonAuthoringMode.Inscribed
            ? radius
            : radius / Math.Cos(halfStep);
        double midpointAngle = Math.Atan2(unit.Y, unit.X);
        if (!TryCreateSnapshot(
                _firstPoint,
                circumradius,
                midpointAngle - halfStep,
                out snapshot))
        {
            errorMessage =
                "The numeric radius does not define finite renderable POLYGON vertices.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private bool TryProcessPoint(
        CadPoint3D point,
        bool acceptFirst,
        out CadPolygonAuthoringSnapshot snapshot,
        out bool completed,
        out string? errorMessage)
    {
        snapshot = default;
        completed = false;
        if (!IsFinite(point))
        {
            errorMessage = "A POLYGON point must contain finite WCS coordinates.";
            return false;
        }
        if (!_hasFirstPoint)
        {
            if (acceptFirst)
            {
                _firstPoint = point;
                _hasFirstPoint = true;
            }
            errorMessage = null;
            return true;
        }
        if (point.Z != _firstPoint.Z)
        {
            errorMessage =
                "A plan-view POLYGON point must remain on the first point's WCS-Z plane.";
            return false;
        }

        bool solved = Mode == CadPolygonAuthoringMode.Edge
            ? TryCreateFromEdge(point, out snapshot)
            : TryCreateFromRadiusPoint(point, out snapshot);
        if (!solved)
        {
            errorMessage = Mode == CadPolygonAuthoringMode.Edge
                ? "The first POLYGON edge must have finite nonzero renderable length."
                : "The POLYGON radius point must differ from its center and define finite renderable vertices.";
            return false;
        }

        completed = true;
        errorMessage = null;
        return true;
    }

    private bool TryCreateFromRadiusPoint(
        CadPoint3D point,
        out CadPolygonAuthoringSnapshot snapshot)
    {
        snapshot = default;
        CadPoint3D delta = new(
            point.X - _firstPoint.X,
            point.Y - _firstPoint.Y,
            0.0);
        double radius = Hypot(delta.X, delta.Y);
        if (!double.IsFinite(radius) || radius <= 0.0)
        {
            return false;
        }

        double directionAngle = Math.Atan2(delta.Y, delta.X);
        double halfStep = Math.PI / _sideCount;
        double circumradius;
        double firstAngle;
        if (Mode == CadPolygonAuthoringMode.Inscribed)
        {
            circumradius = radius;
            firstAngle = directionAngle;
        }
        else
        {
            circumradius = radius / Math.Cos(halfStep);
            firstAngle = directionAngle - halfStep;
        }
        return TryCreateSnapshot(
            _firstPoint,
            circumradius,
            firstAngle,
            out snapshot);
    }

    private bool TryCreateFromEdge(
        CadPoint3D second,
        out CadPolygonAuthoringSnapshot snapshot)
    {
        snapshot = default;
        CadPoint3D edge = new(
            second.X - _firstPoint.X,
            second.Y - _firstPoint.Y,
            0.0);
        double edgeLength = Hypot(edge.X, edge.Y);
        if (!double.IsFinite(edgeLength) || edgeLength <= 0.0)
        {
            return false;
        }

        double halfStep = Math.PI / _sideCount;
        double circumradius = edgeLength / (2.0 * Math.Sin(halfStep));
        double apothem = edgeLength / (2.0 * Math.Tan(halfStep));
        double unitX = edge.X / edgeLength;
        double unitY = edge.Y / edgeLength;
        CadPoint3D center = new(
            (_firstPoint.X * 0.5) + (second.X * 0.5) - (unitY * apothem),
            (_firstPoint.Y * 0.5) + (second.Y * 0.5) + (unitX * apothem),
            _firstPoint.Z);
        double firstAngle = Math.Atan2(
            _firstPoint.Y - center.Y,
            _firstPoint.X - center.X);
        return TryCreateSnapshot(
            center,
            circumradius,
            firstAngle,
            out snapshot);
    }

    private bool TryCreateSnapshot(
        CadPoint3D center,
        double circumradius,
        double firstAngle,
        out CadPolygonAuthoringSnapshot snapshot)
    {
        snapshot = default;
        try
        {
            snapshot = new CadPolygonAuthoringSnapshot(
                Mode,
                _sideCount,
                center,
                circumradius,
                firstAngle);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGetUnit2D(
        CadPoint3D vector,
        out CadPoint3D unit)
    {
        unit = default;
        double scale = Math.Max(Math.Abs(vector.X), Math.Abs(vector.Y));
        if (!double.IsFinite(scale) || scale <= 0.0)
        {
            return false;
        }
        double x = vector.X / scale;
        double y = vector.Y / scale;
        double length = Math.Sqrt((x * x) + (y * y));
        if (!double.IsFinite(length) || length <= 0.0)
        {
            return false;
        }
        unit = new CadPoint3D(x / length, y / length, 0.0);
        return IsFinite(unit);
    }

    private static double Hypot(double x, double y)
    {
        double scale = Math.Max(Math.Abs(x), Math.Abs(y));
        return scale == 0.0
            ? 0.0
            : scale * Math.Sqrt(
                ((x / scale) * (x / scale)) +
                ((y / scale) * (y / scale)));
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace ProGPU.CAD;

/// <summary>The exact point construction used by one plan-view CIRCLE command.</summary>
public enum CadCircleAuthoringMode : byte
{
    CenterRadius = 0,
    CenterDiameter = 1,
    TwoPoint = 2,
    ThreePoint = 3,
}

/// <summary>Immutable analytic input for one plan-view circle.</summary>
public readonly record struct CadCircleAuthoringSnapshot
{
    public CadPoint3D Center { get; }

    public double Radius { get; }

    public CadCircleAuthoringSnapshot(CadPoint3D center, double radius)
    {
        if (!IsFinite(center))
        {
            throw new ArgumentException(
                "A CIRCLE center must contain finite WCS coordinates.",
                nameof(center));
        }
        if (!double.IsFinite(radius) || radius <= 0.0 || radius > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radius),
                radius,
                $"A CIRCLE radius must be finite, positive, and no greater than {float.MaxValue:R} for retained rendering.");
        }

        Center = center;
        Radius = radius;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>Bounded host-neutral point state for one plan-view CIRCLE.</summary>
/// <remarks>
/// At most two accepted points are retained. Intermediate acceptance and every
/// construction except the final three-point solve are O(1); storage is O(1).
/// The final point is resolved without mutating the session so a failed document
/// command remains recoverable at the same prompt.
/// </remarks>
public sealed class CadCircleAuthoringSession
{
    private readonly CadPoint3D[] _points = new CadPoint3D[2];
    private int _pointCount;

    public CadCircleAuthoringMode Mode { get; }

    public int PointCount => _pointCount;

    public int RequiredPointCount =>
        Mode == CadCircleAuthoringMode.ThreePoint ? 3 : 2;

    public bool HasFirstPoint => _pointCount > 0;

    public CadPoint3D? FirstPoint =>
        _pointCount == 0 ? null : _points[0];

    public CadPoint3D? CurrentPoint =>
        _pointCount == 0 ? null : _points[_pointCount - 1];

    public ReadOnlyMemory<CadPoint3D> Points =>
        _points.AsMemory(0, _pointCount);

    public CadCircleAuthoringSession(CadCircleAuthoringMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        Mode = mode;
    }

    /// <summary>Checks the next point, including final construction geometry.</summary>
    public bool CanAcceptPoint(CadPoint3D point)
    {
        if (_pointCount < RequiredPointCount - 1)
        {
            return ValidateNextPoint(point, out _);
        }
        return TryCreateSnapshot(point, out _, out _);
    }

    /// <summary>
    /// Accepts a non-final construction point. A two-point mode accepts only its
    /// first point; three-point mode accepts its first two points.
    /// </summary>
    public bool TryAcceptIntermediatePoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        if (_pointCount >= RequiredPointCount - 1)
        {
            errorMessage = "The CIRCLE command is awaiting its final point.";
            return false;
        }
        if (!ValidateNextPoint(point, out errorMessage))
        {
            return false;
        }

        _points[_pointCount++] = point;
        return true;
    }

    /// <summary>Resolves the final point into one analytic circle without mutation.</summary>
    public bool TryCreateSnapshot(
        CadPoint3D finalPoint,
        out CadCircleAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        snapshot = default;
        if (_pointCount != RequiredPointCount - 1)
        {
            errorMessage =
                $"CIRCLE {Mode} requires {RequiredPointCount - 1} accepted point(s) before its final point.";
            return false;
        }
        if (!ValidateNextPoint(finalPoint, out errorMessage))
        {
            return false;
        }

        CadPoint3D center;
        double radius;
        switch (Mode)
        {
            case CadCircleAuthoringMode.CenterRadius:
                center = _points[0];
                radius = Distance2D(_points[0], finalPoint);
                break;
            case CadCircleAuthoringMode.CenterDiameter:
                center = _points[0];
                radius = Distance2D(_points[0], finalPoint) * 0.5;
                break;
            case CadCircleAuthoringMode.TwoPoint:
                center = new CadPoint3D(
                    Midpoint(_points[0].X, finalPoint.X),
                    Midpoint(_points[0].Y, finalPoint.Y),
                    _points[0].Z);
                radius = Distance2D(_points[0], finalPoint) * 0.5;
                break;
            case CadCircleAuthoringMode.ThreePoint:
                if (!TryGetThreePointCircle(
                        _points[0],
                        _points[1],
                        finalPoint,
                        out center,
                        out radius))
                {
                    errorMessage =
                        "The three circumference points must define a finite non-collinear CIRCLE.";
                    return false;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        try
        {
            snapshot = new CadCircleAuthoringSnapshot(center, radius);
            errorMessage = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Resolves AutoCAD's numeric radius or diameter value without requiring a
    /// cursor direction. Point-defined 2P and 3P modes reject scalar input.
    /// </summary>
    public bool TryCreateSnapshotFromScalar(
        double value,
        out CadCircleAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        snapshot = default;
        if (_pointCount != 1 ||
            Mode is not (CadCircleAuthoringMode.CenterRadius or
                CadCircleAuthoringMode.CenterDiameter))
        {
            errorMessage =
                "A numeric CIRCLE value requires an accepted center and center/radius or center/diameter mode.";
            return false;
        }
        if (!double.IsFinite(value) || value <= 0.0)
        {
            errorMessage = "A CIRCLE radius or diameter value must be finite and positive.";
            return false;
        }

        double radius = Mode == CadCircleAuthoringMode.CenterRadius
            ? value
            : value * 0.5;
        try
        {
            snapshot = new CadCircleAuthoringSnapshot(_points[0], radius);
            errorMessage = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private bool ValidateNextPoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!IsFinite(point))
        {
            errorMessage = "A CIRCLE point must contain finite WCS coordinates.";
            return false;
        }
        if (_pointCount > 0 && point.Z != _points[0].Z)
        {
            errorMessage =
                "A plan-view CIRCLE point must remain on the first point's WCS-Z plane.";
            return false;
        }
        for (int i = 0; i < _pointCount; i++)
        {
            if (point == _points[i])
            {
                errorMessage = "CIRCLE construction points must be distinct.";
                return false;
            }
        }
        return true;
    }

    private static bool TryGetThreePointCircle(
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third,
        out CadPoint3D center,
        out double radius)
    {
        center = default;
        radius = 0.0;
        double secondX = second.X - first.X;
        double secondY = second.Y - first.Y;
        double thirdX = third.X - first.X;
        double thirdY = third.Y - first.Y;
        double scale = Math.Max(
            Math.Max(Math.Abs(secondX), Math.Abs(secondY)),
            Math.Max(Math.Abs(thirdX), Math.Abs(thirdY)));
        if (!double.IsFinite(scale) || scale <= 0.0)
        {
            return false;
        }

        double x2 = secondX / scale;
        double y2 = secondY / scale;
        double x3 = thirdX / scale;
        double y3 = thirdY / scale;
        double determinant = 2.0 * ((x2 * y3) - (y2 * x3));
        if (!double.IsFinite(determinant) || determinant == 0.0)
        {
            return false;
        }

        double length2 = (x2 * x2) + (y2 * y2);
        double length3 = (x3 * x3) + (y3 * y3);
        double normalizedCenterX = ((y3 * length2) - (y2 * length3)) /
            determinant;
        double normalizedCenterY = ((x2 * length3) - (x3 * length2)) /
            determinant;
        double offsetX = scale * normalizedCenterX;
        double offsetY = scale * normalizedCenterY;
        center = new CadPoint3D(
            first.X + offsetX,
            first.Y + offsetY,
            first.Z);
        radius = Hypot(offsetX, offsetY);
        return IsFinite(center) &&
            double.IsFinite(radius) && radius > 0.0;
    }

    private static double Midpoint(double first, double second) =>
        (first * 0.5) + (second * 0.5);

    private static double Distance2D(CadPoint3D first, CadPoint3D second) =>
        Hypot(second.X - first.X, second.Y - first.Y);

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

/// <summary>Adds one analytic circle as one reversible history operation.</summary>
/// <remarks>
/// Current CLAYER, CECOLOR, CELTYPE, CELTSCALE, CELWEIGHT, and THICKNESS are
/// captured atomically on first Apply. Nonzero THICKNESS fails before mutation
/// until retained extrusion geometry is available. Apply/Undo/Redo are O(1).
/// </remarks>
public sealed class CadAddCircleCommand : CadEditCommand
{
    private readonly CadCircleAuthoringSnapshot _snapshot;
    private Circle? _circle;

    public CadCircleAuthoringSnapshot Snapshot => _snapshot;

    public Circle? Circle => _circle;

    public ulong CurrentHandle => _circle?.Handle ?? 0;

    public CadAddCircleCommand(
        CadCircleAuthoringSnapshot snapshot,
        string description = "CIRCLE")
        : base(description)
    {
        _snapshot = snapshot;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Circle circle;
        if (isRedo)
        {
            circle = _circle ?? throw new InvalidOperationException(
                "The CIRCLE command has not been applied.");
        }
        else
        {
            circle = CreateCircle(document);
            _circle = circle;
        }

        ValidateDetached(circle);
        document.Entities.Add(circle);
    }

    internal override void Revert(CadDocument document)
    {
        Circle circle = _circle ?? throw new InvalidOperationException(
            "The CIRCLE command has not been applied.");
        ValidateModelSpaceEntity(document, circle);
        if (!document.Entities.Remove(circle))
        {
            throw new InvalidOperationException(
                "The authored CIRCLE could not be removed from model space.");
        }
    }

    private Circle CreateCircle(CadDocument document)
    {
        Layer layer = document.Header.CurrentLayer;
        if (HasLayerFlag(layer, LayerFlags.Locked))
        {
            throw new InvalidOperationException(
                $"Current layer '{layer.Name}' is locked and cannot receive a CIRCLE entity.");
        }

        double thickness = document.Header.ThicknessDefault;
        if (!double.IsFinite(thickness))
        {
            throw new InvalidOperationException(
                "Current THICKNESS must be finite before creating a CIRCLE.");
        }
        if (thickness != 0.0)
        {
            throw new CadUnsupportedEntityException(
                "Nonzero THICKNESS requires retained circle-extrusion geometry and is not authored as a planar outline.");
        }

        double lineTypeScale = document.Header.CurrentEntityLinetypeScale;
        if (!double.IsFinite(lineTypeScale) || lineTypeScale <= 0.0)
        {
            throw new InvalidOperationException(
                "Current CELTSCALE must be finite and positive before creating a CIRCLE.");
        }

        return new Circle(
            new XYZ(
                _snapshot.Center.X,
                _snapshot.Center.Y,
                _snapshot.Center.Z),
            _snapshot.Radius)
        {
            Normal = XYZ.AxisZ,
            Thickness = thickness,
            Layer = layer,
            Color = document.Header.CurrentEntityColor,
            LineType = document.Header.CurrentLineType,
            LineTypeScale = lineTypeScale,
            LineWeight = document.Header.CurrentEntityLineWeight,
        };
    }

    private static void ValidateDetached(Circle circle)
    {
        if (circle.Owner is not null ||
            circle.Document is not null ||
            circle.Handle != 0)
        {
            throw new InvalidOperationException(
                "The retained CIRCLE entity is not detached and cannot be added.");
        }
    }
}

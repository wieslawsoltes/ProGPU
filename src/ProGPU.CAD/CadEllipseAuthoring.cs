using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using System.Globalization;

namespace ProGPU.CAD;

/// <summary>The first-axis and eccentricity construction for ELLIPSE.</summary>
public enum CadEllipseAuthoringMode : byte
{
    AxisEndpointsDistance = 0,
    AxisEndpointsRotation = 1,
    CenterDistance = 2,
    CenterRotation = 3,
}

/// <summary>The endpoint interpretation for a full ellipse or elliptical arc.</summary>
public enum CadEllipseArcInputMode : byte
{
    Full = 0,
    Angle = 1,
    Parameter = 2,
    IncludedAngle = 3,
}

/// <summary>The exact input expected by the current ELLIPSE prompt.</summary>
public enum CadEllipseAuthoringInputKind : byte
{
    FirstAxisPoint = 0,
    SecondAxisPoint = 1,
    OtherAxisPoint = 2,
    RotationRadians = 3,
    StartDirection = 4,
    StartParameterRadians = 5,
    EndDirection = 6,
    EndParameterRadians = 7,
    IncludedAngleRadians = 8,
}

/// <summary>A bounded invariant finite scalar used by an ELLIPSE prompt.</summary>
public readonly record struct CadEllipseScalarInput
{
    public const int MaximumCodeUnits = 128;

    public double Value { get; }

    private CadEllipseScalarInput(double value)
    {
        Value = value;
    }

    public static bool TryParse(string? text, out CadEllipseScalarInput input)
    {
        input = default;
        return text is not null && TryParse(text.AsSpan(), out input);
    }

    public static bool TryParse(
        ReadOnlySpan<char> text,
        out CadEllipseScalarInput input)
    {
        input = default;
        text = text.Trim();
        if (text.IsEmpty ||
            text.Length > MaximumCodeUnits ||
            !double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value) ||
            !double.IsFinite(value))
        {
            return false;
        }

        input = new CadEllipseScalarInput(value);
        return true;
    }
}

/// <summary>Immutable analytic input for one Axis-Z plan-view ellipse.</summary>
public readonly record struct CadEllipseAuthoringSnapshot
{
    private const double TwoPi = Math.PI * 2.0;

    public CadPoint3D Center { get; }

    /// <summary>Center-relative WCS endpoint of the canonical major axis.</summary>
    public CadPoint3D MajorAxisEndPoint { get; }

    public CadPoint3D MinorAxisEndPoint { get; }

    public double MajorRadius => Hypot(
        MajorAxisEndPoint.X,
        MajorAxisEndPoint.Y);

    public double MinorRadius => MajorRadius * RadiusRatio;

    public double RadiusRatio { get; }

    public double StartParameter { get; }

    public double SweepParameter { get; }

    public double EndParameter { get; }

    public bool IsFullEllipse => SweepParameter == TwoPi;

    public CadPoint3D StartPoint => PointAt(StartParameter);

    public CadPoint3D EndPoint => PointAt(StartParameter + SweepParameter);

    public CadEllipseAuthoringSnapshot(
        CadPoint3D center,
        CadPoint3D majorAxisEndPoint,
        double radiusRatio,
        double startParameter = 0.0,
        double sweepParameter = TwoPi)
    {
        if (!IsFinite(center))
        {
            throw new ArgumentException(
                "An ELLIPSE center must contain finite WCS coordinates.",
                nameof(center));
        }
        if (!IsFinite(majorAxisEndPoint) || majorAxisEndPoint.Z != 0.0)
        {
            throw new ArgumentException(
                "An ELLIPSE major axis must be a finite plan vector.",
                nameof(majorAxisEndPoint));
        }

        double majorLength = Hypot(
            majorAxisEndPoint.X,
            majorAxisEndPoint.Y);
        double minorLength = majorLength * radiusRatio;
        if (!double.IsFinite(majorLength) ||
            majorLength <= 0.0 ||
            majorLength > float.MaxValue ||
            !double.IsFinite(radiusRatio) ||
            radiusRatio <= 0.0 ||
            radiusRatio > 1.0 ||
            !double.IsFinite(minorLength) ||
            minorLength <= 0.0 ||
            minorLength > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radiusRatio),
                radiusRatio,
                "ELLIPSE axes must be finite, positive, canonically ordered, and renderable as retained float vectors.");
        }
        if (!double.IsFinite(startParameter))
        {
            throw new ArgumentOutOfRangeException(
                nameof(startParameter),
                "An ELLIPSE start parameter must be finite.");
        }
        if (!double.IsFinite(sweepParameter) ||
            sweepParameter <= 0.0 ||
            sweepParameter > TwoPi)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sweepParameter),
                sweepParameter,
                "An ELLIPSE sweep must be finite, positive, and no greater than one turn.");
        }

        double normalizedStart = sweepParameter == TwoPi
            ? 0.0
            : NormalizeAngle(startParameter);
        double normalizedEnd = sweepParameter == TwoPi
            ? TwoPi
            : NormalizeAngle(normalizedStart + sweepParameter);
        if (sweepParameter != TwoPi && normalizedStart == normalizedEnd)
        {
            throw new ArgumentException(
                "The elliptical-arc endpoints must remain numerically distinct.",
                nameof(sweepParameter));
        }

        Center = center;
        MajorAxisEndPoint = majorAxisEndPoint;
        MinorAxisEndPoint = new CadPoint3D(
            -majorAxisEndPoint.Y * radiusRatio,
            majorAxisEndPoint.X * radiusRatio,
            0.0);
        RadiusRatio = radiusRatio;
        StartParameter = normalizedStart;
        SweepParameter = sweepParameter;
        EndParameter = normalizedEnd;
    }

    public CadPoint3D PointAt(double parameter) => new(
        Center.X +
            (MajorAxisEndPoint.X * Math.Cos(parameter)) +
            (MinorAxisEndPoint.X * Math.Sin(parameter)),
        Center.Y +
            (MajorAxisEndPoint.Y * Math.Cos(parameter)) +
            (MinorAxisEndPoint.Y * Math.Sin(parameter)),
        Center.Z);

    private static double NormalizeAngle(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
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

/// <summary>Bounded host-neutral state for one plan-view ELLIPSE command.</summary>
/// <remarks>
/// Storage is fixed and every solve is O(1). A final input produces a snapshot
/// without advancing the session so failed document preflight is recoverable.
/// </remarks>
public sealed class CadEllipseAuthoringSession
{
    private const double TwoPi = Math.PI * 2.0;
    private const double MinimumRotationRatio = 0.010471784116245792;
    private readonly CadPoint3D[] _acceptedPoints = new CadPoint3D[4];
    private int _acceptedPointCount;
    private int _stage;
    private CadEllipseAuthoringSnapshot _axes;
    private double _startParameter;
    private CadPoint3D _startDirection;

    public CadEllipseAuthoringMode Mode { get; }

    public CadEllipseArcInputMode ArcInputMode { get; }

    public CadEllipseAuthoringInputKind InputKind => GetInputKind(_stage);

    public int AcceptedInputCount => _stage;

    public int PointCount => _acceptedPointCount;

    public ReadOnlyMemory<CadPoint3D> Points =>
        _acceptedPoints.AsMemory(0, _acceptedPointCount);

    public CadPoint3D? FirstPoint =>
        _acceptedPointCount == 0 ? null : _acceptedPoints[0];

    public CadPoint3D? CurrentPoint =>
        _acceptedPointCount == 0
            ? null
            : _acceptedPoints[_acceptedPointCount - 1];

    /// <summary>
    /// Base used by shared point constraints. Once axes exist, angular inputs
    /// are acquired from the ellipse center rather than the last prompt point.
    /// </summary>
    public CadPoint3D? AcquisitionBasePoint
    {
        get
        {
            if (_stage >= 3)
            {
                return _axes.Center;
            }
            if (_stage == 2 && TryGetFirstAxis(
                    _acceptedPoints[0],
                    _acceptedPoints[1],
                    out CadPoint3D center,
                    out _))
            {
                return center;
            }
            return CurrentPoint;
        }
    }

    public bool IsFinalInput => ArcInputMode == CadEllipseArcInputMode.Full
        ? _stage == 2
        : _stage == 4;

    public bool AcceptsPointInput => InputKind is
        CadEllipseAuthoringInputKind.FirstAxisPoint or
        CadEllipseAuthoringInputKind.SecondAxisPoint or
        CadEllipseAuthoringInputKind.OtherAxisPoint or
        CadEllipseAuthoringInputKind.StartDirection or
        CadEllipseAuthoringInputKind.EndDirection;

    public bool AcceptsScalarInput => InputKind is
        CadEllipseAuthoringInputKind.RotationRadians or
        CadEllipseAuthoringInputKind.StartDirection or
        CadEllipseAuthoringInputKind.StartParameterRadians or
        CadEllipseAuthoringInputKind.EndDirection or
        CadEllipseAuthoringInputKind.EndParameterRadians or
        CadEllipseAuthoringInputKind.IncludedAngleRadians;

    public CadEllipseAuthoringSession(
        CadEllipseAuthoringMode mode,
        CadEllipseArcInputMode arcInputMode = CadEllipseArcInputMode.Full)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!Enum.IsDefined(arcInputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(arcInputMode));
        }

        Mode = mode;
        ArcInputMode = arcInputMode;
    }

    public bool CanAcceptPoint(CadPoint3D point) =>
        TryProcessPoint(
            point,
            acceptIntermediate: false,
            out _,
            out _,
            out _);

    public bool CanAcceptScalar(double value) =>
        TryProcessScalar(
            value,
            acceptIntermediate: false,
            out _,
            out _,
            out _);

    public bool TryPreviewScalar(
        double value,
        out CadEllipseAuthoringSnapshot snapshot,
        out bool completed) =>
        TryProcessScalar(
            value,
            acceptIntermediate: false,
            out snapshot,
            out completed,
            out _);

    public bool TryPreviewDirection(
        CadPoint3D direction,
        out CadEllipseAuthoringSnapshot snapshot,
        out bool completed)
    {
        snapshot = default;
        completed = false;
        return InputKind is (
                CadEllipseAuthoringInputKind.StartDirection or
                CadEllipseAuthoringInputKind.EndDirection) &&
            TryGetUnit2D(direction, out CadPoint3D unit) &&
            TryProcessDirection(
                unit,
                acceptIntermediate: false,
                out snapshot,
                out completed,
                out _);
    }

    /// <summary>
    /// Accepts an input point. Intermediate state advances only after a valid
    /// solve; a final snapshot leaves the session unchanged.
    /// </summary>
    public bool TryAcceptPoint(
        CadPoint3D point,
        out CadEllipseAuthoringSnapshot snapshot,
        out bool completed,
        out string? errorMessage) =>
        TryProcessPoint(
            point,
            acceptIntermediate: true,
            out snapshot,
            out completed,
            out errorMessage);

    /// <summary>
    /// Accepts radians for Rotation, an absolute WCS direction, a parameter,
    /// or an included angle according to <see cref="InputKind"/>.
    /// </summary>
    public bool TryAcceptScalar(
        double value,
        out CadEllipseAuthoringSnapshot snapshot,
        out bool completed,
        out string? errorMessage) =>
        TryProcessScalar(
            value,
            acceptIntermediate: true,
            out snapshot,
            out completed,
            out errorMessage);

    /// <summary>
    /// Accepts an explicit WCS direction without adding a unit point to a
    /// potentially large WCS origin.
    /// </summary>
    public bool TryAcceptDirection(
        CadPoint3D direction,
        out CadEllipseAuthoringSnapshot snapshot,
        out bool completed,
        out string? errorMessage)
    {
        snapshot = default;
        completed = false;
        if (InputKind is not (
                CadEllipseAuthoringInputKind.StartDirection or
                CadEllipseAuthoringInputKind.EndDirection))
        {
            errorMessage = "The current ELLIPSE prompt does not accept a direction.";
            return false;
        }
        if (!TryGetUnit2D(direction, out CadPoint3D unit))
        {
            errorMessage = "An ELLIPSE direction must be finite and nonzero in the plan.";
            return false;
        }

        return TryProcessDirection(
            unit,
            acceptIntermediate: true,
            out snapshot,
            out completed,
            out errorMessage);
    }

    /// <summary>Produces the analytic pointer preview available at this stage.</summary>
    public bool TryPreviewPoint(
        CadPoint3D point,
        out CadEllipseAuthoringSnapshot snapshot)
    {
        snapshot = default;
        if (!AcceptsPointInput || !ValidatePlanPoint(point, out _))
        {
            return false;
        }

        return _stage switch
        {
            2 when UsesDistance =>
                TryBuildAxesFromPoint(point, out snapshot),
            3 => TryGetAxesPreview(out snapshot),
            4 when ArcInputMode == CadEllipseArcInputMode.Angle =>
                TryCreateDirectionArcFromPoint(point, out snapshot),
            _ => false,
        };
    }

    public bool TryGetAxesSnapshot(
        out CadEllipseAuthoringSnapshot snapshot) =>
        TryGetAxesPreview(out snapshot);

    private bool TryProcessPoint(
        CadPoint3D point,
        bool acceptIntermediate,
        out CadEllipseAuthoringSnapshot snapshot,
        out bool completed,
        out string? errorMessage)
    {
        snapshot = default;
        completed = false;
        if (!AcceptsPointInput)
        {
            errorMessage = $"ELLIPSE {InputKind} requires a scalar value.";
            return false;
        }
        if (!ValidatePlanPoint(point, out errorMessage))
        {
            return false;
        }

        switch (_stage)
        {
            case 0:
                if (acceptIntermediate)
                {
                    StorePoint(point);
                    _stage = 1;
                }
                errorMessage = null;
                return true;
            case 1:
                if (point == _acceptedPoints[0] ||
                    !TryGetFirstAxis(
                        _acceptedPoints[0],
                        point,
                        out _,
                        out _))
                {
                    errorMessage = "The first ELLIPSE axis must have finite nonzero length.";
                    return false;
                }
                if (acceptIntermediate)
                {
                    StorePoint(point);
                    _stage = 2;
                }
                errorMessage = null;
                return true;
            case 2:
                if (!TryBuildAxesFromPoint(point, out snapshot))
                {
                    errorMessage =
                        "The other-axis point must define finite positive renderable ELLIPSE axes.";
                    return false;
                }
                if (ArcInputMode == CadEllipseArcInputMode.Full)
                {
                    completed = true;
                    errorMessage = null;
                    return true;
                }
                if (acceptIntermediate)
                {
                    _axes = snapshot;
                    StorePoint(point);
                    _stage = 3;
                }
                snapshot = default;
                errorMessage = null;
                return true;
            case 3:
                if (!TryGetDirectionFromPoint(point, out CadPoint3D startDirection) ||
                    !TryGetParameter(startDirection, out double startParameter))
                {
                    errorMessage =
                        "The elliptical-arc start direction must be finite and differ from the center.";
                    return false;
                }
                if (acceptIntermediate)
                {
                    _startDirection = startDirection;
                    _startParameter = startParameter;
                    StorePoint(point);
                    _stage = 4;
                }
                errorMessage = null;
                return true;
            case 4:
                if (!TryCreateDirectionArcFromPoint(point, out snapshot))
                {
                    errorMessage =
                        "The elliptical-arc end direction must define a finite non-full interval distinct from the start.";
                    return false;
                }
                completed = true;
                errorMessage = null;
                return true;
            default:
                errorMessage = "The ELLIPSE session is in an invalid point-input state.";
                return false;
        }
    }

    private bool TryProcessScalar(
        double value,
        bool acceptIntermediate,
        out CadEllipseAuthoringSnapshot snapshot,
        out bool completed,
        out string? errorMessage)
    {
        snapshot = default;
        completed = false;
        if (!AcceptsScalarInput)
        {
            errorMessage = $"ELLIPSE {InputKind} requires a point.";
            return false;
        }
        if (!double.IsFinite(value))
        {
            errorMessage = "An ELLIPSE scalar value must be finite.";
            return false;
        }

        if (_stage == 2)
        {
            if (!TryBuildAxesFromRotation(value, out snapshot))
            {
                errorMessage =
                    "The Rotation value must define a finite renderable ratio outside the documented edge-on interval.";
                return false;
            }
            if (ArcInputMode == CadEllipseArcInputMode.Full)
            {
                completed = true;
                errorMessage = null;
                return true;
            }
            if (acceptIntermediate)
            {
                _axes = snapshot;
                _stage = 3;
            }
            snapshot = default;
            errorMessage = null;
            return true;
        }

        if (InputKind is
            CadEllipseAuthoringInputKind.StartDirection or
            CadEllipseAuthoringInputKind.EndDirection)
        {
            CadPoint3D direction = new(
                Math.Cos(value),
                Math.Sin(value),
                0.0);
            return TryProcessDirection(
                direction,
                acceptIntermediate,
                out snapshot,
                out completed,
                out errorMessage);
        }

        if (_stage == 3 &&
            InputKind == CadEllipseAuthoringInputKind.StartParameterRadians)
        {
            if (acceptIntermediate)
            {
                _startParameter = NormalizeAngle(value);
                _stage = 4;
            }
            errorMessage = null;
            return true;
        }

        if (_stage == 4 &&
            InputKind == CadEllipseAuthoringInputKind.EndParameterRadians)
        {
            if (!TryCreateArc(
                    _startParameter,
                    NormalizeAngle(value),
                    out snapshot))
            {
                errorMessage =
                    "The end parameter must define a finite non-full interval distinct from the start parameter.";
                return false;
            }
            completed = true;
            errorMessage = null;
            return true;
        }

        if (_stage == 4 &&
            InputKind == CadEllipseAuthoringInputKind.IncludedAngleRadians)
        {
            if (!TryCreateIncludedAngleArc(value, out snapshot))
            {
                errorMessage =
                    "The included angle must be finite, nonzero, less than one turn in magnitude, and define a renderable elliptical arc.";
                return false;
            }
            completed = true;
            errorMessage = null;
            return true;
        }

        errorMessage = "The ELLIPSE scalar does not match the current prompt.";
        return false;
    }

    private bool TryProcessDirection(
        CadPoint3D direction,
        bool acceptIntermediate,
        out CadEllipseAuthoringSnapshot snapshot,
        out bool completed,
        out string? errorMessage)
    {
        snapshot = default;
        completed = false;
        if (!TryGetParameter(direction, out double parameter))
        {
            errorMessage = "The ELLIPSE direction cannot be resolved against its axes.";
            return false;
        }

        if (_stage == 3)
        {
            if (acceptIntermediate)
            {
                _startDirection = direction;
                _startParameter = parameter;
                _stage = 4;
            }
            errorMessage = null;
            return true;
        }
        if (_stage == 4 && ArcInputMode == CadEllipseArcInputMode.Angle)
        {
            if (!TryCreateArc(_startParameter, parameter, out snapshot))
            {
                errorMessage =
                    "The end direction must define a finite non-full interval distinct from the start.";
                return false;
            }
            completed = true;
            errorMessage = null;
            return true;
        }

        errorMessage = "The current ELLIPSE prompt does not accept that direction.";
        return false;
    }

    private bool TryBuildAxesFromPoint(
        CadPoint3D otherAxisPoint,
        out CadEllipseAuthoringSnapshot snapshot)
    {
        snapshot = default;
        if (!TryGetFirstAxis(
                _acceptedPoints[0],
                _acceptedPoints[1],
                out CadPoint3D center,
                out CadPoint3D firstAxis))
        {
            return false;
        }

        double otherLength = Distance2D(center, otherAxisPoint);
        return TryCreateCanonicalAxes(
            center,
            firstAxis,
            otherLength,
            out snapshot);
    }

    private bool TryBuildAxesFromRotation(
        double rotation,
        out CadEllipseAuthoringSnapshot snapshot)
    {
        snapshot = default;
        if (!TryGetFirstAxis(
                _acceptedPoints[0],
                _acceptedPoints[1],
                out CadPoint3D center,
                out CadPoint3D firstAxis))
        {
            return false;
        }

        double ratio = Math.Abs(Math.Cos(rotation));
        if (!double.IsFinite(ratio) || ratio < MinimumRotationRatio)
        {
            return false;
        }

        try
        {
            snapshot = new CadEllipseAuthoringSnapshot(
                center,
                firstAxis,
                Math.Min(1.0, ratio));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool TryGetFirstAxis(
        CadPoint3D first,
        CadPoint3D second,
        out CadPoint3D center,
        out CadPoint3D firstAxis)
    {
        if (Mode is
            CadEllipseAuthoringMode.AxisEndpointsDistance or
            CadEllipseAuthoringMode.AxisEndpointsRotation)
        {
            center = new CadPoint3D(
                (first.X * 0.5) + (second.X * 0.5),
                (first.Y * 0.5) + (second.Y * 0.5),
                first.Z);
            firstAxis = new CadPoint3D(
                (second.X - first.X) * 0.5,
                (second.Y - first.Y) * 0.5,
                0.0);
        }
        else
        {
            center = first;
            firstAxis = new CadPoint3D(
                second.X - first.X,
                second.Y - first.Y,
                0.0);
        }

        return IsFinite(center) &&
            TryGetUnit2D(firstAxis, out _);
    }

    private static bool TryCreateCanonicalAxes(
        CadPoint3D center,
        CadPoint3D firstAxis,
        double otherLength,
        out CadEllipseAuthoringSnapshot snapshot)
    {
        snapshot = default;
        double firstLength = Hypot(firstAxis.X, firstAxis.Y);
        if (!double.IsFinite(firstLength) ||
            firstLength <= 0.0 ||
            !double.IsFinite(otherLength) ||
            otherLength <= 0.0)
        {
            return false;
        }

        CadPoint3D majorAxis;
        double ratio;
        if (firstLength >= otherLength)
        {
            majorAxis = firstAxis;
            ratio = otherLength / firstLength;
        }
        else
        {
            double scale = otherLength / firstLength;
            majorAxis = new CadPoint3D(
                -firstAxis.Y * scale,
                firstAxis.X * scale,
                0.0);
            ratio = firstLength / otherLength;
        }

        try
        {
            snapshot = new CadEllipseAuthoringSnapshot(
                center,
                majorAxis,
                ratio);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool TryGetDirectionFromPoint(
        CadPoint3D point,
        out CadPoint3D direction)
    {
        direction = new CadPoint3D(
            point.X - _axes.Center.X,
            point.Y - _axes.Center.Y,
            0.0);
        return TryGetUnit2D(direction, out direction);
    }

    private bool TryGetParameter(
        CadPoint3D direction,
        out double parameter)
    {
        parameter = 0.0;
        CadPoint3D major = _axes.MajorAxisEndPoint;
        CadPoint3D minor = _axes.MinorAxisEndPoint;
        double majorSquared =
            (major.X * major.X) + (major.Y * major.Y);
        double minorSquared =
            (minor.X * minor.X) + (minor.Y * minor.Y);
        if (!double.IsFinite(majorSquared) || majorSquared <= 0.0 ||
            !double.IsFinite(minorSquared) || minorSquared <= 0.0)
        {
            return false;
        }

        double x =
            ((direction.X * major.X) + (direction.Y * major.Y)) /
            majorSquared;
        double y =
            ((direction.X * minor.X) + (direction.Y * minor.Y)) /
            minorSquared;
        parameter = NormalizeAngle(Math.Atan2(y, x));
        return double.IsFinite(parameter);
    }

    private bool TryCreateDirectionArcFromPoint(
        CadPoint3D point,
        out CadEllipseAuthoringSnapshot snapshot)
    {
        snapshot = default;
        return TryGetDirectionFromPoint(point, out CadPoint3D direction) &&
            TryGetParameter(direction, out double endParameter) &&
            TryCreateArc(_startParameter, endParameter, out snapshot);
    }

    private bool TryCreateIncludedAngleArc(
        double signedAngle,
        out CadEllipseAuthoringSnapshot snapshot)
    {
        snapshot = default;
        double magnitude = Math.Abs(signedAngle);
        if (!double.IsFinite(magnitude) ||
            magnitude <= 0.0 ||
            magnitude >= TwoPi)
        {
            return false;
        }

        double cosine = Math.Cos(signedAngle);
        double sine = Math.Sin(signedAngle);
        CadPoint3D endDirection = new(
            (_startDirection.X * cosine) - (_startDirection.Y * sine),
            (_startDirection.X * sine) + (_startDirection.Y * cosine),
            0.0);
        if (!TryGetParameter(endDirection, out double endParameter))
        {
            return false;
        }

        return signedAngle > 0.0
            ? TryCreateArc(_startParameter, endParameter, out snapshot)
            : TryCreateArc(endParameter, _startParameter, out snapshot);
    }

    private bool TryCreateArc(
        double startParameter,
        double endParameter,
        out CadEllipseAuthoringSnapshot snapshot)
    {
        snapshot = default;
        double sweep = PositiveAngle(endParameter - startParameter);
        if (!double.IsFinite(sweep) || sweep <= 0.0 || sweep >= TwoPi)
        {
            return false;
        }

        try
        {
            snapshot = new CadEllipseAuthoringSnapshot(
                _axes.Center,
                _axes.MajorAxisEndPoint,
                _axes.RadiusRatio,
                startParameter,
                sweep);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool TryGetAxesPreview(out CadEllipseAuthoringSnapshot snapshot)
    {
        snapshot = _axes;
        return _stage >= 3;
    }

    private bool ValidatePlanPoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        if (!IsFinite(point))
        {
            errorMessage = "An ELLIPSE point must contain finite WCS coordinates.";
            return false;
        }
        if (_stage > 0 && point.Z != _acceptedPoints[0].Z)
        {
            errorMessage =
                "A plan-view ELLIPSE point must remain on the first point's WCS-Z plane.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private CadEllipseAuthoringInputKind GetInputKind(int stage) => stage switch
    {
        0 => CadEllipseAuthoringInputKind.FirstAxisPoint,
        1 => CadEllipseAuthoringInputKind.SecondAxisPoint,
        2 when UsesDistance => CadEllipseAuthoringInputKind.OtherAxisPoint,
        2 => CadEllipseAuthoringInputKind.RotationRadians,
        3 when ArcInputMode == CadEllipseArcInputMode.Parameter =>
            CadEllipseAuthoringInputKind.StartParameterRadians,
        3 => CadEllipseAuthoringInputKind.StartDirection,
        4 when ArcInputMode == CadEllipseArcInputMode.Parameter =>
            CadEllipseAuthoringInputKind.EndParameterRadians,
        4 when ArcInputMode == CadEllipseArcInputMode.IncludedAngle =>
            CadEllipseAuthoringInputKind.IncludedAngleRadians,
        4 => CadEllipseAuthoringInputKind.EndDirection,
        _ => throw new InvalidOperationException(
            "The ELLIPSE authoring stage is outside its bounded state machine."),
    };

    private bool UsesDistance => Mode is
        CadEllipseAuthoringMode.AxisEndpointsDistance or
        CadEllipseAuthoringMode.CenterDistance;

    private void StorePoint(CadPoint3D point)
    {
        if (_acceptedPointCount >= _acceptedPoints.Length)
        {
            throw new InvalidOperationException(
                "The ELLIPSE point state exceeded its fixed capacity.");
        }
        _acceptedPoints[_acceptedPointCount++] = point;
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

    private static double NormalizeAngle(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }

    private static double PositiveAngle(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>Adds one analytic ELLIPSE as one reversible history operation.</summary>
/// <remarks>
/// Current CLAYER, CECOLOR, CELTYPE, CELTSCALE, CELWEIGHT, and THICKNESS are
/// captured atomically on first Apply. Nonzero THICKNESS fails before mutation.
/// Apply/Undo/Redo are O(1).
/// </remarks>
public sealed class CadAddEllipseCommand : CadEditCommand
{
    private readonly CadEllipseAuthoringSnapshot _snapshot;
    private Ellipse? _ellipse;

    public CadEllipseAuthoringSnapshot Snapshot => _snapshot;

    public Ellipse? Ellipse => _ellipse;

    public ulong CurrentHandle => _ellipse?.Handle ?? 0;

    public CadAddEllipseCommand(
        CadEllipseAuthoringSnapshot snapshot,
        string description = "ELLIPSE")
        : base(description)
    {
        _snapshot = snapshot;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Ellipse ellipse;
        if (isRedo)
        {
            ellipse = _ellipse ?? throw new InvalidOperationException(
                "The ELLIPSE command has not been applied.");
        }
        else
        {
            ellipse = CreateEllipse(document);
            _ellipse = ellipse;
        }

        ValidateDetached(ellipse);
        document.Entities.Add(ellipse);
    }

    internal override void Revert(CadDocument document)
    {
        Ellipse ellipse = _ellipse ?? throw new InvalidOperationException(
            "The ELLIPSE command has not been applied.");
        ValidateModelSpaceEntity(document, ellipse);
        if (!document.Entities.Remove(ellipse))
        {
            throw new InvalidOperationException(
                "The authored ELLIPSE could not be removed from model space.");
        }
    }

    private Ellipse CreateEllipse(CadDocument document)
    {
        Layer layer = document.Header.CurrentLayer;
        if (HasLayerFlag(layer, LayerFlags.Locked))
        {
            throw new InvalidOperationException(
                $"Current layer '{layer.Name}' is locked and cannot receive an ELLIPSE entity.");
        }

        double thickness = document.Header.ThicknessDefault;
        if (!double.IsFinite(thickness))
        {
            throw new InvalidOperationException(
                "Current THICKNESS must be finite before creating an ELLIPSE.");
        }
        if (thickness != 0.0)
        {
            throw new CadUnsupportedEntityException(
                "Nonzero THICKNESS requires retained ellipse-extrusion geometry and is not authored as a planar outline.");
        }

        double lineTypeScale = document.Header.CurrentEntityLinetypeScale;
        if (!double.IsFinite(lineTypeScale) || lineTypeScale <= 0.0)
        {
            throw new InvalidOperationException(
                "Current CELTSCALE must be finite and positive before creating an ELLIPSE.");
        }

        return new Ellipse
        {
            Center = new XYZ(
                _snapshot.Center.X,
                _snapshot.Center.Y,
                _snapshot.Center.Z),
            MajorAxisEndPoint = new XYZ(
                _snapshot.MajorAxisEndPoint.X,
                _snapshot.MajorAxisEndPoint.Y,
                0.0),
            RadiusRatio = _snapshot.RadiusRatio,
            StartParameter = _snapshot.StartParameter,
            EndParameter = _snapshot.EndParameter,
            Normal = XYZ.AxisZ,
            Thickness = thickness,
            Layer = layer,
            Color = document.Header.CurrentEntityColor,
            LineType = document.Header.CurrentLineType,
            LineTypeScale = lineTypeScale,
            LineWeight = document.Header.CurrentEntityLineWeight,
        };
    }

    private static void ValidateDetached(Ellipse ellipse)
    {
        if (ellipse.Owner is not null ||
            ellipse.Document is not null ||
            ellipse.Handle != 0)
        {
            throw new InvalidOperationException(
                "The retained ELLIPSE entity is not detached and cannot be added.");
        }
    }
}

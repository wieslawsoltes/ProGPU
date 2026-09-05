using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using System.Globalization;

namespace ProGPU.CAD;

/// <summary>The exact independent construction used by one plan-view ARC command.</summary>
public enum CadArcAuthoringMode : byte
{
    ThreePoint = 0,
    CenterStartEnd = 1,
    CenterStartAngle = 2,
    CenterStartChord = 3,
    StartCenterEnd = 4,
    StartCenterAngle = 5,
    StartCenterChord = 6,
    StartEndAngle = 7,
    StartEndDirection = 8,
    StartEndRadius = 9,
}

/// <summary>The kind and unit of a mode's non-point final input.</summary>
public enum CadArcScalarInputKind : byte
{
    None = 0,
    IncludedAngleRadians = 1,
    ChordLength = 2,
    DirectionAngleRadians = 3,
    Radius = 4,
}

/// <summary>A bounded invariant signed scalar used by an ARC final prompt.</summary>
public readonly record struct CadArcScalarInput
{
    public const int MaximumCodeUnits = 128;

    private const NumberStyles ScalarNumberStyles = NumberStyles.Float;

    public double Value { get; }

    private CadArcScalarInput(double value)
    {
        Value = value;
    }

    public static bool TryParse(string? text, out CadArcScalarInput input)
    {
        input = default;
        return text is not null && TryParse(text.AsSpan(), out input);
    }

    public static bool TryParse(
        ReadOnlySpan<char> text,
        out CadArcScalarInput input)
    {
        input = default;
        text = text.Trim();
        if (text.IsEmpty ||
            text.Length > MaximumCodeUnits ||
            !double.TryParse(
                text,
                ScalarNumberStyles,
                CultureInfo.InvariantCulture,
                out double value) ||
            !double.IsFinite(value))
        {
            return false;
        }

        input = new CadArcScalarInput(value);
        return true;
    }
}

/// <summary>Immutable analytic input for one Axis-Z plan-view arc.</summary>
public readonly record struct CadArcAuthoringSnapshot
{
    private const double TwoPi = Math.PI * 2.0;

    public CadPoint3D Center { get; }

    public double Radius { get; }

    /// <summary>Normalized persisted OCS start angle in radians.</summary>
    public double StartAngle { get; }

    /// <summary>Exact positive counterclockwise persisted sweep, less than one turn.</summary>
    public double SweepAngle { get; }

    /// <summary>Normalized persisted OCS end angle in radians.</summary>
    public double EndAngle { get; }

    public CadPoint3D StartPoint => PointAt(StartAngle);

    public CadPoint3D EndPoint => PointAt(StartAngle + SweepAngle);

    public CadArcAuthoringSnapshot(
        CadPoint3D center,
        double radius,
        double startAngle,
        double sweepAngle)
    {
        if (!IsFinite(center))
        {
            throw new ArgumentException(
                "An ARC center must contain finite WCS coordinates.",
                nameof(center));
        }
        if (!double.IsFinite(radius) || radius <= 0.0 || radius > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radius),
                radius,
                $"An ARC radius must be finite, positive, and no greater than {float.MaxValue:R} for retained rendering.");
        }
        if (!double.IsFinite(startAngle))
        {
            throw new ArgumentOutOfRangeException(
                nameof(startAngle),
                "An ARC start angle must be finite.");
        }
        if (!double.IsFinite(sweepAngle) ||
            sweepAngle <= 0.0 ||
            sweepAngle >= TwoPi)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sweepAngle),
                sweepAngle,
                "An ARC sweep must be finite, positive, and less than one complete turn.");
        }

        double normalizedStart = NormalizeAngle(startAngle);
        double normalizedEnd = NormalizeAngle(normalizedStart + sweepAngle);
        if (normalizedStart == normalizedEnd)
        {
            throw new ArgumentException(
                "The ARC endpoints must remain numerically distinct.",
                nameof(sweepAngle));
        }

        Center = center;
        Radius = radius;
        StartAngle = normalizedStart;
        SweepAngle = sweepAngle;
        EndAngle = normalizedEnd;
    }

    public CadPoint3D PointAt(double angle) => new(
        Center.X + (Radius * Math.Cos(angle)),
        Center.Y + (Radius * Math.Sin(angle)),
        Center.Z);

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

/// <summary>Bounded host-neutral point state for one plan-view ARC.</summary>
/// <remarks>
/// Every independent construction retains exactly two accepted points. The final
/// point or scalar is solved without changing the session, so failed document
/// preflight remains recoverable. Storage and every solve are O(1).
/// </remarks>
public sealed class CadArcAuthoringSession
{
    private const double TwoPi = Math.PI * 2.0;
    private readonly CadPoint3D[] _points = new CadPoint3D[2];
    private int _pointCount;

    public CadArcAuthoringMode Mode { get; }

    public int PointCount => _pointCount;

    public int RequiredPointCount => 3;

    public bool HasFirstPoint => _pointCount > 0;

    public CadPoint3D? FirstPoint =>
        _pointCount == 0 ? null : _points[0];

    public CadPoint3D? CurrentPoint =>
        _pointCount == 0 ? null : _points[_pointCount - 1];

    public ReadOnlyMemory<CadPoint3D> Points =>
        _points.AsMemory(0, _pointCount);

    public CadArcScalarInputKind ScalarInputKind => Mode switch
    {
        CadArcAuthoringMode.CenterStartAngle or
        CadArcAuthoringMode.StartCenterAngle or
        CadArcAuthoringMode.StartEndAngle =>
            CadArcScalarInputKind.IncludedAngleRadians,
        CadArcAuthoringMode.CenterStartChord or
        CadArcAuthoringMode.StartCenterChord =>
            CadArcScalarInputKind.ChordLength,
        CadArcAuthoringMode.StartEndDirection =>
            CadArcScalarInputKind.DirectionAngleRadians,
        CadArcAuthoringMode.StartEndRadius =>
            CadArcScalarInputKind.Radius,
        _ => CadArcScalarInputKind.None,
    };

    public bool RequiresScalarFinalInput =>
        ScalarInputKind != CadArcScalarInputKind.None &&
        Mode != CadArcAuthoringMode.StartEndDirection;

    public bool AcceptsScalarFinalInput =>
        ScalarInputKind != CadArcScalarInputKind.None;

    public bool AcceptsPointFinalInput => Mode is
        CadArcAuthoringMode.ThreePoint or
        CadArcAuthoringMode.CenterStartEnd or
        CadArcAuthoringMode.StartCenterEnd or
        CadArcAuthoringMode.StartEndDirection;

    /// <summary>
    /// Whether the active point-final construction has an alternate clockwise
    /// route selected by Autodesk's transient Ctrl override.
    /// </summary>
    public bool CanApplyClockwiseOverride =>
        _pointCount == 2 &&
        Mode is (CadArcAuthoringMode.CenterStartEnd or
            CadArcAuthoringMode.StartCenterEnd or
            CadArcAuthoringMode.StartEndDirection);

    public CadArcAuthoringSession(CadArcAuthoringMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Mode = mode;
    }

    public bool CanAcceptPoint(CadPoint3D point)
    {
        return CanAcceptPoint(point, clockwiseOverride: false);
    }

    public bool CanAcceptPoint(
        CadPoint3D point,
        bool clockwiseOverride)
    {
        if (_pointCount < 2)
        {
            return ValidateNextPoint(point, out _);
        }

        return TryCreateSnapshot(
            point,
            clockwiseOverride,
            out _,
            out _);
    }

    /// <summary>Accepts either of the two non-final construction points.</summary>
    public bool TryAcceptIntermediatePoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        if (_pointCount >= 2)
        {
            errorMessage = RequiresScalarFinalInput
                ? $"ARC {Mode} is awaiting its final scalar value."
                : $"ARC {Mode} is awaiting its final point.";
            return false;
        }
        if (!ValidateNextPoint(point, out errorMessage))
        {
            return false;
        }

        _points[_pointCount++] = point;
        return true;
    }

    /// <summary>Resolves a point-defined final prompt without mutating accepted state.</summary>
    public bool TryCreateSnapshot(
        CadPoint3D finalPoint,
        out CadArcAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        return TryCreateSnapshot(
            finalPoint,
            clockwiseOverride: false,
            out snapshot,
            out errorMessage);
    }

    /// <summary>
    /// Resolves a point-defined final prompt with an explicit transient
    /// clockwise override. Three-point construction ignores the override
    /// because its second circumference point already fixes the route.
    /// </summary>
    public bool TryCreateSnapshot(
        CadPoint3D finalPoint,
        bool clockwiseOverride,
        out CadArcAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        snapshot = default;
        if (_pointCount != 2)
        {
            errorMessage =
                $"ARC {Mode} requires two accepted points before its final input.";
            return false;
        }
        if (!AcceptsPointFinalInput)
        {
            errorMessage =
                $"ARC {Mode} requires a final {DescribeScalarInput(ScalarInputKind)} value.";
            return false;
        }
        if (!ValidateFinalPoint(finalPoint, out errorMessage))
        {
            return false;
        }

        bool created = Mode switch
        {
            CadArcAuthoringMode.ThreePoint =>
                TryCreateThreePoint(
                    _points[0],
                    _points[1],
                    finalPoint,
                    out snapshot),
            CadArcAuthoringMode.CenterStartEnd =>
                TryCreateCenterStartEnd(
                    _points[0],
                    _points[1],
                    finalPoint,
                    clockwiseOverride,
                    out snapshot),
            CadArcAuthoringMode.StartCenterEnd =>
                TryCreateCenterStartEnd(
                    _points[1],
                    _points[0],
                    finalPoint,
                    clockwiseOverride,
                    out snapshot),
            CadArcAuthoringMode.StartEndDirection =>
                TryCreateStartEndDirection(
                    _points[0],
                    _points[1],
                    finalPoint,
                    clockwiseOverride,
                    out snapshot),
            _ => false,
        };
        if (!created)
        {
            errorMessage = GetPointFailureMessage();
            return false;
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Resolves the mode-specific final scalar without mutating accepted state.
    /// Angle and direction values are radians; chord and radius values are WCS
    /// lengths. Signs preserve the Autodesk minor/major and sweep contracts.
    /// </summary>
    public bool TryCreateSnapshotFromScalar(
        double value,
        out CadArcAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        snapshot = default;
        if (_pointCount != 2)
        {
            errorMessage =
                $"ARC {Mode} requires two accepted points before its final input.";
            return false;
        }
        if (!double.IsFinite(value))
        {
            errorMessage = "An ARC scalar value must be finite.";
            return false;
        }
        if (!AcceptsScalarFinalInput)
        {
            errorMessage = $"ARC {Mode} requires a final point, not a scalar value.";
            return false;
        }

        bool created = Mode switch
        {
            CadArcAuthoringMode.CenterStartAngle =>
                TryCreateCenterStartSweep(
                    _points[0],
                    _points[1],
                    value,
                    out snapshot),
            CadArcAuthoringMode.StartCenterAngle =>
                TryCreateCenterStartSweep(
                    _points[1],
                    _points[0],
                    value,
                    out snapshot),
            CadArcAuthoringMode.CenterStartChord =>
                TryCreateCenterStartChord(
                    _points[0],
                    _points[1],
                    value,
                    out snapshot),
            CadArcAuthoringMode.StartCenterChord =>
                TryCreateCenterStartChord(
                    _points[1],
                    _points[0],
                    value,
                    out snapshot),
            CadArcAuthoringMode.StartEndAngle =>
                TryCreateStartEndSweep(
                    _points[0],
                    _points[1],
                    value,
                    out snapshot),
            CadArcAuthoringMode.StartEndDirection =>
                TryCreateStartEndDirectionAngle(
                    _points[0],
                    _points[1],
                    value,
                    out snapshot),
            CadArcAuthoringMode.StartEndRadius =>
                TryCreateStartEndRadius(
                    _points[0],
                    _points[1],
                    value,
                    out snapshot),
            _ => false,
        };
        if (!created)
        {
            errorMessage = GetScalarFailureMessage();
            return false;
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Resolves an explicit WCS tangent direction for Start/End/Direction
    /// without adding a small direction point to a potentially large WCS origin.
    /// </summary>
    public bool TryCreateSnapshotFromDirection(
        CadPoint3D direction,
        out CadArcAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        snapshot = default;
        if (_pointCount != 2 ||
            Mode != CadArcAuthoringMode.StartEndDirection)
        {
            errorMessage =
                "An explicit ARC direction requires Start/End/Direction mode and two accepted points.";
            return false;
        }
        if (!IsFinite(direction) ||
            !TryCreateStartEndDirectionVector(
                _points[0],
                _points[1],
                direction,
                out snapshot))
        {
            errorMessage = GetScalarFailureMessage();
            return false;
        }

        errorMessage = null;
        return true;
    }

    private bool ValidateNextPoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!IsFinite(point))
        {
            errorMessage = "An ARC point must contain finite WCS coordinates.";
            return false;
        }
        if (_pointCount > 0 && point.Z != _points[0].Z)
        {
            errorMessage =
                "A plan-view ARC point must remain on the first point's WCS-Z plane.";
            return false;
        }
        for (int i = 0; i < _pointCount; i++)
        {
            if (point == _points[i])
            {
                errorMessage = "ARC construction points must be distinct.";
                return false;
            }
        }

        return true;
    }

    private bool ValidateFinalPoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        if (!IsFinite(point))
        {
            errorMessage = "An ARC point must contain finite WCS coordinates.";
            return false;
        }
        if (point.Z != _points[0].Z)
        {
            errorMessage =
                "A plan-view ARC point must remain on the first point's WCS-Z plane.";
            return false;
        }
        if (Mode != CadArcAuthoringMode.StartEndDirection)
        {
            for (int i = 0; i < _pointCount; i++)
            {
                if (point == _points[i])
                {
                    errorMessage = "ARC construction points must be distinct.";
                    return false;
                }
            }
        }
        else if (point == _points[0])
        {
            errorMessage =
                "An ARC tangent direction point must differ from its start point.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private string GetPointFailureMessage() => Mode switch
    {
        CadArcAuthoringMode.ThreePoint =>
            "The three circumference points must define a finite non-collinear ARC.",
        CadArcAuthoringMode.CenterStartEnd or
        CadArcAuthoringMode.StartCenterEnd =>
            "The center, start point, and endpoint ray must define a finite non-degenerate ARC.",
        CadArcAuthoringMode.StartEndDirection =>
            "The start, end, and tangent direction must define a finite non-degenerate ARC.",
        _ => $"ARC {Mode} does not accept a final point.",
    };

    private string GetScalarFailureMessage() => ScalarInputKind switch
    {
        CadArcScalarInputKind.IncludedAngleRadians =>
            "The included angle must be finite, nonzero, less than one turn in magnitude, and define a renderable ARC.",
        CadArcScalarInputKind.ChordLength =>
            "The signed chord length must be finite, nonzero, no greater than the diameter in magnitude, and define a renderable ARC.",
        CadArcScalarInputKind.DirectionAngleRadians =>
            "The tangent direction must be finite, non-collinear with the chord, and define a renderable ARC.",
        CadArcScalarInputKind.Radius =>
            "The signed radius must be finite, nonzero, at least half the endpoint chord in magnitude, and renderable.",
        _ => $"ARC {Mode} does not accept a final scalar value.",
    };

    private static string DescribeScalarInput(CadArcScalarInputKind kind) =>
        kind switch
        {
            CadArcScalarInputKind.IncludedAngleRadians => "included-angle",
            CadArcScalarInputKind.ChordLength => "signed chord-length",
            CadArcScalarInputKind.DirectionAngleRadians => "tangent-direction angle",
            CadArcScalarInputKind.Radius => "signed radius",
            _ => "point",
        };

    private static bool TryCreateThreePoint(
        CadPoint3D start,
        CadPoint3D pointOnArc,
        CadPoint3D end,
        out CadArcAuthoringSnapshot snapshot)
    {
        snapshot = default;
        if (!TryGetThreePointCircle(
                start,
                pointOnArc,
                end,
                out CadPoint3D center,
                out double radius))
        {
            return false;
        }

        double orientation = Cross2D(pointOnArc - start, end - start);
        if (!double.IsFinite(orientation) || orientation == 0.0)
        {
            return false;
        }

        double startAngle = Angle(center, start);
        double endAngle = Angle(center, end);
        return orientation > 0.0
            ? TryCreateSnapshot(center, radius, startAngle, endAngle, out snapshot)
            : TryCreateSnapshot(center, radius, endAngle, startAngle, out snapshot);
    }

    private static bool TryCreateCenterStartEnd(
        CadPoint3D center,
        CadPoint3D start,
        CadPoint3D endRayPoint,
        bool clockwiseOverride,
        out CadArcAuthoringSnapshot snapshot)
    {
        snapshot = default;
        double radius = Distance2D(center, start);
        if (!double.IsFinite(radius) || radius <= 0.0 ||
            !TryGetUnit2D(endRayPoint - center, out _, out _))
        {
            return false;
        }

        double startAngle = Angle(center, start);
        double endAngle = Angle(center, endRayPoint);
        return clockwiseOverride
            ? TryCreateSnapshot(
                center,
                radius,
                endAngle,
                startAngle,
                out snapshot)
            : TryCreateSnapshot(
                center,
                radius,
                startAngle,
                endAngle,
                out snapshot);
    }

    private static bool TryCreateCenterStartSweep(
        CadPoint3D center,
        CadPoint3D start,
        double signedSweep,
        out CadArcAuthoringSnapshot snapshot)
    {
        snapshot = default;
        double magnitude = Math.Abs(signedSweep);
        double radius = Distance2D(center, start);
        if (!double.IsFinite(magnitude) ||
            magnitude <= 0.0 ||
            magnitude >= TwoPi ||
            !double.IsFinite(radius) ||
            radius <= 0.0)
        {
            return false;
        }

        double constructionStart = Angle(center, start);
        return signedSweep > 0.0
            ? TryCreateSnapshot(
                center,
                radius,
                constructionStart,
                constructionStart + magnitude,
                out snapshot)
            : TryCreateSnapshot(
                center,
                radius,
                constructionStart - magnitude,
                constructionStart,
                out snapshot);
    }

    private static bool TryCreateCenterStartChord(
        CadPoint3D center,
        CadPoint3D start,
        double signedChordLength,
        out CadArcAuthoringSnapshot snapshot)
    {
        snapshot = default;
        double radius = Distance2D(center, start);
        double chord = Math.Abs(signedChordLength);
        if (!double.IsFinite(radius) ||
            radius <= 0.0 ||
            !double.IsFinite(chord) ||
            chord <= 0.0 ||
            chord > radius * 2.0)
        {
            return false;
        }

        double ratio = Math.Min(1.0, chord / (radius * 2.0));
        double minorSweep = 2.0 * Math.Asin(ratio);
        double sweep = signedChordLength > 0.0
            ? minorSweep
            : TwoPi - minorSweep;
        return TryCreateCenterStartSweep(
            center,
            start,
            sweep,
            out snapshot);
    }

    private static bool TryCreateStartEndSweep(
        CadPoint3D start,
        CadPoint3D end,
        double signedSweep,
        out CadArcAuthoringSnapshot snapshot)
    {
        snapshot = default;
        double magnitude = Math.Abs(signedSweep);
        if (!double.IsFinite(magnitude) ||
            magnitude <= 0.0 ||
            magnitude >= TwoPi ||
            !TryGetUnit2D(end - start, out double chordX, out double chordY))
        {
            return false;
        }

        double chordLength = Distance2D(start, end);
        double halfSweep = signedSweep * 0.5;
        double sine = Math.Sin(halfSweep);
        if (!double.IsFinite(chordLength) ||
            chordLength <= 0.0 ||
            !double.IsFinite(sine) ||
            sine == 0.0)
        {
            return false;
        }

        double halfChord = chordLength * 0.5;
        double centerOffset = halfChord * Math.Cos(halfSweep) / sine;
        CadPoint3D center = new(
            Midpoint(start.X, end.X) - (chordY * centerOffset),
            Midpoint(start.Y, end.Y) + (chordX * centerOffset),
            start.Z);
        return TryCreateCenterStartSweep(
            center,
            start,
            signedSweep,
            out snapshot);
    }

    private static bool TryCreateStartEndDirectionAngle(
        CadPoint3D start,
        CadPoint3D end,
        double directionAngle,
        out CadArcAuthoringSnapshot snapshot)
    {
        snapshot = default;
        if (!double.IsFinite(directionAngle))
        {
            return false;
        }

        return TryCreateStartEndDirectionVector(
            start,
            end,
            new CadPoint3D(
                Math.Cos(directionAngle),
                Math.Sin(directionAngle),
                0.0),
            out snapshot);
    }

    private static bool TryCreateStartEndDirection(
        CadPoint3D start,
        CadPoint3D end,
        CadPoint3D directionPoint,
        bool clockwiseOverride,
        out CadArcAuthoringSnapshot snapshot)
    {
        snapshot = default;
        return TryCreateStartEndDirectionVector(
            start,
            end,
            directionPoint - start,
            clockwiseOverride,
            out snapshot);
    }

    private static bool TryCreateStartEndDirectionVector(
        CadPoint3D start,
        CadPoint3D end,
        CadPoint3D direction,
        out CadArcAuthoringSnapshot snapshot)
    {
        return TryCreateStartEndDirectionVector(
            start,
            end,
            direction,
            clockwiseOverride: false,
            out snapshot);
    }

    private static bool TryCreateStartEndDirectionVector(
        CadPoint3D start,
        CadPoint3D end,
        CadPoint3D direction,
        bool clockwiseOverride,
        out CadArcAuthoringSnapshot snapshot)
    {
        snapshot = default;
        if (!TryGetUnit2D(
                direction,
                out double tangentX,
                out double tangentY))
        {
            return false;
        }

        double chordDeltaX = end.X - start.X;
        double chordDeltaY = end.Y - start.Y;
        double chordScale = Math.Max(
            Math.Abs(chordDeltaX),
            Math.Abs(chordDeltaY));
        if (!double.IsFinite(chordScale) || chordScale <= 0.0)
        {
            return false;
        }

        double chordX = chordDeltaX / chordScale;
        double chordY = chordDeltaY / chordScale;
        double normalX = -tangentY;
        double normalY = tangentX;
        double denominator = 2.0 *
            ((chordX * normalX) + (chordY * normalY));
        double normalizedLengthSquared =
            (chordX * chordX) + (chordY * chordY);
        if (!double.IsFinite(denominator) || denominator == 0.0)
        {
            return false;
        }

        double centerOffset =
            chordScale * normalizedLengthSquared / denominator;
        CadPoint3D center = new(
            start.X + (normalX * centerOffset),
            start.Y + (normalY * centerOffset),
            start.Z);
        double radius = Math.Abs(centerOffset);
        double startAngle = Angle(center, start);
        double endAngle = Angle(center, end);
        bool useCounterclockwiseRoute =
            centerOffset > 0.0 && !clockwiseOverride;
        return useCounterclockwiseRoute
            ? TryCreateSnapshot(
                center,
                radius,
                startAngle,
                endAngle,
                out snapshot)
            : TryCreateSnapshot(
                center,
                radius,
                endAngle,
                startAngle,
                out snapshot);
    }

    private static bool TryCreateStartEndRadius(
        CadPoint3D start,
        CadPoint3D end,
        double signedRadius,
        out CadArcAuthoringSnapshot snapshot)
    {
        snapshot = default;
        double radius = Math.Abs(signedRadius);
        if (!double.IsFinite(radius) ||
            radius <= 0.0 ||
            !TryGetUnit2D(end - start, out double chordX, out double chordY))
        {
            return false;
        }

        double chordLength = Distance2D(start, end);
        double halfChord = chordLength * 0.5;
        if (!double.IsFinite(chordLength) ||
            chordLength <= 0.0 ||
            radius < halfChord)
        {
            return false;
        }

        double height = Math.Sqrt(
            Math.Max(0.0, (radius - halfChord) * (radius + halfChord)));
        double side = signedRadius > 0.0 ? 1.0 : -1.0;
        CadPoint3D center = new(
            Midpoint(start.X, end.X) - (chordY * height * side),
            Midpoint(start.Y, end.Y) + (chordX * height * side),
            start.Z);
        double minorSweep = 2.0 * Math.Asin(
            Math.Min(1.0, halfChord / radius));
        double sweep = signedRadius > 0.0
            ? minorSweep
            : TwoPi - minorSweep;
        return TryCreateCenterStartSweep(
            center,
            start,
            sweep,
            out snapshot);
    }

    private static bool TryCreateSnapshot(
        CadPoint3D center,
        double radius,
        double startAngle,
        double endAngle,
        out CadArcAuthoringSnapshot snapshot)
    {
        snapshot = default;
        double sweep = PositiveAngle(endAngle - startAngle);
        if (!double.IsFinite(sweep) || sweep <= 0.0 || sweep >= TwoPi)
        {
            return false;
        }

        try
        {
            snapshot = new CadArcAuthoringSnapshot(
                center,
                radius,
                startAngle,
                sweep);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
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
        double offsetX = scale *
            (((y3 * length2) - (y2 * length3)) / determinant);
        double offsetY = scale *
            (((x2 * length3) - (x3 * length2)) / determinant);
        center = new CadPoint3D(
            first.X + offsetX,
            first.Y + offsetY,
            first.Z);
        radius = Hypot(offsetX, offsetY);
        return IsFinite(center) &&
            double.IsFinite(radius) &&
            radius > 0.0;
    }

    private static bool TryGetUnit2D(
        CadPoint3D vector,
        out double x,
        out double y)
    {
        x = 0.0;
        y = 0.0;
        double scale = Math.Max(Math.Abs(vector.X), Math.Abs(vector.Y));
        if (!double.IsFinite(scale) || scale <= 0.0)
        {
            return false;
        }

        double scaledX = vector.X / scale;
        double scaledY = vector.Y / scale;
        double length = Math.Sqrt(
            (scaledX * scaledX) + (scaledY * scaledY));
        if (!double.IsFinite(length) || length <= 0.0)
        {
            return false;
        }

        x = scaledX / length;
        y = scaledY / length;
        return double.IsFinite(x) && double.IsFinite(y);
    }

    private static double Angle(CadPoint3D center, CadPoint3D point) =>
        Math.Atan2(point.Y - center.Y, point.X - center.X);

    private static double PositiveAngle(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }

    private static double Cross2D(CadPoint3D first, CadPoint3D second) =>
        (first.X * second.Y) - (first.Y * second.X);

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

/// <summary>Adds one analytic ARC as one reversible history operation.</summary>
/// <remarks>
/// Current CLAYER, CECOLOR, CELTYPE, CELTSCALE, CELWEIGHT, and THICKNESS are
/// captured atomically on first Apply. Nonzero THICKNESS fails before mutation
/// until retained extrusion geometry is available. Apply/Undo/Redo are O(1).
/// </remarks>
public sealed class CadAddArcCommand : CadEditCommand
{
    private readonly CadArcAuthoringSnapshot _snapshot;
    private Arc? _arc;

    public CadArcAuthoringSnapshot Snapshot => _snapshot;

    public Arc? Arc => _arc;

    public ulong CurrentHandle => _arc?.Handle ?? 0;

    public CadAddArcCommand(
        CadArcAuthoringSnapshot snapshot,
        string description = "ARC")
        : base(description)
    {
        _snapshot = snapshot;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Arc arc;
        if (isRedo)
        {
            arc = _arc ?? throw new InvalidOperationException(
                "The ARC command has not been applied.");
        }
        else
        {
            arc = CreateArc(document);
            _arc = arc;
        }

        ValidateDetached(arc);
        document.Entities.Add(arc);
    }

    internal override void Revert(CadDocument document)
    {
        Arc arc = _arc ?? throw new InvalidOperationException(
            "The ARC command has not been applied.");
        ValidateModelSpaceEntity(document, arc);
        if (!document.Entities.Remove(arc))
        {
            throw new InvalidOperationException(
                "The authored ARC could not be removed from model space.");
        }
    }

    private Arc CreateArc(CadDocument document)
    {
        Layer layer = document.Header.CurrentLayer;
        if (HasLayerFlag(layer, LayerFlags.Locked))
        {
            throw new InvalidOperationException(
                $"Current layer '{layer.Name}' is locked and cannot receive an ARC entity.");
        }

        double thickness = document.Header.ThicknessDefault;
        if (!double.IsFinite(thickness))
        {
            throw new InvalidOperationException(
                "Current THICKNESS must be finite before creating an ARC.");
        }
        if (thickness != 0.0)
        {
            throw new CadUnsupportedEntityException(
                "Nonzero THICKNESS requires retained arc-extrusion geometry and is not authored as a planar outline.");
        }

        double lineTypeScale = document.Header.CurrentEntityLinetypeScale;
        if (!double.IsFinite(lineTypeScale) || lineTypeScale <= 0.0)
        {
            throw new InvalidOperationException(
                "Current CELTSCALE must be finite and positive before creating an ARC.");
        }

        return new Arc(
            new XYZ(
                _snapshot.Center.X,
                _snapshot.Center.Y,
                _snapshot.Center.Z),
            _snapshot.Radius,
            _snapshot.StartAngle,
            _snapshot.EndAngle)
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

    private static void ValidateDetached(Arc arc)
    {
        if (arc.Owner is not null ||
            arc.Document is not null ||
            arc.Handle != 0)
        {
            throw new InvalidOperationException(
                "The retained ARC entity is not detached and cannot be added.");
        }
    }
}

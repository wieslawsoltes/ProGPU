using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace ProGPU.CAD;

/// <summary>One immutable persisted XLINE point and unit WCS direction.</summary>
public readonly record struct CadXLineDefinition
{
    public CadPoint3D FirstPoint { get; }

    public CadPoint3D Direction { get; }

    public CadXLineDefinition(CadPoint3D firstPoint, CadPoint3D direction)
    {
        if (!IsFinite(firstPoint))
        {
            throw new ArgumentException(
                "An XLINE first point must contain finite WCS coordinates.",
                nameof(firstPoint));
        }
        if (!CadRayAuthoringSession.TryNormalizeDirection(
                direction,
                out CadPoint3D normalized))
        {
            throw new ArgumentException(
                "An XLINE direction must be a finite nonzero WCS vector.",
                nameof(direction));
        }

        FirstPoint = firstPoint;
        Direction = normalized;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>Exact allocation-free construction solvers for XLINE prompt modes.</summary>
public static class CadXLineConstruction
{
    private const double PlaneTolerance = 1e-10;

    public static bool TryCreateThroughPoint(
        CadPoint3D point,
        CadPoint3D direction,
        out CadXLineDefinition definition)
    {
        definition = default;
        try
        {
            definition = new CadXLineDefinition(point, direction);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool TryCreateAtAngle(
        CadPoint3D point,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        double angleRadians,
        bool isClockwise,
        out CadXLineDefinition definition)
    {
        definition = default;
        if (!double.IsFinite(angleRadians) ||
            !TryGetPlanBasis(xAxis, yAxis, out xAxis, out yAxis))
        {
            return false;
        }

        double signedAngle = isClockwise ? -angleRadians : angleRadians;
        double cosine = Math.Cos(signedAngle);
        double sine = Math.Sin(signedAngle);
        if (!double.IsFinite(cosine) || !double.IsFinite(sine))
        {
            return false;
        }
        return TryCreateThroughPoint(
            point,
            (xAxis * cosine) + (yAxis * sine),
            out definition);
    }

    public static bool TryCreateAtReferenceAngle(
        CadPoint3D point,
        CadPoint3D referenceDirection,
        CadPoint3D planeNormal,
        double counterclockwiseRadians,
        out CadXLineDefinition definition)
    {
        definition = default;
        if (!double.IsFinite(counterclockwiseRadians) ||
            !CadRayAuthoringSession.TryNormalizeDirection(
                referenceDirection,
                out CadPoint3D reference) ||
            !CadRayAuthoringSession.TryNormalizeDirection(
                planeNormal,
                out CadPoint3D normal) ||
            Math.Abs(CadPoint3D.Dot(reference, normal)) > PlaneTolerance)
        {
            return false;
        }

        double cosine = Math.Cos(counterclockwiseRadians);
        double sine = Math.Sin(counterclockwiseRadians);
        CadPoint3D perpendicular = CadPoint3D.Cross(normal, reference);
        return double.IsFinite(cosine) &&
            double.IsFinite(sine) &&
            TryCreateThroughPoint(
                point,
                (reference * cosine) + (perpendicular * sine),
                out definition);
    }

    public static bool TryCreateBisector(
        CadPoint3D vertex,
        CadPoint3D firstRayPoint,
        CadPoint3D secondRayPoint,
        out CadXLineDefinition definition)
    {
        definition = default;
        if (!CadRayAuthoringSession.TryGetUnitDirection(
                vertex,
                firstRayPoint,
                out CadPoint3D first) ||
            !CadRayAuthoringSession.TryGetUnitDirection(
                vertex,
                secondRayPoint,
                out CadPoint3D second))
        {
            return false;
        }
        return TryCreateThroughPoint(vertex, first + second, out definition);
    }

    public static bool TryCreateOffsetThrough(
        CadXLineDefinition source,
        CadPoint3D throughPoint,
        CadPoint3D planeNormal,
        out CadXLineDefinition definition)
    {
        definition = default;
        if (!TryGetPlanDirection(
                source.Direction,
                planeNormal,
                out CadPoint3D direction,
                out CadPoint3D normal) ||
            !IsFinite(throughPoint))
        {
            return false;
        }

        if (!CadRayAuthoringSession.TryGetUnitDirection(
                source.FirstPoint,
                throughPoint,
                out CadPoint3D normalizedDelta) ||
            Math.Abs(CadPoint3D.Dot(normalizedDelta, normal)) >
                PlaneTolerance ||
            Math.Abs(CadPoint3D.Dot(
                normalizedDelta,
                CadPoint3D.Cross(normal, direction))) <= PlaneTolerance)
        {
            return false;
        }
        return TryCreateThroughPoint(throughPoint, direction, out definition);
    }

    public static bool TryCreateOffsetAtDistance(
        CadXLineDefinition source,
        CadPoint3D sidePoint,
        CadPoint3D planeNormal,
        double distance,
        out CadXLineDefinition definition)
    {
        definition = default;
        if (!double.IsFinite(distance) || distance <= 0.0 ||
            !IsFinite(sidePoint) ||
            !TryGetPlanDirection(
                source.Direction,
                planeNormal,
                out CadPoint3D direction,
                out CadPoint3D normal))
        {
            return false;
        }

        CadPoint3D perpendicular = CadPoint3D.Cross(normal, direction);
        if (!CadRayAuthoringSession.TryGetUnitDirection(
                source.FirstPoint,
                sidePoint,
                out CadPoint3D sideDirection))
        {
            return false;
        }
        double side = CadPoint3D.Dot(sideDirection, perpendicular);
        if (!double.IsFinite(side) ||
            Math.Abs(CadPoint3D.Dot(sideDirection, normal)) >
                PlaneTolerance ||
            Math.Abs(side) <= PlaneTolerance)
        {
            return false;
        }
        CadPoint3D offset = perpendicular * Math.CopySign(distance, side);
        CadPoint3D point = source.FirstPoint + offset;
        return TryCreateThroughPoint(point, direction, out definition);
    }

    private static bool TryGetPlanBasis(
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        out CadPoint3D normalizedX,
        out CadPoint3D normalizedY)
    {
        normalizedX = default;
        normalizedY = default;
        if (!CadRayAuthoringSession.TryNormalizeDirection(
                xAxis,
                out normalizedX) ||
            !CadRayAuthoringSession.TryNormalizeDirection(
                yAxis,
                out normalizedY) ||
            Math.Abs(CadPoint3D.Dot(normalizedX, normalizedY)) > PlaneTolerance)
        {
            normalizedX = default;
            normalizedY = default;
            return false;
        }
        return true;
    }

    private static bool TryGetPlanDirection(
        CadPoint3D direction,
        CadPoint3D planeNormal,
        out CadPoint3D normalizedDirection,
        out CadPoint3D normalizedNormal)
    {
        normalizedDirection = default;
        normalizedNormal = default;
        return CadRayAuthoringSession.TryNormalizeDirection(
                direction,
                out normalizedDirection) &&
            CadRayAuthoringSession.TryNormalizeDirection(
                planeNormal,
                out normalizedNormal) &&
            Math.Abs(CadPoint3D.Dot(
                normalizedDirection,
                normalizedNormal)) <= PlaneTolerance;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>Selectable immutable linear geometry accepted by XLINE modes.</summary>
public readonly record struct CadXLineLinearSource
{
    public ulong ContentGeneration { get; }

    public int EntityIndex { get; }

    public ulong Handle { get; }

    public CadEntityKind Kind { get; }

    public CadPoint3D BasePoint { get; }

    public CadPoint3D Direction { get; }

    internal CadXLineLinearSource(
        ulong contentGeneration,
        int entityIndex,
        ulong handle,
        CadEntityKind kind,
        CadPoint3D basePoint,
        CadPoint3D direction)
    {
        ContentGeneration = contentGeneration;
        EntityIndex = entityIndex;
        Handle = handle;
        Kind = kind;
        BasePoint = basePoint;
        Direction = direction;
    }
}

public enum CadXLineLinearSourceStatus : byte
{
    Success = 0,
    StaleGeneration = 1,
    InvalidCandidate = 2,
    CandidateMismatch = 3,
    HiddenPrimitive = 4,
    UnsupportedKind = 5,
    DegenerateGeometry = 6,
}

public readonly record struct CadXLineLinearSourceResult(
    CadXLineLinearSourceStatus Status,
    CadXLineLinearSource Source)
{
    public bool IsSuccess => Status == CadXLineLinearSourceStatus.Success;
}

/// <summary>
/// Resolves one exact snapshot LINE, RAY, or XLINE selection without consulting
/// the mutable ACadSharp graph.
/// </summary>
public static class CadXLineLinearSourceResolver
{
    public static CadXLineLinearSourceResult Resolve(
        CadDocumentSnapshot snapshot,
        CadSelectionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (candidate.ContentGeneration != snapshot.ContentGeneration)
        {
            return Failure(CadXLineLinearSourceStatus.StaleGeneration);
        }

        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        if ((uint)candidate.EntityIndex >= (uint)entities.Length)
        {
            return Failure(CadXLineLinearSourceStatus.InvalidCandidate);
        }

        CadEntityHeader header = entities[candidate.EntityIndex];
        if (candidate.Handle != header.Handle ||
            candidate.Kind != header.Kind ||
            candidate.Bounds != header.Bounds)
        {
            return Failure(CadXLineLinearSourceStatus.CandidateMismatch);
        }
        if (!header.IsVisible)
        {
            return Failure(CadXLineLinearSourceStatus.HiddenPrimitive);
        }

        CadPoint3D basePoint;
        CadPoint3D direction;
        switch (header.Kind)
        {
            case CadEntityKind.Line:
                if ((uint)header.PrimitiveIndex >= (uint)snapshot.Lines.Length)
                {
                    return Failure(CadXLineLinearSourceStatus.InvalidCandidate);
                }
                CadLinePrimitive line = snapshot.Lines.Span[header.PrimitiveIndex];
                basePoint = line.Start;
                if (!CadRayAuthoringSession.TryGetUnitDirection(
                        line.Start,
                        line.End,
                        out direction))
                {
                    return Failure(
                        CadXLineLinearSourceStatus.DegenerateGeometry);
                }
                break;
            case CadEntityKind.Ray:
            case CadEntityKind.XLine:
                if ((uint)header.PrimitiveIndex >=
                    (uint)snapshot.ConstructionLines.Length)
                {
                    return Failure(CadXLineLinearSourceStatus.InvalidCandidate);
                }
                CadConstructionLinePrimitive construction =
                    snapshot.ConstructionLines.Span[header.PrimitiveIndex];
                basePoint = construction.BasePoint;
                if (!CadRayAuthoringSession.TryNormalizeDirection(
                        construction.Direction,
                        out direction))
                {
                    return Failure(
                        CadXLineLinearSourceStatus.DegenerateGeometry);
                }
                break;
            default:
                return Failure(CadXLineLinearSourceStatus.UnsupportedKind);
        }

        return new CadXLineLinearSourceResult(
            CadXLineLinearSourceStatus.Success,
            new CadXLineLinearSource(
                snapshot.ContentGeneration,
                candidate.EntityIndex,
                header.Handle,
                header.Kind,
                basePoint,
                direction));
    }

    private static CadXLineLinearSourceResult Failure(
        CadXLineLinearSourceStatus status) => new(status, default);
}

public enum CadXLineAuthoringMode : byte
{
    TwoPoint = 0,
    Horizontal = 1,
    Vertical = 2,
    Angle = 3,
    Bisect = 4,
    Offset = 5,
}

public enum CadXLinePromptKind : byte
{
    FirstPoint = 0,
    ThroughPoint = 1,
    PlacementPoint = 2,
    AngleValue = 3,
    AngleReferenceSource = 4,
    BisectorVertex = 5,
    BisectorFirstRayPoint = 6,
    BisectorSecondRayPoint = 7,
    OffsetDistance = 8,
    OffsetSource = 9,
    OffsetSidePoint = 10,
    OffsetThroughPoint = 11,
}

/// <summary>Bounded host-neutral state for every documented XLINE mode.</summary>
/// <remarks>
/// Point, scalar, and source transitions are O(1). Accepted definitions use a
/// geometrically growing array capped by MaximumLineCount; snapshot capture is
/// O(L) for L accepted construction lines. No mutable document object is retained.
/// </remarks>
public sealed class CadXLineModeAuthoringSession
{
    private readonly CadPlanAuthoringContext _context;
    private readonly ulong _sourceContentGeneration;
    private CadXLineDefinition[] _definitions;
    private int _definitionCount;
    private CadPoint3D _firstPoint;
    private bool _hasFirstPoint;
    private CadPoint3D _fixedDirection;
    private CadPoint3D _bisectorVertex;
    private CadPoint3D _bisectorFirstRayPoint;
    private CadXLineLinearSource _linearSource;
    private bool _hasLinearSource;
    private CadPoint3D _lastAcceptedPoint;
    private bool _hasLastAcceptedPoint;
    private bool _usesReferenceAngle;
    private bool _usesThroughOffset;
    private double _offsetDistance;

    public CadXLineAuthoringMode Mode { get; }

    public CadXLinePromptKind Prompt { get; private set; }

    public CadPlanAuthoringContext Context => _context;

    public ulong SourceContentGeneration => _sourceContentGeneration;

    public int MaximumLineCount { get; }

    public int LineCount => _definitionCount;

    public bool HasFirstPoint => _hasFirstPoint;

    public CadPoint3D? FirstPoint => _hasFirstPoint ? _firstPoint : null;

    public bool UsesReferenceAngle => _usesReferenceAngle;

    public bool UsesThroughOffset => _usesThroughOffset;

    public CadPoint3D? PlacementDirection =>
        Prompt == CadXLinePromptKind.PlacementPoint
            ? _fixedDirection
            : null;

    public CadPoint3D? BisectorVertex =>
        Prompt is CadXLinePromptKind.BisectorFirstRayPoint or
            CadXLinePromptKind.BisectorSecondRayPoint
            ? _bisectorVertex
            : null;

    public CadPoint3D? BisectorFirstRayPoint =>
        Prompt == CadXLinePromptKind.BisectorSecondRayPoint
            ? _bisectorFirstRayPoint
            : null;

    public CadXLineLinearSource? CurrentLinearSource =>
        _hasLinearSource ? _linearSource : null;

    public CadPoint3D? AcquisitionBasePoint => Prompt switch
    {
        CadXLinePromptKind.ThroughPoint => FirstPoint,
        CadXLinePromptKind.PlacementPoint when _hasLastAcceptedPoint =>
            _lastAcceptedPoint,
        CadXLinePromptKind.BisectorVertex when _hasLastAcceptedPoint =>
            _lastAcceptedPoint,
        CadXLinePromptKind.BisectorFirstRayPoint or
            CadXLinePromptKind.BisectorSecondRayPoint => _bisectorVertex,
        CadXLinePromptKind.OffsetSidePoint or
            CadXLinePromptKind.OffsetThroughPoint when _hasLinearSource =>
            _linearSource.BasePoint,
        _ => null,
    };

    public ReadOnlyMemory<CadXLineDefinition> Definitions =>
        _definitions.AsMemory(0, _definitionCount);

    public CadXLineModeAuthoringSession(
        CadXLineAuthoringMode mode,
        CadPlanAuthoringContext context,
        ulong sourceContentGeneration,
        int maximumLineCount = CadXLineAuthoringSession.DefaultMaximumLineCount)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!context.IsSupported)
        {
            throw new ArgumentException(
                "XLINE mode authoring requires a supported plan-UCS context.",
                nameof(context));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineCount);

        Mode = mode;
        _context = context;
        _sourceContentGeneration = sourceContentGeneration;
        MaximumLineCount = maximumLineCount;
        _definitions = new CadXLineDefinition[Math.Min(maximumLineCount, 16)];
        Prompt = mode switch
        {
            CadXLineAuthoringMode.TwoPoint => CadXLinePromptKind.FirstPoint,
            CadXLineAuthoringMode.Horizontal or
                CadXLineAuthoringMode.Vertical =>
                CadXLinePromptKind.PlacementPoint,
            CadXLineAuthoringMode.Angle => CadXLinePromptKind.AngleValue,
            CadXLineAuthoringMode.Bisect => CadXLinePromptKind.BisectorVertex,
            CadXLineAuthoringMode.Offset => CadXLinePromptKind.OffsetDistance,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        _fixedDirection = mode switch
        {
            CadXLineAuthoringMode.Horizontal => context.HorizontalAxis,
            CadXLineAuthoringMode.Vertical => context.VerticalAxis,
            _ => default,
        };
    }

    public bool TryChooseAngleReference(out string? errorMessage)
    {
        errorMessage = null;
        if (Mode != CadXLineAuthoringMode.Angle ||
            Prompt != CadXLinePromptKind.AngleValue ||
            _usesReferenceAngle)
        {
            errorMessage =
                "Angle/Reference is not available at the current XLINE prompt.";
            return false;
        }

        _usesReferenceAngle = true;
        Prompt = CadXLinePromptKind.AngleReferenceSource;
        return true;
    }

    public bool TryChooseOffsetThrough(out string? errorMessage)
    {
        errorMessage = null;
        if (Mode != CadXLineAuthoringMode.Offset ||
            Prompt != CadXLinePromptKind.OffsetDistance)
        {
            errorMessage =
                "Offset/Through is not available at the current XLINE prompt.";
            return false;
        }

        _usesThroughOffset = true;
        Prompt = CadXLinePromptKind.OffsetSource;
        return true;
    }

    public bool TryAcceptValue(double value, out string? errorMessage)
    {
        errorMessage = null;
        if (!double.IsFinite(value))
        {
            errorMessage = "An XLINE scalar input must be finite.";
            return false;
        }

        if (Prompt == CadXLinePromptKind.AngleValue)
        {
            CadXLineDefinition directionDefinition = default;
            bool created;
            if (_usesReferenceAngle)
            {
                created = _hasLinearSource &&
                    CadXLineConstruction.TryCreateAtReferenceAngle(
                        _context.Origin,
                        _linearSource.Direction,
                        _context.Normal,
                        value,
                        out directionDefinition);
            }
            else
            {
                created = CadXLineConstruction.TryCreateAtAngle(
                    _context.Origin,
                    _context.AngleXAxis,
                    _context.AngleYAxis,
                    value,
                    _context.IsClockwise,
                    out directionDefinition);
            }
            if (!created)
            {
                errorMessage = _usesReferenceAngle
                    ? "Select a valid coplanar linear reference before entering its counterclockwise angle."
                    : "The XLINE angle cannot be resolved in the active plan basis.";
                return false;
            }

            _fixedDirection = directionDefinition.Direction;
            Prompt = CadXLinePromptKind.PlacementPoint;
            return true;
        }

        if (Prompt == CadXLinePromptKind.OffsetDistance)
        {
            if (value <= 0.0)
            {
                errorMessage = "An XLINE offset distance must be positive.";
                return false;
            }
            _offsetDistance = value;
            _usesThroughOffset = false;
            Prompt = CadXLinePromptKind.OffsetSource;
            return true;
        }

        errorMessage = "The current XLINE prompt does not accept a scalar value.";
        return false;
    }

    public bool TryAcceptLinearSource(
        CadXLineLinearSource source,
        out string? errorMessage)
    {
        errorMessage = null;
        if (source.ContentGeneration != _sourceContentGeneration)
        {
            errorMessage =
                "The selected XLINE source belongs to a stale document generation.";
            return false;
        }
        if (source.Kind is not (CadEntityKind.Line or
                CadEntityKind.Ray or CadEntityKind.XLine) ||
            !IsFinite(source.BasePoint) ||
            !CadRayAuthoringSession.TryNormalizeDirection(
                source.Direction,
                out CadPoint3D direction))
        {
            errorMessage = "XLINE source selection requires valid linear geometry.";
            return false;
        }
        if (_definitionCount == MaximumLineCount)
        {
            errorMessage = LimitMessage();
            return false;
        }

        source = new CadXLineLinearSource(
            source.ContentGeneration,
            source.EntityIndex,
            source.Handle,
            source.Kind,
            source.BasePoint,
            direction);
        if (Prompt == CadXLinePromptKind.AngleReferenceSource &&
            _usesReferenceAngle)
        {
            _linearSource = source;
            _hasLinearSource = true;
            Prompt = CadXLinePromptKind.AngleValue;
            return true;
        }
        if (Prompt == CadXLinePromptKind.OffsetSource)
        {
            _linearSource = source;
            _hasLinearSource = true;
            Prompt = _usesThroughOffset
                ? CadXLinePromptKind.OffsetThroughPoint
                : CadXLinePromptKind.OffsetSidePoint;
            return true;
        }

        errorMessage = "The current XLINE prompt does not accept a linear source.";
        return false;
    }

    public bool TryAcceptPoint(CadPoint3D point, out string? errorMessage)
    {
        errorMessage = null;
        if (!IsFinite(point))
        {
            errorMessage = "An XLINE point must contain finite WCS coordinates.";
            return false;
        }

        switch (Prompt)
        {
            case CadXLinePromptKind.FirstPoint:
                _firstPoint = point;
                _hasFirstPoint = true;
                Prompt = CadXLinePromptKind.ThroughPoint;
                return true;
            case CadXLinePromptKind.ThroughPoint:
                if (!_hasFirstPoint ||
                    !CadRayAuthoringSession.TryGetUnitDirection(
                        _firstPoint,
                        point,
                        out CadPoint3D twoPointDirection))
                {
                    errorMessage =
                        "An XLINE first point and through point must be distinct.";
                    return false;
                }
                var twoPoint = new CadXLineDefinition(
                    _firstPoint,
                    twoPointDirection);
                return TryAddDefinition(twoPoint, out errorMessage);
            case CadXLinePromptKind.PlacementPoint:
                if (!CadXLineConstruction.TryCreateThroughPoint(
                        point,
                        _fixedDirection,
                        out CadXLineDefinition placed))
                {
                    errorMessage =
                        "The current XLINE direction cannot be placed at this point.";
                    return false;
                }
                return TryAddDefinition(placed, out errorMessage);
            case CadXLinePromptKind.BisectorVertex:
                if (_definitionCount == MaximumLineCount)
                {
                    errorMessage = LimitMessage();
                    return false;
                }
                _bisectorVertex = point;
                Prompt = CadXLinePromptKind.BisectorFirstRayPoint;
                return true;
            case CadXLinePromptKind.BisectorFirstRayPoint:
                if (!CadRayAuthoringSession.TryGetUnitDirection(
                        _bisectorVertex,
                        point,
                        out _))
                {
                    errorMessage =
                        "The bisector vertex and first ray point must be distinct.";
                    return false;
                }
                _bisectorFirstRayPoint = point;
                Prompt = CadXLinePromptKind.BisectorSecondRayPoint;
                return true;
            case CadXLinePromptKind.BisectorSecondRayPoint:
                if (!CadXLineConstruction.TryCreateBisector(
                        _bisectorVertex,
                        _bisectorFirstRayPoint,
                        point,
                        out CadXLineDefinition bisector))
                {
                    errorMessage =
                        "The bisector rays must be distinct and cannot point in exactly opposite directions.";
                    return false;
                }
                if (!TryAddDefinition(bisector, out errorMessage))
                {
                    return false;
                }
                Prompt = CadXLinePromptKind.BisectorVertex;
                return true;
            case CadXLinePromptKind.OffsetSidePoint:
                if (!_hasLinearSource ||
                    !CadXLineConstruction.TryCreateOffsetAtDistance(
                        ToDefinition(_linearSource),
                        point,
                        _context.Normal,
                        _offsetDistance,
                        out CadXLineDefinition offset))
                {
                    errorMessage =
                        "The offset side point must resolve to one side of a coplanar source.";
                    return false;
                }
                if (!TryAddDefinition(offset, out errorMessage))
                {
                    return false;
                }
                ResetOffsetSource();
                return true;
            case CadXLinePromptKind.OffsetThroughPoint:
                if (!_hasLinearSource ||
                    !CadXLineConstruction.TryCreateOffsetThrough(
                        ToDefinition(_linearSource),
                        point,
                        _context.Normal,
                        out CadXLineDefinition through))
                {
                    errorMessage =
                        "The through point must be coplanar and cannot reproduce the selected source line.";
                    return false;
                }
                if (!TryAddDefinition(through, out errorMessage))
                {
                    return false;
                }
                ResetOffsetSource();
                return true;
            default:
                errorMessage = "The current XLINE prompt does not accept a point.";
                return false;
        }
    }

    /// <summary>
    /// Resolves the construction line that accepting <paramref name="point"/>
    /// would create without mutating prompt state.
    /// </summary>
    public bool TryPreviewPoint(
        CadPoint3D point,
        out CadXLineDefinition definition)
    {
        definition = default;
        if (!IsFinite(point))
        {
            return false;
        }

        return Prompt switch
        {
            CadXLinePromptKind.ThroughPoint when _hasFirstPoint =>
                CadRayAuthoringSession.TryGetUnitDirection(
                    _firstPoint,
                    point,
                    out CadPoint3D twoPointDirection) &&
                CadXLineConstruction.TryCreateThroughPoint(
                    _firstPoint,
                    twoPointDirection,
                    out definition),
            CadXLinePromptKind.PlacementPoint =>
                CadXLineConstruction.TryCreateThroughPoint(
                    point,
                    _fixedDirection,
                    out definition),
            CadXLinePromptKind.BisectorSecondRayPoint =>
                CadXLineConstruction.TryCreateBisector(
                    _bisectorVertex,
                    _bisectorFirstRayPoint,
                    point,
                    out definition),
            CadXLinePromptKind.OffsetSidePoint when _hasLinearSource =>
                CadXLineConstruction.TryCreateOffsetAtDistance(
                    ToDefinition(_linearSource),
                    point,
                    _context.Normal,
                    _offsetDistance,
                    out definition),
            CadXLinePromptKind.OffsetThroughPoint when _hasLinearSource =>
                CadXLineConstruction.TryCreateOffsetThrough(
                    ToDefinition(_linearSource),
                    point,
                    _context.Normal,
                    out definition),
            _ => false,
        };
    }

    public bool TryUndoLastLine()
    {
        if (_definitionCount == 0)
        {
            return false;
        }

        _definitions[--_definitionCount] = default;
        _hasLastAcceptedPoint = _definitionCount > 0;
        _lastAcceptedPoint = _hasLastAcceptedPoint
            ? _definitions[_definitionCount - 1].FirstPoint
            : default;
        ResetPartialPrompt();
        return true;
    }

    public CadXLineDefinition[] CreateDefinitionSnapshot()
    {
        if (_definitionCount == 0)
        {
            throw new InvalidOperationException(
                "At least one XLINE definition is required before completion.");
        }
        return _definitions.AsSpan(0, _definitionCount).ToArray();
    }

    private bool TryAddDefinition(
        CadXLineDefinition definition,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_definitionCount == MaximumLineCount)
        {
            errorMessage = LimitMessage();
            return false;
        }
        EnsureCapacity(_definitionCount + 1);
        _definitions[_definitionCount++] = definition;
        _lastAcceptedPoint = definition.FirstPoint;
        _hasLastAcceptedPoint = true;
        return true;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _definitions.Length)
        {
            return;
        }
        int capacity = Math.Min(
            MaximumLineCount,
            Math.Max(required, checked(_definitions.Length * 2)));
        Array.Resize(ref _definitions, capacity);
    }

    private void ResetOffsetSource()
    {
        _linearSource = default;
        _hasLinearSource = false;
        Prompt = CadXLinePromptKind.OffsetSource;
    }

    private void ResetPartialPrompt()
    {
        switch (Mode)
        {
            case CadXLineAuthoringMode.TwoPoint:
                Prompt = CadXLinePromptKind.ThroughPoint;
                break;
            case CadXLineAuthoringMode.Horizontal:
            case CadXLineAuthoringMode.Vertical:
            case CadXLineAuthoringMode.Angle:
                Prompt = CadXLinePromptKind.PlacementPoint;
                break;
            case CadXLineAuthoringMode.Bisect:
                Prompt = CadXLinePromptKind.BisectorVertex;
                break;
            case CadXLineAuthoringMode.Offset:
                _linearSource = default;
                _hasLinearSource = false;
                Prompt = CadXLinePromptKind.OffsetSource;
                break;
            default:
                throw new InvalidOperationException("Unknown XLINE authoring mode.");
        }
    }

    private static CadXLineDefinition ToDefinition(
        CadXLineLinearSource source) =>
        new(source.BasePoint, source.Direction);

    private string LimitMessage() =>
        $"The XLINE sequence reached its configured limit of {MaximumLineCount} lines.";

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>Bounded host-neutral state for the default two-point XLINE mode.</summary>
/// <remarks>
/// One fixed WCS point is shared by every accepted through point. Directions
/// are normalized immediately. Acceptance is amortized O(1), local Undo is
/// O(1), and immutable command capture is O(L) for L construction lines.
/// </remarks>
public sealed class CadXLineAuthoringSession
{
    public const int DefaultMaximumLineCount = 65_536;

    private CadPoint3D[] _directions;
    private int _lineCount;
    private CadPoint3D _firstPoint;
    private bool _hasFirstPoint;

    public int MaximumLineCount { get; }

    public int PointCount => _lineCount + (_hasFirstPoint ? 1 : 0);

    public int LineCount => _lineCount;

    public bool HasFirstPoint => _hasFirstPoint;

    public CadPoint3D? FirstPoint => _hasFirstPoint ? _firstPoint : null;

    public ReadOnlyMemory<CadPoint3D> Directions =>
        _directions.AsMemory(0, _lineCount);

    public CadXLineAuthoringSession(
        int maximumLineCount = DefaultMaximumLineCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineCount);
        MaximumLineCount = maximumLineCount;
        _directions = new CadPoint3D[Math.Min(maximumLineCount, 16)];
    }

    public bool TryAcceptPoint(CadPoint3D point, out string? errorMessage)
    {
        errorMessage = null;
        if (!IsFinite(point))
        {
            errorMessage = "An XLINE point must contain finite WCS coordinates.";
            return false;
        }
        if (!_hasFirstPoint)
        {
            _firstPoint = point;
            _hasFirstPoint = true;
            return true;
        }
        if (_lineCount == MaximumLineCount)
        {
            errorMessage =
                $"The XLINE sequence reached its configured limit of {MaximumLineCount} lines.";
            return false;
        }
        if (!CadRayAuthoringSession.TryGetUnitDirection(
                _firstPoint,
                point,
                out CadPoint3D direction))
        {
            errorMessage =
                "An XLINE first point and through point must be distinct.";
            return false;
        }

        EnsureCapacity(_lineCount + 1);
        _directions[_lineCount++] = direction;
        return true;
    }

    public bool TryUndoLastLine()
    {
        if (_lineCount == 0)
        {
            return false;
        }
        _directions[--_lineCount] = default;
        return true;
    }

    public CadPoint3D[] CreateDirectionSnapshot()
    {
        if (_lineCount == 0)
        {
            throw new InvalidOperationException(
                "At least one XLINE through point is required before completion.");
        }
        return _directions.AsSpan(0, _lineCount).ToArray();
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _directions.Length)
        {
            return;
        }
        int capacity = Math.Min(
            MaximumLineCount,
            Math.Max(required, checked(_directions.Length * 2)));
        Array.Resize(ref _directions, capacity);
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>Adds separate XLINE entities as one history action.</summary>
/// <remarks>
/// CLAYER, CECOLOR, CELTYPE, CELTSCALE, and CELWEIGHT are captured atomically
/// on first Apply. Apply/Undo/Redo are O(L) and retained storage is O(L).
/// </remarks>
public sealed class CadAddXLineSequenceCommand : CadEditCommand
{
    private readonly CadPoint3D _firstPoint;
    private readonly CadPoint3D[] _firstPoints;
    private readonly CadPoint3D[] _directions;
    private readonly ulong[] _currentHandles;
    private XLine[]? _lines;

    public CadPoint3D FirstPoint => _firstPoint;

    public ReadOnlyMemory<CadPoint3D> FirstPoints => _firstPoints;

    public ReadOnlyMemory<CadPoint3D> Directions => _directions;

    public ReadOnlyMemory<ulong> CurrentHandles => _currentHandles;

    public ReadOnlyMemory<XLine> Lines =>
        _lines ?? ReadOnlyMemory<XLine>.Empty;

    public int LineCount => _directions.Length;

    public int MaximumLineCount { get; }

    public CadAddXLineSequenceCommand(
        CadPoint3D firstPoint,
        ReadOnlySpan<CadPoint3D> directions,
        string description = "XLINE",
        int maximumLineCount = CadXLineAuthoringSession.DefaultMaximumLineCount)
        : base(description)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineCount);
        if (!IsFinite(firstPoint))
        {
            throw new ArgumentException(
                "The XLINE first point must contain finite WCS coordinates.",
                nameof(firstPoint));
        }
        if (directions.IsEmpty)
        {
            throw new ArgumentException(
                "An XLINE sequence requires at least one direction.",
                nameof(directions));
        }
        if (directions.Length > maximumLineCount)
        {
            throw new ArgumentException(
                $"The XLINE sequence exceeds the configured limit of {maximumLineCount} lines.",
                nameof(directions));
        }

        _firstPoint = firstPoint;
        _firstPoints = new CadPoint3D[directions.Length];
        _directions = new CadPoint3D[directions.Length];
        for (int i = 0; i < directions.Length; i++)
        {
            _firstPoints[i] = firstPoint;
            if (!CadRayAuthoringSession.TryNormalizeDirection(
                    directions[i],
                    out _directions[i]))
            {
                throw new ArgumentException(
                    "Every XLINE direction must be a finite nonzero WCS vector.",
                    nameof(directions));
            }
        }
        MaximumLineCount = maximumLineCount;
        _currentHandles = new ulong[LineCount];
    }

    public CadAddXLineSequenceCommand(
        ReadOnlySpan<CadXLineDefinition> definitions,
        string description = "XLINE",
        int maximumLineCount = CadXLineAuthoringSession.DefaultMaximumLineCount)
        : base(description)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineCount);
        if (definitions.IsEmpty)
        {
            throw new ArgumentException(
                "An XLINE sequence requires at least one definition.",
                nameof(definitions));
        }
        if (definitions.Length > maximumLineCount)
        {
            throw new ArgumentException(
                $"The XLINE sequence exceeds the configured limit of {maximumLineCount} lines.",
                nameof(definitions));
        }

        _firstPoint = definitions[0].FirstPoint;
        _firstPoints = new CadPoint3D[definitions.Length];
        _directions = new CadPoint3D[definitions.Length];
        for (int i = 0; i < definitions.Length; i++)
        {
            CadXLineDefinition definition = definitions[i];
            if (!IsFinite(definition.FirstPoint) ||
                !CadRayAuthoringSession.TryNormalizeDirection(
                    definition.Direction,
                    out CadPoint3D normalized))
            {
                throw new ArgumentException(
                    "Every XLINE definition requires a finite point and nonzero finite direction.",
                    nameof(definitions));
            }
            _firstPoints[i] = definition.FirstPoint;
            _directions[i] = normalized;
        }
        MaximumLineCount = maximumLineCount;
        _currentHandles = new ulong[LineCount];
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        XLine[] lines;
        if (isRedo)
        {
            lines = _lines ?? throw new InvalidOperationException(
                "The XLINE command has not been applied.");
        }
        else
        {
            lines = CreateLines(document);
            _lines = lines;
        }

        foreach (XLine line in lines)
        {
            ValidateDetached(line);
        }
        document.Entities.AddRange(lines);
        for (int i = 0; i < lines.Length; i++)
        {
            _currentHandles[i] = lines[i].Handle;
        }
    }

    internal override void Revert(CadDocument document)
    {
        XLine[] lines = _lines ?? throw new InvalidOperationException(
            "The XLINE command has not been applied.");
        ValidateModelSpaceEntities(document, lines);
        if (!document.Entities.TryRemoveRange(lines))
        {
            throw new InvalidOperationException(
                "The XLINE sequence removal was cancelled before mutation.");
        }
        Array.Clear(_currentHandles);
    }

    private XLine[] CreateLines(CadDocument document)
    {
        Layer layer = document.Header.CurrentLayer;
        if (HasLayerFlag(layer, LayerFlags.Locked))
        {
            throw new InvalidOperationException(
                $"Current layer '{layer.Name}' is locked and cannot receive XLINE entities.");
        }
        double lineTypeScale = document.Header.CurrentEntityLinetypeScale;
        if (!double.IsFinite(lineTypeScale) || lineTypeScale <= 0.0)
        {
            throw new InvalidOperationException(
                "Current CELTSCALE must be finite and positive before creating XLINE entities.");
        }

        ACadSharp.Color color = document.Header.CurrentEntityColor;
        LineType lineType = document.Header.CurrentLineType;
        LineWeightType lineWeight = document.Header.CurrentEntityLineWeight;
        var lines = new XLine[LineCount];
        for (int i = 0; i < lines.Length; i++)
        {
            CadPoint3D firstPoint = _firstPoints[i];
            CadPoint3D direction = _directions[i];
            lines[i] = new XLine
            {
                FirstPoint = new XYZ(
                    firstPoint.X,
                    firstPoint.Y,
                    firstPoint.Z),
                Direction = new XYZ(direction.X, direction.Y, direction.Z),
                Layer = layer,
                Color = color,
                LineType = lineType,
                LineTypeScale = lineTypeScale,
                LineWeight = lineWeight,
            };
        }
        return lines;
    }

    private static void ValidateDetached(XLine line)
    {
        if (line.Owner is not null || line.Document is not null || line.Handle != 0)
        {
            throw new InvalidOperationException(
                "A retained XLINE entity is not detached and cannot be added.");
        }
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

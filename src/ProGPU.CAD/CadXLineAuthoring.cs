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
        if (!double.IsFinite(side) || side == 0.0)
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

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace ProGPU.CAD;

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

/// <summary>Adds separate common-point XLINE entities as one history action.</summary>
/// <remarks>
/// CLAYER, CECOLOR, CELTYPE, CELTSCALE, and CELWEIGHT are captured atomically
/// on first Apply. Apply/Undo/Redo are O(L) and retained storage is O(L).
/// </remarks>
public sealed class CadAddXLineSequenceCommand : CadEditCommand
{
    private readonly CadPoint3D _firstPoint;
    private readonly CadPoint3D[] _directions;
    private readonly ulong[] _currentHandles;
    private XLine[]? _lines;

    public CadPoint3D FirstPoint => _firstPoint;

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
        _directions = new CadPoint3D[directions.Length];
        for (int i = 0; i < directions.Length; i++)
        {
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
        var first = new XYZ(_firstPoint.X, _firstPoint.Y, _firstPoint.Z);
        var lines = new XLine[LineCount];
        for (int i = 0; i < lines.Length; i++)
        {
            CadPoint3D direction = _directions[i];
            lines[i] = new XLine
            {
                FirstPoint = first,
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

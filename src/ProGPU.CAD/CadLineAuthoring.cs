using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace ProGPU.CAD;

/// <summary>
/// Bounded host-neutral state for one contiguous LINE point-acquisition sequence.
/// </summary>
/// <remarks>
/// Accepted points are retained in a geometrically growing buffer. Pointer motion
/// does not touch this state, accepting a point is amortized O(1), Undo is O(1),
/// and the final immutable point snapshot is O(S) for S segments.
/// </remarks>
public sealed class CadLineAuthoringSession
{
    public const int DefaultMaximumSegmentCount = 65_536;

    private CadPoint3D[] _points;
    private int _pointCount;

    public int MaximumSegmentCount { get; }

    public int PointCount => _pointCount;

    public int SegmentCount => Math.Max(0, _pointCount - 1);

    public bool HasFirstPoint => _pointCount > 0;

    public CadPoint3D? FirstPoint =>
        _pointCount == 0 ? null : _points[0];

    public CadPoint3D? CurrentPoint =>
        _pointCount == 0 ? null : _points[_pointCount - 1];

    public CadPoint3D? PreviousSegmentDirection =>
        _pointCount < 2
            ? null
            : _points[_pointCount - 1] - _points[_pointCount - 2];

    public ReadOnlyMemory<CadPoint3D> Points =>
        _points.AsMemory(0, _pointCount);

    public bool CanClose =>
        SegmentCount >= 2 && _points[_pointCount - 1] != _points[0];

    public CadLineAuthoringSession(
        int maximumSegmentCount = DefaultMaximumSegmentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegmentCount);
        MaximumSegmentCount = maximumSegmentCount;
        _points = new CadPoint3D[Math.Min(maximumSegmentCount + 1, 16)];
    }

    public bool TryAcceptPoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!IsFinite(point))
        {
            errorMessage = "A LINE point must contain finite WCS coordinates.";
            return false;
        }
        if (_pointCount > 0 && _points[_pointCount - 1] == point)
        {
            errorMessage = "A LINE segment must have distinct endpoints.";
            return false;
        }
        if (SegmentCount == MaximumSegmentCount)
        {
            errorMessage =
                $"The LINE sequence reached its configured limit of {MaximumSegmentCount} segments.";
            return false;
        }

        EnsureCapacity(_pointCount + 1);
        _points[_pointCount++] = point;
        return true;
    }

    /// <summary>Removes only the latest segment while retaining its start point.</summary>
    public bool TryUndoLastSegment()
    {
        if (_pointCount < 2)
        {
            return false;
        }

        _pointCount--;
        _points[_pointCount] = default;
        return true;
    }

    /// <summary>
    /// Creates the immutable command input. Closing adds one final segment to
    /// the first point and requires at least two already accepted segments.
    /// </summary>
    public CadPoint3D[] CreatePointSnapshot(bool close)
    {
        if (SegmentCount == 0)
        {
            throw new InvalidOperationException(
                "At least one LINE segment is required before completion.");
        }
        if (close && !CanClose)
        {
            throw new InvalidOperationException(
                "Close requires at least two segments and a distinct current point.");
        }

        int count = checked(_pointCount + (close ? 1 : 0));
        var snapshot = new CadPoint3D[count];
        _points.AsSpan(0, _pointCount).CopyTo(snapshot);
        if (close)
        {
            snapshot[^1] = _points[0];
        }
        return snapshot;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _points.Length)
        {
            return;
        }

        int maximumPointCount = checked(MaximumSegmentCount + 1);
        int capacity = Math.Min(
            maximumPointCount,
            Math.Max(required, checked(_points.Length * 2)));
        Array.Resize(ref _points, capacity);
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>
/// Adds one contiguous LINE sequence as separate ACadSharp LINE entities and
/// one reversible document-history operation.
/// </summary>
/// <remarks>
/// Current CLAYER, CECOLOR, CELTYPE, CELTSCALE, and CELWEIGHT values are captured
/// atomically on first execution. Apply, Undo, and Redo are O(S) for S segments;
/// retained command storage is O(S). No render/native boundary is involved.
/// </remarks>
public sealed class CadAddLineSequenceCommand : CadEditCommand
{
    public const int DefaultMaximumSegmentCount =
        CadLineAuthoringSession.DefaultMaximumSegmentCount;

    private readonly CadPoint3D[] _points;
    private readonly ulong[] _currentHandles;
    private Line[]? _lines;

    public ReadOnlyMemory<CadPoint3D> Points => _points;

    public ReadOnlyMemory<ulong> CurrentHandles => _currentHandles;

    public ReadOnlyMemory<Line> Lines =>
        _lines ?? ReadOnlyMemory<Line>.Empty;

    public int SegmentCount => _points.Length - 1;

    public int MaximumSegmentCount { get; }

    public CadAddLineSequenceCommand(
        ReadOnlySpan<CadPoint3D> points,
        string description = "LINE",
        int maximumSegmentCount = DefaultMaximumSegmentCount)
        : base(description)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegmentCount);
        if (points.Length < 2)
        {
            throw new ArgumentException(
                "A LINE sequence requires at least two points.",
                nameof(points));
        }
        if (points.Length - 1 > maximumSegmentCount)
        {
            throw new ArgumentException(
                $"The LINE sequence exceeds the configured limit of {maximumSegmentCount} segments.",
                nameof(points));
        }

        _points = points.ToArray();
        for (int i = 0; i < _points.Length; i++)
        {
            if (!IsFinite(_points[i]))
            {
                throw new ArgumentException(
                    "Every LINE point must contain finite WCS coordinates.",
                    nameof(points));
            }
            if (i > 0 && _points[i] == _points[i - 1])
            {
                throw new ArgumentException(
                    "Every LINE segment must have distinct endpoints.",
                    nameof(points));
            }
        }

        MaximumSegmentCount = maximumSegmentCount;
        _currentHandles = new ulong[SegmentCount];
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Line[] lines;
        if (isRedo)
        {
            lines = _lines ?? throw new InvalidOperationException(
                "The LINE command has not been applied.");
        }
        else
        {
            lines = CreateLines(document);
            _lines = lines;
        }

        foreach (Line line in lines)
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
        Line[] lines = _lines ?? throw new InvalidOperationException(
            "The LINE command has not been applied.");
        ValidateModelSpaceEntities(document, lines);
        if (!document.Entities.TryRemoveRange(lines))
        {
            throw new InvalidOperationException(
                "The LINE sequence removal was cancelled before mutation.");
        }
        Array.Clear(_currentHandles);
    }

    private Line[] CreateLines(CadDocument document)
    {
        Layer layer = document.Header.CurrentLayer;
        if (HasLayerFlag(layer, LayerFlags.Locked))
        {
            throw new InvalidOperationException(
                $"Current layer '{layer.Name}' is locked and cannot receive LINE entities.");
        }

        ACadSharp.Color color = document.Header.CurrentEntityColor;
        LineType lineType = document.Header.CurrentLineType;
        double lineTypeScale = document.Header.CurrentEntityLinetypeScale;
        LineWeightType lineWeight = document.Header.CurrentEntityLineWeight;
        if (!double.IsFinite(lineTypeScale) || lineTypeScale <= 0.0)
        {
            throw new InvalidOperationException(
                "Current CELTSCALE must be finite and positive before creating LINE entities.");
        }

        var lines = new Line[SegmentCount];
        for (int i = 0; i < lines.Length; i++)
        {
            CadPoint3D start = _points[i];
            CadPoint3D end = _points[i + 1];
            lines[i] = new Line(
                new XYZ(start.X, start.Y, start.Z),
                new XYZ(end.X, end.Y, end.Z))
            {
                Layer = layer,
                Color = color,
                LineType = lineType,
                LineTypeScale = lineTypeScale,
                LineWeight = lineWeight,
            };
        }
        return lines;
    }

    private static void ValidateDetached(Line line)
    {
        if (line.Owner is not null ||
            line.Document is not null ||
            line.Handle != 0)
        {
            throw new InvalidOperationException(
                "A retained LINE entity is not detached and cannot be added.");
        }
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

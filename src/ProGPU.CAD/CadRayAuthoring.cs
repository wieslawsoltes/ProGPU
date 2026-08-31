using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace ProGPU.CAD;

/// <summary>
/// Bounded host-neutral state for one AutoCAD-compatible RAY acquisition sequence.
/// </summary>
/// <remarks>
/// The first accepted WCS point remains the start of every ray. Later accepted
/// points are reduced immediately to unit WCS directions in a geometrically
/// growing buffer. Acceptance is amortized O(1), Undo is O(1), and creating the
/// immutable command input is O(R) for R rays.
/// </remarks>
public sealed class CadRayAuthoringSession
{
    public const int DefaultMaximumRayCount = 65_536;

    private CadPoint3D[] _directions;
    private int _rayCount;
    private CadPoint3D _startPoint;
    private bool _hasStartPoint;

    public int MaximumRayCount { get; }

    public int PointCount => _rayCount + (_hasStartPoint ? 1 : 0);

    public int RayCount => _rayCount;

    public bool HasStartPoint => _hasStartPoint;

    public CadPoint3D? StartPoint =>
        _hasStartPoint ? _startPoint : null;

    public CadPoint3D? CurrentDirection =>
        _rayCount == 0 ? null : _directions[_rayCount - 1];

    public ReadOnlyMemory<CadPoint3D> Directions =>
        _directions.AsMemory(0, _rayCount);

    public CadRayAuthoringSession(
        int maximumRayCount = DefaultMaximumRayCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRayCount);
        MaximumRayCount = maximumRayCount;
        _directions = new CadPoint3D[Math.Min(maximumRayCount, 16)];
    }

    public bool TryAcceptPoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!IsFinite(point))
        {
            errorMessage = "A RAY point must contain finite WCS coordinates.";
            return false;
        }

        if (!_hasStartPoint)
        {
            _startPoint = point;
            _hasStartPoint = true;
            return true;
        }

        if (_rayCount == MaximumRayCount)
        {
            errorMessage =
                $"The RAY sequence reached its configured limit of {MaximumRayCount} rays.";
            return false;
        }

        if (!TryGetUnitDirection(_startPoint, point, out CadPoint3D direction))
        {
            errorMessage = "A RAY start point and through point must be distinct.";
            return false;
        }

        EnsureCapacity(_rayCount + 1);
        _directions[_rayCount++] = direction;
        return true;
    }

    /// <summary>Removes only the latest ray while retaining the common start point.</summary>
    public bool TryUndoLastRay()
    {
        if (_rayCount == 0)
        {
            return false;
        }

        _directions[--_rayCount] = default;
        return true;
    }

    public CadPoint3D[] CreateDirectionSnapshot()
    {
        if (_rayCount == 0)
        {
            throw new InvalidOperationException(
                "At least one RAY through point is required before completion.");
        }

        return _directions.AsSpan(0, _rayCount).ToArray();
    }

    internal static bool TryNormalizeDirection(
        CadPoint3D direction,
        out CadPoint3D unitDirection)
    {
        unitDirection = default;
        if (!IsFinite(direction))
        {
            return false;
        }

        double scale = Math.Max(
            Math.Abs(direction.X),
            Math.Max(Math.Abs(direction.Y), Math.Abs(direction.Z)));
        if (!(scale > 0.0) || !double.IsFinite(scale))
        {
            return false;
        }

        double x = direction.X / scale;
        double y = direction.Y / scale;
        double z = direction.Z / scale;
        double length = Math.Sqrt((x * x) + (y * y) + (z * z));
        if (!(length > 0.0) || !double.IsFinite(length))
        {
            return false;
        }

        unitDirection = new CadPoint3D(x / length, y / length, z / length);
        return IsFinite(unitDirection);
    }

    internal static bool TryGetUnitDirection(
        CadPoint3D startPoint,
        CadPoint3D throughPoint,
        out CadPoint3D unitDirection)
    {
        unitDirection = default;
        if (!IsFinite(startPoint) || !IsFinite(throughPoint) ||
            startPoint == throughPoint)
        {
            return false;
        }

        var difference = new CadPoint3D(
            throughPoint.X - startPoint.X,
            throughPoint.Y - startPoint.Y,
            throughPoint.Z - startPoint.Z);
        if (TryNormalizeDirection(difference, out unitDirection))
        {
            return true;
        }

        // Opposite finite endpoints can overflow during direct subtraction.
        // Scale both endpoints before subtracting to retain their direction.
        double coordinateScale = Math.Max(
            Math.Max(Math.Abs(startPoint.X), Math.Abs(throughPoint.X)),
            Math.Max(
                Math.Max(Math.Abs(startPoint.Y), Math.Abs(throughPoint.Y)),
                Math.Max(Math.Abs(startPoint.Z), Math.Abs(throughPoint.Z))));
        if (!(coordinateScale > 0.0) || !double.IsFinite(coordinateScale))
        {
            return false;
        }

        difference = new CadPoint3D(
            (throughPoint.X / coordinateScale) - (startPoint.X / coordinateScale),
            (throughPoint.Y / coordinateScale) - (startPoint.Y / coordinateScale),
            (throughPoint.Z / coordinateScale) - (startPoint.Z / coordinateScale));
        return TryNormalizeDirection(difference, out unitDirection);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _directions.Length)
        {
            return;
        }

        int capacity = Math.Min(
            MaximumRayCount,
            Math.Max(required, checked(_directions.Length * 2)));
        Array.Resize(ref _directions, capacity);
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>
/// Adds one common-start RAY sequence as separate ACadSharp RAY entities and
/// one reversible document-history operation.
/// </summary>
/// <remarks>
/// Current CLAYER, CECOLOR, CELTYPE, CELTSCALE, and CELWEIGHT values are captured
/// atomically on first execution. Apply, Undo, and Redo are O(R) for R rays;
/// retained command storage is O(R). No managed/native crossing is introduced.
/// </remarks>
public sealed class CadAddRaySequenceCommand : CadEditCommand
{
    public const int DefaultMaximumRayCount =
        CadRayAuthoringSession.DefaultMaximumRayCount;

    private readonly CadPoint3D _startPoint;
    private readonly CadPoint3D[] _directions;
    private readonly ulong[] _currentHandles;
    private Ray[]? _rays;

    public CadPoint3D StartPoint => _startPoint;

    public ReadOnlyMemory<CadPoint3D> Directions => _directions;

    public ReadOnlyMemory<ulong> CurrentHandles => _currentHandles;

    public ReadOnlyMemory<Ray> Rays =>
        _rays ?? ReadOnlyMemory<Ray>.Empty;

    public int RayCount => _directions.Length;

    public int MaximumRayCount { get; }

    public CadAddRaySequenceCommand(
        CadPoint3D startPoint,
        ReadOnlySpan<CadPoint3D> directions,
        string description = "RAY",
        int maximumRayCount = DefaultMaximumRayCount)
        : base(description)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRayCount);
        if (!IsFinite(startPoint))
        {
            throw new ArgumentException(
                "The RAY start point must contain finite WCS coordinates.",
                nameof(startPoint));
        }
        if (directions.IsEmpty)
        {
            throw new ArgumentException(
                "A RAY sequence requires at least one direction.",
                nameof(directions));
        }
        if (directions.Length > maximumRayCount)
        {
            throw new ArgumentException(
                $"The RAY sequence exceeds the configured limit of {maximumRayCount} rays.",
                nameof(directions));
        }

        _startPoint = startPoint;
        _directions = new CadPoint3D[directions.Length];
        for (int i = 0; i < directions.Length; i++)
        {
            if (!CadRayAuthoringSession.TryNormalizeDirection(
                    directions[i],
                    out _directions[i]))
            {
                throw new ArgumentException(
                    "Every RAY direction must be a finite nonzero WCS vector.",
                    nameof(directions));
            }
        }

        MaximumRayCount = maximumRayCount;
        _currentHandles = new ulong[RayCount];
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Ray[] rays;
        if (isRedo)
        {
            rays = _rays ?? throw new InvalidOperationException(
                "The RAY command has not been applied.");
        }
        else
        {
            rays = CreateRays(document);
            _rays = rays;
        }

        foreach (Ray ray in rays)
        {
            ValidateDetached(ray);
        }
        document.Entities.AddRange(rays);
        for (int i = 0; i < rays.Length; i++)
        {
            _currentHandles[i] = rays[i].Handle;
        }
    }

    internal override void Revert(CadDocument document)
    {
        Ray[] rays = _rays ?? throw new InvalidOperationException(
            "The RAY command has not been applied.");
        ValidateModelSpaceEntities(document, rays);
        if (!document.Entities.TryRemoveRange(rays))
        {
            throw new InvalidOperationException(
                "The RAY sequence removal was cancelled before mutation.");
        }
        Array.Clear(_currentHandles);
    }

    private Ray[] CreateRays(CadDocument document)
    {
        Layer layer = document.Header.CurrentLayer;
        if (HasLayerFlag(layer, LayerFlags.Locked))
        {
            throw new InvalidOperationException(
                $"Current layer '{layer.Name}' is locked and cannot receive RAY entities.");
        }

        ACadSharp.Color color = document.Header.CurrentEntityColor;
        LineType lineType = document.Header.CurrentLineType;
        double lineTypeScale = document.Header.CurrentEntityLinetypeScale;
        LineWeightType lineWeight = document.Header.CurrentEntityLineWeight;
        if (!double.IsFinite(lineTypeScale) || lineTypeScale <= 0.0)
        {
            throw new InvalidOperationException(
                "Current CELTSCALE must be finite and positive before creating RAY entities.");
        }

        var rays = new Ray[RayCount];
        var start = new XYZ(_startPoint.X, _startPoint.Y, _startPoint.Z);
        for (int i = 0; i < rays.Length; i++)
        {
            CadPoint3D direction = _directions[i];
            rays[i] = new Ray
            {
                StartPoint = start,
                Direction = new XYZ(direction.X, direction.Y, direction.Z),
                Layer = layer,
                Color = color,
                LineType = lineType,
                LineTypeScale = lineTypeScale,
                LineWeight = lineWeight,
            };
        }
        return rays;
    }

    private static void ValidateDetached(Ray ray)
    {
        if (ray.Owner is not null ||
            ray.Document is not null ||
            ray.Handle != 0)
        {
            throw new InvalidOperationException(
                "A retained RAY entity is not detached and cannot be added.");
        }
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

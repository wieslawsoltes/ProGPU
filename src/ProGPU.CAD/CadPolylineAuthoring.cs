using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace ProGPU.CAD;

/// <summary>The next-segment construction used by a 2D PLINE session.</summary>
public enum CadPolylineAuthoringMode : byte
{
    Line = 0,
    TangentArc = 1,
}

/// <summary>Immutable planar centerline input for one lightweight polyline.</summary>
public sealed class CadPolylineAuthoringSnapshot
{
    private readonly CadPoint3D[] _points;
    private readonly double[] _bulges;

    public ReadOnlyMemory<CadPoint3D> Points => _points;

    /// <summary>
    /// One bulge per vertex. The last value owns the closing segment only when
    /// <see cref="IsClosed"/> is true and is otherwise zero.
    /// </summary>
    public ReadOnlyMemory<double> Bulges => _bulges;

    public bool IsClosed { get; }

    public int SegmentCount => IsClosed ? _points.Length : _points.Length - 1;

    public double Elevation => _points[0].Z;

    public CadPolylineAuthoringSnapshot(
        ReadOnlySpan<CadPoint3D> points,
        ReadOnlySpan<double> bulges,
        bool isClosed)
    {
        if (points.Length < 2)
        {
            throw new ArgumentException(
                "A lightweight polyline requires at least two vertices.",
                nameof(points));
        }
        if (bulges.Length != points.Length)
        {
            throw new ArgumentException(
                "A lightweight polyline requires one bulge slot per vertex.",
                nameof(bulges));
        }

        double elevation = points[0].Z;
        for (int i = 0; i < points.Length; i++)
        {
            CadPoint3D point = points[i];
            if (!IsFinite(point) || point.Z != elevation)
            {
                throw new ArgumentException(
                    "Every lightweight-polyline vertex must be finite and lie on one WCS-Z plane.",
                    nameof(points));
            }
            if (!double.IsFinite(bulges[i]))
            {
                throw new ArgumentException(
                    "Every lightweight-polyline bulge must be finite.",
                    nameof(bulges));
            }
            if (i > 0 && point == points[i - 1])
            {
                throw new ArgumentException(
                    "Every lightweight-polyline segment must have distinct endpoints.",
                    nameof(points));
            }
        }
        if (!isClosed && bulges[^1] != 0.0)
        {
            throw new ArgumentException(
                "An open lightweight polyline cannot carry a terminal closing bulge.",
                nameof(bulges));
        }
        if (isClosed && points[^1] == points[0])
        {
            throw new ArgumentException(
                "A closed lightweight polyline stores closure as a flag, not a duplicate vertex.",
                nameof(points));
        }

        _points = points.ToArray();
        _bulges = bulges.ToArray();
        IsClosed = isClosed;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>
/// Bounded host-neutral state for one planar lightweight-polyline command.
/// </summary>
/// <remarks>
/// Accepted WCS points and analytic bulges use geometrically growing arrays.
/// Acceptance is amortized O(1), segment Undo is O(1), and completion is O(S)
/// for S segments. Tangent arcs retain one exact DXF bulge, never tessellation.
/// </remarks>
public sealed class CadPolylineAuthoringSession
{
    public const int DefaultMaximumSegmentCount = 65_536;

    private const double MaximumArcAngle = Math.Tau - 1e-12;
    private CadPoint3D[] _points;
    private double[] _bulges;
    private int _pointCount;

    public int MaximumSegmentCount { get; }

    public CadPolylineAuthoringMode Mode { get; set; }

    public int PointCount => _pointCount;

    public int SegmentCount => Math.Max(0, _pointCount - 1);

    public bool HasFirstPoint => _pointCount > 0;

    public bool CanClose
    {
        get
        {
            if (SegmentCount < 2 ||
                _points[_pointCount - 1] == _points[0] ||
                SegmentCount == MaximumSegmentCount)
            {
                return false;
            }
            return Mode == CadPolylineAuthoringMode.Line ||
                (TryGetPreviousSegmentTangent(out CadPoint3D tangent) &&
                 TryGetTangentBulge(
                     _points[_pointCount - 1],
                     _points[0],
                     tangent,
                     out _));
        }
    }

    public CadPoint3D? FirstPoint =>
        _pointCount == 0 ? null : _points[0];

    public CadPoint3D? CurrentPoint =>
        _pointCount == 0 ? null : _points[_pointCount - 1];

    /// <summary>
    /// The actual end tangent of the latest accepted line or arc segment.
    /// </summary>
    public CadPoint3D? PreviousSegmentDirection =>
        TryGetPreviousSegmentTangent(out CadPoint3D tangent)
            ? tangent
            : null;

    public ReadOnlyMemory<CadPoint3D> Points =>
        _points.AsMemory(0, _pointCount);

    public ReadOnlyMemory<double> Bulges =>
        _bulges.AsMemory(0, _pointCount);

    public CadPolylineAuthoringSession(
        int maximumSegmentCount = DefaultMaximumSegmentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegmentCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumSegmentCount,
            DefaultMaximumSegmentCount);
        MaximumSegmentCount = maximumSegmentCount;
        int initialCapacity = Math.Min(maximumSegmentCount + 1, 16);
        _points = new CadPoint3D[initialCapacity];
        _bulges = new double[initialCapacity];
    }

    public bool TryAcceptPoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        if (_pointCount == 0 || Mode == CadPolylineAuthoringMode.Line)
        {
            return TryAcceptLinePoint(point, out errorMessage);
        }

        return TryAcceptTangentArcPoint(point, out errorMessage);
    }

    /// <summary>
    /// Resolves the next segment without mutating accepted state. This is the
    /// allocation-free transient-preview seam used by shared hosts.
    /// </summary>
    public bool TryGetPendingBulge(CadPoint3D point, out double bulge)
    {
        bulge = 0.0;
        if (_pointCount == 0 || !IsFinite(point) ||
            point.Z != _points[0].Z || point == _points[_pointCount - 1])
        {
            return false;
        }
        if (Mode == CadPolylineAuthoringMode.Line)
        {
            return true;
        }
        return TryGetPreviousSegmentTangent(out CadPoint3D tangent) &&
            TryGetTangentBulge(_points[_pointCount - 1], point, tangent, out bulge);
    }

    public bool TryAcceptLinePoint(
        CadPoint3D point,
        out string? errorMessage) =>
        TryAccept(point, bulge: 0.0, out errorMessage);

    /// <summary>
    /// Adds an analytic circular arc whose starting tangent continues the
    /// actual tangent of the preceding accepted segment.
    /// </summary>
    public bool TryAcceptTangentArcPoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_pointCount < 2)
        {
            errorMessage =
                "A tangent PLINE arc requires a preceding accepted segment.";
            return false;
        }
        if (!ValidateNextPoint(point, out errorMessage))
        {
            return false;
        }
        if (!TryGetPreviousSegmentTangent(out CadPoint3D tangent) ||
            !TryGetTangentBulge(
                _points[_pointCount - 1],
                point,
                tangent,
                out double bulge))
        {
            errorMessage =
                "The endpoint does not define a finite non-degenerate arc from the previous segment tangent.";
            return false;
        }

        return TryAccept(point, bulge, out errorMessage);
    }

    /// <summary>
    /// Adds an analytic arc with an explicit signed included angle. Positive
    /// angles are counterclockwise and negative angles are clockwise.
    /// </summary>
    public bool TryAcceptArcPoint(
        CadPoint3D point,
        double includedAngleRadians,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_pointCount == 0)
        {
            errorMessage = "Accept the first PLINE point before adding an arc.";
            return false;
        }
        if (!double.IsFinite(includedAngleRadians) ||
            includedAngleRadians == 0.0 ||
            Math.Abs(includedAngleRadians) >= MaximumArcAngle)
        {
            errorMessage =
                "A PLINE arc angle must be finite, nonzero, and less than one complete turn.";
            return false;
        }

        double bulge = Math.Tan(includedAngleRadians * 0.25);
        if (!double.IsFinite(bulge) || bulge == 0.0)
        {
            errorMessage = "The PLINE arc angle does not produce a finite bulge.";
            return false;
        }
        return TryAccept(point, bulge, out errorMessage);
    }

    /// <summary>Removes only the latest segment while retaining its start.</summary>
    public bool TryUndoLastSegment()
    {
        if (_pointCount < 2)
        {
            return false;
        }

        _pointCount--;
        _points[_pointCount] = default;
        _bulges[_pointCount - 1] = 0.0;
        _bulges[_pointCount] = 0.0;
        return true;
    }

    public bool TryCreateSnapshot(
        bool close,
        out CadPolylineAuthoringSnapshot? snapshot,
        out string? errorMessage)
    {
        snapshot = null;
        errorMessage = null;
        if (SegmentCount == 0)
        {
            errorMessage =
                "At least one PLINE segment is required before completion.";
            return false;
        }
        if (close && !CanClose)
        {
            errorMessage =
                "Close requires at least two accepted PLINE segments within the configured limit.";
            return false;
        }

        var points = new CadPoint3D[_pointCount];
        var bulges = new double[_pointCount];
        _points.AsSpan(0, _pointCount).CopyTo(points);
        _bulges.AsSpan(0, _pointCount).CopyTo(bulges);
        if (close && Mode == CadPolylineAuthoringMode.TangentArc)
        {
            if (!TryGetPreviousSegmentTangent(out CadPoint3D tangent) ||
                !TryGetTangentBulge(
                    points[^1],
                    points[0],
                    tangent,
                    out bulges[^1]))
            {
                errorMessage =
                    "The first point does not define a finite tangent closing arc.";
                return false;
            }
        }

        snapshot = new CadPolylineAuthoringSnapshot(points, bulges, close);
        return true;
    }

    private bool TryAccept(
        CadPoint3D point,
        double bulge,
        out string? errorMessage)
    {
        if (!ValidateNextPoint(point, out errorMessage))
        {
            return false;
        }
        if (!double.IsFinite(bulge))
        {
            errorMessage = "A PLINE segment bulge must be finite.";
            return false;
        }

        EnsureCapacity(_pointCount + 1);
        if (_pointCount > 0)
        {
            _bulges[_pointCount - 1] = bulge;
        }
        _points[_pointCount++] = point;
        _bulges[_pointCount - 1] = 0.0;
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
            errorMessage = "A PLINE point must contain finite WCS coordinates.";
            return false;
        }
        if (_pointCount > 0 && point.Z != _points[0].Z)
        {
            errorMessage =
                "A lightweight PLINE point must remain on the first point's WCS-Z plane.";
            return false;
        }
        if (_pointCount > 0 && point == _points[_pointCount - 1])
        {
            errorMessage = "A PLINE segment must have distinct endpoints.";
            return false;
        }
        if (SegmentCount == MaximumSegmentCount)
        {
            errorMessage =
                $"The PLINE reached its configured limit of {MaximumSegmentCount} segments.";
            return false;
        }
        return true;
    }

    private bool TryGetPreviousSegmentTangent(out CadPoint3D tangent)
    {
        tangent = default;
        if (_pointCount < 2)
        {
            return false;
        }

        CadPoint3D start = _points[_pointCount - 2];
        CadPoint3D end = _points[_pointCount - 1];
        double bulge = _bulges[_pointCount - 2];
        if (bulge == 0.0)
        {
            tangent = end - start;
            return IsFinite(tangent) && LengthSquared2D(tangent) > 0.0;
        }

        if (!TryGetBulgeGeometry(
                start,
                end,
                bulge,
                out CadPoint3D center,
                out _,
                out _,
                out _))
        {
            return false;
        }
        CadPoint3D radial = end - center;
        tangent = bulge > 0.0
            ? new CadPoint3D(-radial.Y, radial.X, 0.0)
            : new CadPoint3D(radial.Y, -radial.X, 0.0);
        return IsFinite(tangent) && LengthSquared2D(tangent) > 0.0;
    }

    private static bool TryGetTangentBulge(
        CadPoint3D start,
        CadPoint3D end,
        CadPoint3D tangent,
        out double bulge)
    {
        bulge = 0.0;
        double tangentLength = Hypot(tangent.X, tangent.Y);
        CadPoint3D chord = end - start;
        double chordLength = Hypot(chord.X, chord.Y);
        if (!double.IsFinite(tangentLength) || tangentLength <= 0.0 ||
            !double.IsFinite(chordLength) || chordLength <= 0.0)
        {
            return false;
        }

        double tx = tangent.X / tangentLength;
        double ty = tangent.Y / tangentLength;
        double x = (chord.X * tx) + (chord.Y * ty);
        double y = (-chord.X * ty) + (chord.Y * tx);
        double denominator = chordLength + x;
        double scale = Math.Max(chordLength, Math.Abs(x));
        if (!double.IsFinite(denominator) ||
            Math.Abs(denominator) <= scale * 1e-14)
        {
            return false;
        }

        bulge = y / denominator;
        return double.IsFinite(bulge) && bulge != 0.0;
    }

    /// <summary>
    /// Resolves one finite analytic bulge arc in the shared WCS-Z plane.
    /// </summary>
    public static bool TryGetBulgeGeometry(
        CadPoint3D start,
        CadPoint3D end,
        double bulge,
        out CadPoint3D center,
        out double radius,
        out double startAngle,
        out double sweep)
    {
        center = default;
        radius = 0.0;
        startAngle = 0.0;
        sweep = 0.0;
        CadPoint3D chord = end - start;
        double chordLength = Hypot(chord.X, chord.Y);
        if (!double.IsFinite(chordLength) || chordLength <= 0.0 ||
            !double.IsFinite(bulge) || bulge == 0.0)
        {
            return false;
        }

        double factor = ((1.0 / bulge) - bulge) * 0.25;
        center = new CadPoint3D(
            start.X + (chord.X * 0.5) - (chord.Y * factor),
            start.Y + (chord.Y * 0.5) + (chord.X * factor),
            start.Z);
        double absoluteBulge = Math.Abs(bulge);
        radius = (chordLength * 0.25) *
            (absoluteBulge + (1.0 / absoluteBulge));
        startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        sweep = 4.0 * Math.Atan(bulge);
        return IsFinite(center) &&
            double.IsFinite(radius) && radius > 0.0 &&
            double.IsFinite(startAngle) &&
            double.IsFinite(sweep) && sweep != 0.0;
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
        Array.Resize(ref _bulges, capacity);
    }

    private static double LengthSquared2D(CadPoint3D value) =>
        (value.X * value.X) + (value.Y * value.Y);

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

/// <summary>
/// Adds one lightweight 2D polyline as one reversible history operation.
/// </summary>
/// <remarks>
/// Current CLAYER, CECOLOR, CELTYPE, CELTSCALE, CELWEIGHT, PLINEGEN, and
/// PLINEWID are captured atomically on first Apply. Nonzero PLINEWID fails
/// before mutation until filled wide-polyline rendering is available.
/// Apply/Undo/Redo are O(S), and retained command storage is O(S).
/// </remarks>
public sealed class CadAddPolylineCommand : CadEditCommand
{
    private readonly CadPolylineAuthoringSnapshot _snapshot;
    private LwPolyline? _polyline;

    public CadPolylineAuthoringSnapshot Snapshot => _snapshot;

    public LwPolyline? Polyline => _polyline;

    public ulong CurrentHandle => _polyline?.Handle ?? 0;

    public CadAddPolylineCommand(
        CadPolylineAuthoringSnapshot snapshot,
        string description = "PLINE")
        : base(description)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.SegmentCount > CadPolylineAuthoringSession.DefaultMaximumSegmentCount)
        {
            throw new ArgumentException(
                $"The PLINE exceeds the configured limit of {CadPolylineAuthoringSession.DefaultMaximumSegmentCount} segments.",
                nameof(snapshot));
        }
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        LwPolyline polyline;
        if (isRedo)
        {
            polyline = _polyline ?? throw new InvalidOperationException(
                "The PLINE command has not been applied.");
        }
        else
        {
            polyline = CreatePolyline(document);
            _polyline = polyline;
        }

        ValidateDetached(polyline);
        document.Entities.Add(polyline);
    }

    internal override void Revert(CadDocument document)
    {
        LwPolyline polyline = _polyline ?? throw new InvalidOperationException(
            "The PLINE command has not been applied.");
        ValidateModelSpaceEntity(document, polyline);
        if (!document.Entities.Remove(polyline))
        {
            throw new InvalidOperationException(
                "The authored PLINE could not be removed from model space.");
        }
    }

    private LwPolyline CreatePolyline(CadDocument document)
    {
        Layer layer = document.Header.CurrentLayer;
        if (HasLayerFlag(layer, LayerFlags.Locked))
        {
            throw new InvalidOperationException(
                $"Current layer '{layer.Name}' is locked and cannot receive a PLINE entity.");
        }

        double defaultWidth = document.Header.PolylineWidthDefault;
        if (!double.IsFinite(defaultWidth) || defaultWidth < 0.0)
        {
            throw new InvalidOperationException(
                "Current PLINEWID must be finite and non-negative before creating a PLINE.");
        }
        if (defaultWidth != 0.0)
        {
            throw new CadUnsupportedEntityException(
                "Nonzero PLINEWID requires filled wide-polyline lowering and is not authored as a cosmetic centerline.");
        }

        double lineTypeScale = document.Header.CurrentEntityLinetypeScale;
        if (!double.IsFinite(lineTypeScale) || lineTypeScale <= 0.0)
        {
            throw new InvalidOperationException(
                "Current CELTSCALE must be finite and positive before creating a PLINE.");
        }

        var polyline = new LwPolyline
        {
            Layer = layer,
            Color = document.Header.CurrentEntityColor,
            LineType = document.Header.CurrentLineType,
            LineTypeScale = lineTypeScale,
            LineWeight = document.Header.CurrentEntityLineWeight,
            Elevation = _snapshot.Elevation,
            Normal = XYZ.AxisZ,
            Flags = document.Header.PolylineLineTypeGeneration
                ? LwPolylineFlags.Plinegen
                : LwPolylineFlags.Default,
        };
        polyline.IsClosed = _snapshot.IsClosed;
        ReadOnlySpan<CadPoint3D> points = _snapshot.Points.Span;
        ReadOnlySpan<double> bulges = _snapshot.Bulges.Span;
        for (int i = 0; i < points.Length; i++)
        {
            polyline.Vertices.Add(new LwPolyline.Vertex(points[i].X, points[i].Y)
            {
                Bulge = bulges[i],
            });
        }
        return polyline;
    }

    private static void ValidateDetached(LwPolyline polyline)
    {
        if (polyline.Owner is not null ||
            polyline.Document is not null ||
            polyline.Handle != 0)
        {
            throw new InvalidOperationException(
                "The retained PLINE entity is not detached and cannot be added.");
        }
    }
}

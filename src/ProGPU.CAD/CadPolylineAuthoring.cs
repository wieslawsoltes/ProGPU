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

/// <summary>The value currently requested by a PLINE command.</summary>
public enum CadPolylineAuthoringPrompt : byte
{
    Point = 0,
    StartingWidth = 1,
    EndingWidth = 2,
    Length = 3,
    ArcIncludedAngle = 4,
    ArcCenter = 5,
    ArcDirection = 6,
    ArcRadius = 7,
    ArcSecondPoint = 8,
    ArcEndpoint = 9,
    ArcChordDirection = 10,
    ArcChordLength = 11,
}

/// <summary>The explicit construction used by the next PLINE arc.</summary>
public enum CadPolylineArcConstruction : byte
{
    TangentEndpoint = 0,
    IncludedAngle = 1,
    Center = 2,
    Direction = 3,
    Radius = 4,
    ThreePoint = 5,
}

/// <summary>An option selected after a PLINE arc's Angle or Center input.</summary>
public enum CadPolylineArcNestedOption : byte
{
    None = 0,
    Center = 1,
    Radius = 2,
    IncludedAngle = 3,
    ChordLength = 4,
}

/// <summary>Whether a PLINE width prompt consumes full or half widths.</summary>
public enum CadPolylineWidthInputMode : byte
{
    Width = 0,
    Halfwidth = 1,
}

/// <summary>Immutable planar centerline input for one lightweight polyline.</summary>
public sealed class CadPolylineAuthoringSnapshot
{
    private readonly CadPoint3D[] _points;
    private readonly double[] _bulges;
    private readonly double[] _startWidths;
    private readonly double[] _endWidths;

    public ReadOnlyMemory<CadPoint3D> Points => _points;

    /// <summary>
    /// One bulge per vertex. The last value owns the closing segment only when
    /// <see cref="IsClosed"/> is true and is otherwise zero.
    /// </summary>
    public ReadOnlyMemory<double> Bulges => _bulges;

    /// <summary>
    /// Explicit segment widths indexed by segment-start vertex. Empty means
    /// the command must capture the drawing-level PLINEWID at Apply time.
    /// </summary>
    public ReadOnlyMemory<double> StartWidths => _startWidths;

    public ReadOnlyMemory<double> EndWidths => _endWidths;

    public bool HasExplicitWidths => _startWidths.Length != 0;

    /// <summary>
    /// The final uniform width to publish back to drawing-level PLINEWID when
    /// the interactive Width or Halfwidth option was used.
    /// </summary>
    public double? ResultingDefaultWidth { get; }

    public bool IsClosed { get; }

    public int SegmentCount => IsClosed ? _points.Length : _points.Length - 1;

    public double Elevation => _points[0].Z;

    public CadPolylineAuthoringSnapshot(
        ReadOnlySpan<CadPoint3D> points,
        ReadOnlySpan<double> bulges,
        bool isClosed,
        ReadOnlySpan<double> startWidths = default,
        ReadOnlySpan<double> endWidths = default,
        double? resultingDefaultWidth = null)
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
        bool hasExplicitWidths = startWidths.Length != 0 || endWidths.Length != 0;
        if (hasExplicitWidths &&
            (startWidths.Length != points.Length || endWidths.Length != points.Length))
        {
            throw new ArgumentException(
                "Explicit lightweight-polyline widths require one start/end slot per vertex.",
                nameof(startWidths));
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
            if (hasExplicitWidths &&
                (!IsValidWidth(startWidths[i]) || !IsValidWidth(endWidths[i])))
            {
                throw new ArgumentException(
                    "Every explicit lightweight-polyline width must be finite, non-negative, and within the retained float domain.",
                    nameof(startWidths));
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
        if (!isClosed && hasExplicitWidths &&
            (startWidths[^1] != 0.0 || endWidths[^1] != 0.0))
        {
            throw new ArgumentException(
                "An open lightweight polyline cannot carry terminal segment widths.",
                nameof(startWidths));
        }
        if (isClosed && points[^1] == points[0])
        {
            throw new ArgumentException(
                "A closed lightweight polyline stores closure as a flag, not a duplicate vertex.",
                nameof(points));
        }

        _points = points.ToArray();
        _bulges = bulges.ToArray();
        _startWidths = hasExplicitWidths ? startWidths.ToArray() : [];
        _endWidths = hasExplicitWidths ? endWidths.ToArray() : [];
        if (resultingDefaultWidth is double finalWidth && !IsValidWidth(finalWidth))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resultingDefaultWidth),
                "The resulting PLINEWID must be finite, non-negative, and within the retained float domain.");
        }
        if (resultingDefaultWidth is not null && !hasExplicitWidths)
        {
            throw new ArgumentException(
                "A resulting PLINEWID requires explicit authored segment widths.",
                nameof(resultingDefaultWidth));
        }
        ResultingDefaultWidth = resultingDefaultWidth;
        IsClosed = isClosed;
    }

    private static bool IsValidWidth(double width) =>
        double.IsFinite(width) && width >= 0.0 && width <= float.MaxValue;

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>
/// Bounded host-neutral state for one planar lightweight-polyline command.
/// </summary>
/// <remarks>
/// Accepted WCS points, analytic bulges, and explicit widths use geometrically growing arrays.
/// Acceptance is amortized O(1), segment Undo is O(1), and completion is O(S)
/// for S segments. Tangent arcs retain one exact DXF bulge, never tessellation.
/// Width and Halfwidth preserve the documented end-to-next-segment state; a
/// variable profile is rejected when any accepted arc would require a tapered
/// curved boundary that the exact retained renderer cannot yet represent.
/// </remarks>
public sealed class CadPolylineAuthoringSession
{
    public const int DefaultMaximumSegmentCount = 65_536;

    private const double MaximumArcAngle = Math.Tau - 1e-12;
    private CadPoint3D[] _points;
    private double[] _bulges;
    private double[] _startWidths;
    private double[] _endWidths;
    private bool[] _uniformWidthProfileThrough;
    private double[] _uniformWidthThrough;
    private int _pointCount;
    private int _acceptedArcCount;
    private CadPolylineAuthoringMode _mode;
    private double _nextStartWidth;
    private double _nextEndWidth;
    private double _widthInputStart;
    private bool _widthWasChanged;
    private CadPolylineArcConstruction _arcConstruction;
    private CadPolylineArcNestedOption _arcNestedOption;
    private double _arcScalar;
    private double _arcNestedScalar;
    private CadPoint3D _arcControlPoint;

    public int MaximumSegmentCount { get; }

    public CadPolylineAuthoringMode Mode
    {
        get => _mode;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (Prompt != CadPolylineAuthoringPrompt.Point)
            {
                throw new InvalidOperationException(
                    "Complete the active PLINE scalar prompt before changing segment mode.");
            }
            _mode = value;
        }
    }

    public CadPolylineAuthoringPrompt Prompt { get; private set; }

    public CadPolylineWidthInputMode WidthInputMode { get; private set; }

    public CadPolylineArcConstruction ArcConstruction => _arcConstruction;

    public CadPolylineArcNestedOption ArcNestedOption => _arcNestedOption;

    public double NextStartWidth => _nextStartWidth;

    public double NextEndWidth => _nextEndWidth;

    public double WidthPromptDefault => Prompt switch
    {
        CadPolylineAuthoringPrompt.StartingWidth => _nextStartWidth,
        CadPolylineAuthoringPrompt.EndingWidth => _widthInputStart,
        _ => throw new InvalidOperationException("No PLINE width prompt is active."),
    };

    public int PointCount => _pointCount;

    public int SegmentCount => Math.Max(0, _pointCount - 1);

    public bool HasFirstPoint => _pointCount > 0;

    public bool CanBeginWidthInput =>
        Prompt == CadPolylineAuthoringPrompt.Point && HasFirstPoint;

    public bool CanBeginLengthInput =>
        Prompt == CadPolylineAuthoringPrompt.Point &&
        Mode == CadPolylineAuthoringMode.Line &&
        SegmentCount > 0;

    public bool CanBeginArcConstruction =>
        Prompt == CadPolylineAuthoringPrompt.Point &&
        Mode == CadPolylineAuthoringMode.TangentArc &&
        HasFirstPoint &&
        SegmentCount < MaximumSegmentCount;

    public bool CanBeginArcNestedOption(CadPolylineArcNestedOption option)
    {
        if (!Enum.IsDefined(option) || option == CadPolylineArcNestedOption.None)
        {
            return false;
        }
        return Prompt == CadPolylineAuthoringPrompt.ArcEndpoint &&
            _arcNestedOption == CadPolylineArcNestedOption.None &&
            ((_arcConstruction == CadPolylineArcConstruction.IncludedAngle &&
              option is (CadPolylineArcNestedOption.Center or
                  CadPolylineArcNestedOption.Radius)) ||
             (_arcConstruction == CadPolylineArcConstruction.Center &&
              option is (CadPolylineArcNestedOption.IncludedAngle or
                  CadPolylineArcNestedOption.ChordLength)));
    }

    public bool IsArcNestedFinalPointPrompt =>
        (_arcNestedOption == CadPolylineArcNestedOption.Center &&
         Prompt == CadPolylineAuthoringPrompt.ArcCenter) ||
        (_arcNestedOption == CadPolylineArcNestedOption.Radius &&
         Prompt == CadPolylineAuthoringPrompt.ArcChordDirection);

    public bool IsArcNestedFinalScalarPrompt =>
        (_arcNestedOption == CadPolylineArcNestedOption.IncludedAngle &&
         Prompt == CadPolylineAuthoringPrompt.ArcIncludedAngle) ||
        (_arcNestedOption == CadPolylineArcNestedOption.ChordLength &&
         Prompt == CadPolylineAuthoringPrompt.ArcChordLength);

    public bool CanUndo =>
        Prompt == CadPolylineAuthoringPrompt.Point && SegmentCount > 0;

    public bool CanClose
    {
        get
        {
            if (Prompt != CadPolylineAuthoringPrompt.Point ||
                SegmentCount < 2 ||
                _points[_pointCount - 1] == _points[0] ||
                SegmentCount == MaximumSegmentCount)
            {
                return false;
            }
            if (Mode == CadPolylineAuthoringMode.Line)
            {
                return true;
            }
            return TryGetPreviousSegmentTangent(out CadPoint3D tangent) &&
                TryGetTangentBulge(
                    _points[_pointCount - 1],
                    _points[0],
                    tangent,
                    out double bulge) &&
                CanAcceptBulgeWithNextWidth(bulge, out _);
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

    public ReadOnlyMemory<double> StartWidths =>
        _startWidths.AsMemory(0, _pointCount);

    public ReadOnlyMemory<double> EndWidths =>
        _endWidths.AsMemory(0, _pointCount);

    public CadPolylineAuthoringSession(
        int maximumSegmentCount = DefaultMaximumSegmentCount,
        double initialWidth = 0.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegmentCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumSegmentCount,
            DefaultMaximumSegmentCount);
        if (!IsValidWidth(initialWidth))
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialWidth),
                "Initial PLINE width must be finite, non-negative, and within the retained float domain.");
        }
        MaximumSegmentCount = maximumSegmentCount;
        int initialCapacity = Math.Min(maximumSegmentCount + 1, 16);
        _points = new CadPoint3D[initialCapacity];
        _bulges = new double[initialCapacity];
        _startWidths = new double[initialCapacity];
        _endWidths = new double[initialCapacity];
        _uniformWidthProfileThrough = new bool[initialCapacity];
        _uniformWidthThrough = new double[initialCapacity];
        _nextStartWidth = initialWidth;
        _nextEndWidth = initialWidth;
    }

    public bool TryAcceptPoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        if (IsArcNestedFinalPointPrompt)
        {
            return TryAcceptArcNestedPoint(point, out _, out errorMessage);
        }
        if (Prompt is CadPolylineAuthoringPrompt.ArcCenter or
            CadPolylineAuthoringPrompt.ArcDirection or
            CadPolylineAuthoringPrompt.ArcSecondPoint)
        {
            return TryAcceptArcControlPoint(point, out errorMessage);
        }
        if (Prompt == CadPolylineAuthoringPrompt.ArcEndpoint)
        {
            return TryAcceptArcEndpoint(point, out _, out errorMessage);
        }
        if (Prompt != CadPolylineAuthoringPrompt.Point)
        {
            errorMessage = "Complete the active PLINE scalar prompt before specifying a point.";
            return false;
        }
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
        return TryResolvePendingSegment(point, out _, out bulge, out _);
    }

    /// <summary>
    /// Starts an explicit O(1) PLINE arc construction. The implementation is
    /// derived from the original ProGPU-owned normalized ARC solvers in
    /// <c>CadArcAuthoring.cs</c>; signed bulges preserve PLINE vertex order.
    /// </summary>
    public bool TryBeginArcConstruction(
        CadPolylineArcConstruction construction,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!Enum.IsDefined(construction) ||
            construction == CadPolylineArcConstruction.TangentEndpoint)
        {
            throw new ArgumentOutOfRangeException(nameof(construction));
        }
        if (!CanBeginArcConstruction)
        {
            errorMessage = HasFirstPoint
                ? "Explicit PLINE arc options require Arc mode and no active value prompt."
                : "Accept the first PLINE point before choosing an arc option.";
            return false;
        }

        _arcConstruction = construction;
        _arcNestedOption = CadPolylineArcNestedOption.None;
        Prompt = construction switch
        {
            CadPolylineArcConstruction.IncludedAngle =>
                CadPolylineAuthoringPrompt.ArcIncludedAngle,
            CadPolylineArcConstruction.Center => CadPolylineAuthoringPrompt.ArcCenter,
            CadPolylineArcConstruction.Direction => CadPolylineAuthoringPrompt.ArcDirection,
            CadPolylineArcConstruction.Radius => CadPolylineAuthoringPrompt.ArcRadius,
            CadPolylineArcConstruction.ThreePoint =>
                CadPolylineAuthoringPrompt.ArcSecondPoint,
            _ => throw new InvalidOperationException(),
        };
        return true;
    }

    public bool TryBeginArcNestedOption(
        CadPolylineArcNestedOption option,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!Enum.IsDefined(option) || option == CadPolylineArcNestedOption.None)
        {
            throw new ArgumentOutOfRangeException(nameof(option));
        }
        if (!CanBeginArcNestedOption(option))
        {
            errorMessage =
                "The requested nested PLINE arc option is not available for the active construction.";
            return false;
        }

        _arcNestedOption = option;
        Prompt = option switch
        {
            CadPolylineArcNestedOption.Center =>
                CadPolylineAuthoringPrompt.ArcCenter,
            CadPolylineArcNestedOption.Radius =>
                CadPolylineAuthoringPrompt.ArcRadius,
            CadPolylineArcNestedOption.IncludedAngle =>
                CadPolylineAuthoringPrompt.ArcIncludedAngle,
            CadPolylineArcNestedOption.ChordLength =>
                CadPolylineAuthoringPrompt.ArcChordLength,
            _ => throw new InvalidOperationException(),
        };
        return true;
    }

    public bool TryAcceptArcScalar(double value, out string? errorMessage)
    {
        errorMessage = null;
        if (Prompt == CadPolylineAuthoringPrompt.ArcIncludedAngle)
        {
            if (!double.IsFinite(value) || value == 0.0 ||
                Math.Abs(value) >= MaximumArcAngle)
            {
                errorMessage =
                    "A PLINE arc angle must be finite, nonzero, and less than one complete turn.";
                return false;
            }
        }
        else if (Prompt == CadPolylineAuthoringPrompt.ArcRadius)
        {
            if (!double.IsFinite(value) || value <= 0.0)
            {
                errorMessage = "A PLINE arc radius must be finite and positive.";
                return false;
            }
        }
        else
        {
            errorMessage = "No PLINE arc scalar value is currently requested.";
            return false;
        }

        if (_arcNestedOption == CadPolylineArcNestedOption.Radius)
        {
            _arcNestedScalar = value;
            Prompt = CadPolylineAuthoringPrompt.ArcChordDirection;
        }
        else
        {
            _arcScalar = value;
            Prompt = CadPolylineAuthoringPrompt.ArcEndpoint;
        }
        return true;
    }

    public bool TryResolveArcNestedPoint(
        CadPoint3D inputPoint,
        out CadPoint3D endpoint,
        out double bulge,
        out string? errorMessage)
    {
        endpoint = default;
        bulge = 0.0;
        errorMessage = null;
        if (!IsArcNestedFinalPointPrompt ||
            !ValidatePlanPoint(inputPoint, out errorMessage))
        {
            errorMessage ??= "No nested PLINE arc point is currently requested.";
            return false;
        }

        CadPoint3D start = _points[_pointCount - 1];
        bool solved = _arcNestedOption switch
        {
            CadPolylineArcNestedOption.Center =>
                TryGetCenterSweepArc(
                    start,
                    inputPoint,
                    _arcScalar,
                    out endpoint,
                    out bulge),
            CadPolylineArcNestedOption.Radius =>
                TryGetSweepRadiusDirectionArc(
                    start,
                    _arcScalar,
                    _arcNestedScalar,
                    inputPoint,
                    out endpoint,
                    out bulge),
            _ => false,
        };
        if (!solved ||
            !ValidateResolvedArc(start, endpoint, bulge, out errorMessage))
        {
            return FailNestedArcSolve(ref errorMessage);
        }
        return true;
    }

    public bool TryAcceptArcNestedPoint(
        CadPoint3D inputPoint,
        out CadPoint3D endpoint,
        out string? errorMessage)
    {
        if (!TryResolveArcNestedPoint(
                inputPoint,
                out endpoint,
                out double bulge,
                out errorMessage) ||
            !TryAccept(endpoint, bulge, out errorMessage))
        {
            return false;
        }
        ResetArcConstruction();
        return true;
    }

    public bool TryResolveArcNestedScalar(
        double value,
        out CadPoint3D endpoint,
        out double bulge,
        out string? errorMessage)
    {
        endpoint = default;
        bulge = 0.0;
        errorMessage = null;
        if (!IsArcNestedFinalScalarPrompt)
        {
            errorMessage = "No nested PLINE arc scalar is currently requested.";
            return false;
        }

        CadPoint3D start = _points[_pointCount - 1];
        bool solved;
        if (_arcNestedOption == CadPolylineArcNestedOption.IncludedAngle)
        {
            solved = double.IsFinite(value) && value != 0.0 &&
                Math.Abs(value) < MaximumArcAngle &&
                TryGetCenterSweepArc(
                    start,
                    _arcControlPoint,
                    value,
                    out endpoint,
                    out bulge);
        }
        else
        {
            solved = double.IsFinite(value) && value != 0.0 &&
                TryGetCenterChordArc(
                    start,
                    _arcControlPoint,
                    value,
                    out endpoint,
                    out bulge);
        }
        if (!solved ||
            !ValidateResolvedArc(start, endpoint, bulge, out errorMessage))
        {
            return FailNestedArcSolve(ref errorMessage);
        }
        return true;
    }

    public bool TryAcceptArcNestedScalar(
        double value,
        out CadPoint3D endpoint,
        out string? errorMessage)
    {
        if (!TryResolveArcNestedScalar(
                value,
                out endpoint,
                out double bulge,
                out errorMessage) ||
            !TryAccept(endpoint, bulge, out errorMessage))
        {
            return false;
        }
        ResetArcConstruction();
        return true;
    }

    public bool TryAcceptArcControlPoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        errorMessage = null;
        if (IsArcNestedFinalPointPrompt ||
            Prompt is not (CadPolylineAuthoringPrompt.ArcCenter or
            CadPolylineAuthoringPrompt.ArcDirection or
            CadPolylineAuthoringPrompt.ArcSecondPoint))
        {
            errorMessage = "No PLINE arc construction point is currently requested.";
            return false;
        }
        if (!ValidatePlanPoint(point, out errorMessage))
        {
            return false;
        }

        CadPoint3D start = _points[_pointCount - 1];
        if (point == start)
        {
            errorMessage = Prompt switch
            {
                CadPolylineAuthoringPrompt.ArcCenter =>
                    "A PLINE arc center must differ from its start point.",
                CadPolylineAuthoringPrompt.ArcDirection =>
                    "A PLINE arc direction point must differ from its start point.",
                _ => "A PLINE arc second point must differ from its start point.",
            };
            return false;
        }

        _arcControlPoint = point;
        Prompt = CadPolylineAuthoringPrompt.ArcEndpoint;
        return true;
    }

    public bool CanAcceptArcControlPoint(CadPoint3D point) =>
        !IsArcNestedFinalPointPrompt &&
        Prompt is (CadPolylineAuthoringPrompt.ArcCenter or
            CadPolylineAuthoringPrompt.ArcDirection or
            CadPolylineAuthoringPrompt.ArcSecondPoint) &&
        ValidatePlanPoint(point, out _) &&
        point != _points[_pointCount - 1];

    public bool TryAcceptArcEndpoint(
        CadPoint3D inputPoint,
        out CadPoint3D endpoint,
        out string? errorMessage)
    {
        if (!TryResolvePendingSegment(
                inputPoint,
                out endpoint,
                out double bulge,
                out errorMessage))
        {
            return false;
        }
        if (!TryAccept(endpoint, bulge, out errorMessage))
        {
            return false;
        }

        ResetArcConstruction();
        return true;
    }

    /// <summary>
    /// Resolves the pointer/input point to the exact next endpoint and signed
    /// DXF bulge without mutating accepted state.
    /// </summary>
    public bool TryResolvePendingSegment(
        CadPoint3D inputPoint,
        out CadPoint3D endpoint,
        out double bulge,
        out string? errorMessage)
    {
        endpoint = inputPoint;
        bulge = 0.0;
        errorMessage = null;
        if (_pointCount == 0 || !ValidatePlanPoint(inputPoint, out errorMessage))
        {
            return false;
        }
        CadPoint3D start = _points[_pointCount - 1];
        if (inputPoint == start)
        {
            errorMessage = "A PLINE segment must have distinct endpoints.";
            return false;
        }

        if (Prompt == CadPolylineAuthoringPrompt.Point)
        {
            if (Mode == CadPolylineAuthoringMode.Line)
            {
                return true;
            }
            if (!TryGetPreviousSegmentTangent(out CadPoint3D tangent) ||
                !TryGetTangentBulge(start, inputPoint, tangent, out bulge))
            {
                errorMessage =
                    "The endpoint does not define a finite non-degenerate arc from the previous segment tangent.";
                return false;
            }
        }
        else if (Prompt == CadPolylineAuthoringPrompt.ArcEndpoint)
        {
            bool solved = _arcConstruction switch
            {
                CadPolylineArcConstruction.IncludedAngle =>
                    TryGetSweepBulge(_arcScalar, out bulge),
                CadPolylineArcConstruction.Center =>
                    TryGetCenterArc(start, _arcControlPoint, inputPoint, out endpoint, out bulge),
                CadPolylineArcConstruction.Direction =>
                    TryGetTangentBulge(start, inputPoint, _arcControlPoint - start, out bulge),
                CadPolylineArcConstruction.Radius =>
                    TryGetRadiusBulge(start, inputPoint, _arcScalar, out bulge),
                CadPolylineArcConstruction.ThreePoint =>
                    TryGetThreePointBulge(start, _arcControlPoint, inputPoint, out bulge),
                _ => false,
            };
            if (!solved)
            {
                errorMessage =
                    "The PLINE arc inputs do not define a finite non-degenerate circular arc.";
                return false;
            }
        }
        else
        {
            errorMessage = "Complete the active PLINE arc option before specifying its endpoint.";
            return false;
        }

        if (bulge != 0.0 &&
            !TryGetBulgeGeometry(
                start,
                endpoint,
                bulge,
                out _,
                out _,
                out _,
                out _))
        {
            errorMessage =
                "The PLINE arc resolves outside finite analytic geometry.";
            return false;
        }
        if (!CanAcceptBulgeWithNextWidth(bulge, out errorMessage))
        {
            return false;
        }
        return true;
    }

    public bool TryBeginWidthInput(
        CadPolylineWidthInputMode mode,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (Prompt != CadPolylineAuthoringPrompt.Point)
        {
            errorMessage = "Complete the active PLINE scalar prompt first.";
            return false;
        }
        if (!HasFirstPoint)
        {
            errorMessage = "Accept the first PLINE point before changing width.";
            return false;
        }

        WidthInputMode = mode;
        Prompt = CadPolylineAuthoringPrompt.StartingWidth;
        return true;
    }

    public bool TryAcceptWidthValue(
        double value,
        out string? errorMessage)
    {
        errorMessage = null;
        if (Prompt is not (CadPolylineAuthoringPrompt.StartingWidth or
            CadPolylineAuthoringPrompt.EndingWidth))
        {
            errorMessage = "No PLINE width value is currently requested.";
            return false;
        }
        if (!double.IsFinite(value) || value < 0.0)
        {
            errorMessage = "A PLINE width value must be finite and non-negative.";
            return false;
        }

        double fullWidth = WidthInputMode == CadPolylineWidthInputMode.Halfwidth
            ? value * 2.0
            : value;
        if (!IsValidWidth(fullWidth))
        {
            errorMessage = "The PLINE width exceeds the retained float geometry domain.";
            return false;
        }
        if (Prompt == CadPolylineAuthoringPrompt.StartingWidth)
        {
            if (!CanChangeNextWidthAfterAcceptedArc(
                    fullWidth,
                    fullWidth,
                    out errorMessage))
            {
                return false;
            }
            _widthInputStart = fullWidth;
            Prompt = CadPolylineAuthoringPrompt.EndingWidth;
            return true;
        }
        if (!CanChangeNextWidthAfterAcceptedArc(
                _widthInputStart,
                fullWidth,
                out errorMessage))
        {
            return false;
        }

        _nextStartWidth = _widthInputStart;
        _nextEndWidth = fullWidth;
        _widthWasChanged = true;
        Prompt = CadPolylineAuthoringPrompt.Point;
        return true;
    }

    public bool TryAcceptDefaultWidthValue(out string? errorMessage) =>
        TryAcceptWidthValue(
            WidthInputMode == CadPolylineWidthInputMode.Halfwidth
                ? WidthPromptDefault * 0.5
                : WidthPromptDefault,
            out errorMessage);

    public bool TryBeginLengthInput(out string? errorMessage)
    {
        errorMessage = null;
        if (Prompt != CadPolylineAuthoringPrompt.Point)
        {
            errorMessage = "Complete the active PLINE scalar prompt first.";
            return false;
        }
        if (Mode != CadPolylineAuthoringMode.Line)
        {
            errorMessage = "The PLINE Length option is available only in Line mode.";
            return false;
        }
        if (!TryGetPreviousSegmentTangent(out _))
        {
            errorMessage = "PLINE Length requires a preceding accepted line or arc segment.";
            return false;
        }

        Prompt = CadPolylineAuthoringPrompt.Length;
        return true;
    }

    public bool TryAcceptLength(double length, out string? errorMessage)
    {
        if (!TryGetLengthEndpoint(length, out CadPoint3D end, out errorMessage))
        {
            return false;
        }
        if (!TryAccept(end, bulge: 0.0, out errorMessage))
        {
            return false;
        }
        Prompt = CadPolylineAuthoringPrompt.Point;
        return true;
    }

    public bool TryGetLengthEndpoint(
        double length,
        out CadPoint3D end,
        out string? errorMessage)
    {
        end = default;
        errorMessage = null;
        if (Prompt != CadPolylineAuthoringPrompt.Length)
        {
            errorMessage = "No PLINE line length is currently requested.";
            return false;
        }
        if (!double.IsFinite(length) || length <= 0.0)
        {
            errorMessage = "A PLINE line length must be finite and positive.";
            return false;
        }
        if (!TryGetPreviousSegmentTangent(out CadPoint3D tangent))
        {
            errorMessage = "The previous PLINE segment has no finite tangent.";
            return false;
        }

        double tangentLength = Hypot(tangent.X, tangent.Y);
        double scale = length / tangentLength;
        CadPoint3D start = _points[_pointCount - 1];
        end = new CadPoint3D(
            start.X + (tangent.X * scale),
            start.Y + (tangent.Y * scale),
            start.Z);
        if (!IsFinite(end))
        {
            errorMessage = "The PLINE length resolves outside finite WCS coordinates.";
            return false;
        }
        return true;
    }

    public bool TryAcceptLinePoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        if (Prompt != CadPolylineAuthoringPrompt.Point)
        {
            errorMessage = "Complete the active PLINE scalar prompt before specifying a point.";
            return false;
        }
        return TryAccept(point, bulge: 0.0, out errorMessage);
    }

    /// <summary>
    /// Adds an analytic circular arc whose starting tangent continues the
    /// actual tangent of the preceding accepted segment.
    /// </summary>
    public bool TryAcceptTangentArcPoint(
        CadPoint3D point,
        out string? errorMessage)
    {
        errorMessage = null;
        if (Prompt != CadPolylineAuthoringPrompt.Point)
        {
            errorMessage = "Complete the active PLINE scalar prompt before specifying an arc endpoint.";
            return false;
        }
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
        if (!CanAcceptBulgeWithNextWidth(bulge, out errorMessage))
        {
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
        if (Prompt != CadPolylineAuthoringPrompt.Point)
        {
            errorMessage = "Complete the active PLINE scalar prompt before specifying an arc endpoint.";
            return false;
        }
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
        if (!CanAcceptBulgeWithNextWidth(bulge, out errorMessage))
        {
            return false;
        }
        return TryAccept(point, bulge, out errorMessage);
    }

    /// <summary>Removes only the latest segment while retaining its start.</summary>
    public bool TryUndoLastSegment()
    {
        if (Prompt != CadPolylineAuthoringPrompt.Point || _pointCount < 2)
        {
            return false;
        }

        int removedSegmentIndex = _pointCount - 2;
        if (_bulges[removedSegmentIndex] != 0.0)
        {
            _acceptedArcCount--;
        }
        _pointCount--;
        _points[_pointCount] = default;
        _bulges[_pointCount - 1] = 0.0;
        _bulges[_pointCount] = 0.0;
        _startWidths[_pointCount - 1] = 0.0;
        _endWidths[_pointCount - 1] = 0.0;
        _startWidths[_pointCount] = 0.0;
        _endWidths[_pointCount] = 0.0;
        _uniformWidthProfileThrough[removedSegmentIndex] = false;
        _uniformWidthThrough[removedSegmentIndex] = 0.0;
        return true;
    }

    public bool TryCreateSnapshot(
        bool close,
        out CadPolylineAuthoringSnapshot? snapshot,
        out string? errorMessage)
    {
        snapshot = null;
        errorMessage = null;
        if (Prompt != CadPolylineAuthoringPrompt.Point)
        {
            errorMessage = "Complete the active PLINE scalar prompt before completion.";
            return false;
        }
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
        var startWidths = new double[_pointCount];
        var endWidths = new double[_pointCount];
        _points.AsSpan(0, _pointCount).CopyTo(points);
        _bulges.AsSpan(0, _pointCount).CopyTo(bulges);
        _startWidths.AsSpan(0, _pointCount).CopyTo(startWidths);
        _endWidths.AsSpan(0, _pointCount).CopyTo(endWidths);
        if (close)
        {
            startWidths[^1] = _nextStartWidth;
            endWidths[^1] = _nextEndWidth;
        }
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
            if (!CanAcceptBulgeWithWidth(
                    bulges[^1],
                    startWidths[^1],
                    endWidths[^1],
                    out errorMessage))
            {
                return false;
            }
        }

        snapshot = new CadPolylineAuthoringSnapshot(
            points,
            bulges,
            close,
            startWidths,
            endWidths,
            _widthWasChanged ? _nextEndWidth : null);
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
        if (bulge != 0.0 && !CanAcceptBulgeWithNextWidth(bulge, out errorMessage))
        {
            return false;
        }

        EnsureCapacity(_pointCount + 1);
        if (_pointCount > 0)
        {
            int segmentIndex = _pointCount - 1;
            _bulges[segmentIndex] = bulge;
            _startWidths[segmentIndex] = _nextStartWidth;
            _endWidths[segmentIndex] = _nextEndWidth;
            bool isUniformThroughSegment =
                _nextStartWidth == _nextEndWidth &&
                (segmentIndex == 0 ||
                 (_uniformWidthProfileThrough[segmentIndex - 1] &&
                  _uniformWidthThrough[segmentIndex - 1] == _nextStartWidth));
            _uniformWidthProfileThrough[segmentIndex] = isUniformThroughSegment;
            _uniformWidthThrough[segmentIndex] = isUniformThroughSegment
                ? _nextStartWidth
                : 0.0;
            if (bulge != 0.0)
            {
                _acceptedArcCount++;
            }
            _nextStartWidth = _nextEndWidth;
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

    private bool CanChangeNextWidthAfterAcceptedArc(
        double startWidth,
        double endWidth,
        out string? errorMessage)
    {
        if (_acceptedArcCount != 0)
        {
            return CanAcceptBulgeWithWidth(
                bulge: 1.0,
                startWidth,
                endWidth,
                out errorMessage);
        }
        errorMessage = null;
        return true;
    }

    private bool CanAcceptBulgeWithNextWidth(
        double bulge,
        out string? errorMessage) =>
        CanAcceptBulgeWithWidth(
            bulge,
            _nextStartWidth,
            _nextEndWidth,
            out errorMessage);

    private bool CanAcceptBulgeWithWidth(
        double bulge,
        double startWidth,
        double endWidth,
        out string? errorMessage)
    {
        errorMessage = null;
        if (bulge == 0.0)
        {
            return true;
        }
        if (startWidth != endWidth)
        {
            errorMessage =
                "A tapered PLINE arc requires an exact spiral-boundary renderer and remains unsupported.";
            return false;
        }
        if (SegmentCount != 0 &&
            (!_uniformWidthProfileThrough[SegmentCount - 1] ||
             _uniformWidthThrough[SegmentCount - 1] != startWidth))
        {
            errorMessage =
                "A PLINE containing an arc currently requires one uniform entity-wide width.";
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

    private static bool TryGetSweepBulge(double signedSweep, out double bulge)
    {
        bulge = Math.Tan(signedSweep * 0.25);
        return double.IsFinite(signedSweep) &&
            signedSweep != 0.0 &&
            Math.Abs(signedSweep) < MaximumArcAngle &&
            double.IsFinite(bulge) &&
            bulge != 0.0;
    }

    private static bool TryGetCenterArc(
        CadPoint3D start,
        CadPoint3D center,
        CadPoint3D endRayPoint,
        out CadPoint3D endpoint,
        out double bulge)
    {
        endpoint = default;
        bulge = 0.0;
        double radius = Hypot(start.X - center.X, start.Y - center.Y);
        if (!double.IsFinite(radius) || radius <= 0.0 ||
            !TryGetUnit2D(
                endRayPoint - center,
                out double endX,
                out double endY))
        {
            return false;
        }

        endpoint = new CadPoint3D(
            center.X + (endX * radius),
            center.Y + (endY * radius),
            start.Z);
        double startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        double endAngle = Math.Atan2(endpoint.Y - center.Y, endpoint.X - center.X);
        return IsFinite(endpoint) &&
            TryGetSweepBulge(PositiveAngle(endAngle - startAngle), out bulge);
    }

    private static bool TryGetCenterSweepArc(
        CadPoint3D start,
        CadPoint3D center,
        double signedSweep,
        out CadPoint3D endpoint,
        out double bulge)
    {
        endpoint = default;
        bulge = 0.0;
        double radiusX = start.X - center.X;
        double radiusY = start.Y - center.Y;
        double radius = Hypot(radiusX, radiusY);
        if (!double.IsFinite(radius) || radius <= 0.0 ||
            !TryGetSweepBulge(signedSweep, out bulge))
        {
            return false;
        }

        double cosine = Math.Cos(signedSweep);
        double sine = Math.Sin(signedSweep);
        endpoint = new CadPoint3D(
            center.X + (radiusX * cosine) - (radiusY * sine),
            center.Y + (radiusX * sine) + (radiusY * cosine),
            start.Z);
        return IsFinite(endpoint) && endpoint != start;
    }

    private static bool TryGetCenterChordArc(
        CadPoint3D start,
        CadPoint3D center,
        double signedChordLength,
        out CadPoint3D endpoint,
        out double bulge)
    {
        endpoint = default;
        bulge = 0.0;
        double radius = Hypot(start.X - center.X, start.Y - center.Y);
        double chord = Math.Abs(signedChordLength);
        if (!double.IsFinite(radius) || radius <= 0.0 ||
            !double.IsFinite(chord) || chord <= 0.0 || chord > radius * 2.0)
        {
            return false;
        }

        double minorSweep = 2.0 * Math.Asin(
            Math.Min(1.0, chord / (radius * 2.0)));
        double sweep = signedChordLength > 0.0
            ? minorSweep
            : Math.Tau - minorSweep;
        return TryGetCenterSweepArc(
            start,
            center,
            sweep,
            out endpoint,
            out bulge);
    }

    private static bool TryGetSweepRadiusDirectionArc(
        CadPoint3D start,
        double signedSweep,
        double radius,
        CadPoint3D chordDirectionPoint,
        out CadPoint3D endpoint,
        out double bulge)
    {
        endpoint = default;
        bulge = 0.0;
        if (!double.IsFinite(radius) || radius <= 0.0 ||
            !TryGetSweepBulge(signedSweep, out bulge) ||
            !TryGetUnit2D(
                chordDirectionPoint - start,
                out double chordX,
                out double chordY))
        {
            return false;
        }

        double chordLength = 2.0 * radius *
            Math.Abs(Math.Sin(signedSweep * 0.5));
        if (!double.IsFinite(chordLength) || chordLength <= 0.0)
        {
            return false;
        }
        endpoint = new CadPoint3D(
            start.X + (chordX * chordLength),
            start.Y + (chordY * chordLength),
            start.Z);
        return IsFinite(endpoint) && endpoint != start;
    }

    private bool ValidateResolvedArc(
        CadPoint3D start,
        CadPoint3D endpoint,
        double bulge,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!TryGetBulgeGeometry(
                start,
                endpoint,
                bulge,
                out _,
                out double radius,
                out _,
                out _) ||
            radius > float.MaxValue)
        {
            errorMessage =
                "The PLINE arc resolves outside finite retained geometry.";
            return false;
        }
        return CanAcceptBulgeWithNextWidth(bulge, out errorMessage);
    }

    private static bool FailNestedArcSolve(ref string? errorMessage)
    {
        errorMessage ??=
            "The nested PLINE arc inputs do not define a finite non-degenerate circular arc.";
        return false;
    }

    private static bool TryGetRadiusBulge(
        CadPoint3D start,
        CadPoint3D end,
        double radius,
        out double bulge)
    {
        bulge = 0.0;
        double chordLength = Hypot(end.X - start.X, end.Y - start.Y);
        double halfChord = chordLength * 0.5;
        if (!double.IsFinite(radius) || radius <= 0.0 ||
            !double.IsFinite(chordLength) || chordLength <= 0.0 ||
            radius < halfChord)
        {
            return false;
        }

        double sweep = 2.0 * Math.Asin(Math.Min(1.0, halfChord / radius));
        return TryGetSweepBulge(sweep, out bulge);
    }

    private static bool TryGetThreePointBulge(
        CadPoint3D start,
        CadPoint3D pointOnArc,
        CadPoint3D end,
        out double bulge)
    {
        bulge = 0.0;
        if (!TryGetThreePointCircle(
                start,
                pointOnArc,
                end,
                out CadPoint3D center))
        {
            return false;
        }

        double orientation = Cross2D(pointOnArc - start, end - start);
        if (!double.IsFinite(orientation) || orientation == 0.0)
        {
            return false;
        }
        double startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        double endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);
        double sweep = orientation > 0.0
            ? PositiveAngle(endAngle - startAngle)
            : -PositiveAngle(startAngle - endAngle);
        return TryGetSweepBulge(sweep, out bulge);
    }

    private static bool TryGetThreePointCircle(
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third,
        out CadPoint3D center)
    {
        center = default;
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
        double radius = Hypot(offsetX, offsetY);
        return IsFinite(center) && double.IsFinite(radius) && radius > 0.0;
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
        double length = Math.Sqrt((scaledX * scaledX) + (scaledY * scaledY));
        if (!double.IsFinite(length) || length <= 0.0)
        {
            return false;
        }
        x = scaledX / length;
        y = scaledY / length;
        return double.IsFinite(x) && double.IsFinite(y);
    }

    private static double Cross2D(CadPoint3D first, CadPoint3D second) =>
        (first.X * second.Y) - (first.Y * second.X);

    private static double PositiveAngle(double angle)
    {
        double normalized = angle % Math.Tau;
        return normalized < 0.0 ? normalized + Math.Tau : normalized;
    }

    private bool ValidatePlanPoint(
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
        return true;
    }

    private void ResetArcConstruction()
    {
        _arcConstruction = CadPolylineArcConstruction.TangentEndpoint;
        _arcNestedOption = CadPolylineArcNestedOption.None;
        _arcScalar = 0.0;
        _arcNestedScalar = 0.0;
        _arcControlPoint = default;
        Prompt = CadPolylineAuthoringPrompt.Point;
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
        Array.Resize(ref _startWidths, capacity);
        Array.Resize(ref _endWidths, capacity);
        Array.Resize(ref _uniformWidthProfileThrough, capacity);
        Array.Resize(ref _uniformWidthThrough, capacity);
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

    private static bool IsValidWidth(double width) =>
        double.IsFinite(width) && width >= 0.0 && width <= float.MaxValue;
}

/// <summary>
/// Adds one lightweight 2D polyline as one reversible history operation.
/// </summary>
/// <remarks>
/// Current CLAYER, CECOLOR, CELTYPE, CELTSCALE, CELWEIGHT, PLINEGEN, and
/// PLINEWID are captured atomically on first Apply. A finite constant nonzero
/// PLINEWID is authored independently of FILLMODE; snapshot compilation retains
/// the drawing-level fill/outline policy without changing entity geometry.
/// Apply/Undo/Redo are O(S), and retained command storage is O(S).
/// </remarks>
public sealed class CadAddPolylineCommand : CadEditCommand
{
    private readonly CadPolylineAuthoringSnapshot _snapshot;
    private LwPolyline? _polyline;
    private double _previousDefaultWidth;
    private bool _hasPreviousDefaultWidth;

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
            _previousDefaultWidth = document.Header.PolylineWidthDefault;
            polyline = CreatePolyline(document);
            _polyline = polyline;
            _hasPreviousDefaultWidth = true;
        }

        ValidateDetached(polyline);
        document.Entities.Add(polyline);
        if (_snapshot.ResultingDefaultWidth is double resultingDefaultWidth)
        {
            document.Header.PolylineWidthDefault = resultingDefaultWidth;
        }
    }

    internal override void Revert(CadDocument document)
    {
        LwPolyline polyline = _polyline ?? throw new InvalidOperationException(
            "The PLINE command has not been applied.");
        if (_snapshot.ResultingDefaultWidth is not null && !_hasPreviousDefaultWidth)
        {
            throw new InvalidOperationException(
                "The prior PLINEWID state was not captured.");
        }
        ValidateModelSpaceEntity(document, polyline);
        if (!document.Entities.Remove(polyline))
        {
            throw new InvalidOperationException(
                "The authored PLINE could not be removed from model space.");
        }
        if (_snapshot.ResultingDefaultWidth is not null)
        {
            document.Header.PolylineWidthDefault = _previousDefaultWidth;
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
        if (defaultWidth > float.MaxValue)
        {
            throw new InvalidOperationException(
                "Current PLINEWID exceeds the retained float stroke domain.");
        }
        double lineTypeScale = document.Header.CurrentEntityLinetypeScale;
        if (!double.IsFinite(lineTypeScale) || lineTypeScale <= 0.0)
        {
            throw new InvalidOperationException(
                "Current CELTSCALE must be finite and positive before creating a PLINE.");
        }

        ReadOnlySpan<double> bulges = _snapshot.Bulges.Span;
        double entityWidth = defaultWidth;
        bool hasVariableWidth = false;
        if (_snapshot.HasExplicitWidths)
        {
            ReadOnlySpan<double> startWidths = _snapshot.StartWidths.Span;
            ReadOnlySpan<double> endWidths = _snapshot.EndWidths.Span;
            int segmentCount = _snapshot.SegmentCount;
            entityWidth = startWidths[0];
            for (int i = 0; i < segmentCount; i++)
            {
                hasVariableWidth |=
                    startWidths[i] != entityWidth || endWidths[i] != entityWidth;
            }
            if (hasVariableWidth)
            {
                for (int i = 0; i < segmentCount; i++)
                {
                    if (bulges[i] != 0.0)
                    {
                        throw new CadUnsupportedEntityException(
                            "A PLINE containing an arc currently requires one uniform entity-wide width.");
                    }
                }
                entityWidth = 0.0;
            }
        }

        var polyline = new LwPolyline
        {
            Layer = layer,
            Color = document.Header.CurrentEntityColor,
            LineType = document.Header.CurrentLineType,
            LineTypeScale = lineTypeScale,
            LineWeight = document.Header.CurrentEntityLineWeight,
            ConstantWidth = entityWidth,
            Elevation = _snapshot.Elevation,
            Normal = XYZ.AxisZ,
            Flags = document.Header.PolylineLineTypeGeneration && !hasVariableWidth
                ? LwPolylineFlags.Plinegen
                : LwPolylineFlags.Default,
        };
        polyline.IsClosed = _snapshot.IsClosed;
        ReadOnlySpan<CadPoint3D> points = _snapshot.Points.Span;
        ReadOnlySpan<double> explicitStartWidths = _snapshot.StartWidths.Span;
        ReadOnlySpan<double> explicitEndWidths = _snapshot.EndWidths.Span;
        int authoredSegmentCount = _snapshot.SegmentCount;
        for (int i = 0; i < points.Length; i++)
        {
            var vertex = new LwPolyline.Vertex(points[i].X, points[i].Y)
            {
                Bulge = bulges[i],
            };
            if (hasVariableWidth && i < authoredSegmentCount)
            {
                vertex.StartWidth = explicitStartWidths[i];
                vertex.EndWidth = explicitEndWidths[i];
            }
            polyline.Vertices.Add(vertex);
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

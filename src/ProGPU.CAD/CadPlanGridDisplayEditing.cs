using ACadSharp;
using ACadSharp.Tables;
using CSMath;

namespace ProGPU.CAD;

/// <summary>
/// Persisted rectangular drafting-grid values editable on the active VPORT.
/// </summary>
/// <remarks>
/// Zero GRIDUNIT components retain AutoCAD's documented meaning: inherit the
/// matching SNAPUNIT component. GRIDDISPLAY bit 8 (dynamic-UCS following) and
/// any unknown bits are deliberately outside this edit and remain unchanged.
/// </remarks>
public readonly record struct CadPlanGridDisplayEditValues
{
    public bool IsVisible { get; }
    public double GridUnitX { get; }
    public double GridUnitY { get; }
    public bool IsAdaptive { get; }
    public bool AllowsSubdivision { get; }
    public bool ShowsBeyondLimits { get; }
    public int MinorLinesPerMajorLine { get; }

    public CadPlanGridDisplayEditValues(
        bool isVisible,
        double gridUnitX,
        double gridUnitY,
        bool isAdaptive,
        bool allowsSubdivision,
        bool showsBeyondLimits,
        int minorLinesPerMajorLine)
    {
        if (!double.IsFinite(gridUnitX) || gridUnitX < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(gridUnitX));
        }
        if (!double.IsFinite(gridUnitY) || gridUnitY < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(gridUnitY));
        }
        if (minorLinesPerMajorLine is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(minorLinesPerMajorLine));
        }

        IsVisible = isVisible;
        GridUnitX = gridUnitX;
        GridUnitY = gridUnitY;
        IsAdaptive = isAdaptive;
        AllowsSubdivision = allowsSubdivision;
        ShowsBeyondLimits = showsBeyondLimits;
        MinorLinesPerMajorLine = minorLinesPerMajorLine;
    }

    public static CadPlanGridDisplayEditValues Capture(CadDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        VPort active = GetActiveViewport(document);
        return Capture(active);
    }

    internal static CadPlanGridDisplayEditValues Capture(VPort active) => new(
        active.ShowGrid,
        active.GridSpacing.X,
        active.GridSpacing.Y,
        (((short)active.GridFlags) & 2) != 0,
        (((short)active.GridFlags) & 4) != 0,
        (((short)active.GridFlags) & 1) != 0,
        active.MinorGridLinesPerMajorGridLine);

    internal static VPort GetActiveViewport(CadDocument document)
    {
        if (!document.VPorts.TryGetValue(VPort.DefaultName, out VPort? active) ||
            active is null)
        {
            throw new InvalidOperationException(
                "The drawing does not contain an active VPORT entry.");
        }
        return active;
    }
}

/// <summary>
/// Replaces the active VPORT's persisted rectangular grid-display values as
/// one generation-safe reversible edit.
/// </summary>
/// <remarks>
/// Apply, Undo, and Redo are O(1). The command retains the exact VPORT identity
/// and all touched/raw GRIDDISPLAY state so an intervening replacement or
/// mutation fails rather than overwriting newer data.
/// </remarks>
public sealed class CadSetPlanGridDisplayCommand : CadEditCommand
{
    private const short EditableGridFlags = 1 | 2 | 4;

    private VPort? _activeViewport;
    private VPortState _previousState;
    private VPortState _appliedState;

    public CadPlanGridDisplayEditValues Values { get; }

    public CadSetPlanGridDisplayCommand(
        CadPlanGridDisplayEditValues values,
        string description = "Edit active drafting grid")
        : base(description)
    {
        Values = values;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        VPort active;
        if (isRedo)
        {
            active = GetRetainedViewport(document, _previousState);
        }
        else
        {
            active = CadPlanGridDisplayEditValues.GetActiveViewport(document);
            _previousState = VPortState.Capture(active);
            if (HasEditableValues(active, Values))
            {
                throw new InvalidOperationException(
                    "The active drafting-grid display already has those values.");
            }

            _activeViewport = active;
            short preservedFlags = (short)(
                ((short)active.GridFlags) & ~EditableGridFlags);
            short replacementFlags = (short)(
                (Values.ShowsBeyondLimits ? 1 : 0) |
                (Values.IsAdaptive ? 2 : 0) |
                (Values.AllowsSubdivision ? 4 : 0));
            _appliedState = new VPortState(
                Values.IsVisible,
                Values.GridUnitX,
                Values.GridUnitY,
                (GridFlags)(preservedFlags | replacementFlags),
                checked((short)Values.MinorLinesPerMajorLine));
        }

        _appliedState.Apply(active);
    }

    private static bool HasEditableValues(
        VPort active,
        CadPlanGridDisplayEditValues values)
    {
        short flags = (short)active.GridFlags;
        return active.ShowGrid == values.IsVisible &&
            active.GridSpacing.X == values.GridUnitX &&
            active.GridSpacing.Y == values.GridUnitY &&
            ((flags & 2) != 0) == values.IsAdaptive &&
            ((flags & 4) != 0) == values.AllowsSubdivision &&
            ((flags & 1) != 0) == values.ShowsBeyondLimits &&
            active.MinorGridLinesPerMajorGridLine ==
                values.MinorLinesPerMajorLine;
    }

    internal override void Revert(CadDocument document)
    {
        VPort active = GetRetainedViewport(document, _appliedState);
        _previousState.Apply(active);
    }

    private VPort GetRetainedViewport(
        CadDocument document,
        VPortState expectedState)
    {
        VPort retained = _activeViewport ?? throw new InvalidOperationException(
            "The drafting-grid command has not been applied.");
        VPort current = CadPlanGridDisplayEditValues.GetActiveViewport(document);
        if (!ReferenceEquals(current, retained))
        {
            throw new InvalidOperationException(
                "The active VPORT is no longer the retained VPORT.");
        }
        if (VPortState.Capture(retained) != expectedState)
        {
            throw new InvalidOperationException(
                "The active VPORT drafting-grid state changed unexpectedly.");
        }
        return retained;
    }

    private readonly record struct VPortState(
        bool IsVisible,
        double GridUnitX,
        double GridUnitY,
        GridFlags GridFlags,
        short MinorLinesPerMajorLine)
    {
        public static VPortState Capture(VPort active) => new(
            active.ShowGrid,
            active.GridSpacing.X,
            active.GridSpacing.Y,
            active.GridFlags,
            active.MinorGridLinesPerMajorGridLine);

        public void Apply(VPort active)
        {
            active.ShowGrid = IsVisible;
            active.GridSpacing = new XY(GridUnitX, GridUnitY);
            active.GridFlags = GridFlags;
            active.MinorGridLinesPerMajorGridLine = MinorLinesPerMajorLine;
        }
    }
}

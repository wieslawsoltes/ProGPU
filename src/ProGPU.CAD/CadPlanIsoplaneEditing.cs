using ACadSharp;
using ACadSharp.Tables;

namespace ProGPU.CAD;

/// <summary>
/// Cycles the active VPORT's drawing-persisted SNAPISOPAIR value through
/// Left, Top, and Right as one generation-safe reversible edit.
/// </summary>
/// <remarks>
/// Apply, Undo, and Redo are O(1). The command changes only SNAPISOPAIR and
/// retains the exact active VPORT identity plus the relevant SNAPSTYL state so
/// replacement or intervening mutation fails instead of overwriting newer data.
/// </remarks>
public sealed class CadCyclePlanIsoplaneCommand : CadEditCommand
{
    private VPort? _activeViewport;
    private VPortState _previousState;
    private VPortState _appliedState;

    public CadPlanIsoplane? AppliedIsoplane { get; private set; }

    public CadCyclePlanIsoplaneCommand(
        string description = "Cycle active isoplane")
        : base(description)
    {
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
            _activeViewport = active;
            _previousState = VPortState.Capture(active);
            CadPlanIsoplane current = ParseIsoplane(active.SnapIsoPair);
            CadPlanIsoplane next = current switch
            {
                CadPlanIsoplane.Left => CadPlanIsoplane.Top,
                CadPlanIsoplane.Top => CadPlanIsoplane.Right,
                CadPlanIsoplane.Right => CadPlanIsoplane.Left,
                _ => throw new ArgumentOutOfRangeException(nameof(current)),
            };
            _appliedState = _previousState with
            {
                SnapIsoPair = checked((short)next),
            };
            AppliedIsoplane = next;
        }

        active.SnapIsoPair = _appliedState.SnapIsoPair;
    }

    internal override void Revert(CadDocument document)
    {
        VPort active = GetRetainedViewport(document, _appliedState);
        active.SnapIsoPair = _previousState.SnapIsoPair;
    }

    private VPort GetRetainedViewport(
        CadDocument document,
        VPortState expectedState)
    {
        VPort retained = _activeViewport ?? throw new InvalidOperationException(
            "The isoplane command has not been applied.");
        VPort current = CadPlanGridDisplayEditValues.GetActiveViewport(document);
        if (!ReferenceEquals(current, retained))
        {
            throw new InvalidOperationException(
                "The active VPORT is no longer the retained VPORT.");
        }
        if (VPortState.Capture(retained) != expectedState)
        {
            throw new InvalidOperationException(
                "The active VPORT isometric drafting state changed unexpectedly.");
        }
        return retained;
    }

    private static CadPlanIsoplane ParseIsoplane(short rawValue)
    {
        CadPlanIsoplane isoplane = (CadPlanIsoplane)rawValue;
        if (!Enum.IsDefined(isoplane))
        {
            throw new InvalidOperationException(
                $"The active VPORT SNAPISOPAIR value {rawValue} is invalid.");
        }
        return isoplane;
    }

    private readonly record struct VPortState(
        bool IsometricSnap,
        short SnapIsoPair)
    {
        public static VPortState Capture(VPort active) => new(
            active.IsometricSnap,
            active.SnapIsoPair);
    }
}

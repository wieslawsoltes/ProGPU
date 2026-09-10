using ACadSharp;
using ACadSharp.Tables;

namespace ProGPU.CAD;

/// <summary>
/// Replaces active-VPORT SNAPMODE as one generation-safe reversible edit.
/// </summary>
/// <remarks>
/// Apply, Undo, and Redo are O(1). The exact active VPORT identity and expected
/// pre/post value are retained so replacement or intervening SNAPMODE mutation
/// fails instead of overwriting newer drawing state.
/// </remarks>
public sealed class CadSetPlanSnapModeCommand : CadEditCommand
{
    private VPort? _activeViewport;
    private bool _previousValue;

    public bool IsEnabled { get; }

    public CadSetPlanSnapModeCommand(
        bool isEnabled,
        string description = "Set Snap mode")
        : base(description)
    {
        IsEnabled = isEnabled;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        VPort active;
        if (isRedo)
        {
            active = GetRetainedViewport(document, _previousValue);
        }
        else
        {
            active = CadPlanGridDisplayEditValues.GetActiveViewport(document);
            if (active.SnapOn == IsEnabled)
            {
                throw new InvalidOperationException(
                    $"Active-VPORT SNAPMODE is already {(IsEnabled ? 1 : 0)}.");
            }

            _activeViewport = active;
            _previousValue = active.SnapOn;
        }

        active.SnapOn = IsEnabled;
    }

    internal override void Revert(CadDocument document)
    {
        VPort active = GetRetainedViewport(document, IsEnabled);
        active.SnapOn = _previousValue;
    }

    private VPort GetRetainedViewport(
        CadDocument document,
        bool expectedValue)
    {
        VPort retained = _activeViewport ?? throw new InvalidOperationException(
            "The Snap-mode command has not been applied.");
        VPort current = CadPlanGridDisplayEditValues.GetActiveViewport(document);
        if (!ReferenceEquals(current, retained))
        {
            throw new InvalidOperationException(
                "The active VPORT is no longer the retained VPORT.");
        }
        if (retained.SnapOn != expectedValue)
        {
            throw new InvalidOperationException(
                $"Active-VPORT SNAPMODE changed from the expected value " +
                $"{(expectedValue ? 1 : 0)}.");
        }
        return retained;
    }
}

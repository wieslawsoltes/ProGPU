using ACadSharp;
using ACadSharp.Header;

namespace ProGPU.CAD;

/// <summary>
/// Replaces drawing-persisted ORTHOMODE as one generation-safe reversible edit.
/// </summary>
/// <remarks>
/// Apply, Undo, and Redo are O(1). The exact header identity and expected value
/// are retained so replacement or intervening mutation fails instead of
/// overwriting newer drawing state.
/// </remarks>
public sealed class CadSetOrthoModeCommand : CadEditCommand
{
    private CadHeader? _header;
    private bool _previousValue;

    public bool IsEnabled { get; }

    public CadSetOrthoModeCommand(
        bool isEnabled,
        string description = "Set Ortho mode")
        : base(description)
    {
        IsEnabled = isEnabled;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        CadHeader header;
        if (isRedo)
        {
            header = GetRetainedHeader(document, _previousValue);
        }
        else
        {
            header = document.Header;
            if (header.OrthoMode == IsEnabled)
            {
                throw new InvalidOperationException(
                    $"Drawing ORTHOMODE is already {(IsEnabled ? 1 : 0)}.");
            }

            _header = header;
            _previousValue = header.OrthoMode;
        }

        header.OrthoMode = IsEnabled;
    }

    internal override void Revert(CadDocument document)
    {
        CadHeader header = GetRetainedHeader(document, IsEnabled);
        header.OrthoMode = _previousValue;
    }

    private CadHeader GetRetainedHeader(
        CadDocument document,
        bool expectedValue)
    {
        CadHeader header = _header ?? throw new InvalidOperationException(
            "The Ortho-mode command has not been applied.");
        if (!ReferenceEquals(document.Header, header))
        {
            throw new InvalidOperationException(
                "The drawing header is no longer the retained header.");
        }
        if (header.OrthoMode != expectedValue)
        {
            throw new InvalidOperationException(
                $"Drawing ORTHOMODE changed from the expected value " +
                $"{(expectedValue ? 1 : 0)}.");
        }
        return header;
    }
}

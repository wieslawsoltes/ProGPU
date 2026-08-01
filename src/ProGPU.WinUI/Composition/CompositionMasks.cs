using ProGPU.Scene;
using ProGPU.Vector;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Composition;

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionMaskBrush :
    CompositionBrush,
    ICompositionBrushOwner
{
    private CompositionBrush? _mask;
    private Brush? _maskSceneBrush;
    private CompositionBrush? _source;
    private Brush? _sourceSceneBrush;

    internal CompositionMaskBrush(Compositor compositor)
        : base(compositor)
    {
    }

    public CompositionBrush? Mask
    {
        get => _mask;
        set => SetInput(
            ref _mask,
            value,
            CompositionBrushInputKind.OpacityMask,
            nameof(value));
    }

    public CompositionBrush? Source
    {
        get => _source;
        set => SetInput(
            ref _source,
            value,
            CompositionBrushInputKind.MaskSource,
            nameof(value));
    }

    internal override bool RequiresSceneBrushScope => true;

    internal override int SceneCommandOverhead => 2;

    internal override Brush? BeginSceneBrush(
        DrawingContext context,
        in Rect bounds,
        ref Brush? sceneBrush,
        out bool popOpacityMask)
    {
        popOpacityMask = false;
        sceneBrush = null;
        if (_source is null || _mask is null)
            return null;

        _source.PrepareSceneBrush(
            context,
            bounds,
            ref _sourceSceneBrush);
        _mask.PrepareSceneBrush(
            context,
            bounds,
            ref _maskSceneBrush);
        if (_sourceSceneBrush is null || _maskSceneBrush is null)
            return null;

        context.PushOpacityMask(_maskSceneBrush, bounds);
        popOpacityMask = true;
        sceneBrush = _sourceSceneBrush;
        return sceneBrush;
    }

    internal override void EndSceneBrush(
        DrawingContext context,
        bool popOpacityMask)
    {
        if (popOpacityMask)
            context.PopOpacityMask();
    }

    void ICompositionBrushOwner.NotifyBrushValueChanged() =>
        NotifyOwnersChanged();

    internal override void OnDisposed()
    {
        _mask?.RemoveOwner(this);
        _source?.RemoveOwner(this);
        _mask = null;
        _source = null;
        _maskSceneBrush = null;
        _sourceSceneBrush = null;
        base.OnDisposed();
    }

    private void SetInput(
        ref CompositionBrush? field,
        CompositionBrush? value,
        CompositionBrushInputKind requiredKind,
        string parameterName)
    {
        ThrowIfDisposed();
        if (ReferenceEquals(field, value))
            return;
        if (value is not null)
        {
            value.ThrowIfDisposed();
            EnsureSameCompositor(value);
            if ((value.InputKinds & requiredKind) == 0)
            {
                throw new ArgumentException(
                    "The composition brush cannot be used in this mask input.",
                    parameterName);
            }
        }

        CompositionBrush? other = requiredKind ==
            CompositionBrushInputKind.OpacityMask
                ? _source
                : _mask;
        if (field is not null && !ReferenceEquals(field, other))
            field.RemoveOwner(this);
        field = value;
        field?.AddOwner(this);
        NotifyOwnersChanged();
    }
}

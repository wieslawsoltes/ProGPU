using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using WinRT;
using Windows.Foundation.Metadata;
using WinUiColor = Windows.UI.Color;

namespace Microsoft.UI.Composition;

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionDropShadowSourcePolicy
{
    Default = 0,
    InheritFromVisualContent = 1
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public class CompositionShadow : CompositionObject
{
    private List<WeakReference<ICompositionShadowOwner>>? _owners;

    protected internal CompositionShadow(IObjectReference objRef)
        : base(objRef)
    {
    }

    protected CompositionShadow(DerivedComposed _)
        : base(_)
    {
    }

    internal CompositionShadow(Compositor compositor)
        : base(compositor)
    {
    }

    internal void AddOwner(ICompositionShadowOwner owner)
    {
        ThrowIfDisposed();
        List<WeakReference<ICompositionShadowOwner>> owners =
            _owners ??= new List<WeakReference<ICompositionShadowOwner>>();
        for (int index = owners.Count - 1; index >= 0; index--)
        {
            if (!owners[index].TryGetTarget(
                    out ICompositionShadowOwner? existing))
            {
                owners.RemoveAt(index);
                continue;
            }
            if (ReferenceEquals(existing, owner))
                return;
        }
        owners.Add(new WeakReference<ICompositionShadowOwner>(owner));
    }

    internal void RemoveOwner(ICompositionShadowOwner owner)
    {
        if (_owners is null)
            return;
        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (!_owners[index].TryGetTarget(
                    out ICompositionShadowOwner? existing) ||
                ReferenceEquals(existing, owner))
            {
                _owners.RemoveAt(index);
            }
        }
    }

    internal virtual void UpdateSceneEffect(
        Visual owner,
        bool defaultInheritsContent,
        in Rect bounds,
        ref DropShadowEffect? sceneEffect)
    {
        sceneEffect = null;
    }

    internal void NotifyOwnersChanged()
    {
        if (_owners is null)
            return;
        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (_owners[index].TryGetTarget(
                    out ICompositionShadowOwner? owner))
            {
                owner.NotifyShadowChanged();
            }
            else
            {
                _owners.RemoveAt(index);
            }
        }
    }

    internal override void OnDisposed()
    {
        if (_owners is not null)
        {
            for (int index = _owners.Count - 1; index >= 0; index--)
            {
                if (_owners[index].TryGetTarget(
                        out ICompositionShadowOwner? owner))
                {
                    owner.NotifyShadowDisposed(this);
                }
            }
            _owners.Clear();
        }
        base.OnDisposed();
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class DropShadow : CompositionShadow, ICompositionBrushOwner
{
    private float _blurRadius = 9f;
    private WinUiColor _color = WinUiColor.FromArgb(255, 0, 0, 0);
    private CompositionBrush? _mask;
    private Vector3 _offset;
    private float _opacity = 1f;
    private CompositionDropShadowSourcePolicy _sourcePolicy;

    internal DropShadow(Compositor compositor)
        : base(compositor)
    {
    }

    public float BlurRadius
    {
        get => _blurRadius;
        set => SetNonNegative(ref _blurRadius, value);
    }

    public WinUiColor Color
    {
        get => _color;
        set
        {
            ThrowIfDisposed();
            if (_color == value)
                return;
            _color = value;
            NotifyOwnersChanged();
        }
    }

    public CompositionBrush? Mask
    {
        get => _mask;
        set
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_mask, value))
                return;
            if (value is not null)
                EnsureSameCompositor(value);
            _mask?.RemoveOwner(this);
            _mask = value;
            _mask?.AddOwner(this);
            NotifyOwnersChanged();
        }
    }

    public Vector3 Offset
    {
        get => _offset;
        set
        {
            ThrowIfDisposed();
            if (!IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_offset == value)
                return;
            _offset = value;
            NotifyOwnersChanged();
        }
    }

    public float Opacity
    {
        get => _opacity;
        set
        {
            ThrowIfDisposed();
            if (!float.IsFinite(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_opacity == value)
                return;
            _opacity = value;
            NotifyOwnersChanged();
        }
    }

    public CompositionDropShadowSourcePolicy SourcePolicy
    {
        get => _sourcePolicy;
        set
        {
            ThrowIfDisposed();
            if (value is not CompositionDropShadowSourcePolicy.Default and
                not CompositionDropShadowSourcePolicy
                    .InheritFromVisualContent)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (_sourcePolicy == value)
                return;
            _sourcePolicy = value;
            NotifyOwnersChanged();
        }
    }

    internal override void UpdateSceneEffect(
        Visual owner,
        bool defaultInheritsContent,
        in Rect bounds,
        ref DropShadowEffect? sceneEffect)
    {
        sceneEffect ??= new DropShadowEffect();
        sceneEffect.BlurRadius = _blurRadius;
        sceneEffect.Offset = new Vector2(_offset.X, _offset.Y);
        sceneEffect.Color = new Vector4(
            _color.R / 255f,
            _color.G / 255f,
            _color.B / 255f,
            (_color.A / 255f) * _opacity);

        bool inheritsContent = _mask is null &&
            (_sourcePolicy == CompositionDropShadowSourcePolicy
                .InheritFromVisualContent ||
             defaultInheritsContent);
        if (inheritsContent)
        {
            sceneEffect.OpacityMaskVisual = null;
            return;
        }

        DrawingVisual maskVisual =
            sceneEffect.OpacityMaskVisual as DrawingVisual ?? new DrawingVisual();
        maskVisual.Size = bounds.Size;
        maskVisual.Context.Clear();
        maskVisual.Context.EnsureCommandCapacity(1);
        Brush? maskBrush = null;
        if (_mask is null)
        {
            maskBrush = OpaqueMaskBrush;
        }
        else
        {
            _mask.UpdateSceneBrush(bounds, ref maskBrush);
        }
        if (maskBrush is not null && !bounds.IsEmpty)
            maskVisual.Context.DrawRectangle(maskBrush, null, bounds);
        maskVisual.Invalidate();
        sceneEffect.OpacityMaskVisual = maskVisual;
    }

    void ICompositionBrushOwner.NotifyBrushValueChanged() =>
        NotifyOwnersChanged();

    internal override void OnDisposed()
    {
        _mask?.RemoveOwner(this);
        _mask = null;
        base.OnDisposed();
    }

    private static SolidColorBrush OpaqueMaskBrush { get; } =
        new(Vector4.One);

    private void SetNonNegative(ref float field, float value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        NotifyOwnersChanged();
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

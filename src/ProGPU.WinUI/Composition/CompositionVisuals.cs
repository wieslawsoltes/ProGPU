using System.Collections;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using WinRT;
using Windows.Foundation.Metadata;
using WinUiColor = Windows.UI.Color;

namespace Microsoft.UI.Composition;

internal interface ICompositionBrushOwner
{
    void NotifyBrushValueChanged();
}

internal interface ICompositionShadowOwner
{
    void NotifyShadowChanged();

    void NotifyShadowDisposed(CompositionShadow shadow);
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public class CompositionBrush : CompositionObject
{
    private List<WeakReference<ICompositionBrushOwner>>? _owners;

    protected internal CompositionBrush(IObjectReference objRef)
        : base(objRef)
    {
    }

    protected CompositionBrush(DerivedComposed _)
        : base(_)
    {
    }

    internal CompositionBrush(Compositor compositor)
        : base(compositor)
    {
    }

    internal virtual void UpdateSceneBrush(
        in Rect bounds,
        ref Brush? sceneBrush)
    {
        sceneBrush = null;
    }

    internal void AddOwner(ICompositionBrushOwner owner)
    {
        List<WeakReference<ICompositionBrushOwner>> owners =
            _owners ??= new List<WeakReference<ICompositionBrushOwner>>();
        for (int index = owners.Count - 1; index >= 0; index--)
        {
            if (!owners[index].TryGetTarget(out ICompositionBrushOwner? existing))
            {
                owners.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, owner))
                return;
        }

        owners.Add(new WeakReference<ICompositionBrushOwner>(owner));
    }

    internal void RemoveOwner(ICompositionBrushOwner owner)
    {
        if (_owners is null)
            return;

        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (!_owners[index].TryGetTarget(out ICompositionBrushOwner? existing) ||
                ReferenceEquals(existing, owner))
            {
                _owners.RemoveAt(index);
            }
        }
    }

    internal void NotifyOwnersChanged()
    {
        if (_owners is null)
            return;

        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (_owners[index].TryGetTarget(out ICompositionBrushOwner? owner))
                owner.NotifyBrushValueChanged();
            else
                _owners.RemoveAt(index);
        }
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionColorBrush : CompositionBrush
{
    private WinUiColor _color;

    internal CompositionColorBrush(Compositor compositor, WinUiColor color)
        : base(compositor)
    {
        _color = color;
        SceneBrush = new SolidColorBrush(ToVector(color));
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
            SceneBrush.Color = ToVector(value);
            NotifyOwnersChanged();
        }
    }

    internal SolidColorBrush SceneBrush { get; }

    internal override void UpdateSceneBrush(
        in Rect bounds,
        ref Brush? sceneBrush) =>
        sceneBrush = SceneBrush;

    private static Vector4 ToVector(WinUiColor color) =>
        new(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public class Visual :
    CompositionObject,
    ICompositionClipOwner,
    ICompositionShadowOwner
{
    private readonly bool _ownsSceneNode;
    private ContainerVisual? _parent;
    private ProGPU.Scene.ContainerVisual? _externalHost;
    private Vector2 _anchorPoint;
    private CompositionClip? _clip;
    private CompositionShadow? _shadow;
    private DropShadowEffect? _sceneShadow;
    private bool _defaultShadowInheritsContent;
    private Vector3 _centerPoint;
    private Vector3 _offset;
    private Quaternion _orientation = Quaternion.Identity;
    private Vector3 _relativeOffsetAdjustment;
    private Vector2 _relativeSizeAdjustment;
    private float _rotationAngle;
    private Vector3 _rotationAxis = Vector3.UnitZ;
    private Vector3 _scale = Vector3.One;
    private Vector2 _size;
    private Matrix4x4 _transformMatrix = Matrix4x4.Identity;
    private bool _isVisible = true;
    private float _opacity = 1f;
    private Vector2 _parentSize;
    private Vector2 _effectiveSize;

    protected internal Visual(IObjectReference objRef)
        : base(objRef)
    {
        SceneNode = CreateSceneNode();
        _ownsSceneNode = true;
        ConnectSceneNode();
        RefreshSceneState();
    }

    protected Visual(DerivedComposed _)
        : base(_)
    {
        SceneNode = CreateSceneNode();
        _ownsSceneNode = true;
        ConnectSceneNode();
        RefreshSceneState();
    }

    internal Visual(Compositor compositor)
        : base(compositor)
    {
        SceneNode = CreateSceneNode();
        _ownsSceneNode = true;
        ConnectSceneNode();
        RefreshSceneState();
    }

    internal Visual(
        Compositor compositor,
        ProGPU.Scene.ContainerVisual sceneNode)
        : base(compositor)
    {
        SceneNode = sceneNode;
        _size = sceneNode.Size;
        _effectiveSize = sceneNode.Size;
        _offset = new Vector3(sceneNode.Offset, 0f);
        _centerPoint = sceneNode.CenterPoint;
        _scale = sceneNode.Scale;
        _rotationAngle = sceneNode.Rotation;
        _transformMatrix = sceneNode.Transform;
        _isVisible = sceneNode.IsVisible;
        _opacity = sceneNode.Opacity;
    }

    public Vector2 AnchorPoint
    {
        get => _anchorPoint;
        set
        {
            if (SetFinite(ref _anchorPoint, value))
                RefreshSceneState();
        }
    }

    public Vector3 CenterPoint
    {
        get => _centerPoint;
        set
        {
            if (SetFinite(ref _centerPoint, value))
                RefreshSceneState();
        }
    }

    public CompositionClip? Clip
    {
        get => _clip;
        set
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_clip, value))
                return;
            if (value is not null)
                EnsureSameCompositor(value);
            _clip?.RemoveOwner(this);
            _clip = value;
            _clip?.AddOwner(this);
            RefreshSceneClip();
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            ThrowIfDisposed();
            if (_isVisible == value)
                return;
            _isVisible = value;
            SceneNode.IsVisible = value;
        }
    }

    public Vector3 Offset
    {
        get => _offset;
        set
        {
            if (SetFinite(ref _offset, value))
                RefreshSceneState();
        }
    }

    public float Opacity
    {
        get => _opacity;
        set
        {
            ThrowIfDisposed();
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            float normalized = Math.Clamp(value, 0f, 1f);
            if (_opacity == normalized)
                return;
            _opacity = normalized;
            SceneNode.Opacity = normalized;
        }
    }

    public Quaternion Orientation
    {
        get => _orientation;
        set
        {
            ThrowIfDisposed();
            if (!IsFinite(value) || value.LengthSquared() <= float.Epsilon)
                throw new ArgumentOutOfRangeException(nameof(value));
            Quaternion normalized = Quaternion.Normalize(value);
            if (_orientation == normalized)
                return;
            _orientation = normalized;
            RefreshSceneState();
        }
    }

    public ContainerVisual? Parent => _parent;

    public Vector3 RelativeOffsetAdjustment
    {
        get => _relativeOffsetAdjustment;
        set
        {
            if (SetFinite(ref _relativeOffsetAdjustment, value))
                RefreshSceneState();
        }
    }

    public Vector2 RelativeSizeAdjustment
    {
        get => _relativeSizeAdjustment;
        set
        {
            if (SetFinite(ref _relativeSizeAdjustment, value))
                RefreshSceneState();
        }
    }

    public float RotationAngle
    {
        get => _rotationAngle;
        set
        {
            ThrowIfDisposed();
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_rotationAngle == value)
                return;
            _rotationAngle = value;
            RefreshSceneState();
        }
    }

    public float RotationAngleInDegrees
    {
        get => _rotationAngle * (180f / MathF.PI);
        set => RotationAngle = value * (MathF.PI / 180f);
    }

    public Vector3 RotationAxis
    {
        get => _rotationAxis;
        set
        {
            ThrowIfDisposed();
            if (!IsFinite(value) || value.LengthSquared() <= float.Epsilon)
                throw new ArgumentOutOfRangeException(nameof(value));
            Vector3 normalized = Vector3.Normalize(value);
            if (_rotationAxis == normalized)
                return;
            _rotationAxis = normalized;
            RefreshSceneState();
        }
    }

    public Vector3 Scale
    {
        get => _scale;
        set
        {
            if (SetFinite(ref _scale, value))
                RefreshSceneState();
        }
    }

    public Vector2 Size
    {
        get => _size;
        set
        {
            ThrowIfDisposed();
            if (!IsFinite(value) || value.X < 0f || value.Y < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_size == value)
                return;
            _size = value;
            RefreshSceneState();
        }
    }

    public Matrix4x4 TransformMatrix
    {
        get => _transformMatrix;
        set
        {
            ThrowIfDisposed();
            if (!IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_transformMatrix == value)
                return;
            _transformMatrix = value;
            RefreshSceneState();
        }
    }

    internal ProGPU.Scene.ContainerVisual SceneNode { get; }

    internal Vector2 EffectiveSize => _effectiveSize;

    internal CompositionShadow? GetShadow() => _shadow;

    internal void SetShadow(
        CompositionShadow? value,
        bool defaultInheritsContent)
    {
        ThrowIfDisposed();
        if (ReferenceEquals(_shadow, value) &&
            _defaultShadowInheritsContent == defaultInheritsContent)
        {
            return;
        }
        if (value is not null)
            EnsureSameCompositor(value);
        _shadow?.RemoveOwner(this);
        _shadow = value;
        _defaultShadowInheritsContent = defaultInheritsContent;
        _shadow?.AddOwner(this);
        RefreshSceneShadow();
    }

    internal void SetCompositionParent(ContainerVisual? parent)
    {
        _parent = parent;
        _externalHost = null;
        SetParentSize(parent?.EffectiveSize ?? Vector2.Zero);
    }

    internal void AttachToExternalHost(
        ProGPU.Scene.ContainerVisual host)
    {
        DetachFromCurrentParent();
        _externalHost = host;
        host.AddTopmostChild(SceneNode);
        SetParentSize(host.Size);
    }

    internal void DetachFromCurrentParent()
    {
        if (_parent is not null)
        {
            _parent.Children.Remove(this);
            return;
        }

        if (_externalHost is not null)
        {
            ProGPU.Scene.ContainerVisual host = _externalHost;
            _externalHost = null;
            host.RemoveChild(SceneNode);
            SetParentSize(Vector2.Zero);
        }
    }

    internal bool IsAttachedTo(ProGPU.Scene.ContainerVisual host) =>
        ReferenceEquals(_externalHost, host) &&
        ReferenceEquals(SceneNode.Parent, host);

    internal void SetParentSize(Vector2 parentSize)
    {
        if (_parentSize == parentSize)
            return;
        _parentSize = parentSize;
        RefreshSceneState();
    }

    internal override void OnDisposed()
    {
        DetachFromCurrentParent();
        _clip?.RemoveOwner(this);
        _shadow?.RemoveOwner(this);
        _clip = null;
        _shadow = null;
        _sceneShadow = null;
        SceneNode.Effect = null;
        SceneNode.LocalCompositeClip = null;
        if (_ownsSceneNode)
            SceneNode.ClearChildren();
        base.OnDisposed();
    }

    private CompositionSceneNode CreateSceneNode() => new();

    private void ConnectSceneNode()
    {
        if (SceneNode is CompositionSceneNode node)
            node.Owner = this;
    }

    private void RefreshSceneState()
    {
        Vector2 relativeSize =
            _relativeSizeAdjustment * _parentSize;
        Vector2 effectiveSize = Vector2.Max(
            Vector2.Zero,
            _size + relativeSize);
        bool sizeChanged = _effectiveSize != effectiveSize;
        _effectiveSize = effectiveSize;

        Vector3 position = _offset + new Vector3(
            _relativeOffsetAdjustment.X * _parentSize.X,
            _relativeOffsetAdjustment.Y * _parentSize.Y,
            0f);
        position -= new Vector3(
            _anchorPoint.X * _effectiveSize.X,
            _anchorPoint.Y * _effectiveSize.Y,
            0f);

        Quaternion axisRotation = Quaternion.CreateFromAxisAngle(
            _rotationAxis,
            _rotationAngle);
        Matrix4x4 localTransform =
            Matrix4x4.CreateTranslation(-_centerPoint) *
            Matrix4x4.CreateScale(_scale) *
            Matrix4x4.CreateFromQuaternion(axisRotation) *
            Matrix4x4.CreateFromQuaternion(_orientation) *
            Matrix4x4.CreateTranslation(_centerPoint) *
            _transformMatrix *
            Matrix4x4.CreateTranslation(position);

        SceneNode.Offset = Vector2.Zero;
        SceneNode.Scale = Vector3.One;
        SceneNode.Rotation = 0f;
        SceneNode.CenterPoint = Vector3.Zero;
        SceneNode.RenderTransformOrigin = Vector2.Zero;
        SceneNode.Size = _effectiveSize;
        SceneNode.Transform = localTransform;
        RefreshSceneClip();
        if (sizeChanged &&
            SceneNode is CompositionSceneNode compositionNode)
        {
            compositionNode.UpdateContent();
        }
        if (sizeChanged)
            RefreshSceneShadow();
    }

    void ICompositionClipOwner.NotifyClipChanged() =>
        RefreshSceneClip();

    void ICompositionClipOwner.NotifyClipDisposed(CompositionClip clip)
    {
        if (!ReferenceEquals(_clip, clip))
            return;
        _clip = null;
        SceneNode.LocalCompositeClip = null;
    }

    void ICompositionShadowOwner.NotifyShadowChanged() =>
        RefreshSceneShadow();

    void ICompositionShadowOwner.NotifyShadowDisposed(
        CompositionShadow shadow)
    {
        if (!ReferenceEquals(_shadow, shadow))
            return;
        _shadow = null;
        _sceneShadow = null;
        SceneNode.Effect = null;
    }

    private void RefreshSceneShadow()
    {
        if (_shadow is null)
        {
            _sceneShadow = null;
            SceneNode.Effect = null;
            return;
        }

        _shadow.UpdateSceneEffect(
            this,
            _defaultShadowInheritsContent,
            new Rect(0f, 0f, _effectiveSize.X, _effectiveSize.Y),
            ref _sceneShadow);
        SceneNode.Effect = _sceneShadow;
        SceneNode.Invalidate();
    }

    private void RefreshSceneClip()
    {
        SceneNode.LocalCompositeClip =
            _clip?.TryCreateSceneClip(_effectiveSize, out VisualCompositeClip value) == true
                ? value
                : null;
    }

    private bool SetFinite(
        ref Vector2 field,
        Vector2 value)
    {
        ThrowIfDisposed();
        if (!IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return false;
        field = value;
        return true;
    }

    private bool SetFinite(
        ref Vector3 field,
        Vector3 value)
    {
        ThrowIfDisposed();
        if (!IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return false;
        field = value;
        return true;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

    private static bool IsFinite(in Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public class ContainerVisual : Visual
{
    protected internal ContainerVisual(IObjectReference objRef)
        : base(objRef)
    {
        Children = new VisualCollection(Compositor, this);
    }

    protected ContainerVisual(DerivedComposed _)
        : base(_)
    {
        Children = new VisualCollection(Compositor, this);
    }

    internal ContainerVisual(Compositor compositor)
        : base(compositor)
    {
        Children = new VisualCollection(compositor, this);
    }

    public VisualCollection Children { get; }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class SpriteVisual : ContainerVisual, ICompositionBrushOwner
{
    private CompositionBrush? _brush;

    internal SpriteVisual(Compositor compositor)
        : base(compositor)
    {
    }

    public CompositionBrush? Brush
    {
        get => _brush;
        set
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_brush, value))
                return;
            if (value is not null)
                EnsureSameCompositor(value);
            _brush?.RemoveOwner(this);
            _brush = value;
            _brush?.AddOwner(this);
            NotifyBrushChanged();
        }
    }

    public CompositionShadow? Shadow
    {
        get => GetShadow();
        set => SetShadow(value, defaultInheritsContent: false);
    }

    internal void NotifyBrushChanged()
    {
        if (SceneNode is CompositionSceneNode node)
            node.UpdateContent();
    }

    void ICompositionBrushOwner.NotifyBrushValueChanged() =>
        NotifyBrushChanged();

    internal override void OnDisposed()
    {
        _brush?.RemoveOwner(this);
        _brush = null;
        base.OnDisposed();
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class LayerVisual : ContainerVisual
{
    internal LayerVisual(Compositor compositor)
        : base(compositor)
    {
    }

    public CompositionShadow? Shadow
    {
        get => GetShadow();
        set => SetShadow(value, defaultInheritsContent: true);
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class VisualCollection :
    CompositionObject,
    IEnumerable<Visual>
{
    private readonly ContainerVisual _owner;
    private readonly List<Visual> _children = new();

    internal VisualCollection(
        Compositor compositor,
        ContainerVisual owner)
        : base(compositor)
    {
        _owner = owner;
    }

    public int Count => _children.Count;

    public void InsertAbove(Visual newChild, Visual sibling)
    {
        if (ReferenceEquals(newChild, sibling))
            return;
        int siblingIndex = GetSiblingIndex(sibling);
        InsertCore(newChild, siblingIndex + 1);
    }

    public void InsertAtBottom(Visual newChild) => InsertCore(newChild, 0);

    public void InsertAtTop(Visual newChild) =>
        InsertCore(newChild, _children.Count);

    public void InsertBelow(Visual newChild, Visual sibling)
    {
        if (ReferenceEquals(newChild, sibling))
            return;
        int siblingIndex = GetSiblingIndex(sibling);
        InsertCore(newChild, siblingIndex);
    }

    public void Remove(Visual child)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(child);
        int index = _children.IndexOf(child);
        if (index < 0)
            return;

        _children.RemoveAt(index);
        _owner.SceneNode.RemoveChild(child.SceneNode);
        child.SetCompositionParent(null);
    }

    public void RemoveAll()
    {
        ThrowIfDisposed();
        for (int index = _children.Count - 1; index >= 0; index--)
        {
            Visual child = _children[index];
            _owner.SceneNode.RemoveChild(child.SceneNode);
            child.SetCompositionParent(null);
        }
        _children.Clear();
    }

    public IEnumerator<Visual> GetEnumerator() => _children.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int GetSiblingIndex(Visual sibling)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sibling);
        int index = _children.IndexOf(sibling);
        if (index < 0)
        {
            throw new ArgumentException(
                "The sibling is not in this VisualCollection.",
                nameof(sibling));
        }
        return index;
    }

    private void InsertCore(Visual newChild, int index)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(newChild);
        EnsureSameCompositor(newChild);
        for (Visual? ancestor = _owner;
             ancestor is not null;
             ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(newChild, ancestor))
            {
                throw new InvalidOperationException(
                    "A visual cannot be inserted beneath itself or one of its descendants.");
            }
        }

        int existingIndex = _children.IndexOf(newChild);
        if (existingIndex >= 0)
        {
            _children.RemoveAt(existingIndex);
            _owner.SceneNode.RemoveChild(newChild.SceneNode);
            if (existingIndex < index)
                index--;
        }
        else
        {
            newChild.DetachFromCurrentParent();
        }

        index = Math.Clamp(index, 0, _children.Count);
        _children.Insert(index, newChild);
        newChild.SetCompositionParent(_owner);
        _owner.SceneNode.InsertChild(index, newChild.SceneNode);
    }
}

internal sealed class CompositionSceneNode :
    ProGPU.Scene.ContainerVisual,
    IIncrementalRenderCommandCache,
    IParentSizeDependentVisual
{
    private readonly DrawingContext _commands = new();
    private Brush? _sceneBrush;

    internal Visual? Owner { get; set; }

    public bool HasRenderCommands => _commands.Commands.Count != 0;

    public int RenderCommandCount => _commands.Commands.Count;

    public override Rect? LocalRenderBounds =>
        HasRenderCommands ? new Rect(0f, 0f, Size.X, Size.Y) : null;

    public DrawingContext GetOrUpdateRenderCommandCache() => _commands;

    public RenderCommand GetRenderCommand(int index) =>
        _commands.Commands[index];

    public void OnParentSizeChanged(Vector2 parentSize) =>
        Owner?.SetParentSize(parentSize);

    internal void UpdateContent()
    {
        _commands.Clear();
        if (Owner is SpriteVisual { Brush: { } brush } &&
            Size.X > 0f && Size.Y > 0f)
        {
            brush.UpdateSceneBrush(
                new Rect(0f, 0f, Size.X, Size.Y),
                ref _sceneBrush);
            _commands.EnsureCommandCapacity(1);
            if (_sceneBrush is not null)
            {
                _commands.DrawRectangle(
                    _sceneBrush,
                    null,
                    new Rect(0f, 0f, Size.X, Size.Y));
            }
        }
        else if (Owner is ShapeVisual shapeVisual)
        {
            ClipBounds = new Rect(0f, 0f, Size.X, Size.Y);
            shapeVisual.RecordShapes(_commands);
        }
        else
        {
            ClipBounds = null;
            _sceneBrush = null;
        }
        Invalidate();
    }
}

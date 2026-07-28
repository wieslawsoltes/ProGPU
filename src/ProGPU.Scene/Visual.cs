using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Scene;

/// <summary>
/// Marks a visual whose <see cref="Visual.OnRender"/> implementation already owns
/// an immutable-until-invalidated command cache. The compositor must not retain a
/// second copy of that command stream.
/// </summary>
public interface IOwnedRenderCommandCache
{
    /// <summary>
    /// Gets whether the owned cache currently contains local render commands.
    /// Visual state and descendants are compiled regardless of this value.
    /// </summary>
    bool HasRenderCommands => true;

    DrawingContext GetOrUpdateRenderCommandCache();

    /// <summary>
    /// Gets the number of retained commands without requiring their storage to
    /// use <see cref="List{T}"/> or the general-purpose 560-byte
    /// <see cref="RenderCommand"/> array stride. The default preserves the
    /// original drawing-context representation.
    /// </summary>
    int RenderCommandCount =>
        GetOrUpdateRenderCommandCache().Commands.Count;

    /// <summary>
    /// Materializes one retained command for compilation. Implementations may
    /// keep a typed compact representation and expand it into this value on the
    /// stack; command identity is not observable and no heap allocation is
    /// required.
    /// </summary>
    RenderCommand GetRenderCommand(int index) =>
        GetOrUpdateRenderCommandCache().Commands[index];
}

/// <summary>
/// Identifies the exact late-bound presentation values used while expanding an
/// incremental command cache. Dependency bits make unrelated inherited state
/// collapse to one value, so pages vary only when their compiled output can
/// actually change.
/// </summary>
public readonly record struct IncrementalRenderPresentationState(
    RenderCommandPresentationDependencies Dependencies,
    TextureSamplingMode TextureSamplingMode,
    TextRenderingMode TextRenderingMode,
    TextHintingMode TextHintingMode);

/// <summary>
/// Opts an owned immutable-until-invalidated command cache into bounded local
/// scene-page compilation. Implementations must invalidate their visual when
/// the command stream or any referenced mutable resource changes.
/// </summary>
public interface IIncrementalRenderCommandCache : IOwnedRenderCommandCache
{
    /// <summary>
    /// Gets whether the current command stream is stable enough to retain as
    /// a compiled local scene page. Continuously changing producers should
    /// return false so they do not populate and immediately invalidate the
    /// bounded page cache.
    /// </summary>
    bool CanCacheIncrementalPage => true;

    /// <summary>
    /// Gets the exact state used to late-bind presentation-only command fields.
    /// Changing this value requires visual-state invalidation but does not
    /// require incrementing the immutable command-content revision.
    /// </summary>
    IncrementalRenderPresentationState IncrementalPresentationState =>
        default;
}

public readonly struct VisualCompositeClip : IEquatable<VisualCompositeClip>
{
    public VisualCompositeClip(Rect bounds, Matrix4x4 transform)
    {
        Bounds = bounds;
        Geometry = null;
        Transform = transform;
    }

    public VisualCompositeClip(
        PathGeometry geometry,
        Matrix4x4 transform)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        Bounds = null;
        Geometry = geometry;
        Transform = transform;
    }

    public Rect? Bounds { get; }
    public PathGeometry? Geometry { get; }
    public Matrix4x4 Transform { get; }

    public bool Equals(VisualCompositeClip other) =>
        Bounds == other.Bounds &&
        ReferenceEquals(Geometry, other.Geometry) &&
        Transform == other.Transform;

    public override bool Equals(object? obj) =>
        obj is VisualCompositeClip other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Bounds, Geometry, Transform);

    public static bool operator ==(
        VisualCompositeClip left,
        VisualCompositeClip right) =>
        left.Equals(right);

    public static bool operator !=(
        VisualCompositeClip left,
        VisualCompositeClip right) =>
        !left.Equals(right);
}

public class Visual
{
    private Vector2 _offset;
    private Vector2 _size;
    private bool _isVisible = true;
    private float _opacity = 1.0f;
    private Matrix4x4 _transform = Matrix4x4.Identity;
    private bool _isDirty = true;
    private long _changeVersion;
    private long _renderContentVersion;
    private long _treeVersion;
    public virtual bool HasTemplate => false;
    private Vector3 _scale = Vector3.One;
    private float _rotation = 0f;
    private Vector3 _centerPoint = Vector3.Zero;
    private Vector2 _renderTransformOrigin = new Vector2(0.5f, 0.5f);
    private Rect? _clipBounds;
    private int _hitTestId;
    private VisualColdState? _coldState;
    private int _activeAnimationSubtreeCount;
    private bool _hasActiveCustomAnimation;

    public EffectBase? Effect
    {
        get => _coldState?.Effect;
        set
        {
            EffectBase? current = _coldState?.Effect;
            if (!ReferenceEquals(current, value))
            {
                current?.RemoveOwner(this);
                if (value is null)
                {
                    if (_coldState is { } state)
                        state.Effect = null;
                }
                else
                {
                    GetOrCreateColdState().Effect = value;
                }
                value?.AddOwner(this);
                InvalidateVisualState();
            }
        }
    }

    /// <summary>
    /// Gets or sets the local bounds containing the uneffected subtree pixels.
    /// When unset, effects use the visual's layout size. Backends with retained
    /// subtree bounds should set this value so translated descendants are not
    /// clipped and effect textures stay proportional to their actual content.
    /// </summary>
    public Rect? EffectContentBounds
    {
        get => _coldState?.EffectContentBounds;
        set
        {
            Rect? current = _coldState?.EffectContentBounds;
            if (current != value)
            {
                if (value is null)
                {
                    if (_coldState is { } state)
                        state.EffectContentBounds = null;
                }
                else
                {
                    GetOrCreateColdState().EffectContentBounds = value;
                }
                InvalidateVisualState();
            }
        }
    }

    /// <summary>
    /// Gets or sets the logical transparent border reserved around
    /// <see cref="EffectContentBounds"/> while rasterizing an effect. A null
    /// value selects the effect's native default.
    /// </summary>
    public float? EffectRasterPadding
    {
        get => _coldState?.EffectRasterPadding;
        set
        {
            float? current = _coldState?.EffectRasterPadding;
            if (current != value)
            {
                if (value is null)
                {
                    if (_coldState is { } state)
                        state.EffectRasterPadding = null;
                }
                else
                {
                    GetOrCreateColdState().EffectRasterPadding = value;
                }
                InvalidateVisualState();
            }
        }
    }

    private ContainerVisual? _parent;
    public ContainerVisual? Parent
    {
        get => _parent;
        internal set
        {
            if (_parent != value)
            {
                var oldParent = _parent;
                if (_activeAnimationSubtreeCount != 0)
                    oldParent?.AdjustActiveAnimationSubtreeCount(-_activeAnimationSubtreeCount);
                _parent = value;
                if (_activeAnimationSubtreeCount != 0)
                    _parent?.AdjustActiveAnimationSubtreeCount(_activeAnimationSubtreeCount);
                OnParentChanged(oldParent, _parent);
            }
        }
    }

    protected virtual void OnParentChanged(ContainerVisual? oldParent, ContainerVisual? newParent)
    {
    }

    public Vector2 Offset
    {
        get => _offset;
        set
        {
            if (_offset != value)
            {
                _offset = value;
                InvalidateVisualState();
            }
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                InvalidateVisualState();
            }
        }
    }

    public Vector2 Size
    {
        get => _size;
        set
        {
            if (_size != value)
            {
                _size = value;
                Invalidate();
            }
        }
    }

    public float Opacity
    {
        get => _opacity;
        set
        {
            if (_opacity != value)
            {
                _opacity = value;
                InvalidateVisualState();
            }
        }
    }

    public Matrix4x4 Transform
    {
        get => _transform;
        set
        {
            if (_transform != value)
            {
                _transform = value;
                InvalidateVisualState();
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (value)
            {
                Invalidate();
            }
            else
            {
                _isDirty = false;
            }
        }
    }

    public long ChangeVersion => _changeVersion;
    public long TreeVersion => _treeVersion;

    internal long RenderContentVersion => _renderContentVersion;

    public bool CacheAsLayer
    {
        get => _coldState?.CacheAsLayer ?? false;
        set
        {
            bool current = _coldState?.CacheAsLayer ?? false;
            if (current != value)
            {
                if (value)
                    GetOrCreateColdState().CacheAsLayer = true;
                else if (_coldState is { } state)
                    state.CacheAsLayer = false;
                InvalidateVisualState();
            }
        }
    }

    /// <summary>
    /// Gets or sets the raster-resolution multiplier for a cached layer.
    /// A non-positive value suppresses layer rendering.
    /// </summary>
    public float LayerCacheRenderScale
    {
        get => _coldState?.LayerCacheRenderScale ?? 1f;
        set
        {
            float normalized = float.IsFinite(value)
                ? MathF.Max(0f, value)
                : 0f;
            float current = _coldState?.LayerCacheRenderScale ?? 1f;
            if (current != normalized)
            {
                if (normalized == 1f)
                {
                    if (_coldState is { } state)
                        state.LayerCacheRenderScale = 1f;
                }
                else
                {
                    GetOrCreateColdState().LayerCacheRenderScale = normalized;
                }
                InvalidateVisualState();
            }
        }
    }

    /// <summary>
    /// Gets or sets whether a cached layer's destination origin is aligned to
    /// the physical pixel grid before composition.
    /// </summary>
    public bool LayerCacheSnapsToDevicePixels
    {
        get => _coldState?.LayerCacheSnapsToDevicePixels ?? false;
        set
        {
            bool current =
                _coldState?.LayerCacheSnapsToDevicePixels ?? false;
            if (current != value)
            {
                if (value)
                {
                    GetOrCreateColdState().LayerCacheSnapsToDevicePixels =
                        true;
                }
                else if (_coldState is { } state)
                {
                    state.LayerCacheSnapsToDevicePixels = false;
                }
                InvalidateVisualState();
            }
        }
    }

    public Vector3 Scale
    {
        get => _scale;
        set
        {
            if (_scale != value)
            {
                _scale = value;
                InvalidateVisualState();
            }
        }
    }

    public float Rotation
    {
        get => _rotation;
        set
        {
            if (_rotation != value)
            {
                _rotation = value;
                InvalidateVisualState();
            }
        }
    }

    public Vector3 CenterPoint
    {
        get => _centerPoint;
        set
        {
            if (_centerPoint != value)
            {
                _centerPoint = value;
                InvalidateVisualState();
            }
        }
    }

    public Vector2 RenderTransformOrigin
    {
        get => _renderTransformOrigin;
        set
        {
            if (_renderTransformOrigin != value)
            {
                _renderTransformOrigin = value;
                InvalidateVisualState();
            }
        }
    }

    // Composition layer texture view
    public GpuTexture? LayerTexture
    {
        get => _coldState?.LayerTexture;
        internal set
        {
            if (value is null)
            {
                if (_coldState is { } state)
                    state.LayerTexture = null;
            }
            else
            {
                GetOrCreateColdState().LayerTexture = value;
            }
        }
    }

    public int HitTestId
    {
        get => _hitTestId;
        set
        {
            if (_hitTestId != value)
            {
                _hitTestId = value;
                InvalidateVisualState();
            }
        }
    }

    public Rect? ClipBounds
    {
        get => _clipBounds;
        set
        {
            if (_clipBounds != value)
            {
                _clipBounds = value;
                InvalidateVisualState();
            }
        }
    }

    public Rect? OuterClipBounds
    {
        get => _coldState?.OuterClipBounds;
        set
        {
            Rect? current = _coldState?.OuterClipBounds;
            if (current != value)
            {
                if (value is null)
                {
                    if (_coldState is { } state)
                        state.OuterClipBounds = null;
                }
                else
                {
                    GetOrCreateColdState().OuterClipBounds = value;
                }
                InvalidateVisualState();
            }
        }
    }

    public ReadOnlySpan<VisualCompositeClip> OuterCompositeClips =>
        _coldState?.OuterCompositeClips;

    public void SetOuterCompositeClips(
        IReadOnlyList<VisualCompositeClip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        VisualCompositeClip[] current =
            _coldState?.OuterCompositeClips ??
            Array.Empty<VisualCompositeClip>();
        bool unchanged = clips.Count == current.Length;
        if (unchanged)
        {
            for (int index = 0; index < clips.Count; index++)
            {
                if (clips[index] != current[index])
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (unchanged)
            return;

        if (clips.Count == 0)
        {
            if (_coldState is { } state)
            {
                state.OuterCompositeClips =
                    Array.Empty<VisualCompositeClip>();
            }
        }
        else
        {
            var replacement = new VisualCompositeClip[clips.Count];
            for (int index = 0; index < replacement.Length; index++)
                replacement[index] = clips[index];
            GetOrCreateColdState().OuterCompositeClips = replacement;
        }

        InvalidateVisualState();
    }

    public PathGeometry? GeometryClip
    {
        get => _coldState?.GeometryClip;
        set
        {
            PathGeometry? current = _coldState?.GeometryClip;
            if (!ReferenceEquals(current, value))
            {
                if (value is null)
                {
                    if (_coldState is { } state)
                        state.GeometryClip = null;
                }
                else
                {
                    GetOrCreateColdState().GeometryClip = value;
                }
                InvalidateVisualState();
            }
        }
    }

    public Brush? OpacityMask
    {
        get => _coldState?.OpacityMask;
        set
        {
            Brush? current = _coldState?.OpacityMask;
            if (current != value)
            {
                if (value is null)
                {
                    if (_coldState is { } state)
                        state.OpacityMask = null;
                }
                else
                {
                    GetOrCreateColdState().OpacityMask = value;
                }
                InvalidateVisualState();
            }
        }
    }

    public GpuPicture? OpacityMaskPicture
    {
        get => _coldState?.OpacityMaskPicture;
        set
        {
            GpuPicture? current = _coldState?.OpacityMaskPicture;
            if (!ReferenceEquals(current, value))
            {
                if (value is null)
                {
                    if (_coldState is { } state)
                        state.OpacityMaskPicture = null;
                }
                else
                {
                    GetOrCreateColdState().OpacityMaskPicture = value;
                }
                InvalidateVisualState();
            }
        }
    }

    public Rect? OpacityMaskBounds
    {
        get => _coldState?.OpacityMaskBounds;
        set
        {
            Rect? current = _coldState?.OpacityMaskBounds;
            if (current != value)
            {
                if (value is null)
                {
                    if (_coldState is { } state)
                        state.OpacityMaskBounds = null;
                }
                else
                {
                    GetOrCreateColdState().OpacityMaskBounds = value;
                }
                InvalidateVisualState();
            }
        }
    }

    public void Invalidate()
    {
        InvalidateCore(invalidateRenderContent: true);
    }

    protected void InvalidateVisualState()
    {
        InvalidateCore(invalidateRenderContent: false);
    }

    private void InvalidateCore(bool invalidateRenderContent)
    {
        unchecked
        {
            _changeVersion++;
            if (_changeVersion < 0)
            {
                _changeVersion = 1;
            }

            if (invalidateRenderContent)
            {
                _renderContentVersion++;
                if (_renderContentVersion < 0)
                {
                    _renderContentVersion = 1;
                }
            }
        }

        _isDirty = true;
        Parent?.InvalidateCore(invalidateRenderContent: false);
    }

    public virtual void OnRender(DrawingContext context)
    {
        // Base visual does not record operations directly
    }

    /// <summary>
    /// Gets conservative local bounds for commands emitted directly by <see cref="OnRender"/>.
    /// A null value disables clip culling. Descendants are always traversed independently.
    /// </summary>
    public virtual Rect? LocalRenderBounds => null;

    public Matrix4x4 GetLocalTransform()
    {
        return GetLocalTransform(Offset);
    }

    public Matrix4x4 GetLocalTransform(Vector2 offset)
    {
        Vector3 anchor = new Vector3(Size.X * RenderTransformOrigin.X, Size.Y * RenderTransformOrigin.Y, 0f);
        if (CenterPoint != Vector3.Zero)
        {
            anchor = CenterPoint;
        }

        var translationToOrigin = Matrix4x4.CreateTranslation(-anchor.X, -anchor.Y, -anchor.Z);
        var scaleMatrix = Matrix4x4.CreateScale(Scale);
        var rotationMatrix = Matrix4x4.CreateRotationZ(Rotation);
        var translationToOffsetAndRestoreCenter = Matrix4x4.CreateTranslation(offset.X + anchor.X, offset.Y + anchor.Y, anchor.Z);

        var modelMatrix = translationToOrigin * scaleMatrix * rotationMatrix * translationToOffsetAndRestoreCenter;
        return Transform * modelMatrix;
    }

    public Matrix4x4 GetGlobalTransformMatrix()
    {
        var local = GetLocalTransform();
        if (Parent == null) return local;
        return local * Parent.GetGlobalTransformMatrix();
    }

    /// <summary>
    /// Gets the transform from this visual's public coordinate frame to its
    /// physical local coordinate frame. Framework integrations override this
    /// for direction-sensitive coordinate systems without reflecting render
    /// content such as text.
    /// </summary>
    protected virtual Matrix4x4 GetCoordinateFrameTransform() => Matrix4x4.Identity;

    /// <summary>
    /// Gets the transform from this visual's public coordinate frame to the
    /// root coordinate frame, including direction-sensitive coordinate rules.
    /// </summary>
    public Matrix4x4 GetGlobalCoordinateTransformMatrix() =>
        GetCoordinateFrameTransform() * GetGlobalTransformMatrix();

    public GeneralTransform TransformToVisual(Visual? visual)
    {
        var globalA = GetGlobalCoordinateTransformMatrix();
        if (visual == null)
        {
            return new GeneralTransform(globalA);
        }
        var globalB = visual.GetGlobalCoordinateTransformMatrix();
        if (Matrix4x4.Invert(globalB, out var invB))
        {
            return new GeneralTransform(globalA * invB);
        }
        return new GeneralTransform(globalA);
    }

    public void StartAnimation(string propertyName, CompositionAnimation animation)
    {
        var animations = GetOrCreateColdState().ActiveAnimations ??=
            new Dictionary<string, CompositionAnimation>(
                StringComparer.OrdinalIgnoreCase);
        var wasEmpty = animations.Count == 0;
        animations[propertyName] = animation;
        if (wasEmpty)
            AdjustActiveAnimationSubtreeCount(1);
        InvalidateVisualState();
    }

    public void StopAnimation(string propertyName)
    {
        if (_coldState?.ActiveAnimations?.Remove(propertyName) == true)
        {
            if (_coldState.ActiveAnimations.Count == 0)
                AdjustActiveAnimationSubtreeCount(-1);
            InvalidateVisualState();
        }
    }

    public void UpdateAnimations(float elapsedSeconds)
    {
        if (_activeAnimationSubtreeCount == 0)
            return;

        if (_hasActiveCustomAnimation)
            OnUpdateAnimations(elapsedSeconds);
        TickAnimations(elapsedSeconds);

        if (this is ContainerVisual container)
        {
            var children = container.Children;
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child._activeAnimationSubtreeCount != 0)
                    child.UpdateAnimations(elapsedSeconds);
            }
        }
    }

    /// <summary>
    /// Marks a control-owned animation such as kinetic scrolling as active.
    /// The state is propagated to ancestors so inactive branches can be skipped.
    /// </summary>
    protected void SetCustomAnimationActive(bool isActive)
    {
        if (_hasActiveCustomAnimation == isActive)
            return;

        _hasActiveCustomAnimation = isActive;
        AdjustActiveAnimationSubtreeCount(isActive ? 1 : -1);
    }

    internal int ActiveAnimationSubtreeCount => _activeAnimationSubtreeCount;

    private void AdjustActiveAnimationSubtreeCount(int delta)
    {
        _activeAnimationSubtreeCount += delta;
        if (_activeAnimationSubtreeCount < 0)
            throw new InvalidOperationException("Animation subtree activity became unbalanced.");
        Parent?.AdjustActiveAnimationSubtreeCount(delta);
    }

    internal void InvalidateTreeVersion()
    {
        _treeVersion++;
        Parent?.InvalidateTreeVersion();
    }

    protected virtual void OnUpdateAnimations(float elapsedSeconds)
    {
    }

    public void TickAnimations(float elapsedSeconds)
    {
        if (_coldState?.ActiveAnimations is not
            { Count: > 0 } activeAnimations)
            return;

        bool changed = false;
        bool renderContentChanged = false;
        List<string>? completedProperties = null;

        var activeAnimationEnumerator = activeAnimations.GetEnumerator();
        while (activeAnimationEnumerator.MoveNext())
        {
            var kvp = activeAnimationEnumerator.Current;
            var propertyName = kvp.Key;
            var animation = kvp.Value;

            animation.Tick(elapsedSeconds);
            if (animation.IsCompleted)
                (completedProperties ??= new List<string>()).Add(propertyName);

            var value = animation.CurrentValue;
            if (value == null) continue;

            if (IsAnimationProperty(propertyName, "opacity"))
            {
                if (value is float fOpacity)
                {
                    if (_opacity != fOpacity)
                    {
                        _opacity = fOpacity;
                        changed = true;
                    }
                }
            }
            else if (IsAnimationProperty(propertyName, "rotation"))
            {
                if (value is float fRotation)
                {
                    if (_rotation != fRotation)
                    {
                        _rotation = fRotation;
                        changed = true;
                    }
                }
            }
            else if (IsAnimationProperty(propertyName, "offset"))
            {
                if (value is Vector2 vOffset)
                {
                    if (_offset != vOffset)
                    {
                        _offset = vOffset;
                        changed = true;
                    }
                }
            }
            else if (IsAnimationProperty(propertyName, "size"))
            {
                if (value is Vector2 vSize)
                {
                    if (_size != vSize)
                    {
                        _size = vSize;
                        changed = true;
                        renderContentChanged = true;
                    }
                }
            }
            else if (IsAnimationProperty(propertyName, "scale"))
            {
                if (value is Vector3 vScale)
                {
                    if (_scale != vScale)
                    {
                        _scale = vScale;
                        changed = true;
                    }
                }
                else if (value is Vector2 vScale2)
                {
                    var vScale3 = new Vector3(vScale2, 1.0f);
                    if (_scale != vScale3)
                    {
                        _scale = vScale3;
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            InvalidateCore(renderContentChanged);
        }

        if (completedProperties != null)
        {
            for (var index = 0; index < completedProperties.Count; index++)
                activeAnimations.Remove(completedProperties[index]);
            if (activeAnimations.Count == 0)
                AdjustActiveAnimationSubtreeCount(-1);
        }
    }

    private VisualColdState GetOrCreateColdState() =>
        _coldState ??= new VisualColdState();

    private sealed class VisualColdState
    {
        public Dictionary<string, CompositionAnimation>? ActiveAnimations;
        public Rect? OuterClipBounds;
        public VisualCompositeClip[] OuterCompositeClips =
            Array.Empty<VisualCompositeClip>();
        public PathGeometry? GeometryClip;
        public Brush? OpacityMask;
        public GpuPicture? OpacityMaskPicture;
        public Rect? OpacityMaskBounds;
        public Rect? EffectContentBounds;
        public float? EffectRasterPadding;
        public EffectBase? Effect;
        public bool CacheAsLayer;
        public float LayerCacheRenderScale = 1f;
        public bool LayerCacheSnapsToDevicePixels;
        public GpuTexture? LayerTexture;
    }

    private static bool IsAnimationProperty(string propertyName, string expected)
    {
        return string.Equals(propertyName, expected, StringComparison.OrdinalIgnoreCase);
    }
}

public class ContainerVisual : Visual
{
    private List<Visual>? _children;
    private readonly object _childrenLock = new();

    public IReadOnlyList<Visual> Children =>
        _children is null
            ? Array.Empty<Visual>()
            : _children;

    public void AddChild(Visual child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ThrowIfWouldCreateCycle(child);
        if (child.Parent != null)
        {
            child.Parent.RemoveChild(child);
        }

        lock (_childrenLock)
        {
            child.Parent = this;
            (_children ??= new List<Visual>()).Add(child);
        }
        InvalidateTreeVersion();
        Invalidate();
        if (this is ILayoutNode layoutNode)
        {
            layoutNode.InvalidateMeasure();
        }
    }

    public void InsertChild(int index, Visual child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ThrowIfWouldCreateCycle(child);
        if (child.Parent != null)
        {
            child.Parent.RemoveChild(child);
        }

        lock (_childrenLock)
        {
            child.Parent = this;
            var children = _children ??= new List<Visual>();
            children.Insert(Math.Clamp(index, 0, children.Count), child);
        }

        InvalidateTreeVersion();
        Invalidate();
        if (this is ILayoutNode layoutNode)
        {
            layoutNode.InvalidateMeasure();
        }
    }

    private void ThrowIfWouldCreateCycle(Visual child)
    {
        for (Visual? ancestor = this;
             ancestor != null;
             ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, child))
            {
                throw new InvalidOperationException(
                    "A visual cannot be added beneath itself or one of its descendants.");
            }
        }
    }

    public void RemoveChild(Visual child)
    {
        bool removed;
        lock (_childrenLock)
        {
            removed = _children?.Remove(child) == true;
            if (removed)
            {
                child.Parent = null;
            }
        }
        if (removed)
        {
            InvalidateTreeVersion();
            Invalidate();
            if (this is ILayoutNode layoutNode)
            {
                layoutNode.InvalidateMeasure();
            }
        }
    }

    public void ClearChildren()
    {
        lock (_childrenLock)
        {
            if (_children != null)
            {
                for (var i = 0; i < _children.Count; i++)
                {
                    _children[i].Parent = null;
                }
                _children.Clear();
            }
        }
        InvalidateTreeVersion();
        Invalidate();
        if (this is ILayoutNode layoutNode)
        {
            layoutNode.InvalidateMeasure();
        }
    }

    public void BringChildToFront(Visual child)
    {
        ArgumentNullException.ThrowIfNull(child);
        bool reordered = false;
        lock (_childrenLock)
        {
            if (ReferenceEquals(child.Parent, this) &&
                _children is { Count: > 0 } children &&
                !ReferenceEquals(children[^1], child))
            {
                children.Remove(child);
                children.Add(child);
                reordered = true;
            }
        }

        if (reordered)
        {
            InvalidateTreeVersion();
            Invalidate();
        }
    }
}

public class DrawingVisual : Visual
{
    public DrawingContext Context { get; } = new();

    public override void OnRender(DrawingContext context)
    {
        context.Append(Context);
    }
}

public abstract class EffectBase
{
    private readonly object _ownersLock = new();
    private readonly List<WeakReference<Visual>> _owners = new();
    private long _changeVersion;

    public long ChangeVersion => _changeVersion;

    internal void AddOwner(Visual owner)
    {
        lock (_ownersLock)
        {
            for (var i = _owners.Count - 1; i >= 0; i--)
            {
                if (!_owners[i].TryGetTarget(out var existing))
                {
                    _owners.RemoveAt(i);
                    continue;
                }

                if (ReferenceEquals(existing, owner))
                {
                    return;
                }
            }

            _owners.Add(new WeakReference<Visual>(owner));
        }
    }

    internal void RemoveOwner(Visual owner)
    {
        lock (_ownersLock)
        {
            for (var i = _owners.Count - 1; i >= 0; i--)
            {
                if (!_owners[i].TryGetTarget(out var existing) || ReferenceEquals(existing, owner))
                {
                    _owners.RemoveAt(i);
                }
            }
        }
    }

    protected void Invalidate()
    {
        unchecked
        {
            _changeVersion++;
            if (_changeVersion < 0)
            {
                _changeVersion = 1;
            }
        }

        NotifyOwners();
    }

    internal virtual int GetRenderCacheKey()
    {
        return HashCode.Combine(GetType(), ChangeVersion);
    }

    private void NotifyOwners()
    {
        Visual[]? owners = null;
        var ownerCount = 0;

        try
        {
            lock (_ownersLock)
            {
                for (var i = _owners.Count - 1; i >= 0; i--)
                {
                    if (!_owners[i].TryGetTarget(out var owner))
                    {
                        _owners.RemoveAt(i);
                        continue;
                    }

                    if (owners == null)
                    {
                        owners = ArrayPool<Visual>.Shared.Rent(Math.Max(4, _owners.Count));
                    }
                    else if (ownerCount == owners.Length)
                    {
                        Visual[] expandedOwners = ArrayPool<Visual>.Shared.Rent(owners.Length * 2);
                        Array.Copy(owners, expandedOwners, ownerCount);
                        ArrayPool<Visual>.Shared.Return(owners, clearArray: true);
                        owners = expandedOwners;
                    }

                    owners[ownerCount++] = owner;
                }
            }

            for (var i = 0; i < ownerCount; i++)
            {
                owners![i].Invalidate();
            }
        }
        finally
        {
            if (owners != null)
            {
                ArrayPool<Visual>.Shared.Return(owners, clearArray: true);
            }
        }
    }
}

public sealed class WpfShaderEffect : EffectBase
{
    private float _padding;
    private string? _failedShaderKey;
    private string? _failedShaderSourceKey;

    public WpfShaderEffect(WpfShaderEffectParams parameters)
    {
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public WpfShaderEffectParams Parameters { get; }

    public float Padding
    {
        get => _padding;
        set
        {
            if (_padding != value)
            {
                _padding = value;
                Invalidate();
            }
        }
    }

    public bool IsFailed => Parameters.IsFailed;

    public string? LastError => Parameters.LastError;

    internal void UpdateDrawParameters(WpfShaderEffectParams target, GpuTexture sourceTexture, Rect rect)
    {
        var currentShaderKey = Parameters.GetStableShaderKey();
        var currentShaderSourceKey = Parameters.GetStableShaderSourceKey();
        if (Parameters.IsFailed &&
            (!string.Equals(_failedShaderKey, currentShaderKey, StringComparison.Ordinal) ||
             !string.Equals(_failedShaderSourceKey, currentShaderSourceKey, StringComparison.Ordinal)))
        {
            Parameters.IsFailed = false;
            Parameters.LastError = null;
            _failedShaderKey = null;
            _failedShaderSourceKey = null;
        }

        if (target.IsFailed)
        {
            var targetShaderKey = target.GetStableShaderKey();
            var targetShaderSourceKey = target.GetStableShaderSourceKey();
            if (string.Equals(targetShaderKey, currentShaderKey, StringComparison.Ordinal) &&
                string.Equals(targetShaderSourceKey, currentShaderSourceKey, StringComparison.Ordinal))
            {
                Parameters.IsFailed = true;
                Parameters.LastError = target.LastError;
                _failedShaderKey = currentShaderKey;
                _failedShaderSourceKey = currentShaderSourceKey;
            }
            else
            {
                target.IsFailed = false;
                target.LastError = null;
            }
        }

        target.Texture = sourceTexture;
        target.Rect = rect;
        target.ShaderSource = Parameters.ShaderSource;
        target.ShaderKey = Parameters.ShaderKey;
        target.Constants = Parameters.Constants;
        target.Samplers = Parameters.Samplers;
        target.SamplingMode = Parameters.SamplingMode;
        target.SourceTextureRegisterIndex = Parameters.SourceTextureRegisterIndex;
        target.IsFailed = Parameters.IsFailed;
        target.LastError = Parameters.LastError;
        target.SourceTextureOverridesSampler = true;
    }

    internal override int GetRenderCacheKey()
    {
        var hash = new HashCode();
        hash.Add(GetType());
        hash.Add(ChangeVersion);
        hash.Add(Padding);
        Parameters.AddRenderCacheKey(ref hash);
        return hash.ToHashCode();
    }
}

public class BlurEffect : EffectBase
{
    private float _blurRadius;

    public float BlurRadius
    {
        get => _blurRadius;
        set
        {
            if (_blurRadius != value)
            {
                _blurRadius = value;
                Invalidate();
            }
        }
    }

    public BlurEffect(float blurRadius = 5f)
    {
        BlurRadius = blurRadius;
    }
}

public class DropShadowEffect : EffectBase
{
    private float _blurRadius;
    private Vector2 _offset;
    private Vector4 _color;

    public float BlurRadius
    {
        get => _blurRadius;
        set
        {
            if (_blurRadius != value)
            {
                _blurRadius = value;
                Invalidate();
            }
        }
    }

    public Vector2 Offset
    {
        get => _offset;
        set
        {
            if (_offset != value)
            {
                _offset = value;
                Invalidate();
            }
        }
    }

    public Vector4 Color
    {
        get => _color;
        set
        {
            if (_color != value)
            {
                _color = value;
                Invalidate();
            }
        }
    }

    public DropShadowEffect(float blurRadius = 5f, Vector2 offset = default, Vector4 color = default)
    {
        BlurRadius = blurRadius;
        Offset = offset;
        Color = color == default ? new Vector4(0f, 0f, 0f, 0.5f) : color;
    }
}


public interface ILayoutNode
{
    void InvalidateMeasure();
}

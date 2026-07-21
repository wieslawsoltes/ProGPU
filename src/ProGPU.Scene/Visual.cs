using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Scene;

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
    private bool _cacheAsLayer;
    public virtual bool HasTemplate => false;
    private Vector3 _scale = Vector3.One;
    private float _rotation = 0f;
    private Vector3 _centerPoint = Vector3.Zero;
    private Vector2 _renderTransformOrigin = new Vector2(0.5f, 0.5f);
    private readonly Dictionary<string, CompositionAnimation> _activeAnimations = new(StringComparer.OrdinalIgnoreCase);
    private Rect? _clipBounds;
    private Rect? _outerClipBounds;
    private PathGeometry? _geometryClip;
    private Brush? _opacityMask;
    private Rect? _opacityMaskBounds;
    private int _hitTestId;

    private EffectBase? _effect;
    public EffectBase? Effect
    {
        get => _effect;
        set
        {
            if (_effect != value)
            {
                _effect?.RemoveOwner(this);
                _effect = value;
                _effect?.AddOwner(this);
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
                _parent = value;
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

    internal long RenderContentVersion => _renderContentVersion;

    public bool CacheAsLayer
    {
        get => _cacheAsLayer;
        set
        {
            if (_cacheAsLayer != value)
            {
                _cacheAsLayer = value;
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
    public GpuTexture? LayerTexture { get; internal set; }

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
        get => _outerClipBounds;
        set
        {
            if (_outerClipBounds != value)
            {
                _outerClipBounds = value;
                InvalidateVisualState();
            }
        }
    }

    public PathGeometry? GeometryClip
    {
        get => _geometryClip;
        set
        {
            if (!ReferenceEquals(_geometryClip, value))
            {
                _geometryClip = value;
                InvalidateVisualState();
            }
        }
    }

    public Brush? OpacityMask
    {
        get => _opacityMask;
        set
        {
            if (_opacityMask != value)
            {
                _opacityMask = value;
                InvalidateVisualState();
            }
        }
    }

    public Rect? OpacityMaskBounds
    {
        get => _opacityMaskBounds;
        set
        {
            if (_opacityMaskBounds != value)
            {
                _opacityMaskBounds = value;
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
        _activeAnimations[propertyName] = animation;
        InvalidateVisualState();
    }

    public void StopAnimation(string propertyName)
    {
        if (_activeAnimations.Remove(propertyName))
        {
            InvalidateVisualState();
        }
    }

    public void UpdateAnimations(float elapsedSeconds)
    {
        OnUpdateAnimations(elapsedSeconds);
        TickAnimations(elapsedSeconds);

        if (this is ContainerVisual container)
        {
            var children = container.Children;
            for (int i = 0; i < children.Count; i++)
            {
                children[i].UpdateAnimations(elapsedSeconds);
            }
        }
    }

    protected virtual void OnUpdateAnimations(float elapsedSeconds)
    {
    }

    public void TickAnimations(float elapsedSeconds)
    {
        if (_activeAnimations.Count == 0) return;

        bool changed = false;
        bool renderContentChanged = false;

        var activeAnimationEnumerator = _activeAnimations.GetEnumerator();
        while (activeAnimationEnumerator.MoveNext())
        {
            var kvp = activeAnimationEnumerator.Current;
            var propertyName = kvp.Key;
            var animation = kvp.Value;

            animation.Tick(elapsedSeconds);

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
    }

    private static bool IsAnimationProperty(string propertyName, string expected)
    {
        return string.Equals(propertyName, expected, StringComparison.OrdinalIgnoreCase);
    }
}

public class ContainerVisual : Visual
{
    private readonly List<Visual> _children = new();
    private readonly object _childrenLock = new();

    public IReadOnlyList<Visual> Children => _children;

    public void AddChild(Visual child)
    {
        if (child.Parent != null)
        {
            child.Parent.RemoveChild(child);
        }

        lock (_childrenLock)
        {
            child.Parent = this;
            _children.Add(child);
        }
        Invalidate();
        if (this is ILayoutNode layoutNode)
        {
            layoutNode.InvalidateMeasure();
        }
    }

    public void InsertChild(int index, Visual child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.Parent != null)
        {
            child.Parent.RemoveChild(child);
        }

        lock (_childrenLock)
        {
            child.Parent = this;
            _children.Insert(Math.Clamp(index, 0, _children.Count), child);
        }

        Invalidate();
        if (this is ILayoutNode layoutNode)
        {
            layoutNode.InvalidateMeasure();
        }
    }

    public void RemoveChild(Visual child)
    {
        bool removed;
        lock (_childrenLock)
        {
            removed = _children.Remove(child);
            if (removed)
            {
                child.Parent = null;
            }
        }
        if (removed)
        {
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
            for (var i = 0; i < _children.Count; i++)
            {
                _children[i].Parent = null;
            }
            _children.Clear();
        }
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
                _children.Count > 0 &&
                !ReferenceEquals(_children[^1], child))
            {
                _children.Remove(child);
                _children.Add(child);
                reordered = true;
            }
        }

        if (reordered)
        {
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

using System;
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
    private bool _cacheAsLayer;
    public virtual bool HasTemplate => false;
    private Vector3 _scale = Vector3.One;
    private float _rotation = 0f;
    private Vector3 _centerPoint = Vector3.Zero;
    private Vector2 _renderTransformOrigin = new Vector2(0.5f, 0.5f);
    private readonly Dictionary<string, CompositionAnimation> _activeAnimations = new();
    private Rect? _clipBounds;

    private EffectBase? _effect;
    public EffectBase? Effect
    {
        get => _effect;
        set
        {
            if (_effect != value)
            {
                _effect = value;
                Invalidate();
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
                Invalidate();
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
                Invalidate();
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
                Invalidate();
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
                Invalidate();
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

    public bool CacheAsLayer
    {
        get => _cacheAsLayer;
        set
        {
            if (_cacheAsLayer != value)
            {
                _cacheAsLayer = value;
                Invalidate();
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
                Invalidate();
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
                Invalidate();
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
                Invalidate();
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
                Invalidate();
            }
        }
    }

    // Composition layer texture view
    public GpuTexture? LayerTexture { get; internal set; }

    public Rect? ClipBounds
    {
        get => _clipBounds;
        set
        {
            if (_clipBounds != value)
            {
                _clipBounds = value;
                Invalidate();
            }
        }
    }

    public void Invalidate()
    {
        unchecked
        {
            _changeVersion++;
            if (_changeVersion < 0)
            {
                _changeVersion = 1;
            }
        }

        _isDirty = true;
        Parent?.Invalidate();
    }

    public virtual void OnRender(DrawingContext context)
    {
        // Base visual does not record operations directly
    }

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

    public GeneralTransform TransformToVisual(Visual? visual)
    {
        var globalA = GetGlobalTransformMatrix();
        if (visual == null)
        {
            return new GeneralTransform(globalA);
        }
        var globalB = visual.GetGlobalTransformMatrix();
        if (Matrix4x4.Invert(globalB, out var invB))
        {
            return new GeneralTransform(globalA * invB);
        }
        return new GeneralTransform(globalA);
    }

    public void StartAnimation(string propertyName, CompositionAnimation animation)
    {
        _activeAnimations[propertyName] = animation;
        Invalidate();
    }

    public void StopAnimation(string propertyName)
    {
        if (_activeAnimations.Remove(propertyName))
        {
            Invalidate();
        }
    }

    public void UpdateAnimations(float elapsedSeconds)
    {
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

    public void TickAnimations(float elapsedSeconds)
    {
        if (_activeAnimations.Count == 0) return;

        bool changed = false;

        foreach (var kvp in _activeAnimations)
        {
            var propertyName = kvp.Key;
            var animation = kvp.Value;

            animation.Tick(elapsedSeconds);

            var value = animation.CurrentValue;
            if (value == null) continue;

            switch (propertyName.ToLowerInvariant())
            {
                case "opacity":
                    if (value is float fOpacity)
                    {
                        if (_opacity != fOpacity)
                        {
                            _opacity = fOpacity;
                            changed = true;
                        }
                    }
                    break;

                case "rotation":
                    if (value is float fRotation)
                    {
                        if (_rotation != fRotation)
                        {
                            _rotation = fRotation;
                            changed = true;
                        }
                    }
                    break;

                case "offset":
                    if (value is Vector2 vOffset)
                    {
                        if (_offset != vOffset)
                        {
                            _offset = vOffset;
                            changed = true;
                        }
                    }
                    break;

                case "size":
                    if (value is Vector2 vSize)
                    {
                        if (_size != vSize)
                        {
                            _size = vSize;
                            changed = true;
                        }
                    }
                    break;

                case "scale":
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
                    break;
            }
        }

        if (changed)
        {
            Invalidate();
        }
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
            foreach (var child in _children)
            {
                child.Parent = null;
            }
            _children.Clear();
        }
        Invalidate();
        if (this is ILayoutNode layoutNode)
        {
            layoutNode.InvalidateMeasure();
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
    private long _changeVersion;

    public long ChangeVersion => _changeVersion;

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
    }

    internal virtual int GetRenderCacheKey()
    {
        return HashCode.Combine(GetType(), ChangeVersion);
    }
}

public sealed class WpfShaderEffect : EffectBase
{
    private float _padding;

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
        if (target.IsFailed)
        {
            Parameters.IsFailed = true;
            Parameters.LastError = target.LastError;
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

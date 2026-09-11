using System.Numerics;
using Microsoft.UI.Dispatching;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Composition;

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionBitmapInterpolationMode
{
    NearestNeighbor = 0,
    Linear = 1,
    MagLinearMinLinearMipLinear = 2,
    MagLinearMinLinearMipNearest = 3,
    MagLinearMinNearestMipLinear = 4,
    MagLinearMinNearestMipNearest = 5,
    MagNearestMinLinearMipLinear = 6,
    MagNearestMinLinearMipNearest = 7,
    MagNearestMinNearestMipLinear = 8,
    MagNearestMinNearestMipNearest = 9
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public interface ICompositionSurface
{
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionSurfaceBrush : CompositionBrush
{
    private readonly EventHandler _textureChangedHandler;
    private readonly DispatcherQueueHandler _dispatchTextureChangedHandler;
    private readonly GpuTextureBrush _sceneBrush = new()
    {
        ExtendToFillBounds = false
    };
    private CompositionBitmapInterpolationMode _bitmapInterpolationMode =
        CompositionBitmapInterpolationMode.Linear;
    private CompositionStretch _stretch = CompositionStretch.Uniform;
    private ICompositionSurface? _surface;
    private bool _snapToPixels;
    private Matrix3x2 _transformMatrix = Matrix3x2.Identity;
    private Vector2 _anchorPoint;
    private Vector2 _centerPoint;
    private Vector2 _offset;
    private Vector2 _scale = Vector2.One;
    private float _horizontalAlignmentRatio = 0.5f;
    private float _rotationAngle;
    private float _verticalAlignmentRatio = 0.5f;
    private int _textureInvalidationPending;

    internal CompositionSurfaceBrush(
        Compositor compositor,
        ICompositionSurface? surface = null)
        : base(compositor)
    {
        _textureChangedHandler = OnTextureChanged;
        _dispatchTextureChangedHandler = DispatchTextureChanged;
        SetSurface(surface, notify: false);
    }

    public CompositionBitmapInterpolationMode BitmapInterpolationMode
    {
        get => _bitmapInterpolationMode;
        set
        {
            ThrowIfDisposed();
            if (_bitmapInterpolationMode == value)
                return;
            _bitmapInterpolationMode = value;
            NotifyOwnersChanged();
        }
    }

    public CompositionStretch Stretch
    {
        get => _stretch;
        set
        {
            ThrowIfDisposed();
            if (_stretch == value)
                return;
            _stretch = value;
            NotifyOwnersChanged();
        }
    }

    public ICompositionSurface? Surface
    {
        get => _surface;
        set => SetSurface(value, notify: true);
    }

    public bool SnapToPixels
    {
        get => _snapToPixels;
        set
        {
            ThrowIfDisposed();
            if (_snapToPixels == value)
                return;
            _snapToPixels = value;
            NotifyOwnersChanged();
        }
    }

    public Matrix3x2 TransformMatrix
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
            NotifyOwnersChanged();
        }
    }

    public Vector2 AnchorPoint
    {
        get => _anchorPoint;
        set => SetVector(ref _anchorPoint, value);
    }

    public Vector2 CenterPoint
    {
        get => _centerPoint;
        set => SetVector(ref _centerPoint, value);
    }

    public Vector2 Offset
    {
        get => _offset;
        set => SetVector(ref _offset, value);
    }

    public Vector2 Scale
    {
        get => _scale;
        set => SetVector(ref _scale, value);
    }

    public float HorizontalAlignmentRatio
    {
        get => _horizontalAlignmentRatio;
        set => SetRatio(ref _horizontalAlignmentRatio, value);
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
            NotifyOwnersChanged();
        }
    }

    public float RotationAngleInDegrees
    {
        get => _rotationAngle * (180f / MathF.PI);
        set => RotationAngle = value * (MathF.PI / 180f);
    }

    public float VerticalAlignmentRatio
    {
        get => _verticalAlignmentRatio;
        set => SetRatio(ref _verticalAlignmentRatio, value);
    }

    internal override CompositionBrushInputKind InputKinds =>
        CompositionBrushInputKind.MaskSource |
        CompositionBrushInputKind.OpacityMask;

    internal override bool RequiresSceneBrushScope => true;

    internal override int SceneCommandOverhead => 2;

    internal override void PrepareSceneBrush(
        DrawingContext context,
        in Rect bounds,
        ref Brush? sceneBrush)
    {
        sceneBrush = null;
        if (_surface is not IProGpuTextureLeaseSource textureSource ||
            !context.TryRetainTexture(textureSource, out GpuTexture texture))
        {
            _sceneBrush.Texture = null;
            return;
        }

        UpdateSceneBrush(texture, bounds);
        sceneBrush = _sceneBrush;
    }

    internal override void OnDisposed()
    {
        Unsubscribe(_surface);
        _surface = null;
        _sceneBrush.Texture = null;
        base.OnDisposed();
    }

    private void UpdateSceneBrush(GpuTexture texture, in Rect bounds)
    {
        var sourceSize = new Vector2(texture.Width, texture.Height);
        var destinationSize = bounds.Size;
        Vector2 stretchScale = _stretch switch
        {
            CompositionStretch.None => Vector2.One,
            CompositionStretch.Fill => Divide(destinationSize, sourceSize),
            CompositionStretch.UniformToFill => UniformScale(
                destinationSize,
                sourceSize,
                useMaximum: true),
            _ => UniformScale(
                destinationSize,
                sourceSize,
                useMaximum: false)
        };
        Vector2 paintedSize = sourceSize * stretchScale;
        Vector2 alignment = (destinationSize - paintedSize) * new Vector2(
            _horizontalAlignmentRatio,
            _verticalAlignmentRatio);

        Matrix3x2 customTransform =
            Matrix3x2.CreateTranslation(-_anchorPoint * destinationSize) *
            Matrix3x2.CreateTranslation(-_centerPoint) *
            Matrix3x2.CreateScale(_scale) *
            Matrix3x2.CreateRotation(_rotationAngle) *
            Matrix3x2.CreateTranslation(_centerPoint) *
            _transformMatrix *
            Matrix3x2.CreateTranslation(_offset);

        _sceneBrush.Texture = texture;
        _sceneBrush.SourceRect = new Rect(
            0f,
            0f,
            texture.Width,
            texture.Height);
        _sceneBrush.DestinationRect = new Rect(
            bounds.Position + alignment,
            paintedSize);
        _sceneBrush.Transform = ToMatrix4x4(customTransform);
        _sceneBrush.SamplingMode = ToSamplingMode(
            _bitmapInterpolationMode);
        _sceneBrush.SnapToPixels = _snapToPixels;
    }

    private void SetSurface(ICompositionSurface? value, bool notify)
    {
        ThrowIfDisposed();
        if (ReferenceEquals(_surface, value))
            return;
        if (value is CompositionObject compositionObject)
            EnsureSameCompositor(compositionObject);
        Unsubscribe(_surface);
        _surface = value;
        Subscribe(_surface);
        if (notify)
            NotifyOwnersChanged();
    }

    private void Subscribe(ICompositionSurface? surface)
    {
        if (surface is IProGpuInvalidatingTextureSource invalidating)
            invalidating.TextureChanged += _textureChangedHandler;
    }

    private void Unsubscribe(ICompositionSurface? surface)
    {
        if (surface is IProGpuInvalidatingTextureSource invalidating)
            invalidating.TextureChanged -= _textureChangedHandler;
    }

    private void OnTextureChanged(object? sender, EventArgs args)
    {
        DispatcherQueue? dispatcher = DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            if (Interlocked.Exchange(ref _textureInvalidationPending, 1) == 0 &&
                !dispatcher.TryEnqueue(_dispatchTextureChangedHandler))
            {
                Volatile.Write(ref _textureInvalidationPending, 0);
            }
            return;
        }

        NotifyOwnersChanged();
    }

    private void DispatchTextureChanged()
    {
        Volatile.Write(ref _textureInvalidationPending, 0);
        if (!IsDisposed)
            NotifyOwnersChanged();
    }

    private void SetVector(ref Vector2 field, Vector2 value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        NotifyOwnersChanged();
    }

    private void SetRatio(ref float field, float value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        value = Math.Clamp(value, 0f, 1f);
        if (field == value)
            return;
        field = value;
        NotifyOwnersChanged();
    }

    private static Vector2 Divide(Vector2 numerator, Vector2 denominator) =>
        new(
            denominator.X > 0f ? numerator.X / denominator.X : 0f,
            denominator.Y > 0f ? numerator.Y / denominator.Y : 0f);

    private static Vector2 UniformScale(
        Vector2 destination,
        Vector2 source,
        bool useMaximum)
    {
        Vector2 scale = Divide(destination, source);
        float uniform = useMaximum
            ? MathF.Max(scale.X, scale.Y)
            : MathF.Min(scale.X, scale.Y);
        return new Vector2(uniform);
    }

    private static TextureSamplingMode ToSamplingMode(
        CompositionBitmapInterpolationMode mode) =>
        mode switch
        {
            CompositionBitmapInterpolationMode.NearestNeighbor or
            CompositionBitmapInterpolationMode.MagNearestMinNearestMipNearest =>
                TextureSamplingMode.Nearest,
            CompositionBitmapInterpolationMode.MagLinearMinLinearMipLinear =>
                TextureSamplingMode.LinearMipmap,
            CompositionBitmapInterpolationMode.MagLinearMinLinearMipNearest =>
                TextureSamplingMode.MagLinearMinLinearMipNearest,
            CompositionBitmapInterpolationMode.MagLinearMinNearestMipLinear =>
                TextureSamplingMode.MagLinearMinNearestMipLinear,
            CompositionBitmapInterpolationMode.MagLinearMinNearestMipNearest =>
                TextureSamplingMode.MagLinearMinNearestMipNearest,
            CompositionBitmapInterpolationMode.MagNearestMinLinearMipLinear =>
                TextureSamplingMode.MagNearestMinLinearMipLinear,
            CompositionBitmapInterpolationMode.MagNearestMinLinearMipNearest =>
                TextureSamplingMode.MagNearestMinLinearMipNearest,
            CompositionBitmapInterpolationMode.MagNearestMinNearestMipLinear =>
                TextureSamplingMode.MagNearestMinNearestMipLinear,
            _ => TextureSamplingMode.Linear
        };

    private static Matrix4x4 ToMatrix4x4(Matrix3x2 value) =>
        new(
            value.M11, value.M12, 0f, 0f,
            value.M21, value.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            value.M31, value.M32, 0f, 1f);

    private static bool IsFinite(in Matrix3x2 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32);
}

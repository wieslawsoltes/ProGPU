using System.Numerics;
using ProGPU.Scene;
using NativeBrush = ProGPU.Vector.Brush;
using NativeSolidColorBrush = ProGPU.Vector.SolidColorBrush;

namespace Microsoft.Graphics.Canvas.Brushes;

/// <summary>
/// GPU-resident Win2D-shaped image brush. The qualified portable lane retains
/// same-device <see cref="CanvasBitmap"/> textures and lowers rectangle fills
/// to one addressed native image draw without readback or CPU tiling.
/// </summary>
public sealed class CanvasImageBrush : ICanvasBrush, ICanvasBrushInternal
{
    private readonly CanvasBrushState _state;
    private ICanvasImage? _image;
    private CanvasEdgeBehavior _extendX;
    private CanvasEdgeBehavior _extendY;
    private Windows.Foundation.Rect? _sourceRectangle;
    private CanvasImageInterpolation _interpolation =
        CanvasImageInterpolation.Linear;
    private NativeBrush? _cachedBrush;
    private ProGPU.Backend.GpuTexture? _cachedTexture;
    private int _cachedVersion = -1;

    public CanvasImageBrush(ICanvasResourceCreator resourceCreator)
        : this(resourceCreator, null)
    {
    }

    public CanvasImageBrush(
        ICanvasResourceCreator resourceCreator,
        ICanvasImage? image)
    {
        _state = new CanvasBrushState(resourceCreator);
        _image = image;
    }

    public CanvasDevice Device => _state.Device;

    public float Opacity
    {
        get => _state.Opacity;
        set => _state.Opacity = value;
    }

    public Matrix3x2 Transform
    {
        get => _state.Transform;
        set => _state.Transform = value;
    }

    public ICanvasImage? Image
    {
        get
        {
            _state.ThrowIfDisposed();
            return _image;
        }
        set
        {
            _state.ThrowIfDisposed();
            if (!ReferenceEquals(_image, value))
            {
                _image = value;
                _state.Changed();
            }
        }
    }

    public CanvasEdgeBehavior ExtendX
    {
        get => Get(_extendX);
        set => Set(ref _extendX, value);
    }

    public CanvasEdgeBehavior ExtendY
    {
        get => Get(_extendY);
        set => Set(ref _extendY, value);
    }

    public Windows.Foundation.Rect? SourceRectangle
    {
        get
        {
            _state.ThrowIfDisposed();
            return _sourceRectangle;
        }
        set
        {
            _state.ThrowIfDisposed();
            if (value is { } rectangle)
            {
                ValidateRectangle(rectangle);
            }
            if (_sourceRectangle != value)
            {
                _sourceRectangle = value;
                _state.Changed();
            }
        }
    }

    public CanvasImageInterpolation Interpolation
    {
        get => Get(_interpolation);
        set
        {
            ValidateInterpolation(value);
            Set(ref _interpolation, value);
        }
    }

    public void Dispose()
    {
        _state.Dispose();
        _image = null;
        _cachedBrush = null;
        _cachedTexture = null;
        GC.SuppressFinalize(this);
    }

    NativeBrush ICanvasBrushInternal.GetNativeBrush(
        CanvasDevice requiredDevice,
        DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        _state.ValidateDevice(requiredDevice);
        if (_image is null)
        {
            if (_cachedBrush is NativeSolidColorBrush transparent &&
                _cachedVersion == _state.Version)
            {
                return transparent;
            }
            _cachedTexture = null;
            _cachedBrush = new NativeSolidColorBrush(Vector4.Zero);
            _cachedVersion = _state.Version;
            return _cachedBrush;
        }
        if (_image is not CanvasBitmap bitmap)
        {
            throw new NotSupportedException(
                "The portable CanvasImageBrush lane currently accepts CanvasBitmap and CanvasRenderTarget images. Command lists and effect graphs require the retained render-to-texture lane.");
        }
        if (!ReferenceEquals(bitmap.Device, requiredDevice))
        {
            throw new ArgumentException(
                "Canvas image-brush resources must belong to the drawing-session device.");
        }
        if (!drawingContext.TryRetainTexture(
                bitmap,
                requiredDevice.Context,
                out ProGPU.Backend.GpuTexture texture))
        {
            throw new ObjectDisposedException(nameof(Image));
        }
        if (_cachedBrush is GpuTextureBrush cached &&
            _cachedVersion == _state.Version &&
            ReferenceEquals(_cachedTexture, texture))
        {
            return cached;
        }

        Windows.Foundation.Rect sourceDips = _sourceRectangle ?? bitmap.Bounds;
        ValidateRectangle(sourceDips);
        float pixelScale = bitmap.Dpi / CanvasContract.DefaultDpi;
        var sourcePixels = new Rect(
            (float)sourceDips.X * pixelScale,
            (float)sourceDips.Y * pixelScale,
            (float)sourceDips.Width * pixelScale,
            (float)sourceDips.Height * pixelScale);
        _cachedTexture = texture;
        _cachedBrush = new GpuTextureBrush
        {
            Texture = texture,
            SourceRect = sourcePixels,
            DestinationRect = new Rect(
                (float)sourceDips.X,
                (float)sourceDips.Y,
                (float)sourceDips.Width,
                (float)sourceDips.Height),
            Transform = ToMatrix4x4(_state.Transform),
            SamplingMode = MapInterpolation(_interpolation),
            AddressModeU = MapEdgeBehavior(_extendX),
            AddressModeV = MapEdgeBehavior(_extendY),
            Opacity = _state.Opacity
        };
        _cachedVersion = _state.Version;
        return _cachedBrush;
    }

    private CanvasEdgeBehavior Get(CanvasEdgeBehavior value)
    {
        _state.ThrowIfDisposed();
        return value;
    }

    private CanvasImageInterpolation Get(CanvasImageInterpolation value)
    {
        _state.ThrowIfDisposed();
        return value;
    }

    private void Set(ref CanvasEdgeBehavior target, CanvasEdgeBehavior value)
    {
        _state.ThrowIfDisposed();
        if ((uint)value > (uint)CanvasEdgeBehavior.Mirror)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (target != value)
        {
            target = value;
            _state.Changed();
        }
    }

    private void Set(
        ref CanvasImageInterpolation target,
        CanvasImageInterpolation value)
    {
        _state.ThrowIfDisposed();
        if (target != value)
        {
            target = value;
            _state.Changed();
        }
    }

    private static void ValidateInterpolation(CanvasImageInterpolation value)
    {
        if (value is CanvasImageInterpolation.Anisotropic or
            CanvasImageInterpolation.HighQualityCubic)
        {
            throw new NotSupportedException(
                $"Canvas image-brush interpolation {value} is not qualified by the portable texture lane.");
        }
        if ((uint)value > (uint)CanvasImageInterpolation.HighQualityCubic)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static TextureSamplingMode MapInterpolation(
        CanvasImageInterpolation value) => value switch
        {
            CanvasImageInterpolation.NearestNeighbor =>
                TextureSamplingMode.Nearest,
            CanvasImageInterpolation.Linear or
            CanvasImageInterpolation.MultiSampleLinear =>
                TextureSamplingMode.Linear,
            CanvasImageInterpolation.Cubic => TextureSamplingMode.Cubic,
            _ => throw new NotSupportedException(
                $"Canvas image-brush interpolation {value} is not qualified by the portable texture lane.")
        };

    private static TextureAddressMode MapEdgeBehavior(
        CanvasEdgeBehavior value) => value switch
        {
            CanvasEdgeBehavior.Clamp => TextureAddressMode.Clamp,
            CanvasEdgeBehavior.Wrap => TextureAddressMode.Repeat,
            CanvasEdgeBehavior.Mirror => TextureAddressMode.MirrorRepeat,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static void ValidateRectangle(Windows.Foundation.Rect value)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) ||
            !double.IsFinite(value.Width) || !double.IsFinite(value.Height) ||
            value.Width <= 0d || value.Height <= 0d ||
            value.X < float.MinValue || value.X > float.MaxValue ||
            value.Y < float.MinValue || value.Y > float.MaxValue ||
            value.Width > float.MaxValue || value.Height > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static Matrix4x4 ToMatrix4x4(in Matrix3x2 value) => new(
        value.M11, value.M12, 0f, 0f,
        value.M21, value.M22, 0f, 0f,
        0f, 0f, 1f, 0f,
        value.M31, value.M32, 0f, 1f);
}

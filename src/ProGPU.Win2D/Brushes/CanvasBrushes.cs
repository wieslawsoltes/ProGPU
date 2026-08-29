using System.Numerics;
using ProGPU.Vector;
using Color = Windows.UI.Color;
using NativeBrush = ProGPU.Vector.Brush;
using NativeGradientStop = ProGPU.Vector.GradientStop;
using NativeLinearGradientBrush = ProGPU.Vector.LinearGradientBrush;
using NativeRadialGradientBrush = ProGPU.Vector.RadialGradientBrush;
using NativeSolidColorBrush = ProGPU.Vector.SolidColorBrush;

namespace Microsoft.Graphics.Canvas.Brushes;

public interface ICanvasBrush : IDisposable
{
    float Opacity { get; set; }

    Matrix3x2 Transform { get; set; }

    CanvasDevice Device { get; }
}

public struct CanvasGradientStop
{
    public float Position;
    public Color Color;
}

public struct CanvasGradientStopHdr
{
    public float Position;
    public Vector4 Color;
}

internal interface ICanvasBrushInternal
{
    NativeBrush GetNativeBrush(CanvasDevice requiredDevice);
}

internal sealed class CanvasBrushState
{
    private float _opacity = 1f;
    private Matrix3x2 _transform = Matrix3x2.Identity;
    private bool _isDisposed;

    public CanvasBrushState(ICanvasResourceCreator resourceCreator)
    {
        ArgumentNullException.ThrowIfNull(resourceCreator);
        Device = resourceCreator.Device;
    }

    public CanvasDevice Device { get; }

    public int Version { get; private set; }

    public float Opacity
    {
        get
        {
            ThrowIfDisposed();
            return _opacity;
        }
        set
        {
            ThrowIfDisposed();
            if (!float.IsFinite(value) || value is < 0f or > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (_opacity != value)
            {
                _opacity = value;
                Version++;
            }
        }
    }

    public Matrix3x2 Transform
    {
        get
        {
            ThrowIfDisposed();
            return _transform;
        }
        set
        {
            ThrowIfDisposed();
            if (!IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (_transform != value)
            {
                _transform = value;
                Version++;
            }
        }
    }

    public void Changed()
    {
        ThrowIfDisposed();
        Version++;
    }

    public void ValidateDevice(CanvasDevice requiredDevice)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(Device, requiredDevice))
        {
            throw new ArgumentException(
                "Canvas brush resources must belong to the drawing-session device.");
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
    }

    public void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException("CanvasBrush");
        }
    }

    private static bool IsFinite(in Matrix3x2 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32);
}

public sealed class CanvasSolidColorBrush : ICanvasBrush, ICanvasBrushInternal
{
    private readonly CanvasBrushState _state;
    private Vector4 _color;
    private NativeSolidColorBrush? _cachedBrush;
    private int _cachedVersion = -1;

    public CanvasSolidColorBrush(
        ICanvasResourceCreator resourceCreator,
        Color color)
    {
        _state = new CanvasBrushState(resourceCreator);
        _color = CanvasBrushUtilities.ToStraightVector(color);
    }

    private CanvasSolidColorBrush(
        ICanvasResourceCreator resourceCreator,
        Vector4 color)
    {
        CanvasBrushUtilities.ValidateFiniteColor(color);
        _state = new CanvasBrushState(resourceCreator);
        _color = color;
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

    public Color Color
    {
        get => CanvasBrushUtilities.ToColor(ColorHdr);
        set => ColorHdr = CanvasBrushUtilities.ToStraightVector(value);
    }

    public Vector4 ColorHdr
    {
        get
        {
            _state.ThrowIfDisposed();
            return _color;
        }
        set
        {
            CanvasBrushUtilities.ValidateFiniteColor(value);
            _state.ThrowIfDisposed();
            if (_color != value)
            {
                _color = value;
                _state.Changed();
            }
        }
    }

    public static CanvasSolidColorBrush CreateHdr(
        ICanvasResourceCreator resourceCreator,
        Vector4 colorHdr) =>
        new(resourceCreator, colorHdr);

    public void Dispose()
    {
        _state.Dispose();
        _cachedBrush = null;
        GC.SuppressFinalize(this);
    }

    NativeBrush ICanvasBrushInternal.GetNativeBrush(
        CanvasDevice requiredDevice)
    {
        _state.ValidateDevice(requiredDevice);
        if (_cachedBrush is not null && _cachedVersion == _state.Version)
        {
            return _cachedBrush;
        }

        _cachedBrush = new NativeSolidColorBrush(_color)
        {
            Opacity = _state.Opacity
        };
        _cachedVersion = _state.Version;
        return _cachedBrush;
    }
}

public sealed class CanvasLinearGradientBrush :
    ICanvasBrush,
    ICanvasBrushInternal
{
    private readonly CanvasBrushState _state;
    private readonly CanvasGradientStopHdr[] _stops;
    private readonly CanvasEdgeBehavior _edgeBehavior;
    private readonly CanvasAlphaMode _alphaMode;
    private readonly CanvasColorSpace _preInterpolationSpace;
    private readonly CanvasColorSpace _postInterpolationSpace;
    private readonly CanvasBufferPrecision _bufferPrecision;
    private Vector2 _startPoint;
    private Vector2 _endPoint;
    private NativeLinearGradientBrush? _cachedBrush;
    private int _cachedVersion = -1;

    public CanvasLinearGradientBrush(
        ICanvasResourceCreator resourceCreator,
        Color startColor,
        Color endColor)
        : this(
            resourceCreator,
            [
                new CanvasGradientStop { Position = 0f, Color = startColor },
                new CanvasGradientStop { Position = 1f, Color = endColor }
            ])
    {
    }

    public CanvasLinearGradientBrush(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStop[] gradientStops)
        : this(
            resourceCreator,
            gradientStops,
            CanvasEdgeBehavior.Clamp,
            CanvasAlphaMode.Premultiplied)
    {
    }

    public CanvasLinearGradientBrush(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStop[] gradientStops,
        CanvasEdgeBehavior edgeBehavior,
        CanvasAlphaMode alphaMode)
        : this(
            resourceCreator,
            CanvasBrushUtilities.ToHdrStops(gradientStops),
            edgeBehavior,
            alphaMode,
            CanvasColorSpace.Srgb,
            CanvasColorSpace.Srgb,
            CanvasBufferPrecision.Precision8UIntNormalized)
    {
    }

    public CanvasLinearGradientBrush(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStop[] gradientStops,
        CanvasEdgeBehavior edgeBehavior,
        CanvasAlphaMode alphaMode,
        CanvasColorSpace preInterpolationSpace,
        CanvasColorSpace postInterpolationSpace,
        CanvasBufferPrecision bufferPrecision)
        : this(
            resourceCreator,
            CanvasBrushUtilities.ToHdrStops(gradientStops),
            edgeBehavior,
            alphaMode,
            preInterpolationSpace,
            postInterpolationSpace,
            bufferPrecision)
    {
    }

    private CanvasLinearGradientBrush(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStopHdr[] gradientStops,
        CanvasEdgeBehavior edgeBehavior,
        CanvasAlphaMode alphaMode,
        CanvasColorSpace preInterpolationSpace,
        CanvasColorSpace postInterpolationSpace,
        CanvasBufferPrecision bufferPrecision)
    {
        _state = new CanvasBrushState(resourceCreator);
        _stops = CanvasBrushUtilities.ValidateAndCopyStops(gradientStops);
        CanvasBrushUtilities.ValidateGradientOptions(
            edgeBehavior,
            alphaMode,
            preInterpolationSpace,
            postInterpolationSpace,
            bufferPrecision);
        _edgeBehavior = edgeBehavior;
        _alphaMode = alphaMode;
        _preInterpolationSpace = preInterpolationSpace;
        _postInterpolationSpace = postInterpolationSpace;
        _bufferPrecision = bufferPrecision;
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

    public Vector2 StartPoint
    {
        get => Get(_startPoint);
        set => Set(ref _startPoint, value);
    }

    public Vector2 EndPoint
    {
        get => Get(_endPoint);
        set => Set(ref _endPoint, value);
    }

    public CanvasGradientStop[] Stops =>
        CanvasBrushUtilities.ToColorStops(StopsHdr);

    public CanvasGradientStopHdr[] StopsHdr
    {
        get
        {
            _state.ThrowIfDisposed();
            return (CanvasGradientStopHdr[])_stops.Clone();
        }
    }

    public CanvasEdgeBehavior EdgeBehavior => Get(_edgeBehavior);
    public CanvasAlphaMode AlphaMode => Get(_alphaMode);
    public CanvasColorSpace PreInterpolationSpace =>
        Get(_preInterpolationSpace);
    public CanvasColorSpace PostInterpolationSpace =>
        Get(_postInterpolationSpace);
    public CanvasBufferPrecision BufferPrecision => Get(_bufferPrecision);

    public static CanvasLinearGradientBrush CreateHdr(
        ICanvasResourceCreator resourceCreator,
        Vector4 startColorHdr,
        Vector4 endColorHdr) =>
        CreateHdr(
            resourceCreator,
            [
                new CanvasGradientStopHdr
                {
                    Position = 0f,
                    Color = startColorHdr
                },
                new CanvasGradientStopHdr
                {
                    Position = 1f,
                    Color = endColorHdr
                }
            ]);

    public static CanvasLinearGradientBrush CreateHdr(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStopHdr[] gradientStopsHdr) =>
        new(
            resourceCreator,
            gradientStopsHdr,
            CanvasEdgeBehavior.Clamp,
            CanvasAlphaMode.Premultiplied,
            CanvasColorSpace.Srgb,
            CanvasColorSpace.Srgb,
            CanvasBufferPrecision.Precision8UIntNormalized);

    public static CanvasLinearGradientBrush CreateHdr(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStopHdr[] gradientStopsHdr,
        CanvasEdgeBehavior edgeBehavior,
        CanvasAlphaMode alphaMode) =>
        new(
            resourceCreator,
            gradientStopsHdr,
            edgeBehavior,
            alphaMode,
            CanvasColorSpace.Srgb,
            CanvasColorSpace.Srgb,
            CanvasBufferPrecision.Precision8UIntNormalized);

    public static CanvasLinearGradientBrush CreateHdr(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStopHdr[] gradientStopsHdr,
        CanvasEdgeBehavior edgeBehavior,
        CanvasAlphaMode alphaMode,
        CanvasColorSpace preInterpolationSpace,
        CanvasColorSpace postInterpolationSpace,
        CanvasBufferPrecision bufferPrecision) =>
        new(
            resourceCreator,
            gradientStopsHdr,
            edgeBehavior,
            alphaMode,
            preInterpolationSpace,
            postInterpolationSpace,
            bufferPrecision);

    public void Dispose()
    {
        _state.Dispose();
        _cachedBrush = null;
        GC.SuppressFinalize(this);
    }

    NativeBrush ICanvasBrushInternal.GetNativeBrush(
        CanvasDevice requiredDevice)
    {
        _state.ValidateDevice(requiredDevice);
        if (_cachedBrush is not null && _cachedVersion == _state.Version)
        {
            return _cachedBrush;
        }

        _cachedBrush = new NativeLinearGradientBrush(
            _startPoint,
            _endPoint,
            CanvasBrushUtilities.ToNativeStops(_stops))
        {
            Opacity = _state.Opacity,
            CoordinateTransform = CanvasBrushUtilities.ToMatrix4x4(
                _state.Transform),
            SpreadMethod = CanvasBrushUtilities.MapEdgeBehavior(_edgeBehavior),
            ColorInterpolationMode =
                GradientColorInterpolationMode.SRgbLinearInterpolation
        };
        _cachedVersion = _state.Version;
        return _cachedBrush;
    }

    private T Get<T>(T value)
    {
        _state.ThrowIfDisposed();
        return value;
    }

    private void Set(ref Vector2 field, Vector2 value)
    {
        CanvasBrushUtilities.ValidateFinite(value);
        _state.ThrowIfDisposed();
        if (field != value)
        {
            field = value;
            _state.Changed();
        }
    }
}

public sealed class CanvasRadialGradientBrush :
    ICanvasBrush,
    ICanvasBrushInternal
{
    private readonly CanvasBrushState _state;
    private readonly CanvasGradientStopHdr[] _stops;
    private readonly CanvasEdgeBehavior _edgeBehavior;
    private readonly CanvasAlphaMode _alphaMode;
    private readonly CanvasColorSpace _preInterpolationSpace;
    private readonly CanvasColorSpace _postInterpolationSpace;
    private readonly CanvasBufferPrecision _bufferPrecision;
    private Vector2 _center;
    private Vector2 _originOffset;
    private float _radiusX;
    private float _radiusY;
    private NativeRadialGradientBrush? _cachedBrush;
    private int _cachedVersion = -1;

    public CanvasRadialGradientBrush(
        ICanvasResourceCreator resourceCreator,
        Color startColor,
        Color endColor)
        : this(
            resourceCreator,
            [
                new CanvasGradientStop { Position = 0f, Color = startColor },
                new CanvasGradientStop { Position = 1f, Color = endColor }
            ])
    {
    }

    public CanvasRadialGradientBrush(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStop[] gradientStops)
        : this(
            resourceCreator,
            gradientStops,
            CanvasEdgeBehavior.Clamp,
            CanvasAlphaMode.Premultiplied)
    {
    }

    public CanvasRadialGradientBrush(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStop[] gradientStops,
        CanvasEdgeBehavior edgeBehavior,
        CanvasAlphaMode alphaMode)
        : this(
            resourceCreator,
            CanvasBrushUtilities.ToHdrStops(gradientStops),
            edgeBehavior,
            alphaMode,
            CanvasColorSpace.Srgb,
            CanvasColorSpace.Srgb,
            CanvasBufferPrecision.Precision8UIntNormalized)
    {
    }

    public CanvasRadialGradientBrush(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStop[] gradientStops,
        CanvasEdgeBehavior edgeBehavior,
        CanvasAlphaMode alphaMode,
        CanvasColorSpace preInterpolationSpace,
        CanvasColorSpace postInterpolationSpace,
        CanvasBufferPrecision bufferPrecision)
        : this(
            resourceCreator,
            CanvasBrushUtilities.ToHdrStops(gradientStops),
            edgeBehavior,
            alphaMode,
            preInterpolationSpace,
            postInterpolationSpace,
            bufferPrecision)
    {
    }

    private CanvasRadialGradientBrush(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStopHdr[] gradientStops,
        CanvasEdgeBehavior edgeBehavior,
        CanvasAlphaMode alphaMode,
        CanvasColorSpace preInterpolationSpace,
        CanvasColorSpace postInterpolationSpace,
        CanvasBufferPrecision bufferPrecision)
    {
        _state = new CanvasBrushState(resourceCreator);
        _stops = CanvasBrushUtilities.ValidateAndCopyStops(gradientStops);
        CanvasBrushUtilities.ValidateGradientOptions(
            edgeBehavior,
            alphaMode,
            preInterpolationSpace,
            postInterpolationSpace,
            bufferPrecision);
        _edgeBehavior = edgeBehavior;
        _alphaMode = alphaMode;
        _preInterpolationSpace = preInterpolationSpace;
        _postInterpolationSpace = postInterpolationSpace;
        _bufferPrecision = bufferPrecision;
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

    public Vector2 Center
    {
        get => Get(_center);
        set => Set(ref _center, value);
    }

    public Vector2 OriginOffset
    {
        get => Get(_originOffset);
        set => Set(ref _originOffset, value);
    }

    public float RadiusX
    {
        get => Get(_radiusX);
        set => SetRadius(ref _radiusX, value);
    }

    public float RadiusY
    {
        get => Get(_radiusY);
        set => SetRadius(ref _radiusY, value);
    }

    public CanvasGradientStop[] Stops =>
        CanvasBrushUtilities.ToColorStops(StopsHdr);

    public CanvasGradientStopHdr[] StopsHdr
    {
        get
        {
            _state.ThrowIfDisposed();
            return (CanvasGradientStopHdr[])_stops.Clone();
        }
    }

    public CanvasEdgeBehavior EdgeBehavior => Get(_edgeBehavior);
    public CanvasAlphaMode AlphaMode => Get(_alphaMode);
    public CanvasColorSpace PreInterpolationSpace =>
        Get(_preInterpolationSpace);
    public CanvasColorSpace PostInterpolationSpace =>
        Get(_postInterpolationSpace);
    public CanvasBufferPrecision BufferPrecision => Get(_bufferPrecision);

    public static CanvasRadialGradientBrush CreateHdr(
        ICanvasResourceCreator resourceCreator,
        Vector4 startColorHdr,
        Vector4 endColorHdr) =>
        CreateHdr(
            resourceCreator,
            [
                new CanvasGradientStopHdr
                {
                    Position = 0f,
                    Color = startColorHdr
                },
                new CanvasGradientStopHdr
                {
                    Position = 1f,
                    Color = endColorHdr
                }
            ]);

    public static CanvasRadialGradientBrush CreateHdr(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStopHdr[] gradientStopsHdr) =>
        new(
            resourceCreator,
            gradientStopsHdr,
            CanvasEdgeBehavior.Clamp,
            CanvasAlphaMode.Premultiplied,
            CanvasColorSpace.Srgb,
            CanvasColorSpace.Srgb,
            CanvasBufferPrecision.Precision8UIntNormalized);

    public static CanvasRadialGradientBrush CreateHdr(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStopHdr[] gradientStopsHdr,
        CanvasEdgeBehavior edgeBehavior,
        CanvasAlphaMode alphaMode) =>
        new(
            resourceCreator,
            gradientStopsHdr,
            edgeBehavior,
            alphaMode,
            CanvasColorSpace.Srgb,
            CanvasColorSpace.Srgb,
            CanvasBufferPrecision.Precision8UIntNormalized);

    public static CanvasRadialGradientBrush CreateHdr(
        ICanvasResourceCreator resourceCreator,
        CanvasGradientStopHdr[] gradientStopsHdr,
        CanvasEdgeBehavior edgeBehavior,
        CanvasAlphaMode alphaMode,
        CanvasColorSpace preInterpolationSpace,
        CanvasColorSpace postInterpolationSpace,
        CanvasBufferPrecision bufferPrecision) =>
        new(
            resourceCreator,
            gradientStopsHdr,
            edgeBehavior,
            alphaMode,
            preInterpolationSpace,
            postInterpolationSpace,
            bufferPrecision);

    public void Dispose()
    {
        _state.Dispose();
        _cachedBrush = null;
        GC.SuppressFinalize(this);
    }

    NativeBrush ICanvasBrushInternal.GetNativeBrush(
        CanvasDevice requiredDevice)
    {
        _state.ValidateDevice(requiredDevice);
        if (_radiusX <= 0f || _radiusY <= 0f)
        {
            throw new InvalidOperationException(
                "A radial Canvas brush requires positive RadiusX and RadiusY before drawing.");
        }
        if (_cachedBrush is not null && _cachedVersion == _state.Version)
        {
            return _cachedBrush;
        }

        _cachedBrush = new NativeRadialGradientBrush(
            _center,
            _center + _originOffset,
            _radiusX,
            _radiusY,
            CanvasBrushUtilities.ToNativeStops(_stops))
        {
            Opacity = _state.Opacity,
            CoordinateTransform = CanvasBrushUtilities.ToMatrix4x4(
                _state.Transform),
            SpreadMethod = CanvasBrushUtilities.MapEdgeBehavior(_edgeBehavior),
            ColorInterpolationMode =
                GradientColorInterpolationMode.SRgbLinearInterpolation
        };
        _cachedVersion = _state.Version;
        return _cachedBrush;
    }

    private T Get<T>(T value)
    {
        _state.ThrowIfDisposed();
        return value;
    }

    private void Set(ref Vector2 field, Vector2 value)
    {
        CanvasBrushUtilities.ValidateFinite(value);
        _state.ThrowIfDisposed();
        if (field != value)
        {
            field = value;
            _state.Changed();
        }
    }

    private void SetRadius(ref float field, float value)
    {
        _state.ThrowIfDisposed();
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (field != value)
        {
            field = value;
            _state.Changed();
        }
    }
}

internal static class CanvasBrushUtilities
{
    public static Vector4 ToStraightVector(Color color) => new(
        color.R / 255f,
        color.G / 255f,
        color.B / 255f,
        color.A / 255f);

    public static Color ToColor(Vector4 value)
    {
        ValidateFiniteColor(value);
        return Color.FromArgb(
            ToByte(value.W),
            ToByte(value.X),
            ToByte(value.Y),
            ToByte(value.Z));
    }

    public static void ValidateFinite(Vector2 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    public static void ValidateFiniteColor(Vector4 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) || !float.IsFinite(value.W))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    public static CanvasGradientStopHdr[] ToHdrStops(
        CanvasGradientStop[] gradientStops)
    {
        ArgumentNullException.ThrowIfNull(gradientStops);
        var result = new CanvasGradientStopHdr[gradientStops.Length];
        for (int index = 0; index < gradientStops.Length; index++)
        {
            result[index] = new CanvasGradientStopHdr
            {
                Position = gradientStops[index].Position,
                Color = ToStraightVector(gradientStops[index].Color)
            };
        }
        return result;
    }

    public static CanvasGradientStopHdr[] ValidateAndCopyStops(
        CanvasGradientStopHdr[] gradientStops)
    {
        ArgumentNullException.ThrowIfNull(gradientStops);
        if (gradientStops.Length == 0)
        {
            throw new ArgumentException(
                "A Canvas gradient requires at least one stop.",
                nameof(gradientStops));
        }

        var result = new CanvasGradientStopHdr[gradientStops.Length];
        float previous = float.NegativeInfinity;
        for (int index = 0; index < gradientStops.Length; index++)
        {
            CanvasGradientStopHdr stop = gradientStops[index];
            ValidateFiniteColor(stop.Color);
            if (!float.IsFinite(stop.Position) || stop.Position < previous)
            {
                throw new ArgumentException(
                    "Canvas gradient stops must have finite nondecreasing positions.",
                    nameof(gradientStops));
            }
            result[index] = stop;
            previous = stop.Position;
        }
        return result;
    }

    public static CanvasGradientStop[] ToColorStops(
        CanvasGradientStopHdr[] gradientStops)
    {
        var result = new CanvasGradientStop[gradientStops.Length];
        for (int index = 0; index < gradientStops.Length; index++)
        {
            result[index] = new CanvasGradientStop
            {
                Position = gradientStops[index].Position,
                Color = ToColor(gradientStops[index].Color)
            };
        }
        return result;
    }

    public static NativeGradientStop[] ToNativeStops(
        CanvasGradientStopHdr[] gradientStops)
    {
        var result = new NativeGradientStop[gradientStops.Length];
        for (int index = 0; index < gradientStops.Length; index++)
        {
            result[index] = new NativeGradientStop(
                gradientStops[index].Color,
                gradientStops[index].Position);
        }
        return result;
    }

    public static void ValidateGradientOptions(
        CanvasEdgeBehavior edgeBehavior,
        CanvasAlphaMode alphaMode,
        CanvasColorSpace preInterpolationSpace,
        CanvasColorSpace postInterpolationSpace,
        CanvasBufferPrecision bufferPrecision)
    {
        if (!Enum.IsDefined(edgeBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(edgeBehavior));
        }
        if (alphaMode != CanvasAlphaMode.Premultiplied ||
            preInterpolationSpace != CanvasColorSpace.Srgb ||
            postInterpolationSpace != CanvasColorSpace.Srgb ||
            bufferPrecision != CanvasBufferPrecision.Precision8UIntNormalized)
        {
            throw new NotSupportedException(
                "The portable Canvas gradient lane currently supports premultiplied sRGB interpolation with 8-bit normalized precision only.");
        }
    }

    public static GradientSpreadMethod MapEdgeBehavior(
        CanvasEdgeBehavior value) => value switch
        {
            CanvasEdgeBehavior.Clamp => GradientSpreadMethod.Pad,
            CanvasEdgeBehavior.Wrap => GradientSpreadMethod.Repeat,
            CanvasEdgeBehavior.Mirror => GradientSpreadMethod.Reflect,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static Matrix4x4 ToMatrix4x4(in Matrix3x2 value) =>
        new(
            value.M11, value.M12, 0f, 0f,
            value.M21, value.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            value.M31, value.M32, 0f, 1f);

    private static byte ToByte(float value) => checked((byte)Math.Clamp(
        (int)MathF.Round(
            Math.Clamp(value, 0f, 1f) * 255f,
            MidpointRounding.AwayFromZero),
        0,
        255));
}

using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Scene;
using ProGPU.Vector;
using WinRT;
using Windows.Foundation.Metadata;
using SceneBrush = ProGPU.Vector.Brush;
using SceneGradientStop = ProGPU.Vector.GradientStop;
using WinUiColor = Windows.UI.Color;

namespace Microsoft.UI.Composition;

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionColorSpace
{
    Auto = 0,
    Hsl = 1,
    Rgb = 2,
    HslLinear = 3,
    RgbLinear = 4
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionGradientExtendMode
{
    Clamp = 0,
    Wrap = 1,
    Mirror = 2
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionMappingMode
{
    Absolute = 0,
    Relative = 1
}

internal interface ICompositionColorGradientStopOwner
{
    void NotifyGradientStopChanged();

    void NotifyGradientStopDisposed(CompositionColorGradientStop stop);
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionColorGradientStop : CompositionObject
{
    private List<WeakReference<ICompositionColorGradientStopOwner>>? _owners;
    private WinUiColor _color;
    private float _offset;

    internal CompositionColorGradientStop(
        Compositor compositor,
        float offset,
        WinUiColor color)
        : base(compositor)
    {
        ValidateOffset(offset);
        _offset = offset;
        _color = color;
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

    public float Offset
    {
        get => _offset;
        set
        {
            ThrowIfDisposed();
            ValidateOffset(value);
            if (_offset == value)
                return;
            _offset = value;
            NotifyOwnersChanged();
        }
    }

    internal void AddOwner(ICompositionColorGradientStopOwner owner)
    {
        ThrowIfDisposed();
        List<WeakReference<ICompositionColorGradientStopOwner>> owners =
            _owners ??=
                new List<WeakReference<ICompositionColorGradientStopOwner>>();
        for (int index = owners.Count - 1; index >= 0; index--)
        {
            if (!owners[index].TryGetTarget(
                    out ICompositionColorGradientStopOwner? existing))
            {
                owners.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, owner))
                return;
        }

        owners.Add(
            new WeakReference<ICompositionColorGradientStopOwner>(owner));
    }

    internal void RemoveOwner(ICompositionColorGradientStopOwner owner)
    {
        if (_owners is null)
            return;
        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (!_owners[index].TryGetTarget(
                    out ICompositionColorGradientStopOwner? existing) ||
                ReferenceEquals(existing, owner))
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
                        out ICompositionColorGradientStopOwner? owner))
                {
                    owner.NotifyGradientStopDisposed(this);
                }
            }
            _owners.Clear();
        }
        base.OnDisposed();
    }

    private void NotifyOwnersChanged()
    {
        if (_owners is null)
            return;
        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (_owners[index].TryGetTarget(
                    out ICompositionColorGradientStopOwner? owner))
            {
                owner.NotifyGradientStopChanged();
            }
            else
            {
                _owners.RemoveAt(index);
            }
        }
    }

    private static void ValidateOffset(float value)
    {
        if (!float.IsFinite(value) || value < 0f || value > 1f)
            throw new ArgumentOutOfRangeException(nameof(value));
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionColorGradientStopCollection :
    IList<CompositionColorGradientStop>,
    ICompositionColorGradientStopOwner
{
    private readonly CompositionGradientBrush _owner;
    private readonly List<CompositionColorGradientStop> _items = new();
    private SceneGradientStop[]? _sceneStops;
    private SceneGradientStop[]? _sortScratch;
    private bool _sceneStopsDirty = true;

    internal CompositionColorGradientStopCollection(
        CompositionGradientBrush owner)
    {
        _owner = owner;
    }

    [IndexerName("ListItem")]
    public CompositionColorGradientStop this[int index]
    {
        get => _items[index];
        set
        {
            Validate(value);
            CompositionColorGradientStop previous = _items[index];
            if (ReferenceEquals(previous, value))
                return;
            _items[index] = value;
            value.AddOwner(this);
            RemoveOwnerWhenUnused(previous);
            Changed(countChanged: false);
        }
    }

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public void Add(CompositionColorGradientStop item) =>
        Insert(_items.Count, item);

    public void Clear()
    {
        _owner.ThrowIfDisposed();
        if (_items.Count == 0)
            return;
        foreach (CompositionColorGradientStop item in _items)
            item.RemoveOwner(this);
        _items.Clear();
        Changed(countChanged: true);
    }

    public bool Contains(CompositionColorGradientStop item) =>
        _items.Contains(item);

    public void CopyTo(
        CompositionColorGradientStop[] array,
        int arrayIndex) =>
        _items.CopyTo(array, arrayIndex);

    public IEnumerator<CompositionColorGradientStop> GetEnumerator() =>
        _items.GetEnumerator();

    public int IndexOf(CompositionColorGradientStop item) =>
        _items.IndexOf(item);

    public void Insert(int index, CompositionColorGradientStop item)
    {
        _owner.ThrowIfDisposed();
        Validate(item);
        _items.Insert(index, item);
        item.AddOwner(this);
        Changed(countChanged: true);
    }

    public bool Remove(CompositionColorGradientStop item)
    {
        _owner.ThrowIfDisposed();
        int index = _items.IndexOf(item);
        if (index < 0)
            return false;
        _items.RemoveAt(index);
        RemoveOwnerWhenUnused(item);
        Changed(countChanged: true);
        return true;
    }

    public void RemoveAt(int index)
    {
        _owner.ThrowIfDisposed();
        CompositionColorGradientStop item = _items[index];
        _items.RemoveAt(index);
        RemoveOwnerWhenUnused(item);
        Changed(countChanged: true);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void ICompositionColorGradientStopOwner.NotifyGradientStopChanged() =>
        Changed(countChanged: false);

    void ICompositionColorGradientStopOwner.NotifyGradientStopDisposed(
        CompositionColorGradientStop stop)
    {
        bool removed = false;
        for (int index = _items.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(_items[index], stop))
            {
                _items.RemoveAt(index);
                removed = true;
            }
        }
        if (removed)
            Changed(countChanged: true);
    }

    internal SceneGradientStop[] GetSceneStops()
    {
        _owner.ThrowIfDisposed();
        if (!_sceneStopsDirty && _sceneStops is not null)
            return _sceneStops;
        if (_items.Count == 0)
        {
            _sceneStops = Array.Empty<SceneGradientStop>();
            _sceneStopsDirty = false;
            return _sceneStops;
        }

        if (_sceneStops is null || _sceneStops.Length != _items.Count)
            _sceneStops = new SceneGradientStop[_items.Count];
        if (_sortScratch is null || _sortScratch.Length != _items.Count)
            _sortScratch = new SceneGradientStop[_items.Count];
        for (int index = 0; index < _items.Count; index++)
        {
            CompositionColorGradientStop item = _items[index];
            _sceneStops[index] = new SceneGradientStop(
                ToVector(item.Color),
                item.Offset);
        }

        StableSortByOffset(_sceneStops, _sortScratch);
        _sceneStopsDirty = false;
        return _sceneStops;
    }

    private void Changed(bool countChanged)
    {
        if (countChanged &&
            _sceneStops is not null &&
            _sceneStops.Length != _items.Count)
        {
            _sceneStops = null;
            _sortScratch = null;
        }
        _sceneStopsDirty = true;
        _owner.NotifyGradientChanged();
    }

    private void RemoveOwnerWhenUnused(CompositionColorGradientStop item)
    {
        if (!_items.Contains(item))
            item.RemoveOwner(this);
    }

    private void Validate(CompositionColorGradientStop item)
    {
        _owner.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        _owner.EnsureSameCompositor(item);
        item.ThrowIfDisposed();
    }

    private static void StableSortByOffset(
        SceneGradientStop[] stops,
        SceneGradientStop[] scratch)
    {
        int count = stops.Length;
        SceneGradientStop[] source = stops;
        SceneGradientStop[] destination = scratch;
        for (int width = 1; width < count;)
        {
            int runWidth = width > count - width
                ? count
                : width * 2;
            for (int start = 0; start < count; start += runWidth)
            {
                int middle = start + Math.Min(width, count - start);
                int end = start + Math.Min(runWidth, count - start);
                int left = start;
                int right = middle;
                for (int output = start; output < end; output++)
                {
                    if (right >= end ||
                        (left < middle &&
                         source[left].Offset <= source[right].Offset))
                    {
                        destination[output] = source[left++];
                    }
                    else
                    {
                        destination[output] = source[right++];
                    }
                }
            }
            (source, destination) = (destination, source);
            width = runWidth;
        }
        if (!ReferenceEquals(source, stops))
            source.AsSpan().CopyTo(stops);
    }

    private static Vector4 ToVector(WinUiColor color) => new(
        color.R / 255f,
        color.G / 255f,
        color.B / 255f,
        color.A / 255f);
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public class CompositionGradientBrush : CompositionBrush
{
    private Vector2 _anchorPoint;
    private Vector2 _centerPoint;
    private CompositionGradientExtendMode _extendMode;
    private CompositionColorSpace _interpolationSpace;
    private CompositionMappingMode _mappingMode = CompositionMappingMode.Relative;
    private Vector2 _offset;
    private float _rotationAngle;
    private Vector2 _scale = Vector2.One;
    private Matrix3x2 _transformMatrix = Matrix3x2.Identity;

    protected internal CompositionGradientBrush(IObjectReference objRef)
        : base(objRef)
    {
        ColorStops = new CompositionColorGradientStopCollection(this);
    }

    protected CompositionGradientBrush(DerivedComposed _)
        : base(_)
    {
        ColorStops = new CompositionColorGradientStopCollection(this);
    }

    internal CompositionGradientBrush(Compositor compositor)
        : base(compositor)
    {
        ColorStops = new CompositionColorGradientStopCollection(this);
    }

    public Vector2 AnchorPoint
    {
        get => _anchorPoint;
        set => SetFinite(ref _anchorPoint, value);
    }

    public Vector2 CenterPoint
    {
        get => _centerPoint;
        set => SetFinite(ref _centerPoint, value);
    }

    public CompositionColorGradientStopCollection ColorStops { get; }

    public CompositionGradientExtendMode ExtendMode
    {
        get => _extendMode;
        set
        {
            ValidateEnum(value);
            SetValue(ref _extendMode, value);
        }
    }

    public CompositionColorSpace InterpolationSpace
    {
        get => _interpolationSpace;
        set
        {
            ValidateEnum(value);
            if (value is CompositionColorSpace.Hsl or
                CompositionColorSpace.HslLinear)
            {
                throw new NotSupportedException(
                    "Composition gradients support RGB interpolation only.");
            }
            SetValue(ref _interpolationSpace, value);
        }
    }

    public CompositionMappingMode MappingMode
    {
        get => _mappingMode;
        set
        {
            ValidateEnum(value);
            SetValue(ref _mappingMode, value);
        }
    }

    public Vector2 Offset
    {
        get => _offset;
        set => SetFinite(ref _offset, value);
    }

    public float RotationAngle
    {
        get => _rotationAngle;
        set => SetFinite(ref _rotationAngle, value);
    }

    public float RotationAngleInDegrees
    {
        get => _rotationAngle * (180f / MathF.PI);
        set => RotationAngle = value * (MathF.PI / 180f);
    }

    public Vector2 Scale
    {
        get => _scale;
        set => SetFinite(ref _scale, value);
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
            NotifyGradientChanged();
        }
    }

    internal override void UpdateSceneBrush(
        in Rect bounds,
        ref SceneBrush? sceneBrush)
    {
        sceneBrush = null;
    }

    internal void NotifyGradientChanged() => NotifyOwnersChanged();

    internal void ApplySceneState(
        SceneBrush sceneBrush,
        in Rect bounds)
    {
        Matrix4x4 transform = CreateCoordinateTransform(bounds.Size);
        GradientSpreadMethod spread = _extendMode switch
        {
            CompositionGradientExtendMode.Wrap =>
                GradientSpreadMethod.Repeat,
            CompositionGradientExtendMode.Mirror =>
                GradientSpreadMethod.Reflect,
            _ => GradientSpreadMethod.Pad
        };
        GradientColorInterpolationMode interpolation =
            _interpolationSpace == CompositionColorSpace.RgbLinear
                ? GradientColorInterpolationMode.ScRgbLinearInterpolation
                : GradientColorInterpolationMode.SRgbLinearInterpolation;
        switch (sceneBrush)
        {
            case LinearGradientBrush linear:
                linear.CoordinateTransform = transform;
                linear.SpreadMethod = spread;
                linear.ColorInterpolationMode = interpolation;
                break;
            case RadialGradientBrush radial:
                radial.CoordinateTransform = transform;
                radial.SpreadMethod = spread;
                radial.ColorInterpolationMode = interpolation;
                break;
        }
    }

    internal Vector2 ResolvePoint(Vector2 value, in Rect bounds) =>
        _mappingMode == CompositionMappingMode.Relative
            ? new Vector2(bounds.X, bounds.Y) + (value * bounds.Size)
            : new Vector2(bounds.X, bounds.Y) + value;

    internal Vector2 ResolveSize(Vector2 value, in Rect bounds) =>
        _mappingMode == CompositionMappingMode.Relative
            ? value * bounds.Size
            : value;

    private Matrix4x4 CreateCoordinateTransform(Vector2 brushSize)
    {
        Matrix3x2 forward =
            Matrix3x2.CreateTranslation(-_centerPoint) *
            Matrix3x2.CreateScale(_scale) *
            Matrix3x2.CreateRotation(_rotationAngle) *
            Matrix3x2.CreateTranslation(_centerPoint) *
            _transformMatrix *
            Matrix3x2.CreateTranslation(
                _offset - (_anchorPoint * brushSize));
        if (!Matrix3x2.Invert(forward, out Matrix3x2 inverse))
            inverse = default;
        return CompositionClip.ToMatrix4x4(inverse);
    }

    private void SetFinite(ref Vector2 field, Vector2 value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        NotifyGradientChanged();
    }

    private void SetFinite(ref float field, float value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        NotifyGradientChanged();
    }

    private void SetValue<T>(ref T field, T value)
        where T : struct
    {
        ThrowIfDisposed();
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        NotifyGradientChanged();
    }

    private static void ValidateEnum<T>(T value)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static bool IsFinite(in Matrix3x2 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32);
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionLinearGradientBrush : CompositionGradientBrush
{
    private Vector2 _endPoint = Vector2.One;
    private Vector2 _startPoint;

    internal CompositionLinearGradientBrush(Compositor compositor)
        : base(compositor)
    {
    }

    public Vector2 EndPoint
    {
        get => _endPoint;
        set => SetPoint(ref _endPoint, value);
    }

    public Vector2 StartPoint
    {
        get => _startPoint;
        set => SetPoint(ref _startPoint, value);
    }

    internal override void UpdateSceneBrush(
        in Rect bounds,
        ref SceneBrush? sceneBrush)
    {
        Vector2 start = ResolvePoint(_startPoint, bounds);
        Vector2 end = ResolvePoint(_endPoint, bounds);
        SceneGradientStop[] stops = ColorStops.GetSceneStops();
        if (sceneBrush is not LinearGradientBrush linear)
        {
            linear = new LinearGradientBrush(start, end, stops);
            sceneBrush = linear;
        }
        else
        {
            linear.StartPoint = start;
            linear.EndPoint = end;
            linear.Stops = stops;
        }
        ApplySceneState(linear, bounds);
    }

    private void SetPoint(ref Vector2 field, Vector2 value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        NotifyGradientChanged();
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionRadialGradientBrush : CompositionGradientBrush
{
    private Vector2 _ellipseCenter = new(0.5f, 0.5f);
    private Vector2 _ellipseRadius = new(0.5f, 0.5f);
    private Vector2 _gradientOriginOffset;

    internal CompositionRadialGradientBrush(Compositor compositor)
        : base(compositor)
    {
    }

    public Vector2 EllipseCenter
    {
        get => _ellipseCenter;
        set => SetPoint(ref _ellipseCenter, value, allowNegative: true);
    }

    public Vector2 EllipseRadius
    {
        get => _ellipseRadius;
        set => SetPoint(ref _ellipseRadius, value, allowNegative: false);
    }

    public Vector2 GradientOriginOffset
    {
        get => _gradientOriginOffset;
        set => SetPoint(ref _gradientOriginOffset, value, allowNegative: true);
    }

    internal override void UpdateSceneBrush(
        in Rect bounds,
        ref SceneBrush? sceneBrush)
    {
        Vector2 center = ResolvePoint(_ellipseCenter, bounds);
        Vector2 radius = ResolveSize(_ellipseRadius, bounds);
        Vector2 origin = center + ResolveSize(_gradientOriginOffset, bounds);
        SceneGradientStop[] stops = ColorStops.GetSceneStops();
        if (sceneBrush is not RadialGradientBrush radial)
        {
            radial = new RadialGradientBrush(
                center,
                origin,
                radius.X,
                radius.Y,
                stops);
            sceneBrush = radial;
        }
        else
        {
            radial.Center = center;
            radial.GradientOrigin = origin;
            radial.RadiusX = radius.X;
            radial.RadiusY = radius.Y;
            radial.Stops = stops;
        }
        ApplySceneState(radial, bounds);
    }

    private void SetPoint(
        ref Vector2 field,
        Vector2 value,
        bool allowNegative)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            (!allowNegative && (value.X < 0f || value.Y < 0f)))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (field == value)
            return;
        field = value;
        NotifyGradientChanged();
    }
}

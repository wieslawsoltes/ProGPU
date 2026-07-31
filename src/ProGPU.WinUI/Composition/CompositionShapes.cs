using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Scene;
using ProGPU.Vector;
using WinRT;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Composition;

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionStrokeCap
{
    Flat = 0,
    Square = 1,
    Round = 2,
    Triangle = 3
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionStrokeLineJoin
{
    Miter = 0,
    Bevel = 1,
    Round = 2,
    MiterOrBevel = 3
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public enum CompositionStretch
{
    None = 0,
    Fill = 1,
    Uniform = 2,
    UniformToFill = 3
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public class CompositionGeometry : CompositionObject
{
    private List<WeakReference<CompositionSpriteShape>>? _owners;
    private float _trimEnd = 1f;
    private float _trimOffset;
    private float _trimStart;

    protected internal CompositionGeometry(IObjectReference objRef)
        : base(objRef)
    {
    }

    protected CompositionGeometry(DerivedComposed _)
        : base(_)
    {
    }

    internal CompositionGeometry(Compositor compositor)
        : base(compositor)
    {
    }

    public float TrimEnd
    {
        get => _trimEnd;
        set => SetTrim(ref _trimEnd, value);
    }

    public float TrimOffset
    {
        get => _trimOffset;
        set => SetTrim(ref _trimOffset, value);
    }

    public float TrimStart
    {
        get => _trimStart;
        set => SetTrim(ref _trimStart, value);
    }

    internal bool HasFullTrim =>
        Math.Clamp(_trimEnd, 0f, 1f) -
        Math.Clamp(_trimStart, 0f, 1f) >= 1f;

    internal float TrimLength => Math.Max(
        0f,
        Math.Clamp(_trimEnd, 0f, 1f) -
        Math.Clamp(_trimStart, 0f, 1f));

    internal float TrimOrigin
    {
        get
        {
            float origin = Math.Clamp(_trimStart, 0f, 1f) + _trimOffset;
            return origin - MathF.Floor(origin);
        }
    }

    internal void AddOwner(CompositionSpriteShape owner)
    {
        List<WeakReference<CompositionSpriteShape>> owners =
            _owners ??= new List<WeakReference<CompositionSpriteShape>>();
        for (int index = owners.Count - 1; index >= 0; index--)
        {
            if (!owners[index].TryGetTarget(out CompositionSpriteShape? existing))
            {
                owners.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, owner))
                return;
        }

        owners.Add(new WeakReference<CompositionSpriteShape>(owner));
    }

    internal void RemoveOwner(CompositionSpriteShape owner)
    {
        if (_owners is null)
            return;

        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (!_owners[index].TryGetTarget(out CompositionSpriteShape? existing) ||
                ReferenceEquals(existing, owner))
            {
                _owners.RemoveAt(index);
            }
        }
    }

    internal virtual void Record(
        DrawingContext context,
        Brush? fill,
        Pen? stroke,
        Matrix4x4 transform)
    {
    }

    private void SetTrim(ref float field, float value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        OnTrimChanged();
        NotifyOwnersChanged();
    }

    internal virtual void OnTrimChanged()
    {
    }

    internal void NotifyOwnersChanged()
    {
        if (_owners is null)
            return;

        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (_owners[index].TryGetTarget(out CompositionSpriteShape? owner))
                owner.NotifyShapeChanged();
            else
                _owners.RemoveAt(index);
        }
    }

    internal static void ValidateFinite(Vector2 value, string propertyName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentOutOfRangeException(propertyName);
    }

}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionEllipseGeometry : CompositionGeometry
{
    private Vector2 _center;
    private Vector2 _radius;
    private PathGeometry? _trimmedPath;

    internal CompositionEllipseGeometry(Compositor compositor)
        : base(compositor)
    {
    }

    public Vector2 Center
    {
        get => _center;
        set
        {
            ThrowIfDisposed();
            ValidateFinite(value, nameof(value));
            if (_center == value)
                return;
            _center = value;
            _trimmedPath = null;
            NotifyOwnersChanged();
        }
    }

    public Vector2 Radius
    {
        get => _radius;
        set
        {
            ThrowIfDisposed();
            ValidateFinite(value, nameof(value));
            if (value.X < 0f || value.Y < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_radius == value)
                return;
            _radius = value;
            _trimmedPath = null;
            NotifyOwnersChanged();
        }
    }

    internal override void Record(
        DrawingContext context,
        Brush? fill,
        Pen? stroke,
        Matrix4x4 transform)
    {
        if (_radius.X <= 0f || _radius.Y <= 0f)
            return;
        if (HasFullTrim)
        {
            context.DrawEllipse(
                fill,
                stroke,
                _center,
                _radius.X,
                _radius.Y,
                transform);
            return;
        }

        _trimmedPath ??= CreateTrimmedPath();
        context.DrawPath(fill, stroke, _trimmedPath, transform);
    }

    internal override void OnTrimChanged() => _trimmedPath = null;

    private PathGeometry CreateTrimmedPath()
    {
        float length = TrimLength;
        var path = new PathGeometry();
        if (length <= 0f)
            return path;

        float startAngle = TrimOrigin * MathF.Tau;
        float endAngle = (TrimOrigin + length) * MathF.Tau;
        var start = _center + new Vector2(
            MathF.Cos(startAngle) * _radius.X,
            MathF.Sin(startAngle) * _radius.Y);
        var end = _center + new Vector2(
            MathF.Cos(endAngle) * _radius.X,
            MathF.Sin(endAngle) * _radius.Y);
        var figure = new PathFigure(start)
        {
            IsFilled = true
        };
        figure.Segments.Add(
            new ArcSegment(
                end,
                _radius,
                0f,
                length > 0.5f,
                SweepDirection.Clockwise));
        path.Figures.Add(figure);
        return path;
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionRectangleGeometry : CompositionGeometry
{
    private Vector2 _offset;
    private Vector2 _size;
    private PathGeometry? _trimmedPath;

    internal CompositionRectangleGeometry(Compositor compositor)
        : base(compositor)
    {
    }

    public Vector2 Offset
    {
        get => _offset;
        set
        {
            ThrowIfDisposed();
            ValidateFinite(value, nameof(value));
            if (_offset == value)
                return;
            _offset = value;
            _trimmedPath = null;
            NotifyOwnersChanged();
        }
    }

    public Vector2 Size
    {
        get => _size;
        set
        {
            ThrowIfDisposed();
            ValidateFinite(value, nameof(value));
            if (value.X < 0f || value.Y < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_size == value)
                return;
            _size = value;
            _trimmedPath = null;
            NotifyOwnersChanged();
        }
    }

    internal override void Record(
        DrawingContext context,
        Brush? fill,
        Pen? stroke,
        Matrix4x4 transform)
    {
        if (_size.X <= 0f || _size.Y <= 0f)
            return;
        if (HasFullTrim)
        {
            context.DrawRectangle(
                fill,
                stroke,
                new Rect(_offset.X, _offset.Y, _size.X, _size.Y),
                transform);
            return;
        }

        _trimmedPath ??= CreateTrimmedPath();
        context.DrawPath(fill, stroke, _trimmedPath, transform);
    }

    internal override void OnTrimChanged() => _trimmedPath = null;

    private PathGeometry CreateTrimmedPath()
    {
        float length = TrimLength;
        var path = new PathGeometry();
        if (length <= 0f)
            return path;

        float perimeter = 2f * (_size.X + _size.Y);
        float origin = TrimOrigin;
        float end = origin + length;
        var figure = new PathFigure(GetPerimeterPoint(origin))
        {
            IsFilled = true
        };
        Span<float> corners = stackalloc float[4]
        {
            _size.X / perimeter,
            (_size.X + _size.Y) / perimeter,
            ((2f * _size.X) + _size.Y) / perimeter,
            1f
        };
        for (int lap = 0; lap <= 1; lap++)
        {
            for (int index = 0; index < corners.Length; index++)
            {
                float corner = corners[index] + lap;
                if (corner > origin && corner < end)
                {
                    figure.Segments.Add(
                        new LineSegment(GetPerimeterPoint(corner)));
                }
            }
        }
        figure.Segments.Add(new LineSegment(GetPerimeterPoint(end)));
        path.Figures.Add(figure);
        return path;
    }

    private Vector2 GetPerimeterPoint(float progress)
    {
        progress -= MathF.Floor(progress);
        float perimeter = 2f * (_size.X + _size.Y);
        float distance = progress * perimeter;
        if (distance <= _size.X)
            return _offset + new Vector2(distance, 0f);
        distance -= _size.X;
        if (distance <= _size.Y)
            return _offset + new Vector2(_size.X, distance);
        distance -= _size.Y;
        if (distance <= _size.X)
            return _offset + new Vector2(_size.X - distance, _size.Y);
        distance -= _size.X;
        return _offset + new Vector2(0f, _size.Y - distance);
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionLineGeometry : CompositionGeometry
{
    private Vector2 _end;
    private Vector2 _start;
    private PathGeometry? _path;

    internal CompositionLineGeometry(Compositor compositor)
        : base(compositor)
    {
    }

    public Vector2 End
    {
        get => _end;
        set
        {
            ThrowIfDisposed();
            ValidateFinite(value, nameof(value));
            if (_end == value)
                return;
            _end = value;
            _path = null;
            NotifyOwnersChanged();
        }
    }

    public Vector2 Start
    {
        get => _start;
        set
        {
            ThrowIfDisposed();
            ValidateFinite(value, nameof(value));
            if (_start == value)
                return;
            _start = value;
            _path = null;
            NotifyOwnersChanged();
        }
    }

    internal override void Record(
        DrawingContext context,
        Brush? fill,
        Pen? stroke,
        Matrix4x4 transform)
    {
        if (stroke is null)
            return;
        _path ??= CreatePath();
        context.DrawPath(null, stroke, _path, transform);
    }

    private PathGeometry CreatePath()
    {
        float origin = HasFullTrim ? 0f : TrimOrigin;
        float length = TrimLength;
        var path = new PathGeometry();
        if (length <= 0f)
            return path;

        float start = Math.Clamp(origin, 0f, 1f);
        float end = Math.Clamp(origin + length, 0f, 1f);
        var figure = new PathFigure(Vector2.Lerp(_start, _end, start))
        {
            IsFilled = false
        };
        figure.Segments.Add(
            new LineSegment(Vector2.Lerp(_start, _end, end)));
        path.Figures.Add(figure);
        return path;
    }

    internal override void OnTrimChanged() => _path = null;
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public class CompositionShape : CompositionObject
{
    private Vector2 _centerPoint;
    private Vector2 _offset;
    private float _rotationAngle;
    private Vector2 _scale = Vector2.One;
    private Matrix3x2 _transformMatrix = Matrix3x2.Identity;

    protected internal CompositionShape(IObjectReference objRef)
        : base(objRef)
    {
    }

    protected CompositionShape(DerivedComposed _)
        : base(_)
    {
    }

    internal CompositionShape(Compositor compositor)
        : base(compositor)
    {
    }

    public Vector2 CenterPoint
    {
        get => _centerPoint;
        set => SetFinite(ref _centerPoint, value);
    }

    public Vector2 Offset
    {
        get => _offset;
        set => SetFinite(ref _offset, value);
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
            NotifyShapeChanged();
        }
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
            NotifyShapeChanged();
        }
    }

    internal CompositionShapeCollection? ParentCollection { get; set; }

    internal virtual void Record(
        DrawingContext context,
        Matrix3x2 parentTransform)
    {
    }

    internal Matrix3x2 GetTransform(Matrix3x2 parentTransform) =>
        Matrix3x2.CreateTranslation(-_centerPoint) *
        Matrix3x2.CreateScale(_scale) *
        Matrix3x2.CreateRotation(_rotationAngle) *
        Matrix3x2.CreateTranslation(_centerPoint) *
        _transformMatrix *
        Matrix3x2.CreateTranslation(_offset) *
        parentTransform;

    internal void NotifyShapeChanged() =>
        ParentCollection?.NotifyChanged();

    internal override void OnDisposed()
    {
        ParentCollection?.Remove(this);
        base.OnDisposed();
    }

    private void SetFinite(ref Vector2 field, Vector2 value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        NotifyShapeChanged();
    }

    private static bool IsFinite(in Matrix3x2 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32);
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionContainerShape : CompositionShape
{
    internal CompositionContainerShape(Compositor compositor)
        : base(compositor)
    {
        Shapes = new CompositionShapeCollection(compositor, this);
    }

    public CompositionShapeCollection Shapes { get; }

    internal override void Record(
        DrawingContext context,
        Matrix3x2 parentTransform)
    {
        Matrix3x2 transform = GetTransform(parentTransform);
        Shapes.Record(context, transform);
    }

    internal bool Contains(CompositionShape candidate)
    {
        if (ReferenceEquals(this, candidate))
            return true;
        foreach (CompositionShape shape in Shapes)
        {
            if (shape is CompositionContainerShape container &&
                container.Contains(candidate))
            {
                return true;
            }
        }
        return false;
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionSpriteShape : CompositionShape,
    ICompositionBrushOwner
{
    private CompositionBrush? _fillBrush;
    private CompositionGeometry? _geometry;
    private CompositionBrush? _strokeBrush;
    private readonly Pen _strokePen;
    private double[]? _appliedDashArray;
    private bool _isStrokeNonScaling;
    private CompositionStrokeCap _strokeDashCap;
    private float _strokeDashOffset;
    private CompositionStrokeCap _strokeEndCap;
    private CompositionStrokeLineJoin _strokeLineJoin;
    private float _strokeMiterLimit = 10f;
    private CompositionStrokeCap _strokeStartCap;
    private float _strokeThickness;

    internal CompositionSpriteShape(
        Compositor compositor,
        CompositionGeometry? geometry = null)
        : base(compositor)
    {
        StrokeDashArray = new CompositionStrokeDashArray(compositor, this);
        _strokePen = new Pen(new SolidColorBrush(Vector4.Zero), 0f);
        Geometry = geometry;
    }

    public CompositionBrush? FillBrush
    {
        get => _fillBrush;
        set => SetBrush(ref _fillBrush, value);
    }

    public CompositionGeometry? Geometry
    {
        get => _geometry;
        set
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_geometry, value))
                return;
            if (value is not null)
                EnsureSameCompositor(value);
            _geometry?.RemoveOwner(this);
            _geometry = value;
            _geometry?.AddOwner(this);
            NotifyShapeChanged();
        }
    }

    public bool IsStrokeNonScaling
    {
        get => _isStrokeNonScaling;
        set => SetValue(ref _isStrokeNonScaling, value);
    }

    public CompositionBrush? StrokeBrush
    {
        get => _strokeBrush;
        set => SetBrush(ref _strokeBrush, value);
    }

    public CompositionStrokeDashArray StrokeDashArray { get; }

    public CompositionStrokeCap StrokeDashCap
    {
        get => _strokeDashCap;
        set => SetValue(ref _strokeDashCap, value);
    }

    public float StrokeDashOffset
    {
        get => _strokeDashOffset;
        set => SetFinite(ref _strokeDashOffset, value, allowNegative: true);
    }

    public CompositionStrokeCap StrokeEndCap
    {
        get => _strokeEndCap;
        set => SetValue(ref _strokeEndCap, value);
    }

    public CompositionStrokeLineJoin StrokeLineJoin
    {
        get => _strokeLineJoin;
        set => SetValue(ref _strokeLineJoin, value);
    }

    public float StrokeMiterLimit
    {
        get => _strokeMiterLimit;
        set => SetFinite(ref _strokeMiterLimit, value, allowNegative: false);
    }

    public CompositionStrokeCap StrokeStartCap
    {
        get => _strokeStartCap;
        set => SetValue(ref _strokeStartCap, value);
    }

    public float StrokeThickness
    {
        get => _strokeThickness;
        set => SetFinite(ref _strokeThickness, value, allowNegative: false);
    }

    void ICompositionBrushOwner.NotifyBrushValueChanged() =>
        NotifyShapeChanged();

    internal override void Record(
        DrawingContext context,
        Matrix3x2 parentTransform)
    {
        if (_geometry is null)
            return;

        Matrix3x2 transform2D = GetTransform(parentTransform);
        Matrix4x4 transform = ToMatrix4x4(transform2D);
        Brush? fill = (_fillBrush as CompositionColorBrush)?.SceneBrush;
        Pen? stroke = null;
        if (_strokeBrush is CompositionColorBrush colorStroke &&
            _strokeThickness > 0f)
        {
            _strokePen.Brush = colorStroke.SceneBrush;
            _strokePen.Thickness = _isStrokeNonScaling
                ? _strokeThickness /
                  TransformMetrics.GetStrokeScale(transform)
                : _strokeThickness;
            _strokePen.LineJoin = ToLineJoin(_strokeLineJoin);
            _strokePen.MiterLimit = Math.Max(1f, _strokeMiterLimit);
            _strokePen.StartLineCap = ToLineCap(_strokeStartCap);
            _strokePen.EndLineCap = ToLineCap(_strokeEndCap);
            _strokePen.DashCap = ToLineCap(_strokeDashCap);
            _strokePen.DashOffset = _strokeDashOffset;
            double[]? dashArray = StrokeDashArray.GetSnapshot();
            if (!ReferenceEquals(_appliedDashArray, dashArray))
            {
                _strokePen.DashArray = dashArray;
                _appliedDashArray = dashArray;
            }
            stroke = _strokePen;
        }

        _geometry.Record(context, fill, stroke, transform);
    }

    internal override void OnDisposed()
    {
        _fillBrush?.RemoveOwner(this);
        _strokeBrush?.RemoveOwner(this);
        _geometry?.RemoveOwner(this);
        _fillBrush = null;
        _strokeBrush = null;
        _geometry = null;
        StrokeDashArray.Dispose();
        base.OnDisposed();
    }

    private void SetBrush(
        ref CompositionBrush? field,
        CompositionBrush? value)
    {
        ThrowIfDisposed();
        if (ReferenceEquals(field, value))
            return;
        if (value is not null)
            EnsureSameCompositor(value);
        field?.RemoveOwner(this);
        field = value;
        field?.AddOwner(this);
        NotifyShapeChanged();
    }

    private void SetFinite(
        ref float field,
        float value,
        bool allowNegative)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value) || (!allowNegative && value < 0f))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        NotifyShapeChanged();
    }

    private void SetValue<T>(ref T field, T value)
        where T : struct
    {
        ThrowIfDisposed();
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        NotifyShapeChanged();
    }

    private static Matrix4x4 ToMatrix4x4(in Matrix3x2 value) => new(
        value.M11, value.M12, 0f, 0f,
        value.M21, value.M22, 0f, 0f,
        0f, 0f, 1f, 0f,
        value.M31, value.M32, 0f, 1f);

    private static PenLineCap ToLineCap(CompositionStrokeCap value) =>
        value switch
        {
            CompositionStrokeCap.Square => PenLineCap.Square,
            CompositionStrokeCap.Round => PenLineCap.Round,
            CompositionStrokeCap.Triangle => PenLineCap.Triangle,
            _ => PenLineCap.Flat
        };

    private static PenLineJoin ToLineJoin(
        CompositionStrokeLineJoin value) => value switch
        {
            CompositionStrokeLineJoin.Bevel => PenLineJoin.Bevel,
            CompositionStrokeLineJoin.Round => PenLineJoin.Round,
            _ => PenLineJoin.Miter
        };
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionStrokeDashArray :
    CompositionObject,
    IList<float>
{
    private readonly List<float> _items = new();
    private readonly CompositionSpriteShape _owner;
    private double[]? _snapshot;

    internal CompositionStrokeDashArray(
        Compositor compositor,
        CompositionSpriteShape owner)
        : base(compositor)
    {
        _owner = owner;
    }

    [IndexerName("ListItem")]
    public float this[int index]
    {
        get => _items[index];
        set
        {
            Validate(value);
            if (_items[index] == value)
                return;
            _items[index] = value;
            Changed();
        }
    }

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public void Add(float item)
    {
        Validate(item);
        _items.Add(item);
        Changed();
    }

    public void Clear()
    {
        if (_items.Count == 0)
            return;
        _items.Clear();
        Changed();
    }

    public bool Contains(float item) => _items.Contains(item);

    public void CopyTo(float[] array, int arrayIndex) =>
        _items.CopyTo(array, arrayIndex);

    public IEnumerator<float> GetEnumerator() => _items.GetEnumerator();

    public int IndexOf(float item) => _items.IndexOf(item);

    public void Insert(int index, float item)
    {
        Validate(item);
        _items.Insert(index, item);
        Changed();
    }

    public bool Remove(float item)
    {
        bool removed = _items.Remove(item);
        if (removed)
            Changed();
        return removed;
    }

    public void RemoveAt(int index)
    {
        _items.RemoveAt(index);
        Changed();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal double[]? GetSnapshot()
    {
        if (_items.Count == 0)
            return null;
        if (_snapshot is null)
        {
            _snapshot = new double[_items.Count];
            for (int index = 0; index < _items.Count; index++)
                _snapshot[index] = _items[index];
        }
        return _snapshot;
    }

    private void Changed()
    {
        _snapshot = null;
        _owner.NotifyShapeChanged();
    }

    private void Validate(float value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException(nameof(value));
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionShapeCollection :
    CompositionObject,
    IList<CompositionShape>
{
    private readonly List<CompositionShape> _items = new();
    private readonly CompositionContainerShape? _containerOwner;
    private readonly ShapeVisual? _visualOwner;

    internal CompositionShapeCollection(
        Compositor compositor,
        ShapeVisual owner)
        : base(compositor)
    {
        _visualOwner = owner;
    }

    internal CompositionShapeCollection(
        Compositor compositor,
        CompositionContainerShape owner)
        : base(compositor)
    {
        _containerOwner = owner;
    }

    [IndexerName("ListItem")]
    public CompositionShape this[int index]
    {
        get => _items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_items[index], value))
                return;
            ValidateInsertion(value);
            RemoveAt(index);
            Insert(index, value);
        }
    }

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public void Add(CompositionShape item) => Insert(_items.Count, item);

    public void Clear()
    {
        ThrowIfDisposed();
        if (_items.Count == 0)
            return;
        foreach (CompositionShape item in _items)
            item.ParentCollection = null;
        _items.Clear();
        NotifyChanged();
    }

    public bool Contains(CompositionShape item) => _items.Contains(item);

    public void CopyTo(CompositionShape[] array, int arrayIndex) =>
        _items.CopyTo(array, arrayIndex);

    public IEnumerator<CompositionShape> GetEnumerator() =>
        _items.GetEnumerator();

    public int IndexOf(CompositionShape item) => _items.IndexOf(item);

    public void Insert(int index, CompositionShape item)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        if ((uint)index > (uint)_items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        ValidateInsertion(item);

        CompositionShapeCollection? previous = item.ParentCollection;
        int previousIndex = ReferenceEquals(previous, this)
            ? _items.IndexOf(item)
            : -1;
        if (previousIndex >= 0)
        {
            _items.RemoveAt(previousIndex);
            if (previousIndex < index)
                index--;
        }
        else
        {
            previous?.Remove(item);
        }

        _items.Insert(index, item);
        item.ParentCollection = this;
        NotifyChanged();
    }

    public bool Remove(CompositionShape item)
    {
        ThrowIfDisposed();
        int index = _items.IndexOf(item);
        if (index < 0)
            return false;
        _items.RemoveAt(index);
        item.ParentCollection = null;
        NotifyChanged();
        return true;
    }

    public void RemoveAt(int index)
    {
        ThrowIfDisposed();
        CompositionShape item = _items[index];
        _items.RemoveAt(index);
        item.ParentCollection = null;
        NotifyChanged();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void NotifyChanged()
    {
        if (_visualOwner is not null)
            _visualOwner.NotifyShapesChanged();
        else
            _containerOwner?.NotifyShapeChanged();
    }

    internal void Record(
        DrawingContext context,
        Matrix3x2 parentTransform)
    {
        foreach (CompositionShape shape in _items)
            shape.Record(context, parentTransform);
    }

    private void ValidateInsertion(CompositionShape item)
    {
        EnsureSameCompositor(item);
        if (_containerOwner is not null &&
            item is CompositionContainerShape candidate &&
            candidate.Contains(_containerOwner))
        {
            throw new InvalidOperationException(
                "A composition shape cannot contain itself or an ancestor.");
        }
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class ShapeVisual : ContainerVisual
{
    private CompositionViewBox? _viewBox;

    internal ShapeVisual(Compositor compositor)
        : base(compositor)
    {
        Shapes = new CompositionShapeCollection(compositor, this);
    }

    public CompositionShapeCollection Shapes { get; }

    public CompositionViewBox? ViewBox
    {
        get => _viewBox;
        set
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_viewBox, value))
                return;
            if (value is not null)
                EnsureSameCompositor(value);
            _viewBox?.RemoveOwner(this);
            _viewBox = value;
            _viewBox?.AddOwner(this);
            NotifyShapesChanged();
        }
    }

    internal void NotifyShapesChanged()
    {
        if (SceneNode is CompositionSceneNode node)
            node.UpdateContent();
    }

    internal void RecordShapes(DrawingContext context)
    {
        Matrix3x2 transform = _viewBox?.CreateTransform(EffectiveSize) ??
            Matrix3x2.Identity;
        Shapes.Record(context, transform);
    }

    internal override void OnDisposed()
    {
        _viewBox?.RemoveOwner(this);
        _viewBox = null;
        base.OnDisposed();
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionViewBox : CompositionObject
{
    private List<WeakReference<ShapeVisual>>? _owners;
    private float _horizontalAlignmentRatio = 0.5f;
    private Vector2 _offset;
    private Vector2 _size;
    private CompositionStretch _stretch = CompositionStretch.Uniform;
    private float _verticalAlignmentRatio = 0.5f;

    internal CompositionViewBox(Compositor compositor)
        : base(compositor)
    {
    }

    public float HorizontalAlignmentRatio
    {
        get => _horizontalAlignmentRatio;
        set => SetRatio(ref _horizontalAlignmentRatio, value);
    }

    public Vector2 Offset
    {
        get => _offset;
        set => SetVector(ref _offset, value, allowNegative: true);
    }

    public Vector2 Size
    {
        get => _size;
        set => SetVector(ref _size, value, allowNegative: false);
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

    public float VerticalAlignmentRatio
    {
        get => _verticalAlignmentRatio;
        set => SetRatio(ref _verticalAlignmentRatio, value);
    }

    internal void AddOwner(ShapeVisual owner)
    {
        List<WeakReference<ShapeVisual>> owners =
            _owners ??= new List<WeakReference<ShapeVisual>>();
        for (int index = owners.Count - 1; index >= 0; index--)
        {
            if (!owners[index].TryGetTarget(out ShapeVisual? existing))
            {
                owners.RemoveAt(index);
                continue;
            }
            if (ReferenceEquals(existing, owner))
                return;
        }
        owners.Add(new WeakReference<ShapeVisual>(owner));
    }

    internal void RemoveOwner(ShapeVisual owner)
    {
        if (_owners is null)
            return;
        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (!_owners[index].TryGetTarget(out ShapeVisual? existing) ||
                ReferenceEquals(existing, owner))
            {
                _owners.RemoveAt(index);
            }
        }
    }

    internal Matrix3x2 CreateTransform(Vector2 destinationSize)
    {
        if (_size.X <= 0f || _size.Y <= 0f)
            return Matrix3x2.Identity;

        Vector2 scale = _stretch switch
        {
            CompositionStretch.None => Vector2.One,
            CompositionStretch.Fill => destinationSize / _size,
            CompositionStretch.UniformToFill => new Vector2(
                MathF.Max(
                    destinationSize.X / _size.X,
                    destinationSize.Y / _size.Y)),
            _ => new Vector2(
                MathF.Min(
                    destinationSize.X / _size.X,
                    destinationSize.Y / _size.Y))
        };
        Vector2 available = destinationSize - (_size * scale);
        Vector2 alignment = new(
            available.X * _horizontalAlignmentRatio,
            available.Y * _verticalAlignmentRatio);
        return Matrix3x2.CreateTranslation(-_offset) *
            Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation(alignment);
    }

    private void SetRatio(ref float field, float value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        NotifyOwnersChanged();
    }

    private void SetVector(
        ref Vector2 field,
        Vector2 value,
        bool allowNegative)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            (!allowNegative && (value.X < 0f || value.Y < 0f)))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (field == value)
            return;
        field = value;
        NotifyOwnersChanged();
    }

    private void NotifyOwnersChanged()
    {
        if (_owners is null)
            return;
        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (_owners[index].TryGetTarget(out ShapeVisual? owner))
                owner.NotifyShapesChanged();
            else
                _owners.RemoveAt(index);
        }
    }
}

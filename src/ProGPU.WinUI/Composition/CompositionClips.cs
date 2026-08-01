using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using WinRT;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Composition;

internal interface ICompositionClipOwner
{
    void NotifyClipChanged();

    void NotifyClipDisposed(CompositionClip clip);
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public class CompositionClip : CompositionObject
{
    private List<WeakReference<ICompositionClipOwner>>? _owners;
    private Vector2 _anchorPoint;
    private Vector2 _centerPoint;
    private Vector2 _offset;
    private float _rotationAngle;
    private Vector2 _scale = Vector2.One;
    private Matrix3x2 _transformMatrix = Matrix3x2.Identity;

    protected internal CompositionClip(IObjectReference objRef)
        : base(objRef)
    {
    }

    protected CompositionClip(DerivedComposed _)
        : base(_)
    {
    }

    internal CompositionClip(Compositor compositor)
        : base(compositor)
    {
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
            NotifyOwnersChanged();
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
            NotifyOwnersChanged();
        }
    }

    internal void AddOwner(ICompositionClipOwner owner)
    {
        ThrowIfDisposed();
        List<WeakReference<ICompositionClipOwner>> owners =
            _owners ??= new List<WeakReference<ICompositionClipOwner>>();
        for (int index = owners.Count - 1; index >= 0; index--)
        {
            if (!owners[index].TryGetTarget(out ICompositionClipOwner? existing))
            {
                owners.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, owner))
                return;
        }

        owners.Add(new WeakReference<ICompositionClipOwner>(owner));
    }

    internal void RemoveOwner(ICompositionClipOwner owner)
    {
        if (_owners is null)
            return;

        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (!_owners[index].TryGetTarget(out ICompositionClipOwner? existing) ||
                ReferenceEquals(existing, owner))
            {
                _owners.RemoveAt(index);
            }
        }
    }

    internal virtual bool TryCreateSceneClip(
        Vector2 visualSize,
        out VisualCompositeClip clip)
    {
        clip = default;
        return false;
    }

    internal Matrix4x4 CreateTransform(Vector2 clipSize)
    {
        Matrix3x2 value =
            Matrix3x2.CreateTranslation(-_centerPoint) *
            Matrix3x2.CreateScale(_scale) *
            Matrix3x2.CreateRotation(_rotationAngle) *
            Matrix3x2.CreateTranslation(_centerPoint) *
            _transformMatrix *
            Matrix3x2.CreateTranslation(
                _offset - (_anchorPoint * clipSize));
        return ToMatrix4x4(value);
    }

    internal void NotifyOwnersChanged()
    {
        if (_owners is null)
            return;

        for (int index = _owners.Count - 1; index >= 0; index--)
        {
            if (_owners[index].TryGetTarget(out ICompositionClipOwner? owner))
                owner.NotifyClipChanged();
            else
                _owners.RemoveAt(index);
        }
    }

    internal override void OnDisposed()
    {
        if (_owners is not null)
        {
            for (int index = _owners.Count - 1; index >= 0; index--)
            {
                if (_owners[index].TryGetTarget(out ICompositionClipOwner? owner))
                    owner.NotifyClipDisposed(this);
            }
            _owners.Clear();
        }
        base.OnDisposed();
    }

    internal void SetFinite(ref Vector2 field, Vector2 value)
    {
        ThrowIfDisposed();
        ValidateFinite(value, nameof(value));
        if (field == value)
            return;
        field = value;
        NotifyOwnersChanged();
    }

    internal void SetFinite(ref float field, float value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        NotifyOwnersChanged();
    }

    internal void SetRadius(ref Vector2 field, Vector2 value)
    {
        ThrowIfDisposed();
        ValidateFinite(value, nameof(value));
        if (value.X < 0f || value.Y < 0f)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        OnClipGeometryChanged();
    }

    internal virtual void OnClipGeometryChanged() =>
        NotifyOwnersChanged();

    private static void ValidateFinite(Vector2 value, string propertyName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentOutOfRangeException(propertyName);
    }

    private static bool IsFinite(in Matrix3x2 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32);

    internal static Matrix4x4 ToMatrix4x4(in Matrix3x2 value) => new(
        value.M11, value.M12, 0f, 0f,
        value.M21, value.M22, 0f, 0f,
        0f, 0f, 1f, 0f,
        value.M31, value.M32, 0f, 1f);
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class InsetClip : CompositionClip
{
    private float _leftInset;
    private float _topInset;
    private float _rightInset;
    private float _bottomInset;

    internal InsetClip(
        Compositor compositor,
        float leftInset = 0f,
        float topInset = 0f,
        float rightInset = 0f,
        float bottomInset = 0f)
        : base(compositor)
    {
        ValidateInsets(leftInset, topInset, rightInset, bottomInset);
        _leftInset = leftInset;
        _topInset = topInset;
        _rightInset = rightInset;
        _bottomInset = bottomInset;
    }

    public float BottomInset
    {
        get => _bottomInset;
        set => SetFinite(ref _bottomInset, value);
    }

    public float LeftInset
    {
        get => _leftInset;
        set => SetFinite(ref _leftInset, value);
    }

    public float RightInset
    {
        get => _rightInset;
        set => SetFinite(ref _rightInset, value);
    }

    public float TopInset
    {
        get => _topInset;
        set => SetFinite(ref _topInset, value);
    }

    internal override bool TryCreateSceneClip(
        Vector2 visualSize,
        out VisualCompositeClip clip)
    {
        var bounds = new Rect(
            _leftInset,
            _topInset,
            MathF.Max(0f, visualSize.X - _leftInset - _rightInset),
            MathF.Max(0f, visualSize.Y - _topInset - _bottomInset));
        clip = new VisualCompositeClip(
            bounds,
            CreateTransform(bounds.Size));
        return true;
    }

    private static void ValidateInsets(
        float leftInset,
        float topInset,
        float rightInset,
        float bottomInset)
    {
        if (!float.IsFinite(leftInset) || !float.IsFinite(topInset) ||
            !float.IsFinite(rightInset) || !float.IsFinite(bottomInset))
        {
            throw new ArgumentOutOfRangeException(nameof(leftInset));
        }
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class RectangleClip : CompositionClip
{
    private float _left;
    private float _top;
    private float _right;
    private float _bottom;
    private Vector2 _topLeftRadius;
    private Vector2 _topRightRadius;
    private Vector2 _bottomRightRadius;
    private Vector2 _bottomLeftRadius;
    private Vector2 _cachedVisualSize = new(float.NaN);
    private PathGeometry? _cachedPath;

    internal RectangleClip(
        Compositor compositor,
        float left = 0f,
        float top = 0f,
        float right = 0f,
        float bottom = 0f,
        Vector2 topLeftRadius = default,
        Vector2 topRightRadius = default,
        Vector2 bottomRightRadius = default,
        Vector2 bottomLeftRadius = default)
        : base(compositor)
    {
        ValidateSides(left, top, right, bottom);
        ValidateRadius(topLeftRadius);
        ValidateRadius(topRightRadius);
        ValidateRadius(bottomRightRadius);
        ValidateRadius(bottomLeftRadius);
        _left = left;
        _top = top;
        _right = right;
        _bottom = bottom;
        _topLeftRadius = topLeftRadius;
        _topRightRadius = topRightRadius;
        _bottomRightRadius = bottomRightRadius;
        _bottomLeftRadius = bottomLeftRadius;
    }

    public float Bottom
    {
        get => _bottom;
        set => SetSide(ref _bottom, value);
    }

    public Vector2 BottomLeftRadius
    {
        get => _bottomLeftRadius;
        set => SetRadius(ref _bottomLeftRadius, value);
    }

    public Vector2 BottomRightRadius
    {
        get => _bottomRightRadius;
        set => SetRadius(ref _bottomRightRadius, value);
    }

    public float Left
    {
        get => _left;
        set => SetSide(ref _left, value);
    }

    public float Right
    {
        get => _right;
        set => SetSide(ref _right, value);
    }

    public float Top
    {
        get => _top;
        set => SetSide(ref _top, value);
    }

    public Vector2 TopLeftRadius
    {
        get => _topLeftRadius;
        set => SetRadius(ref _topLeftRadius, value);
    }

    public Vector2 TopRightRadius
    {
        get => _topRightRadius;
        set => SetRadius(ref _topRightRadius, value);
    }

    internal override bool TryCreateSceneClip(
        Vector2 visualSize,
        out VisualCompositeClip clip)
    {
        var bounds = new Rect(
            _left,
            _top,
            MathF.Max(0f, visualSize.X - _left - _right),
            MathF.Max(0f, visualSize.Y - _top - _bottom));
        Matrix4x4 transform = CreateTransform(bounds.Size);
        if (bounds.IsEmpty || !HasRoundedCorners)
        {
            clip = new VisualCompositeClip(bounds, transform);
            return true;
        }

        if (_cachedPath is null || _cachedVisualSize != visualSize)
        {
            _cachedVisualSize = visualSize;
            _cachedPath = CreateRoundedPath(bounds);
        }
        clip = new VisualCompositeClip(_cachedPath, transform);
        return true;
    }

    internal override void OnClipGeometryChanged()
    {
        _cachedPath = null;
        base.OnClipGeometryChanged();
    }

    private bool HasRoundedCorners =>
        HasRadius(_topLeftRadius) || HasRadius(_topRightRadius) ||
        HasRadius(_bottomRightRadius) || HasRadius(_bottomLeftRadius);

    private void SetSide(ref float field, float value)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value)
            return;
        field = value;
        _cachedPath = null;
        NotifyOwnersChanged();
    }

    private PathGeometry CreateRoundedPath(Rect bounds)
    {
        Vector2 topLeft = NormalizeRadius(_topLeftRadius);
        Vector2 topRight = NormalizeRadius(_topRightRadius);
        Vector2 bottomRight = NormalizeRadius(_bottomRightRadius);
        Vector2 bottomLeft = NormalizeRadius(_bottomLeftRadius);
        float scale = MathF.Min(
            1f,
            MathF.Min(
                GetFitScale(bounds.Width, topLeft.X + topRight.X),
                MathF.Min(
                    GetFitScale(bounds.Width, bottomLeft.X + bottomRight.X),
                    MathF.Min(
                        GetFitScale(bounds.Height, topLeft.Y + bottomLeft.Y),
                        GetFitScale(bounds.Height, topRight.Y + bottomRight.Y)))));
        topLeft *= scale;
        topRight *= scale;
        bottomRight *= scale;
        bottomLeft *= scale;

        float right = bounds.Right;
        float bottom = bounds.Bottom;
        var figure = new PathFigure(
            new Vector2(bounds.X + topLeft.X, bounds.Y),
            isClosed: true);
        figure.Segments.Add(new LineSegment(
            new Vector2(right - topRight.X, bounds.Y)));
        AddCorner(
            figure,
            topRight,
            new Vector2(right, bounds.Y + topRight.Y),
            new Vector2(right, bounds.Y));
        figure.Segments.Add(new LineSegment(
            new Vector2(right, bottom - bottomRight.Y)));
        AddCorner(
            figure,
            bottomRight,
            new Vector2(right - bottomRight.X, bottom),
            new Vector2(right, bottom));
        figure.Segments.Add(new LineSegment(
            new Vector2(bounds.X + bottomLeft.X, bottom)));
        AddCorner(
            figure,
            bottomLeft,
            new Vector2(bounds.X, bottom - bottomLeft.Y),
            new Vector2(bounds.X, bottom));
        figure.Segments.Add(new LineSegment(
            new Vector2(bounds.X, bounds.Y + topLeft.Y)));
        AddCorner(
            figure,
            topLeft,
            new Vector2(bounds.X + topLeft.X, bounds.Y),
            new Vector2(bounds.X, bounds.Y));

        var path = new PathGeometry();
        path.Figures.Add(figure);
        return path;
    }

    private static void AddCorner(
        PathFigure figure,
        Vector2 radius,
        Vector2 end,
        Vector2 squareCorner)
    {
        if (!HasRadius(radius))
        {
            if (figure.Segments[^1] is not LineSegment line ||
                line.Point != squareCorner)
            {
                figure.Segments.Add(new LineSegment(squareCorner));
            }
            return;
        }

        figure.Segments.Add(new ArcSegment(
            end,
            radius,
            0f,
            false,
            SweepDirection.Clockwise));
    }

    private static Vector2 NormalizeRadius(Vector2 value) =>
        HasRadius(value) ? value : Vector2.Zero;

    private static bool HasRadius(Vector2 value) =>
        value.X > 0f && value.Y > 0f;

    private static float GetFitScale(float available, float requested) =>
        requested > 0f ? available / requested : 1f;

    private static void ValidateSides(
        float left,
        float top,
        float right,
        float bottom)
    {
        if (!float.IsFinite(left) || !float.IsFinite(top) ||
            !float.IsFinite(right) || !float.IsFinite(bottom))
        {
            throw new ArgumentOutOfRangeException(nameof(left));
        }
    }

    private static void ValidateRadius(Vector2 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            value.X < 0f || value.Y < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionGeometricClip : CompositionClip,
    ICompositionGeometryOwner,
    ICompositionViewBoxOwner
{
    private CompositionGeometry? _geometry;
    private CompositionViewBox? _viewBox;

    internal CompositionGeometricClip(
        Compositor compositor,
        CompositionGeometry? geometry = null)
        : base(compositor)
    {
        Geometry = geometry;
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
            NotifyOwnersChanged();
        }
    }

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
            NotifyOwnersChanged();
        }
    }

    internal override bool TryCreateSceneClip(
        Vector2 visualSize,
        out VisualCompositeClip clip)
    {
        PathGeometry? path = _geometry?.GetClipPath();
        if (path is null)
        {
            clip = default;
            return false;
        }

        if (!path.TryGetBounds(out Vector2 min, out Vector2 max) ||
            max.X <= min.X || max.Y <= min.Y)
        {
            clip = new VisualCompositeClip(
                Rect.Empty,
                Matrix4x4.Identity);
            return true;
        }

        Matrix3x2 viewBoxTransform =
            _viewBox?.CreateTransform(visualSize) ?? Matrix3x2.Identity;
        Vector2 clipSize = GetTransformedSize(path, viewBoxTransform);
        Matrix4x4 transform =
            CompositionClip.ToMatrix4x4(viewBoxTransform) *
            CreateTransform(clipSize);
        clip = new VisualCompositeClip(path, transform);
        return true;
    }

    void ICompositionGeometryOwner.NotifyGeometryChanged() =>
        NotifyOwnersChanged();

    void ICompositionViewBoxOwner.NotifyViewBoxChanged() =>
        NotifyOwnersChanged();

    internal override void OnDisposed()
    {
        _geometry?.RemoveOwner(this);
        _viewBox?.RemoveOwner(this);
        _geometry = null;
        _viewBox = null;
        base.OnDisposed();
    }

    private static Vector2 GetTransformedSize(
        PathGeometry path,
        Matrix3x2 transform)
    {
        if (!path.TryGetBounds(out Vector2 min, out Vector2 max))
            return Vector2.Zero;

        Vector2 p0 = Vector2.Transform(min, transform);
        Vector2 p1 = Vector2.Transform(new Vector2(max.X, min.Y), transform);
        Vector2 p2 = Vector2.Transform(max, transform);
        Vector2 p3 = Vector2.Transform(new Vector2(min.X, max.Y), transform);
        Vector2 transformedMin = Vector2.Min(
            Vector2.Min(p0, p1),
            Vector2.Min(p2, p3));
        Vector2 transformedMax = Vector2.Max(
            Vector2.Max(p0, p1),
            Vector2.Max(p2, p3));
        return transformedMax - transformedMin;
    }
}

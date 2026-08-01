using System.Numerics;
using Microsoft.UI.Dispatching;
using WinRT;
using Windows.Foundation.Metadata;
using Windows.UI;

namespace Microsoft.UI.Composition;

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public class CompositionObject : IAnimationObject, IDisposable
{
    private CompositionPropertySet? _properties;
    private bool _isDisposed;

    protected internal CompositionObject(IObjectReference objRef)
        : this(Compositor.GetSharedForCurrentThread())
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected CompositionObject(DerivedComposed _)
        : this(Compositor.GetSharedForCurrentThread())
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    internal CompositionObject(Compositor compositor)
    {
        Compositor = compositor ??
            throw new ArgumentNullException(nameof(compositor));
    }

    public string Comment { get; set; } = string.Empty;

    public Compositor Compositor { get; }

    public DispatcherQueue? DispatcherQueue => Compositor.DispatcherQueue;

    public CompositionPropertySet Properties
    {
        get
        {
            ThrowIfDisposed();
            return _properties ??= new CompositionPropertySet(Compositor);
        }
    }

    internal bool IsDisposed => _isDisposed;

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        OnDisposed();
        GC.SuppressFinalize(this);
    }

    public void PopulatePropertyInfo(
        string propertyName,
        AnimationPropertyInfo propertyInfo)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentNullException.ThrowIfNull(propertyInfo);
        EnsureSameCompositor(propertyInfo);
        propertyInfo.Resolve(this, propertyName);
    }

    internal void EnsureSameCompositor(CompositionObject value)
    {
        if (!ReferenceEquals(Compositor, value.Compositor))
        {
            throw new InvalidOperationException(
                "Composition objects must belong to the same Compositor.");
        }
    }

    internal void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(GetType().FullName);
        Compositor.ThrowIfDisposed();
    }

    internal virtual void OnDisposed()
    {
        _properties?.Dispose();
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class AnimationPropertyInfo : CompositionObject
{
    private CompositionObject? _resolvedObject;
    private string _resolvedProperty = string.Empty;

    internal AnimationPropertyInfo(Compositor compositor)
        : base(compositor)
    {
    }

    public AnimationPropertyAccessMode AccessMode { get; set; }

    public CompositionObject? GetResolvedCompositionObject() =>
        _resolvedObject;

    public string GetResolvedCompositionObjectProperty() =>
        _resolvedProperty;

    internal void Resolve(
        CompositionObject compositionObject,
        string propertyName)
    {
        _resolvedObject = compositionObject;
        _resolvedProperty = propertyName;
    }
}

[ContractVersion(CompositionContract.Name, CompositionContract.Version1)]
public sealed class CompositionPropertySet : CompositionObject
{
    private readonly Dictionary<string, CompositionPropertyValue> _values =
        new(StringComparer.Ordinal);

    internal CompositionPropertySet(Compositor compositor)
        : base(compositor)
    {
    }

    public void InsertBoolean(string propertyName, bool value) =>
        Insert(propertyName, CompositionPropertyValue.FromBoolean(value));

    public void InsertColor(string propertyName, Color value) =>
        Insert(propertyName, CompositionPropertyValue.FromColor(value));

    public void InsertMatrix3x2(string propertyName, Matrix3x2 value) =>
        Insert(propertyName, CompositionPropertyValue.FromMatrix3x2(value));

    public void InsertMatrix4x4(string propertyName, Matrix4x4 value) =>
        Insert(propertyName, CompositionPropertyValue.FromMatrix4x4(value));

    public void InsertQuaternion(string propertyName, Quaternion value) =>
        Insert(propertyName, CompositionPropertyValue.FromQuaternion(value));

    public void InsertScalar(string propertyName, float value) =>
        Insert(propertyName, CompositionPropertyValue.FromScalar(value));

    public void InsertVector2(string propertyName, Vector2 value) =>
        Insert(propertyName, CompositionPropertyValue.FromVector2(value));

    public void InsertVector3(string propertyName, Vector3 value) =>
        Insert(propertyName, CompositionPropertyValue.FromVector3(value));

    public void InsertVector4(string propertyName, Vector4 value) =>
        Insert(propertyName, CompositionPropertyValue.FromVector4(value));

    public CompositionGetValueStatus TryGetBoolean(
        string propertyName,
        out bool value)
    {
        CompositionGetValueStatus status = TryGet(
            propertyName,
            CompositionPropertyKind.Boolean,
            out CompositionPropertyValue stored);
        value = status == CompositionGetValueStatus.Succeeded &&
            stored.Boolean;
        return status;
    }

    public CompositionGetValueStatus TryGetColor(
        string propertyName,
        out Color value)
    {
        CompositionGetValueStatus status = TryGet(
            propertyName,
            CompositionPropertyKind.Color,
            out CompositionPropertyValue stored);
        value = status == CompositionGetValueStatus.Succeeded
            ? stored.Color
            : default;
        return status;
    }

    public CompositionGetValueStatus TryGetMatrix3x2(
        string propertyName,
        out Matrix3x2 value)
    {
        CompositionGetValueStatus status = TryGet(
            propertyName,
            CompositionPropertyKind.Matrix3x2,
            out CompositionPropertyValue stored);
        value = status == CompositionGetValueStatus.Succeeded
            ? stored.Matrix3x2
            : default;
        return status;
    }

    public CompositionGetValueStatus TryGetMatrix4x4(
        string propertyName,
        out Matrix4x4 value)
    {
        CompositionGetValueStatus status = TryGet(
            propertyName,
            CompositionPropertyKind.Matrix4x4,
            out CompositionPropertyValue stored);
        value = status == CompositionGetValueStatus.Succeeded
            ? stored.Matrix4x4
            : default;
        return status;
    }

    public CompositionGetValueStatus TryGetQuaternion(
        string propertyName,
        out Quaternion value)
    {
        CompositionGetValueStatus status = TryGet(
            propertyName,
            CompositionPropertyKind.Quaternion,
            out CompositionPropertyValue stored);
        value = status == CompositionGetValueStatus.Succeeded
            ? stored.Quaternion
            : default;
        return status;
    }

    public CompositionGetValueStatus TryGetScalar(
        string propertyName,
        out float value)
    {
        CompositionGetValueStatus status = TryGet(
            propertyName,
            CompositionPropertyKind.Scalar,
            out CompositionPropertyValue stored);
        value = status == CompositionGetValueStatus.Succeeded
            ? stored.Scalar
            : default;
        return status;
    }

    public CompositionGetValueStatus TryGetVector2(
        string propertyName,
        out Vector2 value)
    {
        CompositionGetValueStatus status = TryGet(
            propertyName,
            CompositionPropertyKind.Vector2,
            out CompositionPropertyValue stored);
        value = status == CompositionGetValueStatus.Succeeded
            ? stored.Vector2
            : default;
        return status;
    }

    public CompositionGetValueStatus TryGetVector3(
        string propertyName,
        out Vector3 value)
    {
        CompositionGetValueStatus status = TryGet(
            propertyName,
            CompositionPropertyKind.Vector3,
            out CompositionPropertyValue stored);
        value = status == CompositionGetValueStatus.Succeeded
            ? stored.Vector3
            : default;
        return status;
    }

    public CompositionGetValueStatus TryGetVector4(
        string propertyName,
        out Vector4 value)
    {
        CompositionGetValueStatus status = TryGet(
            propertyName,
            CompositionPropertyKind.Vector4,
            out CompositionPropertyValue stored);
        value = status == CompositionGetValueStatus.Succeeded
            ? stored.Vector4
            : default;
        return status;
    }

    private void Insert(
        string propertyName,
        in CompositionPropertyValue value)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        _values[propertyName] = value;
    }

    private CompositionGetValueStatus TryGet(
        string propertyName,
        CompositionPropertyKind expected,
        out CompositionPropertyValue value)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        if (!_values.TryGetValue(propertyName, out value))
            return CompositionGetValueStatus.NotFound;
        return value.Kind == expected
            ? CompositionGetValueStatus.Succeeded
            : CompositionGetValueStatus.TypeMismatch;
    }

    internal override void OnDisposed()
    {
        _values.Clear();
        base.OnDisposed();
    }

    private enum CompositionPropertyKind : byte
    {
        Boolean,
        Color,
        Matrix3x2,
        Matrix4x4,
        Quaternion,
        Scalar,
        Vector2,
        Vector3,
        Vector4
    }

    private readonly struct CompositionPropertyValue
    {
        private CompositionPropertyValue(
            CompositionPropertyKind kind,
            bool boolean = default,
            Color color = default,
            Matrix3x2 matrix3x2 = default,
            Matrix4x4 matrix4x4 = default,
            Quaternion quaternion = default,
            float scalar = default,
            Vector2 vector2 = default,
            Vector3 vector3 = default,
            Vector4 vector4 = default)
        {
            Kind = kind;
            Boolean = boolean;
            Color = color;
            Matrix3x2 = matrix3x2;
            Matrix4x4 = matrix4x4;
            Quaternion = quaternion;
            Scalar = scalar;
            Vector2 = vector2;
            Vector3 = vector3;
            Vector4 = vector4;
        }

        internal CompositionPropertyKind Kind { get; }
        internal bool Boolean { get; }
        internal Color Color { get; }
        internal Matrix3x2 Matrix3x2 { get; }
        internal Matrix4x4 Matrix4x4 { get; }
        internal Quaternion Quaternion { get; }
        internal float Scalar { get; }
        internal Vector2 Vector2 { get; }
        internal Vector3 Vector3 { get; }
        internal Vector4 Vector4 { get; }

        internal static CompositionPropertyValue FromBoolean(bool value) =>
            new(CompositionPropertyKind.Boolean, boolean: value);

        internal static CompositionPropertyValue FromColor(Color value) =>
            new(CompositionPropertyKind.Color, color: value);

        internal static CompositionPropertyValue FromMatrix3x2(
            Matrix3x2 value) =>
            new(CompositionPropertyKind.Matrix3x2, matrix3x2: value);

        internal static CompositionPropertyValue FromMatrix4x4(
            Matrix4x4 value) =>
            new(CompositionPropertyKind.Matrix4x4, matrix4x4: value);

        internal static CompositionPropertyValue FromQuaternion(
            Quaternion value) =>
            new(CompositionPropertyKind.Quaternion, quaternion: value);

        internal static CompositionPropertyValue FromScalar(float value) =>
            new(CompositionPropertyKind.Scalar, scalar: value);

        internal static CompositionPropertyValue FromVector2(Vector2 value) =>
            new(CompositionPropertyKind.Vector2, vector2: value);

        internal static CompositionPropertyValue FromVector3(Vector3 value) =>
            new(CompositionPropertyKind.Vector3, vector3: value);

        internal static CompositionPropertyValue FromVector4(Vector4 value) =>
            new(CompositionPropertyKind.Vector4, vector4: value);
    }
}

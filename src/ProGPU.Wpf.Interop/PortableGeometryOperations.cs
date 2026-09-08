namespace ProGPU.Wpf.Interop;

public enum PortableGeometryOperandKind { Path, Group, Combined }
public enum PortableGeometryCombineMode { Union, Intersect, Xor, Exclude }

/// <summary>
/// Bounds-free synchronous geometry snapshot. Path leaves own their transform;
/// Group/Combined nodes use Transform and Children. A combined node has two
/// children. Group fill applies to its collected figures, not a union of children.
/// This is separate from retained rendering descriptors to avoid recursive Bounds
/// queries and to preserve group semantics without changing renderer DTO kinds.
/// </summary>
public sealed class PortableGeometryOperand
{
    public PortableGeometryOperandKind Kind { get; init; }
    public PortableGeometryPath? Path { get; init; }
    public PortableGeometryOperand[] Children { get; init; } = [];
    public PortableFillRule FillRule { get; init; } = PortableFillRule.Nonzero;
    public PortableGeometryCombineMode CombineMode { get; init; }
    public PortableMatrix3x2 Transform { get; init; } = PortableMatrix3x2.Identity;
}

public interface IPortableGeometryOperations
{
    /// <summary>
    /// Synchronous CPU-owned result; no input retained. Preserve filled figures,
    /// groups and nested operations. Result transform is applied in output space.
    /// Output is a materialized path, not a deferred boolean or bounds surrogate.
    /// Missing native capability and unsupported finite ranges fail explicitly.
    /// </summary>
    PortableGeometryPath Combine(PortableGeometryOperand first, PortableGeometryOperand second,
        PortableGeometryCombineMode mode, PortableMatrix3x2 resultTransform,
        double tolerance, bool relativeTolerance);
}

public static partial class PortableWpfServiceRegistry
{
    private sealed class GeometryRegistration(IPortableGeometryOperations service) : IDisposable
    {
        internal IPortableGeometryOperations Service { get; } = service;
        public void Dispose() => Interlocked.CompareExchange(ref s_geometryOperations, null, this);
    }

    private static GeometryRegistration? s_geometryOperations;

    public static bool TryGetGeometryOperations(out IPortableGeometryOperations service)
    {
        service = Volatile.Read(ref s_geometryOperations)?.Service!;
        return service != null;
    }

    /// <summary>Replaces the process geometry provider; disposal clears only this registration.</summary>
    public static IDisposable RegisterGeometryOperations(IPortableGeometryOperations service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var registration = new GeometryRegistration(service);
        Interlocked.Exchange(ref s_geometryOperations, registration);
        return registration;
    }

    /// <summary>Installs a default without overriding an explicit provider; performs no native work.</summary>
    public static void EnsureGeometryOperations(IPortableGeometryOperations service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Volatile.Read(ref s_geometryOperations) == null)
            Interlocked.CompareExchange(ref s_geometryOperations, new GeometryRegistration(service), null);
    }
}

namespace ProGPU.Wpf.Interop;

public enum PortableGeometryOperandKind { Path, Group, Combined }
public enum PortableGeometryCombineMode { Union, Intersect, Xor, Exclude }
/// <summary>Filled relation of the first operand relative to the second; not native wire values.</summary>
public enum PortableGeometryRelation { Disjoint, IsContained, Contains, Overlap }

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
    /// Compares actual filled coverage, not envelopes. Empty coverage is
    /// disjoint; equal nonempty coverage is IsContained. Unsupported queries throw.
    /// </summary>
    PortableGeometryRelation CompareFill(PortableGeometryOperand first, PortableGeometryOperand second,
        double tolerance, bool relativeTolerance)
        => throw new PlatformNotSupportedException("The geometry provider does not support relation queries.");

    /// <summary>Union of actual fill bounds and emitted pen coverage after the world transform.</summary>
    PortableRect GetRenderBounds(PortableGeometryOperand geometry, in PortablePenState pen,
        PortableMatrix3x2 worldTransform, double tolerance, bool relativeTolerance, bool skipHollows)
        => throw new PlatformNotSupportedException("The geometry provider does not support pen bounds queries.");

    /// <summary>Actual stroke membership; brush identity does not change stroke geometry.</summary>
    bool StrokeContains(PortableGeometryOperand geometry, in PortablePenState pen,
        PortablePoint point, double tolerance, bool relativeTolerance)
        => throw new PlatformNotSupportedException("The geometry provider does not support stroke queries.");

    /// <summary>
    /// Tight unstroked bounds after the additional world transform. Empty is
    /// distinct from a zero-size bound; skipHollows excludes non-filled figures.
    /// </summary>
    PortableRect GetBounds(PortableGeometryOperand geometry, PortableMatrix3x2 worldTransform, bool skipHollows)
        => throw new PlatformNotSupportedException("The geometry provider does not support bounds queries.");

    /// <summary>Filled-area point membership in geometry output coordinates; never a bounds hit surrogate.</summary>
    bool FillContains(PortableGeometryOperand geometry, PortablePoint point, double tolerance, bool relativeTolerance)
        => throw new PlatformNotSupportedException("The geometry provider does not support fill queries.");

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
    private static readonly PortableDefaultServiceSlot<IPortableGeometryOperations> s_geometryOperations = new();

    public static bool TryGetGeometryOperations(out IPortableGeometryOperations service)
    {
        service = s_geometryOperations.Current!;
        return service != null;
    }

    /// <summary>Replaces the explicit override; disposal reveals the installed default, if any.</summary>
    public static IDisposable RegisterGeometryOperations(IPortableGeometryOperations service) => s_geometryOperations.Register(service);

    /// <summary>Installs a default without overriding an explicit provider; performs no native work.</summary>
    public static void EnsureGeometryOperations(IPortableGeometryOperations service) => s_geometryOperations.EnsureDefault(service);
}

using System.Runtime.CompilerServices;

namespace ProGPU.Wpf.Interop;

/// <summary>The source facility that supplied the unmodified per-axis scale.</summary>
public enum PortablePrimaryDisplayRasterPolicy
{
    WindowsSystemDpi = 1,
    PrimaryMonitorContentScale = 2
}

/// <summary>
/// Optional owner-thread primary-display raster metrics. Failure means that an
/// actual source snapshot is unavailable, not a request to substitute 96 DPI,
/// window DPI, averaged axes, rounded values or a screen-resolution ratio.
/// </summary>
public interface IPortablePrimaryDisplayRasterScaleSource
{
    bool TryGetPrimaryDisplayRasterScale(out PortablePrimaryDisplayRasterScale scale);
}

/// <summary>
/// One immutable primary-display policy observation. SourceIdentity is a stable
/// managed reference owned by the source, never a boxed native monitor/window
/// handle. The source retains required native lifetime separately and changes
/// identity/revision when its selected policy generation changes.
/// </summary>
/// <remarks>
/// Windows system-DPI policy and another platform's monitor content-scale policy
/// are explicitly distinct. A successful provider observation is not evidence of
/// Windows parity on another platform. Float bits are transported unchanged;
/// consumers do not recompute DPI/96. Default and unknown policies are invalid.
/// Equality uses reference identity and exact float bits without invoking source
/// object equality/hash callbacks. Availability and creating-thread affinity are
/// still the provider's responsibility; IsValid checks only the value contract.
/// </remarks>
public readonly record struct PortablePrimaryDisplayRasterScale(
    float ScaleX,
    float ScaleY,
    PortablePrimaryDisplayRasterPolicy Policy,
    object SourceIdentity,
    ulong Revision)
{
    public bool IsValid =>
        float.IsFinite(ScaleX) && ScaleX > 0 &&
        float.IsFinite(ScaleY) && ScaleY > 0 &&
        Policy is PortablePrimaryDisplayRasterPolicy.WindowsSystemDpi or
            PortablePrimaryDisplayRasterPolicy.PrimaryMonitorContentScale &&
        SourceIdentity is not null && Revision != 0;

    public bool Equals(PortablePrimaryDisplayRasterScale other) =>
        BitConverter.SingleToInt32Bits(ScaleX) == BitConverter.SingleToInt32Bits(other.ScaleX) &&
        BitConverter.SingleToInt32Bits(ScaleY) == BitConverter.SingleToInt32Bits(other.ScaleY) &&
        Policy == other.Policy && ReferenceEquals(SourceIdentity, other.SourceIdentity) &&
        Revision == other.Revision;

    public override int GetHashCode() => HashCode.Combine(
        BitConverter.SingleToInt32Bits(ScaleX), BitConverter.SingleToInt32Bits(ScaleY),
        Policy, SourceIdentity is null ? 0 : RuntimeHelpers.GetHashCode(SourceIdentity), Revision);
}

namespace ProGPU.Wpf.Interop;

/// <summary>
/// Describes logical desktop geometry reported by a portable windowing backend.
/// All coordinates and dimensions are expressed in device-independent units.
/// </summary>
public readonly record struct PortableDisplayMetrics(
    PortableRect PrimaryScreen,
    PortableRect PrimaryWorkArea,
    PortableRect VirtualScreen);

/// <summary>
/// Supplies desktop geometry without coupling retained UI code to a specific
/// windowing backend or native monitor API.
/// </summary>
public interface IPortableDisplayMetricsSource
{
    PortableWpfServiceKey ServiceKey { get; }

    bool TryGetDisplayMetrics(out PortableDisplayMetrics metrics);

    event EventHandler? DisplayMetricsChanged;
}

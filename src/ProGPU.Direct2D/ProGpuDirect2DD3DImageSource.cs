using ProGPU.Wpf.Interop;

namespace ProGPU.Direct2D;

/// <summary>
/// Adapts a synchronized Direct2D surface to LibreWPF canonical D3DImage
/// replay without exposing a COM pointer or copying pixels.
/// </summary>
public sealed class ProGpuDirect2DD3DImageSource :
    IPortableD3DImageSource,
    IPortableInvalidationSource
{
    private readonly ProGpuDirect2DSurface _surface;

    public ProGpuDirect2DD3DImageSource(
        ProGpuDirect2DSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;
    }

    public ProGpuDirect2DSurface Surface => _surface;

    public bool TryGetPortableD3DImageFrame(
        out PortableD3DImageFrame frame)
    {
        ProGpuDirect2DSurfaceDescriptor descriptor =
            _surface.Descriptor;
        ulong contentVersion = _surface.ContentVersion;
        if (contentVersion == 0U)
        {
            frame = default;
            return false;
        }

        frame = new PortableD3DImageFrame(
            checked((int)descriptor.Width),
            checked((int)descriptor.Height),
            contentVersion,
            _surface);
        return true;
    }

    public bool TrySubscribeInvalidated(
        EventHandler handler,
        out IDisposable subscription)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _surface.TextureChanged += handler;
        subscription = new PortableInvalidationSubscription(
            () => _surface.TextureChanged -= handler);
        return true;
    }
}

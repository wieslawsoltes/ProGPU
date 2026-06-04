using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.WebGPU;

namespace System.Drawing;

internal static class GpuProvider
{
    private static WgpuContext? _context;
    private static Compositor? _compositor;

    public static WgpuContext Context
    {
        get
        {
            if (_context != null) return _context;
            if (WgpuContext.ActiveContexts.Count > 0)
            {
                return WgpuContext.ActiveContexts[0];
            }
            _context = new WgpuContext();
            _context.Initialize(null);
            return _context;
        }
    }

    public static Compositor Compositor
    {
        get
        {
            if (_compositor != null) return _compositor;
            _compositor = new Compositor(Context, TextureFormat.Rgba8Unorm);
            return _compositor;
        }
    }
}

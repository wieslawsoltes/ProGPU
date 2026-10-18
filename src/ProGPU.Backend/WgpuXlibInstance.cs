using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>The optional dependency extension keeps the standard WebGPU ABI intact.</summary>
internal static unsafe class WgpuXlibInstance
{
    internal const string EntryPoint = "progpuWgpuCreateInstanceXlibV1";

    internal static bool IncludesGl(uint backends) => backends == 0 || (backends & (1u << 1)) != 0;

    internal static Instance* Create(InstanceDescriptor* descriptor, nint display, int screen, nint entryPoint)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        if (display == 0) throw new ArgumentException("A live source Xlib display is required.", nameof(display));
        // The unchanged native WebGPU Xlib surface descriptor carries screen zero.
        if (screen != 0) throw new NotSupportedException("The native Xlib surface ABI supports screen zero only.");
        if (entryPoint == 0)
            throw new NotSupportedException("The matching ProGPU Linux WebGPU runtime is required for source Xlib GL contexts.");
        var create = (delegate* unmanaged[Cdecl]<InstanceDescriptor*, nint, int, Instance*>)entryPoint;
        Instance* instance = create(descriptor, display, screen);
        if (instance == null) throw new InvalidOperationException("Could not create the owned source Xlib GL context.");
        return instance;
    }
}

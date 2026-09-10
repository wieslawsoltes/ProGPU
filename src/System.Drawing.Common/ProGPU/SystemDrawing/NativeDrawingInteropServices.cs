using System.Drawing;

namespace ProGPU.SystemDrawing;

/// <summary>
/// Imports the font selected into a native device context through an explicit
/// local-OS adapter.
/// </summary>
public interface INativeFontInteropService
{
    Font ImportFromDeviceContext(IntPtr deviceContext);
}

/// <summary>
/// Registers the typed native-font capability used by canonical
/// <see cref="Font"/> entry points.
/// </summary>
public static class NativeFontInteropServices
{
    private static INativeFontInteropService? s_current;

    public static bool IsRegistered => Volatile.Read(ref s_current) is not null;

    public static IDisposable Register(INativeFontInteropService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Interlocked.CompareExchange(ref s_current, service, null) is not null)
        {
            throw new InvalidOperationException("A native font-interoperability service is already registered.");
        }

        return new Registration(service);
    }

    internal static Font ImportFromDeviceContext(IntPtr deviceContext)
    {
        if (deviceContext == IntPtr.Zero)
        {
            throw new ArgumentException("A nonzero device-context handle is required.", nameof(deviceContext));
        }

        return GetCurrent().ImportFromDeviceContext(deviceContext)
            ?? throw new InvalidOperationException("The native font adapter returned no font.");
    }

    private static INativeFontInteropService GetCurrent()
        => Volatile.Read(ref s_current)
            ?? throw new PlatformNotSupportedException(
                "Native font import requires a registered typed local-OS adapter.");

    private sealed class Registration(INativeFontInteropService service) : IDisposable
    {
        private INativeFontInteropService? _service = service;

        public void Dispose()
        {
            INativeFontInteropService? registered = Interlocked.Exchange(ref _service, null);
            if (registered is not null)
            {
                Interlocked.CompareExchange(ref s_current, null, registered);
            }
        }
    }
}

/// <summary>
/// Imports native device/window drawing targets and creates Windows halftone
/// palettes through an explicit local-OS adapter.
/// </summary>
/// <remarks>
/// Returned <see cref="Graphics"/> instances are owned by the caller. A host
/// can construct them with the public <c>FromProGpuDrawingContext</c> overloads
/// so disposal, flushing, bounds, transforms, and presentation stay typed.
/// A nonzero palette handle is native adapter state and must be released under
/// the adapter platform's ownership rules.
/// </remarks>
public interface INativeGraphicsInteropService
{
    Graphics CreateFromDeviceContext(IntPtr deviceContext, IntPtr device);

    Graphics CreateFromWindow(IntPtr window);

    IntPtr CreateHalftonePalette();
}

/// <summary>
/// Registers the typed native-graphics capability used by canonical
/// <see cref="Graphics"/> entry points.
/// </summary>
public static class NativeGraphicsInteropServices
{
    private static INativeGraphicsInteropService? s_current;

    public static bool IsRegistered => Volatile.Read(ref s_current) is not null;

    public static IDisposable Register(INativeGraphicsInteropService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Interlocked.CompareExchange(ref s_current, service, null) is not null)
        {
            throw new InvalidOperationException("A native graphics-interoperability service is already registered.");
        }

        return new Registration(service);
    }

    internal static Graphics CreateFromDeviceContext(IntPtr deviceContext, IntPtr device)
    {
        if (deviceContext == IntPtr.Zero)
        {
            throw new ArgumentException("A nonzero device-context handle is required.", nameof(deviceContext));
        }

        return GetCurrent().CreateFromDeviceContext(deviceContext, device)
            ?? throw new InvalidOperationException("The native graphics adapter returned no graphics instance.");
    }

    internal static Graphics CreateFromWindow(IntPtr window)
        => GetCurrent().CreateFromWindow(window)
            ?? throw new InvalidOperationException("The native graphics adapter returned no graphics instance.");

    internal static IntPtr CreateHalftonePalette()
        => GetCurrent().CreateHalftonePalette();

    private static INativeGraphicsInteropService GetCurrent()
        => Volatile.Read(ref s_current)
            ?? throw new PlatformNotSupportedException(
                "Native graphics interoperability requires a registered typed local-OS adapter.");

    private sealed class Registration(INativeGraphicsInteropService service) : IDisposable
    {
        private INativeGraphicsInteropService? _service = service;

        public void Dispose()
        {
            INativeGraphicsInteropService? registered = Interlocked.Exchange(ref _service, null);
            if (registered is not null)
            {
                Interlocked.CompareExchange(ref s_current, null, registered);
            }
        }
    }
}

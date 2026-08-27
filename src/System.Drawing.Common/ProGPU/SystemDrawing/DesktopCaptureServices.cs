using System.Drawing;

namespace ProGPU.SystemDrawing;

/// <summary>
/// Supplies an owned RGBA8 snapshot of a desktop rectangle to
/// <see cref="Graphics.CopyFromScreen(int, int, int, int, Size)"/>.
/// </summary>
/// <remarks>
/// The destination span is exactly <c>width * height * 4</c> bytes in row-major
/// RGBA order. Implementations must fill it before returning and must not retain
/// the span. Screen coordinates and dimensions are expressed in device pixels.
/// </remarks>
public interface IDesktopCaptureService
{
    void Capture(Rectangle sourceRectangle, Span<byte> destinationRgba);
}

/// <summary>
/// Registers the typed desktop-capture capability used by portable
/// <see cref="Graphics"/> instances, including bitmap-backed graphics.
/// </summary>
public static class DesktopCaptureServices
{
    private static IDesktopCaptureService? s_current;

    public static bool IsRegistered => Volatile.Read(ref s_current) is not null;

    public static IDisposable Register(IDesktopCaptureService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Interlocked.CompareExchange(ref s_current, service, null) is not null)
        {
            throw new InvalidOperationException("A desktop-capture service is already registered.");
        }

        return new Registration(service);
    }

    internal static IDesktopCaptureService Current
        => Volatile.Read(ref s_current)
            ?? throw new PlatformNotSupportedException(
                "Desktop capture requires a registered typed local-OS capture service.");

    private sealed class Registration(IDesktopCaptureService service) : IDisposable
    {
        private IDesktopCaptureService? _service = service;

        public void Dispose()
        {
            IDesktopCaptureService? registered = Interlocked.Exchange(ref _service, null);
            if (registered is not null)
            {
                Interlocked.CompareExchange(ref s_current, null, registered);
            }
        }
    }
}

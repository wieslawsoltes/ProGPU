using System.Drawing;

namespace ProGPU.SystemDrawing;

/// <summary>
/// Describes the device-dependent pixel envelope embedded in a WMF
/// <c>Bitmap16</c> object.
/// </summary>
public readonly record struct WmfBitmap16Info(
    short Type,
    int Width,
    int Height,
    int WidthBytes,
    byte Planes,
    byte BitsPerPixel);

/// <summary>
/// Receives one exact, top-down, straight-alpha RGBA8 snapshot from a WMF
/// <c>Bitmap16</c> decoder.
/// </summary>
/// <remarks>
/// The destination copies the supplied span synchronously. A decoder must
/// write exactly once during the decode call and must not retain this object.
/// </remarks>
public sealed class WmfBitmap16DecodeDestination
{
    private readonly int _expectedLength;
    private byte[]? _pixels;
    private bool _active = true;

    internal WmfBitmap16DecodeDestination(int width, int height)
    {
        _expectedLength = checked(width * height * 4);
    }

    public void SetRgba(ReadOnlySpan<byte> pixels)
    {
        if (!_active)
        {
            throw new InvalidOperationException("The WMF Bitmap16 decode destination is no longer active.");
        }
        if (_pixels is not null)
        {
            throw new InvalidOperationException("A WMF Bitmap16 decoder must write exactly one image.");
        }
        if (pixels.Length != _expectedLength)
        {
            throw new ArgumentException(
                "The RGBA buffer length does not match the Bitmap16 dimensions.",
                nameof(pixels));
        }

        _pixels = pixels.ToArray();
    }

    internal byte[] Complete()
    {
        _active = false;
        byte[] pixels = _pixels
            ?? throw new InvalidOperationException(
                "The WMF Bitmap16 decoder did not supply an image.");
        _pixels = null;
        return pixels;
    }

    internal void Cancel()
    {
        _active = false;
        _pixels = null;
    }
}

/// <summary>
/// Converts device-dependent WMF <c>Bitmap16</c> bits into owned portable
/// RGBA8 pixels.
/// </summary>
public interface IWmfBitmap16DecodeService
{
    void Decode(
        in WmfBitmap16Info bitmap,
        ReadOnlySpan<byte> bits,
        WmfBitmap16DecodeDestination destination);
}

/// <summary>
/// Registers the typed device-format capability used to play embedded WMF
/// <c>Bitmap16</c> sources.
/// </summary>
public static class WmfBitmap16DecodeServices
{
    private static IWmfBitmap16DecodeService? s_current;

    public static bool IsRegistered => Volatile.Read(ref s_current) is not null;

    public static IDisposable Register(IWmfBitmap16DecodeService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Interlocked.CompareExchange(ref s_current, service, null) is not null)
        {
            throw new InvalidOperationException("A WMF Bitmap16 decode service is already registered.");
        }

        return new Registration(service);
    }

    internal static Bitmap Decode(
        in WmfBitmap16Info bitmap,
        ReadOnlySpan<byte> bits)
    {
        IWmfBitmap16DecodeService service = Volatile.Read(ref s_current)
            ?? throw new PlatformNotSupportedException(
                "Embedded WMF Bitmap16 source pixels require a registered typed device-format adapter.");
        var destination = new WmfBitmap16DecodeDestination(bitmap.Width, bitmap.Height);
        try
        {
            service.Decode(bitmap, bits, destination);
            return Bitmap.CreateOwnedRgba(bitmap.Width, bitmap.Height, destination.Complete());
        }
        catch
        {
            destination.Cancel();
            throw;
        }
    }

    private sealed class Registration(IWmfBitmap16DecodeService service) : IDisposable
    {
        private IWmfBitmap16DecodeService? _service = service;

        public void Dispose()
        {
            IWmfBitmap16DecodeService? registered = Interlocked.Exchange(ref _service, null);
            if (registered is not null)
            {
                Interlocked.CompareExchange(ref s_current, null, registered);
            }
        }
    }
}

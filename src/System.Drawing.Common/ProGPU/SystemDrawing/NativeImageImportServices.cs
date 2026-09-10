using System.Drawing;

namespace ProGPU.SystemDrawing;

/// <summary>
/// Receives one exact, owned RGBA8 snapshot from a native image-import
/// provider.
/// </summary>
/// <remarks>
/// The destination copies the supplied span synchronously. Providers must
/// write exactly once during the import call and must not retain this object.
/// </remarks>
public sealed class NativeImageImportDestination
{
    private byte[]? _pixels;
    private int _width;
    private int _height;
    private bool _active = true;

    internal NativeImageImportDestination()
    {
    }

    public void SetRgba(int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (!_active)
        {
            throw new InvalidOperationException("The native image-import destination is no longer active.");
        }

        if (_pixels is not null)
        {
            throw new InvalidOperationException("A native image-import provider must write exactly one image.");
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        int expectedLength = checked(width * height * 4);
        if (pixels.Length != expectedLength)
        {
            throw new ArgumentException(
                "The RGBA buffer length does not match its dimensions.",
                nameof(pixels));
        }

        _width = width;
        _height = height;
        _pixels = pixels.ToArray();
    }

    internal (int Width, int Height, byte[] Pixels) Complete()
    {
        _active = false;
        byte[] pixels = _pixels
            ?? throw new InvalidOperationException(
                "The native image-import provider did not supply an image.");
        _pixels = null;
        return (_width, _height, pixels);
    }

    internal void Cancel()
    {
        _active = false;
        _pixels = null;
    }
}

/// <summary>
/// Imports Windows icon and named bitmap-resource handles into owned portable
/// pixels without exposing native handles to the ProGPU renderer.
/// </summary>
public interface INativeImageImportService
{
    void ImportIcon(IntPtr iconHandle, NativeImageImportDestination destination);

    void ImportBitmapResource(
        IntPtr moduleHandle,
        string resourceName,
        NativeImageImportDestination destination);
}

/// <summary>
/// Registers the typed local-OS image-import capability used by canonical
/// <see cref="Bitmap"/> and <see cref="Icon"/> entry points.
/// </summary>
public static class NativeImageImportServices
{
    private static INativeImageImportService? s_current;

    public static bool IsRegistered => Volatile.Read(ref s_current) is not null;

    public static IDisposable Register(INativeImageImportService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (Interlocked.CompareExchange(ref s_current, service, null) is not null)
        {
            throw new InvalidOperationException("A native image-import service is already registered.");
        }

        return new Registration(service);
    }

    internal static Bitmap ImportIcon(IntPtr iconHandle)
    {
        if (iconHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Invalid icon handle.", nameof(iconHandle));
        }

        INativeImageImportService service = GetCurrent();
        var destination = new NativeImageImportDestination();
        try
        {
            service.ImportIcon(iconHandle, destination);
            return CreateBitmap(destination);
        }
        catch
        {
            destination.Cancel();
            throw;
        }
    }

    internal static Bitmap ImportBitmapResource(IntPtr moduleHandle, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(resourceName);
        INativeImageImportService service = GetCurrent();
        var destination = new NativeImageImportDestination();
        try
        {
            service.ImportBitmapResource(moduleHandle, resourceName, destination);
            return CreateBitmap(destination);
        }
        catch
        {
            destination.Cancel();
            throw;
        }
    }

    private static INativeImageImportService GetCurrent()
        => Volatile.Read(ref s_current)
            ?? throw new PlatformNotSupportedException(
                "Native image import requires a registered typed local-OS adapter.");

    private static Bitmap CreateBitmap(NativeImageImportDestination destination)
    {
        (int width, int height, byte[] pixels) = destination.Complete();
        return Bitmap.CreateOwnedRgba(width, height, pixels);
    }

    private sealed class Registration(INativeImageImportService service) : IDisposable
    {
        private INativeImageImportService? _service = service;

        public void Dispose()
        {
            INativeImageImportService? registered = Interlocked.Exchange(ref _service, null);
            if (registered is not null)
            {
                Interlocked.CompareExchange(ref s_current, null, registered);
            }
        }
    }
}

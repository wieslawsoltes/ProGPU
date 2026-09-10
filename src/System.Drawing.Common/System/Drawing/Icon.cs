using System.Drawing.Imaging;
using ProGPU.SystemDrawing;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace System.Drawing;

[System.ComponentModel.TypeConverter(typeof(IconConverter))]
[Serializable]
public sealed partial class Icon : MarshalByRefObject, IDisposable, ICloneable, ISerializable
{
    private readonly Bitmap? _bitmap;

    private Icon(Bitmap bitmap)
    {
        _bitmap = bitmap;
    }

#pragma warning disable SYSLIB0050
    private Icon(SerializationInfo info, StreamingContext context)
    {
        ArgumentNullException.ThrowIfNull(info);
        byte[] data = (byte[])info.GetValue("IconData", typeof(byte[]))!;
        Size size = (Size)info.GetValue("IconSize", typeof(Size))!;
        using var stream = new MemoryStream(data, writable: false);
        _bitmap = size.Width > 0 && size.Height > 0
            ? LoadScaledBitmap(stream, size.Width, size.Height)
            : new Bitmap(stream);
    }
#pragma warning restore SYSLIB0050

    internal static Icon CreateOwned(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return new Icon(bitmap);
    }

    public Icon(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        using var stream = File.OpenRead(fileName);
        _bitmap = new Bitmap(stream);
    }

    public Icon(string fileName, Size size)
        : this(fileName, size.Width, size.Height)
    {
    }

    public Icon(string fileName, int width, int height)
        : this(LoadScaledBitmap(fileName, width, height))
    {
    }

    public Icon(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _bitmap = new Bitmap(stream);
    }

    public Icon(Stream stream, Size size)
        : this(stream, size.Width, size.Height)
    {
    }

    public Icon(Stream stream, int width, int height)
        : this(LoadScaledBitmap(stream, width, height))
    {
    }

    public Icon(Icon original, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        _bitmap = original._bitmap is null
            ? new Bitmap(width, height)
            : new Bitmap(original._bitmap, width, height);
    }

    public Icon(Icon original, Size size)
        : this(original, size.Width, size.Height)
    {
    }

    public Icon(Type type, string resource)
        : this(LoadResourceBitmap(type, resource))
    {
    }

    private Icon(IntPtr handle)
    {
        _bitmap = NativeImageImportServices.ImportIcon(handle);
    }

    public int Width => _bitmap?.Width ?? 0;

    public int Height => _bitmap?.Height ?? 0;

    public Size Size => new(Width, Height);

    public IntPtr Handle => throw new PlatformNotSupportedException(
        "HICON export requires the explicit Windows GDI image adapter.");

    public Bitmap ToBitmap()
    {
        return _bitmap is null ? new Bitmap(1, 1) : new Bitmap(_bitmap);
    }

    public object Clone() => new Icon(this, Width, Height);

#pragma warning disable SYSLIB0050
    void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
    {
        ArgumentNullException.ThrowIfNull(info);
        using var stream = new MemoryStream();
        Save(stream);
        info.AddValue("IconData", stream.ToArray(), typeof(byte[]));
        info.AddValue("IconSize", Size, typeof(Size));
    }
#pragma warning restore SYSLIB0050

    public void Save(Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(outputStream);

        if (_bitmap is null)
            throw new InvalidOperationException("The icon does not contain an image.");

        int width = _bitmap.Width;
        int height = _bitmap.Height;
        if ((uint)(width - 1) >= 256 || (uint)(height - 1) >= 256)
        {
            throw new InvalidOperationException(
                "ICO images must be between 1 and 256 pixels in each dimension.");
        }

        using var imageStream = new MemoryStream();
        _bitmap.Save(imageStream, ImageFormat.Png);
        if (imageStream.Length > uint.MaxValue)
            throw new InvalidOperationException("The encoded icon image is too large.");

        const uint imageOffset = 6 + 16;
        using (var writer = new BinaryWriter(outputStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0); // Reserved.
            writer.Write((ushort)1); // Icon image.
            writer.Write((ushort)1); // One directory entry.
            writer.Write((byte)(width == 256 ? 0 : width));
            writer.Write((byte)(height == 256 ? 0 : height));
            writer.Write((byte)0); // No palette.
            writer.Write((byte)0); // Reserved.
            writer.Write((ushort)1); // Color planes.
            writer.Write((ushort)32); // Bits per pixel.
            writer.Write((uint)imageStream.Length);
            writer.Write(imageOffset);
            writer.Flush();
        }

        if (!imageStream.TryGetBuffer(out ArraySegment<byte> imageData))
            throw new InvalidOperationException("Unable to access the encoded icon image.");

        outputStream.Write(imageData.AsSpan(0, checked((int)imageStream.Length)));
    }

    public static Icon FromHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            throw new ArgumentException("Invalid icon handle.", nameof(handle));

        return new Icon(handle);
    }

    private static Bitmap LoadScaledBitmap(string fileName, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        using var source = new Bitmap(fileName);
        return Scale(source, width, height);
    }

    private static Bitmap LoadScaledBitmap(Stream stream, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var source = new Bitmap(stream);
        return Scale(source, width, height);
    }

    private static Bitmap Scale(Bitmap source, int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        return new Bitmap(source, width, height);
    }

    private static Bitmap LoadResourceBitmap(Type type, string resource)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        Assembly assembly = type.Assembly;
        string qualifiedName = $"{type.Namespace}.{resource}";
        string? resourceName = assembly.GetManifestResourceNames().FirstOrDefault(
            name => string.Equals(name, qualifiedName, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith('.' + resource, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith('.' + resource + ".ico", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            throw new ArgumentException($"Icon resource '{resource}' was not found for '{type.FullName}'.", nameof(resource));
        }

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new ArgumentException($"Icon resource '{resourceName}' could not be opened.", nameof(resource));
        return new Bitmap(stream);
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
    }
}

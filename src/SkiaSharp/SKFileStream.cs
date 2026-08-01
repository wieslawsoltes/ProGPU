namespace SkiaSharp;

public class SKFileStream : SKStreamAsset
{
    public SKFileStream(string path)
        : this(ReadFile(path))
    {
    }

    private SKFileStream((byte[] Data, bool IsValid) file)
        : base(file.Data)
    {
        IsValid = file.IsValid;
    }

    public bool IsValid { get; }

    public static bool IsPathSupported(string path) => true;

    public static SKStreamAsset OpenStream(string path)
    {
        var stream = new SKFileStream(path);
        if (stream.IsValid)
        {
            return stream;
        }

        stream.Dispose();
        return null!;
    }

    private static (byte[] Data, bool IsValid) ReadFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return (Array.Empty<byte>(), false);
        }

        try
        {
            return (File.ReadAllBytes(path), true);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (Array.Empty<byte>(), false);
        }
    }

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}

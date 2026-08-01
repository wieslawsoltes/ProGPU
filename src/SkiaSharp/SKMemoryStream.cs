namespace SkiaSharp;

public abstract class SKStreamMemory : SKStreamAsset
{
    internal SKStreamMemory(byte[] data)
        : base(data)
    {
    }
}

public class SKMemoryStream : SKStreamMemory
{
    public SKMemoryStream()
        : base(Array.Empty<byte>())
    {
    }

    public SKMemoryStream(ulong length)
        : base(new byte[checked((int)length)])
    {
    }

    public SKMemoryStream(SKData data)
        : base(GetDataCopy(data))
    {
    }

    public SKMemoryStream(byte[] data)
        : this()
    {
        SetMemory(data);
    }

    public void SetMemory(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        SetData(data.ToArray());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }

    private static byte[] GetDataCopy(SKData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.ToArray();
    }
}

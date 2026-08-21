using System;

namespace System.Drawing.Imaging;

/// <summary>
/// Describes a native Windows metafile. Portable decoding requires a platform adapter.
/// </summary>
public sealed class Metafile : Image
{
    public Metafile(IntPtr hmetafile, WmfPlaceableFileHeader wmfHeader, bool deleteWmf) =>
        throw CreatePlatformException();

    public Metafile(IntPtr henhmetafile, bool deleteEmf) =>
        throw CreatePlatformException();

    public override int Width => 0;
    public override int Height => 0;
    public override object Clone() => throw CreatePlatformException();
    public override void Dispose() { }

    private static PlatformNotSupportedException CreatePlatformException() => new(
        "WMF/EMF handle import requires the explicit Windows GDI metafile adapter.");
}

public sealed class WmfPlaceableFileHeader
{
    public int Key { get; set; }
    public short Hmf { get; set; }
    public short BboxLeft { get; set; }
    public short BboxTop { get; set; }
    public short BboxRight { get; set; }
    public short BboxBottom { get; set; }
    public short Inch { get; set; }
    public int Reserved { get; set; }
    public short Checksum { get; set; }
}

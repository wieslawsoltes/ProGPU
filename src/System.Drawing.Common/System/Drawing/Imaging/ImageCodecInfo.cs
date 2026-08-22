namespace System.Drawing.Imaging;

public sealed class ImageCodecInfo
{
    private static readonly ImageCodecInfo[] s_decoders =
    [
        CreateCodec("557cf400-1a04-11d3-9a73-0000f81ef32e", ImageFormat.Bmp, "BMP", "*.BMP;*.DIB;*.RLE", "image/bmp", ImageCodecFlags.Decoder, [[0x42, 0x4d]], [[0xff, 0xff]]),
        CreateCodec("557cf401-1a04-11d3-9a73-0000f81ef32e", ImageFormat.Jpeg, "JPEG", "*.JPG;*.JPEG;*.JPE;*.JFIF", "image/jpeg", ImageCodecFlags.Decoder, [[0xff, 0xd8, 0xff]], [[0xff, 0xff, 0xff]]),
        CreateCodec("557cf402-1a04-11d3-9a73-0000f81ef32e", ImageFormat.Gif, "GIF", "*.GIF", "image/gif", ImageCodecFlags.Decoder, [[0x47, 0x49, 0x46, 0x38]], [[0xff, 0xff, 0xff, 0xff]]),
        CreateCodec("557cf406-1a04-11d3-9a73-0000f81ef32e", ImageFormat.Png, "PNG", "*.PNG", "image/png", ImageCodecFlags.Decoder, [[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]], [[0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]]),
        CreateCodec("557cf407-1a04-11d3-9a73-0000f81ef32e", ImageFormat.Icon, "ICO", "*.ICO", "image/x-icon", ImageCodecFlags.Decoder, [[0x00, 0x00, 0x01, 0x00]], [[0xff, 0xff, 0xff, 0xff]])
    ];

    private static readonly ImageCodecInfo[] s_encoders =
    [
        CreateCodec("557cf400-1a04-11d3-9a73-0000f81ef32e", ImageFormat.Bmp, "BMP", "*.BMP;*.DIB;*.RLE", "image/bmp", ImageCodecFlags.Encoder),
        CreateCodec("557cf401-1a04-11d3-9a73-0000f81ef32e", ImageFormat.Jpeg, "JPEG", "*.JPG;*.JPEG;*.JPE;*.JFIF", "image/jpeg", ImageCodecFlags.Encoder),
        CreateCodec("557cf406-1a04-11d3-9a73-0000f81ef32e", ImageFormat.Png, "PNG", "*.PNG", "image/png", ImageCodecFlags.Encoder)
    ];

    internal ImageCodecInfo()
    {
    }

    public Guid Clsid { get; set; }

    public Guid FormatID { get; set; }

    public string? CodecName { get; set; }

    public string? DllName { get; set; }

    public string? FormatDescription { get; set; }

    public string? FilenameExtension { get; set; }

    public string? MimeType { get; set; }

    public ImageCodecFlags Flags { get; set; }

    public int Version { get; set; }

#pragma warning disable CS3021
    [CLSCompliant(false)]
    public byte[][]? SignaturePatterns { get; set; }

    [CLSCompliant(false)]
    public byte[][]? SignatureMasks { get; set; }
#pragma warning restore CS3021

    public static ImageCodecInfo[] GetImageDecoders() => CloneCodecs(s_decoders);

    public static ImageCodecInfo[] GetImageEncoders() => CloneCodecs(s_encoders);

    internal static ImageCodecInfo? FindEncoder(Guid clsid)
    {
        foreach (ImageCodecInfo encoder in s_encoders)
        {
            if (encoder.Clsid == clsid)
            {
                return CloneCodec(encoder);
            }
        }

        return null;
    }

    private static ImageCodecInfo CreateCodec(
        string clsid,
        ImageFormat format,
        string description,
        string extension,
        string mimeType,
        ImageCodecFlags direction,
        byte[][]? signaturePatterns = null,
        byte[][]? signatureMasks = null) =>
        new()
        {
            Clsid = new Guid(clsid),
            FormatID = format.Guid,
            CodecName = $"ProGPU {description} Codec",
            FormatDescription = description,
            FilenameExtension = extension,
            MimeType = mimeType,
            Flags = direction | ImageCodecFlags.SupportBitmap | ImageCodecFlags.Builtin,
            Version = 1,
            SignaturePatterns = signaturePatterns,
            SignatureMasks = signatureMasks
        };

    private static ImageCodecInfo[] CloneCodecs(ImageCodecInfo[] source)
    {
        var clone = new ImageCodecInfo[source.Length];
        for (int index = 0; index < source.Length; index++)
        {
            clone[index] = CloneCodec(source[index]);
        }

        return clone;
    }

    private static ImageCodecInfo CloneCodec(ImageCodecInfo codec) =>
        new()
        {
            Clsid = codec.Clsid,
            FormatID = codec.FormatID,
            CodecName = codec.CodecName,
            DllName = codec.DllName,
            FormatDescription = codec.FormatDescription,
            FilenameExtension = codec.FilenameExtension,
            MimeType = codec.MimeType,
            Flags = codec.Flags,
            Version = codec.Version,
            SignaturePatterns = CloneSignatures(codec.SignaturePatterns),
            SignatureMasks = CloneSignatures(codec.SignatureMasks)
        };

    private static byte[][]? CloneSignatures(byte[][]? signatures)
    {
        if (signatures is null)
        {
            return null;
        }

        var clone = new byte[signatures.Length][];
        for (int index = 0; index < signatures.Length; index++)
        {
            clone[index] = (byte[])signatures[index].Clone();
        }

        return clone;
    }
}

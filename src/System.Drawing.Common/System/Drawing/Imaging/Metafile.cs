namespace System.Drawing.Imaging;

/// <summary>
/// Defines a portable, source-owned WMF, EMF, or EMF+ document.
/// </summary>
[Serializable]
public sealed class Metafile : Image
{
    private readonly MetafileDocument _document;
    private bool _disposed;

    public Metafile(string filename)
        : this(MetafileParser.ParseFile(filename))
    {
    }

    public Metafile(Stream stream)
        : this(MetafileParser.ParseStream(stream))
    {
    }

    private Metafile(MetafileDocument document)
    {
        _document = document;
        RawFormat = document.RawFormat;
    }

    public Metafile(IntPtr hmetafile, WmfPlaceableFileHeader wmfHeader, bool deleteWmf) =>
        throw CreateHandleImportException();

    public Metafile(IntPtr henhmetafile, bool deleteEmf) =>
        throw CreateHandleImportException();

    public Metafile(IntPtr hmetafile, WmfPlaceableFileHeader wmfHeader)
        : this(hmetafile, wmfHeader, false)
    {
    }

    public Metafile(IntPtr referenceHdc, Rectangle frameRect) => throw CreateRecordingException();
    public Metafile(IntPtr referenceHdc, EmfType emfType) => throw CreateRecordingException();
    public Metafile(IntPtr referenceHdc, RectangleF frameRect) => throw CreateRecordingException();
    public Metafile(IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit) => throw CreateRecordingException();
    public Metafile(IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type) => throw CreateRecordingException();
    public Metafile(IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type, string? description) => throw CreateRecordingException();
    public Metafile(IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit) => throw CreateRecordingException();
    public Metafile(IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type) => throw CreateRecordingException();
    public Metafile(IntPtr referenceHdc, EmfType emfType, string? description) => throw CreateRecordingException();
    public Metafile(IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type, string? desc) => throw CreateRecordingException();

    public Metafile(string fileName, IntPtr referenceHdc) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, EmfType type) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, EmfType type, string? description) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, string? desc) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type, string? description) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, string? description) => throw CreateRecordingException();
    public Metafile(string fileName, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type, string? description) => throw CreateRecordingException();

    public Metafile(Stream stream, IntPtr referenceHdc) => throw CreateRecordingException();
    public Metafile(Stream stream, IntPtr referenceHdc, EmfType type) => throw CreateRecordingException();
    public Metafile(Stream stream, IntPtr referenceHdc, EmfType type, string? description) => throw CreateRecordingException();
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect) => throw CreateRecordingException();
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit) => throw CreateRecordingException();
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type) => throw CreateRecordingException();
    public Metafile(Stream stream, IntPtr referenceHdc, RectangleF frameRect, MetafileFrameUnit frameUnit, EmfType type, string? description) => throw CreateRecordingException();
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect) => throw CreateRecordingException();
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit) => throw CreateRecordingException();
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type) => throw CreateRecordingException();
    public Metafile(Stream stream, IntPtr referenceHdc, Rectangle frameRect, MetafileFrameUnit frameUnit, EmfType type, string? description) => throw CreateRecordingException();

    public override int Width => _document.Header.Bounds.Width;
    public override int Height => _document.Header.Bounds.Height;

    public override object Clone()
    {
        ThrowIfDisposed();
        return new Metafile(_document);
    }

    public MetafileHeader GetMetafileHeader()
    {
        ThrowIfDisposed();
        return _document.Header.CloneHeader();
    }

    public static MetafileHeader GetMetafileHeader(string fileName) =>
        MetafileParser.ParseFile(fileName).Header.CloneHeader();

    public static MetafileHeader GetMetafileHeader(Stream stream) =>
        MetafileParser.ParseStream(stream).Header.CloneHeader();

    public static MetafileHeader GetMetafileHeader(IntPtr hmetafile, WmfPlaceableFileHeader wmfHeader) =>
        throw CreateHandleImportException();

    public static MetafileHeader GetMetafileHeader(IntPtr henhmetafile) =>
        throw CreateHandleImportException();

    public IntPtr GetHenhmetafile() =>
        throw new PlatformNotSupportedException(
            "HENHMETAFILE export requires the explicit Windows GDI metafile adapter.");

    public void PlayRecord(EmfPlusRecordType recordType, int flags, int dataSize, byte[] data)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(data);
        if (dataSize < 0 || dataSize > data.Length)
        {
            throw new ArgumentException("The record size exceeds the supplied data buffer.", nameof(dataSize));
        }

        throw new NotSupportedException(
            "Metafile record playback is enabled by Graphics.EnumerateMetafile in the next typed playback checkpoint.");
    }

    public override void Dispose() => _disposed = true;

    internal ReadOnlySpan<byte> Source => _document.Source;
    internal ReadOnlySpan<MetafileRecord> Records => _document.Records;
    internal void EnsureNotDisposed() => ThrowIfDisposed();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static PlatformNotSupportedException CreateHandleImportException() => new(
        "WMF/EMF handle import requires the explicit Windows GDI metafile adapter.");

    private static PlatformNotSupportedException CreateRecordingException() => new(
        "HDC-backed metafile recording requires the explicit Windows GDI metafile adapter.");
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

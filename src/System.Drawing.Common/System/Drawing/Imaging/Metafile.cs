using System.Runtime.Serialization;
using ProGPU.Scene;

namespace System.Drawing.Imaging;

/// <summary>
/// Defines a portable, source-owned WMF, EMF, or EMF+ document.
/// </summary>
[Serializable]
public sealed class Metafile : Image
{
    private MetafileDocument? _document;
    private readonly PortableMetafileRecordingSession? _recording;
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

    private Metafile(PortableMetafileRecordingSession recording)
    {
        _recording = recording;
        RawFormat = ImageFormat.Emf;
    }

#pragma warning disable SYSLIB0050
    private Metafile(SerializationInfo info, StreamingContext context)
        : this(ParseSerialized(info))
    {
    }
#pragma warning restore SYSLIB0050

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

    public override int Width => GetBounds().Width;
    public override int Height => GetBounds().Height;

    public override object Clone()
    {
        ThrowIfDisposed();
        return new Metafile(GetCompletedDocument());
    }

    public MetafileHeader GetMetafileHeader()
    {
        ThrowIfDisposed();
        return GetCompletedDocument().Header.CloneHeader();
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

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recording?.Abort();
    }

    internal ReadOnlySpan<byte> Source => GetCompletedDocument().Source;
    internal ReadOnlySpan<MetafileRecord> Records => GetCompletedDocument().Records;
    internal void EnsureNotDisposed() => ThrowIfDisposed();
    internal static Metafile CreatePortable(Stream target, Rectangle bounds) =>
        new(new PortableMetafileRecordingSession(target, bounds));

    internal PortableMetafileRecordingSession AcquirePortableRecording()
    {
        ThrowIfDisposed();
        if (_recording is null)
        {
            throw new NotSupportedException("Only a portable recording metafile can create a Graphics recorder.");
        }

        _recording.Acquire();
        return _recording;
    }

    internal RectangleF GetRecordingBounds()
    {
        ThrowIfDisposed();
        Rectangle bounds = _recording?.Bounds
            ?? throw new NotSupportedException("The metafile is not a portable recording target.");
        return bounds;
    }

    internal void CompletePortableRecording(PortableMetafileRecordingSession recording, DrawingContext drawingContext)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(recording, _recording))
        {
            throw new InvalidOperationException("The metafile recorder does not belong to this image.");
        }

        _document = recording.Complete(drawingContext);
    }

    internal override byte[] GetSerializedData()
    {
        ThrowIfDisposed();
        return GetCompletedDocument().Source.ToArray();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private Rectangle GetBounds()
    {
        ThrowIfDisposed();
        return _document?.Header.Bounds ?? _recording!.Bounds;
    }

    private MetafileDocument GetCompletedDocument()
    {
        ThrowIfDisposed();
        return _document ?? throw new InvalidOperationException(
            "The portable metafile is not complete until its Graphics recording session is disposed.");
    }

    private static PlatformNotSupportedException CreateHandleImportException() => new(
        "WMF/EMF handle import requires the explicit Windows GDI metafile adapter.");

    private static PlatformNotSupportedException CreateRecordingException() => new(
        "HDC-backed metafile recording requires the explicit Windows GDI metafile adapter.");

#pragma warning disable SYSLIB0050
    private static MetafileDocument ParseSerialized(SerializationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        byte[] data = (byte[])info.GetValue("Data", typeof(byte[]))!;
        using var stream = new MemoryStream(data, writable: false);
        return MetafileParser.ParseStream(stream);
    }
#pragma warning restore SYSLIB0050
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

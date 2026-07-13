using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Scene;

namespace SkiaSharp;

public abstract class SKWStream : SKObject
{
    private sealed class WStreamAdapter : Stream
    {
        private readonly SKWStream _owner;

        public WStreamAdapter(SKWStream owner) => _owner = owner;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _owner.BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _owner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
            {
                throw new ArgumentException("Offset and count exceed the buffer length.");
            }

            if (!_owner.WriteCore(buffer.AsSpan(offset, count)))
            {
                throw new IOException("The Skia write stream rejected the data.");
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!_owner.WriteCore(buffer))
            {
                throw new IOException("The Skia write stream rejected the data.");
            }
        }
    }

    internal SKWStream()
        : base(SKObjectHandle.Create(), owns: true)
    {
    }

    internal virtual Stream? BaseStream => null;
    internal Stream ManagedStream => BaseStream ?? new WStreamAdapter(this);

    public virtual int BytesWritten
    {
        get
        {
            ThrowIfDisposed();
            if (this is SKAbstractManagedWStream managedStream)
            {
                return unchecked((int)managedStream.OnBytesWritten());
            }

            return BaseStream is { CanSeek: true } stream
                ? unchecked((int)stream.Position)
                : 0;
        }
    }

    public virtual bool Write(byte[] buffer, int size)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (size > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        return WriteCore(buffer.AsSpan(0, size));
    }

    public bool NewLine() => WriteCore("\n"u8);

    public virtual void Flush()
    {
        ThrowIfDisposed();
        if (this is SKAbstractManagedWStream managedStream)
        {
            managedStream.OnFlush();
            return;
        }

        BaseStream?.Flush();
    }

    public bool Write8(byte value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(byte)];
        bytes[0] = value;
        return WriteCore(bytes);
    }

    public bool Write16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return WriteCore(bytes);
    }

    public bool Write32(uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return WriteCore(bytes);
    }

    public bool WriteText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WriteCore(Encoding.UTF8.GetBytes(value));
    }

    public bool WriteDecimalAsTest(int value) =>
        WriteText(value.ToString(CultureInfo.InvariantCulture));

    public bool WriteBigDecimalAsText(long value, int digits)
    {
        var text = unchecked((ulong)value).ToString(CultureInfo.InvariantCulture);
        return WriteText(digits > text.Length ? text.PadLeft(digits, '0') : text);
    }

    public bool WriteHexAsText(uint value, int digits)
    {
        var text = value.ToString("X", CultureInfo.InvariantCulture);
        return WriteText(digits > text.Length ? text.PadLeft(digits, '0') : text);
    }

    public bool WriteScalarAsText(float value)
    {
        var text = float.IsNaN(value)
            ? "nan"
            : float.IsPositiveInfinity(value)
                ? "inf"
                : float.IsNegativeInfinity(value)
                    ? "-inf"
                    : value.ToString("R", CultureInfo.InvariantCulture);
        return WriteText(text);
    }

    public bool WriteBool(bool value) => Write8(value ? (byte)1 : (byte)0);

    public bool WriteScalar(float value) => Write32(BitConverter.SingleToUInt32Bits(value));

    public bool WritePackedUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[5];
        int length;
        if (value <= 0xfd)
        {
            bytes[0] = (byte)value;
            length = 1;
        }
        else if (value <= ushort.MaxValue)
        {
            bytes[0] = 0xfe;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[1..], (ushort)value);
            length = 3;
        }
        else
        {
            bytes[0] = 0xff;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[1..], value);
            length = 5;
        }

        return WriteCore(bytes[..length]);
    }

    public unsafe bool WriteStream(SKStream input, int length)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
        {
            return true;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(length, 81920));
        try
        {
            var remaining = length;
            var inputEnded = false;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, buffer.Length);
                var chunk = buffer.AsSpan(0, count);
                chunk.Clear();
                if (!inputEnded)
                {
                    var readTotal = 0;
                    fixed (byte* pointer = chunk)
                    {
                        while (readTotal < count)
                        {
                            var read = input.Read((IntPtr)(pointer + readTotal), count - readTotal);
                            if (read == 0)
                            {
                                inputEnded = true;
                                break;
                            }

                            readTotal += read;
                        }
                    }
                }

                if (!WriteCore(chunk))
                {
                    return false;
                }

                remaining -= count;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static int GetSizeOfPackedUInt32(uint value) =>
        value <= 0xfd ? 1 : value <= ushort.MaxValue ? 3 : 5;

    protected unsafe virtual bool WriteCore(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        if (this is SKAbstractManagedWStream managedStream)
        {
            fixed (byte* pointer = buffer)
            {
                return managedStream.OnWrite((IntPtr)pointer, (IntPtr)buffer.Length);
            }
        }

        if (BaseStream is not { } stream)
        {
            return false;
        }

        stream.Write(buffer);
        return true;
    }

    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}

public abstract class SKAbstractManagedWStream : SKWStream
{
    protected SKAbstractManagedWStream()
        : this(owns: true)
    {
    }

    protected SKAbstractManagedWStream(bool owns)
    {
    }

    protected internal abstract bool OnWrite(IntPtr buffer, IntPtr size);
    protected internal abstract void OnFlush();
    protected internal abstract IntPtr OnBytesWritten();
}

public class SKManagedWStream : SKAbstractManagedWStream
{
    private Stream? _stream;
    private readonly bool _disposeStream;

    public SKManagedWStream(Stream managedStream)
        : this(managedStream, disposeManagedStream: false)
    {
    }

    public SKManagedWStream(Stream managedStream, bool disposeManagedStream)
    {
        ArgumentNullException.ThrowIfNull(managedStream);
        _stream = managedStream;
        _disposeStream = disposeManagedStream;
    }

    internal override Stream? BaseStream => _stream;

    protected internal unsafe override bool OnWrite(IntPtr buffer, IntPtr size)
    {
        var stream = _stream ?? throw new ObjectDisposedException(nameof(SKManagedWStream));
        stream.Write(new ReadOnlySpan<byte>(buffer.ToPointer(), checked((int)size)));
        return true;
    }

    protected internal override void OnFlush() =>
        (_stream ?? throw new ObjectDisposedException(nameof(SKManagedWStream))).Flush();

    protected internal override IntPtr OnBytesWritten() =>
        (IntPtr)(_stream ?? throw new ObjectDisposedException(nameof(SKManagedWStream))).Position;

    protected override void Dispose(bool disposing)
    {
        if (disposing && _disposeStream)
        {
            _stream?.Dispose();
            _stream = null;
        }

        base.Dispose(disposing);
    }
}

public class SKFileWStream : SKWStream
{
    private readonly Stream _stream;

    public SKFileWStream(string path)
    {
        try
        {
            _stream = string.IsNullOrEmpty(path)
                ? Stream.Null
                : new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            IsValid = !ReferenceEquals(_stream, Stream.Null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            _stream = Stream.Null;
        }
    }

    internal override Stream BaseStream => _stream;
    public bool IsValid { get; }

    public static bool IsPathSupported(string path) => true;

    public static SKWStream OpenStream(string path)
    {
        var stream = new SKFileWStream(path);
        if (stream.IsValid)
        {
            return stream;
        }

        stream.Dispose();
        return null!;
    }

    protected override bool WriteCore(ReadOnlySpan<byte> buffer) =>
        IsValid && base.WriteCore(buffer);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }
}

public class SKDynamicMemoryWStream : SKWStream
{
    private readonly MemoryStream _stream = new();

    internal override Stream BaseStream => _stream;

    public SKData CopyToData() => new(_stream.ToArray());

    public SKStreamAsset DetachAsStream() => new SKStreamAssetImplementation(DetachBytes());

    public SKData DetachAsData() => new(DetachBytes());

    public void CopyTo(IntPtr data)
    {
        ThrowIfDisposed();
        var count = BytesWritten;
        if (count == 0)
        {
            return;
        }

        if (data == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(data));
        }

        Marshal.Copy(_stream.GetBuffer(), 0, data, count);
    }

    public void CopyTo(Span<byte> data)
    {
        ThrowIfDisposed();
        var count = BytesWritten;
        if (data.Length < count)
        {
            throw new Exception($"Not enough space to copy. Expected at least {count}, but received {data.Length}.");
        }

        _stream.GetBuffer().AsSpan(0, count).CopyTo(data);
    }

    public bool CopyTo(SKWStream dst)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ThrowIfDisposed();
        return dst.Write(_stream.GetBuffer(), BytesWritten);
    }

    public bool CopyTo(Stream dst)
    {
        ArgumentNullException.ThrowIfNull(dst);
        using var stream = new SKManagedWStream(dst);
        return CopyTo(stream);
    }

    private byte[] DetachBytes()
    {
        ThrowIfDisposed();
        var bytes = _stream.ToArray();
        _stream.SetLength(0);
        _stream.Position = 0;
        return bytes;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }
}

public static class SKSvgCanvas
{
    public static SKCanvas Create(SKRect bounds, SKWStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return CreateCore(bounds, stream.ManagedStream);
    }

    public static SKCanvas Create(SKRect bounds, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return CreateCore(bounds, stream);
    }

    private static SKCanvas CreateCore(SKRect bounds, Stream stream)
    {
        var width = Math.Max(1, (int)MathF.Ceiling(bounds.Width));
        var height = Math.Max(1, (int)MathF.Ceiling(bounds.Height));
        var context = new DrawingContext();
        var written = false;

        void Flush()
        {
            if (written)
            {
                return;
            }

            written = true;
            var page = SKOutputRasterizer.Capture(context, width, height);
            var svg = string.Create(
                CultureInfo.InvariantCulture,
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{bounds.Width}\" height=\"{bounds.Height}\" viewBox=\"0 0 {bounds.Width} {bounds.Height}\"><image width=\"100%\" height=\"100%\" href=\"data:image/png;base64,{Convert.ToBase64String(page.Png)}\"/></svg>");
            var bytes = Encoding.UTF8.GetBytes(svg);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        return new SKCanvas(context, width, height, SKContextHelper.GetContext(), Flush);
    }
}

public struct SKDocumentPdfMetadata : IEquatable<SKDocumentPdfMetadata>
{
    public const float DefaultRasterDpi = 72f;
    public const int DefaultEncodingQuality = 101;

    public static readonly SKDocumentPdfMetadata Default = new()
    {
        RasterDpi = DefaultRasterDpi,
        PdfA = false,
        EncodingQuality = DefaultEncodingQuality,
    };

    public string? Title { readonly get; set; }
    public string? Author { readonly get; set; }
    public string? Subject { readonly get; set; }
    public string? Keywords { readonly get; set; }
    public string? Creator { readonly get; set; }
    public string? Producer { readonly get; set; }
    public DateTime? Creation { readonly get; set; }
    public DateTime? Modified { readonly get; set; }
    public float RasterDpi { readonly get; set; }
    public bool PdfA { readonly get; set; }
    public int EncodingQuality { readonly get; set; }

    public SKDocumentPdfMetadata(float rasterDpi)
        : this(rasterDpi, DefaultEncodingQuality)
    {
    }

    public SKDocumentPdfMetadata(int encodingQuality)
        : this(DefaultRasterDpi, encodingQuality)
    {
    }

    public SKDocumentPdfMetadata(float rasterDpi, int encodingQuality)
    {
        Title = null;
        Author = null;
        Subject = null;
        Keywords = null;
        Creator = null;
        Producer = null;
        Creation = null;
        Modified = null;
        RasterDpi = rasterDpi;
        PdfA = false;
        EncodingQuality = encodingQuality;
    }

    public readonly bool Equals(SKDocumentPdfMetadata obj) =>
        Title == obj.Title &&
        Author == obj.Author &&
        Subject == obj.Subject &&
        Keywords == obj.Keywords &&
        Creator == obj.Creator &&
        Producer == obj.Producer &&
        Creation == obj.Creation &&
        Modified == obj.Modified &&
        RasterDpi == obj.RasterDpi &&
        PdfA == obj.PdfA &&
        EncodingQuality == obj.EncodingQuality;

    public override readonly bool Equals(object? obj) =>
        obj is SKDocumentPdfMetadata other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Title);
        hash.Add(Author);
        hash.Add(Subject);
        hash.Add(Keywords);
        hash.Add(Creator);
        hash.Add(Producer);
        hash.Add(Creation);
        hash.Add(Modified);
        hash.Add(RasterDpi);
        hash.Add(PdfA);
        hash.Add(EncodingQuality);
        return hash.ToHashCode();
    }

    public static bool operator ==(SKDocumentPdfMetadata left, SKDocumentPdfMetadata right) =>
        left.Equals(right);

    public static bool operator !=(SKDocumentPdfMetadata left, SKDocumentPdfMetadata right) =>
        !left.Equals(right);
}

public struct SKDocumentXpsOptions : IEquatable<SKDocumentXpsOptions>
{
    public float Dpi { readonly get; set; }
    public bool AllowNoPngs { readonly get; set; }

    public readonly bool Equals(SKDocumentXpsOptions obj) =>
        Dpi == obj.Dpi && AllowNoPngs == obj.AllowNoPngs;

    public override readonly bool Equals(object? obj) =>
        obj is SKDocumentXpsOptions other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Dpi, AllowNoPngs);

    public static bool operator ==(SKDocumentXpsOptions left, SKDocumentXpsOptions right) =>
        left.Equals(right);

    public static bool operator !=(SKDocumentXpsOptions left, SKDocumentXpsOptions right) =>
        !left.Equals(right);
}

public class SKDocument : SKObject
{
    private enum DocumentKind
    {
        Pdf,
        Xps,
    }

    private sealed class Page
    {
        public required float Width { get; init; }
        public required float Height { get; init; }
        public required DrawingContext Context { get; init; }
        public SKOutputRasterizer.PageData? Captured { get; set; }
    }

    public const float DefaultRasterDpi = 72f;

    private readonly Stream _stream;
    private readonly DocumentKind _kind;
    private readonly SKDocumentPdfMetadata _pdfMetadata;
    private readonly SKDocumentXpsOptions _xpsOptions;
    private readonly List<Page> _pages = new();
    private IDisposable? _ownedStream;
    private bool _closed;
    private bool _aborted;

    private SKDocument(
        Stream stream,
        DocumentKind kind,
        SKDocumentPdfMetadata pdfMetadata = default,
        SKDocumentXpsOptions xpsOptions = default)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _stream = stream;
        _kind = kind;
        _pdfMetadata = pdfMetadata;
        _xpsOptions = xpsOptions;
    }

    public static SKDocument CreatePdf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stream = SKFileWStream.OpenStream(path);
        var document = CreatePdf(stream);
        document._ownedStream = stream;
        return document;
    }

    public static SKDocument CreatePdf(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SKDocument(stream, DocumentKind.Pdf, SKDocumentPdfMetadata.Default);
    }

    public static SKDocument CreatePdf(SKWStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SKDocument(stream.ManagedStream, DocumentKind.Pdf, SKDocumentPdfMetadata.Default);
    }

    public static SKDocument CreatePdf(string path, float dpi)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stream = SKFileWStream.OpenStream(path);
        var document = CreatePdf(stream, dpi);
        document._ownedStream = stream;
        return document;
    }

    public static SKDocument CreatePdf(Stream stream, float dpi) =>
        CreatePdf(stream, new SKDocumentPdfMetadata(dpi));

    public static SKDocument CreatePdf(SKWStream stream, float dpi) =>
        CreatePdf(stream, new SKDocumentPdfMetadata(dpi));

    public static SKDocument CreatePdf(string path, SKDocumentPdfMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stream = SKFileWStream.OpenStream(path);
        var document = CreatePdf(stream, metadata);
        document._ownedStream = stream;
        return document;
    }

    public static SKDocument CreatePdf(Stream stream, SKDocumentPdfMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SKDocument(stream, DocumentKind.Pdf, metadata);
    }

    public static SKDocument CreatePdf(SKWStream stream, SKDocumentPdfMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SKDocument(stream.ManagedStream, DocumentKind.Pdf, metadata);
    }

    public static SKDocument CreateXps(string path) => CreateXps(path, DefaultRasterDpi);

    public static SKDocument CreateXps(Stream stream) => CreateXps(stream, DefaultRasterDpi);

    public static SKDocument CreateXps(SKWStream stream) => CreateXps(stream, DefaultRasterDpi);

    public static SKDocument CreateXps(string path, float dpi)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stream = SKFileWStream.OpenStream(path);
        var document = CreateXps(stream, dpi);
        document._ownedStream = stream;
        return document;
    }

    public static SKDocument CreateXps(Stream stream, float dpi)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SKDocument(
            stream,
            DocumentKind.Xps,
            xpsOptions: new SKDocumentXpsOptions { Dpi = dpi });
    }

    public static SKDocument CreateXps(SKWStream stream, float dpi)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SKDocument(
            stream.ManagedStream,
            DocumentKind.Xps,
            xpsOptions: new SKDocumentXpsOptions { Dpi = dpi });
    }

    public static SKDocument CreateXps(string path, SKDocumentXpsOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        var stream = SKFileWStream.OpenStream(path);
        var document = CreateXps(stream, options);
        document._ownedStream = stream;
        return document;
    }

    public static SKDocument CreateXps(Stream stream, SKDocumentXpsOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SKDocument(stream, DocumentKind.Xps, xpsOptions: options);
    }

    public static SKDocument CreateXps(SKWStream stream, SKDocumentXpsOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SKDocument(stream.ManagedStream, DocumentKind.Xps, xpsOptions: options);
    }

    public SKCanvas BeginPage(float width, float height)
    {
        return BeginPageCore(width, height, content: null);
    }

    public SKCanvas BeginPage(float width, float height, SKRect content)
    {
        return BeginPageCore(width, height, content);
    }

    private SKCanvas BeginPageCore(float width, float height, SKRect? content)
    {
        ObjectDisposedException.ThrowIf(_closed || IsDisposed, this);
        if (!(width > 0f) || !(height > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Page dimensions must be positive.");
        }

        var page = new Page
        {
            Width = width,
            Height = height,
            Context = new DrawingContext(),
        };
        _pages.Add(page);
        var canvas = new SKCanvas(
            page.Context,
            width,
            height,
            SKContextHelper.GetContext(),
            () => Capture(page));
        if (content is { } contentRect)
        {
            canvas.ClipRect(contentRect);
        }

        return canvas;
    }

    public void EndPage()
    {
        if (_pages.Count > 0)
        {
            Capture(_pages[^1]);
        }
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        if (!_aborted)
        {
            foreach (var page in _pages)
            {
                Capture(page);
            }

            if (_pages.Count > 0 && _kind == DocumentKind.Pdf)
            {
                WritePdf();
            }
            else if (_pages.Count > 0)
            {
                WriteXps();
            }

            _stream.Flush();
        }

        _closed = true;
    }

    public void Abort()
    {
        if (_closed)
        {
            return;
        }

        _aborted = true;
        _closed = true;
        _pages.Clear();
    }

    private void Capture(Page page)
    {
        var dpi = _kind == DocumentKind.Pdf ? _pdfMetadata.RasterDpi : _xpsOptions.Dpi;
        if (!(dpi > 0f) || !float.IsFinite(dpi))
        {
            dpi = DefaultRasterDpi;
        }

        var scale = dpi / DefaultRasterDpi;
        page.Captured ??= SKOutputRasterizer.Capture(
            page.Context,
            Math.Max(1, (int)MathF.Ceiling(page.Width * scale)),
            Math.Max(1, (int)MathF.Ceiling(page.Height * scale)),
            scale);
    }

    private void WritePdf()
    {
        var pageCount = _pages.Count;
        var infoEntries = CreatePdfInfoEntries(_pdfMetadata);
        var hasInfo = infoEntries.Length > 0;
        var infoId = hasInfo ? 3 + pageCount * 3 : 0;
        var objectCount = 2 + pageCount * 3 + (hasInfo ? 1 : 0);
        var objects = new byte[objectCount + 1][];
        objects[1] = Ascii("<< /Type /Catalog /Pages 2 0 R >>");

        var kids = new StringBuilder();
        for (var i = 0; i < pageCount; i++)
        {
            kids.Append(3 + i * 3).Append(" 0 R ");
        }

        objects[2] = Ascii($"<< /Type /Pages /Count {pageCount} /Kids [{kids}] >>");
        for (var i = 0; i < pageCount; i++)
        {
            var page = _pages[i];
            var captured = page.Captured!;
            var pageId = 3 + i * 3;
            var imageId = pageId + 1;
            var contentId = pageId + 2;
            var imageData = CompressRgb(captured.Rgba);
            var imageHeader = Ascii(
                $"<< /Type /XObject /Subtype /Image /Width {captured.Width} /Height {captured.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {imageData.Length} >>\nstream\n");
            objects[imageId] = Combine(imageHeader, imageData, Ascii("\nendstream"));

            var width = page.Width.ToString("0.###", CultureInfo.InvariantCulture);
            var height = page.Height.ToString("0.###", CultureInfo.InvariantCulture);
            var commands = Ascii($"q {width} 0 0 -{height} 0 {height} cm /Im{i + 1} Do Q\n");
            objects[contentId] = Combine(
                Ascii($"<< /Length {commands.Length} >>\nstream\n"),
                commands,
                Ascii("endstream"));
            objects[pageId] = Ascii(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im{i + 1} {imageId} 0 R >> >> /Contents {contentId} 0 R >>");
        }

        if (hasInfo)
        {
            objects[infoId] = Ascii($"<< {infoEntries} >>");
        }

        using var document = new MemoryStream();
        document.Write(Ascii("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n"));
        var offsets = new long[objectCount + 1];
        for (var id = 1; id <= objectCount; id++)
        {
            offsets[id] = document.Position;
            document.Write(Ascii($"{id} 0 obj\n"));
            document.Write(objects[id]);
            document.Write(Ascii("\nendobj\n"));
        }

        var xref = document.Position;
        document.Write(Ascii($"xref\n0 {objectCount + 1}\n0000000000 65535 f \n"));
        for (var id = 1; id <= objectCount; id++)
        {
            document.Write(Ascii($"{offsets[id]:D10} 00000 n \n"));
        }

        var infoReference = hasInfo ? $" /Info {infoId} 0 R" : string.Empty;
        document.Write(Ascii($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R{infoReference} >>\nstartxref\n{xref}\n%%EOF\n"));
        document.Position = 0;
        document.CopyTo(_stream);
    }

    private static string CreatePdfInfoEntries(SKDocumentPdfMetadata metadata)
    {
        var entries = new StringBuilder();
        AppendPdfInfo(entries, "Title", metadata.Title);
        AppendPdfInfo(entries, "Author", metadata.Author);
        AppendPdfInfo(entries, "Subject", metadata.Subject);
        AppendPdfInfo(entries, "Keywords", metadata.Keywords);
        AppendPdfInfo(entries, "Creator", metadata.Creator);
        AppendPdfInfo(entries, "Producer", metadata.Producer);
        AppendPdfInfo(entries, "CreationDate", FormatPdfDate(metadata.Creation));
        AppendPdfInfo(entries, "ModDate", FormatPdfDate(metadata.Modified));
        return entries.ToString().TrimEnd();
    }

    private static void AppendPdfInfo(StringBuilder entries, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        entries.Append('/').Append(key).Append(" (").Append(EscapePdfString(value)).Append(") ");
    }

    private static string? FormatPdfDate(DateTime? value)
    {
        if (value is not { } date)
        {
            return null;
        }

        var offset = TimeZoneInfo.Local.GetUtcOffset(date);
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"D:{date:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'");
    }

    private static string EscapePdfString(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private void WriteXps()
    {
        using var package = new ZipArchive(_stream, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(package, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Override PartName=\"/FixedDocSeq.fdseq\" ContentType=\"application/vnd.ms-package.xps-fixeddocumentsequence+xml\"/><Override PartName=\"/Documents/1/FixedDoc.fdoc\" ContentType=\"application/vnd.ms-package.xps-fixeddocument+xml\"/></Types>");
        WriteEntry(package, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"R1\" Type=\"http://schemas.microsoft.com/xps/2005/06/fixedrepresentation\" Target=\"/FixedDocSeq.fdseq\"/></Relationships>");
        WriteEntry(package, "FixedDocSeq.fdseq",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><FixedDocumentSequence xmlns=\"http://schemas.microsoft.com/xps/2005/06\"><DocumentReference Source=\"/Documents/1/FixedDoc.fdoc\"/></FixedDocumentSequence>");

        var pages = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><FixedDocument xmlns=\"http://schemas.microsoft.com/xps/2005/06\">");
        for (var i = 0; i < _pages.Count; i++)
        {
            pages.Append($"<PageContent Source=\"/Documents/1/Pages/{i + 1}.fpage\"/>");
            var page = _pages[i];
            var width = page.Width.ToString("0.###", CultureInfo.InvariantCulture);
            var height = page.Height.ToString("0.###", CultureInfo.InvariantCulture);
            WriteEntry(package, $"Documents/1/Pages/{i + 1}.fpage",
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?><FixedPage xmlns=\"http://schemas.microsoft.com/xps/2005/06\" Width=\"{width}\" Height=\"{height}\"><Path Data=\"M 0,0 L {width},0 {width},{height} 0,{height} Z\"><Path.Fill><ImageBrush ImageSource=\"/Resources/Images/{i + 1}.png\" Viewbox=\"0,0,1,1\" ViewboxUnits=\"RelativeToBoundingBox\" Viewport=\"0,0,1,1\" ViewportUnits=\"RelativeToBoundingBox\"/></Path.Fill></Path></FixedPage>");
            WriteBinaryEntry(package, $"Resources/Images/{i + 1}.png", page.Captured!.Png);
        }

        pages.Append("</FixedDocument>");
        WriteEntry(package, "Documents/1/FixedDoc.fdoc", pages.ToString());
    }

    private static byte[] CompressRgb(byte[] rgba)
    {
        var rgb = GC.AllocateUninitializedArray<byte>(rgba.Length / 4 * 3);
        for (int source = 0, destination = 0; source < rgba.Length; source += 4)
        {
            rgb[destination++] = rgba[source];
            rgb[destination++] = rgba[source + 1];
            rgb[destination++] = rgba[source + 2];
        }

        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(rgb, 0, rgb.Length);
        }

        return output.ToArray();
    }

    private static byte[] Ascii(string value) => Encoding.Latin1.GetBytes(value);

    private static byte[] Combine(params byte[][] buffers)
    {
        var length = buffers.Sum(static buffer => buffer.Length);
        var result = GC.AllocateUninitializedArray<byte>(length);
        var offset = 0;
        foreach (var buffer in buffers)
        {
            Buffer.BlockCopy(buffer, 0, result, offset, buffer.Length);
            offset += buffer.Length;
        }

        return result;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content) =>
        WriteBinaryEntry(archive, path, Encoding.UTF8.GetBytes(content));

    private static void WriteBinaryEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
            _ownedStream?.Dispose();
            _ownedStream = null;
        }

        base.Dispose(disposing);
    }
}

internal static class SKOutputRasterizer
{
    internal sealed record PageData(int Width, int Height, byte[] Rgba, byte[] Png);

    public static PageData Capture(DrawingContext context, int width, int height, float scale = 1f)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            if (scale != 1f)
            {
                canvas.Scale(scale, scale);
            }

            canvas.Context.Append(context);
            canvas.Flush();
        }

        context.Clear();
        var rgba = bitmap.CopyRgba8888Rows();
        Unpremultiply(rgba);
        using var output = new MemoryStream();
        var writer = new StbImageWriteSharp.ImageWriter();
        writer.WritePng(rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output);
        return new PageData(width, height, rgba, output.ToArray());
    }

    private static void Unpremultiply(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha is 0 or 255)
            {
                continue;
            }

            pixels[i] = (byte)Math.Min(255, (pixels[i] * 255 + alpha / 2) / alpha);
            pixels[i + 1] = (byte)Math.Min(255, (pixels[i + 1] * 255 + alpha / 2) / alpha);
            pixels[i + 2] = (byte)Math.Min(255, (pixels[i + 2] * 255 + alpha / 2) / alpha);
        }
    }
}

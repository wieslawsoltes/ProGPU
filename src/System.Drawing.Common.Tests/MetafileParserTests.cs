using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace System.Drawing.Common.Tests;

public sealed class MetafileParserTests
{
    [Fact]
    public void PublicIdentitiesMatchThePinnedContract()
    {
        Assert.Equal(3, (int)EmfType.EmfOnly);
        Assert.Equal(4, (int)EmfType.EmfPlusOnly);
        Assert.Equal(5, (int)EmfType.EmfPlusDual);
        Assert.Equal(7, (int)MetafileFrameUnit.GdiCompatible);
        Assert.Equal(0x0001_0000, (int)EmfPlusRecordType.WmfRecordBase);
        Assert.Equal(0x0001_041B, (int)EmfPlusRecordType.WmfRectangle);
        Assert.Equal(70, (int)EmfPlusRecordType.EmfGdiComment);
        Assert.Equal(0x4001, (int)EmfPlusRecordType.Header);
        Assert.Equal(EmfPlusRecordType.Total - 1, EmfPlusRecordType.Max);
    }

    [Fact]
    public void PlaceableWmfParsesOwnedHeaderAndRecords()
    {
        byte[] bytes = CreatePlaceableWmf();
        using var source = new MemoryStream(bytes, writable: true);
        using var metafile = new Metafile(source);

        bytes.AsSpan().Clear();
        MetafileHeader header = metafile.GetMetafileHeader();

        Assert.Equal(MetafileType.WmfPlaceable, header.Type);
        Assert.Equal(new Rectangle(10, 20, 100, 200), header.Bounds);
        Assert.Equal(1440f, header.DpiX);
        Assert.Equal(46, header.MetafileSize);
        Assert.Equal(12, header.WmfHeader.Size);
        Assert.Equal(3, header.WmfHeader.MaxRecord);
        Assert.Equal(100, metafile.Width);
        Assert.Equal(200, metafile.Height);
        Assert.Equal(ImageFormat.Wmf, metafile.RawFormat);
        Assert.Equal(1, metafile.Records.Length);
        Assert.Equal(
            (EmfPlusRecordType)((int)EmfPlusRecordType.WmfRecordBase),
            metafile.Records[0].Type);
    }

    [Fact]
    public void StandardWmfParsesFromNonSeekableStream()
    {
        byte[] placeable = CreatePlaceableWmf();
        using var source = new NonSeekableReadStream(placeable[22..]);
        using var metafile = new Metafile(source);

        MetafileHeader header = metafile.GetMetafileHeader();
        Assert.Equal(MetafileType.Wmf, header.Type);
        Assert.Equal(Rectangle.Empty, header.Bounds);
        Assert.True(header.IsWmf());
        Assert.False(header.IsWmfPlaceable());
    }

    [Fact]
    public void EmfParsesBoundsDpiAndRecordTable()
    {
        using var metafile = new Metafile(new MemoryStream(CreateEmf(includeEmfPlus: false, dual: false)));
        MetafileHeader header = metafile.GetMetafileHeader();

        Assert.Equal(MetafileType.Emf, header.Type);
        Assert.Equal(new Rectangle(2, 3, 100, 50), header.Bounds);
        Assert.InRange(header.DpiX, 95.99f, 96.01f);
        Assert.InRange(header.DpiY, 95.99f, 96.01f);
        Assert.Equal(2, metafile.Records.Length);
        Assert.Equal(EmfPlusRecordType.EmfHeader, metafile.Records[0].Type);
        Assert.Equal(EmfPlusRecordType.EmfEof, metafile.Records[1].Type);
        Assert.Throws<ArgumentException>(() => _ = header.WmfHeader);
    }

    [Theory]
    [InlineData(false, MetafileType.EmfPlusOnly)]
    [InlineData(true, MetafileType.EmfPlusDual)]
    public void EmfPlusHeaderIsDecodedFromTheTypedComment(bool dual, MetafileType expectedType)
    {
        using var metafile = new Metafile(new MemoryStream(CreateEmf(includeEmfPlus: true, dual)));
        MetafileHeader header = metafile.GetMetafileHeader();

        Assert.Equal(expectedType, header.Type);
        Assert.True(header.IsEmfPlus());
        Assert.Equal(28, header.EmfPlusHeaderSize);
        Assert.Equal(96, header.LogicalDpiX);
        Assert.Equal(96, header.LogicalDpiY);
        Assert.True(header.IsDisplay());
        Assert.Equal(4, metafile.Records.Length);
        Assert.Equal(EmfPlusRecordType.EmfHeader, metafile.Records[0].Type);
        Assert.Equal(EmfPlusRecordType.Header, metafile.Records[1].Type);
        Assert.Equal(EmfPlusRecordType.EndOfFile, metafile.Records[2].Type);
        Assert.Equal(EmfPlusRecordType.EmfEof, metafile.Records[3].Type);
    }

    [Fact]
    public void StaticAndInstanceHeaderQueriesReturnIndependentSnapshots()
    {
        byte[] bytes = CreatePlaceableWmf();
        using var stream = new MemoryStream(bytes);
        MetafileHeader fromStream = Metafile.GetMetafileHeader(stream);
        MetaHeader first = fromStream.WmfHeader;
        first.Size = 999;

        Assert.Equal(12, fromStream.WmfHeader.Size);

        string path = Path.Combine(Path.GetTempPath(), $"progpu-{Guid.NewGuid():N}.wmf");
        try
        {
            File.WriteAllBytes(path, bytes);
            Assert.Equal(MetafileType.WmfPlaceable, Metafile.GetMetafileHeader(path).Type);
            using var metafile = new Metafile(path);
            using var clone = (Metafile)metafile.Clone();
            metafile.Dispose();
            Assert.Equal(MetafileType.WmfPlaceable, clone.GetMetafileHeader().Type);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MalformedSourcesFailClosed()
    {
        byte[] badChecksum = CreatePlaceableWmf();
        badChecksum[20] ^= 1;
        Assert.Throws<ArgumentException>(() => new Metafile(new MemoryStream(badChecksum)));

        byte[] missingWmfEof = CreatePlaceableWmf();
        missingWmfEof[44] = 1;
        Assert.Throws<ArgumentException>(() => new Metafile(new MemoryStream(missingWmfEof)));

        byte[] badEmfAlignment = CreateEmf(includeEmfPlus: false, dual: false);
        WriteUInt32(badEmfAlignment, 92, 18);
        Assert.Throws<ArgumentException>(() => new Metafile(new MemoryStream(badEmfAlignment)));

        byte[] truncated = CreateEmf(includeEmfPlus: true, dual: true)[..^1];
        Assert.Throws<ArgumentException>(() => new Metafile(new MemoryStream(truncated)));
    }

    [Fact]
    public void NativeHandleAndRecordingOperationsStayAtExplicitWindowsSeams()
    {
        Assert.Throws<PlatformNotSupportedException>(() => new Metafile(IntPtr.Zero, false));
        Assert.Throws<PlatformNotSupportedException>(() => new Metafile(IntPtr.Zero, EmfType.EmfOnly));
        Assert.Throws<PlatformNotSupportedException>(() => Metafile.GetMetafileHeader(IntPtr.Zero));

        using var metafile = new Metafile(new MemoryStream(CreateEmf(includeEmfPlus: false, dual: false)));
        Assert.Throws<PlatformNotSupportedException>(() => metafile.GetHenhmetafile());
        Assert.Throws<NotSupportedException>(() =>
            metafile.PlayRecord(EmfPlusRecordType.EmfEof, 0, 0, []));
    }

    [Fact]
    public void LargeRecordTableParsingHasBoundedAllocation()
    {
        byte[] fixture = CreateLargeEmf(4_096);
        using (var warmup = new Metafile(new MemoryStream(fixture, writable: false)))
        {
            Assert.Equal(4_098, warmup.Records.Length);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 16; iteration++)
        {
            using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
            Assert.Equal(4_098, metafile.Records.Length);
        }

        long allocatedPerParse = (GC.GetAllocatedBytesForCurrentThread() - before) / 16;
        Assert.InRange(allocatedPerParse, fixture.Length, 256 * 1024);
    }

    [Fact]
    public void EnumerationExposesOwnedPayloadsInSourceOrderAndStopsOnFalse()
    {
        using var metafile = new Metafile(new MemoryStream(CreateEmf(includeEmfPlus: false, dual: false)));
        using var target = new Bitmap(8, 8);
        using Graphics graphics = Graphics.FromImage(target);
        var records = new List<(EmfPlusRecordType Type, int Flags, int Size, int FirstValue, PlayRecordCallback? Playback)>();

        graphics.EnumerateMetafile(
            metafile,
            Point.Empty,
            (type, flags, size, data, playback) =>
            {
                records.Add((type, flags, size, size == 0 ? 0 : Marshal.ReadInt32(data), playback));
                return true;
            },
            new IntPtr(0x1234));

        Assert.Equal(2, records.Count);
        Assert.Equal((EmfPlusRecordType.EmfHeader, 0, 80, 2, null), records[0]);
        Assert.Equal(EmfPlusRecordType.EmfEof, records[1].Type);
        Assert.Equal(12, records[1].Size);

        int stoppedCount = 0;
        graphics.EnumerateMetafile(
            metafile,
            PointF.Empty,
            (_, _, _, _, _) =>
            {
                stoppedCount++;
                return false;
            });
        Assert.Equal(1, stoppedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmfPlusEnumerationReplacesTheTransportEnvelopeAtItsSourcePosition(bool dual)
    {
        using var metafile = new Metafile(new MemoryStream(CreateEmf(includeEmfPlus: true, dual)));
        using var target = new Bitmap(8, 8);
        using Graphics graphics = Graphics.FromImage(target);
        var recordTypes = new List<EmfPlusRecordType>();

        graphics.EnumerateMetafile(
            metafile,
            Rectangle.Empty,
            (type, _, _, _, _) =>
            {
                recordTypes.Add(type);
                return true;
            });

        Assert.Equal(
            [
                EmfPlusRecordType.EmfHeader,
                EmfPlusRecordType.Header,
                EmfPlusRecordType.EndOfFile,
                EmfPlusRecordType.EmfEof
            ],
            recordTypes);
    }

    [Fact]
    public void EveryOfficialEnumerationOverloadReachesTheTypedEnumerator()
    {
        using var metafile = new Metafile(new MemoryStream(CreateEmf(includeEmfPlus: false, dual: false)));
        using var target = new Bitmap(8, 8);
        using Graphics graphics = Graphics.FromImage(target);
        using var attributes = new ImageAttributes();
        int callbacks = 0;
        Graphics.EnumerateMetafileProc callback = (_, _, _, _, _) =>
        {
            callbacks++;
            return false;
        };

        MethodInfo[] overloads = typeof(Graphics)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(static method => method.Name == nameof(Graphics.EnumerateMetafile))
            .OrderBy(static method => method.ToString(), StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(36, overloads.Length);
        foreach (MethodInfo overload in overloads)
        {
            object?[] arguments = overload.GetParameters()
                .Select(parameter => CreateEnumerationArgument(parameter.ParameterType, metafile, callback, attributes))
                .ToArray();
            overload.Invoke(graphics, arguments);
        }

        Assert.Equal(36, callbacks);
    }

    [Fact]
    public void EnumerationValidatesTypedInputsAndDisposedState()
    {
        using var metafile = new Metafile(new MemoryStream(CreateEmf(includeEmfPlus: false, dual: false)));
        using var target = new Bitmap(8, 8);
        using Graphics graphics = Graphics.FromImage(target);
        Graphics.EnumerateMetafileProc callback = static (_, _, _, _, _) => true;

        Assert.Throws<ArgumentNullException>(() => graphics.EnumerateMetafile(null!, Point.Empty, callback));
        Assert.Throws<ArgumentNullException>(() => graphics.EnumerateMetafile(metafile, Point.Empty, null!));
        Assert.Throws<ArgumentNullException>(() => graphics.EnumerateMetafile(metafile, (Point[])null!, callback));
        Assert.Throws<ArgumentException>(() => graphics.EnumerateMetafile(metafile, new Point[2], callback));
        Assert.Throws<ArgumentException>(() => graphics.EnumerateMetafile(metafile, new PointF[4], callback));
        Assert.Throws<System.ComponentModel.InvalidEnumArgumentException>(() =>
            graphics.EnumerateMetafile(metafile, Point.Empty, Rectangle.Empty, (GraphicsUnit)99, callback));

        var attributes = new ImageAttributes();
        attributes.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            graphics.EnumerateMetafile(metafile, Point.Empty, callback, IntPtr.Zero, attributes));

        using var disposedMetafile = new Metafile(new MemoryStream(CreateEmf(includeEmfPlus: false, dual: false)));
        disposedMetafile.Dispose();
        Assert.Throws<ObjectDisposedException>(() => graphics.EnumerateMetafile(disposedMetafile, Point.Empty, callback));

        graphics.Dispose();
        Assert.Throws<ArgumentException>(() => graphics.EnumerateMetafile(metafile, Point.Empty, callback));
    }

    [Fact]
    public void WarmedEnumerationDoesNotAllocatePerRecordPayloads()
    {
        using var metafile = new Metafile(new MemoryStream(CreateLargeEmf(4_096)));
        using var target = new Bitmap(8, 8);
        using Graphics graphics = Graphics.FromImage(target);
        int count = 0;
        Graphics.EnumerateMetafileProc callback = (_, _, _, _, _) =>
        {
            count++;
            return true;
        };

        // Exercise enough complete walks for tiered compilation and dynamic PGO to
        // settle before the allocation window. A single walk is not a stable warmup
        // on hosted Linux runners even though it invokes the callback 4,098 times.
        for (int iteration = 0; iteration < 16; iteration++)
        {
            graphics.EnumerateMetafile(metafile, Point.Empty, callback);
        }

        count = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 16; iteration++)
        {
            graphics.EnumerateMetafile(metafile, Point.Empty, callback);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(4_098 * 16, count);
        Assert.InRange(allocated, 0, 4_096);
    }

    private static object? CreateEnumerationArgument(
        Type type,
        Metafile metafile,
        Graphics.EnumerateMetafileProc callback,
        ImageAttributes attributes)
    {
        if (type == typeof(Metafile)) return metafile;
        if (type == typeof(Graphics.EnumerateMetafileProc)) return callback;
        if (type == typeof(IntPtr)) return new IntPtr(0x1234);
        if (type == typeof(ImageAttributes)) return attributes;
        if (type == typeof(Point)) return Point.Empty;
        if (type == typeof(PointF)) return PointF.Empty;
        if (type == typeof(Rectangle)) return Rectangle.Empty;
        if (type == typeof(RectangleF)) return RectangleF.Empty;
        if (type == typeof(Point[])) return new[] { Point.Empty, new Point(1, 0), new Point(0, 1) };
        if (type == typeof(PointF[])) return new[] { PointF.Empty, new PointF(1, 0), new PointF(0, 1) };
        if (type == typeof(GraphicsUnit)) return GraphicsUnit.Pixel;
        throw new InvalidOperationException($"Unexpected enumeration parameter type: {type}.");
    }

    private static byte[] CreatePlaceableWmf()
    {
        byte[] bytes = new byte[46];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 6, 10);
        WriteInt16(bytes, 8, 20);
        WriteInt16(bytes, 10, 110);
        WriteInt16(bytes, 12, 220);
        WriteUInt16(bytes, 14, 1440);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }

        WriteUInt16(bytes, 20, checksum);
        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, 12);
        WriteUInt16(bytes, 32, 0);
        WriteUInt32(bytes, 34, 3);
        WriteUInt16(bytes, 38, 0);
        WriteUInt32(bytes, 40, 3);
        WriteUInt16(bytes, 44, 0);
        return bytes;
    }

    private static byte[] CreateEmf(bool includeEmfPlus, bool dual)
    {
        int commentSize = includeEmfPlus ? 56 : 0;
        byte[] bytes = new byte[88 + commentSize + 20];
        WriteUInt32(bytes, 0, 1);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 8, 2);
        WriteInt32(bytes, 12, 3);
        WriteInt32(bytes, 16, 102);
        WriteInt32(bytes, 20, 53);
        WriteInt32(bytes, 24, 0);
        WriteInt32(bytes, 28, 0);
        WriteInt32(bytes, 32, 2646);
        WriteInt32(bytes, 36, 1323);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)bytes.Length);
        WriteUInt32(bytes, 52, includeEmfPlus ? 3u : 2u);
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 960);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 254);
        WriteInt32(bytes, 84, 127);

        int eofOffset = 88;
        if (includeEmfPlus)
        {
            int commentOffset = 88;
            WriteUInt32(bytes, commentOffset, 70);
            WriteUInt32(bytes, commentOffset + 4, 56);
            WriteUInt32(bytes, commentOffset + 8, 44);
            WriteUInt32(bytes, commentOffset + 12, 0x2B46_4D45);
            int plusHeader = commentOffset + 16;
            WriteUInt16(bytes, plusHeader, 0x4001);
            WriteUInt16(bytes, plusHeader + 2, dual ? (ushort)1 : (ushort)0);
            WriteUInt32(bytes, plusHeader + 4, 28);
            WriteUInt32(bytes, plusHeader + 8, 16);
            WriteUInt32(bytes, plusHeader + 12, 0xDBC0_1002);
            WriteUInt32(bytes, plusHeader + 16, 1);
            WriteInt32(bytes, plusHeader + 20, 96);
            WriteInt32(bytes, plusHeader + 24, 96);
            int plusEof = plusHeader + 28;
            WriteUInt16(bytes, plusEof, 0x4002);
            WriteUInt32(bytes, plusEof + 4, 12);
            eofOffset += 56;
        }

        WriteUInt32(bytes, eofOffset, 14);
        WriteUInt32(bytes, eofOffset + 4, 20);
        WriteUInt32(bytes, eofOffset + 16, 20);
        return bytes;
    }

    private static byte[] CreateLargeEmf(int stateRecordCount)
    {
        int totalBytes = checked(88 + stateRecordCount * 8 + 20);
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, 1);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 640);
        WriteInt32(bytes, 20, 480);
        WriteInt32(bytes, 32, 16_933);
        WriteInt32(bytes, 36, 12_700);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, (uint)(stateRecordCount + 2));
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 640);
        WriteInt32(bytes, 76, 480);
        WriteInt32(bytes, 80, 169);
        WriteInt32(bytes, 84, 127);

        int cursor = 88;
        for (int index = 0; index < stateRecordCount; index++)
        {
            WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfSaveDC);
            WriteUInt32(bytes, cursor + 4, 8);
            cursor += 8;
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static void WriteUInt16(byte[] target, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), value);

    private static void WriteInt16(byte[] target, int offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(target.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), value);

    private static void WriteInt32(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), value);

    private sealed class NonSeekableReadStream(byte[] source) : Stream
    {
        private readonly MemoryStream _inner = new(source, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

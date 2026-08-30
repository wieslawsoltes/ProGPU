using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using ProGPU.SystemDrawing;
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

    [Fact]
    public void EmfAcceptsADeclaredRecordCountThatExcludesTheHeaderRecord()
    {
        byte[] bytes = CreateLargeEmf(1);
        WriteUInt32(bytes, 52, 2);

        using var metafile = new Metafile(new MemoryStream(bytes));

        Assert.Equal(3, metafile.Records.Length);
        Assert.Equal(EmfPlusRecordType.EmfHeader, metafile.Records[0].Type);
        Assert.Equal(EmfPlusRecordType.EmfEof, metafile.Records[2].Type);
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
    public void PortableRecorderWritesOwnedCommentsAsAValidEmfPlusDocument()
    {
        using var target = new MemoryStream();
        using Metafile metafile = PortableMetafile.Create(target, new Rectangle(2, 3, 100, 50));
        Assert.Equal(100, metafile.Width);
        Assert.Equal(50, metafile.Height);
        Assert.Throws<InvalidOperationException>(() => metafile.GetMetafileHeader());

        byte[] first = [1, 2, 3];
        using (Graphics recorder = Graphics.FromImage(metafile))
        {
            recorder.AddMetafileComment(first);
            first.AsSpan().Clear();
            recorder.AddMetafileComment([4, 5, 6, 7]);
        }

        Assert.True(target.CanWrite);
        MetafileHeader completedHeader = metafile.GetMetafileHeader();
        Assert.Equal(MetafileType.EmfPlusOnly, completedHeader.Type);
        Assert.Equal(new Rectangle(2, 3, 100, 50), completedHeader.Bounds);
        Assert.Equal(96, completedHeader.LogicalDpiX);
        Assert.Equal(96, completedHeader.LogicalDpiY);
        Assert.True(completedHeader.IsDisplay());

        target.Position = 0;
        using var reparsed = new Metafile(target);
        Assert.Equal(completedHeader.MetafileSize, reparsed.GetMetafileHeader().MetafileSize);
        Assert.Equal(
            [
                EmfPlusRecordType.EmfHeader,
                EmfPlusRecordType.Header,
                EmfPlusRecordType.Comment,
                EmfPlusRecordType.Comment,
                EmfPlusRecordType.EndOfFile,
                EmfPlusRecordType.EmfEof
            ],
            reparsed.Records.ToArray().Select(static record => record.Type));
        Assert.Equal(new byte[] { 1, 2, 3 }, GetPayload(reparsed, reparsed.Records[2]));
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, GetPayload(reparsed, reparsed.Records[3]));
    }

    [Fact]
    public void PortableRecorderSupportsNonSeekableTargetsAndZeroLengthComments()
    {
        using var target = new NonSeekableWriteStream();
        using Metafile metafile = PortableMetafile.Create(target, Rectangle.Empty);
        using (Graphics recorder = Graphics.FromImage(metafile))
        {
            recorder.AddMetafileComment([]);
        }

        byte[] encoded = target.ToArray();
        using var reparsed = new Metafile(new MemoryStream(encoded, writable: false));
        Assert.Equal(MetafileType.EmfPlusOnly, reparsed.GetMetafileHeader().Type);
        Assert.Equal(5, reparsed.Records.Length);
        Assert.Equal(EmfPlusRecordType.Comment, reparsed.Records[2].Type);
        Assert.Equal(0, reparsed.Records[2].DataLength);
    }

    [Fact]
    public void PortableRecorderHasExclusiveLifetimeAndExplicitCommentBoundary()
    {
        using var target = new MemoryStream();
        using Metafile metafile = PortableMetafile.Create(target, new Rectangle(0, 0, 8, 8));
        Graphics recorder = Graphics.FromImage(metafile);

        Assert.Throws<InvalidOperationException>(() => Graphics.FromImage(metafile));
        Assert.Throws<ArgumentNullException>(() => recorder.AddMetafileComment(null!));

        using var bitmap = new Bitmap(1, 1);
        using Graphics ordinary = Graphics.FromImage(bitmap);
        Assert.Throws<InvalidOperationException>(() => ordinary.AddMetafileComment([1]));

        recorder.Dispose();
        recorder.Dispose();
        Assert.Throws<InvalidOperationException>(() => Graphics.FromImage(metafile));
        Assert.NotEmpty(target.ToArray());
    }

    [Fact]
    public void PortableRecorderRejectsUnsupportedDrawingWithoutWritingPartialOutput()
    {
        using var target = new MemoryStream();
        using Metafile metafile = PortableMetafile.Create(target, new Rectangle(0, 0, 8, 8));
        Graphics recorder = Graphics.FromImage(metafile);
        recorder.FillRectangle(Brushes.Red, 0, 0, 1, 1);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(recorder.Dispose);
        Assert.Contains("comment records only", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, target.Length);
        Assert.Throws<InvalidOperationException>(() => metafile.GetMetafileHeader());
    }

    [Fact]
    public void PortableRecorderValidatesTargetBoundsAndAbortedOwnerLifetime()
    {
        using var readOnly = new MemoryStream([], writable: false);
        Assert.Throws<ArgumentException>(() => PortableMetafile.Create(readOnly, Rectangle.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PortableMetafile.Create(new MemoryStream(), new Rectangle(0, 0, -1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PortableMetafile.Create(new MemoryStream(), new Rectangle(int.MaxValue, 0, 1, 1)));

        using var target = new MemoryStream();
        var metafile = PortableMetafile.Create(target, Rectangle.Empty);
        Graphics recorder = Graphics.FromImage(metafile);
        metafile.Dispose();
        Assert.Throws<ObjectDisposedException>(recorder.Dispose);
        Assert.Equal(0, target.Length);
    }

    [Fact]
    public void EmfPlaybackDrawsTypedGdiRecordsIntoTheDestinationRectangle()
    {
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackEmf()));
        using var target = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new RectangleF(4, 6, 20, 20));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(10, 12).ToArgb());
        Assert.Equal(0, target.GetPixel(2, 2).A);
        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(14, 24).ToArgb());
    }

    [Fact]
    public void WmfPlaybackDrawsTypedStateObjectsClipAndVectorPrimitives()
    {
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf()));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(16, 16).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(44, 16).ToArgb());
        Color linePixel = target.GetPixel(32, 32);
        Assert.True(linePixel.A > 0);
        Assert.Equal((0, 0, 0), (linePixel.R, linePixel.G, linePixel.B));
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(8, 46).ToArgb());
        Assert.Equal(0, target.GetPixel(16, 46).A);
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(46, 46).ToArgb());
        Assert.Equal(0, target.GetPixel(46, 54).A);
        Assert.Equal(0, target.GetPixel(2, 2).A);
    }

    [Fact]
    public void WmfPlaybackFailureDoesNotPublishPartialCommands()
    {
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(includeUnsupportedRecord: true)));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfTextOut), exception.Message, StringComparison.Ordinal);
            Assert.Contains("byte offset", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(16, 16).ToArgb());
    }

    [Fact]
    public void WmfEllipseRejectsUnorderedBoundsWithoutPublishingCommands()
    {
        byte[] bytes = CreatePlaybackWmf();
        int ellipseDataOffset;
        using (var parsed = new Metafile(new MemoryStream(bytes, writable: false)))
        {
            ellipseDataOffset = Assert.Single(
                parsed.Records.ToArray(),
                record => record.Type == EmfPlusRecordType.WmfEllipse).DataOffset;
        }
        short left = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(ellipseDataOffset + 6, 2));
        WriteInt16(bytes, ellipseDataOffset + 2, left);

        using var metafile = new Metafile(new MemoryStream(bytes, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfEllipse), exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(16, 16).ToArgb());
    }

    [Fact]
    public void WmfRestoreDcRejectsUnavailableRelativeLevelWithoutPublishingCommands()
    {
        byte[] bytes = CreatePlaybackWmf();
        int restoreDataOffset;
        using (var parsed = new Metafile(new MemoryStream(bytes, writable: false)))
        {
            restoreDataOffset = Assert.Single(
                parsed.Records.ToArray(),
                record => record.Type == EmfPlusRecordType.WmfRestoreDC).DataOffset;
        }
        WriteInt16(bytes, restoreDataOffset, -2);

        using var metafile = new Metafile(new MemoryStream(bytes, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfRestoreDC), exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(16, 16).ToArgb());
    }

    [Fact]
    public void EmfPlaybackFailureDoesNotPublishPartialCommands()
    {
        byte[] bytes = CreatePlaybackEmf();
        WriteUInt32(bytes, 204, (uint)EmfPlusRecordType.EmfSetTextColor);
        using var metafile = new Metafile(new MemoryStream(bytes));
        using var target = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 16, 16)));
            Assert.Contains(nameof(EmfPlusRecordType.EmfSetTextColor), exception.Message, StringComparison.Ordinal);
            Assert.Contains("byte offset 204", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(4, 4).ToArgb());
    }

    [Fact]
    public void EmfPlaybackRejectsImageAttributesAndPerspectiveMappingExplicitly()
    {
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackEmf()));
        using var target = new Bitmap(16, 16);
        using Graphics graphics = Graphics.FromImage(target);
        using var attributes = new ImageAttributes();

        Assert.Throws<NotSupportedException>(() => graphics.DrawImage(
            metafile,
            new Rectangle(0, 0, 16, 16),
            0,
            0,
            10,
            10,
            GraphicsUnit.Pixel,
            attributes));
        Assert.Throws<NotSupportedException>(() => graphics.DrawImage(
            metafile,
            [new PointF(0, 0), new PointF(16, 0), new PointF(0, 16), new PointF(15, 15)]));
    }

    [Fact]
    public void EmfPlaybackComposesMapWorldAndSavedDcState()
    {
        using var metafile = new Metafile(new MemoryStream(CreateStatefulPlaybackEmf()));
        using var target = new Bitmap(20, 20);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 20, 20));
        }

        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(4, 4).ToArgb());
        Assert.Equal(0, target.GetPixel(9, 4).A);
        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(14, 4).ToArgb());
    }

    [Fact]
    public void PortableCommentOnlyMetafilePlaybackIsNonvisual()
    {
        using var encoded = new MemoryStream();
        using (Metafile recording = PortableMetafile.Create(encoded, new Rectangle(0, 0, 8, 8)))
        {
            using Graphics recorder = Graphics.FromImage(recording);
            recorder.AddMetafileComment([1, 2, 3]);
        }

        encoded.Position = 0;
        using var metafile = new Metafile(encoded);
        using var target = new Bitmap(8, 8);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Green);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 8, 8));
        }

        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(4, 4).ToArgb());
    }

    [Fact]
    public void EmfPlaybackRestoresClipAndDrawsPolyPolygonRecords()
    {
        using var metafile = new Metafile(new MemoryStream(CreateClippedPolyPolygonEmf()));
        using var target = new Bitmap(20, 20);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 20, 20));
        }

        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(4, 4).ToArgb());
        Assert.Equal(0, target.GetPixel(14, 4).A);
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(14, 14).ToArgb());
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

    private static byte[] GetPayload(Metafile metafile, MetafileRecord record) =>
        metafile.Source.Slice(record.DataOffset, record.DataLength).ToArray();

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

    private static byte[] CreatePlaybackWmf(bool includeUnsupportedRecord = false)
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x0104, WmfWords(13)),
            (0x0105, WmfWords(0)),
            (0x0106, WmfWords(1)),
            (0x012E, WmfWords(0)),
            (0x0201, WmfColor(Color.White)),
            (0x020C, WmfWords(64, 64)),
            (0x020B, WmfWords(0, 0)),
            (0x0416, WmfWords(52, 64, 0, 0)),
            (0x02FC, WmfBrush(Color.Red)),
            (0x02FA, WmfPen(Color.Black, 1)),
            (0x012D, WmfWords(0)),
            (0x012D, WmfWords(1)),
            (0x0324, WmfPoints(new Point(8, 8), new Point(28, 8), new Point(28, 28), new Point(8, 28))),
            (0x02FA, WmfPen(Color.White, 1, @null: true)),
            (0x012D, WmfWords(2)),
            (0x02FC, WmfBrush(Color.Blue)),
            (0x012D, WmfWords(3)),
            (0x01F0, WmfWords(0)),
            (0x02FC, WmfBrush(Color.Green)),
            (0x012D, WmfWords(0)),
            (0x0324, WmfPoints(new Point(36, 8), new Point(56, 8), new Point(56, 28), new Point(36, 28))),
            (0x012D, WmfWords(1)),
            (0x0325, WmfPoints(new Point(4, 32), new Point(60, 32))),
            (0x0817, WmfWords(36, 18, 46, 28, 56, 28, 36, 8)),
            (0x001E, []),
            (0x0415, WmfWords(50, 50, 42, 12)),
            (0x041B, WmfWords(56, 28, 36, 4)),
            (0x0127, WmfWords(-1)),
            (0x0418, WmfWords(56, 56, 36, 36))
        };
        if (includeUnsupportedRecord)
        {
            records.Add((0x0521, WmfWords(0)));
        }
        records.Add((0, []));

        int maximumRecordWords = records.Max(record => (record.Payload.Length + 6) / 2);
        int declaredWords = 9 + records.Sum(record => (record.Payload.Length + 6) / 2);
        byte[] bytes = new byte[checked(22 + declaredWords * 2)];
        WriteUInt32(bytes, 0, 0x9AC6_CDD7);
        WriteInt16(bytes, 6, 0);
        WriteInt16(bytes, 8, 0);
        WriteInt16(bytes, 10, 64);
        WriteInt16(bytes, 12, 64);
        WriteUInt16(bytes, 14, 96);
        ushort checksum = 0;
        for (int offset = 0; offset < 20; offset += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }
        WriteUInt16(bytes, 20, checksum);

        WriteUInt16(bytes, 22, 1);
        WriteUInt16(bytes, 24, 9);
        WriteUInt16(bytes, 26, 0x0300);
        WriteUInt32(bytes, 28, (uint)declaredWords);
        WriteUInt16(bytes, 32, 4);
        WriteUInt32(bytes, 34, (uint)maximumRecordWords);
        WriteUInt16(bytes, 38, 0);

        int cursor = 40;
        foreach ((ushort function, byte[] payload) in records)
        {
            WriteUInt32(bytes, cursor, (uint)((payload.Length + 6) / 2));
            WriteUInt16(bytes, cursor + 4, function);
            payload.CopyTo(bytes, cursor + 6);
            cursor += payload.Length + 6;
        }
        return bytes;
    }

    private static byte[] WmfWords(params short[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int index = 0; index < values.Length; index++)
        {
            WriteInt16(bytes, index * 2, values[index]);
        }
        return bytes;
    }

    private static byte[] WmfColor(Color color)
    {
        byte[] bytes = new byte[4];
        WriteUInt32(bytes, 0, (uint)(color.R | color.G << 8 | color.B << 16));
        return bytes;
    }

    private static byte[] WmfPen(Color color, short width, bool @null = false)
    {
        byte[] bytes = new byte[10];
        WriteUInt16(bytes, 0, @null ? (ushort)5 : (ushort)0);
        WriteInt16(bytes, 2, width);
        WriteInt16(bytes, 4, width);
        WmfColor(color).CopyTo(bytes, 6);
        return bytes;
    }

    private static byte[] WmfBrush(Color color)
    {
        byte[] bytes = new byte[8];
        WmfColor(color).CopyTo(bytes, 2);
        return bytes;
    }

    private static byte[] WmfPoints(params Point[] points)
    {
        byte[] bytes = new byte[checked(2 + points.Length * 4)];
        WriteInt16(bytes, 0, checked((short)points.Length));
        for (int index = 0; index < points.Length; index++)
        {
            WriteInt16(bytes, 2 + index * 4, checked((short)points[index].X));
            WriteInt16(bytes, 4 + index * 4, checked((short)points[index].Y));
        }
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

    private static byte[] CreatePlaybackEmf()
    {
        byte[] bytes = new byte[240];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 10);
        WriteInt32(bytes, 20, 10);
        WriteInt32(bytes, 32, 264);
        WriteInt32(bytes, 36, 264);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)bytes.Length);
        WriteUInt32(bytes, 52, 9);
        WriteUInt16(bytes, 56, 3);
        WriteInt32(bytes, 72, 96);
        WriteInt32(bytes, 76, 96);
        WriteInt32(bytes, 80, 25);
        WriteInt32(bytes, 84, 25);

        WriteUInt32(bytes, 88, (uint)EmfPlusRecordType.EmfCreateBrushIndirect);
        WriteUInt32(bytes, 92, 24);
        WriteUInt32(bytes, 96, 1);
        WriteUInt32(bytes, 100, 0);
        WriteUInt32(bytes, 104, 0x0000_00FF);

        WriteUInt32(bytes, 112, (uint)EmfPlusRecordType.EmfSelectObject);
        WriteUInt32(bytes, 116, 12);
        WriteUInt32(bytes, 120, 1);

        WriteUInt32(bytes, 124, (uint)EmfPlusRecordType.EmfCreatePen);
        WriteUInt32(bytes, 128, 28);
        WriteUInt32(bytes, 132, 2);
        WriteUInt32(bytes, 136, 0);
        WriteInt32(bytes, 140, 1);
        WriteUInt32(bytes, 148, 0x0000_0000);

        WriteUInt32(bytes, 152, (uint)EmfPlusRecordType.EmfSelectObject);
        WriteUInt32(bytes, 156, 12);
        WriteUInt32(bytes, 160, 2);

        WriteUInt32(bytes, 164, (uint)EmfPlusRecordType.EmfRectangle);
        WriteUInt32(bytes, 168, 24);
        WriteInt32(bytes, 172, 1);
        WriteInt32(bytes, 176, 1);
        WriteInt32(bytes, 180, 5);
        WriteInt32(bytes, 184, 5);

        WriteUInt32(bytes, 188, (uint)EmfPlusRecordType.EmfMoveToEx);
        WriteUInt32(bytes, 192, 16);
        WriteInt32(bytes, 196, 0);
        WriteInt32(bytes, 200, 9);

        WriteUInt32(bytes, 204, (uint)EmfPlusRecordType.EmfLineTo);
        WriteUInt32(bytes, 208, 16);
        WriteInt32(bytes, 212, 10);
        WriteInt32(bytes, 216, 9);

        WriteUInt32(bytes, 220, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, 224, 20);
        WriteUInt32(bytes, 236, 20);
        return bytes;
    }

    private static byte[] CreateStatefulPlaybackEmf()
    {
        byte[] bytes = new byte[308];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 10);
        WriteInt32(bytes, 20, 10);
        WriteInt32(bytes, 32, 264);
        WriteInt32(bytes, 36, 264);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)bytes.Length);
        WriteUInt32(bytes, 52, 14);
        WriteUInt16(bytes, 56, 1);
        WriteInt32(bytes, 72, 96);
        WriteInt32(bytes, 76, 96);
        WriteInt32(bytes, 80, 25);
        WriteInt32(bytes, 84, 25);

        WriteRecordUInt32(bytes, 88, EmfPlusRecordType.EmfSelectObject, 0x8000_0004);
        WriteRecordUInt32(bytes, 100, EmfPlusRecordType.EmfSelectObject, 0x8000_0008);
        WriteRecordInt32(bytes, 112, EmfPlusRecordType.EmfSetMapMode, 8);
        WriteRecordPoint(bytes, 124, EmfPlusRecordType.EmfSetWindowExtEx, 10, 10);
        WriteRecordPoint(bytes, 140, EmfPlusRecordType.EmfSetViewportExtEx, 5, 5);
        WriteRecordPoint(bytes, 156, EmfPlusRecordType.EmfSetWindowOrgEx, 2, 2);
        WriteRecordPoint(bytes, 172, EmfPlusRecordType.EmfSetViewportOrgEx, 1, 1);
        WriteUInt32(bytes, 188, (uint)EmfPlusRecordType.EmfSaveDC);
        WriteUInt32(bytes, 192, 8);

        WriteUInt32(bytes, 196, (uint)EmfPlusRecordType.EmfSetWorldTransform);
        WriteUInt32(bytes, 200, 32);
        WriteSingle(bytes, 204, 1f);
        WriteSingle(bytes, 216, 1f);
        WriteSingle(bytes, 220, 10f);
        WriteRectangleRecord(bytes, 228, 2, 2, 6, 6);
        WriteRecordInt32(bytes, 252, EmfPlusRecordType.EmfRestoreDC, -1);
        WriteRectangleRecord(bytes, 264, 2, 2, 6, 6);

        WriteUInt32(bytes, 288, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, 292, 20);
        WriteUInt32(bytes, 304, 20);
        return bytes;
    }

    private static byte[] CreateClippedPolyPolygonEmf()
    {
        byte[] bytes = new byte[376];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 10);
        WriteInt32(bytes, 20, 10);
        WriteInt32(bytes, 32, 264);
        WriteInt32(bytes, 36, 264);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)bytes.Length);
        WriteUInt32(bytes, 52, 13);
        WriteUInt16(bytes, 56, 2);
        WriteInt32(bytes, 72, 96);
        WriteInt32(bytes, 76, 96);
        WriteInt32(bytes, 80, 25);
        WriteInt32(bytes, 84, 25);

        WriteRecordUInt32(bytes, 88, EmfPlusRecordType.EmfSelectObject, 0x8000_0004);
        WriteRecordUInt32(bytes, 100, EmfPlusRecordType.EmfSelectObject, 0x8000_0008);
        WriteRecordInt32(bytes, 112, EmfPlusRecordType.EmfSetBkMode, 1);
        WriteRecordInt32(bytes, 124, EmfPlusRecordType.EmfSetROP2, 13);
        WriteUInt32(bytes, 136, (uint)EmfPlusRecordType.EmfSaveDC);
        WriteUInt32(bytes, 140, 8);
        WriteRectangleRecord(bytes, 144, EmfPlusRecordType.EmfIntersectClipRect, 0, 0, 5, 10);

        WriteUInt32(bytes, 168, (uint)EmfPlusRecordType.EmfPolyPolygon);
        WriteUInt32(bytes, 172, 88);
        WriteInt32(bytes, 176, 1);
        WriteInt32(bytes, 180, 1);
        WriteInt32(bytes, 184, 9);
        WriteInt32(bytes, 188, 4);
        WriteUInt32(bytes, 192, 2);
        WriteUInt32(bytes, 196, 6);
        WriteUInt32(bytes, 200, 3);
        WriteUInt32(bytes, 204, 3);
        WritePoint(bytes, 208, 1, 1);
        WritePoint(bytes, 216, 4, 1);
        WritePoint(bytes, 224, 1, 4);
        WritePoint(bytes, 232, 6, 1);
        WritePoint(bytes, 240, 9, 1);
        WritePoint(bytes, 248, 6, 4);

        WriteRecordInt32(bytes, 256, EmfPlusRecordType.EmfRestoreDC, -1);
        WriteUInt32(bytes, 268, (uint)EmfPlusRecordType.EmfCreateBrushIndirect);
        WriteUInt32(bytes, 272, 24);
        WriteUInt32(bytes, 276, 1);
        WriteUInt32(bytes, 280, 0);
        WriteUInt32(bytes, 284, 0x0000_00FF);
        WriteRecordUInt32(bytes, 292, EmfPlusRecordType.EmfSelectObject, 1);

        WriteUInt32(bytes, 304, (uint)EmfPlusRecordType.EmfPolygon);
        WriteUInt32(bytes, 308, 52);
        WriteInt32(bytes, 312, 6);
        WriteInt32(bytes, 316, 6);
        WriteInt32(bytes, 320, 9);
        WriteInt32(bytes, 324, 9);
        WriteUInt32(bytes, 328, 3);
        WritePoint(bytes, 332, 6, 6);
        WritePoint(bytes, 340, 9, 6);
        WritePoint(bytes, 348, 6, 9);

        WriteUInt32(bytes, 356, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, 360, 20);
        WriteUInt32(bytes, 372, 20);
        return bytes;
    }

    private static void WriteRecordUInt32(
        byte[] target,
        int offset,
        EmfPlusRecordType type,
        uint value)
    {
        WriteUInt32(target, offset, (uint)type);
        WriteUInt32(target, offset + 4, 12);
        WriteUInt32(target, offset + 8, value);
    }

    private static void WriteRecordInt32(
        byte[] target,
        int offset,
        EmfPlusRecordType type,
        int value)
    {
        WriteUInt32(target, offset, (uint)type);
        WriteUInt32(target, offset + 4, 12);
        WriteInt32(target, offset + 8, value);
    }

    private static void WriteRecordPoint(
        byte[] target,
        int offset,
        EmfPlusRecordType type,
        int x,
        int y)
    {
        WriteUInt32(target, offset, (uint)type);
        WriteUInt32(target, offset + 4, 16);
        WriteInt32(target, offset + 8, x);
        WriteInt32(target, offset + 12, y);
    }

    private static void WriteRectangleRecord(
        byte[] target,
        int offset,
        int left,
        int top,
        int right,
        int bottom)
        => WriteRectangleRecord(
            target,
            offset,
            EmfPlusRecordType.EmfRectangle,
            left,
            top,
            right,
            bottom);

    private static void WriteRectangleRecord(
        byte[] target,
        int offset,
        EmfPlusRecordType type,
        int left,
        int top,
        int right,
        int bottom)
    {
        WriteUInt32(target, offset, (uint)type);
        WriteUInt32(target, offset + 4, 24);
        WriteInt32(target, offset + 8, left);
        WriteInt32(target, offset + 12, top);
        WriteInt32(target, offset + 16, right);
        WriteInt32(target, offset + 20, bottom);
    }

    private static void WritePoint(byte[] target, int offset, int x, int y)
    {
        WriteInt32(target, offset, x);
        WriteInt32(target, offset + 4, y);
    }

    private static void WriteUInt16(byte[] target, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), value);

    private static void WriteInt16(byte[] target, int offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(target.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), value);

    private static void WriteInt32(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), value);

    private static void WriteSingle(byte[] target, int offset, float value) =>
        WriteInt32(target, offset, BitConverter.SingleToInt32Bits(value));

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

    private sealed class NonSeekableWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        internal byte[] ToArray() => _inner.ToArray();
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

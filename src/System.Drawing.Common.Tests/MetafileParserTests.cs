using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
            Assert.Contains(nameof(EmfPlusRecordType.WmfStretchBlt), exception.Message, StringComparison.Ordinal);
            Assert.Contains("byte offset", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(16, 16).ToArgb());
    }

    [Fact]
    public void WmfTextOutUsesSelectedFontColorsAndRestoredTextState()
    {
        using var metafile = new Metafile(new MemoryStream(CreateTextPlaybackWmf()));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.True(CountPixels(target, new Rectangle(2, 2, 28, 18), IsMostlyRed) > 4);
        Assert.True(CountPixels(target, new Rectangle(2, 22, 28, 18), IsMostlyGreen) > 4);
        Assert.True(CountPixels(target, new Rectangle(32, 2, 28, 18), IsMostlyRed) > 4);
    }

    [Fact]
    public void WmfTextOutPaintsMeasuredOpaqueBackground()
    {
        using var metafile = new Metafile(new MemoryStream(CreateOpaqueTextPlaybackWmf()));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        var textBounds = new Rectangle(2, 2, 36, 22);
        Assert.True(CountPixels(target, textBounds, IsMostlyBlue) > 4);
        Assert.True(CountPixels(target, textBounds, IsMostlyYellow) > 4);
    }

    [Fact]
    public void WmfInvalidTextAlignmentRollsBackEarlierText()
    {
        using var metafile = new Metafile(new MemoryStream(
            CreateTextPlaybackWmf(includeInvalidAlignment: true)));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfTextOut), exception.Message, StringComparison.Ordinal);
            Assert.Contains("0x0004", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(8, 8).ToArgb());
    }

    [Fact]
    public void WmfExtTextOutAppliesExplicitOpaqueAndClipRectangle()
    {
        using var metafile = new Metafile(new MemoryStream(CreateExtTextPlaybackWmf()));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        var rectangle = new Rectangle(4, 4, 18, 16);
        Assert.True(CountPixels(target, rectangle, IsMostlyBlue) > 4);
        Assert.True(CountPixels(target, rectangle, IsMostlyYellow) > 4);
        Assert.Equal(0, target.GetPixel(24, 10).A);
        Assert.True(IsMostlyGreen(target.GetPixel(40, 10)));
    }

    [Fact]
    public void WmfExtTextOutUsesPerCharacterAdvancesAndUpdatesCurrentPoint()
    {
        using var metafile = new Metafile(new MemoryStream(CreateAdvancedTextPlaybackWmf()));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.True(CountPixels(target, new Rectangle(2, 2, 14, 18), IsMostlyRed) > 2);
        Assert.True(CountPixels(target, new Rectangle(22, 2, 14, 18), IsMostlyRed) > 2);
        Assert.True(CountPixels(target, new Rectangle(42, 2, 18, 18), IsMostlyGreen) > 2);
        Assert.Equal(0, target.GetPixel(18, 10).A);
    }

    [Fact]
    public void WmfExtTextOutUnsupportedOptionRollsBackEarlierText()
    {
        using var metafile = new Metafile(new MemoryStream(
            CreateExtTextPlaybackWmf(includeUnsupportedOption: true)));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Cyan);
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfExtTextOut), exception.Message, StringComparison.Ordinal);
            Assert.Contains("0x0010", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Cyan.ToArgb(), target.GetPixel(8, 8).ToArgb());
    }

    [Fact]
    public void WmfExtTextOutRejectsIncompleteAdvanceArrayWithoutPublishing()
    {
        byte[] malformed = WmfExtTextOut(
            "MM",
            new Point(4, 4),
            options: 0,
            rectangle: Rectangle.Empty,
            advances: [20, 20]);
        Array.Resize(ref malformed, malformed.Length - 2);
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x0A32, malformed),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Magenta);
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfExtTextOut), exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Magenta.ToArgb(), target.GetPixel(8, 8).ToArgb());
    }

    [Fact]
    public void WmfRoundRectangleDrawsTypedSelectedObjects()
    {
        using var metafile = new Metafile(new MemoryStream(CreateRoundRectanglePlaybackWmf()));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(32, 32).ToArgb());
        Color outlinePixel = target.GetPixel(32, 12);
        Assert.Equal(byte.MaxValue, outlinePixel.A);
        Assert.True(outlinePixel.G < Color.Green.G);
        Assert.Equal(0, target.GetPixel(12, 12).A);
    }

    [Fact]
    public void WmfRoundRectangleZeroCornerSizeFallsBackToRectangle()
    {
        byte[] bytes = CreateRoundRectanglePlaybackWmf();
        int roundRectangleDataOffset;
        using (var parsed = new Metafile(new MemoryStream(bytes, writable: false)))
        {
            roundRectangleDataOffset = Assert.Single(
                parsed.Records.ToArray(),
                record => record.Type == EmfPlusRecordType.WmfRoundRect).DataOffset;
        }
        WriteInt16(bytes, roundRectangleDataOffset, 0);
        WriteInt16(bytes, roundRectangleDataOffset + 2, 0);

        using var metafile = new Metafile(new MemoryStream(bytes, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(32, 32).ToArgb());
        Assert.True(target.GetPixel(12, 12).A > 0);
    }

    [Fact]
    public void WmfRoundRectangleRejectsUnorderedBoundsWithoutPublishingCommands()
    {
        byte[] bytes = CreateRoundRectanglePlaybackWmf();
        int roundRectangleDataOffset;
        using (var parsed = new Metafile(new MemoryStream(bytes, writable: false)))
        {
            roundRectangleDataOffset = Assert.Single(
                parsed.Records.ToArray(),
                record => record.Type == EmfPlusRecordType.WmfRoundRect).DataOffset;
        }
        short left = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(roundRectangleDataOffset + 10, 2));
        WriteInt16(bytes, roundRectangleDataOffset + 6, left);

        using var metafile = new Metafile(new MemoryStream(bytes, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfRoundRect), exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(32, 32).ToArgb());
    }

    [Fact]
    public void WmfPieAndChordDrawTypedSelectedObjects()
    {
        using var metafile = new Metafile(new MemoryStream(CreateFilledArcPlaybackWmf()));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(22, 10).ToArgb());
        Assert.Equal(0, target.GetPixel(10, 22).A);
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(58, 8).ToArgb());
        Assert.Equal(0, target.GetPixel(42, 22).A);
    }

    [Fact]
    public void WmfChordRejectsUnorderedBoundsWithoutPublishingEarlierPie()
    {
        byte[] bytes = CreateFilledArcPlaybackWmf();
        int chordDataOffset;
        using (var parsed = new Metafile(new MemoryStream(bytes, writable: false)))
        {
            chordDataOffset = Assert.Single(
                parsed.Records.ToArray(),
                record => record.Type == EmfPlusRecordType.WmfChord).DataOffset;
        }
        short left = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(chordDataOffset + 14, 2));
        WriteInt16(bytes, chordDataOffset + 10, left);

        using var metafile = new Metafile(new MemoryStream(bytes, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfChord), exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(22, 10).ToArgb());
    }

    [Fact]
    public void WmfLineToTracksAndRestoresCurrentPointAndSetPixelUsesDeviceSize()
    {
        using var metafile = new Metafile(new MemoryStream(CreateLinePixelPlaybackWmf()));
        using var target = new Bitmap(128, 128);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 128, 128));
        }

        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(16, 8).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(24, 16).ToArgb());
        Color restoredCurrentPointPixel = target.GetPixel(16, 16);
        Assert.True(restoredCurrentPointPixel.A > 0);
        Assert.Equal((0, 0, 0),
            (restoredCurrentPointPixel.R, restoredCurrentPointPixel.G, restoredCurrentPointPixel.B));
        Assert.Equal(Color.Magenta.ToArgb(), target.GetPixel(40, 40).ToArgb());
        Assert.Equal(0, target.GetPixel(41, 40).A);
    }

    [Fact]
    public void WmfUnsupportedRecordAfterLineAndPixelRollsBackBothCommands()
    {
        using var metafile = new Metafile(new MemoryStream(
            CreateLinePixelPlaybackWmf(includeUnsupportedRecord: true)));
        using var target = new Bitmap(128, 128);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 128, 128)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfStretchBlt), exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(16, 8).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(40, 40).ToArgb());
    }

    [Fact]
    public void WmfPolyPolygonDrawsEachClosedPolygonWithoutUpdatingCurrentPoint()
    {
        using var metafile = new Metafile(new MemoryStream(CreatePolyPolygonPlaybackWmf()));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(16, 16).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(48, 16).ToArgb());
        Assert.Equal(0, target.GetPixel(32, 16).A);
        Color preservedCurrentPointPixel = target.GetPixel(10, 60);
        Assert.True(preservedCurrentPointPixel.A > 0);
        Assert.Equal((0, 0, 0),
            (preservedCurrentPointPixel.R, preservedCurrentPointPixel.G, preservedCurrentPointPixel.B));
    }

    [Fact]
    public void WmfPolyPolygonRejectsInvalidPerPolygonCountWithoutPublishingCommands()
    {
        byte[] bytes = CreatePolyPolygonPlaybackWmf();
        int polyPolygonDataOffset;
        using (var parsed = new Metafile(new MemoryStream(bytes, writable: false)))
        {
            polyPolygonDataOffset = Assert.Single(
                parsed.Records.ToArray(),
                record => record.Type == EmfPlusRecordType.WmfPolyPolygon).DataOffset;
        }
        WriteUInt16(bytes, polyPolygonDataOffset + 2, 1);

        using var metafile = new Metafile(new MemoryStream(bytes, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfPolyPolygon), exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(16, 16).ToArgb());
    }

    [Fact]
    public void WmfUnsupportedRecordAfterPolyPolygonRollsBackAllPolygons()
    {
        using var metafile = new Metafile(new MemoryStream(
            CreatePolyPolygonPlaybackWmf(includeUnsupportedRecord: true)));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfStretchBlt), exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(16, 16).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(48, 16).ToArgb());
    }

    [Fact]
    public void WmfViewportWindowOffsetAndScaleRecordsComposeAndRestore()
    {
        using var metafile = new Metafile(new MemoryStream(CreateMappedPixelPlaybackWmf()));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(8, 8).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(22, 22).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(20, 25).ToArgb());
        Assert.Equal(Color.Magenta.ToArgb(), target.GetPixel(38, 38).ToArgb());
        Assert.Equal(Color.Yellow.ToArgb(), target.GetPixel(16, 16).ToArgb());
    }

    [Fact]
    public void WmfScaleWindowExtentRejectsZeroDenominatorWithoutPublishingPixels()
    {
        byte[] bytes = CreateMappedPixelPlaybackWmf();
        int scaleDataOffset;
        using (var parsed = new Metafile(new MemoryStream(bytes, writable: false)))
        {
            scaleDataOffset = Assert.Single(
                parsed.Records.ToArray(),
                record => record.Type == EmfPlusRecordType.WmfScaleWindowExt).DataOffset;
        }
        WriteInt16(bytes, scaleDataOffset, 0);

        using var metafile = new Metafile(new MemoryStream(bytes, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Cyan);
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfScaleWindowExt), exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Cyan.ToArgb(), target.GetPixel(8, 8).ToArgb());
        Assert.Equal(Color.Cyan.ToArgb(), target.GetPixel(22, 22).ToArgb());
    }

    [Fact]
    public void WmfPatBltDrawsPatternCopyBlacknessAndWhiteness()
    {
        using var metafile = new Metafile(new MemoryStream(CreatePatBltPlaybackWmf()));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(12, 12).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(32, 12).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(52, 12).ToArgb());
        Assert.Equal(0, target.GetPixel(2, 2).A);
    }

    [Fact]
    public void WmfDestinationDependentPatBltRollsBackEarlierPatternCopy()
    {
        using var metafile = new Metafile(new MemoryStream(
            CreatePatBltPlaybackWmf(includeDestinationDependentRecord: true)));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfPatBlt), exception.Message, StringComparison.Ordinal);
            Assert.Contains("0x005A0049", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(12, 12).ToArgb());
    }

    [Fact]
    public void WmfOffsetClipRegionMovesAndRestoresTypedClip()
    {
        using var metafile = new Metafile(new MemoryStream(CreateOffsetClipPlaybackWmf()));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(12, 12).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(28, 28).ToArgb());
        Assert.Equal(0, target.GetPixel(44, 44).A);
    }

    [Fact]
    public void WmfUnsupportedRecordAfterOffsetClipRollsBackAllClipScopes()
    {
        using var metafile = new Metafile(new MemoryStream(
            CreateOffsetClipPlaybackWmf(includeUnsupportedRecord: true)));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.WmfStretchBlt), exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(12, 12).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(28, 28).ToArgb());
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
            records.Add((0x0B23, []));
        }
        records.Add((0, []));

        return CreatePlaybackWmf(records);
    }

    private static byte[] CreateRoundRectanglePlaybackWmf()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x020C, WmfWords(64, 64)),
            (0x020B, WmfWords(0, 0)),
            (0x02FC, WmfBrush(Color.Green)),
            (0x02FA, WmfPen(Color.Black, 1)),
            (0x012D, WmfWords(0)),
            (0x012D, WmfWords(1)),
            (0x061C, WmfWords(16, 16, 52, 52, 12, 12)),
            (0, [])
        };
        return CreatePlaybackWmf(records);
    }

    private static byte[] CreateTextPlaybackWmf(bool includeInvalidAlignment = false)
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x012E, WmfWords(0)),
            (0x0209, WmfColor(Color.Red)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x0521, WmfTextOut("WMF", new Point(4, 4))),
            (0x001E, []),
            (0x0209, WmfColor(Color.Green)),
            (0x0521, WmfTextOut("WMF", new Point(4, 24))),
            (0x0127, WmfWords(-1)),
            (0x0521, WmfTextOut("WMF", new Point(34, 4)))
        };
        if (includeInvalidAlignment)
        {
            records.Add((0x012E, WmfWords(4)));
            records.Add((0x0521, WmfTextOut("WMF", new Point(4, 44))));
        }
        records.Add((0, []));
        return CreatePlaybackWmf(records);
    }

    private static byte[] CreateOpaqueTextPlaybackWmf()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(2)),
            (0x0201, WmfColor(Color.Blue)),
            (0x0209, WmfColor(Color.Yellow)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x0521, WmfTextOut("WMF", new Point(4, 4))),
            (0, [])
        };
        return CreatePlaybackWmf(records);
    }

    private static byte[] CreateExtTextPlaybackWmf(bool includeUnsupportedOption = false)
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x0201, WmfColor(Color.Blue)),
            (0x0209, WmfColor(Color.Yellow)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x0A32, WmfExtTextOut(
                "MMMM",
                new Point(4, 4),
                options: 0x0006,
                rectangle: Rectangle.FromLTRB(4, 4, 22, 20))),
            (0x0201, WmfColor(Color.Green)),
            (0x0A32, WmfExtTextOut(
                string.Empty,
                Point.Empty,
                options: 0x0002,
                rectangle: Rectangle.FromLTRB(30, 4, 50, 20)))
        };
        if (includeUnsupportedOption)
        {
            records.Add((0x0A32, WmfExtTextOut(
                "M",
                new Point(32, 32),
                options: 0x0010,
                rectangle: Rectangle.Empty)));
        }
        records.Add((0, []));
        return CreatePlaybackWmf(records);
    }

    private static byte[] CreateAdvancedTextPlaybackWmf()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x012E, WmfWords(1)),
            (0x0214, WmfWords(4, 4)),
            (0x0209, WmfColor(Color.Red)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x0A32, WmfExtTextOut(
                "MM",
                new Point(56, 56),
                options: 0,
                rectangle: Rectangle.Empty,
                advances: [20, 20])),
            (0x0209, WmfColor(Color.Green)),
            (0x0521, WmfTextOut("M", new Point(56, 56))),
            (0, [])
        };
        return CreatePlaybackWmf(records);
    }

    private static byte[] CreateFilledArcPlaybackWmf()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x020C, WmfWords(64, 64)),
            (0x020B, WmfWords(0, 0)),
            (0x02FC, WmfBrush(Color.Green)),
            (0x02FA, WmfPen(Color.Black, 1)),
            (0x012D, WmfWords(0)),
            (0x012D, WmfWords(1)),
            (0x081A, WmfWords(0, 16, 16, 32, 32, 32, 0, 0)),
            (0x0830, WmfWords(0, 48, 16, 64, 32, 64, 0, 32)),
            (0, [])
        };
        return CreatePlaybackWmf(records);
    }

    private static byte[] CreateLinePixelPlaybackWmf(bool includeUnsupportedRecord = false)
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x02FA, WmfPen(Color.Black, 1)),
            (0x012D, WmfWords(0)),
            (0x0214, WmfWords(4, 4)),
            (0x0213, WmfWords(4, 12)),
            (0x001E, []),
            (0x0213, WmfWords(12, 12)),
            (0x0127, WmfWords(-1)),
            (0x0213, WmfWords(12, 4)),
            (0x041F, WmfSetPixel(Color.Magenta, new Point(20, 20)))
        };
        if (includeUnsupportedRecord)
        {
            records.Add((0x0B23, []));
        }
        records.Add((0, []));
        return CreatePlaybackWmf(records);
    }

    private static byte[] CreatePolyPolygonPlaybackWmf(bool includeUnsupportedRecord = false)
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x020C, WmfWords(64, 64)),
            (0x020B, WmfWords(0, 0)),
            (0x02FC, WmfBrush(Color.Green)),
            (0x02FA, WmfPen(Color.Black, 1)),
            (0x012D, WmfWords(0)),
            (0x012D, WmfWords(1)),
            (0x0214, WmfWords(60, 2)),
            (0x0538, WmfPolyPolygon(
                [new Point(8, 8), new Point(24, 8), new Point(24, 24), new Point(8, 24)],
                [new Point(40, 8), new Point(56, 8), new Point(56, 24), new Point(40, 24)])),
            (0x0213, WmfWords(60, 20))
        };
        if (includeUnsupportedRecord)
        {
            records.Add((0x0B23, []));
        }
        records.Add((0, []));
        return CreatePlaybackWmf(records);
    }

    private static byte[] CreateMappedPixelPlaybackWmf()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0103, WmfWords(8)),
            (0x020C, WmfWords(64, 64)),
            (0x020B, WmfWords(0, 0)),
            (0x020E, WmfWords(64, 64)),
            (0x020D, WmfWords(0, 0)),
            (0x001E, []),
            (0x041F, WmfSetPixel(Color.Red, new Point(8, 8))),
            (0x020F, WmfWords(6, 4)),
            (0x0211, WmfWords(20, 10)),
            (0x041F, WmfSetPixel(Color.Green, new Point(16, 8))),
            (0x0410, WmfWords(1, 2, 1, 2)),
            (0x041F, WmfSetPixel(Color.Blue, new Point(24, 16))),
            (0x0412, WmfWords(1, 2, 1, 2)),
            (0x041F, WmfSetPixel(Color.Magenta, new Point(32, 24))),
            (0x0127, WmfWords(-1)),
            (0x041F, WmfSetPixel(Color.Yellow, new Point(16, 16))),
            (0, [])
        };
        return CreatePlaybackWmf(records);
    }

    private static byte[] CreatePatBltPlaybackWmf(bool includeDestinationDependentRecord = false)
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x02FC, WmfBrush(Color.Green)),
            (0x012D, WmfWords(0)),
            (0x061D, WmfPatBlt(0x00F0_0021, new Rectangle(4, 4, 16, 16))),
            (0x061D, WmfPatBlt(0x0000_0042, new Rectangle(24, 4, 16, 16))),
            (0x061D, WmfPatBlt(0x00FF_0062, new Rectangle(44, 4, 16, 16)))
        };
        if (includeDestinationDependentRecord)
        {
            records.Add((0x061D, WmfPatBlt(0x005A_0049, new Rectangle(4, 4, 16, 16))));
        }
        records.Add((0, []));
        return CreatePlaybackWmf(records);
    }

    private static byte[] CreateOffsetClipPlaybackWmf(bool includeUnsupportedRecord = false)
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x02FC, WmfBrush(Color.Green)),
            (0x012D, WmfWords(0)),
            (0x0416, WmfWords(24, 24, 8, 8)),
            (0x061D, WmfPatBlt(0x00F0_0021, new Rectangle(0, 0, 64, 64))),
            (0x001E, []),
            (0x0220, WmfWords(16, 16)),
            (0x061D, WmfPatBlt(0x00FF_0062, new Rectangle(0, 0, 64, 64))),
            (0x0127, WmfWords(-1)),
            (0x061D, WmfPatBlt(0x0000_0042, new Rectangle(0, 0, 64, 64)))
        };
        if (includeUnsupportedRecord)
        {
            records.Add((0x0B23, []));
        }
        records.Add((0, []));
        return CreatePlaybackWmf(records);
    }

    private static byte[] CreatePlaybackWmf(List<(ushort Function, byte[] Payload)> records)
    {
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

    private static byte[] WmfFont(short height, string faceName, byte charSet = 1)
    {
        byte[] bytes = new byte[50];
        WriteInt16(bytes, 0, height);
        WriteInt16(bytes, 8, 400);
        bytes[13] = charSet;
        byte[] faceBytes = Encoding.ASCII.GetBytes(faceName);
        faceBytes.AsSpan(0, Math.Min(faceBytes.Length, 31)).CopyTo(bytes.AsSpan(18));
        return bytes;
    }

    private static byte[] WmfTextOut(string text, Point point)
    {
        byte[] textBytes = Encoding.ASCII.GetBytes(text);
        int paddedLength = (textBytes.Length + 1) & ~1;
        byte[] bytes = new byte[checked(2 + paddedLength + 4)];
        WriteInt16(bytes, 0, checked((short)textBytes.Length));
        textBytes.CopyTo(bytes, 2);
        WriteInt16(bytes, 2 + paddedLength, checked((short)point.Y));
        WriteInt16(bytes, 2 + paddedLength + 2, checked((short)point.X));
        return bytes;
    }

    private static byte[] WmfExtTextOut(
        string text,
        Point point,
        ushort options,
        Rectangle rectangle,
        short[]? advances = null)
    {
        byte[] textBytes = Encoding.ASCII.GetBytes(text);
        int paddedLength = (textBytes.Length + 1) & ~1;
        bool hasRectangle = (options & 0x0006) != 0;
        int stringOffset = hasRectangle ? 16 : 8;
        byte[] bytes = new byte[checked(
            stringOffset + paddedLength + (advances?.Length ?? 0) * 2)];
        WriteInt16(bytes, 0, checked((short)point.Y));
        WriteInt16(bytes, 2, checked((short)point.X));
        WriteInt16(bytes, 4, checked((short)textBytes.Length));
        WriteUInt16(bytes, 6, options);
        if (hasRectangle)
        {
            WriteInt16(bytes, 8, checked((short)rectangle.Left));
            WriteInt16(bytes, 10, checked((short)rectangle.Top));
            WriteInt16(bytes, 12, checked((short)rectangle.Right));
            WriteInt16(bytes, 14, checked((short)rectangle.Bottom));
        }
        textBytes.CopyTo(bytes, stringOffset);
        if (advances is not null)
        {
            int advanceOffset = stringOffset + paddedLength;
            for (int index = 0; index < advances.Length; index++)
            {
                WriteInt16(bytes, advanceOffset + index * 2, advances[index]);
            }
        }
        return bytes;
    }

    private static int CountPixels(Bitmap bitmap, Rectangle bounds, Func<Color, bool> predicate)
    {
        int count = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                if (predicate(bitmap.GetPixel(x, y)))
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static bool IsMostlyRed(Color color) =>
        color.A > 0 && color.R > color.G * 2 && color.R > color.B * 2;

    private static bool IsMostlyGreen(Color color) =>
        color.A > 0 && color.G > color.R * 2 && color.G > color.B * 2;

    private static bool IsMostlyBlue(Color color) =>
        color.A > 0 && color.B > color.R * 2 && color.B > color.G * 2;

    private static bool IsMostlyYellow(Color color) =>
        color.A > 0 && color.R > color.B * 2 && color.G > color.B * 2;

    private static byte[] WmfSetPixel(Color color, Point point)
    {
        byte[] bytes = new byte[8];
        WmfColor(color).CopyTo(bytes, 0);
        WriteInt16(bytes, 4, checked((short)point.Y));
        WriteInt16(bytes, 6, checked((short)point.X));
        return bytes;
    }

    private static byte[] WmfPolyPolygon(params Point[][] polygons)
    {
        int pointCount = polygons.Sum(polygon => polygon.Length);
        byte[] bytes = new byte[checked(2 + polygons.Length * 2 + pointCount * 4)];
        WriteUInt16(bytes, 0, checked((ushort)polygons.Length));
        int cursor = 2;
        foreach (Point[] polygon in polygons)
        {
            WriteUInt16(bytes, cursor, checked((ushort)polygon.Length));
            cursor += 2;
        }
        foreach (Point[] polygon in polygons)
        {
            foreach (Point point in polygon)
            {
                WriteInt16(bytes, cursor, checked((short)point.X));
                WriteInt16(bytes, cursor + 2, checked((short)point.Y));
                cursor += 4;
            }
        }
        return bytes;
    }

    private static byte[] WmfPatBlt(uint rasterOperation, Rectangle rectangle)
    {
        byte[] bytes = new byte[12];
        WriteUInt32(bytes, 0, rasterOperation);
        WriteInt16(bytes, 4, checked((short)rectangle.Height));
        WriteInt16(bytes, 6, checked((short)rectangle.Width));
        WriteInt16(bytes, 8, checked((short)rectangle.Y));
        WriteInt16(bytes, 10, checked((short)rectangle.X));
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

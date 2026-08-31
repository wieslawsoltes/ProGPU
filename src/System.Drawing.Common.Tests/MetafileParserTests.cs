using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Scene;
using ProGPU.SystemDrawing;
using ProGPU.Vector;
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
    public void WmfExtTextOutBatchesExplicitAdvancesIntoOneShapedGlyphRun()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x0A32, WmfExtTextOut(
                "MM",
                new Point(4, 4),
                options: 0,
                rectangle: Rectangle.Empty,
                advances: [20, 20])),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand glyphRun = Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        Assert.NotNull(glyphRun.GlyphPositions);
        Assert.Equal(2, glyphRun.GlyphPositions.Length);
        Assert.Equal(20f, glyphRun.GlyphPositions[1].X - glyphRun.GlyphPositions[0].X, 3);
    }

    [Fact]
    public void WmfTextCharacterExtraIsSavedRestoredAndSpacesOneShapedRun()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x0108, WmfWords(8)),
            (0x001E, []),
            (0x0108, WmfWords(2)),
            (0x0521, WmfTextOut("MM", new Point(4, 4))),
            (0x0127, WmfWords(-1)),
            (0x0521, WmfTextOut("MM", new Point(4, 24))),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] glyphRuns = context.Commands
            .Where(static command => command.Type == RenderCommandType.DrawGlyphRun)
            .ToArray();
        Assert.Equal(2, glyphRuns.Length);
        Assert.All(glyphRuns, static run => Assert.Equal(2, run.GlyphPositions!.Length));
        Vector2[] temporaryPositions = glyphRuns[0].GlyphPositions!;
        Vector2[] restoredPositions = glyphRuns[1].GlyphPositions!;
        float temporarySpacing =
            temporaryPositions[1].X - temporaryPositions[0].X;
        float restoredSpacing =
            restoredPositions[1].X - restoredPositions[0].X;
        Assert.Equal(6f, restoredSpacing - temporarySpacing, 3);
    }

    [Fact]
    public void WmfTextCharacterExtraExpandsOpaqueBackgroundAndRightAlignment()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x012E, WmfWords(2)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x0108, WmfWords(2)),
            (0x0521, WmfTextOut("MM", new Point(60, 4))),
            (0x0108, WmfWords(8)),
            (0x0521, WmfTextOut("MM", new Point(60, 24))),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] backgrounds = context.Commands
            .Where(static command => command.Type == RenderCommandType.DrawRect)
            .ToArray();
        RenderCommand[] glyphRuns = context.Commands
            .Where(static command => command.Type == RenderCommandType.DrawGlyphRun)
            .ToArray();
        Assert.Equal(2, backgrounds.Length);
        Assert.Equal(2, glyphRuns.Length);
        Assert.Equal(12f, backgrounds[1].Rect.Width - backgrounds[0].Rect.Width, 3);
        Assert.Equal(-12f, glyphRuns[1].Position.X - glyphRuns[0].Position.X, 3);
    }

    [Fact]
    public void WmfExplicitAdvancesOverrideDefaultTextSpacing()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x0108, WmfWords(8)),
            (0x020A, WmfWords(2, 100)),
            (0x0A32, WmfExtTextOut(
                "MM",
                new Point(4, 4),
                options: 0,
                rectangle: Rectangle.Empty,
                advances: [20, 20])),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand glyphRun = Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        Assert.Equal(20f, glyphRun.GlyphPositions![1].X - glyphRun.GlyphPositions[0].X, 3);
    }

    [Fact]
    public void WmfTextJustificationIsSavedRestoredAndDistributesRemainder()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x020A, WmfWords(2, 5)),
            (0x001E, []),
            (0x020A, WmfWords(2, 4)),
            (0x0521, WmfTextOut("M M M", new Point(4, 4))),
            (0x0127, WmfWords(-1)),
            (0x0521, WmfTextOut("M M M", new Point(4, 24))),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] glyphRuns = context.Commands
            .Where(static command => command.Type == RenderCommandType.DrawGlyphRun)
            .ToArray();
        Assert.Equal(2, glyphRuns.Length);
        Vector2[] temporary = glyphRuns[0].GlyphPositions!;
        Vector2[] restored = glyphRuns[1].GlyphPositions!;
        Assert.Equal(5, temporary.Length);
        Assert.Equal(5, restored.Length);
        Assert.Equal(0f, restored[2].X - temporary[2].X, 3);
        Assert.Equal(1f, restored[4].X - temporary[4].X, 3);
    }

    [Fact]
    public void WmfTextJustificationCarriesRoundingErrorAcrossRuns()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x012E, WmfWords(1)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x0214, WmfWords(4, 4)),
            (0x020A, WmfWords(2, 5)),
            (0x0521, WmfTextOut("M ", Point.Empty)),
            (0x0521, WmfTextOut("M ", Point.Empty)),
            (0x020A, WmfWords(0, 0)),
            (0x0521, WmfTextOut("M", Point.Empty)),
            (0x0214, WmfWords(24, 4)),
            (0x020A, WmfWords(2, 4)),
            (0x0521, WmfTextOut("M ", Point.Empty)),
            (0x0521, WmfTextOut("M ", Point.Empty)),
            (0x020A, WmfWords(0, 0)),
            (0x0521, WmfTextOut("M", Point.Empty)),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] textCommands = context.Commands
            .Where(static command => command.Type is
                RenderCommandType.DrawGlyphRun or RenderCommandType.DrawText)
            .ToArray();
        Assert.Equal(6, textCommands.Length);
        Assert.Equal(1f, textCommands[2].Position.X - textCommands[5].Position.X, 3);
    }

    [Fact]
    public void WmfTextJustificationRoundsTotalThroughNonTextMapMode()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0103, WmfWords(8)),
            (0x020C, WmfWords(1, 3)),
            (0x020E, WmfWords(1, 2)),
            (0x0102, WmfWords(1)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x020A, WmfWords(2, 3)),
            (0x0521, WmfTextOut("M M M", new Point(4, 4))),
            (0x020A, WmfWords(2, 5)),
            (0x0521, WmfTextOut("M M M", new Point(4, 24))),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] glyphRuns = context.Commands
            .Where(static command => command.Type == RenderCommandType.DrawGlyphRun)
            .ToArray();
        Assert.Equal(2, glyphRuns.Length);
        Vector2[] exactTwoPixels = glyphRuns[0].GlyphPositions!;
        Vector2[] roundedThreePixels = glyphRuns[1].GlyphPositions!;
        Assert.Equal(0f, roundedThreePixels[2].X - exactTwoPixels[2].X, 3);
        Assert.Equal(1.5f, roundedThreePixels[4].X - exactTwoPixels[4].X, 3);
    }

    [Fact]
    public void WmfTextCharacterExtraRoundsThroughNonTextMapMode()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0103, WmfWords(8)),
            (0x020C, WmfWords(1, 3)),
            (0x020E, WmfWords(1, 2)),
            (0x0102, WmfWords(1)),
            (0x02FB, WmfFont(-14, SystemFonts.DefaultFont.Name)),
            (0x012D, WmfWords(0)),
            (0x0108, WmfWords(1)),
            (0x0521, WmfTextOut("MM", new Point(4, 4))),
            (0x0108, WmfWords(3)),
            (0x0521, WmfTextOut("MM", new Point(4, 24))),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] glyphRuns = context.Commands
            .Where(static command => command.Type == RenderCommandType.DrawGlyphRun)
            .ToArray();
        Assert.Equal(2, glyphRuns.Length);
        Vector2[] roundedPositions = glyphRuns[0].GlyphPositions!;
        Vector2[] exactPositions = glyphRuns[1].GlyphPositions!;
        float roundedOne = roundedPositions[1].X - roundedPositions[0].X;
        float exactThree = exactPositions[1].X - exactPositions[0].X;
        Assert.Equal(1.5f, exactThree - roundedOne, 3);
    }

    [Fact]
    public void WmfSelectedFontRecordsUnderlineAndStrikeout()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x02FB, WmfFont(
                -14,
                SystemFonts.DefaultFont.Name,
                underline: true,
                strikeout: true,
                escapement: 900)),
            (0x012D, WmfWords(0)),
            (0x0A32, WmfExtTextOut(
                "WM",
                new Point(4, 4),
                options: 0,
                rectangle: Rectangle.Empty,
                advances: [20, 20])),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] decorations = context.Commands
            .Where(static command => command.Type == RenderCommandType.DrawRect)
            .ToArray();
        Assert.Equal(2, decorations.Length);
        RenderCommand glyphRun = Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        Vector3 baseline = Vector3.TransformNormal(Vector3.UnitX, glyphRun.Transform);
        Assert.InRange(MathF.Abs(baseline.X), 0f, 0.001f);
        Assert.InRange(baseline.Y, -1.001f, -0.999f);
        Assert.All(decorations, decoration => Assert.Equal(glyphRun.Transform, decoration.Transform));
    }

    [Fact]
    public void WmfRotatedTextUpdatesCurrentPointAlongEscapement()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x012E, WmfWords(1)),
            (0x0214, WmfWords(56, 48)),
            (0x02FB, WmfFont(
                -14,
                SystemFonts.DefaultFont.Name,
                escapement: 900)),
            (0x012D, WmfWords(0)),
            (0x0A32, WmfExtTextOut(
                "MM",
                Point.Empty,
                options: 0,
                rectangle: Rectangle.Empty,
                advances: [12, 12])),
            (0x0521, WmfTextOut("M", Point.Empty)),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] textCommands = context.Commands
            .Where(static command => command.Type is
                RenderCommandType.DrawGlyphRun or RenderCommandType.DrawText)
            .ToArray();
        Assert.Equal(2, textCommands.Length);
        Assert.Equal(48f, textCommands[0].Position.X, 3);
        Assert.Equal(56f, textCommands[0].Position.Y, 3);
        Assert.Equal(48f, textCommands[1].Position.X, 3);
        Assert.Equal(32f, textCommands[1].Position.Y, 3);
        Assert.All(textCommands, command =>
        {
            Vector3 baseline = Vector3.TransformNormal(Vector3.UnitX, command.Transform);
            Assert.InRange(MathF.Abs(baseline.X), 0f, 0.001f);
            Assert.InRange(baseline.Y, -1.001f, -0.999f);
        });

        using var target = new Bitmap(64, 64);
        using (Graphics bitmapGraphics = Graphics.FromImage(target))
        {
            bitmapGraphics.Clear(Color.Transparent);
            bitmapGraphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }
        Rectangle paintedBounds = GetPaintedBounds(target);
        Assert.True(paintedBounds.Height > paintedBounds.Width);
    }

    [Fact]
    public void WmfRotatedDefaultTextSpacingUpdatesCurrentPointAlongEscapement()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0102, WmfWords(1)),
            (0x012E, WmfWords(1)),
            (0x0214, WmfWords(56, 48)),
            (0x02FB, WmfFont(
                -14,
                SystemFonts.DefaultFont.Name,
                escapement: 900)),
            (0x012D, WmfWords(0)),
            (0x0108, WmfWords(6)),
            (0x020A, WmfWords(1, 4)),
            (0x0521, WmfTextOut("M M", Point.Empty)),
            (0x0108, WmfWords(0)),
            (0x020A, WmfWords(0, 0)),
            (0x0521, WmfTextOut("M", Point.Empty)),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        using var expectedFont = new Font(
            SystemFonts.DefaultFont.Name,
            14f,
            FontStyle.Regular,
            GraphicsUnit.Pixel);
        float expectedAdvance = graphics.MeasureString("M M", expectedFont).Width + 22f;

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] textCommands = context.Commands
            .Where(static command => command.Type is
                RenderCommandType.DrawGlyphRun or RenderCommandType.DrawText)
            .ToArray();
        Assert.Equal(2, textCommands.Length);
        Assert.Equal(48f, textCommands[0].Position.X, 3);
        Assert.Equal(56f, textCommands[0].Position.Y, 3);
        Assert.Equal(48f, textCommands[1].Position.X, 3);
        Assert.Equal(MathF.Round(56f - expectedAdvance), textCommands[1].Position.Y, 3);
    }

    [Fact]
    public void WmfMalformedTextCharacterExtraRollsBackEarlierText()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0521, WmfTextOut("M", new Point(4, 4))),
            (0x0108, WmfWords(2, 3)),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.WmfSetTextCharExtra), exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void WmfMalformedTextJustificationRollsBackEarlierText()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x0521, WmfTextOut("M", new Point(4, 4))),
            (0x020A, WmfWords(2)),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.WmfSetTextJustification), exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void WmfIndependentFontOrientationFailsWithoutPublishing()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x02FB, WmfFont(
                -14,
                SystemFonts.DefaultFont.Name,
                escapement: 900,
                orientation: 0)),
            (0, [])
        };
        using var metafile = new Metafile(new MemoryStream(CreatePlaybackWmf(records)));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.WmfCreateFontIndirect), exception.Message);
        Assert.Empty(context.Commands);
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
        WriteUInt32(bytes, 204, (uint)EmfPlusRecordType.EmfSetLayout);
        using var metafile = new Metafile(new MemoryStream(bytes));
        using var target = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 16, 16)));
            Assert.Contains(nameof(EmfPlusRecordType.EmfSetLayout), exception.Message, StringComparison.Ordinal);
            Assert.Contains("byte offset 204", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(4, 4).ToArgb());
    }

    [Fact]
    public void EmfExtTextOutWUsesSelectedUnicodeFontColorAnd32BitAdvances()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfSetTextColor, EmfUInt32(0x0000_00FF)),
            (EmfPlusRecordType.EmfSetTextAlign, EmfInt32(1)),
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(4, 4)),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW(
                    "M\u03A9",
                    Point.Empty,
                    0,
                    Rectangle.Empty,
                    [20, 24],
                    stringPadding: 2)),
            (EmfPlusRecordType.EmfSetTextColor, EmfUInt32(0x0000_FF00)),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW("M", Point.Empty, 0, Rectangle.Empty, null))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] textCommands = context.Commands
            .Where(static command =>
                command.Type is RenderCommandType.DrawGlyphRun or RenderCommandType.DrawText)
            .ToArray();
        Assert.Equal(2, textCommands.Length);
        ushort[] glyphIndices = Assert.IsType<ushort[]>(textCommands[0].GlyphIndices);
        Vector2[] glyphPositions = Assert.IsType<Vector2[]>(textCommands[0].GlyphPositions);
        Assert.Equal(2, glyphIndices.Length);
        Assert.NotEqual(glyphIndices[0], glyphIndices[1]);
        Assert.Equal(20f,
            glyphPositions[1].X - glyphPositions[0].X,
            3);
        Assert.Equal(48f, textCommands[1].Position.X, 3);
        var firstBrush = Assert.IsType<ProGPU.Vector.SolidColorBrush>(textCommands[0].Brush);
        var secondBrush = Assert.IsType<ProGPU.Vector.SolidColorBrush>(textCommands[1].Brush);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), firstBrush.Color);
        Assert.Equal(new Vector4(0f, 1f, 0f, 1f), secondBrush.Color);
    }

    [Fact]
    public void EmfTextJustificationIsSavedRestoredAndDistributesRemainder()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfSetTextJustification, EmfTextJustification(5, 2)),
            (EmfPlusRecordType.EmfSaveDC, []),
            (EmfPlusRecordType.EmfSetTextJustification, EmfTextJustification(4, 2)),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW("M M M", new Point(4, 4), 0, Rectangle.Empty, null)),
            (EmfPlusRecordType.EmfRestoreDC, EmfInt32(-1)),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW("M M M", new Point(4, 24), 0, Rectangle.Empty, null))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] glyphRuns = context.Commands
            .Where(static command => command.Type == RenderCommandType.DrawGlyphRun)
            .ToArray();
        Assert.Equal(2, glyphRuns.Length);
        Vector2[] temporary = Assert.IsType<Vector2[]>(glyphRuns[0].GlyphPositions);
        Vector2[] restored = Assert.IsType<Vector2[]>(glyphRuns[1].GlyphPositions);
        Assert.Equal(5, temporary.Length);
        Assert.Equal(5, restored.Length);
        Assert.Equal(0f, restored[2].X - temporary[2].X, 3);
        Assert.Equal(1f, restored[4].X - temporary[4].X, 3);
    }

    [Fact]
    public void EmfExtTextOutWPdyUsesTwoDimensionalCellsAndUpdatesCurrentPoint()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfSetTextAlign, EmfInt32(1)),
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(4, 4)),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutWPdy(
                    "M\u03A9",
                    Point.Empty,
                    [new Point(20, 5), new Point(24, 7)])),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW("M", Point.Empty, 0, Rectangle.Empty, null))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] textCommands = context.Commands
            .Where(static command => command.Type is
                RenderCommandType.DrawGlyphRun or RenderCommandType.DrawText)
            .ToArray();
        Assert.Equal(2, textCommands.Length);
        Vector2[] positions = Assert.IsType<Vector2[]>(textCommands[0].GlyphPositions);
        Assert.Equal(2, positions.Length);
        Assert.Equal(20f, positions[1].X - positions[0].X, 3);
        Assert.Equal(5f, positions[1].Y - positions[0].Y, 3);
        Assert.Equal(48f, textCommands[1].Position.X, 3);
        Assert.Equal(16f, textCommands[1].Position.Y, 3);
    }

    [Fact]
    public void EmfExtTextOutWPdyRejectsOutOfRangeCellWithoutPublishing()
    {
        byte[] malformed = EmfExtTextOutWPdy(
            "M",
            new Point(4, 4),
            [new Point(10, 2)]);
        WriteUInt32(malformed, 76, 0x8000_0000);
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
            (EmfPlusRecordType.EmfExtTextOutW, malformed)
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfExtTextOutW), exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void EmfExtTextOutWGlyphIndexPreservesSelectedFontIdsAndCells()
    {
        ushort firstGlyph = SystemFonts.DefaultFont.TtfFont.GetGlyphIndex('M');
        ushort secondGlyph = SystemFonts.DefaultFont.TtfFont.GetGlyphIndex('\u03A9');
        Assert.NotEqual((ushort)0, firstGlyph);
        Assert.NotEqual((ushort)0, secondGlyph);
        string encodedGlyphs = new([(char)firstGlyph, (char)secondGlyph]);
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfSetTextAlign, EmfInt32(1)),
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(4, 4)),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW(
                    encodedGlyphs,
                    Point.Empty,
                    0x0000_0010,
                    Rectangle.Empty,
                    [20, 24])),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW("M", Point.Empty, 0, Rectangle.Empty, null))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] textCommands = context.Commands
            .Where(static command => command.Type is
                RenderCommandType.DrawGlyphRun or RenderCommandType.DrawText)
            .ToArray();
        Assert.Equal(2, textCommands.Length);
        Assert.Equal(
            new[] { firstGlyph, secondGlyph },
            Assert.IsType<ushort[]>(textCommands[0].GlyphIndices));
        Vector2[] positions = Assert.IsType<Vector2[]>(textCommands[0].GlyphPositions);
        Assert.Equal(20f, positions[1].X - positions[0].X, 3);
        Assert.Equal(48f, textCommands[1].Position.X, 3);
        Assert.Equal(4f, textCommands[1].Position.Y, 3);
    }

    [Fact]
    public void EmfExtTextOutWGlyphIndexUsesSelectedFontNaturalCells()
    {
        ushort firstGlyph = SystemFonts.DefaultFont.TtfFont.GetGlyphIndex('M');
        ushort secondGlyph = SystemFonts.DefaultFont.TtfFont.GetGlyphIndex('\u03A9');
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW(
                    new string([(char)firstGlyph, (char)secondGlyph]),
                    new Point(4, 4),
                    0x0000_0010,
                    Rectangle.Empty,
                    null))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand glyphRun = Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        Assert.Equal(
            new[] { firstGlyph, secondGlyph },
            Assert.IsType<ushort[]>(glyphRun.GlyphIndices));
        Vector2[] positions = Assert.IsType<Vector2[]>(glyphRun.GlyphPositions);
        Assert.True(positions[1].X > positions[0].X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmfExtTextOutWGlyphIndexDecoratesHorizontalCells(bool naturalAdvances)
    {
        ushort firstGlyph = SystemFonts.DefaultFont.TtfFont.GetGlyphIndex('M');
        ushort secondGlyph = SystemFonts.DefaultFont.TtfFont.GetGlyphIndex('\u03A9');
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(
                    1,
                    -14,
                    SystemFonts.DefaultFont.Name,
                    underline: true,
                    strikeout: true)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW(
                    new string([(char)firstGlyph, (char)secondGlyph]),
                    new Point(4, 4),
                    0x0000_0010,
                    Rectangle.Empty,
                    naturalAdvances ? null : [20, 24]))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand glyphRun = Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        Assert.Equal(
            new[] { firstGlyph, secondGlyph },
            Assert.IsType<ushort[]>(glyphRun.GlyphIndices));
        RenderCommand[] decorations = context.Commands
            .Where(static command => command.Type == RenderCommandType.DrawRect)
            .ToArray();
        Assert.Equal(2, decorations.Length);
        Assert.All(decorations, decoration => Assert.Equal(glyphRun.Transform, decoration.Transform));
    }

    [Fact]
    public void EmfExtTextOutWGlyphIndexRetainsStoredOrderWhenLanguageProcessingIsDisabled()
    {
        ushort firstGlyph = SystemFonts.DefaultFont.TtfFont.GetGlyphIndex('M');
        ushort secondGlyph = SystemFonts.DefaultFont.TtfFont.GetGlyphIndex('\u03A9');
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfSetTextAlign, EmfInt32(0x0100)),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW(
                    new string([(char)firstGlyph, (char)secondGlyph]),
                    new Point(4, 4),
                    0x0000_1090,
                    Rectangle.Empty,
                    [20, 24]))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand glyphRun = Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        Assert.Equal(
            new[] { firstGlyph, secondGlyph },
            Assert.IsType<ushort[]>(glyphRun.GlyphIndices));
        Vector2[] positions = Assert.IsType<Vector2[]>(glyphRun.GlyphPositions);
        Assert.Equal(20f, positions[1].X - positions[0].X, 3);
    }

    [Fact]
    public void EmfExtTextOutWGlyphIndexRejectsDecoratedPdyWithoutPublishing()
    {
        ushort glyph = SystemFonts.DefaultFont.TtfFont.GetGlyphIndex('M');
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name, underline: true)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutWPdy(
                    new string((char)glyph, 1),
                    new Point(4, 4),
                    [new Point(10, 2)],
                    options: 0x0000_2010))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfExtTextOutW), exception.Message);
        Assert.Contains("per-cell decoration geometry", exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void EmfExtTextOutARejectsGlyphIndexStorageWithoutPublishing()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
            (EmfPlusRecordType.EmfExtTextOutA,
                EmfExtTextOutA(
                    "M",
                    new Point(4, 4),
                    0x0000_0010,
                    Rectangle.Empty,
                    [10],
                    codePage: 1252))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfExtTextOutA), exception.Message);
        Assert.Contains("ANSI EMF glyph-index", exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void EmfTextStateRestoresAndAppliesOpaqueClipRectangle()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfSetBkColor, EmfUInt32(0x00FF_0000)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfSetTextColor, EmfUInt32(0x0000_00FF)),
            (EmfPlusRecordType.EmfSaveDC, []),
            (EmfPlusRecordType.EmfSetTextColor, EmfUInt32(0x0000_FF00)),
            (EmfPlusRecordType.EmfRestoreDC, EmfInt32(-1)),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW(
                    "MMMM",
                    new Point(4, 4),
                    0x0000_1006,
                    new Rectangle(4, 4, 18, 16),
                    [12, 12, 12, 12]))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        var rectangle = new Rectangle(4, 4, 18, 16);
        Assert.True(CountPixels(target, rectangle, IsMostlyRed) > 2);
        Assert.True(CountPixels(target, rectangle, IsMostlyBlue) > 4);
        Assert.Equal(0, target.GetPixel(24, 10).A);
    }

    [Fact]
    public void EmfExtTextOutWRejectsInvalidRecordOffsetWithoutPublishingCommands()
    {
        foreach (uint invalidOffset in new uint[] { 72, 77 })
        {
            byte[] malformedText = EmfExtTextOutW(
                "M",
                new Point(4, 4),
                0,
                Rectangle.Empty,
                [12]);
            WriteUInt32(malformedText, 40, invalidOffset);
            byte[] emf = CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
                (EmfPlusRecordType.EmfExtTextOutW, malformedText)
            ]);
            using var metafile = new Metafile(new MemoryStream(emf));
            var context = new DrawingContext();
            using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

            Assert.Contains(nameof(EmfPlusRecordType.EmfExtTextOutW), exception.Message);
            Assert.Empty(context.Commands);
        }
    }

    [Fact]
    public void EmfExtTextOutWRejectsInvalidUtf16WithoutPublishingCommands()
    {
        byte[] malformedText = EmfExtTextOutW(
            "M",
            new Point(4, 4),
            0,
            Rectangle.Empty,
            null);
        WriteUInt16(malformedText, 68, 0xD800);
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
            (EmfPlusRecordType.EmfExtTextOutW, malformedText)
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfExtTextOutW), exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void EmfExtTextOutAUsesSelectedCharsetOddOffsetAnd32BitAdvances()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfSetTextColor, EmfUInt32(0x0000_00FF)),
            (EmfPlusRecordType.EmfExtTextOutA,
                EmfExtTextOutA(
                    "M\u20AC",
                    new Point(4, 4),
                    0,
                    Rectangle.Empty,
                    [20, 24],
                    codePage: 1252,
                    stringPadding: 1))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand glyphRun = Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        ushort[] glyphIndices = Assert.IsType<ushort[]>(glyphRun.GlyphIndices);
        Vector2[] glyphPositions = Assert.IsType<Vector2[]>(glyphRun.GlyphPositions);
        Assert.Equal(2, glyphIndices.Length);
        Assert.NotEqual(glyphIndices[0], glyphIndices[1]);
        Assert.Equal(20f, glyphPositions[1].X - glyphPositions[0].X, 3);
        var brush = Assert.IsType<ProGPU.Vector.SolidColorBrush>(glyphRun.Brush);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), brush.Color);
    }

    [Fact]
    public void EmfExtTextOutARejectsDbcsAdvancesWithoutPublishingCommands()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name, charSet: 128)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfExtTextOutA,
                EmfExtTextOutA(
                    "\u3042",
                    new Point(4, 4),
                    0,
                    Rectangle.Empty,
                    [10, 10],
                    codePage: 932))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfExtTextOutA), exception.Message);
        Assert.Contains("one-byte charset", exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void EmfExtTextOutADecodesDbcsWithoutExplicitAdvances()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name, charSet: 128)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfExtTextOutA,
                EmfExtTextOutA(
                    "\u3042",
                    new Point(4, 4),
                    0,
                    Rectangle.Empty,
                    null,
                    codePage: 932))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand textCommand = Assert.Single(
            context.Commands,
            static command => command.Type is
                RenderCommandType.DrawGlyphRun or RenderCommandType.DrawText);
        if (textCommand.Type == RenderCommandType.DrawGlyphRun)
        {
            Assert.Single(Assert.IsType<ushort[]>(textCommand.GlyphIndices));
        }
        else
        {
            Assert.Equal("\u3042", textCommand.Text);
        }
    }

    [Fact]
    public void EmfExtTextOutARejectsInvalidDbcsSequenceWithoutPublishingCommands()
    {
        byte[] malformedText = EmfExtTextOutA(
            "M",
            new Point(4, 4),
            0,
            Rectangle.Empty,
            null,
            codePage: 932);
        malformedText[68] = 0x81;
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name, charSet: 128)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfExtTextOutA, malformedText)
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfExtTextOutA), exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void EmfPolyTextOutWDrawsCountedUnicodeStringsWithIndependentCells()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfSetTextColor, EmfUInt32(0x0000_00FF)),
            (EmfPlusRecordType.EmfPolyTextOutW,
                EmfPolyTextOutW(
                    ("M\u03A9", new Point(4, 4), [20, 24]),
                    ("MM", new Point(8, 28), [12, 16])))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] glyphRuns = context.Commands
            .Where(static command => command.Type == RenderCommandType.DrawGlyphRun)
            .ToArray();
        Assert.Equal(2, glyphRuns.Length);
        ushort[] firstIndices = Assert.IsType<ushort[]>(glyphRuns[0].GlyphIndices);
        Vector2[] firstPositions = Assert.IsType<Vector2[]>(glyphRuns[0].GlyphPositions);
        Vector2[] secondPositions = Assert.IsType<Vector2[]>(glyphRuns[1].GlyphPositions);
        Assert.Equal(2, firstIndices.Length);
        Assert.NotEqual(firstIndices[0], firstIndices[1]);
        Assert.Equal(4f, glyphRuns[0].Position.X, 3);
        Assert.Equal(4f, glyphRuns[0].Position.Y, 3);
        Assert.Equal(0f, firstPositions[0].X, 3);
        Assert.Equal(20f, firstPositions[1].X - firstPositions[0].X, 3);
        Assert.Equal(8f, glyphRuns[1].Position.X, 3);
        Assert.Equal(28f, glyphRuns[1].Position.Y, 3);
        Assert.Equal(0f, secondPositions[0].X, 3);
        Assert.Equal(12f, secondPositions[1].X - secondPositions[0].X, 3);
    }

    [Fact]
    public void EmfPolyTextOutAUsesSelectedCharsetAndOddStringOffset()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfPolyTextOutA,
                EmfPolyTextOutA(
                    "M\u20AC",
                    new Point(5, 7),
                    [18, 22],
                    codePage: 1252,
                    stringPadding: 1))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand glyphRun = Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        ushort[] glyphIndices = Assert.IsType<ushort[]>(glyphRun.GlyphIndices);
        Vector2[] glyphPositions = Assert.IsType<Vector2[]>(glyphRun.GlyphPositions);
        Assert.Equal(2, glyphIndices.Length);
        Assert.NotEqual(glyphIndices[0], glyphIndices[1]);
        Assert.Equal(5f, glyphRun.Position.X, 3);
        Assert.Equal(7f, glyphRun.Position.Y, 3);
        Assert.Equal(0f, glyphPositions[0].X, 3);
        Assert.Equal(18f, glyphPositions[1].X - glyphPositions[0].X, 3);
    }

    [Fact]
    public void EmfPolyTextOutRejectsLaterDescriptorOverlapWithoutPublishingCommands()
    {
        byte[] malformed = EmfPolyTextOutW(
            ("M", new Point(4, 4), null),
            ("M", new Point(20, 4), null));
        WriteUInt32(malformed, 84, 80);
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
            (EmfPlusRecordType.EmfPolyTextOutW, malformed)
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfPolyTextOutW), exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void EmfSmallTextOutDecodesCompactUnicodeWithoutRectangle()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfSmallTextOut,
                EmfSmallTextOut("M\u03A9", new Point(6, 8), 0x0000_0100, Rectangle.Empty))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand textCommand = Assert.Single(
            context.Commands,
            static command => command.Type is
                RenderCommandType.DrawGlyphRun or RenderCommandType.DrawText);
        if (textCommand.Type == RenderCommandType.DrawGlyphRun)
        {
            ushort[] glyphIndices = Assert.IsType<ushort[]>(textCommand.GlyphIndices);
            Assert.Equal(2, glyphIndices.Length);
            Assert.NotEqual(glyphIndices[0], glyphIndices[1]);
        }
        else
        {
            Assert.Equal("M\u03A9", textCommand.Text);
        }
        Assert.Equal(6f, textCommand.Position.X, 3);
        Assert.Equal(8f, textCommand.Position.Y, 3);
    }

    [Fact]
    public void EmfSmallTextOutExpandsSmallCharactersAsUnicodeLowBytes()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name, charSet: 128)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfSmallTextOut,
                EmfSmallTextOut("M\u00E9", new Point(5, 7), 0x0000_0300, Rectangle.Empty))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand textCommand = Assert.Single(
            context.Commands,
            static command => command.Type is
                RenderCommandType.DrawGlyphRun or RenderCommandType.DrawText);
        if (textCommand.Type == RenderCommandType.DrawGlyphRun)
        {
            ushort[] glyphIndices = Assert.IsType<ushort[]>(textCommand.GlyphIndices);
            Assert.Equal(2, glyphIndices.Length);
            Assert.NotEqual(glyphIndices[0], glyphIndices[1]);
        }
        else
        {
            Assert.Equal("M\u00E9", textCommand.Text);
        }
        Assert.Equal(5f, textCommand.Position.X, 3);
        Assert.Equal(7f, textCommand.Position.Y, 3);
    }

    [Fact]
    public void EmfSmallTextOutUsesPresentBoundsForOpaqueClippedText()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetBkMode, EmfInt32(1)),
            (EmfPlusRecordType.EmfSetBkColor, EmfUInt32(0x00FF_0000)),
            (EmfPlusRecordType.EmfExtCreateFontIndirect,
                EmfFont(1, -14, SystemFonts.DefaultFont.Name)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfSetTextColor, EmfUInt32(0x0000_00FF)),
            (EmfPlusRecordType.EmfSmallTextOut,
                EmfSmallTextOut(
                    "MMMM",
                    new Point(4, 4),
                    0x0000_0006,
                    new Rectangle(4, 4, 18, 16)))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        var rectangle = new Rectangle(4, 4, 18, 16);
        Assert.True(CountPixels(target, rectangle, IsMostlyRed) > 2);
        Assert.True(CountPixels(target, rectangle, IsMostlyBlue) > 4);
        Assert.Equal(0, target.GetPixel(24, 10).A);
    }

    [Fact]
    public void EmfSmallTextOutRejectsContradictoryCompactBoundsWithoutPublishing()
    {
        byte[] malformed = EmfSmallTextOut(
            "M",
            new Point(4, 4),
            0x0000_0102,
            Rectangle.Empty);
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
            (EmfPlusRecordType.EmfSmallTextOut, malformed)
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfSmallTextOut), exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void EmfPieHonorsArcDirectionAndSavedState()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSaveDC, []),
            (EmfPlusRecordType.EmfSetArcDirection, EmfInt32(2)),
            (EmfPlusRecordType.EmfPie,
                EmfArc(
                    new Rectangle(4, 4, 24, 24),
                    new Point(28, 16),
                    new Point(16, 28))),
            (EmfPlusRecordType.EmfRestoreDC, EmfInt32(-1)),
            (EmfPlusRecordType.EmfPie,
                EmfArc(
                    new Rectangle(36, 4, 24, 24),
                    new Point(60, 16),
                    new Point(48, 28)))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.NotEqual(0, target.GetPixel(22, 22).A);
        Assert.Equal(0, target.GetPixel(10, 10).A);
        Assert.NotEqual(0, target.GetPixel(42, 10).A);
        Assert.Equal(0, target.GetPixel(54, 22).A);
    }

    [Fact]
    public void EmfArcAndChordPublishTypedPathCommands()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRoundArc,
                EmfArc(
                    new Rectangle(2, 2, 16, 16),
                    new Point(18, 10),
                    new Point(10, 18))),
            (EmfPlusRecordType.EmfChord,
                EmfArc(
                    new Rectangle(22, 2, 16, 16),
                    new Point(38, 10),
                    new Point(30, 18)))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 32));

        Assert.Equal(
            3,
            context.Commands.Count(static command => command.Type == RenderCommandType.DrawPath));
    }

    [Fact]
    public void EmfRoundRectangleAndSetPixelVRenderTypedGeometry()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRoundRect,
                EmfRoundRectangle(new Rectangle(2, 2, 20, 20), new Size(8, 8))),
            (EmfPlusRecordType.EmfSetPixelV,
                EmfSetPixel(new Point(30, 12), Color.Magenta))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(0, target.GetPixel(2, 2).A);
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(12, 12).ToArgb());
        Assert.Equal(Color.Magenta.ToArgb(), target.GetPixel(30, 12).ToArgb());
    }

    [Fact]
    public void EmfInvalidArcDirectionRollsBackEarlierGeometry()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
            (EmfPlusRecordType.EmfSetArcDirection, EmfInt32(3))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfSetArcDirection), exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void EmfBezierAndPolylineToFamiliesPreserveCurrentPositionContracts()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(4, 4)),
            (EmfPlusRecordType.EmfPolyBezierTo,
                EmfPointArray(
                    [new Point(8, 4), new Point(12, 12), new Point(16, 12)],
                    points16: false)),
            (EmfPlusRecordType.EmfPolylineTo16,
                EmfPointArray(
                    [new Point(20, 12), new Point(20, 16)],
                    points16: true)),
            (EmfPlusRecordType.EmfPolyBezier16,
                EmfPointArray(
                    [new Point(30, 4), new Point(34, 4), new Point(38, 12), new Point(42, 12)],
                    points16: true)),
            (EmfPlusRecordType.EmfLineTo, EmfPoint(24, 16))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] drawingCommands = context.Commands
            .Where(static command => command.Type is RenderCommandType.DrawPath or RenderCommandType.DrawLine)
            .ToArray();
        Assert.Equal(4, drawingCommands.Length);
        PathFigure first = Assert.Single(drawingCommands[0].Path!.Figures);
        Assert.Equal(new Vector2(4, 4), first.StartPoint);
        Assert.Equal(new Vector2(16, 12), Assert.IsType<CubicBezierSegment>(Assert.Single(first.Segments)).Point);
        PathFigure second = Assert.Single(drawingCommands[1].Path!.Figures);
        Assert.Equal(new Vector2(16, 12), second.StartPoint);
        Assert.Equal(new Vector2(20, 16), Assert.IsType<LineSegment>(second.Segments[^1]).Point);
        PathFigure third = Assert.Single(drawingCommands[2].Path!.Figures);
        Assert.Equal(new Vector2(30, 4), third.StartPoint);
        Assert.Equal(new Vector2(42, 12), Assert.IsType<CubicBezierSegment>(Assert.Single(third.Segments)).Point);
        Assert.Equal(RenderCommandType.DrawLine, drawingCommands[3].Type);
        Assert.Equal(new Vector2(20, 16), drawingCommands[3].Position);
        Assert.Equal(new Vector2(24, 16), drawingCommands[3].Position2);
    }

    [Fact]
    public void EmfCompactPointRecordsUseSigned16BitCoordinates()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfPolygon16,
                EmfPointArray(
                    [new Point(-2, 2), new Point(6, 2), new Point(6, 8)],
                    points16: true)),
            (EmfPlusRecordType.EmfPolyline16,
                EmfPointArray(
                    [new Point(10, 2), new Point(14, 6)],
                    points16: true)),
            (EmfPlusRecordType.EmfPolyPolygon16,
                EmfPolyPoly(
                    [3, 3],
                    [
                        new Point(18, 2), new Point(24, 2), new Point(21, 8),
                        new Point(28, 2), new Point(34, 2), new Point(31, 8)
                    ],
                    points16: true)),
            (EmfPlusRecordType.EmfPolyPolyline16,
                EmfPolyPoly(
                    [2, 2],
                    [new Point(38, 2), new Point(42, 6), new Point(46, 2), new Point(50, 6)],
                    points16: true))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        Assert.Equal(
            9,
            context.Commands.Count(static command => command.Type == RenderCommandType.DrawPath));
        RenderCommand firstPath = Assert.Single(
            context.Commands.Where(static command => command.Type == RenderCommandType.DrawPath).Take(1));
        Assert.Equal(new Vector2(-2, 2), Assert.Single(firstPath.Path!.Figures).StartPoint);
    }

    [Fact]
    public void EmfPolyDrawClosesToSavedMoveOriginAndUpdatesCurrentPosition()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(4, 4)),
            (EmfPlusRecordType.EmfPolyDraw,
                EmfPolyDraw(
                    [new Point(8, 4), new Point(8, 12)],
                    [0x02, 0x02],
                    points16: false)),
            (EmfPlusRecordType.EmfSaveDC, []),
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(30, 30)),
            (EmfPlusRecordType.EmfRestoreDC, EmfInt32(-1)),
            (EmfPlusRecordType.EmfPolyDraw16,
                EmfPolyDraw(
                    [new Point(12, 12), new Point(16, 8), new Point(12, 4)],
                    [0x04, 0x04, 0x05],
                    points16: true)),
            (EmfPlusRecordType.EmfLineTo, EmfPoint(4, 0))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] drawingCommands = context.Commands
            .Where(static command => command.Type is RenderCommandType.DrawPath or RenderCommandType.DrawLine)
            .ToArray();
        Assert.Equal(3, drawingCommands.Length);
        PathFigure curve = Assert.Single(drawingCommands[1].Path!.Figures);
        Assert.Equal(new Vector2(8, 12), curve.StartPoint);
        Assert.Equal(2, curve.Segments.Count);
        Assert.Equal(new Vector2(12, 4), Assert.IsType<CubicBezierSegment>(curve.Segments[0]).Point);
        Assert.Equal(new Vector2(4, 4), Assert.IsType<LineSegment>(curve.Segments[1]).Point);
        Assert.Equal(RenderCommandType.DrawLine, drawingCommands[2].Type);
        Assert.Equal(new Vector2(4, 4), drawingCommands[2].Position);
        Assert.Equal(new Vector2(4, 0), drawingCommands[2].Position2);
    }

    [Fact]
    public void EmfArcToConnectsToTheArcAndUpdatesCurrentPosition()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(4, 20)),
            (EmfPlusRecordType.EmfArcTo,
                EmfArc(
                    new Rectangle(8, 8, 24, 24),
                    new Point(32, 20),
                    new Point(20, 8))),
            (EmfPlusRecordType.EmfLineTo, EmfPoint(12, 8))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] drawingCommands = context.Commands
            .Where(static command => command.Type is RenderCommandType.DrawPath or RenderCommandType.DrawLine)
            .ToArray();
        Assert.Equal(2, drawingCommands.Length);
        PathFigure arcFigure = Assert.Single(drawingCommands[0].Path!.Figures);
        Assert.Equal(new Vector2(4, 20), arcFigure.StartPoint);
        Assert.Equal(new Vector2(32, 20), Assert.IsType<LineSegment>(arcFigure.Segments[0]).Point);
        ArcSegment arc = Assert.IsType<ArcSegment>(arcFigure.Segments[1]);
        Assert.Equal(new Vector2(20, 8), arc.Point);
        Assert.Equal(SweepDirection.Counterclockwise, arc.SweepDirection);
        Assert.Equal(new Vector2(20, 8), drawingCommands[1].Position);
        Assert.Equal(new Vector2(12, 8), drawingCommands[1].Position2);
    }

    [Fact]
    public void EmfAngleArcUsesCounterclockwiseAnglesAndUpdatesCurrentPosition()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(4, 20)),
            (EmfPlusRecordType.EmfAngleArc,
                EmfAngleArc(new Point(20, 20), 8, startAngle: 0f, sweepAngle: 90f)),
            (EmfPlusRecordType.EmfLineTo, EmfPoint(12, 12))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] drawingCommands = context.Commands
            .Where(static command => command.Type is RenderCommandType.DrawPath or RenderCommandType.DrawLine)
            .ToArray();
        Assert.Equal(2, drawingCommands.Length);
        PathFigure arcFigure = Assert.Single(drawingCommands[0].Path!.Figures);
        Assert.Equal(new Vector2(4, 20), arcFigure.StartPoint);
        Assert.Equal(new Vector2(28, 20), Assert.IsType<LineSegment>(arcFigure.Segments[0]).Point);
        ArcSegment arc = Assert.IsType<ArcSegment>(arcFigure.Segments[1]);
        Assert.Equal(new Vector2(20, 12), arc.Point);
        Assert.Equal(SweepDirection.Counterclockwise, arc.SweepDirection);
        Assert.Equal(new Vector2(20, 12), drawingCommands[1].Position);
        Assert.Equal(new Vector2(12, 12), drawingCommands[1].Position2);
    }

    [Fact]
    public void EmfMalformedPolyDrawRollsBackEarlierGeometry()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
            (EmfPlusRecordType.EmfPolyDraw,
                EmfPolyDraw(
                    [new Point(4, 4), new Point(8, 8)],
                    [0x06, 0x04],
                    points16: false))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfPolyDraw), exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void EmfMalformedBezierToCountRollsBackEarlierGeometry()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
            (EmfPlusRecordType.EmfPolyBezierTo,
                EmfPointArray([new Point(4, 4), new Point(8, 8)], points16: false))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfPolyBezierTo), exception.Message);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void EmfAngleArcRejectsZeroRadiusWithoutPublishing()
    {
        byte[] emf = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(1, 1, 4, 4)),
            (EmfPlusRecordType.EmfAngleArc,
                EmfAngleArc(new Point(20, 20), 0, startAngle: 0f, sweepAngle: 90f))
        ]);
        using var metafile = new Metafile(new MemoryStream(emf));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains(nameof(EmfPlusRecordType.EmfAngleArc), exception.Message);
        Assert.Empty(context.Commands);
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
    public void EmfOffsetAndExcludeClipRegionRespectSavedDcState()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfIntersectClipRect, EmfRectangle(8, 8, 32, 32)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64)),
            (EmfPlusRecordType.EmfSaveDC, []),
            (EmfPlusRecordType.EmfOffsetClipRgn, EmfPoint(16, 0)),
            (EmfPlusRecordType.EmfExcludeClipRect, EmfRectangle(32, 8, 40, 32)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0000)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64)),
            (EmfPlusRecordType.EmfRestoreDC, EmfInt32(-1)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0000)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(8, 8, 16, 16))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.White.ToArgb(), target.GetPixel(12, 12).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(20, 12).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(28, 12).ToArgb());
        Assert.Equal(0, target.GetPixel(36, 12).A);
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(44, 12).ToArgb());
        Assert.Equal(0, target.GetPixel(52, 12).A);
    }

    [Fact]
    public void EmfMalformedOffsetClipRegionRollsBackEarlierGeometry()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64)),
            (EmfPlusRecordType.EmfOffsetClipRgn, EmfInt32(16))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(
                nameof(EmfPlusRecordType.EmfOffsetClipRgn),
                exception.Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(12, 12).ToArgb());
    }

    [Fact]
    public void EmfExtendedClipRegionCombinesCopyUnionDifferenceAndRestoresTypedState()
    {
        var left = new Rectangle(8, 8, 16, 16);
        var right = new Rectangle(32, 8, 16, 16);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(5, [left])),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(2, [right])),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64)),
            (EmfPlusRecordType.EmfSaveDC, []),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(4, [left])),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0000)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64)),
            (EmfPlusRecordType.EmfRestoreDC, EmfInt32(-1))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(12, 12).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(36, 12).ToArgb());
        Assert.Equal(0, target.GetPixel(24, 12).A);
        Assert.Equal(0, target.GetPixel(56, 12).A);
    }

    [Fact]
    public void EmfExtendedClipRegionCombinesIntersectionAndExclusiveOr()
    {
        var left = new Rectangle(8, 8, 16, 16);
        var right = new Rectangle(32, 8, 16, 16);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(5, [left, right])),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(3, [right])),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(1, [left])),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(12, 12).ToArgb());
        Assert.Equal(0, target.GetPixel(36, 12).A);
    }

    [Fact]
    public void EmfSetMetaRegionConstrainsLaterCopyAndDefaultClip()
    {
        var left = new Rectangle(8, 8, 16, 16);
        var right = new Rectangle(32, 8, 16, 16);
        var outside = new Rectangle(48, 8, 8, 16);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(5, [left, right])),
            (EmfPlusRecordType.EmfSetMetaRgn, []),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(5, [right])),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(2, [outside])),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64)),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(5, rectangles: null)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0000)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(8, 8, 24, 24))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.White.ToArgb(), target.GetPixel(12, 12).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(36, 12).ToArgb());
        Assert.Equal(0, target.GetPixel(52, 12).A);
        Assert.Equal(0, target.GetPixel(24, 12).A);
    }

    [Fact]
    public void EmfExtendedClipRegionCapturesItsSelectionTransform()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetWorldTransform, EmfTransform(16f, 0f)),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(5, [new Rectangle(0, 8, 8, 8)])),
            (EmfPlusRecordType.EmfSetWorldTransform, EmfTransform(0f, 0f)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(0, target.GetPixel(4, 12).A);
        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(20, 12).ToArgb());
        Assert.Equal(0, target.GetPixel(28, 12).A);
    }

    [Fact]
    public void EmfMalformedExtendedClipRegionRollsBackEarlierGeometry()
    {
        byte[][] malformedPayloads =
        [
            EmfExtSelectClipRegion(0, [new Rectangle(8, 8, 16, 16)]),
            EmfExtSelectClipRegion(1, rectangles: null),
            EmfExtSelectClipRegion(5, [new Rectangle(8, 8, 16, 16)]),
            EmfExtSelectClipRegion(5, [new Rectangle(8, 8, 16, 16)])
        ];
        WriteUInt32(malformedPayloads[2], 8 + 4, 2);
        WriteInt32(malformedPayloads[3], 8 + 16, 9);

        foreach (byte[] payload in malformedPayloads)
        {
            byte[] fixture = CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64)),
                (EmfPlusRecordType.EmfExtSelectClipRgn, payload)
            ]);
            using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
            var context = new DrawingContext();
            using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

            Exception exception = Assert.ThrowsAny<Exception>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.True(exception is ArgumentException or NotSupportedException);
            Assert.Contains(
                nameof(EmfPlusRecordType.EmfExtSelectClipRgn),
                exception.Message,
                StringComparison.Ordinal);
            Assert.Empty(context.Commands);
        }
    }

    [Fact]
    public void EmfRegionClipPlaybackHasBoundedWarmedAllocation()
    {
        var records = new List<(EmfPlusRecordType Type, byte[] Payload)>
        {
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(5,
                [
                    new Rectangle(0, 0, 32, 64),
                    new Rectangle(32, 0, 32, 64)
                ])),
            (EmfPlusRecordType.EmfSetMetaRgn, [])
        };
        for (int index = 0; index < 64; index++)
        {
            int x = (index % 8) * 8;
            int y = (index / 8) * 8;
            records.Add((
                EmfPlusRecordType.EmfExtSelectClipRgn,
                EmfExtSelectClipRegion(
                    index % 5 + 1,
                    [
                        new Rectangle(x, y, 3, 6),
                        new Rectangle(x + 4, y, 3, 6)
                    ])));
            records.Add((
                EmfPlusRecordType.EmfRectangle,
                EmfRectangle(x, y, x + 7, y + 6)));
        }

        using var metafile = new Metafile(new MemoryStream(
            CreateTextPlaybackEmf(records),
            writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }
        long allocatedPerPlayback =
            (GC.GetAllocatedBytesForCurrentThread() - before) / 8;

        Assert.InRange(allocatedPerPlayback, 4 * 1024 * 1024, 7 * 1024 * 1024);
    }

    [Fact]
    public void EmfStretchDibitsDecodesBottomUp24BitRowsAndPadding()
    {
        TestDib dib = CreateRgbDib(
            2,
            2,
            24,
            [
                255, 0, 0, 255, 255, 255, 0, 0,
                0, 0, 255, 0, 255, 0, 0, 0
            ]);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(0, 0, 2, 2), new Rectangle(8, 8, 32, 32)))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(48, 48);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(12, 12).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(36, 12).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(12, 36).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(36, 36).ToArgb());
    }

    [Fact]
    public void EmfStretchDibitsCropsTopDown32BitAndMirrorsNegativeSourceWidth()
    {
        TestDib dib = CreateRgbDib(
            4,
            -2,
            32,
            [
                0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0,
                0, 255, 255, 0, 255, 0, 255, 0, 255, 255, 0, 0, 0, 0, 0, 0
            ]);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetWorldTransform, EmfTransform(8f, 8f)),
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(3, 0, -2, 2), new Rectangle(0, 0, 32, 32)))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(48, 48);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(12, 12).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(36, 12).ToArgb());
        Assert.Equal(Color.Aqua.ToArgb(), target.GetPixel(12, 36).ToArgb());
        Assert.Equal(Color.Magenta.ToArgb(), target.GetPixel(36, 36).ToArgb());
        Assert.Equal(0, target.GetPixel(4, 4).A);
    }

    [Fact]
    public void EmfStretchDibitsClipsSourceBoundsAndAdjustsDestination()
    {
        TestDib dib = CreateRgbDib(
            2,
            -1,
            24,
            [0, 0, 255, 0, 255, 0, 0, 0]);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(-1, 0, 3, 1), new Rectangle(0, 0, 30, 10)))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(40, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(0, target.GetPixel(5, 5).A);
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(12, 5).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(25, 5).ToArgb());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void EmfStretchDibitsDecodesIndexedAndRgb555Pixels(int bitCount)
    {
        TestDib dib = bitCount switch
        {
            1 => CreateRgbDib(1, -1, 1, [0x80, 0, 0, 0], [Color.Black, Color.Red]),
            4 => CreateRgbDib(1, -1, 4, [0x10, 0, 0, 0], [Color.Black, Color.Red]),
            8 => CreateRgbDib(1, -1, 8, [1, 0, 0, 0], [Color.Black, Color.Red]),
            16 => CreateRgbDib(1, -1, 16, [0, 0x7C, 0, 0]),
            _ => throw new InvalidOperationException()
        };
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(0, 0, 1, 1), new Rectangle(8, 8, 16, 16)))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(12, 12).ToArgb());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmfSetDibitsToDevicePlacesOnlyTheSuppliedScanBand(bool topDown)
    {
        TestDib dib = CreateRgbDib(
            2,
            topDown ? -4 : 4,
            24,
            topDown
                ? [0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 0]
                : [255, 0, 0, 255, 0, 0, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0]);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSetDIBitsToDevice,
                EmfSetDibitsToDevice(dib, new Rectangle(0, 0, 2, 4), new Point(8, 8), 1, 2))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(0, target.GetPixel(8, 8).A);
        if (topDown)
        {
            Assert.Equal(Color.Red.ToArgb(), target.GetPixel(8, 9).ToArgb());
            Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(8, 10).ToArgb());
        }
        else
        {
            Assert.Equal(Color.Red.ToArgb(), target.GetPixel(8, 9).ToArgb());
            Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(8, 10).ToArgb());
        }
        Assert.Equal(0, target.GetPixel(8, 11).A);
    }

    [Fact]
    public void EmfStretchModeIsTypedAndRestoredWithSavedDeviceContext()
    {
        TestDib dib = CreateRgbDib(1, -1, 24, [0, 0, 255, 0]);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSaveDC, []),
            (EmfPlusRecordType.EmfSetStretchBltMode, EmfInt32(4)),
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(0, 0, 1, 1), new Rectangle(0, 0, 8, 8))),
            (EmfPlusRecordType.EmfRestoreDC, EmfInt32(-1)),
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(0, 0, 1, 1), new Rectangle(8, 0, 8, 8)))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand[] images = context.Commands
            .Where(static command => command.Type == RenderCommandType.DrawTexture)
            .ToArray();
        Assert.Equal(2, images.Length);
        Assert.Equal(TextureSamplingMode.Linear, images[0].TextureSamplingMode);
        Assert.Equal(TextureSamplingMode.Nearest, images[1].TextureSamplingMode);
    }

    [Fact]
    public void EmfMalformedAndUnsupportedDibRecordsRollBackEarlierGeometry()
    {
        TestDib valid = CreateRgbDib(1, -1, 24, [0, 0, 255, 0]);
        byte[][] malformedPayloads =
        [
            EmfStretchDibits(valid, new Rectangle(0, 0, 1, 1), new Rectangle(0, 0, 8, 8)),
            EmfStretchDibits(valid, new Rectangle(0, 0, 1, 1), new Rectangle(0, 0, 8, 8)),
            EmfStretchDibits(valid, new Rectangle(0, 0, 1, 1), new Rectangle(0, 0, 8, 8)),
            EmfSetDibitsToDevice(valid, new Rectangle(0, 0, 1, 1), Point.Empty, 0, 1)
        ];
        WriteUInt32(malformedPayloads[0], 40, 76);
        WriteUInt32(malformedPayloads[1], 52, 8);
        WriteUInt32(malformedPayloads[2], 60, 0x0066_0046);
        WriteUInt32(malformedPayloads[3], 64, 2);

        foreach (byte[] payload in malformedPayloads)
        {
            EmfPlusRecordType type = payload.Length == malformedPayloads[3].Length
                ? EmfPlusRecordType.EmfSetDIBitsToDevice
                : EmfPlusRecordType.EmfStretchDIBits;
            byte[] fixture = CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64)),
                (type, payload)
            ]);
            using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
            var context = new DrawingContext();
            using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

            Exception exception = Assert.ThrowsAny<Exception>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.True(exception is ArgumentException or NotSupportedException);
            Assert.Contains(type.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.Empty(context.Commands);
        }
    }

    [Fact]
    public void EmfDibPlaybackHasBoundedWarmedAllocation()
    {
        TestDib dib = CreateRgbDib(
            2,
            -2,
            32,
            [0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]);
        var records = new List<(EmfPlusRecordType Type, byte[] Payload)>();
        for (int index = 0; index < 64; index++)
        {
            records.Add((
                EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(
                    dib,
                    new Rectangle(0, 0, 2, 2),
                    new Rectangle((index % 8) * 8, (index / 8) * 8, 8, 8))));
        }
        using var metafile = new Metafile(new MemoryStream(
            CreateTextPlaybackEmf(records),
            writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }
        long allocatedPerPlayback =
            (GC.GetAllocatedBytesForCurrentThread() - before) / 8;

        Assert.InRange(allocatedPerPlayback, 64 * 1024, 16 * 1024 * 1024);
    }

    [Fact]
    public void WmfDibBitBltDecodesBottomUpRowsAndPadding()
    {
        TestDib dib = CreateRgbDib(
            2,
            2,
            24,
            [255, 0, 0, 255, 255, 255, 0, 0, 0, 0, 255, 0, 255, 0, 0, 0]);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x0940, WmfDibBitBlt(dib, new Rectangle(0, 0, 2, 2), new Point(8, 8))),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(8, 8).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(9, 8).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(8, 9).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(9, 9).ToArgb());
    }

    [Theory]
    [InlineData(0x0B41)]
    [InlineData(0x0F43)]
    public void WmfStretchDibFamiliesDecodeAndScalePackedDibs(ushort function)
    {
        TestDib dib = CreateRgbDib(1, -1, 24, [0, 0, 255, 0]);
        byte[] payload = function == 0x0B41
            ? WmfDibStretchBlt(dib, new Rectangle(0, 0, 1, 1), new Rectangle(8, 8, 16, 16))
            : WmfStretchDib(dib, new Rectangle(0, 0, 1, 1), new Rectangle(8, 8, 16, 16));
        byte[] fixture = CreatePlaybackWmf([(function, payload), (0, [])]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));

        RenderCommand image = Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawTexture);
        Assert.Equal(TextureSamplingMode.Nearest, image.TextureSamplingMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WmfSetDibToDevicePlacesOnlyTheSuppliedScanBand(bool topDown)
    {
        TestDib dib = CreateRgbDib(
            2,
            topDown ? -4 : 4,
            24,
            topDown
                ? [0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0]
                : [255, 0, 0, 255, 0, 0, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0]);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x0D33, WmfSetDibToDevice(
                dib,
                new Rectangle(0, 0, 2, 4),
                new Point(8, 8),
                1,
                2)),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(0, target.GetPixel(8, 8).A);
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(8, 9).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(8, 10).ToArgb());
        Assert.Equal(0, target.GetPixel(8, 11).A);
    }

    [Theory]
    [InlineData(0x0940, 18)]
    [InlineData(0x0B41, 22)]
    public void WmfDibSourceRequiredPlaybackDcRecordsRemainTransactional(
        ushort function,
        int payloadSize)
    {
        byte[] payload = new byte[payloadSize];
        WriteUInt32(payload, 0, 0x00CC_0020);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x041B, WmfWords(12, 12, 0, 0)),
            (function, payload),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains("embedded bitmap source", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void WmfPackedDibSkipsDirectColorOptimizationTable()
    {
        TestDib original = CreateRgbDib(1, -1, 24, [0, 0, 255, 0]);
        byte[] info = new byte[44];
        original.Info.CopyTo(info, 0);
        WriteUInt32(info, 32, 1);
        info[40] = 0x55;
        var dib = new TestDib(info, original.Bits);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x0940, WmfDibBitBlt(dib, new Rectangle(0, 0, 1, 1), new Point(8, 8))),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(8, 8).ToArgb());
    }

    [Theory]
    [InlineData(40)]
    [InlineData(108)]
    [InlineData(124)]
    public void EmfStretchDibitsDecodesRgb565BitFields(int headerSize)
    {
        TestDib dib = CreateBitFieldsDib(
            2,
            -1,
            16,
            [0, 0xF8, 0xE0, 0x07],
            0xF800,
            0x07E0,
            0x001F,
            headerSize: headerSize);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(0, 0, 2, 1), new Rectangle(8, 8, 16, 8)))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(32, 24);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(10, 10).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(20, 10).ToArgb());
    }

    [Fact]
    public void WmfStretchDibDecodesV4BitFieldAlphaAndCustomChannelOrder()
    {
        TestDib dib = CreateBitFieldsDib(
            1,
            -1,
            32,
            [0x10, 0x20, 0x40, 0x80],
            0x0000_00FF,
            0x0000_FF00,
            0x00FF_0000,
            0xFF00_0000,
            headerSize: 108);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x0F43, WmfStretchDib(
                dib,
                new Rectangle(0, 0, 1, 1),
                new Rectangle(8, 8, 8, 8))),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(24, 24);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Color pixel = target.GetPixel(10, 10);
        Assert.InRange(pixel.A, 127, 129);
        Assert.InRange(pixel.R, 15, 17);
        Assert.InRange(pixel.G, 31, 33);
        Assert.InRange(pixel.B, 63, 65);
    }

    [Fact]
    public void WmfPackedBitFieldsSkipExternalMasksAndOptimizationTable()
    {
        TestDib dib = CreateBitFieldsDib(
            1,
            -1,
            16,
            [0, 0xF8, 0, 0],
            0xF800,
            0x07E0,
            0x001F,
            optimizationPalette: [Color.Magenta]);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x0940, WmfDibBitBlt(dib, new Rectangle(0, 0, 1, 1), new Point(8, 8))),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(8, 8).ToArgb());
    }

    [Fact]
    public void MalformedBitFieldMasksRollBackEmfAndWmfPlayback()
    {
        TestDib valid = CreateBitFieldsDib(
            1,
            -1,
            16,
            [0, 0xF8, 0, 0],
            0xF800,
            0x07E0,
            0x001F);
        TestDib[] malformed =
        [
            WithBitFieldMasks(valid, 0, 0x07E0, 0x001F),
            WithBitFieldMasks(valid, 0xF800, 0xF800, 0x001F),
            WithBitFieldMasks(valid, 0xF801, 0x07E0, 0x001F),
            WithBitFieldMasks(valid, 0x1_F000, 0x07E0, 0x001F),
            new TestDib(valid.Info[..48], valid.Bits),
            CreateBitFieldsDib(1, -1, 24, [0, 0, 0, 0], 0xFF0000, 0x00FF00, 0x0000FF),
            CreateBitFieldsDib(
                1,
                -1,
                32,
                [0, 0, 0, 0],
                0x00FF0000,
                0x0000FF00,
                0x000000FF,
                0x00F00000,
                headerSize: 108)
        ];

        foreach (TestDib dib in malformed)
        {
            byte[] emfFixture = CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetPixelV, EmfSetPixel(new Point(1, 1), Color.Red)),
                (EmfPlusRecordType.EmfStretchDIBits,
                    EmfStretchDibits(dib, new Rectangle(0, 0, 1, 1), new Rectangle(0, 0, 8, 8)))
            ]);
            AssertDibPlaybackRollsBack(emfFixture);

            byte[] wmfFixture = CreatePlaybackWmf(
            [
                (0x041F, WmfSetPixel(Color.Red, new Point(1, 1))),
                (0x0F43, WmfStretchDib(
                    dib,
                    new Rectangle(0, 0, 1, 1),
                    new Rectangle(0, 0, 8, 8))),
                (0, [])
            ]);
            AssertDibPlaybackRollsBack(wmfFixture);
        }
    }

    [Fact]
    public void BitFieldDibPlaybackHasBoundedWarmedAllocation()
    {
        TestDib dib = CreateBitFieldsDib(
            1,
            -1,
            16,
            [0, 0xF8, 0, 0],
            0xF800,
            0x07E0,
            0x001F);
        var records = new List<(ushort Function, byte[] Payload)>();
        for (int index = 0; index < 64; index++)
        {
            records.Add((
                0x0F43,
                WmfStretchDib(
                    dib,
                    new Rectangle(0, 0, 1, 1),
                    new Rectangle((index % 8) * 8, (index / 8) * 8, 8, 8))));
        }
        records.Add((0, []));
        using var metafile = new Metafile(new MemoryStream(
            CreatePlaybackWmf(records),
            writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }
        long allocatedPerPlayback =
            (GC.GetAllocatedBytesForCurrentThread() - before) / 8;

        Assert.InRange(allocatedPerPlayback, 64 * 1024, 16 * 1024 * 1024);
    }

    [Fact]
    public void EmfStretchDibitsDecodesRle8EncodedAndAbsoluteRows()
    {
        TestDib dib = CreateRleDib(
            4,
            2,
            8,
            [2, 1, 2, 2, 0, 0, 0, 4, 3, 4, 3, 4, 0, 0, 0, 1],
            [Color.Black, Color.Red, Color.Lime, Color.Blue, Color.White]);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(0, 0, 4, 2), new Rectangle(8, 8, 16, 8)))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(32, 24);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(9, 9).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(13, 9).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(9, 13).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(21, 13).ToArgb());
    }

    [Fact]
    public void WmfStretchDibDecodesRle4EncodedAbsoluteDeltaAndDefaultPixels()
    {
        TestDib dib = CreateRleDib(
            6,
            3,
            4,
            [
                6, 0x12, 0, 0,
                0, 2, 2, 0, 2, 0x34, 0, 0,
                0, 5, 0x12, 0x34, 0x50, 0, 0, 1
            ],
            [Color.Black, Color.Red, Color.Lime, Color.Blue, Color.White, Color.Yellow]);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x0F43, WmfStretchDib(
                dib,
                new Rectangle(0, 0, 6, 3),
                new Rectangle(4, 4, 12, 6))),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(24, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(5, 5).ToArgb());
        Assert.Equal(Color.Yellow.ToArgb(), target.GetPixel(13, 5).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(15, 5).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(5, 7).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(9, 7).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(11, 7).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(5, 9).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(7, 9).ToArgb());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetDibToDeviceDecodesOnlyTheSuppliedRleScanBand(bool wmf)
    {
        TestDib dib = CreateRleDib(
            2,
            4,
            8,
            [2, 2, 0, 0, 2, 1, 0, 1],
            [Color.Black, Color.Red, Color.Blue]);
        byte[] fixture = wmf
            ? CreatePlaybackWmf(
            [
                (0x0D33, WmfSetDibToDevice(
                    dib,
                    new Rectangle(0, 0, 2, 4),
                    new Point(8, 8),
                    1,
                    2)),
                (0, [])
            ])
            : CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetDIBitsToDevice,
                    EmfSetDibitsToDevice(
                        dib,
                        new Rectangle(0, 0, 2, 4),
                        new Point(8, 8),
                        1,
                        2))
            ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(0, target.GetPixel(8, 8).A);
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(8, 9).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(8, 10).ToArgb());
        Assert.Equal(0, target.GetPixel(8, 11).A);
    }

    [Fact]
    public void MalformedRleDibsRollBackEmfAndWmfPlayback()
    {
        Color[] palette = [Color.Black, Color.Red, Color.Lime, Color.Blue];
        TestDib[] malformed =
        [
            CreateRleDib(4, 1, 8, [0, 2], palette),
            CreateRleDib(4, 1, 8, [2, 1], palette),
            CreateRleDib(4, 1, 8, [0, 1, 0, 0], palette),
            CreateRleDib(4, 1, 8, [5, 1, 0, 1], palette),
            CreateRleDib(4, 1, 8, [0, 2, 5, 0, 0, 1], palette),
            CreateRleDib(4, 1, 8, [0, 3, 1, 2, 3, 9, 0, 1], palette),
            CreateRleDib(4, 1, 8, [1, 7, 0, 1], palette),
            CreateRleDib(4, -1, 8, [4, 1, 0, 1], palette),
            CreateRleDib(4, 1, 4, [4, 0x12, 0, 1], palette, compression: 1),
            WithRleImageSize(
                CreateRleDib(4, 1, 8, [4, 1, 0, 1], palette),
                2)
        ];

        foreach (TestDib dib in malformed)
        {
            byte[] emfFixture = CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetPixelV, EmfSetPixel(new Point(1, 1), Color.Red)),
                (EmfPlusRecordType.EmfStretchDIBits,
                    EmfStretchDibits(dib, new Rectangle(0, 0, 4, 1), new Rectangle(0, 0, 8, 8)))
            ]);
            AssertDibPlaybackRollsBack(emfFixture);

            byte[] wmfFixture = CreatePlaybackWmf(
            [
                (0x041F, WmfSetPixel(Color.Red, new Point(1, 1))),
                (0x0F43, WmfStretchDib(
                    dib,
                    new Rectangle(0, 0, 4, 1),
                    new Rectangle(0, 0, 8, 8))),
                (0, [])
            ]);
            AssertDibPlaybackRollsBack(wmfFixture);
        }
    }

    [Fact]
    public void RleDibPlaybackHasBoundedWarmedAllocation()
    {
        TestDib dib = CreateRleDib(
            2,
            2,
            8,
            [2, 1, 0, 0, 2, 2, 0, 1],
            [Color.Black, Color.Red, Color.Blue]);
        var records = new List<(ushort Function, byte[] Payload)>();
        for (int index = 0; index < 64; index++)
        {
            records.Add((
                0x0F43,
                WmfStretchDib(
                    dib,
                    new Rectangle(0, 0, 2, 2),
                    new Rectangle((index % 8) * 8, (index / 8) * 8, 8, 8))));
        }
        records.Add((0, []));
        using var metafile = new Metafile(new MemoryStream(
            CreatePlaybackWmf(records),
            writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }
        long allocatedPerPlayback =
            (GC.GetAllocatedBytesForCurrentThread() - before) / 8;

        Assert.InRange(allocatedPerPlayback, 64 * 1024, 16 * 1024 * 1024);
    }

    [Fact]
    public void EmfStretchDibitsDecodesPngWithSourceCropAndMirroring()
    {
        TestDib dib = CreateEncodedDib(
            4,
            2,
            5,
            [
                Color.Red, Color.Lime, Color.Blue, Color.White,
                Color.Black, Color.Yellow, Color.Cyan, Color.Magenta
            ]);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(3, 0, -2, 2), new Rectangle(8, 8, 16, 8)))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(32, 24);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(9, 9).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(17, 9).ToArgb());
        Assert.Equal(Color.Cyan.ToArgb(), target.GetPixel(9, 13).ToArgb());
        Assert.Equal(Color.Yellow.ToArgb(), target.GetPixel(17, 13).ToArgb());
    }

    [Fact]
    public void WmfStretchDibDecodesOddSizedJpegBufferAndIgnoresRecordPadding()
    {
        TestDib dib = CreateEncodedDib(
            3,
            2,
            4,
            Enumerable.Repeat(Color.FromArgb(255, 220, 30, 20), 6).ToArray(),
            forceOddSize: true);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x0F43, WmfStretchDib(
                dib,
                new Rectangle(0, 0, 3, 2),
                new Rectangle(8, 8, 18, 8))),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(32, 24);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Color pixel = target.GetPixel(12, 10);
        Assert.InRange(pixel.R, 190, 240);
        Assert.InRange(pixel.G, 10, 55);
        Assert.InRange(pixel.B, 5, 45);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetDibToDeviceDecodesCompletePngImage(bool wmf)
    {
        TestDib dib = CreateEncodedDib(
            2,
            2,
            5,
            [Color.Red, Color.Lime, Color.Blue, Color.White]);
        byte[] fixture = wmf
            ? CreatePlaybackWmf(
            [
                (0x0D33, WmfSetDibToDevice(
                    dib,
                    new Rectangle(0, 0, 2, 2),
                    new Point(8, 8),
                    0,
                    2)),
                (0, [])
            ])
            : CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetDIBitsToDevice,
                    EmfSetDibitsToDevice(
                        dib,
                        new Rectangle(0, 0, 2, 2),
                        new Point(8, 8),
                        0,
                        2))
            ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(8, 8).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(9, 8).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(8, 9).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(9, 9).ToArgb());
    }

    [Fact]
    public void MalformedEncodedDibsRollBackEmfAndWmfPlayback()
    {
        TestDib valid = CreateEncodedDib(
            2,
            2,
            5,
            [Color.Red, Color.Lime, Color.Blue, Color.White]);
        byte[] truncatedBits = valid.Bits[..8];
        byte[] trailingBits = [.. valid.Bits, 0, 0];
        TestDib[] malformed =
        [
            WithEncodedDibHeader(valid, compression: 4),
            WithEncodedDibHeader(valid, imageSize: checked((uint)valid.Bits.Length - 2)),
            WithEncodedDibHeader(valid, width: 3),
            WithEncodedDibHeader(valid, height: -2),
            WithEncodedDibHeader(valid, bitCount: 24),
            WithEncodedDibHeader(valid, colorsUsed: 1),
            new TestDib(
                WithEncodedDibHeader(valid, imageSize: checked((uint)truncatedBits.Length)).Info,
                truncatedBits),
            new TestDib(valid.Info, trailingBits)
        ];

        foreach (TestDib dib in malformed)
        {
            byte[] emfFixture = CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetPixelV, EmfSetPixel(new Point(1, 1), Color.Red)),
                (EmfPlusRecordType.EmfStretchDIBits,
                    EmfStretchDibits(dib, new Rectangle(0, 0, 2, 2), new Rectangle(0, 0, 8, 8)))
            ]);
            AssertDibPlaybackRollsBack(emfFixture);

            byte[] wmfFixture = CreatePlaybackWmf(
            [
                (0x041F, WmfSetPixel(Color.Red, new Point(1, 1))),
                (0x0F43, WmfStretchDib(
                    dib,
                    new Rectangle(0, 0, 2, 2),
                    new Rectangle(0, 0, 8, 8))),
                (0, [])
            ]);
            AssertDibPlaybackRollsBack(wmfFixture);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialEncodedScanBandsRollBackPlayback(bool wmf)
    {
        TestDib dib = CreateEncodedDib(
            2,
            2,
            5,
            [Color.Red, Color.Lime, Color.Blue, Color.White]);
        byte[] fixture = wmf
            ? CreatePlaybackWmf(
            [
                (0x041F, WmfSetPixel(Color.Red, new Point(1, 1))),
                (0x0D33, WmfSetDibToDevice(
                    dib,
                    new Rectangle(0, 0, 2, 2),
                    new Point(8, 8),
                    0,
                    1)),
                (0, [])
            ])
            : CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetPixelV, EmfSetPixel(new Point(1, 1), Color.Red)),
                (EmfPlusRecordType.EmfSetDIBitsToDevice,
                    EmfSetDibitsToDevice(
                        dib,
                        new Rectangle(0, 0, 2, 2),
                        new Point(8, 8),
                        0,
                        1))
            ]);

        AssertDibPlaybackRollsBack(fixture);
    }

    [Fact]
    public void EncodedDibPlaybackHasBoundedWarmedAllocation()
    {
        TestDib dib = CreateEncodedDib(
            2,
            2,
            5,
            [Color.Red, Color.Lime, Color.Blue, Color.White]);
        var records = new List<(ushort Function, byte[] Payload)>();
        for (int index = 0; index < 64; index++)
        {
            records.Add((
                0x0F43,
                WmfStretchDib(
                    dib,
                    new Rectangle(0, 0, 2, 2),
                    new Rectangle((index % 8) * 8, (index / 8) * 8, 8, 8))));
        }
        records.Add((0, []));
        using var metafile = new Metafile(new MemoryStream(
            CreatePlaybackWmf(records),
            writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }
        long allocatedPerPlayback =
            (GC.GetAllocatedBytesForCurrentThread() - before) / 8;

        Assert.InRange(allocatedPerPlayback, 64 * 1024, 32 * 1024 * 1024);
    }

    [Fact]
    public void WmfMalformedAndUnsupportedDibRecordsRollBackEarlierCommands()
    {
        TestDib valid = CreateRgbDib(1, -1, 24, [0, 0, 255, 0]);
        byte[] truncated = WmfStretchDib(
            valid,
            new Rectangle(0, 0, 1, 1),
            new Rectangle(0, 0, 8, 8));
        Array.Resize(ref truncated, truncated.Length - 2);
        byte[] unsupportedUsage = WmfStretchDib(
            valid,
            new Rectangle(0, 0, 1, 1),
            new Rectangle(0, 0, 8, 8),
            usage: 3);
        byte[] unsupportedRop = WmfDibBitBlt(
            valid,
            new Rectangle(0, 0, 1, 1),
            Point.Empty,
            rasterOperation: 0x0066_0046);
        byte[] invalidScan = WmfSetDibToDevice(
            valid,
            new Rectangle(0, 0, 1, 1),
            Point.Empty,
            0,
            2);
        (ushort Function, byte[] Payload)[] malformedRecords =
        [
            (0x0F43, truncated),
            (0x0F43, unsupportedUsage),
            (0x0940, unsupportedRop),
            (0x0D33, invalidScan)
        ];

        foreach ((ushort function, byte[] payload) in malformedRecords)
        {
            byte[] fixture = CreatePlaybackWmf(
            [
                (0x041F, WmfSetPixel(Color.Red, new Point(1, 1))),
                (function, payload),
                (0, [])
            ]);
            using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
            var context = new DrawingContext();
            using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

            Exception exception = Assert.ThrowsAny<Exception>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

            Assert.True(exception is ArgumentException or NotSupportedException);
            Assert.Empty(context.Commands);
        }
    }

    [Fact]
    public void EmfLogicalPaletteMapsDibPaletteColorTableIndices()
    {
        TestDib dib = CreateLogicalPaletteDib(
            2,
            -1,
            [0, 1, 0, 0],
            [2, 0]);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfCreatePalette,
                EmfPalette(1, [Color.Red, Color.Lime, Color.Blue])),
            (EmfPlusRecordType.EmfSelectPalette, EmfUInt32(1)),
            (EmfPlusRecordType.EmfRealizePalette, []),
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(
                    dib,
                    new Rectangle(0, 0, 2, 1),
                    new Rectangle(8, 8, 8, 4),
                    usage: 1))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(24, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(9, 9).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(13, 9).ToArgb());
    }

    [Fact]
    public void WmfLogicalPaletteMapsDirectDibPaletteIndices()
    {
        TestDib dib = CreateLogicalPaletteDib(
            2,
            -1,
            [1, 0, 0, 0],
            [],
            directIndices: true);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x00F7, WmfPalette(0x0300, [Color.Red, Color.Blue])),
            (0x0234, WmfWords(0)),
            (0x0035, []),
            (0x0F43, WmfStretchDib(
                dib,
                new Rectangle(0, 0, 2, 1),
                new Rectangle(8, 8, 8, 4),
                usage: 2)),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(24, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(9, 9).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(13, 9).ToArgb());
    }

    [Fact]
    public void EmfPaletteSelectionAndEntriesRestoreWithSavedDeviceContext()
    {
        TestDib dib = CreateLogicalPaletteDib(
            1,
            -1,
            [0, 0, 0, 0],
            [],
            directIndices: true);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfCreatePalette, EmfPalette(1, [Color.Red])),
            (EmfPlusRecordType.EmfCreatePalette, EmfPalette(2, [Color.Blue, Color.White])),
            (EmfPlusRecordType.EmfSelectPalette, EmfUInt32(1)),
            (EmfPlusRecordType.EmfSaveDC, []),
            (EmfPlusRecordType.EmfSelectPalette, EmfUInt32(2)),
            (EmfPlusRecordType.EmfSetPaletteEntries,
                EmfPaletteEntries(2, 0, [Color.Lime])),
            (EmfPlusRecordType.EmfResizePalette, EmfUInt32Pair(2, 1)),
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(0, 0, 1, 1), new Rectangle(8, 8, 4, 4), 2)),
            (EmfPlusRecordType.EmfRestoreDC, EmfInt32(-1)),
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(0, 0, 1, 1), new Rectangle(16, 8, 4, 4), 2))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(24, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(9, 9).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(17, 9).ToArgb());
    }

    [Fact]
    public void WmfPaletteAnimationChangesOnlyReservedEntries()
    {
        TestDib dib = CreateLogicalPaletteDib(
            2,
            -1,
            [0, 1, 0, 0],
            [],
            directIndices: true);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x00F7, WmfPalette(0x0300, [Color.Red, Color.Lime], [1, 0])),
            (0x0234, WmfWords(0)),
            (0x0436, WmfPalette(0, [Color.Blue, Color.White])),
            (0x0037, WmfPalette(1, [Color.Yellow])),
            (0x0139, WmfWords(2)),
            (0x0F43, WmfStretchDib(
                dib,
                new Rectangle(0, 0, 2, 1),
                new Rectangle(8, 8, 8, 4),
                usage: 2)),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(24, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(9, 9).ToArgb());
        Assert.Equal(Color.Yellow.ToArgb(), target.GetPixel(13, 9).ToArgb());
    }

    [Fact]
    public void LogicalPaletteRecordsRejectMalformedStateTransactionally()
    {
        TestDib invalidPaletteIndex = CreateLogicalPaletteDib(
            1,
            -1,
            [0, 0, 0, 0],
            [1]);
        byte[][] fixtures =
        [
            CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetPixelV, EmfSetPixel(new Point(1, 1), Color.Red)),
                (EmfPlusRecordType.EmfCreatePalette, EmfPalette(1, [Color.Red], version: 0x0200))
            ]),
            CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetPixelV, EmfSetPixel(new Point(1, 1), Color.Red)),
                (EmfPlusRecordType.EmfCreatePalette, EmfPalette(1, []))
            ]),
            CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetPixelV, EmfSetPixel(new Point(1, 1), Color.Red)),
                (EmfPlusRecordType.EmfCreatePalette, EmfPalette(1, [Color.Red])),
                (EmfPlusRecordType.EmfSetPaletteEntries,
                    EmfPaletteEntries(1, 1, [Color.Blue]))
            ]),
            CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetPixelV, EmfSetPixel(new Point(1, 1), Color.Red)),
                (EmfPlusRecordType.EmfCreatePalette, EmfPalette(1, [Color.Red])),
                (EmfPlusRecordType.EmfSelectPalette, EmfUInt32(1)),
                (EmfPlusRecordType.EmfStretchDIBits,
                    EmfStretchDibits(
                        invalidPaletteIndex,
                        new Rectangle(0, 0, 1, 1),
                        new Rectangle(0, 0, 4, 4),
                        usage: 1))
            ]),
            CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetPixelV, EmfSetPixel(new Point(1, 1), Color.Red)),
                (EmfPlusRecordType.EmfCreatePalette, EmfPalette(1, [Color.Red])),
                (EmfPlusRecordType.EmfSelectPalette, EmfUInt32(1)),
                (EmfPlusRecordType.EmfDeleteObject, EmfUInt32(1))
            ])
        ];

        foreach (byte[] fixture in fixtures)
        {
            using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
            var context = new DrawingContext();
            using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

            Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Empty(context.Commands);
        }
    }

    [Fact]
    public void EmfStretchDibitsDecodesBottomUpCmykPixels()
    {
        TestDib dib = CreateCmykDib(
            2,
            2,
            [
                255, 255, 0, 0, 0, 0, 0, 0,
                0, 255, 255, 0, 255, 0, 255, 0
            ]);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(0, 0, 2, 2), new Rectangle(8, 8, 8, 8)))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(24, 20);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(9, 9).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(13, 9).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(9, 13).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(13, 13).ToArgb());
    }

    [Fact]
    public void WmfStretchDibDecodesTopDownCmykAndBlackInk()
    {
        TestDib dib = CreateCmykDib(
            2,
            -1,
            [64, 128, 192, 32, 0, 0, 0, 255]);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x0F43, WmfStretchDib(
                dib,
                new Rectangle(0, 0, 2, 1),
                new Rectangle(8, 8, 8, 4))),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(24, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.FromArgb(167, 111, 55).ToArgb(), target.GetPixel(9, 9).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(13, 9).ToArgb());
    }

    [Fact]
    public void EmfCmykRle8UsesBoundedIndexedColorTable()
    {
        TestDib dib = CreateRleDib(
            4,
            1,
            8,
            [2, 1, 2, 2, 0, 1],
            [Color.Black, Color.Red, Color.Blue],
            compression: 12);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(dib, new Rectangle(0, 0, 4, 1), new Rectangle(8, 8, 8, 4)))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(24, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(9, 9).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(11, 9).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(13, 9).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(15, 9).ToArgb());
    }

    [Fact]
    public void WmfCmykRle4UsesBoundedIndexedColorTable()
    {
        TestDib dib = CreateRleDib(
            4,
            1,
            4,
            [4, 0x12, 0, 1],
            [Color.Black, Color.Red, Color.Lime],
            compression: 13);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x0F43, WmfStretchDib(
                dib,
                new Rectangle(0, 0, 4, 1),
                new Rectangle(8, 8, 8, 4))),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(24, 16);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(9, 9).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(11, 9).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(13, 9).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), target.GetPixel(15, 9).ToArgb());
    }

    [Fact]
    public void CmykDibRecordsRejectInvalidDepthOrientationAndSizeTransactionally()
    {
        TestDib invalidDepth = CreateCmykDib(1, 1, [0, 0, 0, 0]);
        WriteUInt16(invalidDepth.Info, 14, 24);
        TestDib wrongRleDepth = CreateRleDib(
            1, 1, 4, [1, 1, 0, 1], [Color.Black, Color.Red], compression: 12);
        TestDib topDownRle = CreateRleDib(
            1, -1, 8, [1, 1, 0, 1], [Color.Black, Color.Red], compression: 12);
        TestDib missingSize = WithRleImageSize(CreateRleDib(
            1, 1, 4, [1, 0x10, 0, 1], [Color.Black, Color.Red], compression: 13), 0);
        TestDib[] invalid = [invalidDepth, wrongRleDepth, topDownRle, missingSize];

        foreach (TestDib dib in invalid)
        {
            byte[] fixture = CreateTextPlaybackEmf(
            [
                (EmfPlusRecordType.EmfSetPixelV, EmfSetPixel(new Point(1, 1), Color.Red)),
                (EmfPlusRecordType.EmfStretchDIBits,
                    EmfStretchDibits(dib, new Rectangle(0, 0, 1, 1), new Rectangle(0, 0, 4, 4)))
            ]);
            using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
            var context = new DrawingContext();
            using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

            Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Empty(context.Commands);
        }
    }

    [Fact]
    public void CmykDibPlaybackHasBoundedWarmedAllocation()
    {
        TestDib dib = CreateCmykDib(1, -1, [0, 255, 255, 0]);
        var records = new List<(ushort Function, byte[] Payload)>();
        for (int index = 0; index < 64; index++)
        {
            records.Add((
                0x0F43,
                WmfStretchDib(
                    dib,
                    new Rectangle(0, 0, 1, 1),
                    new Rectangle((index % 8) * 8, (index / 8) * 8, 8, 8))));
        }
        records.Add((0, []));
        using var metafile = new Metafile(new MemoryStream(
            CreatePlaybackWmf(records),
            writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }
        long allocatedPerPlayback =
            (GC.GetAllocatedBytesForCurrentThread() - before) / 8;

        Assert.InRange(allocatedPerPlayback, 64 * 1024, 16 * 1024 * 1024);
    }

    [Fact]
    public void EmfStretchDibitsAppliesNotSourceCopyToRgbChannels()
    {
        TestDib dib = CreateRgbDib(1, -1, 24, [0x56, 0x34, 0x12, 0]);
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfStretchDIBits,
                EmfStretchDibits(
                    dib,
                    new Rectangle(0, 0, 1, 1),
                    new Rectangle(8, 8, 8, 8),
                    rasterOperation: 0x0033_0008))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(24, 24);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Yellow);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(
            Color.FromArgb(0xED, 0xCB, 0xA9).ToArgb(),
            target.GetPixel(10, 10).ToArgb());
        Assert.Equal(Color.Yellow.ToArgb(), target.GetPixel(2, 2).ToArgb());
    }

    [Fact]
    public void WmfDibRasterOperationsDrawBlackWhiteAndSelectedPattern()
    {
        TestDib dib = CreateRgbDib(1, -1, 24, [0, 0, 255, 0]);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x02FC, WmfBrush(Color.Green)),
            (0x012D, WmfWords(0)),
            (0x0F43, WmfStretchDib(
                dib,
                new Rectangle(0, 0, 1, 1),
                new Rectangle(4, 4, 8, 8),
                rasterOperation: 0x0000_0042)),
            (0x0F43, WmfStretchDib(
                dib,
                new Rectangle(4, 4, 1, 1),
                new Rectangle(16, 4, 8, 8),
                rasterOperation: 0x00F0_0021)),
            (0x0F43, WmfStretchDib(
                dib,
                new Rectangle(0, 0, 1, 1),
                new Rectangle(28, 4, 8, 8),
                rasterOperation: 0x00FF_0062)),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(48, 20);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(6, 6).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(18, 6).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(30, 6).ToArgb());
        Assert.Equal(0, target.GetPixel(2, 2).A);
    }

    [Fact]
    public void NotSourceCopyDibPlaybackHasBoundedWarmedAllocation()
    {
        TestDib dib = CreateRgbDib(1, -1, 24, [0, 0, 255, 0]);
        var records = new List<(ushort Function, byte[] Payload)>();
        for (int index = 0; index < 64; index++)
        {
            records.Add((
                0x0F43,
                WmfStretchDib(
                    dib,
                    new Rectangle(0, 0, 1, 1),
                    new Rectangle((index % 8) * 8, (index / 8) * 8, 8, 8),
                    rasterOperation: 0x0033_0008)));
        }
        records.Add((0, []));
        using var metafile = new Metafile(new MemoryStream(
            CreatePlaybackWmf(records),
            writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }
        long allocatedPerPlayback =
            (GC.GetAllocatedBytesForCurrentThread() - before) / 8;

        Assert.InRange(allocatedPerPlayback, 64 * 1024, 16 * 1024 * 1024);
    }

    [Fact]
    public void WmfBitmapRecordsDrawSourceIndependentOperationsWithoutBitmapSources()
    {
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x02FC, WmfBrush(Color.Green)),
            (0x012D, WmfWords(0)),
            (0x0922, WmfBitBltWithoutBitmap(
                Point.Empty,
                new Rectangle(4, 4, 8, 8),
                0x00F0_0021)),
            (0x0B23, WmfStretchBltWithoutBitmap(
                new Rectangle(2, 2, 4, 4),
                new Rectangle(16, 4, 8, 8),
                0x0000_0042)),
            (0x0940, WmfBitBltWithoutBitmap(
                new Point(8, 8),
                new Rectangle(28, 4, 8, 8),
                0x00FF_0062)),
            (0x0B41, WmfStretchBltWithoutBitmap(
                new Rectangle(12, 12, 4, 4),
                new Rectangle(40, 4, 8, 8),
                0x00F0_0021)),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(56, 20);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(6, 6).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(18, 6).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(30, 6).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(42, 6).ToArgb());
        Assert.Equal(0, target.GetPixel(2, 2).A);
    }

    [Fact]
    public void WmfBitmap16EnvelopeIsValidatedBeforeSourceIndependentDrawing()
    {
        byte[] bitmap = CreateBitmap16(2, 1, 24, [0, 0, 255, 0, 255, 0]);
        byte[] fixture = CreatePlaybackWmf(
        [
            (0x02FC, WmfBrush(Color.Blue)),
            (0x012D, WmfWords(0)),
            (0x0922, WmfBitmap16BitBlt(
                bitmap,
                new Point(50, 50),
                new Rectangle(8, 8, 12, 8),
                0x00F0_0021)),
            (0, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(32, 24);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(10, 10).ToArgb());
        Assert.Equal(0, target.GetPixel(4, 4).A);
    }

    [Fact]
    public void WmfSourceRequiredBitmapRecordsRejectTransactionally()
    {
        byte[] bitmap = CreateBitmap16(2, 1, 24, [0, 0, 255, 0, 255, 0]);
        (ushort Function, byte[] Payload)[] unsupported =
        [
            (0x0940, WmfBitBltWithoutBitmap(
                Point.Empty,
                new Rectangle(8, 8, 8, 8),
                0x00CC_0020)),
            (0x0922, WmfBitmap16BitBlt(
                bitmap,
                Point.Empty,
                new Rectangle(8, 8, 8, 8),
                0x00CC_0020))
        ];

        foreach ((ushort function, byte[] payload) in unsupported)
        {
            byte[] fixture = CreatePlaybackWmf(
            [
                (0x041F, WmfSetPixel(Color.Red, new Point(1, 1))),
                (function, payload),
                (0, [])
            ]);
            using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
            var context = new DrawingContext();
            using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains("source", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(context.Commands);
        }
    }

    [Fact]
    public void WmfMalformedBitmap16RecordsRollBackEarlierCommands()
    {
        byte[] valid = CreateBitmap16(2, 1, 24, [0, 0, 255, 0, 255, 0]);
        byte[] wrongStride = (byte[])valid.Clone();
        WriteInt16(wrongStride, 6, 8);
        byte[] wrongPlanes = (byte[])valid.Clone();
        wrongPlanes[8] = 2;
        byte[] zeroHeight = (byte[])valid.Clone();
        WriteInt16(zeroHeight, 4, 0);
        byte[] truncated = valid[..^2];

        foreach (byte[] bitmap in new[] { wrongStride, wrongPlanes, zeroHeight, truncated })
        {
            byte[] fixture = CreatePlaybackWmf(
            [
                (0x041F, WmfSetPixel(Color.Red, new Point(1, 1))),
                (0x0922, WmfBitmap16BitBlt(
                    bitmap,
                    Point.Empty,
                    new Rectangle(8, 8, 8, 8),
                    0x00F0_0021)),
                (0, [])
            ]);
            using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
            var context = new DrawingContext();
            using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

            Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Empty(context.Commands);
        }
    }

    [Fact]
    public void WmfSourceIndependentBitmapPlaybackHasBoundedWarmedAllocation()
    {
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x02FC, WmfBrush(Color.Green)),
            (0x012D, WmfWords(0))
        };
        for (int index = 0; index < 64; index++)
        {
            records.Add((
                0x0922,
                WmfBitBltWithoutBitmap(
                    Point.Empty,
                    new Rectangle((index % 8) * 8, (index / 8) * 8, 8, 8),
                    0x00F0_0021)));
        }
        records.Add((0, []));
        using var metafile = new Metafile(new MemoryStream(
            CreatePlaybackWmf(records),
            writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }
        long allocatedPerPlayback =
            (GC.GetAllocatedBytesForCurrentThread() - before) / 8;

        Assert.InRange(allocatedPerPlayback, 32 * 1024, 8 * 1024 * 1024);
    }

    [Fact]
    public void WmfDibPlaybackHasBoundedWarmedAllocation()
    {
        TestDib dib = CreateRgbDib(1, -1, 32, [0, 0, 255, 0]);
        var records = new List<(ushort Function, byte[] Payload)>();
        for (int index = 0; index < 64; index++)
        {
            records.Add((
                0x0F43,
                WmfStretchDib(
                    dib,
                    new Rectangle(0, 0, 1, 1),
                    new Rectangle((index % 8) * 8, (index / 8) * 8, 8, 8))));
        }
        records.Add((0, []));
        using var metafile = new Metafile(new MemoryStream(
            CreatePlaybackWmf(records),
            writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }
        long allocatedPerPlayback =
            (GC.GetAllocatedBytesForCurrentThread() - before) / 8;

        Assert.InRange(allocatedPerPlayback, 64 * 1024, 16 * 1024 * 1024);
    }

    [Fact]
    public void LogicalPaletteDibPlaybackHasBoundedWarmedAllocation()
    {
        TestDib dib = CreateLogicalPaletteDib(
            1,
            -1,
            [0, 0, 0, 0],
            [],
            directIndices: true);
        var records = new List<(ushort Function, byte[] Payload)>
        {
            (0x00F7, WmfPalette(0x0300, [Color.Red])),
            (0x0234, WmfWords(0))
        };
        for (int index = 0; index < 64; index++)
        {
            records.Add((
                0x0F43,
                WmfStretchDib(
                    dib,
                    new Rectangle(0, 0, 1, 1),
                    new Rectangle((index % 8) * 8, (index / 8) * 8, 8, 8),
                    usage: 2)));
        }
        records.Add((0, []));
        using var metafile = new Metafile(new MemoryStream(
            CreatePlaybackWmf(records),
            writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
            context.Clear();
        }
        long allocatedPerPlayback =
            (GC.GetAllocatedBytesForCurrentThread() - before) / 8;

        Assert.InRange(allocatedPerPlayback, 64 * 1024, 16 * 1024 * 1024);
    }

    [Fact]
    public void EmfPathBracketStoresGeometryInDeviceCoordinatesBeforeFill()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0000)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfBeginPath, []),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(4, 4, 20, 20)),
            (EmfPlusRecordType.EmfSetWorldTransform, EmfTransform(24f, 0f)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(4, 4, 20, 20)),
            (EmfPlusRecordType.EmfEndPath, []),
            (EmfPlusRecordType.EmfSetWorldTransform, EmfTransform(0f, 0f)),
            (EmfPlusRecordType.EmfFillPath, EmfRectangle(4, 4, 44, 20))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.White.ToArgb(), target.GetPixel(12, 12).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(36, 12).ToArgb());
        Assert.Equal(0, target.GetPixel(52, 12).A);
    }

    [Fact]
    public void EmfMoveToInsidePathRetainsItsOriginalDevicePosition()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0005)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0007)),
            (EmfPlusRecordType.EmfBeginPath, []),
            (EmfPlusRecordType.EmfSetWorldTransform, EmfTransform(24f, 0f)),
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(4, 12)),
            (EmfPlusRecordType.EmfSetWorldTransform, EmfTransform(0f, 0f)),
            (EmfPlusRecordType.EmfLineTo, EmfPoint(20, 12)),
            (EmfPlusRecordType.EmfEndPath, []),
            (EmfPlusRecordType.EmfStrokePath, EmfRectangle(20, 12, 28, 12))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.True(target.GetPixel(24, 12).A > 0);
        Assert.Equal(0, target.GetPixel(8, 12).A);
    }

    [Fact]
    public void EmfCloseFigureAndStrokePathCloseTheCurrentSubpath()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0005)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0007)),
            (EmfPlusRecordType.EmfBeginPath, []),
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(8, 8)),
            (EmfPlusRecordType.EmfLineTo, EmfPoint(24, 8)),
            (EmfPlusRecordType.EmfLineTo, EmfPoint(16, 24)),
            (EmfPlusRecordType.EmfCloseFigure, []),
            (EmfPlusRecordType.EmfEndPath, []),
            (EmfPlusRecordType.EmfStrokePath, EmfRectangle(8, 8, 24, 24))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.True(target.GetPixel(12, 16).A > 0);
        Assert.Equal(0, target.GetPixel(4, 16).A);
    }

    [Fact]
    public void EmfFlattenAndWidenPathCreateFillableStrokeGeometry()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfCreatePen, EmfPen(1, 4, Color.Black)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(1)),
            (EmfPlusRecordType.EmfBeginPath, []),
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(8, 16)),
            (EmfPlusRecordType.EmfLineTo, EmfPoint(56, 16)),
            (EmfPlusRecordType.EmfEndPath, []),
            (EmfPlusRecordType.EmfFlattenPath, []),
            (EmfPlusRecordType.EmfSetMiterLimit, EmfSingle(4f)),
            (EmfPlusRecordType.EmfWidenPath, []),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfFillPath, EmfRectangle(6, 12, 58, 20))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(32, 16).ToArgb());
        Assert.Equal(0, target.GetPixel(32, 24).A);
    }

    [Fact]
    public void EmfSelectClipPathConsumesTheCurrentPath()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfBeginPath, []),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(8, 8, 32, 32)),
            (EmfPlusRecordType.EmfEndPath, []),
            (EmfPlusRecordType.EmfSelectClipPath, EmfInt32(5)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(16, 16).ToArgb());
        Assert.Equal(0, target.GetPixel(40, 16).A);
    }

    [Fact]
    public void EmfAbortPathDiscardsOnlyTheBuildingPath()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0000)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfBeginPath, []),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(4, 4, 20, 20)),
            (EmfPlusRecordType.EmfAbortPath, []),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(28, 4, 44, 20))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(0, target.GetPixel(12, 12).A);
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(36, 12).ToArgb());
    }

    [Fact]
    public void EmfInvalidPathStateRollsBackEarlierGeometry()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64)),
            (EmfPlusRecordType.EmfEndPath, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.EmfEndPath), exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(12, 12).ToArgb());
    }

    [Fact]
    public void EmfNonPathPixelInsideBracketFailsWithoutPublishing()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64)),
            (EmfPlusRecordType.EmfBeginPath, []),
            (EmfPlusRecordType.EmfSetPixelV, EmfSetPixel(new Point(4, 4), Color.Red))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.EmfSetPixelV), exception.Message, StringComparison.Ordinal);
            Assert.Contains("path bracket", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(12, 12).ToArgb());
    }

    [Fact]
    public void EmfTextInsidePathFailsWithoutPublishing()
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0008)),
            (EmfPlusRecordType.EmfRectangle, EmfRectangle(0, 0, 64, 64)),
            (EmfPlusRecordType.EmfBeginPath, []),
            (EmfPlusRecordType.EmfExtTextOutW,
                EmfExtTextOutW("M", new Point(4, 4), 0, Rectangle.Empty, null))
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Blue);
            NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
                graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));
            Assert.Contains(nameof(EmfPlusRecordType.EmfExtTextOutW), exception.Message, StringComparison.Ordinal);
            Assert.Contains("outline capture", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(12, 12).ToArgb());
    }

    public static IEnumerable<object[]> EmfPathVectorRecords()
    {
        Point[] triangle = [new(8, 8), new(32, 8), new(20, 32)];
        Point[] line = [new(8, 12), new(24, 20), new(40, 12)];
        Point[] bezier = [new(8, 24), new(16, 4), new(32, 4), new(40, 24)];
        Rectangle arcBounds = new(8, 8, 32, 32);
        Point arcStart = new(40, 24);
        Point arcEnd = new(24, 40);
        yield return [EmfPlusRecordType.EmfRectangle, EmfRectangle(8, 8, 40, 32)];
        yield return [EmfPlusRecordType.EmfEllipse, EmfRectangle(8, 8, 40, 32)];
        yield return [EmfPlusRecordType.EmfRoundRect, EmfRoundRectangle(new Rectangle(8, 8, 32, 24), new Size(8, 8))];
        yield return [EmfPlusRecordType.EmfPolygon, EmfPointArray(triangle, points16: false)];
        yield return [EmfPlusRecordType.EmfPolyline16, EmfPointArray(line, points16: true)];
        yield return [EmfPlusRecordType.EmfPolyPolygon16, EmfPolyPoly([3], triangle, points16: true)];
        yield return [EmfPlusRecordType.EmfPolyPolyline, EmfPolyPoly([3], line, points16: false)];
        yield return [EmfPlusRecordType.EmfPolyBezier, EmfPointArray(bezier, points16: false)];
        yield return [EmfPlusRecordType.EmfPolyBezierTo16, EmfPointArray(bezier[1..], points16: true)];
        yield return [EmfPlusRecordType.EmfPolylineTo16, EmfPointArray(line, points16: true)];
        yield return [EmfPlusRecordType.EmfPolyDraw16, EmfPolyDraw(line, [0x06, 0x02, 0x03], points16: true)];
        yield return [EmfPlusRecordType.EmfRoundArc, EmfArc(arcBounds, arcStart, arcEnd)];
        yield return [EmfPlusRecordType.EmfPie, EmfArc(arcBounds, arcStart, arcEnd)];
        yield return [EmfPlusRecordType.EmfChord, EmfArc(arcBounds, arcStart, arcEnd)];
        yield return [EmfPlusRecordType.EmfArcTo, EmfArc(arcBounds, arcStart, arcEnd)];
        yield return [EmfPlusRecordType.EmfAngleArc, EmfAngleArc(new Point(24, 24), 16, 0f, 90f)];
    }

    [Theory]
    [MemberData(nameof(EmfPathVectorRecords))]
    public void EmfPathBracketCapturesEverySupportedVectorBeforeAbort(
        EmfPlusRecordType type,
        byte[] payload)
    {
        byte[] fixture = CreateTextPlaybackEmf(
        [
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0004)),
            (EmfPlusRecordType.EmfSelectObject, EmfUInt32(0x8000_0007)),
            (EmfPlusRecordType.EmfMoveToEx, EmfPoint(8, 24)),
            (EmfPlusRecordType.EmfBeginPath, []),
            (type, payload),
            (EmfPlusRecordType.EmfAbortPath, [])
        ]);
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        using var target = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64));
        }

        Assert.Equal(0, target.GetPixel(24, 24).A);
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
        int maximumRecordWords = records.Max(record => (record.Payload.Length + 7) / 2);
        int declaredWords = 9 + records.Sum(record => (record.Payload.Length + 7) / 2);
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
            WriteUInt32(bytes, cursor, (uint)((payload.Length + 7) / 2));
            WriteUInt16(bytes, cursor + 4, function);
            payload.CopyTo(bytes, cursor + 6);
            cursor += checked((payload.Length + 7) & ~1);
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

    private static byte[] WmfDibBitBlt(
        in TestDib dib,
        Rectangle source,
        Point destination,
        uint rasterOperation = 0x00CC_0020)
    {
        const int fixedPayloadSize = 16;
        byte[] payload = CreatePackedWmfDibPayload(dib, fixedPayloadSize);
        WriteUInt32(payload, 0, rasterOperation);
        WriteInt16(payload, 4, checked((short)source.Y));
        WriteInt16(payload, 6, checked((short)source.X));
        WriteInt16(payload, 8, checked((short)source.Height));
        WriteInt16(payload, 10, checked((short)source.Width));
        WriteInt16(payload, 12, checked((short)destination.Y));
        WriteInt16(payload, 14, checked((short)destination.X));
        return payload;
    }

    private static byte[] WmfBitBltWithoutBitmap(
        Point source,
        Rectangle destination,
        uint rasterOperation)
    {
        byte[] payload = new byte[18];
        WriteUInt32(payload, 0, rasterOperation);
        WriteInt16(payload, 4, checked((short)source.Y));
        WriteInt16(payload, 6, checked((short)source.X));
        WriteInt16(payload, 10, checked((short)destination.Height));
        WriteInt16(payload, 12, checked((short)destination.Width));
        WriteInt16(payload, 14, checked((short)destination.Y));
        WriteInt16(payload, 16, checked((short)destination.X));
        return payload;
    }

    private static byte[] WmfStretchBltWithoutBitmap(
        Rectangle source,
        Rectangle destination,
        uint rasterOperation)
    {
        byte[] payload = new byte[22];
        WriteUInt32(payload, 0, rasterOperation);
        WriteInt16(payload, 4, checked((short)source.Height));
        WriteInt16(payload, 6, checked((short)source.Width));
        WriteInt16(payload, 8, checked((short)source.Y));
        WriteInt16(payload, 10, checked((short)source.X));
        WriteInt16(payload, 14, checked((short)destination.Height));
        WriteInt16(payload, 16, checked((short)destination.Width));
        WriteInt16(payload, 18, checked((short)destination.Y));
        WriteInt16(payload, 20, checked((short)destination.X));
        return payload;
    }

    private static byte[] WmfBitmap16BitBlt(
        byte[] bitmap,
        Point source,
        Rectangle destination,
        uint rasterOperation)
    {
        byte[] payload = new byte[checked(16 + bitmap.Length)];
        WriteUInt32(payload, 0, rasterOperation);
        WriteInt16(payload, 4, checked((short)source.Y));
        WriteInt16(payload, 6, checked((short)source.X));
        WriteInt16(payload, 8, checked((short)destination.Height));
        WriteInt16(payload, 10, checked((short)destination.Width));
        WriteInt16(payload, 12, checked((short)destination.Y));
        WriteInt16(payload, 14, checked((short)destination.X));
        bitmap.CopyTo(payload, 16);
        return payload;
    }

    private static byte[] CreateBitmap16(
        short width,
        short height,
        byte bitsPerPixel,
        byte[] bits)
    {
        int widthBytes = checked((int)((((long)width * bitsPerPixel + 15) >> 4) << 1));
        byte[] bitmap = new byte[checked(10 + bits.Length)];
        WriteInt16(bitmap, 2, width);
        WriteInt16(bitmap, 4, height);
        WriteInt16(bitmap, 6, checked((short)widthBytes));
        bitmap[8] = 1;
        bitmap[9] = bitsPerPixel;
        bits.CopyTo(bitmap, 10);
        return bitmap;
    }

    private static byte[] WmfDibStretchBlt(
        in TestDib dib,
        Rectangle source,
        Rectangle destination,
        uint rasterOperation = 0x00CC_0020)
    {
        const int fixedPayloadSize = 20;
        byte[] payload = CreatePackedWmfDibPayload(dib, fixedPayloadSize);
        WriteUInt32(payload, 0, rasterOperation);
        WriteInt16(payload, 4, checked((short)source.Height));
        WriteInt16(payload, 6, checked((short)source.Width));
        WriteInt16(payload, 8, checked((short)source.Y));
        WriteInt16(payload, 10, checked((short)source.X));
        WriteInt16(payload, 12, checked((short)destination.Height));
        WriteInt16(payload, 14, checked((short)destination.Width));
        WriteInt16(payload, 16, checked((short)destination.Y));
        WriteInt16(payload, 18, checked((short)destination.X));
        return payload;
    }

    private static byte[] WmfStretchDib(
        in TestDib dib,
        Rectangle source,
        Rectangle destination,
        ushort usage = 0,
        uint rasterOperation = 0x00CC_0020)
    {
        const int fixedPayloadSize = 22;
        byte[] payload = CreatePackedWmfDibPayload(dib, fixedPayloadSize);
        WriteUInt32(payload, 0, rasterOperation);
        WriteUInt16(payload, 4, usage);
        WriteInt16(payload, 6, checked((short)source.Height));
        WriteInt16(payload, 8, checked((short)source.Width));
        WriteInt16(payload, 10, checked((short)source.Y));
        WriteInt16(payload, 12, checked((short)source.X));
        WriteInt16(payload, 14, checked((short)destination.Height));
        WriteInt16(payload, 16, checked((short)destination.Width));
        WriteInt16(payload, 18, checked((short)destination.Y));
        WriteInt16(payload, 20, checked((short)destination.X));
        return payload;
    }

    private static byte[] WmfSetDibToDevice(
        in TestDib dib,
        Rectangle source,
        Point destination,
        ushort startScan,
        ushort scanCount,
        ushort usage = 0)
    {
        const int fixedPayloadSize = 18;
        byte[] payload = CreatePackedWmfDibPayload(dib, fixedPayloadSize);
        WriteUInt16(payload, 0, usage);
        WriteUInt16(payload, 2, scanCount);
        WriteUInt16(payload, 4, startScan);
        WriteUInt16(payload, 6, checked((ushort)source.Y));
        WriteUInt16(payload, 8, checked((ushort)source.X));
        WriteUInt16(payload, 10, checked((ushort)source.Height));
        WriteUInt16(payload, 12, checked((ushort)source.Width));
        WriteUInt16(payload, 14, checked((ushort)destination.Y));
        WriteUInt16(payload, 16, checked((ushort)destination.X));
        return payload;
    }

    private static byte[] CreatePackedWmfDibPayload(in TestDib dib, int fixedPayloadSize)
    {
        byte[] payload = new byte[checked(fixedPayloadSize + dib.Info.Length + dib.Bits.Length)];
        dib.Info.CopyTo(payload, fixedPayloadSize);
        dib.Bits.CopyTo(payload, fixedPayloadSize + dib.Info.Length);
        return payload;
    }

    private static byte[] WmfColor(Color color)
    {
        byte[] bytes = new byte[4];
        WriteUInt32(bytes, 0, (uint)(color.R | color.G << 8 | color.B << 16));
        return bytes;
    }

    private static byte[] WmfFont(
        short height,
        string faceName,
        byte charSet = 1,
        bool underline = false,
        bool strikeout = false,
        short escapement = 0,
        short? orientation = null)
    {
        byte[] bytes = new byte[50];
        WriteInt16(bytes, 0, height);
        WriteInt16(bytes, 4, escapement);
        WriteInt16(bytes, 6, orientation ?? escapement);
        WriteInt16(bytes, 8, 400);
        bytes[11] = underline ? (byte)1 : (byte)0;
        bytes[12] = strikeout ? (byte)1 : (byte)0;
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

    private static Rectangle GetPaintedBounds(Bitmap bitmap)
    {
        int left = bitmap.Width;
        int top = bitmap.Height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A == 0)
                {
                    continue;
                }
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < left || bottom < top
            ? Rectangle.Empty
            : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
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

    private static byte[] WmfPalette(
        ushort start,
        Color[] colors,
        byte[]? flags = null)
    {
        if (flags is not null && flags.Length != colors.Length)
        {
            throw new ArgumentException("Palette flags must match the color count.", nameof(flags));
        }
        byte[] bytes = new byte[checked(4 + colors.Length * 4)];
        WriteUInt16(bytes, 0, start);
        WriteUInt16(bytes, 2, checked((ushort)colors.Length));
        WritePaletteEntries(bytes, 4, colors, flags);
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

    private static byte[] CreateTextPlaybackEmf(
        IReadOnlyList<(EmfPlusRecordType Type, byte[] Payload)> records)
    {
        int totalBytes = checked(88 + 20 + records.Sum(static record => 8 + record.Payload.Length));
        byte[] bytes = new byte[totalBytes];
        WriteUInt32(bytes, 0, (uint)EmfPlusRecordType.EmfHeader);
        WriteUInt32(bytes, 4, 88);
        WriteInt32(bytes, 16, 64);
        WriteInt32(bytes, 20, 64);
        WriteInt32(bytes, 32, 1_693);
        WriteInt32(bytes, 36, 1_693);
        WriteUInt32(bytes, 40, 0x464D_4520);
        WriteUInt32(bytes, 44, 0x0001_0000);
        WriteUInt32(bytes, 48, (uint)totalBytes);
        WriteUInt32(bytes, 52, checked((uint)records.Count + 2));
        WriteUInt16(bytes, 56, 8);
        WriteInt32(bytes, 72, 64);
        WriteInt32(bytes, 76, 64);
        WriteInt32(bytes, 80, 17);
        WriteInt32(bytes, 84, 17);

        int cursor = 88;
        foreach ((EmfPlusRecordType type, byte[] payload) in records)
        {
            Assert.Equal(0, payload.Length & 3);
            WriteUInt32(bytes, cursor, (uint)type);
            WriteUInt32(bytes, cursor + 4, checked((uint)payload.Length + 8));
            payload.CopyTo(bytes, cursor + 8);
            cursor = checked(cursor + 8 + payload.Length);
        }

        WriteUInt32(bytes, cursor, (uint)EmfPlusRecordType.EmfEof);
        WriteUInt32(bytes, cursor + 4, 20);
        WriteUInt32(bytes, cursor + 16, 20);
        return bytes;
    }

    private static byte[] EmfFont(
        uint index,
        int height,
        string faceName,
        byte charSet = 1,
        bool underline = false,
        bool strikeout = false)
    {
        byte[] faceNameBytes = Encoding.Unicode.GetBytes(faceName);
        if (faceNameBytes.Length > 62)
        {
            throw new ArgumentException("The EMF test font face must fit LOGFONTW.", nameof(faceName));
        }

        byte[] payload = new byte[96];
        WriteUInt32(payload, 0, index);
        WriteInt32(payload, 4, height);
        WriteInt32(payload, 12, 0);
        WriteInt32(payload, 16, 0);
        WriteInt32(payload, 20, 400);
        payload[25] = underline ? (byte)1 : (byte)0;
        payload[26] = strikeout ? (byte)1 : (byte)0;
        payload[27] = charSet;
        faceNameBytes.CopyTo(payload, 32);
        return payload;
    }

    private static byte[] EmfExtTextOutW(
        string text,
        Point reference,
        uint options,
        Rectangle rectangle,
        int[]? advances,
        int stringPadding = 0)
    {
        if ((stringPadding & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stringPadding));
        }
        byte[] stringBytes = Encoding.Unicode.GetBytes(text);
        return EmfExtTextOut(
            text.Length,
            stringBytes,
            reference,
            options,
            rectangle,
            advances,
            stringPadding);
    }

    private static byte[] EmfExtTextOutA(
        string text,
        Point reference,
        uint options,
        Rectangle rectangle,
        int[]? advances,
        int codePage,
        int stringPadding = 0)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        byte[] stringBytes = encoding.GetBytes(text);
        return EmfExtTextOut(
            stringBytes.Length,
            stringBytes,
            reference,
            options,
            rectangle,
            advances,
            stringPadding);
    }

    private static byte[] EmfExtTextOutWPdy(
        string text,
        Point reference,
        Point[] advances,
        uint options = 0x0000_2000)
    {
        if (advances.Length != text.Length)
        {
            throw new ArgumentException(
                "One two-dimensional EMF advance is required per UTF-16 code unit.",
                nameof(advances));
        }
        byte[] payload = EmfExtTextOutW(
            text,
            reference,
            options,
            Rectangle.Empty,
            null);
        int advancesOffset = payload.Length;
        Array.Resize(ref payload, checked(payload.Length + advances.Length * 8));
        WriteUInt32(payload, 64, checked((uint)advancesOffset + 8));
        for (int index = 0; index < advances.Length; index++)
        {
            WriteUInt32(payload, advancesOffset + index * 8, checked((uint)advances[index].X));
            WriteUInt32(payload, advancesOffset + index * 8 + 4, checked((uint)advances[index].Y));
        }
        return payload;
    }

    private static byte[] EmfExtTextOut(
        int characterCount,
        byte[] stringBytes,
        Point reference,
        uint options,
        Rectangle rectangle,
        int[]? advances,
        int stringPadding)
    {
        if (stringPadding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stringPadding));
        }
        int stringOffset = checked(68 + stringPadding);
        int afterStringOffset = checked((stringOffset + stringBytes.Length + 3) & ~3);
        int advancesSize = advances is null ? 0 : checked(advances.Length * 4);
        if (advances is not null && advances.Length != characterCount)
        {
            throw new ArgumentException("One EMF advance is required per UTF-16 code unit.", nameof(advances));
        }

        byte[] payload = new byte[checked(afterStringOffset + advancesSize)];
        WriteUInt32(payload, 16, 1);
        WriteSingle(payload, 20, 1f);
        WriteSingle(payload, 24, 1f);
        WriteInt32(payload, 28, reference.X);
        WriteInt32(payload, 32, reference.Y);
        WriteUInt32(payload, 36, checked((uint)characterCount));
        WriteUInt32(payload, 40, checked((uint)stringOffset + 8));
        WriteUInt32(payload, 44, options);
        WriteInt32(payload, 48, rectangle.Left);
        WriteInt32(payload, 52, rectangle.Top);
        WriteInt32(payload, 56, rectangle.Right);
        WriteInt32(payload, 60, rectangle.Bottom);
        stringBytes.CopyTo(payload, stringOffset);
        if (advances is not null)
        {
            int advancesOffset = afterStringOffset;
            WriteUInt32(payload, 64, checked((uint)advancesOffset + 8));
            for (int index = 0; index < advances.Length; index++)
            {
                WriteUInt32(payload, advancesOffset + index * 4, checked((uint)advances[index]));
            }
        }
        return payload;
    }

    private static byte[] EmfPolyTextOutW(
        params (string Text, Point Reference, int[]? Advances)[] entries)
    {
        byte[][] stringBytes = entries
            .Select(static entry => Encoding.Unicode.GetBytes(entry.Text))
            .ToArray();
        return EmfPolyTextOut(entries, stringBytes, unicode: true, stringPadding: 0);
    }

    private static byte[] EmfPolyTextOutA(
        string text,
        Point reference,
        int[]? advances,
        int codePage,
        int stringPadding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        (string Text, Point Reference, int[]? Advances)[] entries =
            [(text, reference, advances)];
        return EmfPolyTextOut(
            entries,
            [encoding.GetBytes(text)],
            unicode: false,
            stringPadding);
    }

    private static byte[] EmfPolyTextOut(
        (string Text, Point Reference, int[]? Advances)[] entries,
        byte[][] stringBytes,
        bool unicode,
        int stringPadding)
    {
        const int EmrTextArrayOffset = 32;
        const int EmrTextSize = 40;
        if (stringPadding < 0 || entries.Length != stringBytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(stringPadding));
        }

        int dataOffset = checked(EmrTextArrayOffset + entries.Length * EmrTextSize + stringPadding);
        int[] stringOffsets = new int[entries.Length];
        int[] advancesOffsets = new int[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            if (unicode && (dataOffset & 1) != 0)
            {
                dataOffset++;
            }
            stringOffsets[index] = dataOffset;
            dataOffset = checked(dataOffset + stringBytes[index].Length);
            int alignedOffset = checked((dataOffset + 3) & ~3);
            if (entries[index].Advances is int[] advances)
            {
                int characterCount = unicode
                    ? entries[index].Text.Length
                    : stringBytes[index].Length;
                if (advances.Length != characterCount)
                {
                    throw new ArgumentException(
                        "One EMF advance is required per encoded character.",
                        nameof(entries));
                }
                advancesOffsets[index] = alignedOffset;
                dataOffset = checked(alignedOffset + advances.Length * 4);
            }
            else
            {
                dataOffset = alignedOffset;
            }
        }

        byte[] payload = new byte[dataOffset];
        WriteUInt32(payload, 16, 1);
        WriteSingle(payload, 20, 1f);
        WriteSingle(payload, 24, 1f);
        WriteUInt32(payload, 28, checked((uint)entries.Length));
        for (int index = 0; index < entries.Length; index++)
        {
            int descriptorOffset = EmrTextArrayOffset + index * EmrTextSize;
            WriteInt32(payload, descriptorOffset, entries[index].Reference.X);
            WriteInt32(payload, descriptorOffset + 4, entries[index].Reference.Y);
            int characterCount = unicode
                ? entries[index].Text.Length
                : stringBytes[index].Length;
            WriteUInt32(payload, descriptorOffset + 8, checked((uint)characterCount));
            WriteUInt32(payload, descriptorOffset + 12, checked((uint)stringOffsets[index] + 8));
            WriteUInt32(payload, descriptorOffset + 36,
                advancesOffsets[index] == 0
                    ? 0
                    : checked((uint)advancesOffsets[index] + 8));
            stringBytes[index].CopyTo(payload, stringOffsets[index]);
            if (entries[index].Advances is int[] advances)
            {
                for (int advanceIndex = 0; advanceIndex < advances.Length; advanceIndex++)
                {
                    WriteUInt32(
                        payload,
                        advancesOffsets[index] + advanceIndex * 4,
                        checked((uint)advances[advanceIndex]));
                }
            }
        }
        return payload;
    }

    private static byte[] EmfSmallTextOut(
        string text,
        Point reference,
        uint options,
        Rectangle rectangle)
    {
        bool hasRectangle = (options & 0x0000_0100) == 0;
        bool smallCharacters = (options & 0x0000_0200) != 0;
        byte[] textBytes;
        if (smallCharacters)
        {
            textBytes = Encoding.Latin1.GetBytes(text);
        }
        else
        {
            textBytes = Encoding.Unicode.GetBytes(text);
        }
        int textOffset = 28 + (hasRectangle ? 16 : 0);
        int payloadSize = checked((textOffset + textBytes.Length + 3) & ~3);
        byte[] payload = new byte[payloadSize];
        WriteInt32(payload, 0, reference.X);
        WriteInt32(payload, 4, reference.Y);
        WriteUInt32(payload, 8, checked((uint)text.Length));
        WriteUInt32(payload, 12, options);
        WriteUInt32(payload, 16, 1);
        WriteSingle(payload, 20, 1f);
        WriteSingle(payload, 24, 1f);
        if (hasRectangle)
        {
            WriteInt32(payload, 28, rectangle.Left);
            WriteInt32(payload, 32, rectangle.Top);
            WriteInt32(payload, 36, rectangle.Right);
            WriteInt32(payload, 40, rectangle.Bottom);
        }
        textBytes.CopyTo(payload, textOffset);
        return payload;
    }

    private static byte[] EmfInt32(int value)
    {
        byte[] payload = new byte[4];
        WriteInt32(payload, 0, value);
        return payload;
    }

    private static byte[] EmfUInt32(uint value)
    {
        byte[] payload = new byte[4];
        WriteUInt32(payload, 0, value);
        return payload;
    }

    private static byte[] EmfSingle(float value)
    {
        byte[] payload = new byte[4];
        WriteSingle(payload, 0, value);
        return payload;
    }

    private static byte[] EmfTransform(float offsetX, float offsetY)
    {
        byte[] payload = new byte[24];
        WriteSingle(payload, 0, 1f);
        WriteSingle(payload, 12, 1f);
        WriteSingle(payload, 16, offsetX);
        WriteSingle(payload, 20, offsetY);
        return payload;
    }

    private static byte[] EmfPalette(
        uint index,
        Color[] colors,
        ushort version = 0x0300)
    {
        byte[] payload = new byte[checked(8 + colors.Length * 4)];
        WriteUInt32(payload, 0, index);
        WriteUInt16(payload, 4, version);
        WriteUInt16(payload, 6, checked((ushort)colors.Length));
        WritePaletteEntries(payload, 8, colors);
        return payload;
    }

    private static byte[] EmfPaletteEntries(uint index, uint start, Color[] colors)
    {
        byte[] payload = new byte[checked(12 + colors.Length * 4)];
        WriteUInt32(payload, 0, index);
        WriteUInt32(payload, 4, start);
        WriteUInt32(payload, 8, checked((uint)colors.Length));
        WritePaletteEntries(payload, 12, colors);
        return payload;
    }

    private static byte[] EmfUInt32Pair(uint first, uint second)
    {
        byte[] payload = new byte[8];
        WriteUInt32(payload, 0, first);
        WriteUInt32(payload, 4, second);
        return payload;
    }

    private static void WritePaletteEntries(
        byte[] destination,
        int offset,
        Color[] colors,
        byte[]? flags = null)
    {
        for (int index = 0; index < colors.Length; index++)
        {
            Color color = colors[index];
            destination[offset + index * 4] = color.R;
            destination[offset + index * 4 + 1] = color.G;
            destination[offset + index * 4 + 2] = color.B;
            destination[offset + index * 4 + 3] = flags?[index] ?? 0;
        }
    }

    private static TestDib CreateRgbDib(
        int width,
        int signedHeight,
        ushort bitCount,
        byte[] bits,
        Color[]? palette = null)
    {
        int paletteCount = palette?.Length ?? 0;
        byte[] info = new byte[checked(40 + paletteCount * 4)];
        WriteUInt32(info, 0, 40);
        WriteInt32(info, 4, width);
        WriteInt32(info, 8, signedHeight);
        WriteUInt16(info, 12, 1);
        WriteUInt16(info, 14, bitCount);
        WriteUInt32(info, 16, 0);
        WriteUInt32(info, 20, checked((uint)bits.Length));
        WriteUInt32(info, 32, checked((uint)paletteCount));
        for (int index = 0; index < paletteCount; index++)
        {
            Color color = palette![index];
            int offset = 40 + index * 4;
            info[offset] = color.B;
            info[offset + 1] = color.G;
            info[offset + 2] = color.R;
        }
        return new TestDib(info, bits);
    }

    private static TestDib CreateLogicalPaletteDib(
        int width,
        int signedHeight,
        byte[] bits,
        ushort[] paletteIndices,
        bool directIndices = false)
    {
        byte[] info = new byte[checked(40 + (directIndices ? 0 : paletteIndices.Length * 2))];
        WriteUInt32(info, 0, 40);
        WriteInt32(info, 4, width);
        WriteInt32(info, 8, signedHeight);
        WriteUInt16(info, 12, 1);
        WriteUInt16(info, 14, 8);
        WriteUInt32(info, 16, 0);
        WriteUInt32(info, 20, checked((uint)bits.Length));
        WriteUInt32(info, 32, directIndices ? 0u : checked((uint)paletteIndices.Length));
        if (!directIndices)
        {
            for (int index = 0; index < paletteIndices.Length; index++)
            {
                WriteUInt16(info, 40 + index * 2, paletteIndices[index]);
            }
        }
        return new TestDib(info, bits);
    }

    private static TestDib CreateCmykDib(int width, int signedHeight, byte[] bits)
    {
        byte[] info = new byte[40];
        WriteUInt32(info, 0, 40);
        WriteInt32(info, 4, width);
        WriteInt32(info, 8, signedHeight);
        WriteUInt16(info, 12, 1);
        WriteUInt16(info, 14, 32);
        WriteUInt32(info, 16, 11);
        WriteUInt32(info, 20, checked((uint)bits.Length));
        return new TestDib(info, bits);
    }

    private static TestDib CreateBitFieldsDib(
        int width,
        int signedHeight,
        ushort bitCount,
        byte[] bits,
        uint redMask,
        uint greenMask,
        uint blueMask,
        uint alphaMask = 0,
        int headerSize = 40,
        Color[]? optimizationPalette = null)
    {
        int externalMaskBytes = headerSize == 40 ? 12 : 0;
        int paletteCount = optimizationPalette?.Length ?? 0;
        byte[] info = new byte[checked(headerSize + externalMaskBytes + paletteCount * 4)];
        WriteUInt32(info, 0, checked((uint)headerSize));
        WriteInt32(info, 4, width);
        WriteInt32(info, 8, signedHeight);
        WriteUInt16(info, 12, 1);
        WriteUInt16(info, 14, bitCount);
        WriteUInt32(info, 16, 3);
        WriteUInt32(info, 20, checked((uint)bits.Length));
        WriteUInt32(info, 32, checked((uint)paletteCount));
        WriteUInt32(info, 40, redMask);
        WriteUInt32(info, 44, greenMask);
        WriteUInt32(info, 48, blueMask);
        if (headerSize >= 108)
        {
            WriteUInt32(info, 52, alphaMask);
        }
        int paletteOffset = headerSize + externalMaskBytes;
        for (int index = 0; index < paletteCount; index++)
        {
            Color color = optimizationPalette![index];
            int offset = paletteOffset + index * 4;
            info[offset] = color.B;
            info[offset + 1] = color.G;
            info[offset + 2] = color.R;
        }
        return new TestDib(info, bits);
    }

    private static TestDib WithBitFieldMasks(
        in TestDib dib,
        uint redMask,
        uint greenMask,
        uint blueMask)
    {
        byte[] info = dib.Info.ToArray();
        WriteUInt32(info, 40, redMask);
        WriteUInt32(info, 44, greenMask);
        WriteUInt32(info, 48, blueMask);
        return new TestDib(info, dib.Bits);
    }

    private static TestDib CreateRleDib(
        int width,
        int signedHeight,
        ushort bitCount,
        byte[] bits,
        Color[] palette,
        uint? compression = null)
    {
        byte[] info = new byte[checked(40 + palette.Length * 4)];
        WriteUInt32(info, 0, 40);
        WriteInt32(info, 4, width);
        WriteInt32(info, 8, signedHeight);
        WriteUInt16(info, 12, 1);
        WriteUInt16(info, 14, bitCount);
        WriteUInt32(info, 16, compression ?? (bitCount == 8 ? 1u : 2u));
        WriteUInt32(info, 20, checked((uint)bits.Length));
        WriteUInt32(info, 32, checked((uint)palette.Length));
        for (int index = 0; index < palette.Length; index++)
        {
            Color color = palette[index];
            int offset = 40 + index * 4;
            info[offset] = color.B;
            info[offset + 1] = color.G;
            info[offset + 2] = color.R;
        }
        return new TestDib(info, bits);
    }

    private static TestDib WithRleImageSize(in TestDib dib, uint imageSize)
    {
        byte[] info = dib.Info.ToArray();
        WriteUInt32(info, 20, imageSize);
        return new TestDib(info, dib.Bits);
    }

    private static TestDib CreateEncodedDib(
        int width,
        int height,
        uint compression,
        Color[] pixels,
        bool forceOddSize = false)
    {
        if (pixels.Length != checked(width * height) || compression is not 4 and not 5)
        {
            throw new ArgumentException("The encoded DIB fixture is invalid.", nameof(pixels));
        }

        using var source = new Bitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                source.SetPixel(x, y, pixels[y * width + x]);
            }
        }
        using var stream = new MemoryStream();
        source.Save(stream, compression == 4 ? ImageFormat.Jpeg : ImageFormat.Png);
        byte[] bits = stream.ToArray();
        if (forceOddSize && (bits.Length & 1) == 0)
        {
            Array.Resize(ref bits, bits.Length + 1);
        }

        byte[] info = new byte[40];
        WriteUInt32(info, 0, 40);
        WriteInt32(info, 4, width);
        WriteInt32(info, 8, height);
        WriteUInt16(info, 12, 1);
        WriteUInt16(info, 14, 0);
        WriteUInt32(info, 16, compression);
        WriteUInt32(info, 20, checked((uint)bits.Length));
        return new TestDib(info, bits);
    }

    private static TestDib WithEncodedDibHeader(
        in TestDib dib,
        int? width = null,
        int? height = null,
        ushort? bitCount = null,
        uint? imageSize = null,
        uint? colorsUsed = null,
        uint? compression = null)
    {
        byte[] info = dib.Info.ToArray();
        if (width is int declaredWidth) WriteInt32(info, 4, declaredWidth);
        if (height is int declaredHeight) WriteInt32(info, 8, declaredHeight);
        if (bitCount is ushort declaredBitCount) WriteUInt16(info, 14, declaredBitCount);
        if (compression is uint declaredCompression) WriteUInt32(info, 16, declaredCompression);
        if (imageSize is uint declaredSize) WriteUInt32(info, 20, declaredSize);
        if (colorsUsed is uint declaredColors) WriteUInt32(info, 32, declaredColors);
        return new TestDib(info, dib.Bits);
    }

    private static void AssertDibPlaybackRollsBack(byte[] fixture)
    {
        using var metafile = new Metafile(new MemoryStream(fixture, writable: false));
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(metafile, new Rectangle(0, 0, 64, 64)));

        Assert.Contains("DIB", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.Commands);
    }

    private static byte[] EmfStretchDibits(
        in TestDib dib,
        Rectangle source,
        Rectangle destination,
        uint usage = 0,
        uint rasterOperation = 0x00CC_0020)
    {
        const int fixedPayloadSize = 72;
        const int recordHeaderSize = 8;
        int unalignedPayloadSize = checked(fixedPayloadSize + dib.Info.Length + dib.Bits.Length);
        byte[] payload = new byte[checked((unalignedPayloadSize + 3) & ~3)];
        WriteInt32(payload, 16, destination.X);
        WriteInt32(payload, 20, destination.Y);
        WriteInt32(payload, 24, source.X);
        WriteInt32(payload, 28, source.Y);
        WriteInt32(payload, 32, source.Width);
        WriteInt32(payload, 36, source.Height);
        WriteUInt32(payload, 40, recordHeaderSize + fixedPayloadSize);
        WriteUInt32(payload, 44, checked((uint)dib.Info.Length));
        WriteUInt32(payload, 48, checked((uint)(recordHeaderSize + fixedPayloadSize + dib.Info.Length)));
        WriteUInt32(payload, 52, checked((uint)dib.Bits.Length));
        WriteUInt32(payload, 56, usage);
        WriteUInt32(payload, 60, rasterOperation);
        WriteInt32(payload, 64, destination.Width);
        WriteInt32(payload, 68, destination.Height);
        dib.Info.CopyTo(payload, fixedPayloadSize);
        dib.Bits.CopyTo(payload, fixedPayloadSize + dib.Info.Length);
        return payload;
    }

    private static byte[] EmfSetDibitsToDevice(
        in TestDib dib,
        Rectangle source,
        Point destination,
        uint startScan,
        uint scanCount,
        uint usage = 0)
    {
        const int fixedPayloadSize = 68;
        const int recordHeaderSize = 8;
        int unalignedPayloadSize = checked(fixedPayloadSize + dib.Info.Length + dib.Bits.Length);
        byte[] payload = new byte[checked((unalignedPayloadSize + 3) & ~3)];
        WriteInt32(payload, 16, destination.X);
        WriteInt32(payload, 20, destination.Y);
        WriteInt32(payload, 24, source.X);
        WriteInt32(payload, 28, source.Y);
        WriteInt32(payload, 32, source.Width);
        WriteInt32(payload, 36, source.Height);
        WriteUInt32(payload, 40, recordHeaderSize + fixedPayloadSize);
        WriteUInt32(payload, 44, checked((uint)dib.Info.Length));
        WriteUInt32(payload, 48, checked((uint)(recordHeaderSize + fixedPayloadSize + dib.Info.Length)));
        WriteUInt32(payload, 52, checked((uint)dib.Bits.Length));
        WriteUInt32(payload, 56, usage);
        WriteUInt32(payload, 60, startScan);
        WriteUInt32(payload, 64, scanCount);
        dib.Info.CopyTo(payload, fixedPayloadSize);
        dib.Bits.CopyTo(payload, fixedPayloadSize + dib.Info.Length);
        return payload;
    }

    private readonly record struct TestDib(byte[] Info, byte[] Bits);

    private static byte[] EmfPen(uint index, int width, Color color)
    {
        byte[] payload = new byte[20];
        WriteUInt32(payload, 0, index);
        WriteUInt32(payload, 4, 0);
        WriteInt32(payload, 8, width);
        WriteUInt32(payload, 16, (uint)(color.R | color.G << 8 | color.B << 16));
        return payload;
    }

    private static byte[] EmfBrush(uint index, Color color)
    {
        byte[] payload = new byte[16];
        WriteUInt32(payload, 0, index);
        WriteUInt32(payload, 4, 0);
        WriteUInt32(payload, 8, (uint)(color.R | color.G << 8 | color.B << 16));
        return payload;
    }

    private static byte[] EmfExtSelectClipRegion(
        int mode,
        Rectangle[]? rectangles)
    {
        if (rectangles is null)
        {
            byte[] omitted = new byte[8];
            WriteInt32(omitted, 4, mode);
            return omitted;
        }

        const int regionHeaderSize = 32;
        const int rectangleSize = 16;
        int rectangleBytes = checked(rectangles.Length * rectangleSize);
        byte[] payload = new byte[checked(8 + regionHeaderSize + rectangleBytes)];
        WriteUInt32(payload, 0, checked((uint)(regionHeaderSize + rectangleBytes)));
        WriteInt32(payload, 4, mode);
        WriteUInt32(payload, 8, regionHeaderSize);
        WriteUInt32(payload, 12, 1);
        WriteUInt32(payload, 16, checked((uint)rectangles.Length));
        WriteUInt32(payload, 20, checked((uint)rectangleBytes));
        if (rectangles.Length == 0)
        {
            return payload;
        }

        int left = rectangles.Min(static rectangle => rectangle.Left);
        int top = rectangles.Min(static rectangle => rectangle.Top);
        int right = rectangles.Max(static rectangle => rectangle.Right);
        int bottom = rectangles.Max(static rectangle => rectangle.Bottom);
        WriteInt32(payload, 24, left);
        WriteInt32(payload, 28, top);
        WriteInt32(payload, 32, right);
        WriteInt32(payload, 36, bottom);
        for (int index = 0; index < rectangles.Length; index++)
        {
            Rectangle rectangle = rectangles[index];
            int offset = 8 + regionHeaderSize + index * rectangleSize;
            WriteInt32(payload, offset, rectangle.Left);
            WriteInt32(payload, offset + 4, rectangle.Top);
            WriteInt32(payload, offset + 8, rectangle.Right);
            WriteInt32(payload, offset + 12, rectangle.Bottom);
        }
        return payload;
    }

    private static byte[] EmfTextJustification(int extra, int breakCount)
    {
        byte[] payload = new byte[8];
        WriteInt32(payload, 0, extra);
        WriteInt32(payload, 4, breakCount);
        return payload;
    }

    private static byte[] EmfPoint(int x, int y)
    {
        byte[] payload = new byte[8];
        WriteInt32(payload, 0, x);
        WriteInt32(payload, 4, y);
        return payload;
    }

    private static byte[] EmfRectangle(int left, int top, int right, int bottom)
    {
        byte[] payload = new byte[16];
        WriteInt32(payload, 0, left);
        WriteInt32(payload, 4, top);
        WriteInt32(payload, 8, right);
        WriteInt32(payload, 12, bottom);
        return payload;
    }

    private static byte[] EmfArc(Rectangle rectangle, Point start, Point end)
    {
        byte[] payload = new byte[32];
        WriteInt32(payload, 0, rectangle.Left);
        WriteInt32(payload, 4, rectangle.Top);
        WriteInt32(payload, 8, rectangle.Right);
        WriteInt32(payload, 12, rectangle.Bottom);
        WriteInt32(payload, 16, start.X);
        WriteInt32(payload, 20, start.Y);
        WriteInt32(payload, 24, end.X);
        WriteInt32(payload, 28, end.Y);
        return payload;
    }

    private static byte[] EmfRoundRectangle(Rectangle rectangle, Size cornerEllipse)
    {
        byte[] payload = new byte[24];
        WriteInt32(payload, 0, rectangle.Left);
        WriteInt32(payload, 4, rectangle.Top);
        WriteInt32(payload, 8, rectangle.Right);
        WriteInt32(payload, 12, rectangle.Bottom);
        WriteInt32(payload, 16, cornerEllipse.Width);
        WriteInt32(payload, 20, cornerEllipse.Height);
        return payload;
    }

    private static byte[] EmfSetPixel(Point point, Color color)
    {
        byte[] payload = new byte[12];
        WriteInt32(payload, 0, point.X);
        WriteInt32(payload, 4, point.Y);
        WriteUInt32(payload, 8, (uint)(color.R | color.G << 8 | color.B << 16));
        return payload;
    }

    private static byte[] EmfPointArray(Point[] points, bool points16)
    {
        int pointSize = points16 ? 4 : 8;
        byte[] payload = new byte[checked(20 + points.Length * pointSize)];
        WriteUInt32(payload, 16, checked((uint)points.Length));
        for (int index = 0; index < points.Length; index++)
        {
            WriteEmfPoint(payload, 20 + index * pointSize, points[index], points16);
        }
        return payload;
    }

    private static byte[] EmfPolyPoly(int[] counts, Point[] points, bool points16)
    {
        Assert.Equal(points.Length, counts.Sum());
        int pointSize = points16 ? 4 : 8;
        int pointsOffset = checked(24 + counts.Length * 4);
        byte[] payload = new byte[checked(pointsOffset + points.Length * pointSize)];
        WriteUInt32(payload, 16, checked((uint)counts.Length));
        WriteUInt32(payload, 20, checked((uint)points.Length));
        for (int index = 0; index < counts.Length; index++)
        {
            WriteUInt32(payload, 24 + index * 4, checked((uint)counts[index]));
        }
        for (int index = 0; index < points.Length; index++)
        {
            WriteEmfPoint(payload, pointsOffset + index * pointSize, points[index], points16);
        }
        return payload;
    }

    private static byte[] EmfPolyDraw(Point[] points, byte[] types, bool points16)
    {
        Assert.Equal(points.Length, types.Length);
        int pointSize = points16 ? 4 : 8;
        int typesOffset = checked(20 + points.Length * pointSize);
        byte[] payload = new byte[checked((typesOffset + types.Length + 3) & ~3)];
        WriteUInt32(payload, 16, checked((uint)points.Length));
        for (int index = 0; index < points.Length; index++)
        {
            WriteEmfPoint(payload, 20 + index * pointSize, points[index], points16);
        }
        types.CopyTo(payload, typesOffset);
        return payload;
    }

    private static void WriteEmfPoint(byte[] payload, int offset, Point point, bool point16)
    {
        if (point16)
        {
            WriteInt16(payload, offset, checked((short)point.X));
            WriteInt16(payload, offset + 2, checked((short)point.Y));
        }
        else
        {
            WriteInt32(payload, offset, point.X);
            WriteInt32(payload, offset + 4, point.Y);
        }
    }

    private static byte[] EmfAngleArc(
        Point center,
        uint radius,
        float startAngle,
        float sweepAngle)
    {
        byte[] payload = new byte[20];
        WriteInt32(payload, 0, center.X);
        WriteInt32(payload, 4, center.Y);
        WriteUInt32(payload, 8, radius);
        WriteSingle(payload, 12, startAngle);
        WriteSingle(payload, 16, sweepAngle);
        return payload;
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

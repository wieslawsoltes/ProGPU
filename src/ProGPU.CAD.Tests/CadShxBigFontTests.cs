using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadShxBigFontTests
{
    [Fact]
    public void IndexedContainerRetainsRangesMetadataProgramsAndOwnedBytes()
    {
        byte[] source = BuildBigFont(
            [(0x82, 0x82)],
            CreateBigFontShapes(),
            trailingCrLf: true,
            sparseSlots: 2);

        CadShxFont font = CadShxFont.Parse(source);

        Assert.Equal(CadShxContainerKind.BigFont, font.ContainerKind);
        Assert.True(font.IsBigFont);
        Assert.True(font.IsTextFont);
        Assert.False(font.IsExtendedBigFont);
        Assert.Equal("PROGPU-BIGFONT", font.Name);
        Assert.Equal(10, font.Above);
        Assert.Equal(2, font.Below);
        Assert.Equal(0, font.Modes);
        Assert.Equal(0, font.BigFontCharacterWidth);
        Assert.Equal(
            new CadShxBigFontRange(0x82, 0x82),
            Assert.Single(font.BigFontRanges.ToArray()));
        Assert.True(font.IsBigFontLeadByte(0x82));
        Assert.False(font.IsBigFontLeadByte(0x81));
        Assert.True(font.TryGetShape(0x82A0, out CadShxShape? shape));
        Assert.Equal("HIRAGANA-A", shape!.Name);
        Assert.Equal(new byte[] { 7, 0x01, 0x23, 0 }, shape.Program.ToArray());

        source.AsSpan().Fill(0);
        Assert.Equal((byte)7, shape.Program.Span[0]);
    }

    [Fact]
    public void RegularBigFontInterpreterUsesBigEndianTwoByteSubshapeReferences()
    {
        CadShxGlyphCache cache = CreateBigFontCache();

        CadShxGlyph primitive = cache.GetGlyph(0x0123);
        CadShxGlyph character = cache.GetGlyph(0x82A0);

        Assert.True(character.HasGeometry);
        Assert.Equal(primitive.Advance, character.Advance);
        Assert.Equal(primitive.BoundsMin, character.BoundsMin);
        Assert.Equal(primitive.BoundsMax, character.BoundsMax);

        CadShxFont truncated = CadShxFont.Parse(BuildBigFont(
            [(0x82, 0x82)],
            [
                (0, "BROKEN", new byte[] { 10, 2, 0, 0 }),
                (0x82A0, "HIRAGANA-A", new byte[] { 7, 0x01, 0 }),
            ]));
        Assert.Throws<InvalidDataException>(() =>
            new CadShxGlyphCache(truncated).GetGlyph(0x82A0));
    }

    [Fact]
    public void LayoutUsesStrictDrawingCodePageAndExplicitLeadRanges()
    {
        CadShxGlyphCache primary = CreatePrimaryCache();
        CadShxGlyphCache big = CreateBigFontCache();

        var layout = new CadShxTextLayout(
            "Aあ\\U+3042\u00A0",
            primary,
            CadShxOrientation.Horizontal,
            null,
            big,
            "ANSI_932");

        Assert.Equal(
            new ushort[] { 0x0041, 0x82A0, 0x82A0, 0x0020 },
            layout.Glyphs.Span.ToArray().Select(item => item.Glyph.ShapeNumber));
        Assert.False(layout.Glyphs.Span[^1].IsBreakOpportunity);

        CadShxGlyphCache extension = CreateBigFontCache(
            [(0x7C, 0x7C)],
            [
                (0, "PROGPU-EXTENSION", new byte[] { 10, 2, 0, 0 }),
                (0x7C41, "GREEK-ALPHA", StrokeProgram()),
            ]);
        var escaped = new CadShxTextLayout(
            "|A",
            primary,
            CadShxOrientation.Horizontal,
            null,
            extension,
            "ANSI_1252");
        Assert.Equal(
            (ushort)0x7C41,
            Assert.Single(escaped.Glyphs.ToArray()).Glyph.ShapeNumber);

        Assert.Throws<NotSupportedException>(() => new CadShxTextLayout(
            "あ",
            primary,
            CadShxOrientation.Horizontal,
            null,
            big,
            "ANSI_1252"));
        Assert.Throws<NotSupportedException>(() => new CadShxTextLayout(
            "あ",
            primary,
            CadShxOrientation.Horizontal,
            null,
            big,
            "UNSUPPORTED_CODE_PAGE"));
        Assert.Throws<NotSupportedException>(() => new CadShxTextLayout(
            "😀",
            primary,
            CadShxOrientation.Horizontal,
            null,
            big,
            "ANSI_932"));
    }

    [Fact]
    public void ParserRejectsMalformedIndexedRangesRecordsAndTrailingData()
    {
        byte[] valid = BuildBigFont([(0x82, 0x82)], CreateBigFontShapes());
        byte[] badEntrySize = valid.ToArray();
        badEntrySize[25] = 7;
        byte[] badRange = valid.ToArray();
        badRange[31] = 0x83;
        badRange[33] = 0x82;
        byte[] duplicate = BuildBigFont(
            [(0x82, 0x82)],
            [
                (0, "HEADER", new byte[] { 10, 2, 0, 0 }),
                (0x82A0, "FIRST", StrokeProgram()),
                (0x82A0, "SECOND", StrokeProgram()),
            ]);
        byte[] trailing = [.. valid, 1];

        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(badEntrySize));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(badRange));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(duplicate));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(trailing));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(
            valid,
            new CadShxParseOptions { MaxShapeCount = 2 }));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(
            valid,
            new CadShxParseOptions { MaxShapeBytes = 7 }));
    }

    [Fact]
    public void ExtendedBigFontPrimitivePlacementUsesBasepointAndAnisotropicScale()
    {
        CadShxFont font = CadShxFont.Parse(BuildBigFont(
            [(0x82, 0x82)],
            [
                (0, "PROGPU-EXTENDED", new byte[] { 16, 0, 0, 16, 0 }),
                (0x82A0, "COMPOSITE", new byte[]
                {
                    2, 8, 10, 0,
                    7, 0, 0x01, 0x23, 2, 3, 8, 4,
                    1, 8, 1, 0,
                    0,
                }),
                (0x0123, "PRIMITIVE", StrokeProgram()),
            ]));

        Assert.True(font.IsExtendedBigFont);
        Assert.Equal(16, font.Above);
        Assert.Equal(0, font.Below);
        Assert.Equal(16, font.BigFontCharacterWidth);
        CadShxGeometry geometry = CadShxInterpreter.Interpret(font, 0x82A0);

        Assert.Equal(new System.Numerics.Vector2(11, 0), geometry.EndPoint);
        Assert.Equal(10, geometry.CommandCount);
        Assert.Equal(3, geometry.SegmentCount);
        Assert.Equal(2, geometry.Path.Figures.Count);
        Assert.Equal(
            new System.Numerics.Vector2(12, 3),
            geometry.Path.Figures[0].StartPoint);
        Assert.Equal(
            new System.Numerics.Vector2(14, 5),
            Assert.IsType<ProGPU.Vector.LineSegment>(
                geometry.Path.Figures[0].Segments[0]).Point);
        Assert.Equal(
            new System.Numerics.Vector2(16, 3),
            Assert.IsType<ProGPU.Vector.LineSegment>(
                geometry.Path.Figures[0].Segments[1]).Point);
        Assert.Equal(
            new System.Numerics.Vector2(10, 0),
            geometry.Path.Figures[1].StartPoint);
        Assert.Equal(
            new System.Numerics.Vector2(11, 0),
            Assert.IsType<ProGPU.Vector.LineSegment>(
                geometry.Path.Figures[1].Segments[0]).Point);
    }

    [Fact]
    public void ExtendedBigFontPrimitivePlacementRejectsMalformedOperands()
    {
        CadShxFont truncated = CadShxFont.Parse(BuildBigFont(
            [(0x82, 0x82)],
            [
                (0, "PROGPU-EXTENDED", new byte[] { 16, 0, 0, 16, 0 }),
                (0x82A0, "TRUNCATED", new byte[] { 7, 0, 1, 0x23, 0 }),
                (0x0123, "PRIMITIVE", StrokeProgram()),
            ]));
        CadShxFont zeroWidth = CadShxFont.Parse(BuildBigFont(
            [(0x82, 0x82)],
            [
                (0, "PROGPU-EXTENDED", new byte[] { 16, 0, 0, 16, 0 }),
                (0x82A0, "ZERO-WIDTH", new byte[] { 7, 0, 1, 0x23, 0, 0, 0, 8, 0 }),
                (0x0123, "PRIMITIVE", StrokeProgram()),
            ]));
        CadShxFont missing = CadShxFont.Parse(BuildBigFont(
            [(0x82, 0x82)],
            [
                (0, "PROGPU-EXTENDED", new byte[] { 16, 0, 0, 16, 0 }),
                (0x82A0, "MISSING", new byte[] { 7, 0, 1, 0x24, 0, 0, 8, 8, 0 }),
            ]));

        Assert.Throws<InvalidDataException>(() =>
            CadShxInterpreter.Interpret(truncated, 0x82A0));
        Assert.Throws<InvalidDataException>(() =>
            CadShxInterpreter.Interpret(zeroWidth, 0x82A0));
        Assert.Throws<InvalidDataException>(() =>
            CadShxInterpreter.Interpret(missing, 0x82A0));
    }

    [Fact]
    public void ExtendedBigFontPrimitiveTurnsCircularArcsIntoAnalyticEllipses()
    {
        CadShxFont font = CadShxFont.Parse(BuildBigFont(
            [(0x82, 0x82)],
            [
                (0, "PROGPU-EXTENDED", new byte[] { 16, 0, 0, 16, 0 }),
                (0x82A0, "ELLIPSE", new byte[] { 7, 0, 1, 0x23, 0, 0, 8, 4, 0 }),
                (0x0123, "CIRCLE", new byte[] { 2, 8, 1, 0, 1, 10, 1, 0, 0 }),
            ]));

        CadShxGeometry geometry = CadShxInterpreter.Interpret(font, 0x82A0);

        Assert.Equal(System.Numerics.Vector2.Zero, geometry.EndPoint);
        ProGPU.Vector.PathFigure figure = Assert.Single(geometry.Path.Figures);
        Assert.Equal(new System.Numerics.Vector2(0.5f, 0), figure.StartPoint);
        Assert.Equal(2, figure.Segments.Count);
        Assert.All(figure.Segments, segment =>
        {
            ProGPU.Vector.ArcSegment arc = Assert.IsType<ProGPU.Vector.ArcSegment>(segment);
            Assert.Equal(new System.Numerics.Vector2(0.5f, 0.25f), arc.Size);
        });
    }

    [Fact]
    public void CatalogResolvesPrimaryAndBigFontAsOneImmutableGeneration()
    {
        CadShxFontCatalog catalog = CreateCatalog();

        CadShxFontResolution resolution = catalog.Resolve(new CadShxFontRequest(
            "JAPANESE",
            "primary.shx",
            "japanese.shx"));

        Assert.NotNull(resolution.GlyphCache);
        Assert.NotNull(resolution.BigFontGlyphCache);
        Assert.False(resolution.GlyphCache!.Font.IsBigFont);
        Assert.True(resolution.BigFontGlyphCache!.Font.IsBigFont);
        Assert.Equal("primary.shx", resolution.ResolvedFontName);
        Assert.Equal("japanese.shx", resolution.ResolvedBigFontName);
        Assert.False(resolution.IsSubstitution);
        (CadShxGlyphCache? primary, string resolvedName, bool substitution) = resolution;
        Assert.Same(resolution.GlyphCache, primary);
        Assert.Equal("primary.shx", resolvedName);
        Assert.False(substitution);

        ICadShxFontResolver frozen = catalog.CreateResolverSnapshot();
        Assert.NotNull(frozen.Resolve(new CadShxFontRequest(
            "JAPANESE",
            "primary.shx",
            "japanese.shx")).BigFontGlyphCache);
        Assert.Throws<ArgumentException>(() => catalog.SetAlternate("japanese.shx"));

        catalog.Register("mapped-big.shx", CreateBigFontCache());
        catalog.SetMapping("japanese", "mapped-big.shx");
        CadShxFontResolution mapped = catalog.Resolve(new CadShxFontRequest(
            "JAPANESE",
            "primary.shx",
            "japanese.shx"));
        Assert.True(mapped.IsSubstitution);
        Assert.Equal("mapped-big.shx", mapped.ResolvedBigFontName);
    }

    [Fact]
    public void BigFontTextReusesRetainedManagedNativeSelectionAndPrintPaths()
    {
        CadDocumentSession session = CreateTextSession();
        CadDocumentSnapshot snapshot = Compile(session, CreateCatalog());

        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        Assert.Equal(CadEntityKind.ShxText, entity.Kind);
        Assert.Equal(2, snapshot.ShxGlyphInstances.Length);
        Assert.Equal(
            new ushort[] { 0x0041, 0x82A0 },
            snapshot.ShxGlyphInstances.Span.ToArray()
                .Select(item => item.Glyph.ShapeNumber));
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        Assert.Equal(2, scene.DrawingContext.Commands.Count);
        Assert.All(
            scene.DrawingContext.Commands,
            command => Assert.Equal(RenderCommandType.DrawPath, command.Type));

        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            entity.Handle,
            entity.Kind,
            entity.Bounds);
        Assert.True(CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(102, 204, 0),
            0.01).IsHit);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(2, native.SourceCommandCount);

        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        Assert.Equal(2, print.SceneStatistics.RecordedCommandCount);
    }

    [Fact]
    public void BigFontMTextAndComplexLineTypeReuseTheSameFontPair()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add Big Font MTEXT and linetype", document =>
        {
            document.Header.CodePage = "ANSI_932";
            TextStyle style = AddStyle(document);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = "Aあ A",
                InsertPoint = new XYZ(10, 20, 0),
                Height = 10,
                RectangleWidth = 20,
            });
            var lineType = new LineType("BIGFONT-LINE");
            lineType.AddSegment(new LineType.Segment { Length = 4 });
            lineType.AddSegment(new LineType.Segment { Length = -2 });
            lineType.AddSegment(new LineType.Segment
            {
                Text = "あ",
                Style = style,
                Scale = 2,
                Flags = LineTypeShapeFlags.Text,
            });
            lineType.AddSegment(new LineType.Segment { Length = -2 });
            document.LineTypes.Add(lineType);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(24, 0, 0))
            {
                LineType = lineType,
            });
        });

        CadDocumentSnapshot snapshot = Compile(session, CreateCatalog());
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        CadShxMTextPrimitive mtext = Assert.Single(snapshot.ShxMTexts.ToArray());
        Assert.Equal(4, mtext.GlyphCount);
        Assert.True(mtext.ContentHeight > 10.0f);
        Assert.Equal(CadLineTypeElementKind.ShxText, snapshot.LineTypeElements.Span[2].Kind);
        Assert.Single(snapshot.LineTypeTextResources.ToArray());
        Assert.True(scene.Statistics.LoweredLineTypePlacementCount > 0);
    }

    [Fact]
    public void VerticalBigFontMTextUsesBothAuthoredOrientationPrograms()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add vertical Big Font MTEXT", document =>
        {
            document.Header.CodePage = "ANSI_932";
            TextStyle style = AddStyle(document);
            style.Flags = StyleFlags.VerticalText;
            document.Entities.Add(new MText
            {
                Style = style,
                Value = "Aあ",
                Height = 10,
                DrawingDirection = DrawingDirectionType.TopToBottom,
            });
        });
        var catalog = new CadShxFontCatalog();
        catalog.Register("primary.shx", CreateVerticalPrimaryCache());
        catalog.Register("japanese.shx", CreateVerticalBigFontCache());

        CadDocumentSnapshot snapshot = Compile(session, catalog);
        CadShxMTextPrimitive text = Assert.Single(snapshot.ShxMTexts.ToArray());
        ReadOnlySpan<CadShxGlyphInstance> glyphs = snapshot.ShxGlyphInstances.Span;

        Assert.Equal(2, text.GlyphCount);
        Assert.Equal((ushort)'A', glyphs[text.GlyphOffset].Glyph.ShapeNumber);
        Assert.Equal((ushort)0x82A0, glyphs[text.GlyphOffset + 1].Glyph.ShapeNumber);
        Assert.All(
            glyphs.Slice(text.GlyphOffset, text.GlyphCount).ToArray(),
            glyph => Assert.Equal(CadShxOrientation.Vertical, glyph.Glyph.Orientation));
        Assert.True(glyphs[text.GlyphOffset + 1].Y > glyphs[text.GlyphOffset].Y);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task BigFontTextStyleCodePageAndContentRoundTripAndRecompile(
        CadDocumentFormat format)
    {
        CadDocumentSession session = CreateTextSession();
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            session,
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: format == CadDocumentFormat.Dxf
                ? "big-font.dxf"
                : "big-font.dwg");

        (string Value, string Primary, string Big, string CodePage) retained =
            loaded.Session.Read(document =>
            {
                TextEntity text = document.Entities.OfType<TextEntity>().Single();
                return (
                    text.Value,
                    text.Style.Filename,
                    text.Style.BigFontFilename,
                    document.Header.CodePage);
            });
        Assert.Equal("Aあ", retained.Value);
        Assert.Equal("primary.shx", retained.Primary);
        Assert.Equal("japanese.shx", retained.Big);
        Assert.True(
            retained.CodePage.Equals("ANSI_932", StringComparison.OrdinalIgnoreCase) ||
            retained.CodePage.Equals("dos932", StringComparison.OrdinalIgnoreCase));

        CadDocumentSnapshot snapshot = Compile(loaded.Session, CreateCatalog());
        Assert.Equal(CadEntityKind.ShxText, Assert.Single(snapshot.Entities.ToArray()).Kind);
        Assert.Equal(2, snapshot.ShxGlyphInstances.Length);
    }

    private static CadDocumentSession CreateTextSession()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add Big Font text", document =>
        {
            document.Header.CodePage = "ANSI_932";
            TextStyle style = AddStyle(document);
            document.Entities.Add(new TextEntity("Aあ")
            {
                Style = style,
                InsertPoint = new XYZ(100, 200, 0),
                Height = 10,
            });
        });
        return session;
    }

    private static TextStyle AddStyle(CadDocument document)
    {
        var style = new TextStyle("JAPANESE")
        {
            Filename = "primary.shx",
            BigFontFilename = "japanese.shx",
        };
        document.TextStyles.Add(style);
        return style;
    }

    private static CadDocumentSnapshot Compile(
        CadDocumentSession session,
        CadShxFontCatalog catalog) =>
        new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { ShxFontResolver = catalog });

    private static CadShxFontCatalog CreateCatalog()
    {
        var catalog = new CadShxFontCatalog();
        catalog.Register("primary.shx", CreatePrimaryCache());
        catalog.Register("japanese.shx", CreateBigFontCache());
        return catalog;
    }

    private static CadShxGlyphCache CreatePrimaryCache() =>
        new(CadShxFont.Parse(BuildStandardFont()));

    private static CadShxGlyphCache CreateVerticalPrimaryCache() =>
        new(CadShxFont.Parse(BuildStandardFont(vertical: true)));

    private static CadShxGlyphCache CreateBigFontCache() =>
        CreateBigFontCache([(0x82, 0x82)], CreateBigFontShapes());

    private static CadShxGlyphCache CreateBigFontCache(
        (byte Start, byte End)[] ranges,
        (ushort Number, string Name, byte[] Program)[] shapes) =>
        new(CadShxFont.Parse(BuildBigFont(ranges, shapes)));

    private static CadShxGlyphCache CreateVerticalBigFontCache() =>
        CreateBigFontCache(
            [(0x82, 0x82)],
            [
                (0, "PROGPU-VERTICAL-BIGFONT", new byte[] { 10, 2, 2, 0 }),
                (0x82A0, "HIRAGANA-A", VerticalStrokeProgram()),
            ]);

    private static (ushort Number, string Name, byte[] Program)[]
        CreateBigFontShapes() =>
        [
            (0, "PROGPU-BIGFONT", new byte[] { 10, 2, 0, 0 }),
            (0x0123, "PRIMITIVE", StrokeProgram()),
            (0x82A0, "HIRAGANA-A", new byte[] { 7, 0x01, 0x23, 0 }),
        ];

    private static byte[] StrokeProgram() =>
        new byte[] { 1, 8, 4, 8, 8, 4, unchecked((byte)-8), 0 };

    private static byte[] VerticalStrokeProgram() =>
    [
        2, 14, 8, 0xFF, 2,
        1, 0x14,
        2, 8, 2, 0xFF,
        14, 8, 0xFF, 0xFD,
        0,
    ];

    private static byte[] BuildStandardFont(bool vertical = false)
    {
        (ushort Number, string Name, byte[] Program)[] shapes =
        [
            (0, "PROGPU-PRIMARY", new byte[] { 10, 2, vertical ? (byte)2 : (byte)0, 0 }),
            (0x0020, "SPACE", vertical
                ? new byte[] { 2, 8, 4, 0, 14, 8, 0xFC, 0xFC, 0 }
                : new byte[] { 2, 8, 4, 0, 0 }),
            (0x0041, "A", vertical ? VerticalStrokeProgram() : StrokeProgram()),
            (0x0100, "DEGREE", StrokeProgram()),
            (0x0101, "PLUS-MINUS", StrokeProgram()),
            (0x0102, "DIAMETER", StrokeProgram()),
        ];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write((ushort)0);
        writer.Write((ushort)0x0102);
        writer.Write(checked((ushort)shapes.Length));
        foreach ((ushort number, string name, byte[] program) in shapes)
        {
            writer.Write(number);
            writer.Write(checked((ushort)(name.Length + 1 + program.Length)));
        }
        foreach ((ushort _, string name, byte[] program) in shapes)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
            writer.Write(program);
        }
        writer.Write("EOF"u8);
        return stream.ToArray();
    }

    private static byte[] BuildBigFont(
        (byte Start, byte End)[] ranges,
        (ushort Number, string Name, byte[] Program)[] shapes,
        bool trailingCrLf = false,
        int sparseSlots = 0)
    {
        byte[][] records = shapes.Select(shape =>
        {
            byte[] name = Encoding.ASCII.GetBytes(shape.Name);
            byte[] record = [.. name, 0, .. shape.Program];
            return record;
        }).ToArray();
        int offset = checked(
            "AutoCAD-86 bigfont 1.0\r\n\x1A"u8.Length +
            6 +
            (ranges.Length * 4) +
            ((shapes.Length + sparseSlots) * 8));

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 bigfont 1.0\r\n\x1A"u8);
        writer.Write((ushort)8);
        writer.Write(checked((ushort)(shapes.Length + sparseSlots)));
        writer.Write(checked((ushort)ranges.Length));
        foreach ((byte start, byte end) in ranges)
        {
            writer.Write((ushort)start);
            writer.Write((ushort)end);
        }
        for (int i = 0; i < shapes.Length; i++)
        {
            writer.Write((byte)(shapes[i].Number >> 8));
            writer.Write((byte)shapes[i].Number);
            writer.Write(checked((ushort)records[i].Length));
            writer.Write(checked((uint)offset));
            offset = checked(offset + records[i].Length);
        }
        for (int i = 0; i < sparseSlots; i++)
        {
            writer.Write(0UL);
        }
        foreach (byte[] record in records)
        {
            writer.Write(record);
        }
        if (trailingCrLf)
        {
            writer.Write((byte)'\r');
            writer.Write((byte)'\n');
        }
        return stream.ToArray();
    }
}

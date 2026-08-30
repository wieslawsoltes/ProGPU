using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadShxUnicodeTests
{
    [Fact]
    public void UnicodeContainerRetainsMetadataProgramsAndOwnedBytes()
    {
        byte[] source = BuildUnicodeShx(
            CadShxUnicodeEncoding.Unicode,
            CadShxEmbeddingPermissions.ReadOnly,
            CreateUnicodeShapes());

        CadShxFont font = CadShxFont.Parse(source);

        Assert.Equal(CadShxContainerKind.Unicode, font.ContainerKind);
        Assert.True(font.IsUnicodeFont);
        Assert.True(font.IsTextFont);
        Assert.Equal("PROGPU-UNICODE", font.Name);
        Assert.Equal(10, font.Above);
        Assert.Equal(2, font.Below);
        Assert.Equal(0, font.Modes);
        Assert.Equal(CadShxUnicodeEncoding.Unicode, font.UnicodeEncoding);
        Assert.Equal(
            CadShxEmbeddingPermissions.ReadOnly,
            font.EmbeddingPermissions);
        Assert.True(font.TryGetShape(0x20AC, out CadShxShape? euro));
        Assert.Equal("EURO", euro!.Name);
        Assert.Equal(new byte[] { 7, 0xA9, 0x03, 0 }, euro.Program.ToArray());

        source.AsSpan().Fill(0);
        Assert.Equal((byte)7, euro.Program.Span[0]);
    }

    [Fact]
    public void UnicodeInterpreterUsesTwoByteSubshapeReferences()
    {
        CadShxGlyphCache cache = CreateUnicodeCache();

        CadShxGlyph omega = cache.GetGlyph(0x03A9);
        CadShxGlyph euro = cache.GetGlyph(0x20AC);

        Assert.True(omega.HasGeometry);
        Assert.Equal(1, omega.SegmentCount);
        Assert.Equal(new System.Numerics.Vector2(8, 0), omega.Advance);
        Assert.Equal(omega.Advance, euro.Advance);
        Assert.Equal(omega.BoundsMin, euro.BoundsMin);
        Assert.Equal(omega.BoundsMax, euro.BoundsMax);
        CadShxFont truncatedReference = CadShxFont.Parse(BuildUnicodeShx(
            CadShxUnicodeEncoding.Unicode,
            CadShxEmbeddingPermissions.Embeddable,
            [
                (0, "BROKEN", new byte[] { 10, 2, 0, 0, 0, 0 }),
                (0x20AC, "EURO", new byte[] { 7, 0xA9, 0 }),
            ]));
        Assert.Throws<InvalidDataException>(() =>
            new CadShxGlyphCache(truncatedReference).GetGlyph(0x20AC));
    }

    [Fact]
    public void UnicodeLayoutMapsBmpTextEscapesControlsAndNonbreakingSpace()
    {
        CadShxGlyphCache cache = CreateUnicodeCache();

        var layout = new CadShxTextLayout(
            "AΩ€\\U+20AC%%d%%p%%c\u00A0",
            cache);
        CadShxGlyphPlacement[] glyphs = layout.Glyphs.ToArray();

        Assert.Equal(
            new ushort[]
            {
                0x0041,
                0x03A9,
                0x20AC,
                0x20AC,
                0x00B0,
                0x00B1,
                0x2205,
                0x00A0,
            },
            glyphs.Select(item => item.Glyph.ShapeNumber));
        Assert.False(glyphs[^1].IsBreakOpportunity);
        Assert.Equal(64.0f, layout.Advance.X);
        Assert.Throws<NotSupportedException>(() =>
            new CadShxTextLayout("😀", cache));

        CadShxGlyphCache packed = CreateUnicodeCache(
            CadShxUnicodeEncoding.PackedMultibyte1);
        Assert.Throws<NotSupportedException>(() =>
            new CadShxTextLayout("A", packed));
    }

    [Fact]
    public void UnicodeParserRejectsMalformedDuplicateBoundedAndBigFontInputs()
    {
        byte[] valid = BuildUnicodeShx(
            CadShxUnicodeEncoding.Unicode,
            CadShxEmbeddingPermissions.Embeddable,
            CreateUnicodeShapes());
        byte[] duplicate = BuildUnicodeShx(
            CadShxUnicodeEncoding.Unicode,
            CadShxEmbeddingPermissions.Embeddable,
            [
                (0, "HEADER", new byte[] { 10, 2, 0, 0, 0, 0 }),
                (65, "FIRST", new byte[] { 0 }),
                (65, "SECOND", new byte[] { 0 }),
            ]);
        byte[] trailing = [.. valid, 0];

        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(
            valid.AsSpan(0, valid.Length - 1)));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(trailing));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(duplicate));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(
            valid,
            new CadShxParseOptions { MaxShapeBytes = 8 }));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(
            valid,
            new CadShxParseOptions { MaxShapeCount = 2 }));
        Assert.Throws<InvalidDataException>(() => CadShxFont.Parse(
            "AutoCAD-86 bigfont 1.0\r\n\x1A"u8));
    }

    [Fact]
    public void UnicodeTextReusesRetainedManagedNativeSelectionAndPrintPaths()
    {
        CadShxFontCatalog catalog = CreateUnicodeCatalog();
        CadDocumentSession session = CreateTextSession();
        CadDocumentSnapshot snapshot = Compile(session, catalog);

        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        Assert.Equal(CadEntityKind.ShxText, entity.Kind);
        Assert.Equal(3, snapshot.ShxGlyphInstances.Length);
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();
        Assert.Equal(3, commands.Length);
        Assert.All(
            commands,
            command => Assert.Equal(RenderCommandType.DrawPath, command.Type));

        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            entity.Handle,
            entity.Kind,
            entity.Bounds);
        CadPointHitResult hit = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(100, 205, 0),
            0.01);
        Assert.True(hit.IsHit);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(3, native.SourceCommandCount);

        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        Assert.Equal(3, print.SceneStatistics.RecordedCommandCount);
    }

    [Fact]
    public void UnicodeMTextUsesTheExistingRetainedWrappingAndPathPipeline()
    {
        CadShxFontCatalog catalog = CreateUnicodeCatalog();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add Unicode SHX MTEXT", document =>
        {
            var style = new TextStyle("UNICODE-SHX")
            {
                Filename = "unicode.shx",
            };
            document.TextStyles.Add(style);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = "AΩ €",
                InsertPoint = new XYZ(10, 20, 0),
                Height = 10,
                RectangleWidth = 24,
            });
        });

        CadDocumentSnapshot snapshot = Compile(session, catalog);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadShxMTextPrimitive text = Assert.Single(snapshot.ShxMTexts.ToArray());

        Assert.Equal(CadEntityKind.ShxMText, entity.Kind);
        Assert.Equal(4, text.GlyphCount);
        Assert.Equal(4, snapshot.ShxGlyphInstances.Length);
        Assert.True(text.ContentHeight > 10.0f);
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        Assert.Equal(3, scene.DrawingContext.Commands.Count);
    }

    [Fact]
    public void UnicodeComplexLineTypeTextReusesTheResolvedGlyphResource()
    {
        CadShxFontCatalog catalog = CreateUnicodeCatalog();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add Unicode SHX complex linetype", document =>
        {
            var style = new TextStyle("UNICODE-SHX")
            {
                Filename = "unicode.shx",
            };
            document.TextStyles.Add(style);
            var lineType = new LineType("UNICODE-SHX-LINE");
            lineType.AddSegment(new LineType.Segment { Length = 4 });
            lineType.AddSegment(new LineType.Segment { Length = -2 });
            lineType.AddSegment(new LineType.Segment
            {
                Text = "Ω",
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

        CadDocumentSnapshot snapshot = Compile(session, catalog);
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(CadLineTypeElementKind.ShxText, snapshot.LineTypeElements.Span[2].Kind);
        Assert.Single(snapshot.LineTypeTextResources.ToArray());
        Assert.Single(snapshot.ShxGlyphInstances.ToArray());
        Assert.Equal(3, scene.Statistics.LoweredLineTypePlacementCount);
        Assert.Equal(4, scene.DrawingContext.Commands.Count);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task UnicodeTextAndStyleRoundTripAndRecompile(
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
                ? "unicode-shx.dxf"
                : "unicode-shx.dwg");

        (string Value, string Filename) retained = loaded.Session.Read(document =>
        {
            TextEntity text = document.Entities.OfType<TextEntity>().Single();
            return (text.Value, text.Style.Filename);
        });
        Assert.Equal("AΩ€", retained.Value);
        Assert.Equal("unicode.shx", retained.Filename);

        CadDocumentSnapshot snapshot = Compile(loaded.Session, CreateUnicodeCatalog());
        Assert.Equal(CadEntityKind.ShxText, Assert.Single(snapshot.Entities.ToArray()).Kind);
        Assert.Equal(3, snapshot.ShxGlyphInstances.Length);
    }

    private static CadDocumentSession CreateTextSession()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add Unicode SHX text", document =>
        {
            var style = new TextStyle("UNICODE-SHX")
            {
                Filename = "unicode.shx",
            };
            document.TextStyles.Add(style);
            document.Entities.Add(new TextEntity("AΩ€")
            {
                Style = style,
                InsertPoint = new XYZ(100, 200, 0),
                Height = 10,
            });
        });
        return session;
    }

    private static CadDocumentSnapshot Compile(
        CadDocumentSession session,
        CadShxFontCatalog catalog) =>
        new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { ShxFontResolver = catalog });

    private static CadShxFontCatalog CreateUnicodeCatalog()
    {
        var catalog = new CadShxFontCatalog();
        catalog.Register("unicode.shx", CreateUnicodeCache());
        return catalog;
    }

    private static CadShxGlyphCache CreateUnicodeCache(
        CadShxUnicodeEncoding encoding = CadShxUnicodeEncoding.Unicode) =>
        new(CadShxFont.Parse(BuildUnicodeShx(
            encoding,
            CadShxEmbeddingPermissions.Embeddable,
            CreateUnicodeShapes())));

    private static (ushort Number, string Name, byte[] Program)[]
        CreateUnicodeShapes() =>
        [
            (0, "PROGPU-UNICODE", new byte[] { 10, 2, 0, 0, 0, 0 }),
            (0x0020, "SPACE", new byte[] { 2, 0x80, 0 }),
            (0x0041, "LATIN-CAPITAL-A", new byte[] { 1, 8, 0, 10, 2, 8, 8, unchecked((byte)-10), 0 }),
            (0x00A0, "NO-BREAK-SPACE", new byte[] { 2, 0x80, 0 }),
            (0x00B0, "DEGREE", new byte[] { 7, 0x41, 0x00, 0 }),
            (0x00B1, "PLUS-MINUS", new byte[] { 7, 0x41, 0x00, 0 }),
            (0x03A9, "GREEK-CAPITAL-OMEGA", new byte[] { 7, 0x41, 0x00, 0 }),
            (0x20AC, "EURO", new byte[] { 7, 0xA9, 0x03, 0 }),
            (0x2205, "EMPTY-SET", new byte[] { 7, 0x41, 0x00, 0 }),
        ];

    private static byte[] BuildUnicodeShx(
        CadShxUnicodeEncoding encoding,
        CadShxEmbeddingPermissions embedding,
        params (ushort Number, string Name, byte[] Program)[] shapes)
    {
        (ushort Number, string Name, byte[] Program)[] records =
            shapes.Select(shape => shape.Number == 0
                ? (shape.Number, shape.Name, new byte[]
                {
                    shape.Program[0],
                    shape.Program[1],
                    shape.Program[2],
                    (byte)encoding,
                    (byte)embedding,
                    0,
                })
                : shape).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 unifont 1.0\r\n\x1A"u8);
        writer.Write(checked((ushort)records.Length));
        foreach ((ushort number, string name, byte[] program) in records)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            writer.Write(number);
            writer.Write(checked((ushort)(nameBytes.Length + 1 + program.Length)));
            writer.Write(nameBytes);
            writer.Write((byte)0);
            writer.Write(program);
        }
        return stream.ToArray();
    }
}

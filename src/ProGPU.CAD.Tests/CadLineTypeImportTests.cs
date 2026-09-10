using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadLineTypeImportTests
{
    [Fact]
    public void ImportCreatesSupportedDefinitionsAndSkipsOnlyTypedUprightEntries()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        CadLinFile file = Parse(
            "*DASHED,Dashed\n" +
            "A,2,-1\n" +
            "*GAS_LINE,Gas text\n" +
            "A,4,-2,[\"GAS\",STANDARD,S=.5,A=90],-2\n" +
            "*UPRIGHT,Upright\n" +
            "A,1,-.2,[\"U\",STANDARD,U=0],-.2\n");
        var history = new CadDocumentHistory(session);
        CadImportLineTypesCommand command =
            CadImportLineTypesCommand.CaptureSupported(
                file,
                CadLineTypeImportConflictPolicy.Reject);

        ulong generation = history.Execute(command);

        Assert.Equal(1UL, generation);
        Assert.Equal(2, command.ImportedCount);
        Assert.Equal(2, command.CreatedCount);
        Assert.Equal(0, command.ReplacedCount);
        Assert.Equal(1, command.UnsupportedCount);
        session.Read(document =>
        {
            LineType dashed = document.LineTypes["DASHED"];
            Assert.Equal("Dashed", dashed.Description);
            Assert.Equal(
                new[] { 2.0, -1.0 },
                dashed.Segments.Select(static segment => segment.Length));
            LineType.Segment text = document.LineTypes["GAS_LINE"]
                .Segments.ElementAt(2);
            Assert.True(text.IsText);
            Assert.Equal("GAS", text.Text);
            Assert.Same(document.TextStyles[TextStyle.DefaultName], text.Style);
            Assert.Equal(0.5, text.Scale);
            Assert.Equal(Math.PI * 0.5, text.Rotation, 12);
            Assert.True(text.Flags.HasFlag(LineTypeShapeFlags.RotationIsAbsolute));
            Assert.False(document.LineTypes.Contains("UPRIGHT"));
            return true;
        });

        Assert.True(history.TryUndo(out generation));
        Assert.Equal(2UL, generation);
        Assert.False(session.Read(document => document.LineTypes.Contains("DASHED")));
        Assert.True(history.TryRedo(out generation));
        Assert.Equal(3UL, generation);
        Assert.True(session.Read(document => document.LineTypes.Contains("GAS_LINE")));
    }

    [Fact]
    public void ReloadPreservesReferencedLineTypeIdentityAndExactUndoDefinition()
    {
        var document = new CadDocument();
        var existing = new LineType("RELOAD") { Description = "Old" };
        var oldDash = new LineType.Segment { Length = 3.0 };
        var oldGap = new LineType.Segment { Length = -1.0 };
        existing.AddSegment(oldDash);
        existing.AddSegment(oldGap);
        document.LineTypes.Add(existing);
        var layer = new Layer("REFERENCE") { LineType = existing };
        document.Layers.Add(layer);
        var line = new Line(XYZ.Zero, XYZ.AxisX) { LineType = existing };
        document.Entities.Add(line);
        ulong handle = existing.Handle;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        CadLinFile file = Parse(
            "*RELOAD,New\nA,8,-2,0,-2\n" +
            "*CREATED,Created\nA,1,-1\n");
        var command = new CadImportLineTypesCommand(
            file.Definitions.ToArray(),
            CadLineTypeImportConflictPolicy.ReplaceExisting);

        history.Execute(command);

        Assert.Equal(1, command.CreatedCount);
        Assert.Equal(1, command.ReplacedCount);
        Assert.Same(existing, document.LineTypes["RELOAD"]);
        Assert.Same(existing, layer.LineType);
        Assert.Same(existing, line.LineType);
        Assert.Equal(handle, existing.Handle);
        Assert.Equal("New", existing.Description);
        Assert.Equal(
            new[] { 8.0, -2.0, 0.0, -2.0 },
            existing.Segments.Select(static segment => segment.Length));

        Assert.True(history.TryUndo(out _));
        Assert.Equal("Old", existing.Description);
        Assert.Equal(new[] { oldDash, oldGap }, existing.Segments);
        Assert.False(document.LineTypes.Contains("CREATED"));
        Assert.Same(existing, layer.LineType);
        Assert.Same(existing, line.LineType);
        Assert.Equal(handle, existing.Handle);

        Assert.True(history.TryRedo(out _));
        Assert.Equal("New", existing.Description);
        Assert.Equal(
            new[] { 8.0, -2.0, 0.0, -2.0 },
            existing.Segments.Select(static segment => segment.Length));
        Assert.True(document.LineTypes.Contains("CREATED"));
    }

    [Fact]
    public void ShapeImportCreatesOneReusableStyleAndRestoresItThroughUndoRedo()
    {
        var catalog = new CadShxFontCatalog();
        catalog.Register("ep.shx", CreateShapeCache((230, "CAP")));
        CadDocumentSession session = CadDocumentSession.CreateNew();
        var history = new CadDocumentHistory(session);
        CadLinFile file = Parse(
            "*SHAPES,Shapes\n" +
            "A,2,-1,[CAP,C:\\fonts\\ep.shx,S=.5,R=10],-1,[CAP,ep.shx],-1\n");
        var command = new CadImportLineTypesCommand(
            file.Definitions.ToArray(),
            CadLineTypeImportConflictPolicy.Reject,
            catalog);

        history.Execute(command);

        (TextStyle style, LineType lineType) = session.Read(document =>
        {
            TextStyle importedStyle = Assert.Single(
                document.TextStyles,
                static candidate => candidate.IsShapeFile);
            return (importedStyle, document.LineTypes["SHAPES"]);
        });
        Assert.Equal("ep.shx", style.Filename);
        Assert.Equal(2, lineType.Segments.Count(static segment => segment.IsShape));
        Assert.All(
            lineType.Segments.Where(static segment => segment.IsShape),
            segment =>
            {
                Assert.Equal((short)230, segment.ShapeNumber);
                Assert.Same(style, segment.Style);
            });

        Assert.True(history.TryUndo(out _));
        Assert.False(session.Read(document => document.LineTypes.Contains("SHAPES")));
        Assert.False(session.Read(document => document.TextStyles.Contains(style.Name)));
        Assert.True(history.TryRedo(out _));
        session.Read(document =>
        {
            Assert.Same(style, document.TextStyles[style.Name]);
            Assert.All(
                document.LineTypes["SHAPES"].Segments
                    .Where(static segment => segment.IsShape),
                segment => Assert.Same(style, segment.Style));
            return true;
        });
    }

    [Fact]
    public void ImportPreflightAndAddNotificationFailureLeaveNoPartialGeneration()
    {
        CadLinFile file = Parse(
            "*FIRST,First\nA,1,-1\n" +
            "*SECOND,Second\nA,2,-2\n");
        var document = new CadDocument();
        document.LineTypes.Add(new LineType("FIRST"));
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadImportLineTypesCommand(
                file.Definitions.ToArray(),
                CadLineTypeImportConflictPolicy.Reject)));
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.False(document.LineTypes.Contains("SECOND"));

        var cleanDocument = new CadDocument();
        var cleanSession = new CadDocumentSession(cleanDocument);
        var cleanHistory = new CadDocumentHistory(cleanSession);
        EventHandler<ACadSharp.CollectionChangedEventArgs> failSecond = (_, args) =>
        {
            if (args.Item is LineType lineType && lineType.Name == "SECOND")
            {
                throw new InvalidOperationException("Injected LIN add failure.");
            }
        };
        cleanDocument.LineTypes.OnAdd += failSecond;
        try
        {
            Assert.Throws<InvalidOperationException>(() => cleanHistory.Execute(
                new CadImportLineTypesCommand(
                    file.Definitions.ToArray(),
                    CadLineTypeImportConflictPolicy.Reject)));
        }
        finally
        {
            cleanDocument.LineTypes.OnAdd -= failSecond;
        }
        Assert.Equal(0UL, cleanSession.ContentGeneration);
        Assert.Equal(0, cleanHistory.UndoCount);
        Assert.False(cleanDocument.LineTypes.Contains("FIRST"));
        Assert.False(cleanDocument.LineTypes.Contains("SECOND"));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task ImportedSimpleAndTextDefinitionsRoundTrip(
        CadDocumentFormat format)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        var history = new CadDocumentHistory(session);
        CadLinFile file = Parse(
            "*PERSISTED_DASH,Dash\nA,2,-1,0,-1\n" +
            "*PERSISTED_TEXT,Text\nA,4,-2,[\"HW\",STANDARD,S=.25,R=15,X=.1,Y=-.2],-2\n");
        history.Execute(new CadImportLineTypesCommand(
            file.Definitions.ToArray(),
            CadLineTypeImportConflictPolicy.Reject));
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
            sourceName: $"lin-import.{format.ToString().ToLowerInvariant()}");

        loaded.Session.Read(document =>
        {
            LineType dash = document.LineTypes["PERSISTED_DASH"];
            Assert.Equal(
                new[] { 2.0, -1.0, 0.0, -1.0 },
                dash.Segments.Select(static segment => segment.Length));
            LineType textType = document.LineTypes["PERSISTED_TEXT"];
            LineType.Segment text = textType.Segments.ElementAt(2);
            Assert.True(text.IsText);
            Assert.Equal("HW", text.Text);
            Assert.Equal(0.25, text.Scale);
            Assert.Equal(Math.PI / 12.0, text.Rotation, 12);
            Assert.Equal(0.1, text.Offset.X);
            Assert.Equal(-0.2, text.Offset.Y);
            Assert.Equal(TextStyle.DefaultName, text.Style.Name);
            return true;
        });
    }

    private static CadLinFile Parse(string source) =>
        CadLinFile.Parse(Encoding.ASCII.GetBytes(source));

    private static CadShxGlyphCache CreateShapeCache(
        params (ushort Number, string Name)[] shapes) =>
        new(CadShxFont.Parse(BuildStandardShx(shapes.Select(shape =>
            (shape.Number, shape.Name, new byte[] { 0x10, 0 })).ToArray())));

    private static byte[] BuildStandardShx(
        params (ushort Number, string Name, byte[] Program)[] shapes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write(shapes.Min(static shape => shape.Number));
        writer.Write(shapes.Max(static shape => shape.Number));
        writer.Write(checked((ushort)shapes.Length));
        foreach ((ushort number, string name, byte[] program) in shapes)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            writer.Write(number);
            writer.Write(checked((ushort)(nameBytes.Length + 1 + program.Length)));
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
}

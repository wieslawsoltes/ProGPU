using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Text;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadAttributeDisplayTests
{
    private static readonly TtfFont Font = InterFontFamily.Regular;

    [Fact]
    public void AttModeDrivesSnapshotSelectionPrintAndNativeReplay()
    {
        CadDocumentSession session = CreateAttributedSession();

        CadDocumentSnapshot normal = Compile(session);
        Assert.Equal(2, normal.Texts.Length);
        Assert.Equal(0, QueryHiddenVariable(normal).TotalCount);

        session.Edit("Set ATTMODE On", document =>
            document.Header.AttributeVisibility = AttributeVisibilityMode.All);
        CadDocumentSnapshot all = Compile(session);
        Assert.Equal(4, all.Texts.Length);
        Assert.Equal(1, QueryHiddenVariable(all).TotalCount);

        session.Edit("Set ATTMODE Off", document =>
            document.Header.AttributeVisibility = AttributeVisibilityMode.None);
        CadDocumentSnapshot none = Compile(session);
        Assert.Empty(none.Texts.ToArray());
        Assert.Equal(0, QueryHiddenVariable(none).TotalCount);

        using CadPrintPlan normalPrint = new CadPrintPlanCompiler().Compile(normal);
        using CadPrintPlan allPrint = new CadPrintPlanCompiler().Compile(all);
        using CadPrintPlan nonePrint = new CadPrintPlanCompiler().Compile(none);
        Assert.True(
            allPrint.SceneStatistics.RecordedEntityCount >
            normalPrint.SceneStatistics.RecordedEntityCount);
        Assert.True(
            normalPrint.SceneStatistics.RecordedEntityCount >
            nonePrint.SceneStatistics.RecordedEntityCount);

        int normalNativeCommands = CompileNativeCommandCount(normal);
        int allNativeCommands = CompileNativeCommandCount(all);
        int noneNativeCommands = CompileNativeCommandCount(none);
        Assert.True(allNativeCommands > normalNativeCommands);
        Assert.True(normalNativeCommands > noneNativeCommands);
    }

    [Fact]
    public void AttributeDisplayCommandUsesOneGenerationAndExactUndoRedo()
    {
        CadDocumentSession session = CreateAttributedSession();
        var history = new CadDocumentHistory(session);
        ulong initialGeneration = session.ContentGeneration;

        ulong applied = history.Execute(
            new CadSetAttributeVisibilityModeCommand(
                AttributeVisibilityMode.All));

        Assert.Equal(initialGeneration + 1, applied);
        Assert.Equal(
            AttributeVisibilityMode.All,
            session.Read(document => document.Header.AttributeVisibility));
        Assert.Equal(4, Compile(session).Texts.Length);

        Assert.True(history.TryUndo(out ulong undone));
        Assert.Equal(applied + 1, undone);
        Assert.Equal(
            AttributeVisibilityMode.Normal,
            session.Read(document => document.Header.AttributeVisibility));
        Assert.Equal(2, Compile(session).Texts.Length);

        Assert.True(history.TryRedo(out ulong redone));
        Assert.Equal(undone + 1, redone);
        Assert.Equal(
            AttributeVisibilityMode.All,
            session.Read(document => document.Header.AttributeVisibility));
        Assert.Equal(4, Compile(session).Texts.Length);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetAttributeVisibilityModeCommand(
                (AttributeVisibilityMode)3));
        Assert.Throws<InvalidOperationException>(() =>
            history.Execute(new CadSetAttributeVisibilityModeCommand(
                AttributeVisibilityMode.All)));
        Assert.Equal(redone, session.ContentGeneration);
    }

    [Fact]
    public void SnapshotRejectsInvalidPersistedAttMode()
    {
        CadDocumentSession session = CreateAttributedSession();
        session.Edit("Set malformed ATTMODE", document =>
            document.Header.AttributeVisibility = (AttributeVisibilityMode)17);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => Compile(session));

        Assert.Contains("ATTMODE value 17", exception.Message);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AttributeDisplayModeSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        CadDocumentSession session = CreateAttributedSession();
        var history = new CadDocumentHistory(session);
        history.Execute(new CadSetAttributeVisibilityModeCommand(
            AttributeVisibilityMode.All));
        using var stream = new MemoryStream();
        var store = new CadDocumentStore();

        await store.SaveAsync(
            session,
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"attmode-roundtrip.{format.ToString().ToLowerInvariant()}");

        Assert.Equal(
            AttributeVisibilityMode.All,
            loaded.Session.Read(document =>
                document.Header.AttributeVisibility));
        Assert.Equal(4, Compile(loaded.Session).Texts.Length);
    }

    private static CadDocumentSession CreateAttributedSession()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit("Add attribute display fixture", document =>
        {
            var style = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(style);
            var block = new BlockRecord("ATTMODE_FIXTURE");
            block.Entities.Add(new Line(
                new XYZ(-50, -50, 0),
                new XYZ(150, -50, 0)));
            block.Entities.Add(CreateDefinition(
                "VISIBLE_VARIABLE",
                "V",
                new XYZ(0, 0, 0),
                style,
                AttributeFlags.None));
            block.Entities.Add(CreateDefinition(
                "HIDDEN_VARIABLE",
                "H",
                new XYZ(100, 0, 0),
                style,
                AttributeFlags.Hidden));
            block.Entities.Add(CreateDefinition(
                "VISIBLE_CONSTANT",
                "C",
                new XYZ(0, 100, 0),
                style,
                AttributeFlags.Constant));
            block.Entities.Add(CreateDefinition(
                "HIDDEN_CONSTANT",
                "X",
                new XYZ(100, 100, 0),
                style,
                AttributeFlags.Constant | AttributeFlags.Hidden));
            var insert = new Insert(block);
            foreach (AttributeEntity attribute in insert.Attributes)
            {
                attribute.Value = attribute.Tag == "HIDDEN_VARIABLE" ? "H" : "V";
            }
            document.Entities.Add(insert);
        });
        return session;
    }

    private static AttributeDefinition CreateDefinition(
        string tag,
        string value,
        XYZ origin,
        TextStyle style,
        AttributeFlags flags) => new()
    {
        Tag = tag,
        Value = value,
        InsertPoint = origin,
        Height = 5,
        Style = style,
        Flags = flags,
    };

    private static CadDocumentSnapshot Compile(CadDocumentSession session) =>
        new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedResolver(),
            });

    private static CadSelectionQueryResult QueryHiddenVariable(
        CadDocumentSnapshot snapshot)
    {
        Span<int> scratch = stackalloc int[8];
        Span<CadSelectionCandidate> candidates =
            stackalloc CadSelectionCandidate[8];
        return CadSelectionQuery.QueryBounds(
            snapshot,
            new CadBounds3D(
                new CadPoint3D(95, -5, -1),
                new CadPoint3D(125, 15, 1)),
            scratch,
            candidates);
    }

    private static int CompileNativeCommandCount(CadDocumentSnapshot snapshot)
    {
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        return Assert.IsType<NativeCompiledPicture>(native).SourceCommandCount;
    }

    private sealed class FixedResolver : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
            new(Font, false);
    }
}

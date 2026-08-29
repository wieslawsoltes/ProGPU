using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Text;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadAttributeSnapshotTests
{
    private static readonly TtfFont Font = InterFontFamily.Regular;

    [Fact]
    public void VariableAttributeReplacesDefinitionWithoutApplyingInsertTransformTwice()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong insertHandle = 0;
        session.Edit("Add transformed attributed insert", document =>
        {
            TextStyle style = AddTextStyle(document);
            var insertLayer = new Layer("ATTRIBUTES")
            {
                Color = ACadSharp.Color.Red,
            };
            document.Layers.Add(insertLayer);
            var definition = new AttributeDefinition
            {
                Tag = "LABEL",
                Value = "DEFAULT",
                Style = style,
                InsertPoint = new XYZ(1, 2, 0),
                Height = 2,
                Color = ACadSharp.Color.ByBlock,
            };
            var block = new BlockRecord("ATTRIBUTED");
            block.Entities.Add(definition);
            var insert = new Insert(block)
            {
                InsertPoint = new XYZ(10, 20, 0),
                XScale = 2,
                YScale = 3,
                Rotation = Math.PI / 2,
                ColumnCount = 2,
                ColumnSpacing = 10,
                Layer = insertLayer,
                Color = ACadSharp.Color.Green,
            };
            AttributeEntity attribute = Assert.Single(insert.Attributes);
            attribute.Value = "A";
            attribute.InsertPoint = new XYZ(100, 200, 0);
            document.Entities.Add(insert);
            insertHandle = insert.Handle;
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadTextPrimitive[] texts = snapshot.Texts.ToArray();
        CadEntityHeader[] entities = snapshot.Entities.ToArray();

        Assert.Equal(2, texts.Length);
        AssertPoint(new CadPoint3D(100, 200, 0), texts[0].Origin);
        AssertPoint(new CadPoint3D(100, 210, 0), texts[1].Origin);
        Assert.All(texts, text => Assert.Equal(1, text.GlyphCount));
        Assert.All(entities, entity => Assert.Equal(insertHandle, entity.Handle));
        Assert.All(entities, entity => Assert.Equal(CadEntityKind.Text, entity.Kind));
        Assert.All(entities, entity =>
        {
            Assert.Equal("ATTRIBUTES", snapshot.Layers.Span[entity.LayerIndex].Name);
            CadStrokeStyle style = snapshot.Styles.Span[entity.StyleIndex];
            Assert.Equal((byte)0, style.Red);
            Assert.Equal(byte.MaxValue, style.Green);
            Assert.Equal((byte)0, style.Blue);
        });
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
        Assert.DoesNotContain(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP004");
    }

    [Fact]
    public void ConstantDefinitionsRenderPerCellAndConstantReferencesDoNotDuplicateThem()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add constant attributed array", document =>
        {
            TextStyle style = AddTextStyle(document);
            var definition = new AttributeDefinition
            {
                Tag = "REVISION",
                Value = "C",
                Style = style,
                InsertPoint = new XYZ(1, 2, 0),
                Height = 2,
                Flags = AttributeFlags.Constant,
            };
            var block = new BlockRecord("CONSTANT_ATTRIBUTE");
            block.Entities.Add(definition);
            var insert = new Insert(block)
            {
                InsertPoint = new XYZ(10, 20, 0),
                ColumnCount = 2,
                ColumnSpacing = 5,
            };
            Assert.Single(insert.Attributes);
            document.Entities.Add(insert);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadTextPrimitive[] texts = snapshot.Texts.ToArray();

        Assert.Equal(2, texts.Length);
        AssertPoint(new CadPoint3D(11, 22, 0), texts[0].Origin);
        AssertPoint(new CadPoint3D(16, 22, 0), texts[1].Origin);
        Assert.Equal(3, snapshot.Statistics.ExpandedEntityCount);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
    }

    [Fact]
    public void InvisibleAttributesRemainStoredButAreNeitherRenderedNorPrinted()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add invisible attribute", document =>
        {
            TextStyle style = AddTextStyle(document);
            var definition = new AttributeDefinition
            {
                Tag = "DATABASE_ID",
                Value = "DEFAULT",
                Style = style,
                Flags = AttributeFlags.Hidden,
            };
            var block = new BlockRecord("HIDDEN_ATTRIBUTE");
            block.Entities.Add(definition);
            var insert = new Insert(block);
            Assert.Single(insert.Attributes).Value = "42";
            document.Entities.Add(insert);
            document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        });

        CadDocumentSnapshot snapshot = Compile(session);
        using CadPrintPlan printPlan = new CadPrintPlanCompiler().Compile(snapshot);

        Assert.Equal(CadEntityKind.Line, Assert.Single(snapshot.Entities.ToArray()).Kind);
        Assert.Empty(snapshot.Texts.ToArray());
        Assert.Equal(1, printPlan.SceneStatistics.RecordedEntityCount);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
    }

    [Fact]
    public void NestedAttributeUsesOnlyAncestorTransformAfterInnerGeometryWasBaked()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add nested attributed insert", document =>
        {
            TextStyle style = AddTextStyle(document);
            var label = new BlockRecord("NESTED_LABEL");
            label.Entities.Add(new AttributeDefinition
            {
                Tag = "NAME",
                Value = "DEFAULT",
                Style = style,
                InsertPoint = new XYZ(2, 1, 0),
            });
            var inner = new Insert(label)
            {
                InsertPoint = new XYZ(5, 0, 0),
            };
            AttributeEntity attribute = Assert.Single(inner.Attributes);
            attribute.Value = "N";
            attribute.InsertPoint = new XYZ(7, 1, 0);

            var assembly = new BlockRecord("ASSEMBLY");
            assembly.Entities.Add(inner);
            document.Entities.Add(new Insert(assembly)
            {
                InsertPoint = new XYZ(10, 20, 0),
                XScale = 2,
                YScale = 3,
                Rotation = Math.PI / 2,
            });
        });

        CadTextPrimitive text = Assert.Single(Compile(session).Texts.ToArray());

        AssertPoint(new CadPoint3D(7, 34, 0), text.Origin);
    }

    [Fact]
    public void MultilineAttributeReusesRetainedSelectionPrintAndNativePicturePipelines()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong insertHandle = 0;
        session.Edit("Add multiline attribute", document =>
        {
            TextStyle style = AddTextStyle(document);
            var definition = new AttributeDefinition
            {
                Tag = "NOTES",
                AttributeType = AttributeType.MultiLine,
                MText = new MText
                {
                    Value = "DEFAULT",
                    Style = style,
                    Height = 3,
                    RectangleWidth = 40,
                    InsertPoint = XYZ.Zero,
                },
            };
            var block = new BlockRecord("MULTILINE_ATTRIBUTE");
            block.Entities.Add(definition);
            var insert = new Insert(block)
            {
                InsertPoint = new XYZ(500, 600, 0),
                XScale = 4,
                YScale = 5,
            };
            AttributeEntity attribute = Assert.Single(insert.Attributes);
            attribute.MText.Value = @"ACTUAL\P\Lnote\l";
            attribute.MText.InsertPoint = new XYZ(30, 40, 0);
            document.Entities.Add(insert);
            insertHandle = insert.Handle;
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadMTextPrimitive text = Assert.Single(snapshot.MTexts.ToArray());
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            entity.Handle,
            entity.Kind,
            entity.Bounds);
        var selectionWindow = new CadBounds3D(
            entity.Bounds.Min - new CadPoint3D(1, 1, 1),
            entity.Bounds.Max + new CadPoint3D(1, 1, 1));

        Assert.Equal(insertHandle, entity.Handle);
        Assert.Equal(CadEntityKind.MText, entity.Kind);
        AssertPoint(new CadPoint3D(30, 40, 0), text.Origin);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                selectionWindow,
                CadBoundsSelectionMode.Window).Status);

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);

        using CadPrintPlan printPlan = new CadPrintPlanCompiler().Compile(snapshot);
        using GpuPicture page = printPlan.CreatePagePicture();
        GpuPicture pageContent = page.GetCommand(1).Picture!;
        Assert.Equal(scene.DrawingContext.Commands.Count, pageContent.CommandCount);
        Assert.True(pageContent.CommandCount > 1);
    }

    [Fact]
    public void StandaloneAttributeDefinitionRetainsItsDefaultText()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add standalone definition", document =>
        {
            TextStyle style = AddTextStyle(document);
            document.Entities.Add(new AttributeDefinition
            {
                Tag = "STANDALONE",
                Value = "D",
                Style = style,
                InsertPoint = new XYZ(3, 4, 0),
            });
        });

        CadTextPrimitive text = Assert.Single(Compile(session).Texts.ToArray());

        AssertPoint(new CadPoint3D(3, 4, 0), text.Origin);
    }

    [Fact]
    public void DefinitionVisibilityModeRecompilesAfterExplicitSynchronization()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong insertHandle = 0;
        session.Edit("Add mode-edit attribute", document =>
        {
            TextStyle style = AddTextStyle(document);
            var block = new BlockRecord("MODE_EDIT_ATTRIBUTE");
            block.Entities.Add(new AttributeDefinition
            {
                Tag = "SERIAL",
                Value = "VISIBLE",
                Style = style,
                Height = 1,
                InsertPoint = new XYZ(2, 1, 0),
            });
            var insert = new Insert(block);
            document.Entities.Add(insert);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 0, 0)));
            insertHandle = insert.Handle;
        });
        var history = new CadDocumentHistory(session);

        CadDocumentSnapshot initiallyVisible = Compile(session);
        Assert.Single(initiallyVisible.Texts.ToArray());
        CadRecordedPlanScene visibleScene =
            new CadPlanSceneCompiler().Compile(initiallyVisible);
        using GpuPicture visiblePicture = visibleScene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            visiblePicture,
            96U,
            visibleScene.ContentGeneration,
            out NativeCompiledPicture? visibleNative,
            out NativePictureCompileFailure visibleFailure),
            visibleFailure.ToString());
        Assert.NotNull(visibleNative);

        history.Execute(new CadSetAttributeDefinitionModesCommand(
            insertHandle,
            "SERIAL",
            isInvisible: true,
            isVerifiable: true,
            isPreset: true,
            isPositionLocked: true));
        Assert.Single(Compile(session).Texts.ToArray());

        history.Execute(new CadSynchronizeBlockAttributePropertiesCommand(
            insertHandle));
        CadDocumentSnapshot synchronizedHidden = Compile(session);
        Assert.Empty(synchronizedHidden.Texts.ToArray());
        CadRecordedPlanScene hiddenScene =
            new CadPlanSceneCompiler().Compile(synchronizedHidden);
        using GpuPicture hiddenPicture = hiddenScene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            hiddenPicture,
            96U,
            hiddenScene.ContentGeneration,
            out NativeCompiledPicture? hiddenNative,
            out NativePictureCompileFailure hiddenFailure),
            hiddenFailure.ToString());
        Assert.NotNull(hiddenNative);
        Assert.True(
            visibleNative.SourceCommandCount > hiddenNative.SourceCommandCount);

        Assert.True(history.TryUndo(out _));
        Assert.Single(Compile(session).Texts.ToArray());
        Assert.True(history.TryUndo(out _));
        Assert.Single(Compile(session).Texts.ToArray());
    }

    [Fact]
    public async Task EditedAttributeValueSurvivesDxfSaveAndReload()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        ulong insertHandle = 0;
        session.Edit("Add saved attribute", document =>
        {
            var block = new BlockRecord("SAVED_ATTRIBUTE");
            block.Entities.Add(new AttributeDefinition
            {
                Tag = "SERIAL",
                Value = "ORIGINAL",
            });
            var insert = new Insert(block);
            document.Entities.Add(insert);
            insertHandle = insert.Handle;
        });
        var history = new CadDocumentHistory(session);
        history.Execute(new CadSetAttributeValueCommand(
            insertHandle,
            "SERIAL",
            "EDITED-42"));

        var store = new CadDocumentStore();
        using var output = new MemoryStream();
        await store.SaveAsync(
            session,
            output,
            CadDocumentFormat.Dxf,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        output.Position = 0;

        CadLoadResult loaded = await store.LoadAsync(
            output,
            CadDocumentFormat.Dxf,
            sourceName: "attribute-roundtrip.dxf");
        string value = loaded.Session.Read(document =>
        {
            Insert insert = Assert.IsType<Insert>(Assert.Single(document.Entities));
            return Assert.Single(insert.Attributes).Value;
        });

        Assert.Equal("EDITED-42", value);
    }

    private static TextStyle AddTextStyle(CadDocument document)
    {
        var style = new TextStyle("INTER") { Filename = "Inter.ttf" };
        document.TextStyles.Add(style);
        return style;
    }

    private static CadDocumentSnapshot Compile(CadDocumentSession session) =>
        new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedResolver(),
            });

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1e-9);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1e-9);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, 1e-9);
    }

    private sealed class FixedResolver : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
            new(Font, false);
    }
}

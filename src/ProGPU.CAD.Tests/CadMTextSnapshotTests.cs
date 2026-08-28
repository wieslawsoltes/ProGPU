using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMTextSnapshotTests
{
    private static readonly TtfFont Font = InterFontFamily.Regular;

    [Fact]
    public void FormattingStacksMasksAndDecorationsRemainRetainedAndColored()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add formatted MTEXT", document =>
        {
            var style = new TextStyle("INTER")
            {
                Filename = "Inter.ttf",
                Width = 0.9,
                ObliqueAngle = 0.05,
            };
            document.TextStyles.Add(style);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = @"A{\C1;\H1.5x;\W1.2;\Q10;\Lwide\l}\S1/2;",
                Height = 10,
                RectangleWidth = 120,
                InsertPoint = new XYZ(10, 20, 0),
                BackgroundColor = new ACadSharp.Color(12, 34, 56),
                BackgroundFillFlags = BackgroundFillFlags.UseBackgroundFillColor |
                    BackgroundFillFlags.TextFrame,
                BackgroundScale = 1.5,
                BackgroundTransparency = new Transparency(20),
            });
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadMTextPrimitive text = Assert.Single(snapshot.MTexts.ToArray());
        CadMTextGlyphRun[] runs = snapshot.MTextGlyphRuns.ToArray();
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(CadEntityKind.MText, entity.Kind);
        Assert.Equal(new CadPoint3D(10, 20, 0), text.Origin);
        Assert.True(text.GlyphCount > 5);
        Assert.True(text.RunCount >= 3);
        Assert.Contains(runs, run => run.Red == 255 && run.Green == 0 && run.Blue == 0);
        Assert.Contains(runs, run => Math.Abs(run.FontSize - 15.0f) < 0.001f);
        Assert.Contains(runs, run => Math.Abs(run.WidthScale - 1.2f) < 0.001f);
        Assert.Contains(runs, run => Math.Abs(run.SkewX - MathF.Tan(MathF.PI / 18.0f)) < 0.001f);
        Assert.NotEmpty(snapshot.MTextDecorations.ToArray());
        Assert.Single(snapshot.MTextStrokes.ToArray());
        Assert.Equal(5, snapshot.MTextBackgrounds.Length);
        Assert.False(entity.Bounds.IsEmpty);
        Assert.Equal(
            text.RunCount + text.BackgroundCount + text.DecorationCount + text.StrokeCount,
            scene.DrawingContext.Commands.Count);
        Assert.Contains(scene.DrawingContext.Commands.ToArray(), command =>
            command.Type == RenderCommandType.DrawGlyphRun && command.HasFontTransform);
        Assert.Contains(scene.DrawingContext.Commands.ToArray(), command =>
            command.Type == RenderCommandType.DrawRect &&
            command.Brush is SolidColorBrush brush &&
            Math.Abs(brush.Color.W - 0.8f) < 0.01f);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
    }

    [Fact]
    public void ForcedBreakAndReverseFlowPlaceCompleteLinesInPersistedColumns()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add column MTEXT", document =>
        {
            var text = new MText
            {
                Value = @"FIRST\NSECOND",
                Height = 5,
                AttachmentPoint = AttachmentPointType.TopLeft,
            };
            text.ColumnData.ColumnType = ColumnType.StaticColumns;
            text.ColumnData.ColumnCount = 2;
            text.ColumnData.Width = 40;
            text.ColumnData.Gutter = 5;
            text.ColumnData.FlowReversed = true;
            text.ColumnData.Heights.Add(50);
            text.ColumnData.Heights.Add(50);
            document.Entities.Add(text);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadMTextPrimitive text = Assert.Single(snapshot.MTexts.ToArray());
        ReadOnlySpan<System.Numerics.Vector2> positions = snapshot.TextGlyphPositions.Span.Slice(
            text.GlyphOffset,
            text.GlyphCount);

        Assert.Equal(2, text.ColumnCount);
        Assert.Equal(85.0f, text.ContentWidth);
        Assert.True(positions[0].X >= 45.0f);
        Assert.Contains(positions.ToArray(), position => position.X < 40.0f);
    }

    [Fact]
    public void DynamicAutoHeightBalancesWholeLinesWithoutPersistedHeight()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add automatic columns", document =>
        {
            var text = new MText
            {
                Value = @"ONE\PTWO\PTHREE\PFOUR",
                Height = 4,
            };
            text.ColumnData.ColumnType = ColumnType.DynamicColumns;
            text.ColumnData.ColumnCount = 2;
            text.ColumnData.Width = 30;
            text.ColumnData.Gutter = 3;
            text.ColumnData.AutoHeight = true;
            document.Entities.Add(text);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadMTextPrimitive text = Assert.Single(snapshot.MTexts.ToArray());
        System.Numerics.Vector2[] positions = snapshot.TextGlyphPositions.Span
            .Slice(text.GlyphOffset, text.GlyphCount)
            .ToArray();

        Assert.Equal(2, text.ColumnCount);
        Assert.Contains(positions, position => position.X < 30.0f);
        Assert.Contains(positions, position => position.X >= 33.0f);
        Assert.True(text.ContentHeight > 0.0f);
    }

    [Fact]
    public void NestedBlockAffineTransformComposesMTextWcsBasisOnce()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong rootHandle = 0;
        session.Edit("Add block MTEXT", document =>
        {
            var block = new BlockRecord("MTEXT_LABEL");
            block.Entities.Add(new MText
            {
                Value = "CAD",
                Height = 2,
                AlignmentPoint = XYZ.AxisX,
            });
            var insert = new Insert(block)
            {
                InsertPoint = new XYZ(10, 20, 0),
                XScale = 2,
                YScale = 3,
                Rotation = Math.PI / 2,
            };
            document.Entities.Add(insert);
            rootHandle = insert.Handle;
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadMTextPrimitive text = Assert.Single(snapshot.MTexts.ToArray());

        Assert.Equal(rootHandle, Assert.Single(snapshot.Entities.ToArray()).Handle);
        AssertPoint(new CadPoint3D(10, 20, 0), text.Origin);
        AssertPoint(new CadPoint3D(0, 2, 0), text.XAxis);
        AssertPoint(new CadPoint3D(3, 0, 0), text.YAxis);
    }

    [Fact]
    public void InvalidLateMTextStateDoesNotPartiallyAppendSharedStreams()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add valid line and invalid MTEXT", document =>
        {
            var style = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(style);
            document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
            document.Entities.Add(new MText
            {
                Style = style,
                Value = "shaped before invalid background",
                RectangleWidth = 100,
                BackgroundFillFlags = BackgroundFillFlags.UseBackgroundFillColor,
                BackgroundScale = 0.5,
            });
        });

        CadDocumentSnapshot snapshot = Compile(session);

        Assert.Single(snapshot.Lines.ToArray());
        Assert.Empty(snapshot.MTexts.ToArray());
        Assert.Empty(snapshot.TextGlyphIndices.ToArray());
        Assert.Empty(snapshot.TextGlyphPositions.ToArray());
        Assert.Empty(snapshot.TextFonts.ToArray());
        Assert.Empty(snapshot.MTextGlyphRuns.ToArray());
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic => diagnostic.Code == "CADSNAP002");
    }

    [Fact]
    public void PrintPlanReplaysTheSameRetainedMTextCommands()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add printable MTEXT", document => document.Entities.Add(new MText
        {
            Value = @"PRINT\P\Lquality\l",
            Height = 8,
            RectangleWidth = 80,
        }));
        CadDocumentSnapshot snapshot = Compile(session);
        CadMTextPrimitive text = Assert.Single(snapshot.MTexts.ToArray());
        using CadPrintPlan plan = new CadPrintPlanCompiler().Compile(snapshot);
        using GpuPicture page = plan.CreatePagePicture();
        GpuPicture content = page.GetCommand(1).Picture!;

        Assert.Equal(1, plan.SceneStatistics.RecordedEntityCount);
        Assert.Equal(text.RunCount + text.DecorationCount, content.CommandCount);
        Assert.Contains(
            Enumerable.Range(0, content.CommandCount).Select(content.GetCommand),
            command => command.Type == RenderCommandType.DrawGlyphRun);
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
        public CadTextFontResolution Resolve(in CadTextFontRequest request) => new(Font, false);
    }
}

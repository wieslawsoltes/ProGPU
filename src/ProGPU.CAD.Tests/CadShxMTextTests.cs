using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using System.Numerics;
using System.Text;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadShxMTextTests
{
    [Fact]
    public void FormattingStacksMasksAndFontOverridesRemainRetainedShxGeometry()
    {
        CadShxGlyphCache primary = CreateCache("PRIMARY");
        CadShxGlyphCache alternate = CreateCache("ALTERNATE");
        var catalog = new CadShxFontCatalog();
        catalog.Register("test.shx", primary);
        catalog.Register("other.shx", alternate);
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add formatted SHX MTEXT", document =>
        {
            var style = new TextStyle("TESTSHX")
            {
                Filename = "test.shx",
                Width = 0.9,
                ObliqueAngle = 0.05,
            };
            document.TextStyles.Add(style);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = @"A{\C1;\H1.5x;\W1.2;\Q10;\LBB\l}{\Fother.shx;A}\S1/2;",
                Height = 10,
                RectangleWidth = 160,
                InsertPoint = new XYZ(10, 20, 0),
                BackgroundColor = new ACadSharp.Color(12, 34, 56),
                BackgroundFillFlags = BackgroundFillFlags.UseBackgroundFillColor |
                    BackgroundFillFlags.TextFrame,
                BackgroundScale = 1.5,
                BackgroundTransparency = new Transparency(20),
            });
        });

        CadDocumentSnapshot snapshot = Compile(session, catalog);
        Assert.True(
            snapshot.ShxMTexts.Length == 1,
            string.Join(Environment.NewLine, snapshot.Diagnostics.ToArray().Select(item => item.Message)));
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadShxMTextPrimitive text = snapshot.ShxMTexts.Span[0];
        CadShxMTextGlyphRun[] runs = snapshot.ShxMTextGlyphRuns.ToArray();
        CadShxGlyphInstance[] glyphs = snapshot.ShxGlyphInstances.ToArray();
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(CadEntityKind.ShxMText, entity.Kind);
        Assert.Empty(snapshot.MTexts.ToArray());
        Assert.Equal(new CadPoint3D(10, 20, 0), text.Origin);
        Assert.Equal(6, text.GlyphCount);
        Assert.True(text.RunCount >= 4);
        Assert.Contains(runs, run => run.Red == 255 && run.Green == 0 && run.Blue == 0);
        Assert.Contains(runs, run => Math.Abs(run.ScaleY - 1.5f) < 0.001f);
        Assert.Contains(runs, run => Math.Abs(run.SkewX - MathF.Tan(MathF.PI / 18.0f)) < 0.001f);
        Assert.Contains(glyphs, glyph => ReferenceEquals(glyph.Glyph, alternate.GetGlyph((ushort)'A')));
        Assert.Single(snapshot.MTextDecorations.ToArray());
        Assert.Single(snapshot.MTextStrokes.ToArray());
        Assert.Equal(5, snapshot.MTextBackgrounds.Length);
        Assert.False(entity.Bounds.IsEmpty);
        Assert.Contains(scene.DrawingContext.Commands.ToArray(), command =>
            command.Type == RenderCommandType.DrawPath);
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
    public void WrappingForcedColumnsReverseFlowAndAttachmentUseRetainedPlacements()
    {
        CadShxGlyphCache cache = CreateCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add column SHX MTEXT", document =>
        {
            TextStyle style = AddStyle(document);
            var text = new MText
            {
                Style = style,
                Value = @"AA AA\NBB",
                Height = 10,
                AttachmentPoint = AttachmentPointType.TopCenter,
            };
            text.ColumnData.ColumnType = ColumnType.StaticColumns;
            text.ColumnData.ColumnCount = 2;
            text.ColumnData.Width = 30;
            text.ColumnData.Gutter = 5;
            text.ColumnData.FlowReversed = true;
            text.ColumnData.Heights.Add(50);
            text.ColumnData.Heights.Add(50);
            document.Entities.Add(text);
        });

        CadDocumentSnapshot snapshot = Compile(session, cache);
        CadShxMTextPrimitive text = Assert.Single(snapshot.ShxMTexts.ToArray());
        CadShxGlyphInstance[] glyphs = snapshot.ShxGlyphInstances.ToArray();

        Assert.Equal(2, text.ColumnCount);
        Assert.Equal(65.0f, text.ContentWidth);
        Assert.Contains(glyphs, glyph => glyph.X > 0.0f);
        Assert.Contains(glyphs, glyph => glyph.X < 0.0f);
        Assert.True(text.ContentHeight > 0.0f);
    }

    [Fact]
    public void ParagraphIndentsTabsAndSpacingLowerToRetainedShxGeometry()
    {
        CadShxGlyphCache cache = CreateCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add paragraph-formatted SHX MTEXT", document =>
        {
            TextStyle style = AddStyle(document);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = @"\pxl1,i1,r1,b0.5,a0.25,se1.2,t4,c8,r12,d16;A^IB^IAA^I12^I1.2",
                Height = 10,
                RectangleWidth = 200,
                AttachmentPoint = AttachmentPointType.TopLeft,
            });
        });

        CadDocumentSnapshot snapshot = Compile(session, cache);
        CadShxMTextPrimitive text = Assert.Single(snapshot.ShxMTexts.ToArray());
        CadShxGlyphInstance[] glyphs = snapshot.ShxGlyphInstances.Span
            .Slice(text.GlyphOffset, text.GlyphCount)
            .ToArray();

        Assert.Equal(9, glyphs.Length);
        Assert.InRange(Math.Abs(glyphs[0].X - 20.0f), 0.0f, 0.01f);
        Assert.InRange(Math.Abs(glyphs[1].X - 40.0f), 0.0f, 0.01f);
        Assert.InRange(Math.Abs(glyphs[2].X - 70.0f), 0.0f, 0.01f);
        Assert.InRange(Math.Abs(glyphs[4].X - 100.0f), 0.0f, 0.01f);
        CadShxGlyphInstance decimalPoint = Assert.Single(
            glyphs,
            static glyph => glyph.Glyph.ShapeNumber == '.');
        Assert.InRange(Math.Abs(decimalPoint.X - 160.0f), 0.0f, 0.01f);
        Assert.InRange(Math.Abs(text.ContentHeight - 19.5f), 0.0f, 0.01f);
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        Assert.Contains(scene.DrawingContext.Commands.ToArray(), static command =>
            command.Type == RenderCommandType.DrawPath);
        Assert.True(print.SceneStatistics.RecordedCommandCount > 0);
    }

    [Fact]
    public void VerticalShxParagraphTabsRemainLogicalBeforePhysicalMapping()
    {
        CadShxGlyphCache cache = CreateCache(vertical: true);
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add vertical paragraph tabs", document =>
        {
            var style = new TextStyle("VERTICALSHX")
            {
                Filename = "test.shx",
                Flags = StyleFlags.VerticalText,
            };
            document.TextStyles.Add(style);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = @"\pxl1,i1,t4,d8;A^IB^I1.2",
                Height = 10,
                RectangleWidth = 120,
                DrawingDirection = DrawingDirectionType.TopToBottom,
                AttachmentPoint = AttachmentPointType.TopLeft,
            });
        });

        CadDocumentSnapshot snapshot = Compile(session, cache);
        CadShxMTextPrimitive text = Assert.Single(snapshot.ShxMTexts.ToArray());
        CadShxGlyphInstance[] glyphs = snapshot.ShxGlyphInstances.Span
            .Slice(text.GlyphOffset, text.GlyphCount)
            .ToArray();
        CadShxGlyphInstance decimalPoint = Assert.Single(
            glyphs,
            static glyph => glyph.Glyph.ShapeNumber == '.');

        Assert.InRange(Math.Abs(glyphs[0].Y - 20.0f), 0.0f, 0.01f);
        Assert.InRange(Math.Abs(decimalPoint.Y - 80.0f), 0.0f, 0.01f);
    }

    [Fact]
    public void VerticalShxMTextUsesAuthoredGlyphsLogicalFormattingAndPhysicalAttachment()
    {
        CadShxGlyphCache cache = CreateCache(vertical: true);
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add vertical SHX MTEXT", document =>
        {
            var style = new TextStyle("VERTICALSHX")
            {
                Filename = "test.shx",
                Flags = StyleFlags.VerticalText,
            };
            document.TextStyles.Add(style);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = @"A A\P\LAA\l\S1/2;",
                Height = 10,
                RectangleWidth = 80,
                AttachmentPoint = AttachmentPointType.MiddleCenter,
                DrawingDirection = DrawingDirectionType.TopToBottom,
                BackgroundColor = new ACadSharp.Color(12, 34, 56),
                BackgroundFillFlags = BackgroundFillFlags.UseBackgroundFillColor |
                    BackgroundFillFlags.TextFrame,
            });
        });

        CadDocumentSnapshot snapshot = Compile(session, cache);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadShxMTextPrimitive text = Assert.Single(snapshot.ShxMTexts.ToArray());
        CadShxGlyphInstance[] glyphs = snapshot.ShxGlyphInstances.ToArray();
        CadMTextRectangle decoration = Assert.Single(snapshot.MTextDecorations.ToArray());

        Assert.Equal(CadEntityKind.ShxMText, entity.Kind);
        Assert.Equal(new CadPoint3D(0, 0, 0), text.Origin);
        Assert.All(glyphs, glyph => Assert.Equal(CadShxOrientation.Vertical, glyph.Glyph.Orientation));
        Assert.True(glyphs[1].Y > glyphs[0].Y);
        Assert.True(glyphs[3].X < glyphs[0].X);
        Assert.True(decoration.Height > decoration.Width);
        Assert.Single(snapshot.MTextStrokes.ToArray());
        Assert.Equal(5, snapshot.MTextBackgrounds.Length);
        Assert.True(text.ContentHeight > text.ContentWidth);
        Assert.False(entity.Bounds.IsEmpty);
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        Assert.True(print.SceneStatistics.RecordedCommandCount > 0);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void VerticalByStyleColumnsAdvanceBelowOrReverseAbove(
        bool reverseFlow,
        bool secondColumnIsBelow)
    {
        CadShxGlyphCache cache = CreateCache(vertical: true);
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add vertical SHX columns", document =>
        {
            var style = new TextStyle("VERTICALSHX")
            {
                Filename = "test.shx",
                Flags = StyleFlags.VerticalText,
            };
            document.TextStyles.Add(style);
            var text = new MText
            {
                Style = style,
                Value = @"AA\NAA",
                Height = 10,
                DrawingDirection = DrawingDirectionType.ByStyle,
            };
            text.ColumnData.ColumnType = ColumnType.StaticColumns;
            text.ColumnData.ColumnCount = 2;
            text.ColumnData.Width = 20;
            text.ColumnData.Gutter = 5;
            text.ColumnData.FlowReversed = reverseFlow;
            text.ColumnData.Heights.Add(30);
            text.ColumnData.Heights.Add(30);
            document.Entities.Add(text);
        });

        CadDocumentSnapshot snapshot = Compile(session, cache);
        CadShxMTextPrimitive text = Assert.Single(snapshot.ShxMTexts.ToArray());
        ReadOnlySpan<CadShxGlyphInstance> glyphs = snapshot.ShxGlyphInstances.Span;

        Assert.Equal(2, text.ColumnCount);
        Assert.Equal(45.0f, text.ContentHeight);
        Assert.Equal(
            secondColumnIsBelow,
            glyphs[text.GlyphOffset + 2].Y > glyphs[text.GlyphOffset].Y);
    }

    [Theory]
    [InlineData(AttachmentPointType.TopLeft, 0.0, 0.0)]
    [InlineData(AttachmentPointType.TopCenter, -0.5, 0.0)]
    [InlineData(AttachmentPointType.TopRight, -1.0, 0.0)]
    [InlineData(AttachmentPointType.MiddleLeft, 0.0, -0.5)]
    [InlineData(AttachmentPointType.MiddleCenter, -0.5, -0.5)]
    [InlineData(AttachmentPointType.MiddleRight, -1.0, -0.5)]
    [InlineData(AttachmentPointType.BottomLeft, 0.0, -1.0)]
    [InlineData(AttachmentPointType.BottomCenter, -0.5, -1.0)]
    [InlineData(AttachmentPointType.BottomRight, -1.0, -1.0)]
    public void VerticalAttachmentsUsePhysicalContentDimensions(
        AttachmentPointType attachment,
        double xFactor,
        double yFactor)
    {
        CadShxGlyphCache cache = CreateCache(vertical: true);
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add attached vertical SHX MTEXT", document =>
        {
            var style = new TextStyle("VERTICALSHX")
            {
                Filename = "test.shx",
                Flags = StyleFlags.VerticalText,
            };
            document.TextStyles.Add(style);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = "AA",
                Height = 10,
                RectangleWidth = 30,
                DrawingDirection = DrawingDirectionType.TopToBottom,
                AttachmentPoint = attachment,
                BackgroundFillFlags = BackgroundFillFlags.UseBackgroundFillColor,
                BackgroundScale = 1.0,
            });
        });

        CadDocumentSnapshot snapshot = Compile(session, cache);
        CadShxMTextPrimitive text = Assert.Single(snapshot.ShxMTexts.ToArray());
        CadMTextRectangle background = Assert.Single(snapshot.MTextBackgrounds.ToArray());

        Assert.InRange(
            Math.Abs(background.X - (text.ContentWidth * xFactor)),
            0.0,
            1e-5);
        Assert.InRange(
            Math.Abs(background.Y - (text.ContentHeight * yFactor)),
            0.0,
            1e-5);
        Assert.InRange(Math.Abs(background.Width - text.ContentWidth), 0.0, 1e-5);
        Assert.InRange(Math.Abs(background.Height - text.ContentHeight), 0.0, 1e-5);
    }

    [Fact]
    public void VerticalMTextRejectsHorizontalOnlyFontsTransactionally()
    {
        CadShxGlyphCache cache = CreateCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add invalid vertical SHX MTEXT", document =>
        {
            var style = new TextStyle("HORIZONTALSHX")
            {
                Filename = "test.shx",
                Flags = StyleFlags.VerticalText,
            };
            document.TextStyles.Add(style);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = "AA",
                DrawingDirection = DrawingDirectionType.TopToBottom,
            });
        });

        CadDocumentSnapshot snapshot = Compile(session, cache);

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.ShxMTexts.ToArray());
        Assert.Empty(snapshot.ShxMTextGlyphRuns.ToArray());
        Assert.Empty(snapshot.ShxGlyphInstances.ToArray());
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("dual-orientation", StringComparison.Ordinal));
    }

    [Fact]
    public void NonbreakingSpaceUsesTheSpaceGlyphWithoutCreatingAWrapOpportunity()
    {
        CadShxGlyphCache cache = CreateCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add wrapping SHX MTEXT", document =>
        {
            TextStyle style = AddStyle(document);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = "A A",
                Height = 10,
                RectangleWidth = 25,
            });
            document.Entities.Add(new MText
            {
                Style = style,
                Value = "A\u00A0A",
                Height = 10,
                RectangleWidth = 25,
            });
        });

        CadDocumentSnapshot snapshot = Compile(session, cache);
        CadShxMTextPrimitive[] texts = snapshot.ShxMTexts.ToArray();
        ReadOnlySpan<CadShxGlyphInstance> glyphs = snapshot.ShxGlyphInstances.Span;

        Assert.Equal(2, texts.Length);
        Assert.NotEqual(
            glyphs[texts[0].GlyphOffset].Y,
            glyphs[texts[0].GlyphOffset + 2].Y);
        Assert.Equal(
            glyphs[texts[1].GlyphOffset].Y,
            glyphs[texts[1].GlyphOffset + 2].Y);
        Assert.Equal((ushort)32, glyphs[texts[1].GlyphOffset + 1].Glyph.ShapeNumber);
    }

    [Fact]
    public void DynamicColumnsAndNestedBlockAffineTransformRemainDeterministic()
    {
        CadShxGlyphCache cache = CreateCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong rootHandle = 0;
        session.Edit("Add transformed automatic SHX columns", document =>
        {
            TextStyle style = AddStyle(document);
            var text = new MText
            {
                Style = style,
                Value = @"AA\PBB\PAA\PBB",
                Height = 4,
            };
            text.ColumnData.ColumnType = ColumnType.DynamicColumns;
            text.ColumnData.ColumnCount = 2;
            text.ColumnData.Width = 20;
            text.ColumnData.Gutter = 3;
            text.ColumnData.AutoHeight = true;
            var block = new BlockRecord("SHX_MTEXT_COLUMNS");
            block.Entities.Add(text);
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

        CadDocumentSnapshot snapshot = Compile(session, cache);
        CadShxMTextPrimitive text = Assert.Single(snapshot.ShxMTexts.ToArray());
        CadShxGlyphInstance[] glyphs = snapshot.ShxGlyphInstances.ToArray();

        Assert.Equal(rootHandle, Assert.Single(snapshot.Entities.ToArray()).Handle);
        Assert.Equal(2, text.ColumnCount);
        AssertPoint(new CadPoint3D(10, 20, 0), text.Origin);
        AssertPoint(new CadPoint3D(0, 2, 0), text.XAxis);
        AssertPoint(new CadPoint3D(3, 0, 0), text.YAxis);
        Assert.Contains(glyphs, glyph => glyph.X < 20.0f);
        Assert.Contains(glyphs, glyph => glyph.X >= 23.0f);
    }

    [Fact]
    public void ExactSelectionCoversPathsMasksDecorationsAndSeparatorsWithoutWarmAllocation()
    {
        CadShxGlyphCache cache = CreateCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add selectable SHX MTEXT", document =>
        {
            TextStyle style = AddStyle(document);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = @"\W1.4;\Q12;A\LAA\l\S1/2;",
                Height = 10,
                RectangleWidth = 100,
                BackgroundColor = new ACadSharp.Color(20, 30, 40),
                BackgroundFillFlags = BackgroundFillFlags.UseBackgroundFillColor,
            });
        });
        CadDocumentSnapshot snapshot = Compile(session, cache);
        CadShxMTextPrimitive text = Assert.Single(snapshot.ShxMTexts.ToArray());
        CadSelectionCandidate candidate = SingleCandidate(snapshot);
        CadShxGlyphInstance first = snapshot.ShxGlyphInstances.Span[text.GlyphOffset];
        CadPoint3D pathPoint = text.Origin +
            (text.XAxis * first.X) +
            (text.YAxis * first.Y);
        CadMTextRectangle mask = Assert.Single(snapshot.MTextBackgrounds.ToArray());
        CadMTextRectangle decoration = Assert.Single(snapshot.MTextDecorations.ToArray());
        CadMTextStroke stroke = Assert.Single(snapshot.MTextStrokes.ToArray());

        AssertHit(pathPoint, 1e-8);
        AssertLocalHit(mask.X + mask.Width * 0.5, mask.Y + mask.Height * 0.5);
        AssertLocalHit(
            decoration.X + decoration.Width * 0.5,
            decoration.Y + decoration.Height * 0.5);
        AssertLocalHit(
            (stroke.StartX + stroke.EndX) * 0.5,
            (stroke.StartY + stroke.EndY) * 0.5);
        CadBounds3D bounds = snapshot.Entities.Span[0].Bounds;
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                bounds,
                CadBoundsSelectionMode.Window).Status);

        _ = CadSelectionHitTester.HitTestPoint(snapshot, candidate, pathPoint, 1e-8);
        _ = CadSelectionHitTester.HitTestBounds(
            snapshot, candidate, bounds, CadBoundsSelectionMode.Window);
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int index = 0; index < 1_000; index++)
        {
            checksum += CadSelectionHitTester.HitTestPoint(
                snapshot, candidate, pathPoint, 1e-8).IsHit ? 1 : 0;
            checksum += CadSelectionHitTester.HitTestBounds(
                snapshot, candidate, bounds, CadBoundsSelectionMode.Window).IsHit ? 1 : 0;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2_000, checksum);
        Assert.Equal(0, allocated);

        void AssertLocalHit(double x, double y) =>
            AssertHit(text.Origin + (text.XAxis * x) + (text.YAxis * y), 1e-9);

        void AssertHit(CadPoint3D point, double tolerance) =>
            Assert.Equal(
                CadPointHitStatus.Hit,
                CadSelectionHitTester.HitTestPoint(
                    snapshot, candidate, point, tolerance).Status);
    }

    [Fact]
    public void LateFailureLeavesAllSharedRetainedStreamsTransactional()
    {
        CadShxGlyphCache cache = CreateCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add invalid SHX MTEXT", document =>
        {
            TextStyle style = AddStyle(document);
            document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
            document.Entities.Add(new MText
            {
                Style = style,
                Value = "AA shaped before invalid background",
                RectangleWidth = 200,
                BackgroundFillFlags = BackgroundFillFlags.UseBackgroundFillColor,
                BackgroundScale = 0.5,
            });
        });

        CadDocumentSnapshot snapshot = Compile(session, cache);

        Assert.Single(snapshot.Lines.ToArray());
        Assert.Empty(snapshot.ShxMTexts.ToArray());
        Assert.Empty(snapshot.ShxMTextGlyphRuns.ToArray());
        Assert.Empty(snapshot.ShxGlyphInstances.ToArray());
        Assert.Empty(snapshot.MTextBackgrounds.ToArray());
        Assert.Empty(snapshot.MTextDecorations.ToArray());
        Assert.Empty(snapshot.MTextStrokes.ToArray());
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP002");
    }

    [Fact]
    public void TrueTypeTextAndShxMTextShareOneDocumentGlyphBudget()
    {
        CadShxGlyphCache cache = CreateCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add mixed retained text", document =>
        {
            var trueTypeStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            TextStyle shxStyle = AddStyle(document);
            document.TextStyles.Add(trueTypeStyle);
            document.Entities.Add(new TextEntity("A") { Style = trueTypeStyle });
            document.Entities.Add(new MText { Style = shxStyle, Value = "AA" });
        });

        InvalidOperationException exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadSnapshotCompiler().Compile(
                session,
                new CadSnapshotOptions
                {
                    TextFontResolver = new FixedTrueTypeResolver(),
                    ShxFontResolver = new FixedResolver(cache),
                    MaxTextGlyphs = 2,
                }));

        Assert.Contains("document limit of 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintPlanReplaysTheSameRetainedShxMTextCommands()
    {
        CadShxGlyphCache cache = CreateCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add printable SHX MTEXT", document =>
        {
            TextStyle style = AddStyle(document);
            document.Entities.Add(new MText
            {
                Style = style,
                Value = @"AA\P\LBB\l",
                Height = 8,
                RectangleWidth = 80,
            });
        });
        CadDocumentSnapshot snapshot = Compile(session, cache);
        using CadPrintPlan plan = new CadPrintPlanCompiler().Compile(snapshot);
        using GpuPicture page = plan.CreatePagePicture();
        GpuPicture content = page.GetCommand(1).Picture!;

        Assert.Equal(1, plan.SceneStatistics.RecordedEntityCount);
        Assert.Contains(
            Enumerable.Range(0, content.CommandCount).Select(content.GetCommand),
            command => command.Type == RenderCommandType.DrawPath);
        Assert.Contains(
            Enumerable.Range(0, content.CommandCount).Select(content.GetCommand),
            command => command.Type == RenderCommandType.DrawRect);
    }

    [Fact]
    public void BigFontBoldItalicAndRightToLeftContractsRemainExplicitGates()
    {
        CadShxGlyphCache cache = CreateCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add gated SHX MTEXT", document =>
        {
            var big = new TextStyle("BIG")
            {
                Filename = "test.shx",
                BigFontFilename = "asian.shx",
            };
            TextStyle standard = AddStyle(document);
            document.TextStyles.Add(big);
            document.Entities.Add(new MText
            {
                Style = big,
                Value = "AA",
            });
            document.Entities.Add(new MText
            {
                Style = standard,
                Value = @"\Ftest.shx|b1;A",
            });
            document.Entities.Add(new MText
            {
                Style = standard,
                Value = @"\FInter.ttf;A",
            });
            document.Entities.Add(new MText
            {
                Style = standard,
                Value = "AA",
                DrawingDirection = DrawingDirectionType.RightToLeft,
            });
        });

        CadDocumentSnapshot snapshot = Compile(session, cache);

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(4, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("Big Font MTEXT", StringComparison.Ordinal));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("cannot apply bold", StringComparison.Ordinal));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("mixed TrueType/SHX", StringComparison.Ordinal));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("left-to-right", StringComparison.Ordinal));
    }

    private static CadDocumentSnapshot Compile(
        CadDocumentSession session,
        CadShxGlyphCache cache) =>
        Compile(session, new FixedResolver(cache));

    private static CadDocumentSnapshot Compile(
        CadDocumentSession session,
        ICadShxFontResolver resolver) =>
        new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                ShxFontResolver = resolver,
            });

    private static TextStyle AddStyle(CadDocument document)
    {
        var style = new TextStyle("TESTSHX") { Filename = "test.shx" };
        document.TextStyles.Add(style);
        return style;
    }

    private static CadSelectionCandidate SingleCandidate(CadDocumentSnapshot snapshot)
    {
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        return new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            entity.Handle,
            entity.Kind,
            entity.Bounds);
    }

    private static CadShxGlyphCache CreateCache(
        string name = "TESTSHX",
        bool vertical = false)
    {
        var shapes = new List<(ushort Number, string Name, byte[] Program)>
        {
            (0, name, new byte[] { 10, 2, vertical ? (byte)2 : (byte)0, 0 }),
            (32, "SPACE", vertical
                ? new byte[] { 2, 8, 10, 0, 14, 8, 0xF6, 0xF6, 0 }
                : new byte[] { 2, 8, 10, 0, 0 }),
        };
        const string characters = "AB12.shapedbforinvlgcku";
        foreach (char value in characters.Distinct())
        {
            shapes.Add(((ushort)value, value.ToString(), vertical
                ? new byte[]
                {
                    2, 14, 8, 0xFF, 2,
                    1, 0x14,
                    2, 8, 2, 0xFF,
                    14, 8, 0xFF, 0xFD,
                    0,
                }
                : new byte[]
                {
                    0xA4,
                    0xA0,
                    2,
                    8, 0, 0xF6,
                    0,
                }));
        }
        shapes.Add((256, "DEGREE", new byte[] { 2, 8, 10, 0, 0 }));
        shapes.Add((257, "PLUSMINUS", new byte[] { 2, 8, 10, 0, 0 }));
        shapes.Add((258, "DIAMETER", new byte[] { 2, 8, 10, 0, 0 }));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write(shapes.Min(shape => shape.Number));
        writer.Write(shapes.Max(shape => shape.Number));
        writer.Write(checked((ushort)shapes.Count));
        foreach ((ushort number, string shapeName, byte[] program) in shapes)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(shapeName);
            writer.Write(number);
            writer.Write(checked((ushort)(nameBytes.Length + 1 + program.Length)));
        }
        foreach ((ushort _, string shapeName, byte[] program) in shapes)
        {
            writer.Write(Encoding.ASCII.GetBytes(shapeName));
            writer.Write((byte)0);
            writer.Write(program);
        }
        writer.Write("EOF"u8);
        return new CadShxGlyphCache(CadShxFont.Parse(stream.ToArray()));
    }

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1e-9);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1e-9);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, 1e-9);
    }

    private sealed class FixedResolver(CadShxGlyphCache cache) : ICadShxFontResolver
    {
        public CadShxFontResolution Resolve(in CadShxFontRequest request) =>
            string.IsNullOrWhiteSpace(request.BigFontFilename)
                ? new CadShxFontResolution(cache, "test.shx", false)
                : default;
    }

    private sealed class FixedTrueTypeResolver : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
            new(InterFontFamily.Regular, IsSubstitution: false);
    }
}

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using System.Numerics;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadWipeoutTests
{
    [Fact]
    public void PolygonClipRetainsPixelFrameAndCompilesManagedAndNativePath()
    {
        Wipeout wipeout = CreateWipeout();
        var document = new CadDocument();
        document.Entities.Add(wipeout);
        var session = new CadDocumentSession(document);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                DrawingBackgroundColor = new CadColor32(12, 34, 56, 10),
            });
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());
        CadWipeoutPrimitive primitive = Assert.Single(snapshot.Wipeouts.ToArray());
        CadWipeoutClipPoint[] clip = snapshot.WipeoutClipPoints.ToArray();
        Assert.Equal(CadEntityKind.Wipeout, header.Kind);
        Assert.Equal(new CadPoint3D(100, 200, 7), primitive.Origin);
        Assert.Equal(new CadPoint3D(2, 0, 0), primitive.UVector);
        Assert.Equal(new CadPoint3D(0, 3, 0), primitive.VVector);
        Assert.Equal(
            [
                new CadWipeoutClipPoint(0, 0),
                new CadWipeoutClipPoint(5, 0),
                new CadWipeoutClipPoint(5, 4),
                new CadWipeoutClipPoint(0, 4),
            ],
            clip);
        Assert.Equal(new CadColor32(12, 34, 56), primitive.MaskColor);
        Assert.Equal(new CadPoint3D(100, 200, 7), header.Bounds.Min);
        Assert.Equal(new CadPoint3D(110, 212, 7), header.Bounds.Max);

        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.NotNull(command.Brush);
        Assert.NotNull(command.Pen);
        Assert.Equal(FillRule.EvenOdd, command.Path!.FillRule);
        Assert.Equal(3, Assert.Single(command.Path.Figures).Segments.Count);
        Assert.Equal(new Vector4(12 / 255.0f, 34 / 255.0f, 56 / 255.0f, 1),
            Assert.IsType<SolidColorBrush>(command.Brush).Color);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            1U,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(1, native.SourceCommandCount);
        Assert.Equal(2, native.NativeDrawCount);
    }

    [Fact]
    public void InvertedClipMasksOuterRegionLeavesHoleAndSelectsExactGeometry()
    {
        Wipeout wipeout = CreateWipeout();
        wipeout.ClipMode = ClipMode.Inside;
        var document = new CadDocument();
        document.Entities.Add(wipeout);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        CadEntityHeader header = snapshot.Entities.Span[0];
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            header.Handle,
            header.Kind,
            header.Bounds);

        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();
        Assert.Equal(2, commands.Length);
        Assert.NotNull(commands[0].Brush);
        Assert.Null(commands[0].Pen);
        Assert.Equal(2, commands[0].Path!.Figures.Count);
        Assert.Null(commands[1].Brush);
        Assert.NotNull(commands[1].Pen);
        Assert.Single(commands[1].Path!.Figures);
        Assert.True(CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(116, 218, 7),
            0.01).IsHit);
        Assert.False(CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(106, 206, 7),
            0.01).IsHit);
        Assert.True(CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(100, 200, 7.005),
            0.01).IsHit);
        Assert.True(CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(115, 217, 6.9),
                new CadPoint3D(117, 219, 7.1)),
            CadBoundsSelectionMode.Crossing).IsHit);
        Assert.False(CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(105, 205, 6.9),
                new CadPoint3D(107, 207, 7.1)),
            CadBoundsSelectionMode.Crossing).IsHit);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            1U,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(2, native.SourceCommandCount);
        Assert.Equal(2, native.NativeDrawCount);
    }

    [Fact]
    public void WipeoutFrameTwoDisplaysOnScreenButDoesNotPlot()
    {
        var document = new CadDocument();
        document.DictionaryVariables.AddOrUpdateVariable(
            DictionaryVariable.WipeoutFrame,
            ((int)WipeoutFrameType.DisplayNoPlotted).ToString());
        document.Entities.Add(CreateWipeout());
        var session = new CadDocumentSession(document);
        var compiler = new CadSnapshotCompiler();
        CadDocumentSnapshot screen = compiler.Compile(session);
        CadDocumentSnapshot paper = compiler.Compile(
            session,
            new CadSnapshotOptions
            {
                DrawOrderPurpose = CadDrawOrderPurpose.Plotting,
                DrawingBackgroundColor = new CadColor32(255, 255, 255),
            });

        Assert.True(screen.Wipeouts.Span[0].DrawFrame);
        Assert.False(paper.Wipeouts.Span[0].DrawFrame);
        RenderCommand screenCommand = Assert.Single(
            new CadPlanSceneCompiler().Compile(screen).DrawingContext.Commands.ToArray());
        RenderCommand paperCommand = Assert.Single(
            new CadPlanSceneCompiler().Compile(paper).DrawingContext.Commands.ToArray());
        Assert.NotNull(screenCommand.Pen);
        Assert.Null(paperCommand.Pen);
        Assert.Equal(Vector4.One,
            Assert.IsType<SolidColorBrush>(paperCommand.Brush).Color);
        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(paper);
        Assert.Equal(1, print.SceneStatistics.RecordedEntityCount);
        Assert.Equal(1, print.SceneStatistics.RecordedCommandCount);
    }

    [Fact]
    public void ClippingOffUsesEntireImageRectangle()
    {
        Wipeout wipeout = CreateWipeout();
        wipeout.ClippingState = false;
        var document = new CadDocument();
        document.Entities.Add(wipeout);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadWipeoutPrimitive primitive = snapshot.Wipeouts.Span[0];
        RenderCommand command = Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray());

        Assert.False(primitive.IsClipped);
        Assert.False(primitive.IsInverted);
        Assert.Empty(snapshot.WipeoutClipPoints.ToArray());
        Assert.Equal(new CadPoint3D(100, 200, 7), snapshot.Bounds.Min);
        Assert.Equal(new CadPoint3D(120, 224, 7), snapshot.Bounds.Max);
        Assert.Equal(
            new[] { Vector2.Zero, new Vector2(10, 0), new Vector2(10, 8), new Vector2(0, 8) },
            GetFigurePoints(Assert.Single(command.Path!.Figures)));
    }

    [Fact]
    public void RectangularClipExpandsHalfPixelCornersAndHiddenFrameRemainsSelectable()
    {
        Wipeout wipeout = CreateWipeout();
        wipeout.ClipBoundaryVertices.Clear();
        wipeout.ClipBoundaryVertices.Add(new XY(0.5, 1.5));
        wipeout.ClipBoundaryVertices.Add(new XY(6.5, 5.5));
        var document = new CadDocument();
        document.DictionaryVariables.AddOrUpdateVariable(
            DictionaryVariable.WipeoutFrame,
            ((int)WipeoutFrameType.NoDisplayOrPlotted).ToString());
        document.Entities.Add(wipeout);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadWipeoutPrimitive primitive = snapshot.Wipeouts.Span[0];
        RenderCommand command = Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray());
        CadEntityHeader header = snapshot.Entities.Span[0];
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            header.Handle,
            header.Kind,
            header.Bounds);

        Assert.Equal(
            [
                new CadWipeoutClipPoint(1, 2),
                new CadWipeoutClipPoint(7, 2),
                new CadWipeoutClipPoint(7, 6),
                new CadWipeoutClipPoint(1, 6),
            ],
            snapshot.WipeoutClipPoints.ToArray());
        Assert.False(primitive.DrawFrame);
        Assert.Null(command.Pen);
        Assert.True(CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(102, 206, 7),
            0.01).IsHit);
    }

    [Fact]
    public void DisplayFlagsCanSuppressMaskWithoutSuppressingFrame()
    {
        Wipeout hiddenMask = CreateWipeout();
        hiddenMask.Flags &= ~ImageDisplayFlags.ShowImage;
        var document = new CadDocument();
        document.Entities.Add(hiddenMask);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        RenderCommand command = Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray());
        CadEntityHeader header = snapshot.Entities.Span[0];
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            header.Handle,
            header.Kind,
            header.Bounds);

        Assert.False(snapshot.Wipeouts.Span[0].DrawMask);
        Assert.Null(command.Brush);
        Assert.NotNull(command.Pen);
        Assert.False(CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(106, 206, 7),
            0.01).IsHit);
        Assert.True(CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(100, 200, 7),
            0.01).IsHit);
    }

    [Fact]
    public void FrameUsesEntityLinetypeAfterMaskWithoutChangingPaintOrder()
    {
        var document = new CadDocument();
        var dashed = new LineType("WIPEOUT_DASHED");
        dashed.AddSegment(new LineType.Segment { Length = 3 });
        dashed.AddSegment(new LineType.Segment { Length = -1 });
        document.LineTypes.Add(dashed);
        Wipeout wipeout = CreateWipeout();
        wipeout.LineType = dashed;
        document.Entities.Add(wipeout);

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(new CadDocumentSession(document)));
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();

        Assert.Equal(2, commands.Length);
        Assert.NotNull(commands[0].Brush);
        Assert.Null(commands[0].Pen);
        Assert.Null(commands[1].Brush);
        Assert.NotNull(commands[1].Pen);
        Assert.True(commands[1].Path!.Figures.Count > 1);
        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(4, scene.Statistics.LineTypeSourceSegmentCount);
    }

    [Fact]
    public void NonAlignedDisplayFlagSuppressesTiltedPlanMaskButKeepsFrame()
    {
        Wipeout wipeout = CreateWipeout();
        wipeout.VVector = new XYZ(0, 0, 3);
        wipeout.Flags &= ~ImageDisplayFlags.ShowNotAlignedImage;
        var document = new CadDocument();
        document.Entities.Add(wipeout);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        RenderCommand command = Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray());

        Assert.False(snapshot.Wipeouts.Span[0].ShowWhenNotAligned);
        Assert.Null(command.Brush);
        Assert.NotNull(command.Pen);
        CadEntityHeader header = snapshot.Entities.Span[0];
        Assert.False(CadSelectionHitTester.HitTestPoint(
            snapshot,
            new CadSelectionCandidate(
                snapshot.ContentGeneration,
                0,
                header.Handle,
                header.Kind,
                header.Bounds),
            new CadPoint3D(106, 200, 13),
            0.01).IsHit);
    }

    [Fact]
    public void NestedInsertTransformsOriginAndPixelVectorsWithoutTranslationLeakage()
    {
        var block = new BlockRecord("WIPEOUT_BLOCK");
        Wipeout wipeout = CreateWipeout();
        wipeout.InsertPoint = new XYZ(1, 2, 3);
        block.Entities.Add(wipeout);
        var document = new CadDocument();
        document.BlockRecords.Add(block);
        document.Entities.Add(new Insert(block)
        {
            InsertPoint = new XYZ(10, 20, 30),
            XScale = 2,
            YScale = 4,
            ZScale = 3,
        });

        CadWipeoutPrimitive primitive = Assert.Single(
            new CadSnapshotCompiler().Compile(
                new CadDocumentSession(document)).Wipeouts.ToArray());

        Assert.Equal(new CadPoint3D(12, 28, 39), primitive.Origin);
        Assert.Equal(new CadPoint3D(4, 0, 0), primitive.UVector);
        Assert.Equal(new CadPoint3D(0, 12, 0), primitive.VVector);
    }

    [Fact]
    public void GenericEditingMovesDuplicatesAndRoundTripsUndoRedo()
    {
        var document = new CadDocument();
        Wipeout wipeout = CreateWipeout();
        document.Entities.Add(wipeout);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadTranslateEntitiesCommand(
            [wipeout.Handle],
            new CadPoint3D(10, -20, 5)));
        var duplicate = new CadDuplicateModelSpaceEntityCommand(
            wipeout.Handle,
            new CadPoint3D(50, 0, 0));
        history.Execute(duplicate);

        CadWipeoutPrimitive[] moved = new CadSnapshotCompiler().Compile(session).Wipeouts.ToArray();
        Assert.Equal(new CadPoint3D(110, 180, 12), moved[0].Origin);
        Assert.Equal(new CadPoint3D(160, 180, 12), moved[1].Origin);
        Assert.Equal(new CadPoint3D(2, 0, 0), moved[1].UVector);
        Assert.True(history.TryUndo(out _));
        Assert.Single(new CadSnapshotCompiler().Compile(session).Wipeouts.ToArray());
        Assert.True(history.TryRedo(out _));
        Assert.Equal(2, new CadSnapshotCompiler().Compile(session).Wipeouts.Length);
    }

    [Fact]
    public void GenericRotationAndScaleTransformOriginAndPixelBasisThroughUndo()
    {
        var document = new CadDocument();
        Wipeout wipeout = CreateWipeout();
        document.Entities.Add(wipeout);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadRotateEntitiesCommand(
            [wipeout.Handle],
            new CadPoint3D(0, 0, 1),
            Math.PI / 2));
        history.Execute(new CadScaleEntitiesCommand([wipeout.Handle], 2));

        CadWipeoutPrimitive transformed = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Wipeouts.ToArray());
        Assert.True(IsNear(transformed.Origin, new CadPoint3D(-400, 200, 14)));
        Assert.True(IsNear(transformed.UVector, new CadPoint3D(0, 4, 0)));
        Assert.True(IsNear(transformed.VVector, new CadPoint3D(-6, 0, 0)));
        Assert.True(history.TryUndo(out _));
        Assert.True(history.TryUndo(out _));
        CadWipeoutPrimitive restored = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Wipeouts.ToArray());
        Assert.True(IsNear(restored.Origin, new CadPoint3D(100, 200, 7)));
        Assert.True(IsNear(restored.UVector, new CadPoint3D(2, 0, 0)));
        Assert.True(IsNear(restored.VVector, new CadPoint3D(0, 3, 0)));
    }

    [Fact]
    public void InvalidClipIsDiagnosedAndConfiguredBudgetsAreEnforced()
    {
        Wipeout invalid = CreateWipeout();
        invalid.ClipBoundaryVertices[0] = new XY(double.NaN, 0);
        var invalidDocument = new CadDocument();
        invalidDocument.Entities.Add(invalid);

        CadDocumentSnapshot invalidSnapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(invalidDocument));

        Assert.Empty(invalidSnapshot.Wipeouts.ToArray());
        Assert.Equal(1, invalidSnapshot.Statistics.InvalidEntityCount);
        Assert.Contains(invalidSnapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Code == "CADSNAP002");

        var transactionalDocument = new CadDocument();
        transactionalDocument.Entities.Add(CreateWipeout());
        Wipeout overflowingBounds = CreateWipeout();
        overflowingBounds.Size = new XY(double.MaxValue, 8);
        overflowingBounds.UVector = new XYZ(2, 0, 0);
        overflowingBounds.ClipMode = ClipMode.Inside;
        transactionalDocument.Entities.Add(overflowingBounds);
        CadDocumentSnapshot transactionalSnapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(transactionalDocument));
        Assert.Single(transactionalSnapshot.Wipeouts.ToArray());
        Assert.Equal(4, transactionalSnapshot.WipeoutClipPoints.Length);
        Assert.Equal(1, transactionalSnapshot.Statistics.InvalidEntityCount);

        Wipeout oversized = CreateWipeout();
        oversized.ClipBoundaryVertices.Insert(2, new XY(3.5, 1.5));
        var oversizedDocument = new CadDocument();
        oversizedDocument.Entities.Add(oversized);
        CadDocumentSnapshot unsupportedSnapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(oversizedDocument),
            new CadSnapshotOptions { MaxWipeoutClipVerticesPerEntity = 4 });
        Assert.Empty(unsupportedSnapshot.Wipeouts.ToArray());
        Assert.Equal(1, unsupportedSnapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(unsupportedSnapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Code == "CADSNAP003");

        var totalDocument = new CadDocument();
        totalDocument.Entities.Add(CreateWipeout());
        Wipeout second = CreateWipeout();
        second.InsertPoint = new XYZ(500, 0, 0);
        totalDocument.Entities.Add(second);
        InvalidOperationException limitError = Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadSnapshotCompiler().Compile(
                new CadDocumentSession(totalDocument),
                new CadSnapshotOptions { MaxWipeoutClipVertices = 4 }));
        Assert.Contains("document limit", limitError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceOrderKeepsWipeoutBetweenUnderlyingAndForegroundGeometry()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(new XYZ(90, 205, 7), new XYZ(130, 205, 7)));
        document.Entities.Add(CreateWipeout());
        document.Entities.Add(new Line(new XYZ(90, 210, 7), new XYZ(130, 210, 7)));

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(new CadDocumentSession(document)));

        Assert.Equal(
            [RenderCommandType.DrawLine, RenderCommandType.DrawPath, RenderCommandType.DrawLine],
            scene.DrawingContext.Commands.ToArray().Select(command => command.Type));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task OutsideClipGeometryAndFramePolicySurviveDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument();
        document.DictionaryVariables.AddOrUpdateVariable(
            DictionaryVariable.WipeoutFrame,
            ((int)WipeoutFrameType.DisplayNoPlotted).ToString());
        Wipeout source = CreateWipeout();
        document.Entities.Add(source);
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            new CadDocumentSession(document),
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"wipeout.{format.ToString().ToLowerInvariant()}");
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);
        CadWipeoutPrimitive restored = Assert.Single(snapshot.Wipeouts.ToArray());

        Assert.True(restored.IsClipped);
        Assert.False(restored.IsInverted);
        Assert.True(restored.DrawFrame);
        Assert.Equal(new CadPoint3D(100, 200, 7), restored.Origin);
        Assert.Equal(new CadPoint3D(2, 0, 0), restored.UVector);
        Assert.Equal(new CadPoint3D(0, 3, 0), restored.VVector);
        Assert.Equal(4, restored.ClipPointCount);
        Assert.Equal(
            ((int)WipeoutFrameType.DisplayNoPlotted).ToString(),
            loaded.Session.Read(value => value.DictionaryVariables.GetValue(
                DictionaryVariable.WipeoutFrame)));
    }

    [Fact]
    public async Task InvertedClipSurvivesDwgAndIsRejectedBeforeLossyDxfWrite()
    {
        var document = new CadDocument();
        Wipeout source = CreateWipeout();
        source.ClipMode = ClipMode.Inside;
        var store = new CadDocumentStore();
        var session = new CadDocumentSession(document);
        session.Edit("Add inverted WIPEOUT", value => value.Entities.Add(source));
        using var dxf = new MemoryStream();
        dxf.Write([1, 2, 3]);
        dxf.Position = 1;

        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await store.SaveAsync(
                session,
                dxf,
                CadDocumentFormat.Dxf,
                new CadSaveOptions { AllowUncertifiedWrite = true }));

        Assert.Contains("CADSAVE001", error.Message, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1, 2, 3 }, dxf.ToArray());
        Assert.Equal(1, dxf.Position);
        Assert.True(session.IsDirty);

        using var dwg = new MemoryStream();
        await store.SaveAsync(
            session,
            dwg,
            CadDocumentFormat.Dwg,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        dwg.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(dwg, CadDocumentFormat.Dwg);
        CadWipeoutPrimitive restored = Assert.Single(
            new CadSnapshotCompiler().Compile(loaded.Session).Wipeouts.ToArray());

        Assert.True(restored.IsInverted);
    }

    private static Wipeout CreateWipeout()
    {
        var wipeout = new Wipeout
        {
            InsertPoint = new XYZ(100, 200, 7),
            UVector = new XYZ(2, 0, 0),
            VVector = new XYZ(0, 3, 0),
            Size = new XY(10, 8),
            ClippingState = true,
            ClipMode = ClipMode.Outside,
        };
        wipeout.ClipBoundaryVertices.Add(new XY(-0.5, -0.5));
        wipeout.ClipBoundaryVertices.Add(new XY(4.5, -0.5));
        wipeout.ClipBoundaryVertices.Add(new XY(4.5, 3.5));
        wipeout.ClipBoundaryVertices.Add(new XY(-0.5, 3.5));
        return wipeout;
    }

    private static Vector2[] GetFigurePoints(PathFigure figure)
    {
        var points = new Vector2[figure.Segments.Count + 1];
        points[0] = figure.StartPoint;
        for (int i = 1; i < points.Length; i++)
        {
            points[i] = Assert.IsType<LineSegment>(figure.Segments[i - 1]).Point;
        }
        return points;
    }

    private static bool IsNear(CadPoint3D first, CadPoint3D second) =>
        Math.Abs(first.X - second.X) <= 1e-10 &&
        Math.Abs(first.Y - second.Y) <= 1e-10 &&
        Math.Abs(first.Z - second.Z) <= 1e-10;
}

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using System.Numerics;
using Xunit;
using ACadLayout = ACadSharp.Objects.Layout;

namespace ProGPU.CAD.Tests;

public sealed class CadViewportTests
{
    [Fact]
    public void LayoutSnapshotAtomicallyOwnsModelPaperAndViewportState()
    {
        var document = new CadDocument();
        ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(20, 10, 0)));
        layout.AssociatedBlock.Entities.Add(new Line(
            new XYZ(1, 2, 0),
            new XYZ(3, 4, 0)));
        var viewport = new Viewport
        {
            Center = new XYZ(100, 75, 0),
            Width = 120,
            Height = 80,
            ViewCenter = new XY(10, 20),
            ViewTarget = new XYZ(1, 2, 3),
            ViewDirection = new XYZ(0, 0, 2),
            ViewHeight = 40,
            TwistAngle = 0.25,
            LensLength = 50,
            FrontClipPlane = 4,
            BackClipPlane = 90,
            ActiveStatus = 2,
            Status = ViewportStatusFlags.PerspectiveMode |
                ViewportStatusFlags.FrontClipping |
                ViewportStatusFlags.BackClipping,
        };
        viewport.FrozenLayers.Add(document.Layers[Layer.DefaultName]);
        layout.AddViewport(viewport);
        var session = new CadDocumentSession(document, sourceName: "drawing.dwg");

        CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
            session,
            ACadLayout.PaperLayoutName);

        Assert.Equal(0UL, snapshot.ContentGeneration);
        Assert.Equal(ACadLayout.PaperLayoutName, snapshot.LayoutName);
        Assert.Equal(snapshot.ContentGeneration, snapshot.ModelSpace.ContentGeneration);
        Assert.Equal(snapshot.ContentGeneration, snapshot.PaperSpace.ContentGeneration);
        Assert.Single(snapshot.ModelSpace.Lines.ToArray());
        Assert.Single(snapshot.PaperSpace.Lines.ToArray());
        Assert.Equal(2, snapshot.PaperSpace.Viewports.Length);
        CadViewportPrimitive captured = snapshot.PaperSpace.Viewports.Span[1];
        Assert.Equal(new CadPoint3D(100, 75, 0), captured.Center);
        Assert.Equal(120, captured.Width);
        Assert.Equal(80, captured.Height);
        Assert.Equal(10, captured.ViewCenterX);
        Assert.Equal(20, captured.ViewCenterY);
        Assert.Equal(new CadPoint3D(1, 2, 3), captured.ViewTarget);
        Assert.Equal(new CadPoint3D(0, 0, 1), captured.ViewDirection);
        Assert.True(captured.IsPerspective);
        Assert.True(captured.HasFrontClip);
        Assert.True(captured.HasBackClip);
        Assert.Equal(1, captured.FrozenLayerCount);
        Assert.Equal(Layer.DefaultName, snapshot.PaperSpace.ViewportFrozenLayers.Span[0].Name);
        Assert.True(snapshot.PaperSpace.Viewports.Span[0].RepresentsPaper);

        session.Edit("mutate both spaces", cad =>
        {
            cad.Entities.Clear();
            cad.Layouts[ACadLayout.PaperLayoutName].AssociatedBlock.Entities
                .OfType<Line>()
                .Single()
                .EndPoint = new XYZ(300, 400, 0);
        });

        Assert.Single(snapshot.ModelSpace.Lines.ToArray());
        Assert.Equal(new CadPoint3D(3, 4, 0), snapshot.PaperSpace.Lines.Span[0].End);
    }

    [Fact]
    public void LayoutSnapshotRejectsMissingAndModelLayouts()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        var compiler = new CadLayoutSnapshotCompiler();

        Assert.Throws<KeyNotFoundException>(() => compiler.Compile(session, "missing"));
        Assert.Throws<ArgumentException>(() =>
            compiler.Compile(session, ACadLayout.ModelLayoutName));
    }

    [Fact]
    public void LayoutSnapshotEnforcesViewportBudgets()
    {
        var document = new CadDocument();
        ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
        layout.AddViewport(new Viewport
        {
            Center = new XYZ(10, 10, 0),
            Width = 10,
            Height = 10,
            ViewHeight = 10,
        });
        var session = new CadDocumentSession(document);

        Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadLayoutSnapshotCompiler().Compile(
                session,
                ACadLayout.PaperLayoutName,
                new CadSnapshotOptions { MaxViewports = 1 }));
    }

    [Fact]
    public void LayoutSceneClipsTransformsAndReusesFrozenLayerVariants()
    {
        var document = new CadDocument();
        var frozen = new Layer("FROZEN_IN_VIEWPORT");
        document.Layers.Add(frozen);
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 0, 0)));
        document.Entities.Add(new Line(new XYZ(0, 1, 0), new XYZ(10, 1, 0))
        {
            Layer = frozen,
        });
        ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
        layout.AssociatedBlock.Entities.Add(new Line(
            new XYZ(0, 0, 0),
            new XYZ(5, 0, 0)));
        Viewport first = CreateTopViewport(
            center: new XYZ(100, 80, 0),
            viewCenter: new XY(4, 3),
            target: new XYZ(20, 30, 0),
            twist: Math.PI / 6.0);
        first.FrozenLayers.Add(frozen);
        layout.AddViewport(first);
        Viewport second = CreateTopViewport(
            center: new XYZ(220, 80, 0),
            viewCenter: new XY(4, 3),
            target: new XYZ(20, 30, 0),
            twist: Math.PI / 6.0);
        second.FrozenLayers.Add(frozen);
        layout.AddViewport(second);
        var session = new CadDocumentSession(document);
        session.Edit("publish layout generation", static _ => { });
        CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
            session,
            ACadLayout.PaperLayoutName);

        using CadRecordedLayoutScene scene = new CadLayoutSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();

        Assert.Equal(2, scene.Statistics.ActiveViewportCount);
        Assert.Equal(1, scene.Statistics.ModelSceneVariantCount);
        RenderCommand[] commands = picture.Commands;
        Assert.Equal(RenderCommandType.PushClip, commands[0].Type);
        Assert.Equal(RenderCommandType.DrawPicture, commands[1].Type);
        Assert.Equal(RenderCommandType.PopClip, commands[2].Type);
        Assert.Equal(RenderCommandType.PushClip, commands[3].Type);
        Assert.Equal(RenderCommandType.DrawPicture, commands[4].Type);
        Assert.Equal(RenderCommandType.PopClip, commands[5].Type);
        Assert.Same(commands[1].Picture, commands[4].Picture);
        Assert.Equal(1, commands[1].Picture!.CommandCount);

        CadViewportPrimitive viewport = snapshot.PaperSpace.Viewports.Span[1];
        Vector2 dcsCenter = Vector2.Transform(
            new Vector2((float)viewport.ViewCenterX, (float)viewport.ViewCenterY),
            Matrix3x2.CreateRotation((float)-viewport.TwistAngle));
        var localModelCenter = new Vector3(
            (float)(viewport.ViewTarget.X + dcsCenter.X - snapshot.ModelSpace.RebaseOrigin.X),
            (float)(viewport.ViewTarget.Y + dcsCenter.Y - snapshot.ModelSpace.RebaseOrigin.Y),
            0.0f);
        Vector3 mapped = Vector3.Transform(localModelCenter, commands[1].Transform);
        Assert.Equal(
            (float)(viewport.Center.X - snapshot.PaperSpace.RebaseOrigin.X),
            mapped.X,
            4);
        Assert.Equal(
            (float)(viewport.Center.Y - snapshot.PaperSpace.RebaseOrigin.Y),
            mapped.Y,
            4);
        Assert.Equal((float)viewport.Width, commands[0].Rect.Width, 4);
        Assert.Equal((float)viewport.Height, commands[0].Rect.Height, 4);
        Assert.Contains(commands[6..], command => command.Type == RenderCommandType.DrawLine);
        Assert.Equal(2, commands.Count(command => command.Type == RenderCommandType.DrawRect));
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }

    [Fact]
    public void LayoutSceneSkipsInactiveViewportContentButRetainsItsFrame()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 0, 0)));
        ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
        Viewport viewport = CreateTopViewport(
            new XYZ(100, 80, 0),
            XY.Zero,
            XYZ.Zero,
            0.0);
        viewport.ActiveStatus = -1;
        layout.AddViewport(viewport);
        CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            ACadLayout.PaperLayoutName);

        using CadRecordedLayoutScene scene = new CadLayoutSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();

        Assert.Equal(0, scene.Statistics.ActiveViewportCount);
        Assert.DoesNotContain(
            picture.Commands,
            command => command.Type == RenderCommandType.DrawPicture);
        Assert.Single(
            picture.Commands,
            command => command.Type == RenderCommandType.DrawRect);
    }

    [Fact]
    public void LayoutSceneFailsClosedForUnsupportedViewportProjection()
    {
        static NotSupportedException CompileUnsupported(Action<Viewport> configure)
        {
            var document = new CadDocument();
            ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
            Viewport viewport = CreateTopViewport(
                new XYZ(100, 80, 0),
                XY.Zero,
                XYZ.Zero,
                0.0);
            configure(viewport);
            layout.AddViewport(viewport);
            CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
                new CadDocumentSession(document),
                ACadLayout.PaperLayoutName);
            return Assert.Throws<NotSupportedException>(() =>
                new CadLayoutSceneCompiler().Compile(snapshot));
        }

        Assert.StartsWith(
            "CADVIEW002:",
            CompileUnsupported(viewport =>
                viewport.Status |= ViewportStatusFlags.PerspectiveMode).Message);
        Assert.StartsWith(
            "CADVIEW003:",
            CompileUnsupported(viewport =>
                viewport.Status |= ViewportStatusFlags.FrontClipping).Message);
        Assert.StartsWith(
            "CADVIEW004:",
            CompileUnsupported(viewport =>
                viewport.Status |= ViewportStatusFlags.NonRectangularClipping).Message);
        Assert.StartsWith(
            "CADVIEW006:",
            CompileUnsupported(viewport =>
                viewport.ViewDirection = XYZ.AxisX).Message);

        static NotSupportedException CompileUnsupportedModel(
            Action<CadDocument> configure)
        {
            var document = new CadDocument();
            configure(document);
            document.Layouts[ACadLayout.PaperLayoutName].AddViewport(
                CreateTopViewport(new XYZ(100, 80, 0), XY.Zero, XYZ.Zero, 0.0));
            CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
                new CadDocumentSession(document),
                ACadLayout.PaperLayoutName);
            return Assert.Throws<NotSupportedException>(() =>
                new CadLayoutSceneCompiler().Compile(snapshot));
        }

        Assert.StartsWith(
            "CADVIEW007:",
            CompileUnsupportedModel(document => document.Entities.Add(new XLine
            {
                FirstPoint = XYZ.Zero,
                Direction = XYZ.AxisX,
            })).Message);
        Assert.StartsWith(
            "CADVIEW008:",
            CompileUnsupportedModel(document =>
            {
                document.Header.PointDisplayMode = 2;
                document.Entities.Add(new Point(XYZ.Zero));
            }).Message);
    }

    [Fact]
    public void PaperLayoutPageSetupCompilesOneToOnePhysicalPrintPlan()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 0, 0)));
        ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
        ConfigurePaperLayout(layout);
        layout.AssociatedBlock.Entities.Add(new Line(
            new XYZ(5, 5, 0),
            new XYZ(20, 5, 0)));
        layout.AddViewport(CreateTopViewport(
            new XYZ(50, 25, 0),
            XY.Zero,
            XYZ.Zero,
            0.0));
        var session = new CadDocumentSession(document);
        CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
            session,
            ACadLayout.PaperLayoutName,
            new CadSnapshotOptions { DrawOrderPurpose = CadDrawOrderPurpose.Plotting });
        CadPageSetupSnapshot pageSetup = new CadPageSetupCatalogCompiler()
            .Compile(session)
            .FindLayout(ACadLayout.PaperLayoutName)!;

        CadPageSetupPrintOptionsResult lowered =
            new CadPageSetupPrintOptionsCompiler().Compile(
                pageSetup,
                new CadPageSetupPrintOptionsCompilerOptions { OutputDpi = 254 });

        Assert.True(lowered.IsSupported);
        Assert.Equal(
            CadPrintScaleMode.ModelUnitsPerMillimeter,
            lowered.PrintOptions!.ScaleMode);
        Assert.Equal(1.0, lowered.PrintOptions.ModelUnitsPerMillimeter);
        using CadPrintPlan plan = new CadLayoutPrintPlanCompiler().Compile(
            snapshot,
            pageSetup,
            new CadPageSetupPrintOptionsCompilerOptions { OutputDpi = 254 });
        Assert.Equal(new CadPrintPixelSize(1000, 500), plan.PageSizePixels);
        Assert.Equal(new CadPrintPixelRect(50, 50, 900, 400), plan.PrintableAreaPixels);
        Assert.Equal(10.0f, plan.PixelsPerModelUnit);
        Assert.Equal(1.0, plan.ModelUnitsPerMillimeter);
        Vector3 paperOrigin = Vector3.Transform(
            new Vector3(
                (float)-snapshot.PaperSpace.RebaseOrigin.X,
                (float)-snapshot.PaperSpace.RebaseOrigin.Y,
                0.0f),
            plan.ContentToPage);
        Assert.Equal(50.0f, paperOrigin.X, 4);
        Assert.Equal(450.0f, paperOrigin.Y, 4);
        using GpuPicture pagePicture = plan.CreatePagePicture();
        Assert.Equal(RenderCommandType.PushClip, pagePicture.GetCommand(0).Type);
        Assert.Equal(RenderCommandType.DrawPicture, pagePicture.GetCommand(1).Type);
        Assert.Equal(RenderCommandType.PopClip, pagePicture.GetCommand(2).Type);
    }

    [Fact]
    public void PaperLayoutPageSetupRejectsNonLayoutAndScaledOutput()
    {
        var document = new CadDocument();
        ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
        ConfigurePaperLayout(layout);
        var session = new CadDocumentSession(document);

        layout.PlotType = PlotType.DrawingExtents;
        CadPageSetupSnapshot wrongArea = new CadPageSetupCatalogCompiler()
            .Compile(session)
            .FindLayout(ACadLayout.PaperLayoutName)!;
        CadPageSetupPrintOptionsResult wrongAreaResult =
            new CadPageSetupPrintOptionsCompiler().Compile(wrongArea);
        Assert.Contains(
            wrongAreaResult.Diagnostics.ToArray(),
            diagnostic => diagnostic.Code == "CADPAGE119");

        layout.PlotType = PlotType.LayoutInformation;
        layout.ScaledFit = ScaledType._17;
        layout.StandardScale = 0.5;
        CadPageSetupSnapshot scaled = new CadPageSetupCatalogCompiler()
            .Compile(session)
            .FindLayout(ACadLayout.PaperLayoutName)!;
        CadPageSetupPrintOptionsResult scaledResult =
            new CadPageSetupPrintOptionsCompiler().Compile(scaled);
        Assert.Contains(
            scaledResult.Diagnostics.ToArray(),
            diagnostic => diagnostic.Code == "CADPAGE120");
    }

    private static Viewport CreateTopViewport(
        XYZ center,
        XY viewCenter,
        XYZ target,
        double twist) =>
        new()
        {
            Center = center,
            Width = 100,
            Height = 50,
            ViewCenter = viewCenter,
            ViewTarget = target,
            ViewDirection = XYZ.AxisZ,
            ViewHeight = 25,
            TwistAngle = twist,
            ActiveStatus = 1,
            RenderMode = RenderMode.Optimized2D,
            ShadePlotMode = ShadePlotMode.Wireframe,
        };

    private static void ConfigurePaperLayout(ACadLayout layout)
    {
        layout.Flags = PlotFlags.DrawViewportsFirst |
            PlotFlags.PrintLineweights |
            PlotFlags.UseStandardScale;
        layout.PaperWidth = 100;
        layout.PaperHeight = 50;
        layout.UnprintableMargin = new PaperMargin(5, 5, 5, 5);
        layout.PlotOriginX = 0;
        layout.PlotOriginY = 0;
        layout.PaperUnits = PlotPaperUnits.Millimeters;
        layout.PaperRotation = PlotRotation.NoRotation;
        layout.PlotType = PlotType.LayoutInformation;
        layout.ScaledFit = ScaledType._16;
        layout.StandardScale = 1;
        layout.ShadePlotMode = ShadePlotMode.Wireframe;
        layout.StyleSheet = string.Empty;
        layout.UpdatePaperViewport();
    }
}

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
    public void LayoutSceneUsesExactNonRectangularBoundaryAndIndependentFramePolicy()
    {
        CadLayoutSnapshot CreateSnapshot(bool borderLayerOn)
        {
            var document = new CadDocument();
            document.Entities.Add(new Line(
                new XYZ(0, 0, 0),
                new XYZ(100, 100, 0)));
            var borderLayer = new Layer("VIEWPORT_BORDER")
            {
                IsOn = borderLayerOn,
            };
            document.Layers.Add(borderLayer);
            ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
            var boundary = new LwPolyline
            {
                Flags = LwPolylineFlags.Closed,
                Layer = borderLayer,
            };
            boundary.Vertices.Add(new LwPolyline.Vertex(50, 40) { Bulge = 0.25 });
            boundary.Vertices.Add(new LwPolyline.Vertex(150, 40));
            boundary.Vertices.Add(new LwPolyline.Vertex(100, 120));
            layout.AssociatedBlock.Entities.Add(boundary);
            Viewport viewport = CreateTopViewport(
                new XYZ(100, 80, 0),
                XY.Zero,
                XYZ.Zero,
                0.0);
            viewport.Layer = borderLayer;
            viewport.Boundary = boundary;
            viewport.Status |= ViewportStatusFlags.NonRectangularClipping;
            layout.AddViewport(viewport);
            var session = new CadDocumentSession(document);
            session.Edit("publish non-rectangular viewport", static _ => { });
            return new CadLayoutSnapshotCompiler().Compile(
                session,
                ACadLayout.PaperLayoutName);
        }

        CadLayoutSnapshot visible = CreateSnapshot(borderLayerOn: true);
        using CadRecordedLayoutScene visibleScene =
            new CadLayoutSceneCompiler().Compile(visible);
        using GpuPicture visiblePicture = visibleScene.CreatePicture();
        RenderCommand[] visibleCommands = visiblePicture.Commands;
        Assert.Equal(RenderCommandType.PushGeometryClip, visibleCommands[0].Type);
        Assert.Equal(RenderCommandType.DrawPicture, visibleCommands[1].Type);
        Assert.Equal(RenderCommandType.PopGeometryClip, visibleCommands[2].Type);
        PathGeometry clipPath = Assert.IsType<PathGeometry>(visibleCommands[0].Path);
        PathFigure clipFigure = Assert.Single(clipPath.Figures);
        Assert.True(clipFigure.IsClosed);
        Assert.Contains(clipFigure.Segments, segment => segment is ArcSegment);
        Assert.Contains(
            visibleCommands[3..],
            command => command.Type == RenderCommandType.DrawPath);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            visiblePicture,
            96U,
            visibleScene.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);

        using CadRecordedLayoutScene frameSuppressed =
            new CadLayoutSceneCompiler().Compile(
                visible,
                new CadLayoutSceneOptions { IncludeViewportFrames = false });
        using GpuPicture frameSuppressedPicture = frameSuppressed.CreatePicture();
        Assert.Equal(
            [
                RenderCommandType.PushGeometryClip,
                RenderCommandType.DrawPicture,
                RenderCommandType.PopGeometryClip,
            ],
            frameSuppressedPicture.Commands.Select(command => command.Type).ToArray());

        CadLayoutSnapshot hidden = CreateSnapshot(borderLayerOn: false);
        CadEntityHeader hiddenBoundary = Assert.Single(
            hidden.PaperSpace.Entities.ToArray(),
            entity => entity.Kind == CadEntityKind.LightweightPolyline);
        Assert.False(hiddenBoundary.IsVisible);
        Assert.False(hidden.PaperSpace.Layers.Span[hiddenBoundary.LayerIndex].IsVisible);
        Assert.Equal(1, hidden.PaperSpace.SpatialIndex.EntityCount);
        using CadRecordedLayoutScene hiddenScene =
            new CadLayoutSceneCompiler().Compile(hidden);
        using GpuPicture hiddenPicture = hiddenScene.CreatePicture();
        Assert.Equal(
            [
                RenderCommandType.PushGeometryClip,
                RenderCommandType.DrawPicture,
                RenderCommandType.PopGeometryClip,
            ],
            hiddenPicture.Commands.Select(command => command.Type).ToArray());
    }

    [Fact]
    public void NonRectangularBoundaryFlowsThroughPhysicalPrintPicture()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 0, 0)));
        var borderLayer = new Layer("HIDDEN_VIEWPORT_BORDER")
        {
            IsOn = false,
        };
        document.Layers.Add(borderLayer);
        ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
        ConfigurePaperLayout(layout);
        Spline boundary = CreatePeriodicRationalSplineBoundary(50, 25);
        boundary.Layer = borderLayer;
        layout.AssociatedBlock.Entities.Add(boundary);
        Viewport viewport = CreateTopViewport(
            new XYZ(50, 25, 0),
            XY.Zero,
            XYZ.Zero,
            0.0);
        viewport.Layer = borderLayer;
        viewport.Boundary = boundary;
        viewport.Status |= ViewportStatusFlags.NonRectangularClipping;
        layout.AddViewport(viewport);
        var session = new CadDocumentSession(document);
        session.Edit("publish non-rectangular print generation", static _ => { });
        CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
            session,
            ACadLayout.PaperLayoutName,
            new CadSnapshotOptions { DrawOrderPurpose = CadDrawOrderPurpose.Plotting });
        CadPageSetupSnapshot pageSetup = new CadPageSetupCatalogCompiler()
            .Compile(session)
            .FindLayout(ACadLayout.PaperLayoutName)!;

        using CadPrintPlan plan = new CadLayoutPrintPlanCompiler().Compile(
            snapshot,
            pageSetup,
            new CadPageSetupPrintOptionsCompilerOptions { OutputDpi = 254 });
        using GpuPicture pagePicture = plan.CreatePagePicture();

        GpuPicture layoutPicture = Assert.IsType<GpuPicture>(
            pagePicture.GetCommand(1).Picture);
        Assert.Equal(
            [
                RenderCommandType.PushGeometryClip,
                RenderCommandType.DrawPicture,
                RenderCommandType.PopGeometryClip,
            ],
            layoutPicture.Commands.Select(command => command.Type).ToArray());
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            layoutPicture,
            96U,
            plan.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }

    [Fact]
    public void LayoutSceneUsesExactPeriodicRationalSplineViewportBoundary()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 10, 0)));
        ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
        Spline boundary = CreatePeriodicRationalSplineBoundary(100, 80);
        layout.AssociatedBlock.Entities.Add(boundary);
        Viewport viewport = CreateTopViewport(
            new XYZ(100, 80, 0),
            XY.Zero,
            XYZ.Zero,
            0.0);
        viewport.Boundary = boundary;
        viewport.Status |= ViewportStatusFlags.NonRectangularClipping;
        layout.AddViewport(viewport);
        var session = new CadDocumentSession(document);
        session.Edit("publish rational spline viewport generation", static _ => { });
        CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
            session,
            ACadLayout.PaperLayoutName);

        using CadRecordedLayoutScene scene = new CadLayoutSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();

        RenderCommand clip = picture.GetCommand(0);
        Assert.Equal(RenderCommandType.PushGeometryClip, clip.Type);
        PathFigure figure = Assert.Single(clip.Path!.Figures);
        Assert.True(figure.IsClosed);
        Assert.True(figure.IsFilled);
        Assert.Equal(4, figure.Segments.Count);
        Assert.All(
            figure.Segments,
            segment => Assert.IsType<RationalQuadraticBezierSegment>(segment));
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            98U,
            scene.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void LayoutSceneUsesExactClosedOrdinarySplineDegrees(
        int degree,
        bool rational)
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 10, 0)));
        ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
        Spline boundary = CreateClosedOrdinarySplineBoundary(degree);
        if (rational)
        {
            boundary.Weights.AddRange([1.0, 2.0, 3.0, 2.0, 1.0]);
        }
        layout.AssociatedBlock.Entities.Add(boundary);
        Viewport viewport = CreateTopViewport(
            new XYZ(100, 80, 0),
            XY.Zero,
            XYZ.Zero,
            0.0);
        viewport.Boundary = boundary;
        viewport.Status |= ViewportStatusFlags.NonRectangularClipping;
        layout.AddViewport(viewport);
        var session = new CadDocumentSession(document);
        session.Edit("publish ordinary spline viewport generation", static _ => { });
        CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
            session,
            ACadLayout.PaperLayoutName);

        using CadRecordedLayoutScene scene = new CadLayoutSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();

        PathFigure figure = Assert.Single(picture.GetCommand(0).Path!.Figures);
        Assert.True(figure.IsClosed);
        Assert.Equal(2, figure.Segments.Count);
        Assert.All(figure.Segments, segment => Assert.True(segment switch
        {
            LineSegment => degree == 1,
            QuadraticBezierSegment => degree == 2,
            CubicBezierSegment => degree == 3 && !rational,
            RationalCubicBezierSegment => degree == 3 && rational,
            _ => false,
        }));
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            checked((ulong)(100 + degree)),
            scene.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayoutSceneSupportsCircularAndEllipticViewportBoundaries(
        bool useEllipse)
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 0, 0)));
        ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
        Entity boundary = useEllipse
            ? new Ellipse
            {
                Center = new XYZ(100, 80, 0),
                MajorAxisEndPoint = new XYZ(50, 0, 0),
                RadiusRatio = 0.5,
                StartParameter = 0.0,
                EndParameter = Math.PI * 2.0,
            }
            : new Circle
            {
                Center = new XYZ(100, 80, 0),
                Radius = 40,
            };
        layout.AssociatedBlock.Entities.Add(boundary);
        Viewport viewport = CreateTopViewport(
            new XYZ(100, 80, 0),
            XY.Zero,
            XYZ.Zero,
            0.0);
        viewport.Boundary = boundary;
        viewport.Status |= ViewportStatusFlags.NonRectangularClipping;
        layout.AddViewport(viewport);
        CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            ACadLayout.PaperLayoutName);

        using CadRecordedLayoutScene scene = new CadLayoutSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();

        RenderCommand clip = picture.GetCommand(0);
        Assert.Equal(RenderCommandType.PushGeometryClip, clip.Type);
        Assert.Equal(2, Assert.Single(clip.Path!.Figures).Segments.Count);
        Assert.NotEqual(0.0f, clip.Transform.M11);
        Assert.NotEqual(0.0f, clip.Transform.M22);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf, false)]
    [InlineData(CadDocumentFormat.Dwg, false)]
    [InlineData(CadDocumentFormat.Dxf, true)]
    [InlineData(CadDocumentFormat.Dwg, true)]
    public async Task NonRectangularViewportBoundarySurvivesAdvertisedRoundTrips(
        CadDocumentFormat format,
        bool useSpline)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 10, 0)));
        ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
        Entity boundary;
        if (useSpline)
        {
            boundary = CreatePeriodicRationalSplineBoundary(100, 80);
        }
        else
        {
            var polyline = new LwPolyline
            {
                Flags = LwPolylineFlags.Closed,
            };
            polyline.Vertices.Add(new LwPolyline.Vertex(50, 40));
            polyline.Vertices.Add(new LwPolyline.Vertex(150, 40));
            polyline.Vertices.Add(new LwPolyline.Vertex(100, 120));
            boundary = polyline;
        }
        layout.AssociatedBlock.Entities.Add(boundary);
        Viewport viewport = CreateTopViewport(
            new XYZ(100, 80, 0),
            XY.Zero,
            XYZ.Zero,
            0.0);
        viewport.Boundary = boundary;
        viewport.Status |= ViewportStatusFlags.NonRectangularClipping;
        layout.AddViewport(viewport);
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
            sourceName: $"nonrect-viewport.{format.ToString().ToLowerInvariant()}");
        CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
            loaded.Session,
            ACadLayout.PaperLayoutName);

        CadViewportPrimitive restored = snapshot.PaperSpace.Viewports.Span[1];
        Assert.NotEqual(0UL, restored.BoundaryHandle);
        using CadRecordedLayoutScene scene = new CadLayoutSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();
        Assert.Equal(RenderCommandType.PushGeometryClip, picture.GetCommand(0).Type);
        Assert.Equal(
            useSpline,
            picture.GetCommand(0).Path!.Figures[0].Segments[0] is
                RationalQuadraticBezierSegment);
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

        static NotSupportedException CompileUnsupportedBoundary(
            Entity boundary,
            bool freezeLayer = false)
        {
            var document = new CadDocument();
            if (freezeLayer)
            {
                var frozen = new Layer("FROZEN_BOUNDARY")
                {
                    Flags = LayerFlags.Frozen,
                };
                document.Layers.Add(frozen);
                boundary.Layer = frozen;
            }
            ACadLayout layout = document.Layouts[ACadLayout.PaperLayoutName];
            layout.AssociatedBlock.Entities.Add(boundary);
            Viewport viewport = CreateTopViewport(
                new XYZ(100, 80, 0),
                XY.Zero,
                XYZ.Zero,
                0.0);
            viewport.Boundary = boundary;
            viewport.Status |= ViewportStatusFlags.NonRectangularClipping;
            layout.AddViewport(viewport);
            CadLayoutSnapshot snapshot = new CadLayoutSnapshotCompiler().Compile(
                new CadDocumentSession(document),
                ACadLayout.PaperLayoutName);
            return Assert.Throws<NotSupportedException>(() =>
                new CadLayoutSceneCompiler().Compile(snapshot));
        }

        Assert.StartsWith(
            "CADVIEW009:",
            CompileUnsupportedBoundary(new Line(
                XYZ.Zero,
                new XYZ(10, 10, 0))).Message);
        var openBoundary = new LwPolyline();
        openBoundary.Vertices.Add(new LwPolyline.Vertex(0, 0));
        openBoundary.Vertices.Add(new LwPolyline.Vertex(10, 0));
        openBoundary.Vertices.Add(new LwPolyline.Vertex(0, 10));
        Assert.StartsWith(
            "CADVIEW011:",
            CompileUnsupportedBoundary(openBoundary).Message);
        Assert.StartsWith(
            "CADVIEW010:",
            CompileUnsupportedBoundary(
                new Circle { Center = XYZ.Zero, Radius = 10 },
                freezeLayer: true).Message);
        var openSpline = new Spline { Degree = 2 };
        openSpline.ControlPoints.AddRange([
            new XYZ(0, 0, 0),
            new XYZ(5, 10, 0),
            new XYZ(10, 0, 0),
        ]);
        openSpline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
        Assert.StartsWith(
            "CADVIEW011:",
            CompileUnsupportedBoundary(openSpline).Message);
        var quarticSpline = new Spline
        {
            Degree = 4,
            IsClosed = true,
        };
        quarticSpline.ControlPoints.AddRange([
            new XYZ(0, 0, 0),
            new XYZ(5, 10, 0),
            new XYZ(10, 12, 0),
            new XYZ(15, 10, 0),
            new XYZ(20, 0, 0),
        ]);
        quarticSpline.Knots.AddRange([0, 0, 0, 0, 0, 1, 1, 1, 1, 1]);
        Assert.StartsWith(
            "CADVIEW012:",
            CompileUnsupportedBoundary(quarticSpline).Message);
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

    private static Spline CreatePeriodicRationalSplineBoundary(
        double centerX,
        double centerY)
    {
        var spline = new Spline
        {
            Degree = 2,
            IsClosed = true,
            IsPeriodic = true,
        };
        spline.ControlPoints.AddRange([
            new XYZ(centerX - 30, centerY, 0),
            new XYZ(centerX, centerY + 20, 0),
            new XYZ(centerX + 30, centerY, 0),
            new XYZ(centerX, centerY - 20, 0),
        ]);
        spline.Knots.AddRange([0, 1, 2, 3, 4]);
        spline.Weights.AddRange([1, 2, 1, 2]);
        return spline;
    }

    private static Spline CreateClosedOrdinarySplineBoundary(int degree)
    {
        var spline = new Spline
        {
            Degree = degree,
            IsClosed = true,
        };
        spline.ControlPoints.AddRange(degree switch
        {
            1 =>
            [
                new XYZ(70, 60, 0),
                new XYZ(130, 60, 0),
                new XYZ(100, 110, 0),
            ],
            2 =>
            [
                new XYZ(70, 60, 0),
                new XYZ(100, 110, 0),
                new XYZ(130, 60, 0),
                new XYZ(100, 45, 0),
            ],
            3 =>
            [
                new XYZ(70, 60, 0),
                new XYZ(70, 105, 0),
                new XYZ(130, 105, 0),
                new XYZ(130, 60, 0),
                new XYZ(100, 45, 0),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(degree)),
        });
        spline.Knots.AddRange(Enumerable.Repeat(0.0, degree + 1));
        spline.Knots.Add(1.0);
        spline.Knots.AddRange(Enumerable.Repeat(2.0, degree + 1));
        return spline;
    }

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

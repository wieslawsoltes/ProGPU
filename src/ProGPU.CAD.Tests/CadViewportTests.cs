using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
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
}

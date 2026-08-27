using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPrintPlanTests
{
    [Fact]
    public void A4FitPlanFiltersNonPlottableLayersAndRetainsPhysicalLineweight()
    {
        var document = new CadDocument();
        var plottedLayer = new Layer("PLOTTED")
        {
            PlotFlag = true,
            LineWeight = LineWeightType.W25,
        };
        var screenLayer = new Layer("SCREEN_ONLY") { PlotFlag = false };
        document.Layers.Add(plottedLayer);
        document.Layers.Add(screenLayer);
        document.Entities.Add(new Line(
            new XYZ(0, 0, 0),
            new XYZ(100, 50, 0))
        {
            Layer = plottedLayer,
            LineWeight = LineWeightType.ByLayer,
        });
        document.Entities.Add(new Line(
            new XYZ(10_000, 10_000, 0),
            new XYZ(20_000, 20_000, 0))
        {
            Layer = screenLayer,
        });
        var session = new CadDocumentSession(document);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        Assert.Equal(
            2,
            new CadPlanSceneCompiler().Compile(snapshot).Statistics.RecordedEntityCount);
        var plan = new CadPrintPlanCompiler().Compile(snapshot);
        GpuPicture pagePicture;
        try
        {
            Assert.Equal(session.ContentGeneration, plan.ContentGeneration);
            Assert.Equal(new CadPrintPixelSize(2480, 3508), plan.PageSizePixels);
            Assert.Equal(
                new CadPrintPixelRect(118, 118, 2244, 3272),
                plan.PrintableAreaPixels);
            Assert.Equal(
                new CadBounds3D(
                    new CadPoint3D(0, 0, 0),
                    new CadPoint3D(100, 50, 0)),
                plan.PlotBounds);
            Assert.Equal(22.44f, plan.PixelsPerModelUnit, 4);
            Assert.Equal(1, plan.SceneStatistics.RecordedEntityCount);
            Assert.Equal(CadPrintScaleMode.FitToPrintableArea, plan.ScaleMode);
            Assert.Equal(CadPrintPlacementMode.Centered, plan.PlacementMode);

            Vector2 pageCenter = TransformToPage(
                snapshot,
                plan.ContentToPage,
                plan.PlotBounds.Center);
            Assert.Equal(1240.0f, pageCenter.X, 3);
            Assert.Equal(1754.0f, pageCenter.Y, 3);

            pagePicture = plan.CreatePagePicture();
            Assert.Equal(3, pagePicture.CommandCount);
            Assert.Equal(RenderCommandType.PushClip, pagePicture.GetCommand(0).Type);
            Assert.Equal(
                new Rect(118, 118, 2244, 3272),
                pagePicture.GetCommand(0).Rect);
            RenderCommand replay = pagePicture.GetCommand(1);
            Assert.Equal(RenderCommandType.DrawPicture, replay.Type);
            Assert.True(replay.UseGpuTransforms);
            Assert.Equal(plan.ContentToPage, replay.CameraView);
            Assert.Equal(RenderCommandType.PopClip, pagePicture.GetCommand(2).Type);
            GpuPicture contentPicture = replay.Picture!;
            Assert.Equal(1, contentPicture.CommandCount);
            Pen pen = contentPicture.GetCommand(0).Pen!;
            Assert.Equal(0.25f * 300.0f / 25.4f, pen.Thickness, 5);
            Assert.Equal(PenStrokeTransformMode.Fixed, pen.StrokeTransformMode);
        }
        finally
        {
            plan.Dispose();
        }

        using (pagePicture)
        using (GpuPicture ownershipProbe = pagePicture.Clone())
        {
            Assert.Equal(pagePicture.CommandCount, ownershipProbe.CommandCount);
            Assert.Equal(
                1,
                pagePicture.GetCommand(1).Picture!.CommandCount);
        }
        Assert.True(plan.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => plan.CreatePagePicture());
    }

    [Fact]
    public void CustomScaleAndPrintableOffsetUsePhysicalPaperCoordinates()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 10, 0)));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var options = new CadPrintPlanOptions
        {
            PaperWidthMillimeters = 100,
            PaperHeightMillimeters = 50,
            MarginLeftMillimeters = 5,
            MarginTopMillimeters = 5,
            MarginRightMillimeters = 5,
            MarginBottomMillimeters = 5,
            OutputDpi = 254,
            ScaleMode = CadPrintScaleMode.ModelUnitsPerMillimeter,
            ModelUnitsPerMillimeter = 2,
            PlacementMode = CadPrintPlacementMode.PrintableAreaOffset,
            PlotOffsetXMillimeters = 3,
            PlotOffsetYMillimeters = 4,
        };

        using CadPrintPlan plan = new CadPrintPlanCompiler().Compile(snapshot, options);

        Assert.Equal(new CadPrintPixelSize(1000, 500), plan.PageSizePixels);
        Assert.Equal(new CadPrintPixelRect(50, 50, 900, 400), plan.PrintableAreaPixels);
        Assert.Equal(5.0f, plan.PixelsPerModelUnit);
        Assert.Equal(2.0, plan.ModelUnitsPerMillimeter, 12);
        Assert.Equal(CadPrintPlacementMode.PrintableAreaOffset, plan.PlacementMode);
        AssertVector(
            new Vector2(80, 410),
            TransformToPage(snapshot, plan.ContentToPage, CadPoint3D.Zero));
        AssertVector(
            new Vector2(130, 360),
            TransformToPage(snapshot, plan.ContentToPage, new CadPoint3D(10, 10, 0)));
    }

    [Fact]
    public void EmptyPlottableOutputAndCancelledCompilationFailExplicitly()
    {
        var document = new CadDocument();
        var screenLayer = new Layer("SCREEN_ONLY") { PlotFlag = false };
        document.Layers.Add(screenLayer);
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX) { Layer = screenLayer });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var compiler = new CadPrintPlanCompiler();

        Assert.Throws<InvalidOperationException>(() => compiler.Compile(snapshot));

        var plottedDocument = new CadDocument();
        plottedDocument.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        CadDocumentSnapshot plottedSnapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(plottedDocument));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            compiler.Compile(plottedSnapshot, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void ExplicitWindowCanProduceAnIntentionallyBlankPage()
    {
        var document = new CadDocument();
        var screenLayer = new Layer("SCREEN_ONLY") { PlotFlag = false };
        document.Layers.Add(screenLayer);
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX) { Layer = screenLayer });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var window = new CadBounds3D(
            new CadPoint3D(-10, -20, 0),
            new CadPoint3D(30, 40, 0));

        using CadPrintPlan plan = new CadPrintPlanCompiler().Compile(
            snapshot,
            new CadPrintPlanOptions { PlotBounds = window });

        Assert.Equal(window, plan.PlotBounds);
        Assert.Equal(0, plan.SceneStatistics.RecordedEntityCount);
        using GpuPicture page = plan.CreatePagePicture();
        Assert.Equal(0, page.GetCommand(1).Picture!.CommandCount);
    }

    [Fact]
    public void InvalidPageScalePlacementAndResourceBudgetsAreRejected()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var compiler = new CadPrintPlanCompiler();

        Assert.Throws<ArgumentOutOfRangeException>(() => compiler.Compile(
            snapshot,
            new CadPrintPlanOptions { PaperWidthMillimeters = double.NaN }));
        Assert.Throws<ArgumentException>(() => compiler.Compile(
            snapshot,
            new CadPrintPlanOptions
            {
                PaperWidthMillimeters = 20,
                MarginLeftMillimeters = 10,
                MarginRightMillimeters = 10,
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => compiler.Compile(
            snapshot,
            new CadPrintPlanOptions { MaxPagePixelCount = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => compiler.Compile(
            snapshot,
            new CadPrintPlanOptions
            {
                ScaleMode = CadPrintScaleMode.ModelUnitsPerMillimeter,
                ModelUnitsPerMillimeter = 0,
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => compiler.Compile(
            snapshot,
            new CadPrintPlanOptions
            {
                PlacementMode = CadPrintPlacementMode.PrintableAreaOffset,
                PlotOffsetXMillimeters = double.PositiveInfinity,
            }));
        Assert.Throws<ArgumentException>(() => compiler.Compile(
            snapshot,
            new CadPrintPlanOptions { PlotBounds = CadBounds3D.Empty }));
    }

    private static Vector2 TransformToPage(
        CadDocumentSnapshot snapshot,
        Matrix4x4 transform,
        CadPoint3D world)
    {
        var local = new Vector3(
            (float)(world.X - snapshot.RebaseOrigin.X),
            (float)(world.Y - snapshot.RebaseOrigin.Y),
            0);
        Vector3 result = Vector3.Transform(local, transform);
        return new Vector2(result.X, result.Y);
    }

    private static void AssertVector(Vector2 expected, Vector2 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
    }
}

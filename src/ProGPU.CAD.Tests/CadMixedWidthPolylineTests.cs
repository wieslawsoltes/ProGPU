using ACadSharp;
using ACadSharp.Entities;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMixedWidthPolylineTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MixedWidthsRetainThinRunsAndWideBodiesAcrossOutputs(bool legacy, bool fill)
    {
        CadDocument document = CreateDocument(legacy, fill);
        CadDocumentSnapshot snapshot = Compile(document);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.True(Assert.Single(snapshot.Polylines.ToArray()).HasVariableWidth);
        using var scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();
        Assert.Equal(fill ? 2 : 1, commands.Length);
        RenderCommand stroke = commands[^1];
        Assert.NotNull(stroke.Pen);
        Assert.Null(stroke.Brush);
        var thinRun = Assert.Single(stroke.Path!.Figures, figure => !figure.IsClosed);
        Assert.False(thinRun.IsFilled);
        Assert.Equal(2, thinRun.Segments.Count);
        if (fill)
        {
            Assert.NotNull(commands[0].Brush);
            Assert.Null(commands[0].Pen);
        }

        var candidate = new CadSelectionCandidate(snapshot.ContentGeneration, 0,
            entity.Handle, entity.Kind, entity.Bounds);
        Assert.Equal(CadPointHitStatus.Hit, CadSelectionHitTester.HitTestPoint(
            snapshot, candidate, new CadPoint3D(10, 5, 0), 0).Status);
        Assert.Equal(CadPointHitStatus.Miss, CadSelectionHitTester.HitTestPoint(
            snapshot, candidate, new CadPoint3D(10.5, 5, 0), 0).Status);
        Assert.Equal(CadPointHitStatus.Hit, CadSelectionHitTester.HitTestPoint(
            snapshot, candidate, new CadPoint3D(5, 0.5, 0), 0).Status);
        Assert.Equal(CadBoundsHitStatus.Hit, CadSelectionHitTester.HitTestBounds(
            snapshot, candidate, new CadBounds3D(new CadPoint3D(9.9, 4.9, -0.1),
                new CadPoint3D(10.1, 5.1, 0.1)), CadBoundsSelectionMode.Crossing).Status);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(picture, 97U, 1U,
            out NativeCompiledPicture? native, out NativePictureCompileFailure failure), failure.ToString());
        Assert.Equal(commands.Length, native!.SourceCommandCount);
        Assert.True(native.NativeDrawCount >= commands.Length);
        using var print = new CadPrintPlanCompiler().Compile(snapshot);
        using GpuPicture page = print.CreatePagePicture();
        Assert.Equal(commands.Length, page.GetCommand(1).Picture!.CommandCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedThinRunCrossesAuthoredStartWithoutExtraCaps(bool fill)
    {
        var document = new CadDocument();
        document.Header.FillMode = fill;
        var polyline = new LwPolyline { IsClosed = true };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0));
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0) { StartWidth = 2, EndWidth = 4 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 10));
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 10));
        document.Entities.Add(polyline);
        using var scene = new CadPlanSceneCompiler().Compile(Compile(document));
        RenderCommand stroke = scene.DrawingContext.Commands.ToArray()[^1];
        var run = Assert.Single(stroke.Path!.Figures, figure => !figure.IsClosed);
        Assert.Equal(3, run.Segments.Count);
    }

    [Theory]
    [InlineData(false, CadDocumentFormat.Dxf)]
    [InlineData(false, CadDocumentFormat.Dwg)]
    [InlineData(true, CadDocumentFormat.Dxf)]
    [InlineData(true, CadDocumentFormat.Dwg)]
    public async Task RoundTripPreservesMixedWidthGeometry(bool legacy, CadDocumentFormat format)
    {
        CadDocument document = CreateDocument(legacy, true);
        CadDocumentSnapshot before = Compile(document);
        using var output = new MemoryStream();
        var store = new CadDocumentStore();
        await store.SaveAsync(new CadDocumentSession(document), output, format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        output.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(output, format);
        CadDocumentSnapshot after = new CadSnapshotCompiler().Compile(loaded.Session);
        Assert.Single(after.Entities.ToArray());
        Assert.Equal(before.PolylineVertices.ToArray(), after.PolylineVertices.ToArray());
        using var scene = new CadPlanSceneCompiler().Compile(after);
        Assert.Equal(2, scene.DrawingContext.Commands.Count);
    }

    private static CadDocumentSnapshot Compile(CadDocument document) =>
        new CadSnapshotCompiler().Compile(new CadDocumentSession(document));

    private static CadDocument CreateDocument(bool legacy, bool fill)
    {
        var document = new CadDocument();
        document.Header.Version = ACadVersion.AC1032;
        document.Header.FillMode = fill;
        var points = new (double X, double Y, double Start, double End)[]
        {
            (0, 0, 2, 4), (10, 0, 0, 0), (10, 10, 0, 0), (20, 10, 4, 2), (30, 10, 0, 0),
        };
        if (legacy)
        {
            var polyline = new Polyline2D();
            foreach (var point in points)
                polyline.Vertices.Add(new Vertex2D
                {
                    Location = new CSMath.XYZ(point.X, point.Y, 0),
                    StartWidth = point.Start, EndWidth = point.End,
                });
            document.Entities.Add(polyline);
        }
        else
        {
            var polyline = new LwPolyline();
            foreach (var point in points)
                polyline.Vertices.Add(new LwPolyline.Vertex(point.X, point.Y)
                {
                    StartWidth = point.Start, EndWidth = point.End,
                });
            document.Entities.Add(polyline);
        }
        return document;
    }
}

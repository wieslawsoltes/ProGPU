using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadModelerGeometryTests
{
    [Fact]
    public void PinnedAcadSharpDwgFixtureRetainsPayloadOnlyModelerEntities()
    {
        string repositoryRoot = FindRepositoryRoot();
        string path = Path.Combine(
            repositoryRoot,
            "external",
            "ACadSharp",
            "samples",
            "sample_AC1021.dwg");
        CadDocument document = DwgReader.Read(path);
        ModelerGeometry[] source = document.Entities
            .OfType<ModelerGeometry>()
            .ToArray();

        Assert.NotEmpty(source);
        Assert.Contains(source, geometry => geometry.AcisData is { Length: > 0 });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document, CadDocumentFormat.Dwg, path));

        Assert.Equal(source.Length, snapshot.ModelerGeometries.Length);
        Assert.NotEmpty(snapshot.ModelerGeometryPayloadBytes.ToArray());
        Assert.All(
            snapshot.Entities.ToArray()
                .Where(entity => entity.Kind == CadEntityKind.ModelerGeometry),
            entity => Assert.True(entity.Bounds.IsEmpty));
    }

    [Fact]
    public void SnapshotRetainsPayloadAndBatchesDisplayWiresForManagedAndNativeReplay()
    {
        byte[] payload = "ACIS BinaryFile-test"u8.ToArray();
        Solid3D solid = CreateSolid(payload);
        var document = new CadDocument();
        document.Entities.Add(solid);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        payload[^1] = (byte)'X';

        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());
        Assert.Equal(CadEntityKind.ModelerGeometry, header.Kind);
        Assert.Equal(
            new CadBounds3D(
                new CadPoint3D(10, 20, 30),
                new CadPoint3D(14, 24, 32)),
            header.Bounds);
        CadModelerGeometryPrimitive primitive = Assert.Single(
            snapshot.ModelerGeometries.ToArray());
        Assert.Equal(CadModelerGeometryKind.Solid3D, primitive.Kind);
        Assert.Equal(1, primitive.WireCount);
        Assert.True(primitive.IsBinaryPayload);
        Assert.Equal(
            "ACIS BinaryFile-test"u8.ToArray(),
            snapshot.ModelerGeometryPayloadBytes.Span
                .Slice(primitive.PayloadOffset, primitive.PayloadCount)
                .ToArray());
        CadModelerGeometryWire wire = Assert.Single(
            snapshot.ModelerGeometryWires.ToArray());
        Assert.Equal(3, wire.PointCount);
        Assert.Equal(17, wire.SelectionMarker);
        Assert.Equal(23, wire.AcisIndex);
        Assert.Equal(5, wire.Type);

        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        Assert.Equal(1, scene.Statistics.ModelerGeometryWireframeCount);
        Assert.Equal(1, scene.Statistics.DeferredModelerSurfaceCount);
        Assert.Equal("CADSCENE007", Assert.Single(scene.Diagnostics.ToArray()).Code);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawExtension, command.Type);
        Assert.Equal(CompositorBuiltInExtensions.AcisSolid, command.ExtensionId);
        Assert.Equal(2, command.Line3DBufferCount);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            701U,
            1U,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(1, native.SourceCommandCount);
        Assert.Equal(2, native.Line3DCount);

        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        Assert.Equal(1, print.SceneStatistics.ModelerGeometryWireframeCount);
        Assert.Equal(1, print.SceneStatistics.DeferredModelerSurfaceCount);
        Assert.Equal(1, print.SceneStatistics.RecordedCommandCount);
    }

    [Fact]
    public void MalformedDisplayWireRollsBackAllParallelStreamsBeforeContinuing()
    {
        Solid3D malformed = CreateSolid("ACIS BinaryFile-malformed"u8.ToArray());
        malformed.Wires[0].Points.Insert(1, new XYZ(double.NaN, 0, 0));
        Solid3D valid = CreateSolid("ACIS BinaryFile-valid"u8.ToArray());
        var document = new CadDocument();
        document.Entities.Add(malformed);
        document.Entities.Add(valid);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadModelerGeometryPrimitive primitive = Assert.Single(
            snapshot.ModelerGeometries.ToArray());
        CadModelerGeometryWire wire = Assert.Single(
            snapshot.ModelerGeometryWires.ToArray());
        Assert.Equal(3, wire.PointCount);
        Assert.Equal(3, snapshot.ModelerGeometryPoints.Length);
        Assert.Equal(
            "ACIS BinaryFile-valid"u8.ToArray(),
            snapshot.ModelerGeometryPayloadBytes.Span
                .Slice(primitive.PayloadOffset, primitive.PayloadCount)
                .ToArray());
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
    }

    [Fact]
    public void PayloadOnlyModelerGeometryIsRetainedWithoutInventedBoundsOrGeometry()
    {
        var region = new Region
        {
            AcisData = "400 0 1 0\nbody $-1 $-1 $-1 $-1 #\n"u8.ToArray(),
            ModelerFormatVersion = 1,
        };
        var document = new CadDocument();
        document.Entities.Add(region);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());
        Assert.True(header.Bounds.IsEmpty);
        Assert.True(snapshot.Bounds.IsEmpty);
        Assert.Equal(0, snapshot.SpatialIndex.EntityCount);
        CadModelerGeometryPrimitive primitive = Assert.Single(
            snapshot.ModelerGeometries.ToArray());
        Assert.Equal(CadModelerGeometryKind.Region, primitive.Kind);
        Assert.False(primitive.IsBinaryPayload);
        Assert.Equal(0, primitive.WireCount);
        Assert.Empty(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(0, scene.Statistics.ModelerGeometryWireframeCount);
        Assert.Equal(1, scene.Statistics.DeferredModelerSurfaceCount);
        Assert.Equal("CADSCENE008", Assert.Single(scene.Diagnostics.ToArray()).Code);
    }

    [Fact]
    public void DisplayWireSelectionUsesExactSegmentsAndWholeWireWindowRules()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateSolid("ACIS BinaryFile"u8.ToArray()));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            header.Handle,
            header.Kind,
            header.Bounds);

        CadPointHitResult point = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(12, 20.25, 30),
            0.3);
        CadBoundsHitResult crossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(11, 19, 29),
                new CadPoint3D(13, 21, 31)),
            CadBoundsSelectionMode.Crossing);
        CadBoundsHitResult partialWindow = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(9, 19, 29),
                new CadPoint3D(13, 25, 33)),
            CadBoundsSelectionMode.Window);
        CadBoundsHitResult fullWindow = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(9, 19, 29),
                new CadPoint3D(15, 25, 33)),
            CadBoundsSelectionMode.Window);

        Assert.True(point.IsHit);
        Assert.True(crossing.IsHit);
        Assert.Equal(CadBoundsHitStatus.Miss, partialWindow.Status);
        Assert.True(fullWindow.IsHit);
    }

    [Theory]
    [InlineData("translate")]
    [InlineData("rotate")]
    [InlineData("scale")]
    public void ModelerTransformsAreRejectedBeforePayloadOrGenerationCanDiverge(
        string operation)
    {
        byte[] payload = "ACIS BinaryFile-original"u8.ToArray();
        Solid3D solid = CreateSolid(payload);
        var document = new CadDocument();
        document.Entities.Add(solid);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        CadEditCommand command = operation switch
        {
            "translate" => new CadTranslateEntitiesCommand(
                [solid.Handle],
                new CadPoint3D(1, 2, 3)),
            "rotate" => new CadRotateEntitiesCommand(
                [solid.Handle],
                new CadPoint3D(0, 0, 1),
                Math.PI / 2),
            "scale" => new CadScaleEntitiesCommand([solid.Handle], 2),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => history.Execute(command));

        Assert.Contains("synchronized ACIS payload editing", error.Message);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal("ACIS BinaryFile-original"u8.ToArray(), solid.AcisData);
        Assert.Equal(new XYZ(10, 20, 30), solid.Wires[0].Points[0]);
    }

    private static Solid3D CreateSolid(byte[] payload)
    {
        var solid = new Solid3D
        {
            AcisData = payload,
            ModelerFormatVersion = 21800,
        };
        var wire = new ModelerGeometry.Wire
        {
            SelectionMarker = 17,
            AcisIndex = 23,
            Type = 5,
        };
        wire.Points.Add(new XYZ(10, 20, 30));
        wire.Points.Add(new XYZ(14, 20, 30));
        wire.Points.Add(new XYZ(14, 24, 32));
        solid.Wires.Add(wire);
        return solid;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "ProGPU.CAD")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("The ProGPU repository root was not found.");
    }
}

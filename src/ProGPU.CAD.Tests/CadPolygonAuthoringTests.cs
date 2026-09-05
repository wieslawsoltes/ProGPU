using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPolygonAuthoringTests
{
    private const double Tolerance = 1e-9;

    [Theory]
    [InlineData(CadPolygonAuthoringMode.Inscribed)]
    [InlineData(CadPolygonAuthoringMode.Circumscribed)]
    [InlineData(CadPolygonAuthoringMode.Edge)]
    public void EveryModeCreatesExactCounterclockwiseSquare(
        CadPolygonAuthoringMode mode)
    {
        var authoring = new CadPolygonAuthoringSession(4, mode);
        CadPolygonAuthoringSnapshot snapshot;
        if (mode == CadPolygonAuthoringMode.Inscribed)
        {
            AcceptFirst(authoring, new CadPoint3D(0, 0, 3));
            snapshot = CompletePoint(authoring, new CadPoint3D(-1, -1, 3));
        }
        else if (mode == CadPolygonAuthoringMode.Circumscribed)
        {
            AcceptFirst(authoring, new CadPoint3D(0, 0, 3));
            snapshot = CompletePoint(authoring, new CadPoint3D(0, -1, 3));
        }
        else
        {
            AcceptFirst(authoring, new CadPoint3D(-1, -1, 3));
            snapshot = CompletePoint(authoring, new CadPoint3D(1, -1, 3));
        }

        AssertPoint(new CadPoint3D(0, 0, 3), snapshot.Center);
        AssertPoint(new CadPoint3D(-1, -1, 3), snapshot.VertexAt(0));
        AssertPoint(new CadPoint3D(1, -1, 3), snapshot.VertexAt(1));
        AssertPoint(new CadPoint3D(1, 1, 3), snapshot.VertexAt(2));
        AssertPoint(new CadPoint3D(-1, 1, 3), snapshot.VertexAt(3));
        AssertClose(2.0, snapshot.EdgeLength);
        AssertClose(1.0, snapshot.Apothem);
    }

    [Theory]
    [InlineData(CadPolygonAuthoringMode.Inscribed, 2.0, 1.4142135623730951)]
    [InlineData(CadPolygonAuthoringMode.Circumscribed, 2.0, 2.0)]
    public void NumericRadiusAlignsBottomEdgeToSnapRotation(
        CadPolygonAuthoringMode mode,
        double radius,
        double expectedBottomY)
    {
        var authoring = new CadPolygonAuthoringSession(4, mode);
        AcceptFirst(authoring, new CadPoint3D(5, 7, 2));

        Assert.True(authoring.TryCreateFromRadius(
            radius,
            new CadPoint3D(0, -1, 0),
            out CadPolygonAuthoringSnapshot snapshot,
            out string? error),
            error);

        AssertClose(7.0 - expectedBottomY, snapshot.VertexAt(0).Y);
        AssertClose(7.0 - expectedBottomY, snapshot.VertexAt(1).Y);
        Assert.True(snapshot.VertexAt(0).X < snapshot.VertexAt(1).X);
    }

    [Fact]
    public void NumericRadiusUsesArbitrarySnapBasisWithoutAddingUnitPoint()
    {
        var authoring = new CadPolygonAuthoringSession(
            4,
            CadPolygonAuthoringMode.Circumscribed);
        AcceptFirst(authoring, new CadPoint3D(
            10_000_000_000_000_000,
            -10_000_000_000_000_000,
            8));

        Assert.True(authoring.TryCreateFromRadius(
            4.0,
            new CadPoint3D(1, 0, 0),
            out CadPolygonAuthoringSnapshot snapshot,
            out string? error),
            error);

        Assert.Equal(
            new CadPoint3D(
                10_000_000_000_000_000,
                -10_000_000_000_000_000,
                8),
            snapshot.Center);
        AssertClose(4.0, snapshot.Apothem);
    }

    [Fact]
    public void EdgeMidpointRemainsStableAtLargeWcsOrigin()
    {
        var authoring = new CadPolygonAuthoringSession(
            4,
            CadPolygonAuthoringMode.Edge);
        AcceptFirst(authoring, new CadPoint3D(
            10_000_000_000_000_000,
            -10_000_000_000_000_000,
            6));

        CadPolygonAuthoringSnapshot snapshot = CompletePoint(
            authoring,
            new CadPoint3D(
                10_000_000_000_000_008,
                -10_000_000_000_000_000,
                6));

        Assert.Equal(
            new CadPoint3D(
                10_000_000_000_000_004,
                -9_999_999_999_999_996,
                6),
            snapshot.Center);
        AssertClose(8.0, snapshot.EdgeLength);
    }

    [Fact]
    public void FinalFailureDoesNotConsumeFirstPoint()
    {
        var authoring = new CadPolygonAuthoringSession(
            6,
            CadPolygonAuthoringMode.Edge);
        var first = new CadPoint3D(2, 3, 4);
        AcceptFirst(authoring, first);

        Assert.False(authoring.TryAcceptPoint(
            first,
            out _,
            out _,
            out string? duplicate));
        Assert.Contains("nonzero", duplicate, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, authoring.AcceptedInputCount);
        Assert.Equal(first, authoring.FirstPoint);

        Assert.True(authoring.TryAcceptPoint(
            new CadPoint3D(5, 3, 4),
            out _,
            out bool completed,
            out _));
        Assert.True(completed);
        Assert.Equal(1, authoring.AcceptedInputCount);
    }

    [Fact]
    public void OffPlaneAndNonfinitePointsFailClosed()
    {
        var authoring = new CadPolygonAuthoringSession(
            5,
            CadPolygonAuthoringMode.Inscribed);
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(double.NaN, 0, 0),
            out _,
            out _,
            out string? nonfinite));
        Assert.Contains("finite", nonfinite, StringComparison.OrdinalIgnoreCase);
        AcceptFirst(authoring, new CadPoint3D(0, 0, 2));
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(1, 0, 3),
            out _,
            out _,
            out string? offPlane));
        Assert.Contains("plane", offPlane, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData(" 1024 ", 1024)]
    public void SideCountParserIsBoundedInvariant(
        string text,
        int expected)
    {
        Assert.True(CadPolygonSideCount.TryParse(text, out var sideCount));
        Assert.Equal(expected, sideCount.Value);
        Assert.False(CadPolygonSideCount.TryParse("2", out _));
        Assert.False(CadPolygonSideCount.TryParse("1025", out _));
        Assert.False(CadPolygonSideCount.TryParse("4.0", out _));
        Assert.False(CadPolygonSideCount.TryParse("+4", out _));
    }

    [Fact]
    public void SnapshotMaterializesOneClosedZeroBulgePolyline()
    {
        var snapshot = new CadPolygonAuthoringSnapshot(
            CadPolygonAuthoringMode.Inscribed,
            1024,
            new CadPoint3D(-2, 5, 7),
            9,
            0.25);

        CadPolylineAuthoringSnapshot polyline = snapshot.CreatePolylineSnapshot();

        Assert.True(polyline.IsClosed);
        Assert.Equal(1024, polyline.Points.Length);
        Assert.Equal(1024, polyline.SegmentCount);
        Assert.All(polyline.Bulges.ToArray(), value => Assert.Equal(0.0, value));
        Assert.All(polyline.Points.ToArray(), point => Assert.Equal(7.0, point.Z));
    }

    [Fact]
    public void ExistingPolylineCommandPublishesPolygonAtomically()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var snapshot = new CadPolygonAuthoringSnapshot(
            CadPolygonAuthoringMode.Inscribed,
            5,
            new CadPoint3D(10, 20, 4),
            6,
            0.5);
        var command = new CadAddPolylineCommand(
            snapshot.CreatePolylineSnapshot(),
            "POLYGON");

        history.Execute(command);

        LwPolyline polyline = Assert.Single(document.Entities.OfType<LwPolyline>());
        Assert.True(polyline.IsClosed);
        Assert.Equal(5, polyline.Vertices.Count);
        Assert.Equal(4.0, polyline.Elevation);
        Assert.True(history.TryUndo(out _));
        Assert.Empty(document.Entities);
        Assert.True(history.TryRedo(out _));
        Assert.Same(polyline, Assert.Single(document.Entities));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AuthoredPolygonRoundTripsThroughCadStore(
        CadDocumentFormat format)
    {
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        var source = new CadPolygonAuthoringSnapshot(
            CadPolygonAuthoringMode.Circumscribed,
            7,
            new CadPoint3D(-3, 8, 2),
            5,
            0.75);
        history.Execute(new CadAddPolylineCommand(
            source.CreatePolylineSnapshot(),
            "POLYGON"));
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            session,
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"polygon-authoring.{format.ToString().ToLowerInvariant()}");

        LwPolyline polyline = Assert.Single(loaded.Session.Read(document =>
            document.Entities.OfType<LwPolyline>().ToArray()));
        Assert.True(polyline.IsClosed);
        Assert.Equal(7, polyline.Vertices.Count);
        Assert.Equal(2.0, polyline.Elevation);
        for (int index = 0; index < polyline.Vertices.Count; index++)
        {
            CadPoint3D expected = source.VertexAt(index);
            AssertClose(expected.X, polyline.Vertices[index].Location.X);
            AssertClose(expected.Y, polyline.Vertices[index].Location.Y);
            Assert.Equal(0.0, polyline.Vertices[index].Bulge);
        }
    }

    [Fact]
    public void AuthoredPolygonRetainsManagedAndNativeReplay()
    {
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddPolylineCommand(
            new CadPolygonAuthoringSnapshot(
                CadPolygonAuthoringMode.Edge,
                12,
                new CadPoint3D(10, 20, 0),
                8,
                0.25).CreatePolylineSnapshot(),
            "POLYGON"));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        Assert.Single(snapshot.Polylines.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        Assert.NotEmpty(scene.DrawingContext.Commands.ToArray());
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            109U,
            scene.ContentGeneration,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.True(compiled.GeometryPrimitiveCount > 0);
    }

    private static void AcceptFirst(
        CadPolygonAuthoringSession authoring,
        CadPoint3D point)
    {
        Assert.True(authoring.TryAcceptPoint(
            point,
            out _,
            out bool completed,
            out string? error),
            error);
        Assert.False(completed);
    }

    private static CadPolygonAuthoringSnapshot CompletePoint(
        CadPolygonAuthoringSession authoring,
        CadPoint3D point)
    {
        Assert.True(authoring.TryAcceptPoint(
            point,
            out CadPolygonAuthoringSnapshot snapshot,
            out bool completed,
            out string? error),
            error);
        Assert.True(completed);
        return snapshot;
    }

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
        AssertClose(expected.Z, actual.Z);
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);
}

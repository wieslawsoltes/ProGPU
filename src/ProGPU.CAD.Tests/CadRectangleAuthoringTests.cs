using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadRectangleAuthoringTests
{
    private const double Tolerance = 1e-9;
    private const double QuarterCircleBulge = 0.4142135623730951;

    [Fact]
    public void DiagonalCornersProjectOntoRotatedBasis()
    {
        var authoring = new CadRectangleAuthoringSession(Math.PI * 0.25);
        CadPoint3D first = new(10, 20, 3);
        AcceptFirst(authoring, first);
        double rootHalf = Math.Sqrt(0.5);
        CadRectangleAuthoringSnapshot snapshot = CompletePoint(
            authoring,
            new CadPoint3D(
                first.X + (2.0 * rootHalf),
                first.Y + (6.0 * rootHalf),
                first.Z));

        AssertClose(4.0, snapshot.LocalXExtent);
        AssertClose(2.0, snapshot.LocalYExtent);
        AssertClose(8.0, snapshot.EnclosedArea);
        Assert.Equal(4, snapshot.VertexCount);
        AssertPoint(first, snapshot.VertexAt(0));
        AssertPoint(
            new CadPoint3D(
                first.X + (4.0 * rootHalf),
                first.Y + (4.0 * rootHalf),
                first.Z),
            snapshot.VertexAt(1));
    }

    [Fact]
    public void DimensionsUsePlacementQuadrantInRotatedBasis()
    {
        var authoring = new CadRectangleAuthoringSession(Math.PI * 0.5);
        AcceptFirst(authoring, new CadPoint3D(4, 5, 6));

        Assert.True(authoring.TryCreateFromDimensions(
            8,
            3,
            new CadPoint3D(10, 0, 6),
            out CadRectangleAuthoringSnapshot snapshot,
            out string? error),
            error);

        AssertClose(-8.0, snapshot.LocalXExtent);
        AssertClose(-3.0, snapshot.LocalYExtent);
        Assert.Equal(1, snapshot.Orientation);
        AssertPoint(new CadPoint3D(4, -3, 6), snapshot.VertexAt(1));
        AssertPoint(new CadPoint3D(7, -3, 6), snapshot.VertexAt(2));
    }

    [Fact]
    public void ChamferProducesEightExactLineVerticesAndCorrectArea()
    {
        var treatment = CadRectangleCornerTreatment.Chamfer(2, 1);
        var snapshot = new CadRectangleAuthoringSnapshot(
            new CadPoint3D(0, 0, 7),
            10,
            6,
            0,
            treatment);

        Assert.Equal(8, snapshot.VertexCount);
        AssertClose(56.0, snapshot.EnclosedArea);
        CadPoint3D[] expected =
        [
            new(2, 0, 7),
            new(8, 0, 7),
            new(10, 1, 7),
            new(10, 5, 7),
            new(8, 6, 7),
            new(2, 6, 7),
            new(0, 5, 7),
            new(0, 1, 7),
        ];
        for (int index = 0; index < expected.Length; index++)
        {
            AssertPoint(expected[index], snapshot.VertexAt(index));
            Assert.Equal(0.0, snapshot.BulgeAt(index));
        }
    }

    [Theory]
    [InlineData(10.0, 6.0, 1.0)]
    [InlineData(-10.0, 6.0, -1.0)]
    [InlineData(10.0, -6.0, -1.0)]
    [InlineData(-10.0, -6.0, 1.0)]
    public void FilletUsesExactOrientationSignedQuarterCircleBulges(
        double xExtent,
        double yExtent,
        double sign)
    {
        var snapshot = new CadRectangleAuthoringSnapshot(
            new CadPoint3D(0, 0, 2),
            xExtent,
            yExtent,
            0,
            CadRectangleCornerTreatment.Fillet(1));

        Assert.Equal(8, snapshot.VertexCount);
        for (int index = 0; index < snapshot.VertexCount; index++)
        {
            double expected = index % 2 == 1
                ? sign * QuarterCircleBulge
                : 0.0;
            AssertClose(expected, snapshot.BulgeAt(index));
        }
        AssertClose(
            60.0 - (4.0 - Math.PI),
            snapshot.EnclosedArea);
    }

    [Fact]
    public void MaximumFilletCoalescesToExactFourBulgeCircle()
    {
        var snapshot = new CadRectangleAuthoringSnapshot(
            new CadPoint3D(-2, -2, 1),
            4,
            4,
            0,
            CadRectangleCornerTreatment.Fillet(2));

        Assert.Equal(4, snapshot.VertexCount);
        AssertClose(Math.PI * 4.0, snapshot.EnclosedArea);
        for (int index = 0; index < snapshot.VertexCount; index++)
        {
            AssertClose(QuarterCircleBulge, snapshot.BulgeAt(index));
        }
        CadPolylineAuthoringSnapshot polyline = snapshot.CreatePolylineSnapshot();
        Assert.Equal(4, polyline.Points.Length);
        Assert.True(polyline.IsClosed);
    }

    [Theory]
    [InlineData(CadRectangleKnownDimension.Length)]
    [InlineData(CadRectangleKnownDimension.Width)]
    public void AreaModeIncludesChamferAndFilletCornerEffects(
        CadRectangleKnownDimension knownDimension)
    {
        foreach (CadRectangleCornerTreatment treatment in new[]
        {
            CadRectangleCornerTreatment.Sharp,
            CadRectangleCornerTreatment.Chamfer(2, 1),
            CadRectangleCornerTreatment.Fillet(1.5),
        })
        {
            var authoring = new CadRectangleAuthoringSession(
                0,
                treatment);
            AcceptFirst(authoring, new CadPoint3D(0, 0, 9));

            Assert.True(authoring.TryCreateFromArea(
                96,
                knownDimension,
                12,
                new CadPoint3D(-1, 1, 9),
                out CadRectangleAuthoringSnapshot snapshot,
                out string? error),
                error);

            AssertClose(96.0, snapshot.EnclosedArea);
            Assert.Equal(-1, snapshot.Orientation);
            if (knownDimension == CadRectangleKnownDimension.Length)
            {
                AssertClose(12.0, snapshot.Length);
            }
            else
            {
                AssertClose(12.0, snapshot.Width);
            }
        }
    }

    [Fact]
    public void CornerSettingsAreValidatedAndMutuallyExclusive()
    {
        var authoring = new CadRectangleAuthoringSession();
        Assert.True(authoring.TrySetChamfer(2, 3, out _));
        Assert.Equal(CadRectangleCornerMode.Chamfer, authoring.CornerTreatment.Mode);
        Assert.True(authoring.TrySetFillet(4, out _));
        Assert.Equal(CadRectangleCornerMode.Fillet, authoring.CornerTreatment.Mode);
        Assert.Equal(4.0, authoring.CornerTreatment.FilletRadius);
        Assert.False(authoring.TrySetFillet(-1, out string? negative));
        Assert.Contains("non-negative", negative, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CadRectangleCornerMode.Fillet, authoring.CornerTreatment.Mode);
        authoring.SetSharpCorners();
        Assert.Equal(CadRectangleCornerMode.Sharp, authoring.CornerTreatment.Mode);
    }

    [Fact]
    public void InvalidFinalInputDoesNotConsumeFirstCorner()
    {
        var authoring = new CadRectangleAuthoringSession(
            0,
            CadRectangleCornerTreatment.Fillet(5));
        CadPoint3D first = new(2, 3, 4);
        AcceptFirst(authoring, first);

        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(8, 9, 4),
            out _,
            out _,
            out string? tooSmall));
        Assert.Contains("radius", tooSmall, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, authoring.AcceptedInputCount);
        Assert.Equal(first, authoring.FirstCorner);

        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(20, 20, 5),
            out _,
            out _,
            out string? offPlane));
        Assert.Contains("plane", offPlane, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first, authoring.FirstCorner);

        CadRectangleAuthoringSnapshot snapshot = CompletePoint(
            authoring,
            new CadPoint3D(20, 20, 4));
        Assert.Equal(8, snapshot.VertexCount);
    }

    [Fact]
    public void NonfiniteDegenerateAndUnrenderableInputsFailClosed()
    {
        var authoring = new CadRectangleAuthoringSession();
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(double.NaN, 0, 0),
            out _,
            out _,
            out string? nonfinite));
        Assert.Contains("finite", nonfinite, StringComparison.OrdinalIgnoreCase);
        AcceptFirst(authoring, new CadPoint3D(0, 0, 0));
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(0, 2, 0),
            out _,
            out _,
            out string? degenerate));
        Assert.Contains("extent", degenerate, StringComparison.OrdinalIgnoreCase);
        Assert.False(authoring.TryCreateFromDimensions(
            double.PositiveInfinity,
            2,
            new CadPoint3D(1, 1, 0),
            out _,
            out string? unrenderable));
        Assert.Contains("renderable", unrenderable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LargeWcsOriginRetainsRepresentableLocalContour()
    {
        const double origin = 10_000_000_000_000_000;
        var snapshot = new CadRectangleAuthoringSnapshot(
            new CadPoint3D(origin, -origin, 8),
            16,
            -24,
            0,
            CadRectangleCornerTreatment.Chamfer(2, 4));

        Assert.Equal(8, snapshot.VertexCount);
        AssertPoint(
            new CadPoint3D(origin + 2, -origin, 8),
            snapshot.VertexAt(0));
        AssertClose(368.0, snapshot.EnclosedArea);
    }

    [Fact]
    public void SnapshotMaterializesOneClosedAnalyticPolyline()
    {
        var snapshot = new CadRectangleAuthoringSnapshot(
            new CadPoint3D(10, 20, 3),
            12,
            8,
            0.25,
            CadRectangleCornerTreatment.Fillet(2));

        CadPolylineAuthoringSnapshot polyline = snapshot.CreatePolylineSnapshot();

        Assert.True(polyline.IsClosed);
        Assert.Equal(8, polyline.Points.Length);
        Assert.Equal(8, polyline.SegmentCount);
        Assert.Equal(4, polyline.Bulges.ToArray().Count(value => value != 0.0));
        Assert.All(polyline.Points.ToArray(), point => Assert.Equal(3.0, point.Z));
    }

    [Fact]
    public void ExistingPolylineCommandPublishesRectangleAtomically()
    {
        var document = new CadDocument();
        document.Header.PolylineLineTypeGeneration = true;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var source = new CadRectangleAuthoringSnapshot(
            new CadPoint3D(1, 2, 3),
            10,
            6,
            0,
            CadRectangleCornerTreatment.Chamfer(1, 2));
        var command = new CadAddPolylineCommand(
            source.CreatePolylineSnapshot(),
            "RECTANG");

        history.Execute(command);

        LwPolyline polyline = Assert.Single(document.Entities.OfType<LwPolyline>());
        Assert.True(polyline.IsClosed);
        Assert.True(polyline.Flags.HasFlag(LwPolylineFlags.Plinegen));
        Assert.Equal(8, polyline.Vertices.Count);
        Assert.Equal(3.0, polyline.Elevation);
        Assert.True(history.TryUndo(out _));
        Assert.Empty(document.Entities);
        Assert.True(history.TryRedo(out _));
        Assert.Same(polyline, Assert.Single(document.Entities));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AuthoredFilletRectangleRoundTripsThroughCadStore(
        CadDocumentFormat format)
    {
        var source = new CadRectangleAuthoringSnapshot(
            new CadPoint3D(-3, 8, 2),
            -14,
            10,
            0.35,
            CadRectangleCornerTreatment.Fillet(2));
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddPolylineCommand(
            source.CreatePolylineSnapshot(),
            "RECTANG"));
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
            sourceName: $"rectangle-authoring.{format.ToString().ToLowerInvariant()}");

        LwPolyline polyline = Assert.Single(loaded.Session.Read(document =>
            document.Entities.OfType<LwPolyline>().ToArray()));
        Assert.True(polyline.IsClosed);
        Assert.Equal(source.VertexCount, polyline.Vertices.Count);
        Assert.Equal(2.0, polyline.Elevation);
        for (int index = 0; index < polyline.Vertices.Count; index++)
        {
            CadPoint3D expected = source.VertexAt(index);
            AssertClose(expected.X, polyline.Vertices[index].Location.X);
            AssertClose(expected.Y, polyline.Vertices[index].Location.Y);
            AssertClose(source.BulgeAt(index), polyline.Vertices[index].Bulge);
        }
    }

    [Fact]
    public void AuthoredRectangleRetainsManagedAndNativeReplay()
    {
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddPolylineCommand(
            new CadRectangleAuthoringSnapshot(
                new CadPoint3D(10, 20, 0),
                30,
                -18,
                0.25,
                CadRectangleCornerTreatment.Fillet(3))
            .CreatePolylineSnapshot(),
            "RECTANG"));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        Assert.Single(snapshot.Polylines.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        Assert.NotEmpty(scene.DrawingContext.Commands.ToArray());
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            131U,
            scene.ContentGeneration,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.True(compiled.GeometryPrimitiveCount > 0);
    }

    [Fact]
    public void BoundedRandomizedSnapshotsPreserveContourInvariants()
    {
        var random = new Random(0x52454354);
        for (int iteration = 0; iteration < 4_096; iteration++)
        {
            double length = 1.0 + (random.NextDouble() * 10_000.0);
            double width = 1.0 + (random.NextDouble() * 10_000.0);
            double x = random.Next(2) == 0 ? length : -length;
            double y = random.Next(2) == 0 ? width : -width;
            CadRectangleCornerTreatment treatment = random.Next(3) switch
            {
                0 => CadRectangleCornerTreatment.Sharp,
                1 => CadRectangleCornerTreatment.Chamfer(
                    random.NextDouble() * length * 0.5,
                    random.NextDouble() * width * 0.5),
                _ => CadRectangleCornerTreatment.Fillet(
                    random.NextDouble() * Math.Min(length, width) * 0.5),
            };
            var snapshot = new CadRectangleAuthoringSnapshot(
                new CadPoint3D(
                    random.NextDouble() * 1_000_000.0,
                    random.NextDouble() * -1_000_000.0,
                    11),
                x,
                y,
                (random.NextDouble() - 0.5) * Math.Tau * 8.0,
                treatment);

            Assert.InRange(snapshot.VertexCount, 4, 8);
            Assert.True(double.IsFinite(snapshot.EnclosedArea));
            Assert.True(snapshot.EnclosedArea > 0.0);
            CadPolylineAuthoringSnapshot polyline = snapshot.CreatePolylineSnapshot();
            Assert.True(polyline.IsClosed);
            Assert.Equal(snapshot.VertexCount, polyline.Points.Length);
            ReadOnlySpan<CadPoint3D> points = polyline.Points.Span;
            for (int index = 0; index < points.Length; index++)
            {
                Assert.NotEqual(points[index], points[(index + 1) % points.Length]);
            }
        }
    }

    private static void AcceptFirst(
        CadRectangleAuthoringSession authoring,
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

    private static CadRectangleAuthoringSnapshot CompletePoint(
        CadRectangleAuthoringSession authoring,
        CadPoint3D point)
    {
        Assert.True(authoring.TryAcceptPoint(
            point,
            out CadRectangleAuthoringSnapshot snapshot,
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

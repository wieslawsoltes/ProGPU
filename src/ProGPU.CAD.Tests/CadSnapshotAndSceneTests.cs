using ACadSharp.Entities;
using CSMath;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadSnapshotAndSceneTests
{
    private const double Tolerance = 1e-10;

    [Fact]
    public void ArbitraryAxisBasisIsOrthonormalAndTransformsOcsToWcs()
    {
        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(new CadPoint3D(0, 1, 0));

        AssertPoint(new CadPoint3D(-1, 0, 0), basis.XAxis);
        AssertPoint(new CadPoint3D(0, 0, 1), basis.YAxis);
        AssertPoint(new CadPoint3D(0, 1, 0), basis.ZAxis);
        AssertPoint(new CadPoint3D(-1, 3, 2), basis.Transform(new CadPoint3D(1, 2, 3)));
        Assert.InRange(Math.Abs(CadPoint3D.Dot(basis.XAxis, basis.YAxis)), 0, Tolerance);
        Assert.InRange(Math.Abs(CadPoint3D.Dot(basis.XAxis, basis.ZAxis)), 0, Tolerance);
        Assert.InRange(Math.Abs(CadPoint3D.Dot(basis.YAxis, basis.ZAxis)), 0, Tolerance);
    }

    [Fact]
    public void SnapshotNormalizesCircleOcsAndComputesExactTiltedBounds()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add OCS circle", document => document.Entities.Add(new Circle
        {
            Center = new XYZ(1, 2, 3),
            Normal = XYZ.AxisY,
            Radius = 4,
        }));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        CadCirclePrimitive circle = Assert.Single(snapshot.Circles.ToArray());
        AssertPoint(new CadPoint3D(-1, 3, 2), circle.Center);
        AssertPoint(new CadPoint3D(-5, 3, -2), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(3, 3, 6), snapshot.Bounds.Max);
        Assert.Equal(1UL, snapshot.ContentGeneration);
        Assert.Equal(snapshot.ContentGeneration, session.ContentGeneration);
    }

    [Fact]
    public void ArcBoundsUseOnlyAnglesInsidePositiveOcsSweep()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add arc", document => document.Entities.Add(new Arc
        {
            Center = XYZ.Zero,
            Normal = XYZ.AxisZ,
            Radius = 10,
            StartAngle = 0,
            EndAngle = Math.PI / 2,
        }));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadArcPrimitive arc = Assert.Single(snapshot.Arcs.ToArray());

        Assert.Equal(Math.PI / 2, arc.SweepAngle, 12);
        AssertPoint(new CadPoint3D(0, 0, 0), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(10, 10, 0), snapshot.Bounds.Max);
    }

    [Fact]
    public void SpatialIndexMatchesBruteForceAndReportsTruncation()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add grid", document =>
        {
            for (int i = 0; i < 64; i++)
            {
                document.Entities.Add(new Line(
                    new XYZ(i * 10, i % 5, -i),
                    new XYZ((i * 10) + 3, (i % 5) + 2, i)));
            }
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        var query = new CadBounds3D(
            new CadPoint3D(95, -10, -100),
            new CadPoint3D(255, 20, 100));
        int[] expected = snapshot.Entities.Span
            .ToArray()
            .Select((entity, index) => (entity, index))
            .Where(item => item.entity.Bounds.Intersects(query))
            .Select(item => item.index)
            .Order()
            .ToArray();
        var actual = new int[expected.Length];

        CadSpatialQueryResult result = snapshot.SpatialIndex.Query(query, actual);

        Assert.Equal(expected.Length, result.TotalCount);
        Assert.Equal(expected.Length, result.WrittenCount);
        Assert.False(result.IsTruncated);
        Assert.Equal(expected, actual.Order().ToArray());

        Span<int> shortBuffer = stackalloc int[2];
        CadSpatialQueryResult truncated = snapshot.SpatialIndex.Query(query, shortBuffer);
        Assert.Equal(2, truncated.WrittenCount);
        Assert.Equal(expected.Length, truncated.TotalCount);
        Assert.True(truncated.IsTruncated);
    }

    [Fact]
    public void WarmSpatialQueriesAllocateNoManagedMemory()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add indexed lines", document =>
        {
            for (int i = 0; i < 256; i++)
            {
                document.Entities.Add(new Line(
                    new XYZ(i * 2, i % 11, 0),
                    new XYZ((i * 2) + 1, (i % 11) + 1, 0)));
            }
        });
        CadSpatialIndex index = new CadSnapshotCompiler().Compile(session).SpatialIndex;
        var query = new CadBounds3D(
            new CadPoint3D(100, -1, -1),
            new CadPoint3D(300, 20, 1));
        Span<int> destination = stackalloc int[128];
        _ = index.Query(query, destination);
        _ = GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            checksum += index.Query(query, destination).TotalCount;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(checksum > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void PlanSceneRecordsAnalyticArcWithoutLineTessellation()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add arc", document => document.Entities.Add(new Arc
        {
            Center = new XYZ(1_000_000_000_000, 2_000_000_000_000, 0),
            Radius = 25,
            StartAngle = 0,
            EndAngle = Math.PI * 1.5,
        }));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(1, scene.Statistics.RecordedEntityCount);
        Assert.Equal(1, scene.Statistics.RecordedCommandCount);
        RenderCommand command = scene.DrawingContext.Commands[0];
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.NotNull(command.Path);
        PathFigure figure = Assert.Single(command.Path.Figures);
        ArcSegment segment = Assert.IsType<ArcSegment>(Assert.Single(figure.Segments));
        Assert.True(segment.IsLargeArc);
        Assert.Equal(SweepDirection.Counterclockwise, segment.SweepDirection);
        Assert.NotNull(command.Pen);
        Assert.Equal(PenStrokeTransformMode.Fixed, command.Pen.StrokeTransformMode);
        Assert.True(float.IsFinite(command.Transform.M41));
        Assert.True(float.IsFinite(command.Transform.M42));
    }

    [Fact]
    public void PlanSceneProjectsTiltedCircleWithOneAnalyticEllipseCommand()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add tilted circle", document => document.Entities.Add(new Circle
        {
            Center = XYZ.Zero,
            Normal = new XYZ(0, 1, 1),
            Radius = 8,
        }));

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawEllipse, command.Type);
        Assert.Equal(8, command.RadiusX);
        Assert.Equal(8, command.RadiusY);
        Assert.NotEqual(System.Numerics.Matrix4x4.Identity, command.Transform);
    }

    [Fact]
    public void SplineSnapshotPreservesControlKnotsWeightsAndRecordsOneCommand()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add spline", document =>
        {
            var spline = new Spline { Degree = 2 };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 0),
                new XYZ(5, 10, 1),
                new XYZ(10, 0, 2),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            spline.Weights.AddRange([1, 0.5, 1]);
            document.Entities.Add(spline);
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadSplinePrimitive spline = Assert.Single(snapshot.Splines.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(3, spline.ControlPointCount);
        Assert.Equal(6, spline.KnotCount);
        Assert.Equal(3, spline.WeightCount);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawExtension, command.Type);
        Assert.Equal(3, command.PointBufferCount);
        Assert.Equal(6, command.DoubleBufferCount);
        Assert.Equal(3, command.WeightBufferCount);
    }

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, Tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, Tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, Tolerance);
    }
}

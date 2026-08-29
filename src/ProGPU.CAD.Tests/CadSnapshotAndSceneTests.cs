using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using System.Numerics;
using System.Text;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Text;
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
    public void FullEllipseHasExactRotatedBoundsAndOneAnalyticCommand()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add rotated ellipse", document => document.Entities.Add(new Ellipse
        {
            Center = new XYZ(10, 20, 30),
            MajorAxisEndPoint = new XYZ(3, 4, 0),
            Normal = XYZ.AxisZ,
            RadiusRatio = 0.5,
        }));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadEllipsePrimitive ellipse = Assert.Single(snapshot.Ellipses.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        AssertPoint(new CadPoint3D(-2, 1.5, 0), ellipse.MinorAxis);
        AssertPoint(
            new CadPoint3D(10 - Math.Sqrt(13), 20 - Math.Sqrt(18.25), 30),
            snapshot.Bounds.Min);
        AssertPoint(
            new CadPoint3D(10 + Math.Sqrt(13), 20 + Math.Sqrt(18.25), 30),
            snapshot.Bounds.Max);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawEllipse, command.Type);
        Assert.Equal(1, command.RadiusX);
        Assert.Equal(1, command.RadiusY);
        Assert.NotEqual(System.Numerics.Matrix4x4.Identity, command.Transform);
    }

    [Fact]
    public void EllipticalArcRetainsOneAnalyticArcAndPartialBounds()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add elliptical arc", document => document.Entities.Add(new Ellipse
        {
            Center = new XYZ(5, 7, 0),
            MajorAxisEndPoint = new XYZ(4, 0, 0),
            Normal = XYZ.AxisZ,
            RadiusRatio = 0.5,
            StartParameter = 0,
            EndParameter = Math.PI / 2,
        }));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        AssertPoint(new CadPoint3D(5, 7, 0), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(9, 9, 0), snapshot.Bounds.Max);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        ArcSegment arc = Assert.IsType<ArcSegment>(
            Assert.Single(Assert.Single(command.Path!.Figures).Segments));
        Assert.Equal(System.Numerics.Vector2.One, arc.Size);
        Assert.Equal(SweepDirection.Counterclockwise, arc.SweepDirection);
    }

    [Fact]
    public void SolidFillsAndFace3DWireframeHonorsInvisibleEdges()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add faces", document =>
        {
            document.Entities.Add(new Solid(
                new XYZ(0, 0, 0),
                new XYZ(4, 0, 0),
                new XYZ(0, 3, 0),
                new XYZ(4, 3, 0)));
            document.Entities.Add(new Face3D
            {
                FirstCorner = new XYZ(10, 0, 1),
                SecondCorner = new XYZ(14, 0, 2),
                ThirdCorner = new XYZ(14, 3, 3),
                FourthCorner = new XYZ(10, 3, 4),
                Flags = InvisibleEdgeFlags.Second | InvisibleEdgeFlags.Fourth,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        CadRecordedMesh3DScene meshScene = new CadMesh3DSceneCompiler().Compile(snapshot);

        Assert.Equal(2, snapshot.Faces.Length);
        Assert.Equal(CadEntityKind.Solid, snapshot.Entities.Span[0].Kind);
        Assert.Equal(CadEntityKind.Face3D, snapshot.Entities.Span[1].Kind);
        AssertPoint(new CadPoint3D(0, 0, 0), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(14, 3, 4), snapshot.Bounds.Max);
        Assert.Equal(2, scene.DrawingContext.Commands.Count);
        CadFacePrimitive normalizedSolid = snapshot.Faces.Span[0];
        AssertPoint(new CadPoint3D(4, 3, 0), normalizedSolid.Third);
        AssertPoint(new CadPoint3D(0, 3, 0), normalizedSolid.Fourth);
        RenderCommand solid = scene.DrawingContext.Commands[0];
        Assert.Equal(RenderCommandType.DrawPath, solid.Type);
        Assert.NotNull(solid.Brush);
        Assert.Null(solid.Pen);
        Assert.Equal(FillRule.EvenOdd, solid.Path!.FillRule);
        Assert.True(Assert.Single(solid.Path.Figures).IsClosed);
        RenderCommand face = scene.DrawingContext.Commands[1];
        Assert.Null(face.Brush);
        Assert.NotNull(face.Pen);
        Assert.Equal(2, face.Path!.Figures.Count);
        Assert.Equal(2, meshScene.Statistics.SourceFaceCount);
        Assert.Equal(2, meshScene.Statistics.FaceRangeCount);
        Assert.Equal(4, meshScene.Statistics.TriangleCount);
        Assert.Equal(2, meshScene.Statistics.DrawBatchCount);
        CadMesh3DDrawBatch[] surfaceBatches = meshScene.DrawBatches.ToArray();
        Assert.All(surfaceBatches, batch => Assert.Equal(6, batch.Indices.Length));
        Assert.NotEqual(
            surfaceBatches[1].Normals.Span[0],
            surfaceBatches[1].Normals.Span[3]);
    }

    [Fact]
    public void ExtrudedSolidRetainsTransformedThicknessAndRecordsPlanShell()
    {
        var block = new BlockRecord("THICK_SOLID");
        block.Entities.Add(new Solid(
            new XYZ(0, 0, 0),
            new XYZ(4, 0, 0),
            new XYZ(0, 3, 0),
            new XYZ(4, 3, 0))
        {
            Normal = XYZ.AxisZ,
            Thickness = 2,
        });
        var insert = new Insert(block)
        {
            InsertPoint = new XYZ(10, 20, 30),
            XScale = 2,
            YScale = 4,
            ZScale = 3,
        };
        var document = new CadDocument();
        document.Entities.Add(insert);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadFacePrimitive face = Assert.Single(snapshot.Faces.ToArray());
        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());
        CadRecordedPlanScene plan = new CadPlanSceneCompiler().Compile(snapshot);
        CadRecordedMesh3DScene mesh = new CadMesh3DSceneCompiler().Compile(snapshot);

        AssertPoint(new CadPoint3D(0, 0, 6), face.Extrusion);
        AssertPoint(new CadPoint3D(10, 20, 30), header.Bounds.Min);
        AssertPoint(new CadPoint3D(18, 32, 36), header.Bounds.Max);
        Assert.Equal(12, mesh.Statistics.TriangleCount);
        Assert.Equal(36, Assert.Single(mesh.DrawBatches.ToArray()).Indices.Length);
        RenderCommand command = Assert.Single(plan.DrawingContext.Commands.ToArray());
        Assert.Null(command.Brush);
        Assert.NotNull(command.Pen);
        Assert.Equal(4, command.Path!.Figures.Count);
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
    public void RecordedSceneCreatesOwnedPictureWithSplineSideBuffers()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add spline", document =>
        {
            var spline = new Spline { Degree = 2 };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 0),
                new XYZ(5, 8, 0),
                new XYZ(10, 0, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            document.Entities.Add(spline);
        });
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        using GpuPicture picture = scene.CreatePicture();

        Assert.Equal(scene.Statistics.RecordedCommandCount, picture.CommandCount);
        Assert.Equal(3, picture.PointBuffer.Length);
        Assert.Equal(6, picture.DoubleBuffer.Length);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PeriodicSplineExpandsCyclicRecordWithoutSyntheticSeam(bool expandedKnots)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add periodic spline", document =>
        {
            var spline = new Spline
            {
                Degree = 2,
                IsClosed = true,
                IsPeriodic = true,
            };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 0),
                new XYZ(10, 0, 0),
                new XYZ(10, 10, 0),
                new XYZ(0, 10, 0),
            ]);
            spline.Knots.AddRange(expandedKnots
                ? [-2, -1, 0, 1, 2, 3, 4, 5, 6]
                : [0, 1, 2, 3, 4]);
            spline.Weights.AddRange([1, 2, 1, 2]);
            document.Entities.Add(spline);
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadSplinePrimitive spline = Assert.Single(snapshot.Splines.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.True(spline.IsClosed);
        Assert.True(spline.IsPeriodic);
        Assert.Equal(4, spline.ControlPointCount);
        Assert.Equal(expandedKnots ? 9 : 5, spline.KnotCount);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands);
        Assert.Equal(6, command.PointBufferCount);
        Assert.Equal(9, command.DoubleBufferCount);
        Assert.Equal(6, command.WeightBufferCount);
        Assert.False(command.IsClosed);
        Assert.Equal(
            new double[] { -2, -1, 0, 1, 2, 3, 4, 5, 6 },
            scene.DrawingContext.DoubleBuffer
                .Skip(command.DoubleBufferOffset)
                .Take(command.DoubleBufferCount));
        Assert.Equal(
            new double[] { 1, 2, 1, 2, 1, 2 },
            scene.DrawingContext.DoubleBuffer
                .Skip(command.WeightBufferOffset)
                .Take(command.WeightBufferCount));
        Vector2[] points = scene.DrawingContext.PointBuffer
            .Skip(command.PointBufferOffset)
            .Take(command.PointBufferCount)
            .ToArray();
        Assert.Equal(points[0], points[4]);
        Assert.Equal(points[1], points[5]);
        double[] knots = scene.DrawingContext.DoubleBuffer
            .Skip(command.DoubleBufferOffset)
            .Take(command.DoubleBufferCount)
            .ToArray();
        double[] weights = scene.DrawingContext.DoubleBuffer
            .Skip(command.WeightBufferOffset)
            .Take(command.WeightBufferCount)
            .ToArray();
        PathGeometry path = RenderCommandGeometryCache.CreateSplinePath(
            points,
            knots,
            weights,
            command.SplineDegree,
            command.IsClosed);
        PathFigure figure = Assert.Single(path.Figures);
        Vector2 end = Assert.IsType<LineSegment>(figure.Segments[^1]).Point;
        Assert.Equal(figure.StartPoint.X, end.X, 5);
        Assert.Equal(figure.StartPoint.Y, end.Y, 5);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.StrokeCount);
        Assert.Equal(6, compiled.StrokePointCount);
        Assert.Equal(15, compiled.StrokeDoubleCount);
    }

    [Theory]
    [InlineData(1.0, SweepDirection.Counterclockwise, -5.0, 0.0)]
    [InlineData(-1.0, SweepDirection.Clockwise, 0.0, 5.0)]
    public void LightweightPolylineBulgeRemainsOneAnalyticArc(
        double bulge,
        SweepDirection expectedDirection,
        double expectedMinY,
        double expectedMaxY)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add bulged polyline", document =>
        {
            var polyline = new LwPolyline();
            polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = bulge });
            polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
            document.Entities.Add(polyline);
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(CadEntityKind.LightweightPolyline, Assert.Single(snapshot.Entities.ToArray()).Kind);
        Assert.Equal(expectedMinY, snapshot.Bounds.Min.Y, 10);
        Assert.Equal(expectedMaxY, snapshot.Bounds.Max.Y, 10);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        ArcSegment arc = Assert.IsType<ArcSegment>(
            Assert.Single(Assert.Single(command.Path!.Figures).Segments));
        Assert.Equal(expectedDirection, arc.SweepDirection);
        Assert.False(arc.IsLargeArc);
        Assert.Equal(new System.Numerics.Vector2(5, 5), arc.Size);
    }

    [Fact]
    public void LegacyPolyline2DPreservesOcsElevationAndAnalyticBulge()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add legacy 2D polyline", document =>
        {
            var polyline = new Polyline2D
            {
                Elevation = 3,
                Normal = XYZ.AxisY,
            };
            polyline.Vertices.Add(new Vertex2D(new XYZ(0, 0, 0)) { Bulge = 1 });
            polyline.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
            document.Entities.Add(polyline);
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(CadEntityKind.Polyline2D, Assert.Single(snapshot.Entities.ToArray()).Kind);
        AssertPoint(new CadPoint3D(-10, 3, -5), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(0, 3, 0), snapshot.Bounds.Max);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        ArcSegment arc = Assert.IsType<ArcSegment>(
            Assert.Single(Assert.Single(command.Path!.Figures).Segments));
        Assert.Equal(SweepDirection.Counterclockwise, arc.SweepDirection);
        Assert.NotEqual(System.Numerics.Matrix4x4.Identity, command.Transform);
    }

    [Fact]
    public void LegacyPolyline3DRetainsWcsPointsAndExactZBounds()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add legacy 3D polyline", document =>
        {
            var polyline = new Polyline3D(
                [
                    new XYZ(-2, 3, -7),
                    new XYZ(5, 11, 13),
                    new XYZ(9, -4, 2),
                ],
                isClosed: true);
            document.Entities.Add(polyline);
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadPolyline3DPrimitive polyline = Assert.Single(snapshot.Polylines3D.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(CadEntityKind.Polyline3D, Assert.Single(snapshot.Entities.ToArray()).Kind);
        Assert.Equal(3, polyline.PointCount);
        Assert.True(polyline.IsClosed);
        Assert.Equal(3, snapshot.Polyline3DPoints.Length);
        AssertPoint(new CadPoint3D(-2, -4, -7), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(9, 11, 13), snapshot.Bounds.Max);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        PathFigure figure = Assert.Single(command.Path!.Figures);
        Assert.True(figure.IsClosed);
        Assert.Equal(2, figure.Segments.Count);
    }

    [Fact]
    public void WidePolylineIsReportedInsteadOfMisclassifiedAsLineweight()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add wide polyline", document =>
        {
            var polyline = new LwPolyline { ConstantWidth = 2 };
            polyline.Vertices.Add(new LwPolyline.Vertex(0, 0));
            polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
            document.Entities.Add(polyline);
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic => diagnostic.Code == "CADSNAP003");
    }

    [Fact]
    public void TextShapesOnceIntoRetainedFontRunsAndConservativeAffineBounds()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add TrueType text", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("office")
            {
                Style = textStyle,
                InsertPoint = new XYZ(10, 20, 0),
                Height = 10,
                WidthFactor = 1.2,
            });
        });

        TtfFont font = InterFontFamily.Regular;
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(font),
            });
        CadTextPrimitive text = Assert.Single(snapshot.Texts.ToArray());
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(CadEntityKind.Text, entity.Kind);
        AssertPoint(new CadPoint3D(10, 20, 0), text.Origin);
        AssertPoint(new CadPoint3D(12, 0, 0), text.XAxis);
        AssertPoint(new CadPoint3D(0, -10, 0), text.YAxis);
        Assert.InRange(text.GlyphCount, 1, 6);
        Assert.Equal(text.GlyphCount, snapshot.TextGlyphIndices.Length);
        Assert.Equal(text.GlyphCount, snapshot.TextGlyphPositions.Length);
        Assert.Equal(1, text.RunCount);
        Assert.Same(font, Assert.Single(snapshot.TextFonts.ToArray()));
        Assert.False(entity.Bounds.IsEmpty);
        Assert.True(entity.Bounds.Min.X < entity.Bounds.Max.X);
        Assert.True(entity.Bounds.Min.Y < 20);
        Assert.True(entity.Bounds.Max.Y > 20);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawGlyphRun, command.Type);
        Assert.Equal(text.GlyphCount, command.GlyphRangeCount);
        Assert.True(command.UseVectorGlyphRendering);
        Assert.Equal(12, command.Transform.M11, 5);
        Assert.Equal(-10, command.Transform.M22, 5);
        using GpuPicture picture = scene.CreatePicture();
        RenderCommand retainedCommand = picture.GetCommand(0);
        Assert.Same(command.GlyphIndices, retainedCommand.GlyphIndices);
        Assert.Same(command.GlyphPositions, retainedCommand.GlyphPositions);
        Assert.Equal(text.GlyphCount, retainedCommand.GlyphRangeCount);
        Assert.True(retainedCommand.UseVectorGlyphRendering);
    }

    [Fact]
    public void TextTopCenterAlignmentUsesTheSecondOcsPoint()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add aligned text", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("CAD")
            {
                Style = textStyle,
                InsertPoint = new XYZ(100, 200, 0),
                AlignmentPoint = new XYZ(5, 6, 0),
                HorizontalAlignment = TextHorizontalAlignment.Center,
                VerticalAlignment = TextVerticalAlignmentType.Top,
                Height = 2,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });
        CadTextPrimitive text = Assert.Single(snapshot.Texts.ToArray());
        Vector2 firstGlyph = snapshot.TextGlyphPositions.Span[text.GlyphOffset];

        AssertPoint(new CadPoint3D(5, 6, 0), text.Origin);
        Assert.True(firstGlyph.X < 0);
        Assert.True(firstGlyph.Y > 0);
        Assert.InRange(Math.Abs(snapshot.Bounds.Max.Y - 6), 0, Tolerance);
    }

    [Fact]
    public void AlignedAndFitTextUseTheirTwoOcsBaselinePoints()
    {
        TtfFont font = InterFontFamily.Regular;
        double advance = new TextLayout(
            "CAD",
            font,
            1.0f,
            float.PositiveInfinity).ContentSize.X;
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add two-point text", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("CAD")
            {
                Style = textStyle,
                InsertPoint = new XYZ(2, 3, 0),
                AlignmentPoint = new XYZ(22, 3, 0),
                HorizontalAlignment = TextHorizontalAlignment.Aligned,
                Height = 4,
                WidthFactor = 0.8,
            });
            document.Entities.Add(new TextEntity("CAD")
            {
                Style = textStyle,
                InsertPoint = new XYZ(1, 10, 0),
                AlignmentPoint = new XYZ(1, 30, 0),
                HorizontalAlignment = TextHorizontalAlignment.Fit,
                Height = 5,
                WidthFactor = 0.2,
            });
            document.Entities.Add(new TextEntity("CAD")
            {
                Style = textStyle,
                InsertPoint = new XYZ(40, 0, 0),
                AlignmentPoint = new XYZ(50, 0, 0),
                HorizontalAlignment = TextHorizontalAlignment.Fit,
                Height = 2,
                Mirror = TextMirrorFlag.Backward,
            });
            document.Entities.Add(new TextEntity("CAD")
            {
                Style = textStyle,
                InsertPoint = new XYZ(0, 40, 0),
                AlignmentPoint = new XYZ(10, 40, 0),
                HorizontalAlignment = TextHorizontalAlignment.Aligned,
                VerticalAlignment = TextVerticalAlignmentType.Top,
            });
            document.Entities.Add(new TextEntity("CAD")
            {
                Style = textStyle,
                InsertPoint = new XYZ(0, 50, 0),
                AlignmentPoint = new XYZ(10, 50, 1),
                HorizontalAlignment = TextHorizontalAlignment.Fit,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(font),
            });
        CadTextPrimitive[] texts = snapshot.Texts.ToArray();

        Assert.Equal(3, texts.Length);
        AssertPoint(new CadPoint3D(2, 3, 0), texts[0].Origin);
        AssertPoint(new CadPoint3D(20, 0, 0), texts[0].XAxis * advance);
        Assert.InRange(
            Math.Abs((texts[0].XAxis.Length / texts[0].YAxis.Length) - 0.8),
            0,
            Tolerance);
        AssertPoint(new CadPoint3D(1, 10, 0), texts[1].Origin);
        AssertPoint(new CadPoint3D(0, 20, 0), texts[1].XAxis * advance);
        Assert.InRange(Math.Abs(texts[1].YAxis.Length - 5), 0, Tolerance);
        AssertPoint(new CadPoint3D(50, 0, 0), texts[2].Origin);
        AssertPoint(new CadPoint3D(-10, 0, 0), texts[2].XAxis * advance);
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Message.Contains("baseline", StringComparison.Ordinal));
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Message.Contains("coplanar", StringComparison.Ordinal));
    }

    [Fact]
    public void AlignedTextInsideNonUniformBlockTransformsBothEndpoints()
    {
        TtfFont font = InterFontFamily.Regular;
        double advance = new TextLayout(
            "CAD",
            font,
            1.0f,
            float.PositiveInfinity).ContentSize.X;
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong rootHandle = 0;
        session.Edit("Add block-aligned text", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            var block = new BlockRecord("ALIGNED_LABEL");
            block.Entities.Add(new TextEntity("CAD")
            {
                Style = textStyle,
                InsertPoint = XYZ.Zero,
                AlignmentPoint = new XYZ(10, 0, 0),
                HorizontalAlignment = TextHorizontalAlignment.Aligned,
            });
            var insert = new Insert(block)
            {
                InsertPoint = new XYZ(5, 7, 0),
                XScale = 2,
                YScale = 3,
                Rotation = Math.PI / 2,
            };
            document.Entities.Add(insert);
            rootHandle = insert.Handle;
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(font),
            });
        CadTextPrimitive text = Assert.Single(snapshot.Texts.ToArray());

        Assert.Equal(rootHandle, Assert.Single(snapshot.Entities.ToArray()).Handle);
        AssertPoint(new CadPoint3D(5, 7, 0), text.Origin);
        AssertPoint(new CadPoint3D(0, 20, 0), text.XAxis * advance);
    }

    [Fact]
    public void TextInsideBlockRetainsRootHandleAndComposesItsGlyphBasis()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong rootHandle = 0;
        session.Edit("Add block text", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            var block = new BlockRecord("LABEL");
            block.Entities.Add(new TextEntity("A") { Style = textStyle });
            var insert = new Insert(block)
            {
                InsertPoint = new XYZ(10, 20, 0),
                XScale = 2,
                YScale = 3,
                Rotation = Math.PI / 2,
            };
            document.Entities.Add(insert);
            rootHandle = insert.Handle;
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });
        CadTextPrimitive text = Assert.Single(snapshot.Texts.ToArray());

        Assert.Equal(rootHandle, Assert.Single(snapshot.Entities.ToArray()).Handle);
        AssertPoint(new CadPoint3D(10, 20, 0), text.Origin);
        AssertPoint(new CadPoint3D(0, 2, 0), text.XAxis);
        AssertPoint(new CadPoint3D(3, 0, 0), text.YAxis);
    }

    [Fact]
    public void TextEntityTransformIsNotAppliedAgainFromItsCreationStyle()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add transformed text", document =>
        {
            var textStyle = new TextStyle("INTER")
            {
                Filename = "Inter.ttf",
                Width = 0.5,
                ObliqueAngle = 0.1,
                MirrorFlag = TextMirrorFlag.Backward,
            };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("CAD")
            {
                Style = textStyle,
                Height = 4,
                WidthFactor = 2,
                ObliqueAngle = 0.2,
                Mirror = TextMirrorFlag.UpsideDown,
            });
        });

        CadTextPrimitive text = Assert.Single(new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            }).Texts.ToArray());

        AssertPoint(new CadPoint3D(8, 0, 0), text.XAxis);
        AssertPoint(new CadPoint3D(4 * Math.Tan(0.2), 4, 0), text.YAxis);
    }

    [Fact]
    public void TrueTypeFontSubstitutionIsDiagnosed()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add substituted text", document =>
        {
            var textStyle = new TextStyle("MISSING") { Filename = "missing.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("CAD") { Style = textStyle });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(
                    InterFontFamily.Regular,
                    isSubstitution: true),
            });

        Assert.Single(snapshot.Entities.ToArray());
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Code == "CADSNAP005");
    }

    [Fact]
    public void FontManagerResolverUsesExplicitFallbackForMissingFamily()
    {
        TtfFont fallback = InterFontFamily.Regular;
        var resolver = new CadFontManagerTextResolver(fallback, new FontManager());

        CadTextFontResolution resolution = resolver.Resolve(new CadTextFontRequest(
            "MISSING_CAD_FACE",
            "missing-cad-face.ttf",
            string.Empty,
            IsBold: false,
            IsItalic: false));

        Assert.Same(fallback, resolution.Font);
        Assert.True(resolution.IsSubstitution);
    }

    [Fact]
    public void TrueTypeTextWithoutHostResolverIsAnExplicitFidelityGate()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add unresolved text", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("CAD") { Style = textStyle });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Code == "CADSNAP003" &&
                diagnostic.Message.Contains("resolver", StringComparison.Ordinal));
    }

    [Fact]
    public void ShxTextIsDiagnosedWithoutTrueTypeSubstitution()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add SHX text", document =>
        {
            var textStyle = new TextStyle("SIMPLEX") { Filename = "simplex.shx" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("CAD") { Style = textStyle });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Message.Contains("SHX", StringComparison.Ordinal));
    }

    [Fact]
    public void StandardShxTextRetainsSharedAnalyticGlyphPaths()
    {
        CadShxGlyphCache cache = CreateShxCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add standard SHX text", document =>
        {
            var textStyle = new TextStyle("TESTSHX") { Filename = "test.shx" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("A A")
            {
                Style = textStyle,
                InsertPoint = new XYZ(100, 200, 0),
                Height = 10,
                WidthFactor = 2,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                ShxFontResolver = new FixedShxFontResolver(cache),
            });
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadShxTextPrimitive text = Assert.Single(snapshot.ShxTexts.ToArray());
        CadShxGlyphInstance[] glyphs = snapshot.ShxGlyphInstances.ToArray();
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();

        Assert.Equal(CadEntityKind.ShxText, entity.Kind);
        Assert.Empty(snapshot.Texts.ToArray());
        Assert.Equal(3, glyphs.Length);
        Assert.Equal(2, cache.Count);
        AssertPoint(new CadPoint3D(2, 0, 0), text.XAxis);
        AssertPoint(new CadPoint3D(0, 1, 0), text.YAxis);
        AssertPoint(new CadPoint3D(100, 198, 0), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(240, 210, 0), entity.Bounds.Max);
        Assert.Equal(2, commands.Length);
        Assert.All(commands, command => Assert.Equal(RenderCommandType.DrawPath, command.Type));
        Assert.Same(commands[0].Path, commands[1].Path);
        Assert.InRange(Math.Abs((commands[1].Transform.M41 - commands[0].Transform.M41) - 80.0f), 0, 1e-5f);
    }

    [Fact]
    public void StandardShxAlignAndFitPreserveEndpointAndHeightContracts()
    {
        CadShxGlyphCache cache = CreateShxCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add aligned SHX text", document =>
        {
            var textStyle = new TextStyle("TESTSHX") { Filename = "test.shx" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("A")
            {
                Style = textStyle,
                InsertPoint = XYZ.Zero,
                AlignmentPoint = new XYZ(90, 0, 0),
                Height = 10,
                WidthFactor = 1.5,
                HorizontalAlignment = TextHorizontalAlignment.Aligned,
            });
            document.Entities.Add(new TextEntity("A")
            {
                Style = textStyle,
                InsertPoint = new XYZ(0, 30, 0),
                AlignmentPoint = new XYZ(90, 30, 0),
                Height = 10,
                WidthFactor = 1.5,
                HorizontalAlignment = TextHorizontalAlignment.Fit,
            });
        });

        CadShxTextPrimitive[] texts = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                ShxFontResolver = new FixedShxFontResolver(cache),
            }).ShxTexts.ToArray();

        Assert.Equal(2, texts.Length);
        AssertPoint(new CadPoint3D(3, 0, 0), texts[0].XAxis);
        AssertPoint(new CadPoint3D(0, 2, 0), texts[0].YAxis);
        AssertPoint(new CadPoint3D(3, 0, 0), texts[1].XAxis);
        AssertPoint(new CadPoint3D(0, 1, 0), texts[1].YAxis);
    }

    [Fact]
    public void StandardShxTextInsideNonUniformBlockComposesItsRetainedBasis()
    {
        CadShxGlyphCache cache = CreateShxCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong rootHandle = 0;
        session.Edit("Add block SHX text", document =>
        {
            var textStyle = new TextStyle("TESTSHX") { Filename = "test.shx" };
            document.TextStyles.Add(textStyle);
            var block = new BlockRecord("SHX_LABEL");
            block.Entities.Add(new TextEntity("A")
            {
                Style = textStyle,
                Height = 10,
            });
            var insert = new Insert(block)
            {
                InsertPoint = new XYZ(10, 20, 0),
                XScale = 2,
                YScale = 3,
                Rotation = Math.PI / 2,
            };
            document.Entities.Add(insert);
            rootHandle = insert.Handle;
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                ShxFontResolver = new FixedShxFontResolver(cache),
            });
        CadShxTextPrimitive text = Assert.Single(snapshot.ShxTexts.ToArray());

        Assert.Equal(rootHandle, Assert.Single(snapshot.Entities.ToArray()).Handle);
        AssertPoint(new CadPoint3D(10, 20, 0), text.Origin);
        AssertPoint(new CadPoint3D(0, 2, 0), text.XAxis);
        AssertPoint(new CadPoint3D(-3, 0, 0), text.YAxis);
    }

    [Fact]
    public void StandardShxDecorationsCoalesceIntoRetainedStrokeSegments()
    {
        CadShxGlyphCache cache = CreateShxCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add decorated SHX text", document =>
        {
            var textStyle = new TextStyle("TESTSHX") { Filename = "test.shx" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("%%uA %%u%%oA%%o%%kA%%k")
            {
                Style = textStyle,
                Height = 10,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                ShxFontResolver = new FixedShxFontResolver(cache),
            });
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadShxTextPrimitive text = Assert.Single(snapshot.ShxTexts.ToArray());
        CadShxDecorationSegment[] decorations = snapshot.ShxDecorationSegments.ToArray();
        RenderCommand[] commands = new CadPlanSceneCompiler()
            .Compile(snapshot)
            .DrawingContext.Commands.ToArray();

        Assert.Equal(4, text.GlyphCount);
        Assert.Equal(0, text.DecorationOffset);
        Assert.Equal(3, text.DecorationCount);
        Assert.Equal(new CadShxDecorationSegment(0, -2, 40, -2), decorations[0]);
        Assert.Equal(new CadShxDecorationSegment(40, 10, 70, 10), decorations[1]);
        Assert.Equal(new CadShxDecorationSegment(70, 4, 100, 4), decorations[2]);
        AssertPoint(new CadPoint3D(0, -2, 0), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(100, 10, 0), entity.Bounds.Max);
        Assert.Equal(3, commands.Count(command => command.Type == RenderCommandType.DrawPath));
        Assert.Equal(3, commands.Count(command => command.Type == RenderCommandType.DrawLine));
        Assert.All(commands[^3..], command => Assert.Same(commands[0].Pen, command.Pen));
    }

    [Fact]
    public void DualOrientationShxTextUsesTopCenterAndDownwardAuthoredAdvance()
    {
        CadShxGlyphCache cache = CreateDualOrientationShxCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add vertical SHX text", document =>
        {
            var textStyle = new TextStyle("VERTICALSHX")
            {
                Filename = "vertical.shx",
                Flags = StyleFlags.VerticalText,
            };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("AA")
            {
                Style = textStyle,
                InsertPoint = new XYZ(100, 200, 0),
                Height = 10,
                WidthFactor = 2,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                ShxFontResolver = new FixedShxFontResolver(cache),
            });
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadShxTextPrimitive text = Assert.Single(snapshot.ShxTexts.ToArray());
        CadShxGlyphInstance[] glyphs = snapshot.ShxGlyphInstances.ToArray();
        RenderCommand[] commands = new CadPlanSceneCompiler()
            .Compile(snapshot)
            .DrawingContext.Commands.ToArray();

        AssertPoint(new CadPoint3D(100, 200, 0), text.Origin);
        AssertPoint(new CadPoint3D(2, 0, 0), text.XAxis);
        AssertPoint(new CadPoint3D(0, 1, 0), text.YAxis);
        Assert.Equal(0, glyphs[0].Y);
        Assert.Equal(-1, glyphs[1].Y);
        AssertPoint(new CadPoint3D(98, 198, 0), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(100, 203, 0), entity.Bounds.Max);
        Assert.Equal(2, commands.Length);
        Assert.All(commands, command => Assert.Equal(RenderCommandType.DrawPath, command.Type));
        Assert.InRange(Math.Abs((commands[1].Transform.M42 - commands[0].Transform.M42) + 1.0f), 0, 1e-5f);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void VerticalShxUnverifiedJustificationAndDecorationRemainExplicit()
    {
        CadShxGlyphCache cache = CreateDualOrientationShxCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add gated vertical SHX text", document =>
        {
            var textStyle = new TextStyle("VERTICALSHX")
            {
                Filename = "vertical.shx",
                Flags = StyleFlags.VerticalText,
            };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("A")
            {
                Style = textStyle,
                HorizontalAlignment = TextHorizontalAlignment.Center,
            });
            document.Entities.Add(new TextEntity("%%uA") { Style = textStyle });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                ShxFontResolver = new FixedShxFontResolver(cache),
            });

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(2, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("non-default justification", StringComparison.Ordinal));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("vertical decoration", StringComparison.Ordinal));
    }

    [Fact]
    public void TrueTypeAndShxTextShareOneDocumentGlyphBudget()
    {
        CadShxGlyphCache cache = CreateShxCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add mixed text", document =>
        {
            var trueTypeStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            var shxStyle = new TextStyle("TESTSHX") { Filename = "test.shx" };
            document.TextStyles.Add(trueTypeStyle);
            document.TextStyles.Add(shxStyle);
            document.Entities.Add(new TextEntity("A") { Style = trueTypeStyle });
            document.Entities.Add(new TextEntity("AA") { Style = shxStyle });
        });

        InvalidOperationException exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadSnapshotCompiler().Compile(
                session,
                new CadSnapshotOptions
                {
                    TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
                    ShxFontResolver = new FixedShxFontResolver(cache),
                    MaxTextGlyphs = 2,
                }));

        Assert.Contains("document limit of 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedShxContainersAndOrientationRemainExplicitAndSubstitutionIsDiagnosed()
    {
        CadShxGlyphCache cache = CreateShxCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add gated SHX text", document =>
        {
            var decorated = new TextStyle("DECORATED") { Filename = "test.shx" };
            var big = new TextStyle("BIG")
            {
                Filename = "test.shx",
                BigFontFilename = "bigfont.shx",
            };
            var vertical = new TextStyle("VERTICALSHX")
            {
                Filename = "test.shx",
                Flags = StyleFlags.VerticalText,
            };
            document.TextStyles.Add(decorated);
            document.TextStyles.Add(big);
            document.TextStyles.Add(vertical);
            document.Entities.Add(new TextEntity("%%uA") { Style = decorated });
            document.Entities.Add(new TextEntity("A") { Style = big });
            document.Entities.Add(new TextEntity("A") { Style = vertical });
            document.Entities.Add(new TextEntity("A")
            {
                Style = decorated,
                InsertPoint = new XYZ(0, 20, 0),
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                ShxFontResolver = new FixedShxFontResolver(cache, isSubstitution: true),
            });

        Assert.Equal(2, snapshot.Entities.Length);
        Assert.Equal(2, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Single(snapshot.ShxDecorationSegments.ToArray());
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic => diagnostic.Code == "CADSNAP006");
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("Big Font", StringComparison.Ordinal));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("Vertical SHX", StringComparison.Ordinal));
    }

    [Fact]
    public void VerticalStyleIsGatedWhileHorizontalMTextIsRetained()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add unsupported text modes", document =>
        {
            var verticalStyle = new TextStyle("VERTICAL")
            {
                Filename = "Inter.ttf",
                Flags = StyleFlags.VerticalText,
            };
            document.TextStyles.Add(verticalStyle);
            document.Entities.Add(new TextEntity("CAD") { Style = verticalStyle });
            document.Entities.Add(new MText { Value = "CAD" });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });

        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        Assert.Equal(CadEntityKind.MText, entity.Kind);
        Assert.Single(snapshot.MTexts.ToArray());
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Message.Contains("Vertical", StringComparison.Ordinal));
    }

    [Fact]
    public void TextInputBudgetsFailAtomicallyBeforeRetainedStreamsChange()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add oversized text", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("CAD") { Style = textStyle });
        });

        InvalidOperationException codeUnitException = Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadSnapshotCompiler().Compile(
                session,
                new CadSnapshotOptions
                {
                    TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
                    MaxTextCodeUnitsPerEntity = 2,
                }));
        InvalidOperationException glyphException = Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadSnapshotCompiler().Compile(
                session,
                new CadSnapshotOptions
                {
                    TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
                    MaxTextGlyphs = 2,
                }));

        Assert.Contains("UTF-16 code units", codeUnitException.Message, StringComparison.Ordinal);
        Assert.Contains("glyph count", glyphException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TextDecodesDocumentedSymbols()
    {
        TtfFont font = InterFontFamily.Regular;
        CadDocumentSession supported = CadDocumentSession.CreateNew();
        supported.Edit("Add encoded symbols", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity(@"45%%d \U+00B1 %%% %%065%%066%%067") { Style = textStyle });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            supported,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(font),
            });
        var expected = new TextLayout("45° ± % ABC", font, 1.0f, float.PositiveInfinity);

        Assert.Equal(
            expected.Glyphs.Select(glyph => glyph.GlyphIndex),
            snapshot.TextGlyphIndices.ToArray());
    }

    [Fact]
    public void MalformedNumericTextControlsAreExplicitFidelityGates()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add malformed numeric controls", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("%%12") { Style = textStyle });
            document.Entities.Add(new TextEntity("%%1A3") { Style = textStyle });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(2, snapshot.Statistics.UnsupportedEntityCount);
        Assert.All(
            snapshot.Diagnostics.ToArray(),
            diagnostic => Assert.Contains("three decimal digits", diagnostic.Message));
    }

    [Fact]
    public void TextRetainsOverlineUnderlineAndStrikeThroughRuns()
    {
        TtfFont font = InterFontFamily.Regular;
        const string encoded = "%%uA%%oB%%uC%%o %%kEF%%k";
        const string decoded = "ABC EF";

        CadDocumentSession decorated = CadDocumentSession.CreateNew();
        decorated.Edit("Add decorated text", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity(encoded)
            {
                Style = textStyle,
                Height = 3,
                WidthFactor = 1.25,
                ObliqueAngle = 0.1,
            });
        });
        CadDocumentSnapshot decoratedSnapshot = new CadSnapshotCompiler().Compile(
            decorated,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(font),
            });
        CadTextPrimitive text = Assert.Single(decoratedSnapshot.Texts.ToArray());
        CadEntityHeader entity = Assert.Single(decoratedSnapshot.Entities.ToArray());
        CadTextDecoration[] decorations = decoratedSnapshot.TextDecorations.ToArray();
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(decoratedSnapshot);
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();
        var expected = new TextLayout(decoded, font, 1.0f, float.PositiveInfinity);

        Assert.Equal(
            expected.Glyphs.Select(glyph => glyph.GlyphIndex),
            decoratedSnapshot.TextGlyphIndices.ToArray());
        Assert.Equal(3, decorations.Length);
        Assert.Equal(0, text.DecorationOffset);
        Assert.Equal(3, text.DecorationCount);
        Assert.All(decorations, decoration =>
        {
            Assert.True(decoration.Width > 0);
            Assert.True(decoration.Height > 0);
        });
        Assert.InRange(
            Math.Abs(decorations[0].Y + ((double)font.UnderlinePosition!.Value / font.UnitsPerEm)),
            0,
            Tolerance);
        Assert.InRange(
            Math.Abs(decorations[1].Y + ((double)font.Ascender / font.UnitsPerEm)),
            0,
            Tolerance);
        Assert.InRange(
            Math.Abs(decorations[2].Y + ((double)font.StrikeoutPosition!.Value / font.UnitsPerEm)),
            0,
            Tolerance);
        Assert.Equal(4, commands.Length);
        Assert.Equal(RenderCommandType.DrawGlyphRun, commands[0].Type);
        Assert.All(commands.Skip(1), command => Assert.Equal(RenderCommandType.DrawRect, command.Type));
        Assert.Equal(3.75f, commands[1].Transform.M11, 5);
        Assert.Equal(-3.0f, commands[1].Transform.M22, 5);
        foreach (CadTextDecoration decoration in decorations)
        {
            AssertDecorationCorner(decoration.X, decoration.Y);
            AssertDecorationCorner(decoration.X + decoration.Width, decoration.Y);
            AssertDecorationCorner(decoration.X, decoration.Y + decoration.Height);
            AssertDecorationCorner(
                decoration.X + decoration.Width,
                decoration.Y + decoration.Height);
        }

        void AssertDecorationCorner(double x, double y)
        {
            CadPoint3D point = text.Origin + (text.XAxis * x) + (text.YAxis * y);
            Assert.InRange(point.X, entity.Bounds.Min.X - Tolerance, entity.Bounds.Max.X + Tolerance);
            Assert.InRange(point.Y, entity.Bounds.Min.Y - Tolerance, entity.Bounds.Max.Y + Tolerance);
            Assert.InRange(point.Z, entity.Bounds.Min.Z - Tolerance, entity.Bounds.Max.Z + Tolerance);
        }
    }

    [Fact]
    public void TextDecorationTogglesAutoCloseAndRejectClusterSplits()
    {
        TtfFont font = InterFontFamily.Regular;
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add decoration boundaries", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("%%uCAD") { Style = textStyle });
            document.Entities.Add(new TextEntity("a%%u\u0301%%u")
            {
                Style = textStyle,
                InsertPoint = new XYZ(0, 10, 0),
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(font),
            });

        Assert.Single(snapshot.Entities.ToArray());
        Assert.Single(snapshot.TextDecorations.ToArray());
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Message.Contains("cluster", StringComparison.Ordinal));
    }

    [Fact]
    public void InsertAppliesBasePointScaleRotationAndKeepsRootHandle()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong insertHandle = 0;
        session.Edit("Add transformed block", document =>
        {
            var block = new BlockRecord("TRANSFORMED");
            block.BlockEntity.BasePoint = new XYZ(10, 5, 0);
            block.Entities.Add(new Line(new XYZ(10, 5, 0), new XYZ(14, 7, 0)));
            var insert = new Insert(block)
            {
                InsertPoint = new XYZ(100, 200, 3),
                XScale = 2,
                YScale = 3,
                Rotation = Math.PI / 2,
            };
            document.Entities.Add(insert);
            insertHandle = insert.Handle;
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadLinePrimitive line = Assert.Single(snapshot.Lines.ToArray());
        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());

        AssertPoint(new CadPoint3D(100, 200, 3), line.Start);
        AssertPoint(new CadPoint3D(94, 208, 3), line.End);
        Assert.Equal(insertHandle, header.Handle);
        Assert.Equal(1, snapshot.Statistics.SourceEntityCount);
        Assert.Equal(2, snapshot.Statistics.ExpandedEntityCount);
    }

    [Fact]
    public void NestedInsertCompositionRetainsAnalyticCircleUnderNonUniformScale()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add nested blocks", document =>
        {
            var symbol = new BlockRecord("SYMBOL");
            symbol.Entities.Add(new Circle(XYZ.Zero, 1));

            var assembly = new BlockRecord("ASSEMBLY");
            assembly.Entities.Add(new Insert(symbol)
            {
                InsertPoint = new XYZ(5, 0, 0),
            });

            document.Entities.Add(new Insert(assembly)
            {
                InsertPoint = new XYZ(100, 20, 0),
                XScale = 2,
                YScale = 3,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadCirclePrimitive circle = Assert.Single(snapshot.Circles.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        AssertPoint(new CadPoint3D(110, 20, 0), circle.Center);
        AssertPoint(new CadPoint3D(108, 17, 0), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(112, 23, 0), snapshot.Bounds.Max);
        Assert.Equal(3, snapshot.Statistics.ExpandedEntityCount);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawEllipse, command.Type);
        Assert.Equal(1, command.RadiusX);
        Assert.Equal(1, command.RadiusY);
        Assert.Equal(2, command.Transform.M11, 5);
        Assert.Equal(3, command.Transform.M22, 5);
    }

    [Fact]
    public void LayerZeroAndByBlockStyleInheritFromInsert()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add styled block", document =>
        {
            var block = new BlockRecord("STYLED");
            block.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX)
            {
                Color = ACadSharp.Color.ByBlock,
                LineWeight = LineWeightType.ByBlock,
                LineType = LineType.ByBlock,
                Transparency = Transparency.ByBlock,
            });
            var insertLayer = new Layer("INSERTS")
            {
                Color = ACadSharp.Color.Red,
            };
            var insertLineType = new LineType("INSERT_DASH");
            document.Layers.Add(insertLayer);
            document.LineTypes.Add(insertLineType);
            document.Entities.Add(new Insert(block)
            {
                Layer = insertLayer,
                Color = ACadSharp.Color.Green,
                LineWeight = LineWeightType.W50,
                LineType = insertLineType,
                Transparency = new Transparency(25),
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());
        CadLayerSnapshot layer = snapshot.Layers.Span[header.LayerIndex];
        CadStrokeStyle style = snapshot.Styles.Span[header.StyleIndex];

        Assert.Equal("INSERTS", layer.Name);
        Assert.Equal((byte)0, style.Red);
        Assert.Equal(byte.MaxValue, style.Green);
        Assert.Equal((byte)0, style.Blue);
        Assert.Equal((byte)191, style.Alpha);
        Assert.Equal(0.5, style.LineWeightMillimeters);
        Assert.Equal("INSERT_DASH", style.LineTypeName);
    }

    [Fact]
    public void InsertNormalMapsBlockAxesIntoWorldCoordinatesWithoutMovingWcsPosition()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add non-world insert", document =>
        {
            var block = new BlockRecord("NON_WORLD");
            block.Entities.Add(new Line(XYZ.Zero, new XYZ(1, 1, 1)));
            document.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(10, 20, 30),
                Normal = XYZ.AxisY,
            });
        });

        CadLinePrimitive line = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Lines.ToArray());

        AssertPoint(new CadPoint3D(10, 20, 30), line.Start);
        AssertPoint(new CadPoint3D(9, 21, 31), line.End);
    }

    [Fact]
    public void InsertNestingDepthIsBoundedAndReported()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add nested insert", document =>
        {
            var leaf = new BlockRecord("LEAF");
            leaf.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
            var outer = new BlockRecord("OUTER");
            outer.Entities.Add(new Insert(leaf));
            document.Entities.Add(new Insert(outer));
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { MaxBlockNestingDepth = 1 });

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Message.Contains("nesting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MInsertRotatesArraySpacingWithoutScalingItAndKeepsRootHandle()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong insertHandle = 0;
        session.Edit("Add block array", document =>
        {
            var block = new BlockRecord("ARRAY_ITEM");
            block.BlockEntity.BasePoint = new XYZ(1, 2, 0);
            block.Entities.Add(new Line(new XYZ(1, 2, 0), new XYZ(2, 3, 0)));
            var insert = new Insert(block)
            {
                InsertPoint = new XYZ(100, 200, 0),
                XScale = 2,
                YScale = 3,
                Rotation = Math.PI / 2,
                ColumnCount = 3,
                ColumnSpacing = 10,
                RowCount = 2,
                RowSpacing = 20,
            };
            document.Entities.Add(insert);
            insertHandle = insert.Handle;
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadLinePrimitive[] lines = snapshot.Lines.ToArray();

        Assert.Equal(6, lines.Length);
        AssertPoint(new CadPoint3D(100, 200, 0), lines[0].Start);
        AssertPoint(new CadPoint3D(97, 202, 0), lines[0].End);
        AssertPoint(new CadPoint3D(100, 210, 0), lines[1].Start);
        AssertPoint(new CadPoint3D(100, 220, 0), lines[2].Start);
        AssertPoint(new CadPoint3D(80, 200, 0), lines[3].Start);
        AssertPoint(new CadPoint3D(80, 220, 0), lines[5].Start);
        Assert.All(snapshot.Entities.ToArray(), entity => Assert.Equal(insertHandle, entity.Handle));
        AssertPoint(new CadPoint3D(77, 200, 0), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(100, 222, 0), snapshot.Bounds.Max);
        Assert.Equal(7, snapshot.Statistics.ExpandedEntityCount);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
    }

    [Fact]
    public void NestedMInsertComposesItsArrayPlaneThroughTheParentInsert()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong rootHandle = 0;
        session.Edit("Add nested block array", document =>
        {
            var item = new BlockRecord("ITEM");
            item.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));

            var assembly = new BlockRecord("ASSEMBLY");
            assembly.Entities.Add(new Insert(item)
            {
                ColumnCount = 2,
                ColumnSpacing = 3,
                RowCount = 2,
                RowSpacing = 4,
            });

            var root = new Insert(assembly)
            {
                InsertPoint = new XYZ(10, 20, 0),
                XScale = 2,
                YScale = 3,
                Rotation = Math.PI / 2,
            };
            document.Entities.Add(root);
            rootHandle = root.Handle;
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadLinePrimitive[] lines = snapshot.Lines.ToArray();

        Assert.Equal(4, lines.Length);
        AssertPoint(new CadPoint3D(10, 20, 0), lines[0].Start);
        AssertPoint(new CadPoint3D(10, 22, 0), lines[0].End);
        AssertPoint(new CadPoint3D(10, 26, 0), lines[1].Start);
        AssertPoint(new CadPoint3D(-2, 20, 0), lines[2].Start);
        AssertPoint(new CadPoint3D(-2, 26, 0), lines[3].Start);
        Assert.All(snapshot.Entities.ToArray(), entity => Assert.Equal(rootHandle, entity.Handle));
        Assert.Equal(6, snapshot.Statistics.ExpandedEntityCount);
    }

    [Fact]
    public void MInsertSpacingUsesTheInsertionOcsPlane()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add non-world block array", document =>
        {
            var block = new BlockRecord("ARRAY_ITEM");
            block.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
            document.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(10, 20, 30),
                Normal = XYZ.AxisY,
                ColumnCount = 2,
                ColumnSpacing = 5,
            });
        });

        CadLinePrimitive[] lines = new CadSnapshotCompiler()
            .Compile(session)
            .Lines
            .ToArray();

        Assert.Equal(2, lines.Length);
        AssertPoint(new CadPoint3D(10, 20, 30), lines[0].Start);
        AssertPoint(new CadPoint3D(9, 20, 30), lines[0].End);
        AssertPoint(new CadPoint3D(5, 20, 30), lines[1].Start);
        AssertPoint(new CadPoint3D(4, 20, 30), lines[1].End);
    }

    [Fact]
    public void MInsertInstanceLimitRejectsTheArrayBeforeEmittingGeometry()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add oversized block array", document =>
        {
            var block = new BlockRecord("ARRAY_ITEM");
            block.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
            document.Entities.Add(new Insert(block)
            {
                ColumnCount = 3,
                RowCount = 2,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { MaxBlockArrayInstances = 5 });

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(1, snapshot.Statistics.ExpandedEntityCount);
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Message.Contains("instance count 6", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidMInsertCountsAndSpacingAreRejected()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add invalid block arrays", document =>
        {
            var block = new BlockRecord("ARRAY_ITEM");
            block.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
            document.Entities.Add(new Insert(block) { ColumnCount = 0 });
            document.Entities.Add(new Insert(block)
            {
                ColumnCount = 2,
                ColumnSpacing = double.NaN,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(2, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Message.Contains("counts", StringComparison.Ordinal));
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Message.Contains("spacing", StringComparison.Ordinal));
    }

    [Fact]
    public void RecursiveInsertCycleIsDiagnosedWithoutEmittingPartialGeometry()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add recursive block", document =>
        {
            var block = new BlockRecord("RECURSIVE");
            block.Entities.Add(new Insert(block));
            document.Entities.Add(new Insert(block));
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(
            snapshot.Diagnostics.ToArray(),
            diagnostic => diagnostic.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExpandedEntityLimitFailsTheSnapshotInsteadOfReturningPartialGeometry()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add oversized block", document =>
        {
            var block = new BlockRecord("OVERSIZED");
            block.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
            block.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 1)));
            document.Entities.Add(new Insert(block));
        });

        InvalidOperationException exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadSnapshotCompiler().Compile(
                session,
                new CadSnapshotOptions { MaxExpandedEntities = 2 }));

        Assert.Contains("limit of 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedEntityLimitFailsMidArrayInsteadOfReturningPartialGeometry()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add budgeted block array", document =>
        {
            var block = new BlockRecord("ARRAY_ITEM");
            block.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
            document.Entities.Add(new Insert(block) { ColumnCount = 2 });
        });

        InvalidOperationException exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadSnapshotCompiler().Compile(
                session,
                new CadSnapshotOptions { MaxExpandedEntities = 2 }));

        Assert.Contains("limit of 2", exception.Message, StringComparison.Ordinal);
    }

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, Tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, Tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, Tolerance);
    }

    private sealed class FixedTextFontResolver(
        TtfFont font,
        bool isSubstitution = false) : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
            new(font, isSubstitution);
    }

    private static CadShxGlyphCache CreateShxCache()
    {
        (ushort Number, string Name, byte[] Program)[] shapes =
        {
            (0, "TESTSHX", new byte[] { 10, 2, 0, 0 }),
            (32, "SPACE", new byte[] { 2, 8, 10, 0, 0 }),
            (65, "UCA", new byte[] { 0xA4, 0xA0, 2, 8, 20, 0xF6, 0 }),
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write(shapes.Min(shape => shape.Number));
        writer.Write(shapes.Max(shape => shape.Number));
        writer.Write(checked((ushort)shapes.Length));
        foreach ((ushort number, string name, byte[] program) in shapes)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            writer.Write(number);
            writer.Write(checked((ushort)(nameBytes.Length + 1 + program.Length)));
        }
        foreach ((ushort _, string name, byte[] program) in shapes)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
            writer.Write(program);
        }
        writer.Write("EOF"u8);
        return new CadShxGlyphCache(CadShxFont.Parse(stream.ToArray()));
    }

    private static CadShxGlyphCache CreateDualOrientationShxCache()
    {
        (ushort Number, string Name, byte[] Program)[] shapes =
        {
            (0, "VERTICALSHX", new byte[] { 10, 2, 2, 0 }),
            (32, "SPACE", new byte[] { 2, 8, 10, 0, 14, 8, 0xF6, 0xF6, 0 }),
            (65, "UCA", new byte[]
            {
                2, 14, 8, 0xFF, 2,
                1, 0x14,
                2, 8, 2, 0xFF,
                14, 8, 0xFF, 0xFD,
                0,
            }),
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write(shapes.Min(shape => shape.Number));
        writer.Write(shapes.Max(shape => shape.Number));
        writer.Write(checked((ushort)shapes.Length));
        foreach ((ushort number, string name, byte[] program) in shapes)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            writer.Write(number);
            writer.Write(checked((ushort)(nameBytes.Length + 1 + program.Length)));
        }
        foreach ((ushort _, string name, byte[] program) in shapes)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
            writer.Write(program);
        }
        writer.Write("EOF"u8);
        return new CadShxGlyphCache(CadShxFont.Parse(stream.ToArray()));
    }

    private sealed class FixedShxFontResolver(
        CadShxGlyphCache cache,
        bool isSubstitution = false) : ICadShxFontResolver
    {
        public CadShxFontResolution Resolve(in CadShxFontRequest request) =>
            new(cache, cache.Font.Name, isSubstitution);
    }
}

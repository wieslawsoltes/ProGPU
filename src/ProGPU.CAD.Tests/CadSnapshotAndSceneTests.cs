using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
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
                new XYZ(4, 3, 0),
                new XYZ(0, 3, 0)));
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

        Assert.Equal(2, snapshot.Faces.Length);
        Assert.Equal(CadEntityKind.Solid, snapshot.Entities.Span[0].Kind);
        Assert.Equal(CadEntityKind.Face3D, snapshot.Entities.Span[1].Kind);
        AssertPoint(new CadPoint3D(0, 0, 0), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(14, 3, 4), snapshot.Bounds.Max);
        Assert.Equal(2, scene.DrawingContext.Commands.Count);
        RenderCommand solid = scene.DrawingContext.Commands[0];
        Assert.Equal(RenderCommandType.DrawPath, solid.Type);
        Assert.NotNull(solid.Brush);
        Assert.Null(solid.Pen);
        Assert.True(Assert.Single(solid.Path!.Figures).IsClosed);
        RenderCommand face = scene.DrawingContext.Commands[1];
        Assert.Null(face.Brush);
        Assert.NotNull(face.Pen);
        Assert.Equal(2, face.Path!.Figures.Count);
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
}

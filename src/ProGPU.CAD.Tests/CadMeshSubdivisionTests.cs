using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using ProGPU.Backend.Native;
using ProGPU.CAD.Native;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMeshSubdivisionTests
{
    [Fact]
    public void RefinedMeshKeepsOneAuthoredFaceFourAuthoredEdgesAndFinalControlVertices()
    {
        CadDocumentSnapshot snapshot = Compile(new CadDocument
        {
            Entities = { CreateOpenQuad(level: 1) },
        });
        CadMesh3DPrimitive primitive = Assert.Single(snapshot.Meshes3D.ToArray());
        CadMesh3DDrawRange[] ranges = snapshot.Mesh3DDrawRanges.ToArray();

        Assert.True(primitive.HasSubobjectTopology);
        Assert.Equal(4, primitive.SubobjectVertexCount);
        Assert.Equal(4, primitive.SubobjectEdgeCount);
        Assert.Equal(1, primitive.SubobjectFaceCount);
        Assert.Equal(4, ranges.Length);
        Assert.All(ranges, range => Assert.Equal(0, range.FaceSubobjectIndex));
        Assert.All(snapshot.Mesh3DSubobjectEdges.ToArray(), edge =>
            Assert.Equal(3, edge.PointCount));

        CadPoint3D firstControl = snapshot.Mesh3DSubobjectPoints.Span[
            primitive.SubobjectVertexPointOffset];
        Assert.Equal(new CadPoint3D(0.25, 0.25, 0), firstControl);
        CadRecordedMesh3DScene scene =
            new CadMesh3DSceneCompiler().Compile(snapshot);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        Assert.Equal(new System.Numerics.Vector3(-0.75f, -0.75f, 0),
            component.VertexPositions.Span[0]);
        Assert.All(scene.DrawBatches.Span[0].TriangleFaceSubobjectIndices.ToArray(),
            face => Assert.Equal(0, face));
    }

    [Fact]
    public void OpenQuadLevelOneUsesBoundaryMasksUvSmoothNormalsAndSharedPipelines()
    {
        Mesh mesh = CreateOpenQuad(level: 1);
        var document = new CadDocument();
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = Compile(document);

        Assert.Equal(12, snapshot.Lines.Length);
        Assert.Single(snapshot.Meshes3D.ToArray());
        Assert.Equal(4, snapshot.Mesh3DDrawRanges.Length);
        Assert.Equal(24, snapshot.Mesh3DVertices.Length);
        Assert.Equal(24, snapshot.Mesh3DIndices.Length);
        Assert.Contains(snapshot.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(0.25, 0.25, 0.0)) &&
            IsNear(vertex.TextureCoordinate, 0.0f, 0.0f));
        Assert.Contains(snapshot.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(1.0, 1.0, 0.0)) &&
            IsNear(vertex.TextureCoordinate, 0.5f, 0.5f));
        Assert.All(snapshot.Mesh3DVertices.ToArray(), vertex =>
            Assert.Equal(new CadPoint3D(0.0, 0.0, 1.0), vertex.Normal));

        CadRecordedPlanScene plan = new CadPlanSceneCompiler().Compile(snapshot);
        Assert.Equal(12, plan.Statistics.RecordedEntityCount);
        Assert.Equal(12, plan.DrawingContext.Commands.Count);

        CadSelectionCandidate candidate = MeshCandidate(snapshot);
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                new CadPoint3D(1.0, 1.0, 0.05),
                0.05).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(
                    new CadPoint3D(0.9, 0.9, -0.1),
                    new CadPoint3D(1.1, 1.1, 0.1)),
                CadBoundsSelectionMode.Crossing).Status);

        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(snapshot);
        CadMesh3DDrawBatch batch = Assert.Single(scene.DrawBatches.ToArray());
        Assert.Equal(8, scene.Statistics.TriangleCount);
        Assert.Equal(24, batch.Positions.Length);
        var camera = new CadNativeMesh3DCamera(
            System.Numerics.Matrix4x4.Identity,
            System.Numerics.Matrix4x4.Identity,
            new System.Numerics.Vector3(0, 0, 5),
            new NativeImageRect(0, 0, 640, 480));
        CadNativeMesh3DScene native = new CadNativeMesh3DSceneCompiler().Compile(
            scene,
            camera,
            sceneId: 8101U);
        Assert.Equal(1, native.DrawBatchCount);
        Assert.Equal(24, native.VertexCount);
        Assert.Equal(24, native.IndexCount);

        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        Assert.Equal(12, print.SceneStatistics.RecordedEntityCount);
    }

    [Fact]
    public void ClosedCubeUsesCatmullClarkCornerMaskAndContinuousNormals()
    {
        Mesh mesh = CreateCube(level: 1, crease: null);
        var document = new CadDocument();
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = Compile(document);

        Assert.Equal(48, snapshot.Lines.Length);
        Assert.Equal(24, snapshot.Mesh3DDrawRanges.Length);
        Assert.Equal(144, snapshot.Mesh3DVertices.Length);
        CadPoint3D expectedCorner = new(-5.0 / 9.0, -5.0 / 9.0, -5.0 / 9.0);
        CadMesh3DVertex[] cornerVertices = snapshot.Mesh3DVertices
            .ToArray()
            .Where(vertex => IsNear(vertex.Position, expectedCorner))
            .ToArray();
        Assert.NotEmpty(cornerVertices);
        CadPoint3D expectedNormal = new CadPoint3D(-1.0, -1.0, -1.0).Normalize();
        Assert.All(cornerVertices, vertex => Assert.True(IsNear(vertex.Normal, expectedNormal)));
    }

    [Fact]
    public void InfiniteCubeCreasesRetainCornersAndSplitNormalsAcrossFaces()
    {
        Mesh mesh = CreateCube(level: 1, crease: -1.0);
        var document = new CadDocument();
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = Compile(document);

        CadMesh3DVertex[] cornerVertices = snapshot.Mesh3DVertices
            .ToArray()
            .Where(vertex => IsNear(vertex.Position, new CadPoint3D(-1.0, -1.0, -1.0)))
            .ToArray();
        Assert.NotEmpty(cornerVertices);
        CadPoint3D[] normals = cornerVertices
            .Select(vertex => vertex.Normal)
            .Distinct()
            .ToArray();
        Assert.Equal(3, normals.Length);
        Assert.Contains(new CadPoint3D(-1.0, 0.0, 0.0), normals);
        Assert.Contains(new CadPoint3D(0.0, -1.0, 0.0), normals);
        Assert.Contains(new CadPoint3D(0.0, 0.0, -1.0), normals);
    }

    [Fact]
    public void TwoSharpEdgesUseTheCreaseVertexMask()
    {
        Mesh mesh = CreateCube(level: 1, crease: null);
        mesh.Edges.Add(new Mesh.Edge(0, 1) { Crease = -1.0 });
        mesh.Edges.Add(new Mesh.Edge(0, 3) { Crease = -1.0 });
        var document = new CadDocument();
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = Compile(document);

        Assert.Contains(snapshot.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(-0.75, -0.75, -1.0)));
    }

    [Fact]
    public void OneSharpEdgeUsesTheSmoothDartVertexMask()
    {
        Mesh mesh = CreateCube(level: 1, crease: null);
        mesh.Edges.Add(new Mesh.Edge(0, 1) { Crease = -1.0 });
        var document = new CadDocument();
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = Compile(document);

        Assert.Contains(snapshot.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(
                -5.0 / 9.0,
                -5.0 / 9.0,
                -5.0 / 9.0)));
    }

    [Fact]
    public void BlendCreaseInterpolatesTheFinalFractionalEdgeMask()
    {
        CadDocumentSnapshot smooth = Compile(CreateFoldDocument(null, blendCrease: false));
        CadDocumentSnapshot blended = Compile(CreateFoldDocument(0.5, blendCrease: true));
        CadDocumentSnapshot sharp = Compile(CreateFoldDocument(-1.0, blendCrease: false));

        Assert.Contains(smooth.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(0.25, 1.0, 0.25)));
        Assert.Contains(blended.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(0.125, 1.0, 0.125)));
        Assert.Contains(sharp.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(0.0, 1.0, 0.0)));
    }

    [Fact]
    public void FiniteCreaseDecaysAfterItsAuthoredSmoothingLevel()
    {
        CadDocumentSnapshot levelOne = Compile(CreateFoldDocument(1.0, blendCrease: false, level: 1));
        CadDocumentSnapshot levelTwo = Compile(CreateFoldDocument(1.0, blendCrease: false, level: 2));

        Assert.True(HasPositionWithDifferentNormals(
            levelOne,
            new CadPoint3D(0.0, 1.0, 0.0)));
        Assert.All(
            levelTwo.Mesh3DVertices.ToArray().GroupBy(vertex => vertex.Position),
            group => Assert.Single(group.Select(vertex => vertex.Normal).Distinct()));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task SubdivisionLevelBlendAndCreasesRoundTripThroughAdvertisedFormats(
        CadDocumentFormat format)
    {
        CadDocument source = CreateFoldDocument(0.5, blendCrease: true);
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            new CadDocumentSession(source),
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"subdivision.{format.ToString().ToLowerInvariant()}");

        Mesh mesh = loaded.Session.Read(document =>
            Assert.IsType<Mesh>(Assert.Single(document.Entities)));
        Assert.Equal(1, mesh.SubdivisionLevel);
        Assert.True(mesh.BlendCrease);
        Mesh.Edge crease = Assert.Single(mesh.Edges, edge => edge.Crease.HasValue);
        Assert.Equal(0.5, crease.Crease!.Value, 12);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(0.125, 1.0, 0.125)));
    }

    [Theory]
    [InlineData("same-winding")]
    [InlineData("non-manifold")]
    [InlineData("disconnected-fan")]
    [InlineData("unknown-crease")]
    [InlineData("fractional-without-blend")]
    [InlineData("negative-fractional")]
    public void InvalidSubdivisionTopologyOrCreaseIsRejectedTransactionally(string kind)
    {
        Mesh mesh = CreateFold(level: 1, crease: null, blendCrease: false);
        switch (kind)
        {
            case "same-winding":
                mesh.Faces[1] = [3, 0, 4, 5];
                break;
            case "non-manifold":
                mesh.Vertices.Add(new XYZ(-2, 0, 0));
                mesh.Faces.Add([0, 3, 6]);
                break;
            case "disconnected-fan":
                mesh = CreateDisconnectedClosedFans();
                break;
            case "unknown-crease":
                mesh.Edges.Add(new Mesh.Edge(1, 5) { Crease = -1.0 });
                break;
            case "fractional-without-blend":
                mesh.Edges.Add(new Mesh.Edge(0, 3) { Crease = 0.5 });
                break;
            case "negative-fractional":
                mesh.BlendCrease = true;
                mesh.Edges.Add(new Mesh.Edge(0, 3) { Crease = -0.5 });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var document = new CadDocument();
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = Compile(document);

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.Meshes3D.ToArray());
        Assert.Empty(snapshot.Lines.ToArray());
        Assert.Equal(
            kind is "same-winding" or "non-manifold" or "disconnected-fan" ? 1 : 0,
            snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(
            kind is "same-winding" or "non-manifold" or "disconnected-fan" ? 0 : 1,
            snapshot.Statistics.InvalidEntityCount);
    }

    [Fact]
    public void SubdivisionLevelAndAggregateTopologyBudgetsFailBeforePublication()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateOpenQuad(level: 2));
        document.Entities.Add(CreateOpenQuad(level: 1));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions
            {
                MaxMeshSubdivisionLevel = 1,
                MaxMeshSubdivisionTopologyVisits = 19,
            });

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.Meshes3D.ToArray());
        Assert.Equal(2, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("subdivision level 2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("refinement limit", StringComparison.OrdinalIgnoreCase));

        CadDocumentSnapshot disabled = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(CreateFoldDocument(null, blendCrease: false)),
            new CadSnapshotOptions { MaxMeshSubdivisionLevel = 0 });
        Assert.Empty(disabled.Entities.ToArray());
        Assert.Equal(1, disabled.Statistics.UnsupportedEntityCount);
    }

    [Fact]
    public void SubdividedMeshTranslationParticipatesInUndoRedoAndRecompilation()
    {
        Mesh mesh = CreateOpenQuad(level: 1);
        var document = new CadDocument();
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        CadDocumentSnapshot original = new CadSnapshotCompiler().Compile(session);
        Assert.Contains(original.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(1.0, 1.0, 0.0)));

        history.Execute(new CadTranslateEntitiesCommand(
            [mesh.Handle],
            new CadPoint3D(5.0, -3.0, 4.0)));
        CadDocumentSnapshot translated = new CadSnapshotCompiler().Compile(session);
        Assert.True(translated.ContentGeneration > original.ContentGeneration);
        Assert.Contains(translated.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(6.0, -2.0, 4.0)));

        Assert.True(history.TryUndo(out _));
        CadDocumentSnapshot undone = new CadSnapshotCompiler().Compile(session);
        Assert.Contains(undone.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(1.0, 1.0, 0.0)));

        Assert.True(history.TryRedo(out _));
        CadDocumentSnapshot redone = new CadSnapshotCompiler().Compile(session);
        Assert.Contains(redone.Mesh3DVertices.ToArray(), vertex =>
            IsNear(vertex.Position, new CadPoint3D(6.0, -2.0, 4.0)));
    }

    private static CadDocumentSnapshot Compile(CadDocument document) =>
        new CadSnapshotCompiler().Compile(new CadDocumentSession(document));

    private static CadDocument CreateFoldDocument(
        double? crease,
        bool blendCrease,
        int level = 1)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(CreateFold(level, crease, blendCrease));
        return document;
    }

    private static Mesh CreateOpenQuad(int level)
    {
        var mesh = new Mesh { SubdivisionLevel = level };
        mesh.Vertices.Add(new XYZ(0, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 2, 0));
        mesh.Vertices.Add(new XYZ(0, 2, 0));
        mesh.TextureCoordinates =
        [
            new XYZ(0, 0, 0),
            new XYZ(1, 0, 0),
            new XYZ(1, 1, 0),
            new XYZ(0, 1, 0),
        ];
        mesh.Faces.Add([0, 1, 2, 3]);
        return mesh;
    }

    private static Mesh CreateFold(int level, double? crease, bool blendCrease)
    {
        var mesh = new Mesh
        {
            SubdivisionLevel = level,
            BlendCrease = blendCrease,
        };
        mesh.Vertices.Add(new XYZ(0, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 2, 0));
        mesh.Vertices.Add(new XYZ(0, 2, 0));
        mesh.Vertices.Add(new XYZ(0, 0, 2));
        mesh.Vertices.Add(new XYZ(0, 2, 2));
        mesh.Faces.Add([0, 1, 2, 3]);
        mesh.Faces.Add([0, 3, 5, 4]);
        if (crease.HasValue)
        {
            mesh.Edges.Add(new Mesh.Edge(0, 3) { Crease = crease.Value });
        }
        return mesh;
    }

    private static Mesh CreateCube(int level, double? crease)
    {
        var mesh = new Mesh { SubdivisionLevel = level };
        mesh.Vertices.Add(new XYZ(-1, -1, -1));
        mesh.Vertices.Add(new XYZ(1, -1, -1));
        mesh.Vertices.Add(new XYZ(1, 1, -1));
        mesh.Vertices.Add(new XYZ(-1, 1, -1));
        mesh.Vertices.Add(new XYZ(-1, -1, 1));
        mesh.Vertices.Add(new XYZ(1, -1, 1));
        mesh.Vertices.Add(new XYZ(1, 1, 1));
        mesh.Vertices.Add(new XYZ(-1, 1, 1));
        mesh.Faces.Add([0, 3, 2, 1]);
        mesh.Faces.Add([4, 5, 6, 7]);
        mesh.Faces.Add([0, 1, 5, 4]);
        mesh.Faces.Add([1, 2, 6, 5]);
        mesh.Faces.Add([2, 3, 7, 6]);
        mesh.Faces.Add([3, 0, 4, 7]);
        if (crease.HasValue)
        {
            int[] edges =
            [
                0, 1, 1, 2, 2, 3, 3, 0,
                4, 5, 5, 6, 6, 7, 7, 4,
                0, 4, 1, 5, 2, 6, 3, 7,
            ];
            for (int i = 0; i < edges.Length; i += 2)
            {
                mesh.Edges.Add(new Mesh.Edge(edges[i], edges[i + 1])
                {
                    Crease = crease.Value,
                });
            }
        }
        return mesh;
    }

    private static Mesh CreateDisconnectedClosedFans()
    {
        var mesh = new Mesh { SubdivisionLevel = 1 };
        mesh.Vertices.Add(new XYZ(0, 0, 0));
        mesh.Vertices.Add(new XYZ(1, 0, 0));
        mesh.Vertices.Add(new XYZ(0, 1, 0));
        mesh.Vertices.Add(new XYZ(0, 0, 1));
        mesh.Vertices.Add(new XYZ(-1, 0, 0));
        mesh.Vertices.Add(new XYZ(0, -1, 0));
        mesh.Vertices.Add(new XYZ(0, 0, -1));
        mesh.Faces.Add([0, 2, 1]);
        mesh.Faces.Add([0, 1, 3]);
        mesh.Faces.Add([1, 2, 3]);
        mesh.Faces.Add([2, 0, 3]);
        mesh.Faces.Add([0, 4, 5]);
        mesh.Faces.Add([0, 6, 4]);
        mesh.Faces.Add([4, 6, 5]);
        mesh.Faces.Add([5, 6, 0]);
        return mesh;
    }

    private static CadSelectionCandidate MeshCandidate(CadDocumentSnapshot snapshot)
    {
        int index = Array.FindIndex(
            snapshot.Entities.ToArray(),
            entity => entity.Kind == CadEntityKind.Mesh3D);
        Assert.True(index >= 0);
        CadEntityHeader header = snapshot.Entities.Span[index];
        return new CadSelectionCandidate(
            snapshot.ContentGeneration,
            index,
            header.Handle,
            header.Kind,
            header.Bounds);
    }

    private static bool HasPositionWithDifferentNormals(
        CadDocumentSnapshot snapshot,
        CadPoint3D position) =>
        snapshot.Mesh3DVertices
            .ToArray()
            .Where(vertex => IsNear(vertex.Position, position))
            .Select(vertex => vertex.Normal)
            .Distinct()
            .Count() > 1;

    private static bool IsNear(CadPoint3D actual, CadPoint3D expected) =>
        Math.Abs(actual.X - expected.X) <= 1e-10 &&
        Math.Abs(actual.Y - expected.Y) <= 1e-10 &&
        Math.Abs(actual.Z - expected.Z) <= 1e-10;

    private static bool IsNear(
        System.Numerics.Vector2 actual,
        float expectedX,
        float expectedY) =>
        Math.Abs(actual.X - expectedX) <= 1e-6f &&
        Math.Abs(actual.Y - expectedY) <= 1e-6f;
}

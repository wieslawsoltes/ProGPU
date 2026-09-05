using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using System.Numerics;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMesh3DEdgeStreamTests
{
    [Fact]
    public void PlanarQuadKeepsBoundarySeparateFromTriangulationDiagonal()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(new XYZ(0, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 2, 0));
        mesh.Vertices.Add(new XYZ(0, 2, 0));
        mesh.Faces.Add([0, 1, 2, 3]);

        CadRecordedMesh3DScene scene = Compile(mesh);
        CadMesh3DEdge[] edges = Assert.Single(scene.EdgeBatches.ToArray())
            .Edges.ToArray();

        Assert.Equal(5, edges.Length);
        Assert.Equal(4, edges.Count(edge =>
            edge.Topology == CadMesh3DEdgeTopology.Boundary));
        CadMesh3DEdge diagonal = Assert.Single(edges, edge =>
            edge.Topology == CadMesh3DEdgeTopology.Manifold);
        Assert.Equal(1.0f, Vector3.Dot(
            diagonal.FirstFaceNormal,
            diagonal.SecondFaceNormal), 5);
        Assert.Equal(5, scene.Statistics.EdgeCount);
        Assert.Equal(4, scene.Statistics.BoundaryEdgeCount);
        Assert.Equal(0, scene.Statistics.NonManifoldEdgeCount);
    }

    [Fact]
    public void FoldRetainsCameraIndependentAdjacentFaceNormals()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(new XYZ(0, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 2, 0));
        mesh.Vertices.Add(new XYZ(0, 2, 0));
        mesh.Vertices.Add(new XYZ(0, 0, 2));
        mesh.Vertices.Add(new XYZ(0, 2, 2));
        mesh.Faces.Add([0, 1, 2, 3]);
        mesh.Faces.Add([0, 3, 5, 4]);

        CadMesh3DEdge[] edges = Assert.Single(Compile(mesh).EdgeBatches.ToArray())
            .Edges.ToArray();

        Assert.Equal(9, edges.Length);
        Assert.Equal(6, edges.Count(edge =>
            edge.Topology == CadMesh3DEdgeTopology.Boundary));
        CadMesh3DEdge creaseCandidate = Assert.Single(edges, edge =>
            edge.Topology == CadMesh3DEdgeTopology.Manifold &&
            Vector3.Dot(edge.FirstFaceNormal, edge.SecondFaceNormal) < 0.01f);
        Assert.Equal(2.0f, Vector3.Distance(
            creaseCandidate.Start,
            creaseCandidate.End), 5);
    }

    [Fact]
    public void ThreeFaceJunctionIsClassifiedAsNonManifold()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(new XYZ(0, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 0, 0));
        mesh.Vertices.Add(new XYZ(0, 1, 0));
        mesh.Vertices.Add(new XYZ(0, 0, 1));
        mesh.Vertices.Add(new XYZ(0, -1, 0));
        mesh.Faces.Add([0, 1, 2]);
        mesh.Faces.Add([1, 0, 3]);
        mesh.Faces.Add([0, 1, 4]);

        CadRecordedMesh3DScene scene = Compile(mesh);

        Assert.Equal(1, scene.Statistics.NonManifoldEdgeCount);
        Assert.Single(
            Assert.Single(scene.EdgeBatches.ToArray()).Edges.ToArray(),
            edge => edge.Topology == CadMesh3DEdgeTopology.NonManifold);
    }

    [Fact]
    public void AggregateEdgeLimitRejectsBeforePublishingScene()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(new XYZ(0, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 0, 0));
        mesh.Vertices.Add(new XYZ(0, 2, 0));
        mesh.Faces.Add([0, 1, 2]);
        CadDocumentSnapshot snapshot = CompileSnapshot(mesh);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new CadMesh3DSceneCompiler().Compile(
                snapshot,
                new CadMesh3DSceneOptions { MaxEdgeCount = 2 }));

        Assert.Contains("configured limit of 2", exception.Message);
    }

    private static CadRecordedMesh3DScene Compile(Mesh mesh) =>
        new CadMesh3DSceneCompiler().Compile(CompileSnapshot(mesh));

    private static CadDocumentSnapshot CompileSnapshot(Mesh mesh)
    {
        var document = new CadDocument();
        document.Entities.Add(mesh);
        return new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
    }
}

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadSelectionTests
{
    [Fact]
    public void BoundsQueryMapsSpatialHitsToGenerationTaggedCandidates()
    {
        var document = new CadDocument();
        var line = new Line(XYZ.Zero, new XYZ(10, 0, 0));
        document.Entities.Add(line);
        document.Entities.Add(new Circle(new XYZ(100, 100, 0), 5));
        var session = new CadDocumentSession(document);
        session.Edit("Advance generation", _ => line.LineTypeScale = 2.0);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        Span<int> scratch = stackalloc int[2];
        Span<CadSelectionCandidate> candidates = stackalloc CadSelectionCandidate[2];

        CadSelectionQueryResult result = CadSelectionQuery.QueryBounds(
            snapshot,
            new CadBounds3D(
                new CadPoint3D(-1, -1, -1),
                new CadPoint3D(11, 1, 1)),
            scratch,
            candidates);

        Assert.Equal(1UL, result.ContentGeneration);
        Assert.Equal(1, result.WrittenCount);
        Assert.Equal(1, result.TotalCount);
        Assert.False(result.IsTruncated);
        Assert.Equal(line.Handle, candidates[0].Handle);
        Assert.Equal(CadEntityKind.Line, candidates[0].Kind);
        Assert.Equal(0, candidates[0].EntityIndex);
    }

    [Fact]
    public void BoundsQueryReportsTruncationWithoutHiddenCapacity()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        document.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 0)));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        Span<int> scratch = stackalloc int[1];
        Span<CadSelectionCandidate> candidates = stackalloc CadSelectionCandidate[1];

        CadSelectionQueryResult result = CadSelectionQuery.QueryBounds(
            snapshot,
            snapshot.Bounds,
            scratch,
            candidates);

        Assert.Equal(1, result.WrittenCount);
        Assert.Equal(2, result.TotalCount);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public void ExpandedBlockPrimitivesRemainSeparateWithSharedRootHandle()
    {
        var document = new CadDocument();
        var block = new BlockRecord("SELECT_BLOCK");
        block.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        block.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 0)));
        var insert = new Insert(block);
        document.Entities.Add(insert);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        Span<int> scratch = stackalloc int[2];
        Span<CadSelectionCandidate> candidates = stackalloc CadSelectionCandidate[2];

        CadSelectionQueryResult result = CadSelectionQuery.QueryBounds(
            snapshot,
            snapshot.Bounds,
            scratch,
            candidates);

        Assert.Equal(2, result.WrittenCount);
        Assert.Equal(insert.Handle, candidates[0].Handle);
        Assert.Equal(insert.Handle, candidates[1].Handle);
        Assert.NotEqual(candidates[0].EntityIndex, candidates[1].EntityIndex);
    }

    [Fact]
    public void WarmBoundsCandidateQueriesAllocateNoManagedMemory()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add selection lines", document =>
        {
            for (int i = 0; i < 256; i++)
            {
                document.Entities.Add(new Line(
                    new XYZ(i * 2, i % 11, 0),
                    new XYZ((i * 2) + 1, (i % 11) + 1, 0)));
            }
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        var bounds = new CadBounds3D(
            new CadPoint3D(100, -1, -1),
            new CadPoint3D(300, 20, 1));
        var scratch = new int[128];
        var candidates = new CadSelectionCandidate[128];
        _ = CadSelectionQuery.QueryBounds(snapshot, bounds, scratch, candidates);
        _ = GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            checksum += CadSelectionQuery.QueryBounds(
                snapshot,
                bounds,
                scratch,
                candidates).TotalCount;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(checksum > 0);
        Assert.Equal(0, allocated);
    }
}

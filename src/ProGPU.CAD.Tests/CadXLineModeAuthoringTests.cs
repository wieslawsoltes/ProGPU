using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadXLineModeAuthoringTests
{
    [Fact]
    public void SnapshotCapturesRawUcsSeparatelyFromAngularBasis()
    {
        CadDocumentSession documentSession = CadDocumentSession.CreateNew();
        documentSession.Edit("Configure XLINE plan context", document =>
        {
            document.Header.AngleBase = Math.PI / 6.0;
            document.Header.AngularDirection = AngularDirection.ClockWise;
            VPort active = document.VPorts[VPort.DefaultName];
            active.Origin = new XYZ(10, 20, 30);
            active.XAxis = new XYZ(0, 1, 0);
            active.YAxis = new XYZ(-1, 0, 0);
            active.SnapRotation = Math.PI / 3.0;
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            documentSession);
        CadPlanAuthoringContext context = snapshot.PlanAuthoringContext;

        Assert.True(context.IsSupported);
        Assert.Equal(new CadPoint3D(10, 20, 30), context.Origin);
        AssertPoint(new CadPoint3D(0, 1, 0), context.HorizontalAxis);
        AssertPoint(new CadPoint3D(-1, 0, 0), context.VerticalAxis);
        AssertPoint(new CadPoint3D(0, 0, 1), context.Normal);
        AssertPoint(
            new CadPoint3D(-0.5, Math.Sqrt(3.0) / 2.0, 0),
            context.AngleXAxis);
        AssertPoint(
            new CadPoint3D(-Math.Sqrt(3.0) / 2.0, -0.5, 0),
            context.AngleYAxis);
        Assert.True(context.IsClockwise);
    }

    [Fact]
    public void LinearSourceResolverAcceptsOnlyExactCurrentLinearCandidates()
    {
        CadDocumentSnapshot snapshot = CreateLinearSourceSnapshot();

        CadXLineLinearSource line = Resolve(snapshot, CadEntityKind.Line);
        AssertPoint(new CadPoint3D(1, 2, 0), line.BasePoint);
        AssertPoint(new CadPoint3D(0.6, 0.8, 0), line.Direction);

        CadXLineLinearSource ray = Resolve(snapshot, CadEntityKind.Ray);
        AssertPoint(new CadPoint3D(-1, 4, 0), ray.BasePoint);
        AssertPoint(new CadPoint3D(0, -1, 0), ray.Direction);

        CadXLineLinearSource xline = Resolve(snapshot, CadEntityKind.XLine);
        AssertPoint(new CadPoint3D(8, 9, 0), xline.BasePoint);
        AssertPoint(new CadPoint3D(-1, 0, 0), xline.Direction);

        CadSelectionCandidate circle = Candidate(snapshot, CadEntityKind.Circle);
        Assert.Equal(
            CadXLineLinearSourceStatus.UnsupportedKind,
            CadXLineLinearSourceResolver.Resolve(snapshot, circle).Status);
        Assert.Equal(
            CadXLineLinearSourceStatus.StaleGeneration,
            CadXLineLinearSourceResolver.Resolve(
                snapshot,
                circle with
                {
                    ContentGeneration = snapshot.ContentGeneration + 1,
                }).Status);
        Assert.Equal(
            CadXLineLinearSourceStatus.CandidateMismatch,
            CadXLineLinearSourceResolver.Resolve(
                snapshot,
                circle with { Handle = circle.Handle + 1 }).Status);
    }

    [Fact]
    public void HorizontalAndVerticalModesUseRawUcsAxesAndRemainBounded()
    {
        var context = new CadPlanAuthoringContext(
            CadPoint3D.Zero,
            new CadPoint3D(0, 1, 0),
            new CadPoint3D(-1, 0, 0),
            Math.PI / 3.0,
            isClockwise: false);
        var horizontal = new CadXLineModeAuthoringSession(
            CadXLineAuthoringMode.Horizontal,
            context,
            sourceContentGeneration: 7,
            maximumLineCount: 2);

        Assert.Equal(CadXLinePromptKind.PlacementPoint, horizontal.Prompt);
        Assert.True(horizontal.TryAcceptPoint(new CadPoint3D(2, 3, 4), out _));
        Assert.True(horizontal.TryAcceptPoint(new CadPoint3D(5, 6, 7), out _));
        Assert.False(horizontal.TryAcceptPoint(
            new CadPoint3D(8, 9, 10),
            out string? bounded));
        Assert.Contains("limit", bounded, StringComparison.OrdinalIgnoreCase);
        Assert.All(horizontal.Definitions.ToArray(), definition =>
            AssertPoint(context.HorizontalAxis, definition.Direction));
        Assert.True(horizontal.TryUndoLastLine());
        Assert.Equal(CadXLinePromptKind.PlacementPoint, horizontal.Prompt);

        var vertical = new CadXLineModeAuthoringSession(
            CadXLineAuthoringMode.Vertical,
            context,
            sourceContentGeneration: 7);
        Assert.True(vertical.TryAcceptPoint(CadPoint3D.Zero, out _));
        AssertPoint(
            context.VerticalAxis,
            Assert.Single(vertical.Definitions.ToArray()).Direction);
    }

    [Fact]
    public void AbsoluteAndReferenceAnglePromptsPreserveDifferentDirectionRules()
    {
        var context = new CadPlanAuthoringContext(
            CadPoint3D.Zero,
            new CadPoint3D(1, 0, 0),
            new CadPoint3D(0, 1, 0),
            Math.PI / 6.0,
            isClockwise: true);
        var absolute = new CadXLineModeAuthoringSession(
            CadXLineAuthoringMode.Angle,
            context,
            sourceContentGeneration: 0);

        Assert.Equal(CadXLinePromptKind.AngleValue, absolute.Prompt);
        Assert.True(absolute.TryAcceptValue(Math.PI / 6.0, out _));
        Assert.Equal(CadXLinePromptKind.PlacementPoint, absolute.Prompt);
        Assert.True(absolute.TryAcceptPoint(new CadPoint3D(4, 5, 6), out _));
        CadXLineDefinition absoluteDefinition = Assert.Single(
            absolute.CreateDefinitionSnapshot());
        Assert.Equal(new CadPoint3D(4, 5, 6), absoluteDefinition.FirstPoint);
        AssertPoint(new CadPoint3D(1, 0, 0), absoluteDefinition.Direction);

        CadDocumentSnapshot snapshot = CreateLinearSourceSnapshot();
        CadXLineLinearSource reference = Resolve(snapshot, CadEntityKind.XLine);
        var relative = new CadXLineModeAuthoringSession(
            CadXLineAuthoringMode.Angle,
            context,
            snapshot.ContentGeneration);
        Assert.True(relative.TryChooseAngleReference(out _));
        Assert.Equal(CadXLinePromptKind.AngleReferenceSource, relative.Prompt);
        Assert.True(relative.TryAcceptLinearSource(reference, out _));
        Assert.Equal(CadXLinePromptKind.AngleValue, relative.Prompt);
        Assert.True(relative.TryAcceptValue(Math.PI / 2.0, out _));
        Assert.True(relative.TryAcceptPoint(CadPoint3D.Zero, out _));
        AssertPoint(
            new CadPoint3D(0, -1, 0),
            Assert.Single(relative.Definitions.ToArray()).Direction);
    }

    [Fact]
    public void BisectModeRecoversFromInvalidFinalRayAndRepeatsFromVertex()
    {
        var authoring = new CadXLineModeAuthoringSession(
            CadXLineAuthoringMode.Bisect,
            CadPlanAuthoringContext.World,
            sourceContentGeneration: 0);

        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 10, 0), out _));
        Assert.Equal(CadXLinePromptKind.BisectorFirstRayPoint, authoring.Prompt);
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(11, 10, 0), out _));
        Assert.Equal(CadXLinePromptKind.BisectorSecondRayPoint, authoring.Prompt);
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(9, 10, 0),
            out string? opposite));
        Assert.Contains("opposite", opposite, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CadXLinePromptKind.BisectorSecondRayPoint, authoring.Prompt);
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 11, 0), out _));
        Assert.Equal(CadXLinePromptKind.BisectorVertex, authoring.Prompt);
        AssertPoint(
            new CadPoint3D(1 / Math.Sqrt(2.0), 1 / Math.Sqrt(2.0), 0),
            Assert.Single(authoring.Definitions.ToArray()).Direction);
    }

    [Fact]
    public void DistanceAndThroughOffsetPromptsRequireCurrentResolvedSources()
    {
        CadDocumentSnapshot snapshot = CreateLinearSourceSnapshot();
        CadXLineLinearSource source = Resolve(snapshot, CadEntityKind.Line);
        var distance = new CadXLineModeAuthoringSession(
            CadXLineAuthoringMode.Offset,
            CadPlanAuthoringContext.World,
            snapshot.ContentGeneration);

        Assert.False(distance.TryAcceptValue(0, out string? positive));
        Assert.Contains("positive", positive, StringComparison.OrdinalIgnoreCase);
        Assert.True(distance.TryAcceptValue(2.5, out _));
        Assert.Equal(CadXLinePromptKind.OffsetSource, distance.Prompt);
        var staleSession = new CadXLineModeAuthoringSession(
            CadXLineAuthoringMode.Offset,
            CadPlanAuthoringContext.World,
            snapshot.ContentGeneration + 1);
        Assert.True(staleSession.TryAcceptValue(2.5, out _));
        Assert.False(staleSession.TryAcceptLinearSource(
            source,
            out string? stale));
        Assert.Contains("stale", stale, StringComparison.OrdinalIgnoreCase);
        Assert.True(distance.TryAcceptLinearSource(source, out _));
        Assert.Equal(CadXLinePromptKind.OffsetSidePoint, distance.Prompt);
        Assert.True(distance.TryAcceptPoint(new CadPoint3D(0, 20, 0), out _));
        Assert.Equal(CadXLinePromptKind.OffsetSource, distance.Prompt);
        Assert.Equal(1, distance.LineCount);

        var through = new CadXLineModeAuthoringSession(
            CadXLineAuthoringMode.Offset,
            CadPlanAuthoringContext.World,
            snapshot.ContentGeneration);
        Assert.True(through.TryChooseOffsetThrough(out _));
        Assert.True(through.TryAcceptLinearSource(source, out _));
        Assert.Equal(CadXLinePromptKind.OffsetThroughPoint, through.Prompt);
        Assert.True(through.TryAcceptPoint(new CadPoint3D(-5, 8, 0), out _));
        CadXLineDefinition definition = Assert.Single(
            through.CreateDefinitionSnapshot());
        Assert.Equal(new CadPoint3D(-5, 8, 0), definition.FirstPoint);
        AssertPoint(source.Direction, definition.Direction);
    }

    [Fact]
    public void TwoPointModeNormalizesOppositeMaximumEndpointsWithoutOverflow()
    {
        var authoring = new CadXLineModeAuthoringSession(
            CadXLineAuthoringMode.TwoPoint,
            CadPlanAuthoringContext.World,
            sourceContentGeneration: 0);
        Assert.True(authoring.TryAcceptPoint(
            new CadPoint3D(double.MaxValue, double.MaxValue / 2.0, 0),
            out _));
        Assert.True(authoring.TryAcceptPoint(
            new CadPoint3D(-double.MaxValue, -double.MaxValue / 2.0, 0),
            out string? error),
            error);

        AssertPoint(
            new CadPoint3D(-2 / Math.Sqrt(5.0), -1 / Math.Sqrt(5.0), 0),
            Assert.Single(authoring.Definitions.ToArray()).Direction);
    }

    [Fact]
    public void WarmLinearSourceResolutionAllocatesNoManagedMemory()
    {
        CadDocumentSnapshot snapshot = CreateLinearSourceSnapshot();
        CadSelectionCandidate candidate = Candidate(snapshot, CadEntityKind.Line);
        Assert.True(CadXLineLinearSourceResolver.Resolve(
            snapshot,
            candidate).IsSuccess);

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong handleMix = 0;
        for (int i = 0; i < 1_024; i++)
        {
            CadXLineLinearSourceResult result =
                CadXLineLinearSourceResolver.Resolve(snapshot, candidate);
            handleMix ^= result.Source.Handle;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0UL, handleMix);
    }

    [Fact]
    public void PreviewAndAcquisitionMetadataFollowPromptWithoutMutation()
    {
        var horizontal = new CadXLineModeAuthoringSession(
            CadXLineAuthoringMode.Horizontal,
            CadPlanAuthoringContext.World,
            sourceContentGeneration: 3);
        Assert.Null(horizontal.AcquisitionBasePoint);
        Assert.Equal(
            new CadPoint3D(1, 0, 0),
            horizontal.PlacementDirection);
        Assert.True(horizontal.TryPreviewPoint(
            new CadPoint3D(4, 5, 0),
            out CadXLineDefinition preview));
        Assert.Equal(new CadPoint3D(4, 5, 0), preview.FirstPoint);
        Assert.Equal(0, horizontal.LineCount);
        Assert.True(horizontal.TryAcceptPoint(preview.FirstPoint, out _));
        Assert.Equal(preview.FirstPoint, horizontal.AcquisitionBasePoint);

        var bisector = new CadXLineModeAuthoringSession(
            CadXLineAuthoringMode.Bisect,
            CadPlanAuthoringContext.World,
            sourceContentGeneration: 3);
        CadPoint3D vertex = new(2, 3, 0);
        CadPoint3D firstRay = new(3, 3, 0);
        Assert.True(bisector.TryAcceptPoint(vertex, out _));
        Assert.Equal(vertex, bisector.BisectorVertex);
        Assert.Equal(vertex, bisector.AcquisitionBasePoint);
        Assert.True(bisector.TryAcceptPoint(firstRay, out _));
        Assert.Equal(firstRay, bisector.BisectorFirstRayPoint);
        Assert.True(bisector.TryPreviewPoint(
            new CadPoint3D(2, 4, 0),
            out CadXLineDefinition bisectorPreview));
        AssertPoint(
            new CadPoint3D(1 / Math.Sqrt(2.0), 1 / Math.Sqrt(2.0), 0),
            bisectorPreview.Direction);
        Assert.Equal(0, bisector.LineCount);
    }

    private static CadDocumentSnapshot CreateLinearSourceSnapshot()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line
        {
            StartPoint = new XYZ(1, 2, 0),
            EndPoint = new XYZ(4, 6, 0),
        });
        document.Entities.Add(new Ray
        {
            StartPoint = new XYZ(-1, 4, 0),
            Direction = new XYZ(0, -5, 0),
        });
        document.Entities.Add(new XLine
        {
            FirstPoint = new XYZ(8, 9, 0),
            Direction = new XYZ(-3, 0, 0),
        });
        document.Entities.Add(new Circle
        {
            Center = new XYZ(20, 20, 0),
            Radius = 2,
        });
        return new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
    }

    private static CadXLineLinearSource Resolve(
        CadDocumentSnapshot snapshot,
        CadEntityKind kind)
    {
        CadXLineLinearSourceResult result =
            CadXLineLinearSourceResolver.Resolve(snapshot, Candidate(snapshot, kind));
        Assert.True(result.IsSuccess, result.Status.ToString());
        Assert.Equal(kind, result.Source.Kind);
        return result.Source;
    }

    private static CadSelectionCandidate Candidate(
        CadDocumentSnapshot snapshot,
        CadEntityKind kind)
    {
        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        for (int i = 0; i < entities.Length; i++)
        {
            CadEntityHeader header = entities[i];
            if (header.Kind == kind)
            {
                return new CadSelectionCandidate(
                    snapshot.ContentGeneration,
                    i,
                    header.Handle,
                    header.Kind,
                    header.Bounds);
            }
        }
        throw new InvalidOperationException($"Snapshot has no {kind} candidate.");
    }

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.Equal(expected.X, actual.X, 12);
        Assert.Equal(expected.Y, actual.Y, 12);
        Assert.Equal(expected.Z, actual.Z, 12);
    }
}

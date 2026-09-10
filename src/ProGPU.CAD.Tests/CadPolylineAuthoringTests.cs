using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPolylineAuthoringTests
{
    [Fact]
    public void SessionRetainsLineAndAnalyticTangentArcWithExactEndTangent()
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(0, 0, 4), out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 0, 4), out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;

        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(20, 10, 4), out _));

        Assert.Equal(2, authoring.SegmentCount);
        Assert.Equal(0.0, authoring.Bulges.Span[0]);
        Assert.Equal(Math.Sqrt(2.0) - 1.0, authoring.Bulges.Span[1], 12);
        CadPoint3D tangent = authoring.PreviousSegmentDirection!.Value;
        Assert.InRange(Math.Abs(tangent.X), 0.0, 1e-12);
        Assert.True(tangent.Y > 0.0);
        Assert.Equal(0.0, tangent.Z);

        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 20, 4), out _));
        Assert.Equal(Math.Sqrt(2.0) - 1.0, authoring.Bulges.Span[2], 12);
        Assert.True(authoring.TryUndoLastSegment());
        Assert.Equal(2, authoring.SegmentCount);
        Assert.Equal(0.0, authoring.Bulges.Span[2]);

        var clockwise = new CadPolylineAuthoringSession();
        Assert.True(clockwise.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(clockwise.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));
        clockwise.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(clockwise.TryAcceptPoint(new CadPoint3D(20, -10, 0), out _));
        Assert.Equal(-(Math.Sqrt(2.0) - 1.0), clockwise.Bulges.Span[1], 12);
        Assert.True(clockwise.PreviousSegmentDirection!.Value.Y < 0.0);
    }

    [Fact]
    public void SessionRejectsOffPlaneDegenerateArcAndBoundOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadPolylineAuthoringSession(
                CadPolylineAuthoringSession.DefaultMaximumSegmentCount + 1));
        var authoring = new CadPolylineAuthoringSession(maximumSegmentCount: 2);
        Assert.True(authoring.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(1, 0, 1),
            out string? offPlane));
        Assert.Contains("plane", offPlane, StringComparison.OrdinalIgnoreCase);
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(1, 1, 0),
            out string? noTangent));
        Assert.Contains("preceding", noTangent, StringComparison.OrdinalIgnoreCase);
        authoring.Mode = CadPolylineAuthoringMode.Line;
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(1, 0, 0), out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(2, 0, 0),
            out string? degenerate));
        Assert.Contains("non-degenerate", degenerate, StringComparison.OrdinalIgnoreCase);
        authoring.Mode = CadPolylineAuthoringMode.Line;
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(2, 0, 0), out _));
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(3, 0, 0),
            out string? bounded));
        Assert.Contains("limit", bounded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitSignedArcAngleMapsToExactDxfBulge()
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptLinePoint(CadPoint3D.Zero, out _));
        Assert.True(authoring.TryAcceptArcPoint(
            new CadPoint3D(10, 0, 0),
            -Math.PI,
            out _));
        Assert.Equal(-1.0, authoring.Bulges.Span[0], 12);

        Assert.False(authoring.TryAcceptArcPoint(
            new CadPoint3D(20, 0, 0),
            Math.Tau,
            out string? fullTurn));
        Assert.Contains("complete turn", fullTurn, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitArcOptionsMatchAuthoritativeArcSolvers()
    {
        CadPoint3D start = new(0, 0, 7);

        AssertMatchesArcSolver(
            CreateExplicitArc(
                start,
                CadPolylineArcConstruction.IncludedAngle,
                scalar: Math.PI / 2.0,
                endpointInput: new CadPoint3D(10, 10, 7)),
            CreateScalarArc(
                CadArcAuthoringMode.StartEndAngle,
                start,
                new CadPoint3D(10, 10, 7),
                Math.PI / 2.0));

        (CadPoint3D centerStart, CadPoint3D centerEnd, double centerBulge) = CreateExplicitArc(
            new CadPoint3D(10, 0, 7),
            CadPolylineArcConstruction.Center,
            controlPoint: new CadPoint3D(0, 0, 7),
            endpointInput: new CadPoint3D(0, 30, 7));
        Assert.Equal(new CadPoint3D(0, 10, 7), centerEnd);
        AssertMatchesArcSolver(
            (centerStart, centerEnd, centerBulge),
            CreatePointArc(
                CadArcAuthoringMode.StartCenterEnd,
                new CadPoint3D(10, 0, 7),
                new CadPoint3D(0, 0, 7),
                new CadPoint3D(0, 30, 7)));

        AssertMatchesArcSolver(
            CreateExplicitArc(
                start,
                CadPolylineArcConstruction.Direction,
                controlPoint: new CadPoint3D(10, 0, 7),
                endpointInput: new CadPoint3D(10, 10, 7)),
            CreateDirectionArc(
                start,
                new CadPoint3D(10, 10, 7),
                new CadPoint3D(10, 0, 0)));

        AssertMatchesArcSolver(
            CreateExplicitArc(
                start,
                CadPolylineArcConstruction.Radius,
                scalar: 10.0,
                endpointInput: new CadPoint3D(10, 0, 7)),
            CreateScalarArc(
                CadArcAuthoringMode.StartEndRadius,
                start,
                new CadPoint3D(10, 0, 7),
                10.0));

        AssertMatchesArcSolver(
            CreateExplicitArc(
                start,
                CadPolylineArcConstruction.ThreePoint,
                controlPoint: new CadPoint3D(5, 5, 7),
                endpointInput: new CadPoint3D(10, 0, 7)),
            CreatePointArc(
                CadArcAuthoringMode.ThreePoint,
                start,
                new CadPoint3D(5, 5, 7),
                new CadPoint3D(10, 0, 7)));
    }

    [Fact]
    public void NestedArcOptionsMatchAuthoritativeArcSolvers()
    {
        CadPoint3D start = new(10, 0, 7);
        CadPoint3D center = new(0, 0, 7);

        AssertMatchesArcSolver(
            CreateNestedPointArc(
                start,
                CadPolylineArcConstruction.IncludedAngle,
                Math.PI / 2.0,
                CadPolylineArcNestedOption.Center,
                center),
            CreateCenterScalarArc(
                CadArcAuthoringMode.StartCenterAngle,
                start,
                center,
                Math.PI / 2.0));

        AssertMatchesArcSolver(
            CreateNestedPointArc(
                CadPoint3D.Zero,
                CadPolylineArcConstruction.IncludedAngle,
                Math.PI / 3.0,
                CadPolylineArcNestedOption.Radius,
                new CadPoint3D(10, 0, 0),
                nestedScalar: 10.0),
            CreateScalarArc(
                CadArcAuthoringMode.StartEndAngle,
                CadPoint3D.Zero,
                new CadPoint3D(10, 0, 0),
                Math.PI / 3.0));

        AssertMatchesArcSolver(
            CreateNestedScalarArc(
                start,
                center,
                CadPolylineArcNestedOption.IncludedAngle,
                -Math.PI / 2.0),
            CreateCenterScalarArc(
                CadArcAuthoringMode.StartCenterAngle,
                start,
                center,
                -Math.PI / 2.0));

        foreach (double chord in new[] { 10.0, -10.0 })
        {
            AssertMatchesArcSolver(
                CreateNestedScalarArc(
                    start,
                    center,
                    CadPolylineArcNestedOption.ChordLength,
                    chord),
                CreateCenterScalarArc(
                    CadArcAuthoringMode.StartCenterChord,
                    start,
                    center,
                    chord));
        }
    }

    [Fact]
    public void NestedArcFailuresRetainTheirExactPromptForRecovery()
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(authoring.TryBeginArcConstruction(
            CadPolylineArcConstruction.Center,
            out _));
        Assert.True(authoring.TryAcceptArcControlPoint(CadPoint3D.Zero, out _));
        Assert.True(authoring.TryBeginArcNestedOption(
            CadPolylineArcNestedOption.ChordLength,
            out _));

        Assert.False(authoring.TryAcceptArcNestedScalar(21.0, out _, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.ArcChordLength, authoring.Prompt);
        Assert.Equal(
            CadPolylineArcNestedOption.ChordLength,
            authoring.ArcNestedOption);
        Assert.True(authoring.TryAcceptArcNestedScalar(10.0, out _, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.Point, authoring.Prompt);

        Assert.True(authoring.TryBeginArcConstruction(
            CadPolylineArcConstruction.IncludedAngle,
            out _));
        Assert.True(authoring.TryAcceptArcScalar(Math.PI / 2.0, out _));
        Assert.True(authoring.TryBeginArcNestedOption(
            CadPolylineArcNestedOption.Radius,
            out _));
        Assert.False(authoring.TryAcceptArcScalar(0.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.ArcRadius, authoring.Prompt);
        Assert.True(authoring.TryAcceptArcScalar(5.0, out _));
        Assert.False(authoring.TryAcceptArcNestedPoint(
            authoring.CurrentPoint!.Value,
            out _,
            out _));
        Assert.Equal(
            CadPolylineAuthoringPrompt.ArcChordDirection,
            authoring.Prompt);
    }

    [Fact]
    public void NestedCenterAngleArcIsStableAtLargeWcsOrigin()
    {
        CadPoint3D center = new(1_000_000_000_000, -2_000_000_000_000, 9);
        CadPoint3D start = center + new CadPoint3D(10, 0, 0);
        (CadPoint3D _, CadPoint3D endpoint, double bulge) =
            CreateNestedScalarArc(
                start,
                center,
                CadPolylineArcNestedOption.IncludedAngle,
                Math.PI / 2.0);

        Assert.Equal(center + new CadPoint3D(0, 10, 0), endpoint);
        Assert.True(CadPolylineAuthoringSession.TryGetBulgeGeometry(
            start,
            endpoint,
            bulge,
            out CadPoint3D resolvedCenter,
            out double radius,
            out _,
            out double sweep));
        Assert.Equal(center, resolvedCenter);
        Assert.Equal(10.0, radius, 10);
        Assert.Equal(Math.PI / 2.0, sweep, 10);
    }

    [Fact]
    public void ExplicitArcPromptFailuresRetainRecoverableState()
    {
        var authoring = new CadPolylineAuthoringSession(initialWidth: 2.0);
        Assert.True(authoring.TryAcceptPoint(CadPoint3D.Zero, out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(authoring.CanBeginArcConstruction);
        Assert.True(authoring.TryBeginArcConstruction(
            CadPolylineArcConstruction.Radius,
            out _));
        Assert.Equal(CadPolylineAuthoringPrompt.ArcRadius, authoring.Prompt);
        Assert.False(authoring.TryAcceptArcScalar(0.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.ArcRadius, authoring.Prompt);
        Assert.True(authoring.TryAcceptArcScalar(4.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.ArcEndpoint, authoring.Prompt);
        Assert.False(authoring.TryAcceptArcEndpoint(
            new CadPoint3D(10, 0, 0),
            out _,
            out string? tooSmall));
        Assert.Contains("do not define", tooSmall, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CadPolylineAuthoringPrompt.ArcEndpoint, authoring.Prompt);
        Assert.True(authoring.TryAcceptArcEndpoint(
            new CadPoint3D(6, 0, 0),
            out CadPoint3D endpoint,
            out _));
        Assert.Equal(new CadPoint3D(6, 0, 0), endpoint);
        Assert.Equal(CadPolylineAuthoringPrompt.Point, authoring.Prompt);
        Assert.Equal(CadPolylineArcConstruction.TangentEndpoint, authoring.ArcConstruction);
    }

    [Theory]
    [InlineData(10.0, Math.PI / 3.0)]
    [InlineData(-10.0, Math.PI * 5.0 / 3.0)]
    public void SignedRadiusAndClockwiseOverrideAreIndependent(
        double signedRadius,
        double expectedSweepMagnitude)
    {
        CadPoint3D start = CadPoint3D.Zero;
        CadPoint3D end = new(10, 0, 0);
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(start, out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(authoring.TryBeginArcConstruction(
            CadPolylineArcConstruction.Radius,
            out _));
        Assert.True(authoring.TryAcceptArcScalar(signedRadius, out _));
        Assert.True(authoring.CanApplyArcClockwiseOverride);

        Assert.True(authoring.TryResolvePendingSegment(
            end,
            clockwiseOverride: false,
            out CadPoint3D defaultEndpoint,
            out double defaultBulge,
            out _));
        Assert.True(authoring.TryResolvePendingSegment(
            end,
            clockwiseOverride: true,
            out CadPoint3D clockwiseEndpoint,
            out double clockwiseBulge,
            out _));

        Assert.Equal(end, defaultEndpoint);
        Assert.Equal(end, clockwiseEndpoint);
        Assert.True(defaultBulge > 0.0);
        Assert.True(clockwiseBulge < 0.0);
        Assert.Equal(Math.Abs(defaultBulge), Math.Abs(clockwiseBulge), 12);
        Assert.True(CadPolylineAuthoringSession.TryGetBulgeGeometry(
            start,
            defaultEndpoint,
            defaultBulge,
            out _,
            out double defaultRadius,
            out _,
            out double defaultSweep));
        Assert.True(CadPolylineAuthoringSession.TryGetBulgeGeometry(
            start,
            clockwiseEndpoint,
            clockwiseBulge,
            out _,
            out double clockwiseRadius,
            out _,
            out double clockwiseSweep));
        Assert.Equal(10.0, defaultRadius, 12);
        Assert.Equal(defaultRadius, clockwiseRadius, 12);
        Assert.Equal(expectedSweepMagnitude, defaultSweep, 12);
        Assert.Equal(-expectedSweepMagnitude, clockwiseSweep, 12);

        AssertMatchesArcSolver(
            (start, defaultEndpoint, defaultBulge),
            CreateScalarArc(
                CadArcAuthoringMode.StartEndRadius,
                start,
                end,
                signedRadius));
    }

    [Fact]
    public void ClockwiseOverridePreservesCenterAndDirectionCircle()
    {
        AssertClockwiseCounterpart(
            CadPolylineArcConstruction.Center,
            new CadPoint3D(10, 0, 0),
            CadPoint3D.Zero,
            new CadPoint3D(0, 10, 0),
            expectedDefaultSweep: Math.PI / 2.0,
            expectedClockwiseSweep: -Math.PI * 3.0 / 2.0);
        AssertClockwiseCounterpart(
            CadPolylineArcConstruction.Direction,
            CadPoint3D.Zero,
            new CadPoint3D(10, 0, 0),
            new CadPoint3D(10, 10, 0),
            expectedDefaultSweep: Math.PI / 2.0,
            expectedClockwiseSweep: -Math.PI * 3.0 / 2.0);
    }

    [Fact]
    public void ExplicitThreePointArcIsStableAtLargeWcsOrigin()
    {
        CadPoint3D start = new(1_000_000_000_005, -2_000_000_000_000, 9);
        (CadPoint3D _, CadPoint3D endpoint, double bulge) = CreateExplicitArc(
            start,
            CadPolylineArcConstruction.ThreePoint,
            controlPoint: new CadPoint3D(
                1_000_000_000_000,
                -1_999_999_999_995,
                9),
            endpointInput: new CadPoint3D(
                999_999_999_995,
                -2_000_000_000_000,
                9));

        Assert.True(CadPolylineAuthoringSession.TryGetBulgeGeometry(
            start,
            endpoint,
            bulge,
            out CadPoint3D center,
            out double radius,
            out _,
            out double sweep));
        Assert.Equal(
            new CadPoint3D(1_000_000_000_000, -2_000_000_000_000, 9),
            center);
        Assert.Equal(5.0, radius, 12);
        Assert.Equal(Math.PI, Math.Abs(sweep), 12);
    }

    [Fact]
    public void WidthAndHalfwidthPersistEndWidthForFollowingSegments()
    {
        var authoring = new CadPolylineAuthoringSession(initialWidth: 2.0);
        Assert.True(authoring.TryAcceptPoint(CadPoint3D.Zero, out _));

        Assert.True(authoring.TryBeginWidthInput(
            CadPolylineWidthInputMode.Width,
            out _));
        Assert.Equal(CadPolylineAuthoringPrompt.StartingWidth, authoring.Prompt);
        Assert.True(authoring.TryAcceptDefaultWidthValue(out _));
        Assert.Equal(CadPolylineAuthoringPrompt.EndingWidth, authoring.Prompt);
        Assert.True(authoring.TryAcceptWidthValue(4.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.Point, authoring.Prompt);
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));

        Assert.Equal(2.0, authoring.StartWidths.Span[0]);
        Assert.Equal(4.0, authoring.EndWidths.Span[0]);
        Assert.Equal(4.0, authoring.NextStartWidth);
        Assert.Equal(4.0, authoring.NextEndWidth);

        Assert.True(authoring.TryBeginWidthInput(
            CadPolylineWidthInputMode.Halfwidth,
            out _));
        Assert.Equal(4.0, authoring.WidthPromptDefault);
        Assert.True(authoring.TryAcceptWidthValue(2.0, out _));
        Assert.Equal(4.0, authoring.WidthPromptDefault);
        Assert.True(authoring.TryAcceptWidthValue(1.0, out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(20, 0, 0), out _));

        Assert.True(authoring.TryCreateSnapshot(false, out var snapshot, out _));
        Assert.NotNull(snapshot);
        Assert.Equal([2.0, 4.0, 0.0], snapshot.StartWidths.ToArray());
        Assert.Equal([4.0, 2.0, 0.0], snapshot.EndWidths.ToArray());
        Assert.Equal(2.0, snapshot.ResultingDefaultWidth);
    }

    [Fact]
    public void LengthContinuesActualLineAndArcEndTangents()
    {
        var line = new CadPolylineAuthoringSession();
        Assert.True(line.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(line.TryAcceptPoint(new CadPoint3D(3, 4, 0), out _));
        Assert.True(line.TryBeginLengthInput(out _));
        Assert.True(line.TryAcceptLength(10.0, out _));
        Assert.Equal(new CadPoint3D(9, 12, 0), line.CurrentPoint);

        var arc = new CadPolylineAuthoringSession();
        Assert.True(arc.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(arc.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));
        arc.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(arc.TryAcceptPoint(new CadPoint3D(20, 10, 0), out _));
        CadPoint3D start = arc.CurrentPoint!.Value;
        CadPoint3D tangent = arc.PreviousSegmentDirection!.Value;
        arc.Mode = CadPolylineAuthoringMode.Line;
        Assert.True(arc.TryBeginLengthInput(out _));
        Assert.True(arc.TryAcceptLength(5.0, out _));

        CadPoint3D delta = arc.CurrentPoint!.Value - start;
        Assert.Equal(5.0, Math.Sqrt(CadPoint3D.Dot(delta, delta)), 12);
        Assert.Equal(0.0, CadPoint3D.Cross(delta, tangent).Z, 12);
        Assert.True(CadPoint3D.Dot(delta, tangent) > 0.0);
    }

    [Fact]
    public void ScalarPromptsRejectInvalidValuesWithoutLosingPromptState()
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.False(authoring.TryBeginWidthInput(
            CadPolylineWidthInputMode.Width,
            out string? beforePoint));
        Assert.Contains("first", beforePoint, StringComparison.OrdinalIgnoreCase);
        Assert.True(authoring.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));

        Assert.True(authoring.TryBeginWidthInput(
            CadPolylineWidthInputMode.Width,
            out _));
        Assert.False(authoring.TryAcceptWidthValue(-1.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.StartingWidth, authoring.Prompt);
        Assert.False(authoring.TryAcceptWidthValue(double.PositiveInfinity, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.StartingWidth, authoring.Prompt);
        Assert.True(authoring.TryAcceptWidthValue(1.0, out _));
        Assert.False(authoring.TryAcceptWidthValue((double)float.MaxValue * 2.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.EndingWidth, authoring.Prompt);
        Assert.True(authoring.TryAcceptWidthValue(1.0, out _));

        Assert.True(authoring.TryBeginLengthInput(out _));
        Assert.False(authoring.TryAcceptLength(0.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.Length, authoring.Prompt);
        Assert.False(authoring.TryAcceptPoint(new CadPoint3D(20, 0, 0), out _));
        Assert.True(authoring.TryAcceptLength(5.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.Point, authoring.Prompt);
    }

    [Fact]
    public void VariableWidthsAndArcsFailClosedBeforeSnapshotMutation()
    {
        var tapered = new CadPolylineAuthoringSession();
        Assert.True(tapered.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(tapered.TryBeginWidthInput(CadPolylineWidthInputMode.Width, out _));
        Assert.True(tapered.TryAcceptWidthValue(1.0, out _));
        Assert.True(tapered.TryAcceptWidthValue(2.0, out _));
        Assert.True(tapered.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));
        tapered.Mode = CadPolylineAuthoringMode.TangentArc;

        Assert.False(tapered.TryAcceptPoint(
            new CadPoint3D(20, 10, 0),
            out string? taperedArc));
        Assert.Contains("uniform", taperedArc, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, tapered.SegmentCount);

        var curved = new CadPolylineAuthoringSession(initialWidth: 2.0);
        Assert.True(curved.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(curved.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));
        curved.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(curved.TryAcceptPoint(new CadPoint3D(20, 10, 0), out _));
        Assert.True(curved.TryBeginWidthInput(CadPolylineWidthInputMode.Width, out _));
        Assert.False(curved.TryAcceptWidthValue(3.0, out string? changedArcWidth));
        Assert.Contains("uniform", changedArcWidth, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CadPolylineAuthoringPrompt.StartingWidth, curved.Prompt);
        Assert.True(curved.TryAcceptWidthValue(2.0, out _));
        Assert.False(curved.TryAcceptWidthValue(3.0, out changedArcWidth));
        Assert.Equal(CadPolylineAuthoringPrompt.EndingWidth, curved.Prompt);
        Assert.True(curved.TryAcceptWidthValue(2.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.Point, curved.Prompt);

        Assert.True(curved.TryUndoLastSegment());
        Assert.True(curved.TryBeginWidthInput(CadPolylineWidthInputMode.Width, out _));
        Assert.True(curved.TryAcceptWidthValue(3.0, out _));
        Assert.True(curved.TryAcceptWidthValue(3.0, out _));
    }

    [Fact]
    public void CloseUsesFlagWithoutDuplicateVertexAndCanUseTangentArc()
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(0, 0, 0), out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 10, 0), out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;

        Assert.True(authoring.TryCreateSnapshot(
            close: true,
            out CadPolylineAuthoringSnapshot? snapshot,
            out _));

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsClosed);
        Assert.Equal(3, snapshot.Points.Length);
        Assert.Equal(3, snapshot.SegmentCount);
        Assert.NotEqual(0.0, snapshot.Bulges.Span[^1]);
    }

    [Fact]
    public void CommandCapturesPropertiesPlinegenAndRoundTripsOneEntity()
    {
        var document = new CadDocument();
        var layer = new Layer("PLINES");
        document.Layers.Add(layer);
        document.Header.CurrentLayerName = layer.Name;
        document.Header.CurrentEntityColor = ACadSharp.Color.Cyan;
        document.Header.CurrentLineTypeName = LineType.ContinuousName;
        document.Header.CurrentEntityLinetypeScale = 2.25;
        document.Header.CurrentEntityLineWeight = LineWeightType.W35;
        document.Header.PolylineLineTypeGeneration = true;
        document.Header.PolylineWidthDefault = 2.0;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var snapshot = new CadPolylineAuthoringSnapshot(
            [
                new CadPoint3D(1, 2, 3),
                new CadPoint3D(5, 2, 3),
                new CadPoint3D(5, 8, 3),
            ],
            [0.0, 0.5, 0.0],
            isClosed: true);
        var command = new CadAddPolylineCommand(snapshot);

        history.Execute(command);

        LwPolyline polyline = Assert.IsType<LwPolyline>(Assert.Single(document.Entities));
        Assert.Same(polyline, command.Polyline);
        Assert.Same(layer, polyline.Layer);
        Assert.Equal(ACadSharp.Color.Cyan, polyline.Color);
        Assert.Equal(LineType.ContinuousName, polyline.LineType.Name);
        Assert.Equal(2.25, polyline.LineTypeScale);
        Assert.Equal(LineWeightType.W35, polyline.LineWeight);
        Assert.True(polyline.IsClosed);
        Assert.True(polyline.Flags.HasFlag(LwPolylineFlags.Plinegen));
        Assert.Equal(2.0, polyline.ConstantWidth);
        Assert.Equal(3.0, polyline.Elevation);
        Assert.Equal(XYZ.AxisZ, polyline.Normal);
        Assert.Equal(3, polyline.Vertices.Count);
        Assert.Equal(0.5, polyline.Vertices[1].Bulge);
        ulong handle = command.CurrentHandle;
        Assert.NotEqual(0UL, handle);

        Assert.True(history.TryUndo(out _));
        Assert.Empty(document.Entities);
        Assert.Equal(0UL, command.CurrentHandle);
        document.Header.CurrentEntityColor = ACadSharp.Color.Red;
        document.Header.CurrentLineTypeName = LineType.ByLayerName;
        Assert.True(history.TryRedo(out _));
        Assert.Same(polyline, Assert.Single(document.Entities));
        Assert.Equal(ACadSharp.Color.Cyan, polyline.Color);
        Assert.Equal(LineType.ContinuousName, polyline.LineType.Name);
        Assert.NotEqual(0UL, command.CurrentHandle);
    }

    [Fact]
    public void CommandRejectsInvalidCeltscaleBeforeMutation()
    {
        var document = new CadDocument();
        document.Header.CurrentEntityLinetypeScale = 0.0;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [CadPoint3D.Zero, new CadPoint3D(10, 0, 0)],
                [0.0, 0.0],
                isClosed: false));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => history.Execute(command));

        Assert.Contains("CELTSCALE", exception.Message, StringComparison.Ordinal);
        Assert.Empty(document.Entities);
        Assert.Equal(0, history.UndoCount);
        Assert.Null(command.Polyline);
    }

    [Fact]
    public void CommandRejectsLockedLayerBeforeMutation()
    {
        var document = new CadDocument();
        document.Header.CurrentLayer.Flags |= LayerFlags.Locked;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [CadPoint3D.Zero, new CadPoint3D(10, 0, 0)],
                [0.0, 0.0],
                isClosed: false));

        Assert.Throws<InvalidOperationException>(() => history.Execute(command));
        Assert.Empty(document.Entities);
        Assert.Equal(0, history.UndoCount);
        Assert.Null(command.Polyline);
    }

    [Fact]
    public void CommandAuthorsNonzeroPlinewidWithFillModeOffForSnapshotOutline()
    {
        var document = new CadDocument();
        document.Header.PolylineWidthDefault = 2.0;
        document.Header.FillMode = false;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [CadPoint3D.Zero, new CadPoint3D(10, 0, 0)],
                [0.0, 0.0],
                isClosed: false));

        history.Execute(command);

        LwPolyline polyline = Assert.IsType<LwPolyline>(Assert.Single(document.Entities));
        Assert.Equal(2.0, polyline.ConstantWidth);
        Assert.Equal(1, history.UndoCount);
        Assert.Same(polyline, command.Polyline);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        Assert.False(Assert.Single(snapshot.Polylines.ToArray()).IsFillEnabled);
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand outline = Assert.Single(
            scene.DrawingContext.Commands.ToArray());
        Assert.NotNull(outline.Pen);
        Assert.Null(outline.Brush);
    }

    [Fact]
    public void TaperedCommandPersistsVertexWidthsPlinewidAndEntityIdentity()
    {
        var document = new CadDocument();
        document.Header.PolylineWidthDefault = 7.0;
        document.Header.PolylineLineTypeGeneration = true;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [
                    CadPoint3D.Zero,
                    new CadPoint3D(10, 0, 0),
                    new CadPoint3D(20, 0, 0),
                ],
                [0.0, 0.0, 0.0],
                isClosed: false,
                startWidths: [2.0, 4.0, 0.0],
                endWidths: [4.0, 3.0, 0.0],
                resultingDefaultWidth: 3.0));

        history.Execute(command);

        LwPolyline polyline = Assert.IsType<LwPolyline>(Assert.Single(document.Entities));
        Assert.Equal(0.0, polyline.ConstantWidth);
        Assert.False(polyline.Flags.HasFlag(LwPolylineFlags.Plinegen));
        Assert.Equal(2.0, polyline.Vertices[0].StartWidth);
        Assert.Equal(4.0, polyline.Vertices[0].EndWidth);
        Assert.Equal(4.0, polyline.Vertices[1].StartWidth);
        Assert.Equal(3.0, polyline.Vertices[1].EndWidth);
        Assert.Equal(3.0, document.Header.PolylineWidthDefault);

        Assert.True(history.TryUndo(out _));
        Assert.Empty(document.Entities);
        Assert.Equal(7.0, document.Header.PolylineWidthDefault);
        Assert.True(history.TryRedo(out _));
        Assert.Same(polyline, Assert.Single(document.Entities));
        Assert.Equal(3.0, document.Header.PolylineWidthDefault);
    }

    [Fact]
    public void UniformExplicitWidthsCollapseToConstantWidth()
    {
        var document = new CadDocument();
        document.Header.PolylineLineTypeGeneration = true;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        history.Execute(new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [CadPoint3D.Zero, new CadPoint3D(10, 0, 0)],
                [0.0, 0.0],
                isClosed: false,
                startWidths: [2.5, 0.0],
                endWidths: [2.5, 0.0],
                resultingDefaultWidth: 2.5)));

        LwPolyline polyline = Assert.IsType<LwPolyline>(Assert.Single(document.Entities));
        Assert.Equal(2.5, polyline.ConstantWidth);
        Assert.True(polyline.Flags.HasFlag(LwPolylineFlags.Plinegen));
        Assert.Equal(0.0, polyline.Vertices[0].StartWidth);
        Assert.Equal(0.0, polyline.Vertices[0].EndWidth);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AnalyticPolylineRoundTripsThroughCadStore(
        CadDocumentFormat format)
    {
        var session = new CadDocumentSession(new CadDocument());
        session.Edit(
            "Set PLINE width",
            document => document.Header.PolylineWidthDefault = 2.5);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [
                    new CadPoint3D(-2, 3, 4),
                    new CadPoint3D(5, 3, 4),
                    new CadPoint3D(5, 9, 4),
                ],
                [0.0, Math.Sqrt(2.0) - 1.0, -0.25],
                isClosed: true)));
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
            sourceName: $"polyline-authoring.{format.ToString().ToLowerInvariant()}");

        LwPolyline polyline = Assert.Single(loaded.Session.Read(document =>
            document.Entities.OfType<LwPolyline>().ToArray()));
        Assert.True(polyline.IsClosed);
        Assert.Equal(3, polyline.Vertices.Count);
        Assert.Equal(4.0, polyline.Elevation);
        Assert.Equal(2.5, polyline.ConstantWidth);
        Assert.Equal(Math.Sqrt(2.0) - 1.0, polyline.Vertices[1].Bulge, 12);
        Assert.Equal(-0.25, polyline.Vertices[2].Bulge, 12);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AuthoredTaperedWidthsRoundTripThroughCadStore(
        CadDocumentFormat format)
    {
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [
                    CadPoint3D.Zero,
                    new CadPoint3D(10, 0, 0),
                    new CadPoint3D(20, 5, 0),
                ],
                [0.0, 0.0, 0.0],
                isClosed: false,
                startWidths: [1.0, 3.0, 0.0],
                endWidths: [3.0, 2.0, 0.0],
                resultingDefaultWidth: 2.0)));
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
            sourceName: $"tapered-polyline-authoring.{format.ToString().ToLowerInvariant()}");

        LwPolyline polyline = Assert.Single(loaded.Session.Read(document =>
            document.Entities.OfType<LwPolyline>().ToArray()));
        Assert.Equal(0.0, polyline.ConstantWidth);
        Assert.Equal(1.0, polyline.Vertices[0].StartWidth);
        Assert.Equal(3.0, polyline.Vertices[0].EndWidth);
        Assert.Equal(3.0, polyline.Vertices[1].StartWidth);
        Assert.Equal(2.0, polyline.Vertices[1].EndWidth);
        Assert.Equal(2.0, loaded.Session.Read(document =>
            document.Header.PolylineWidthDefault));
    }

    private static (CadPoint3D Start, CadPoint3D Endpoint, double Bulge) CreateExplicitArc(
        CadPoint3D start,
        CadPolylineArcConstruction construction,
        double scalar = 0.0,
        CadPoint3D controlPoint = default,
        CadPoint3D endpointInput = default)
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(start, out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(authoring.TryBeginArcConstruction(construction, out _));
        if (construction is CadPolylineArcConstruction.IncludedAngle or
            CadPolylineArcConstruction.Radius)
        {
            Assert.True(authoring.TryAcceptArcScalar(scalar, out _));
        }
        else
        {
            Assert.True(authoring.TryAcceptArcControlPoint(controlPoint, out _));
        }
        Assert.True(authoring.TryAcceptArcEndpoint(
            endpointInput,
            out CadPoint3D endpoint,
            out _));
        return (start, endpoint, authoring.Bulges.Span[0]);
    }

    private static CadArcAuthoringSnapshot CreateScalarArc(
        CadArcAuthoringMode mode,
        CadPoint3D start,
        CadPoint3D end,
        double scalar)
    {
        var authoring = new CadArcAuthoringSession(mode);
        Assert.True(authoring.TryAcceptIntermediatePoint(start, out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(end, out _));
        Assert.True(authoring.TryCreateSnapshotFromScalar(
            scalar,
            out CadArcAuthoringSnapshot snapshot,
            out _));
        return snapshot;
    }

    private static CadArcAuthoringSnapshot CreateCenterScalarArc(
        CadArcAuthoringMode mode,
        CadPoint3D start,
        CadPoint3D center,
        double scalar)
    {
        var authoring = new CadArcAuthoringSession(mode);
        Assert.True(authoring.TryAcceptIntermediatePoint(start, out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(center, out _));
        Assert.True(authoring.TryCreateSnapshotFromScalar(
            scalar,
            out CadArcAuthoringSnapshot snapshot,
            out _));
        return snapshot;
    }

    private static (CadPoint3D Start, CadPoint3D Endpoint, double Bulge)
        CreateNestedPointArc(
            CadPoint3D start,
            CadPolylineArcConstruction construction,
            double scalar,
            CadPolylineArcNestedOption option,
            CadPoint3D point,
            double nestedScalar = 0.0)
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(start, out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(authoring.TryBeginArcConstruction(construction, out _));
        Assert.True(authoring.TryAcceptArcScalar(scalar, out _));
        Assert.True(authoring.TryBeginArcNestedOption(option, out _));
        if (option == CadPolylineArcNestedOption.Radius)
        {
            Assert.True(authoring.TryAcceptArcScalar(nestedScalar, out _));
        }
        Assert.True(authoring.TryAcceptArcNestedPoint(
            point,
            out CadPoint3D endpoint,
            out _));
        return (start, endpoint, authoring.Bulges.Span[0]);
    }

    private static (CadPoint3D Start, CadPoint3D Endpoint, double Bulge)
        CreateNestedScalarArc(
            CadPoint3D start,
            CadPoint3D center,
            CadPolylineArcNestedOption option,
            double scalar)
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(start, out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(authoring.TryBeginArcConstruction(
            CadPolylineArcConstruction.Center,
            out _));
        Assert.True(authoring.TryAcceptArcControlPoint(center, out _));
        Assert.True(authoring.TryBeginArcNestedOption(option, out _));
        Assert.True(authoring.TryAcceptArcNestedScalar(
            scalar,
            out CadPoint3D endpoint,
            out _));
        return (start, endpoint, authoring.Bulges.Span[0]);
    }

    private static CadArcAuthoringSnapshot CreatePointArc(
        CadArcAuthoringMode mode,
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D final)
    {
        var authoring = new CadArcAuthoringSession(mode);
        Assert.True(authoring.TryAcceptIntermediatePoint(first, out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(second, out _));
        Assert.True(authoring.TryCreateSnapshot(
            final,
            out CadArcAuthoringSnapshot snapshot,
            out _));
        return snapshot;
    }

    private static CadArcAuthoringSnapshot CreateDirectionArc(
        CadPoint3D start,
        CadPoint3D end,
        CadPoint3D direction)
    {
        var authoring = new CadArcAuthoringSession(
            CadArcAuthoringMode.StartEndDirection);
        Assert.True(authoring.TryAcceptIntermediatePoint(start, out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(end, out _));
        Assert.True(authoring.TryCreateSnapshotFromDirection(
            direction,
            out CadArcAuthoringSnapshot snapshot,
            out _));
        return snapshot;
    }

    private static void AssertMatchesArcSolver(
        (CadPoint3D Start, CadPoint3D Endpoint, double Bulge) polyline,
        CadArcAuthoringSnapshot arc)
    {
        Assert.True(CadPolylineAuthoringSession.TryGetBulgeGeometry(
            polyline.Start,
            polyline.Endpoint,
            polyline.Bulge,
            out CadPoint3D center,
            out double radius,
            out _,
            out double sweep));
        Assert.Equal(arc.Center.X, center.X, 10);
        Assert.Equal(arc.Center.Y, center.Y, 10);
        Assert.Equal(arc.Center.Z, center.Z, 10);
        Assert.Equal(arc.Radius, radius, 10);
        Assert.Equal(arc.SweepAngle, Math.Abs(sweep), 10);
    }

    private static void AssertClockwiseCounterpart(
        CadPolylineArcConstruction construction,
        CadPoint3D start,
        CadPoint3D control,
        CadPoint3D endpointInput,
        double expectedDefaultSweep,
        double expectedClockwiseSweep)
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(start, out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(authoring.TryBeginArcConstruction(construction, out _));
        Assert.True(authoring.TryAcceptArcControlPoint(control, out _));
        Assert.True(authoring.TryResolvePendingSegment(
            endpointInput,
            clockwiseOverride: false,
            out CadPoint3D defaultEndpoint,
            out double defaultBulge,
            out _));
        Assert.True(authoring.TryResolvePendingSegment(
            endpointInput,
            clockwiseOverride: true,
            out CadPoint3D clockwiseEndpoint,
            out double clockwiseBulge,
            out _));
        Assert.Equal(defaultEndpoint, clockwiseEndpoint);
        Assert.True(CadPolylineAuthoringSession.TryGetBulgeGeometry(
            start,
            defaultEndpoint,
            defaultBulge,
            out CadPoint3D defaultCenter,
            out double defaultRadius,
            out _,
            out double defaultSweep));
        Assert.True(CadPolylineAuthoringSession.TryGetBulgeGeometry(
            start,
            clockwiseEndpoint,
            clockwiseBulge,
            out CadPoint3D clockwiseCenter,
            out double clockwiseRadius,
            out _,
            out double clockwiseSweep));
        Assert.Equal(defaultCenter.X, clockwiseCenter.X, 12);
        Assert.Equal(defaultCenter.Y, clockwiseCenter.Y, 12);
        Assert.Equal(defaultRadius, clockwiseRadius, 12);
        Assert.Equal(expectedDefaultSweep, defaultSweep, 12);
        Assert.Equal(expectedClockwiseSweep, clockwiseSweep, 12);
    }
}

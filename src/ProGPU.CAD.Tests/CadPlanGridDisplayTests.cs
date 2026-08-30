using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Microsoft.UI.Xaml;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;
using Key = Silk.NET.Input.Key;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadPlanGridDisplayTests
{
    [Fact]
    public void SnapshotCapturesDisplayIndependentlyFromSnapMode()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Configure active display grid", document =>
        {
            document.Header.ModelSpaceLimitsMin = new XY(-20.0, -10.0);
            document.Header.ModelSpaceLimitsMax = new XY(80.0, 60.0);
            VPort active = document.VPorts[VPort.DefaultName];
            active.ShowGrid = true;
            active.SnapOn = false;
            active.IsometricSnap = false;
            active.Origin = new XYZ(100.0, 200.0, 3.0);
            active.XAxis = new XYZ(0.0, 1.0, 0.0);
            active.YAxis = new XYZ(-1.0, 0.0, 0.0);
            active.SnapBasePoint = new XY(2.0, 3.0);
            active.SnapRotation = Math.PI / 2.0;
            active.GridSpacing = new XY(2.0, 4.0);
            active.GridFlags = (GridFlags)(1 | 2 | 4 | 8);
            active.MinorGridLinesPerMajorGridLine = 10;
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadPlanGridDisplaySettings settings = snapshot.PlanGridDisplaySettings;

        Assert.True(settings.IsVisible);
        Assert.True(settings.IsSupported);
        Assert.False(snapshot.PlanGridSnapSettings.IsEnabled);
        Assert.Equal(CadPlanGridDisplayStyle.RectangularDots, settings.Style);
        AssertPoint(new CadPoint3D(97.0, 202.0, 3.0), settings.Origin);
        AssertPoint(new CadPoint3D(-1.0, 0.0, 0.0), settings.XAxis);
        AssertPoint(new CadPoint3D(0.0, -1.0, 0.0), settings.YAxis);
        Assert.Equal(2.0, settings.SpacingX);
        Assert.Equal(4.0, settings.SpacingY);
        Assert.True(settings.IsAdaptive);
        Assert.True(settings.AllowsSubdivision);
        Assert.True(settings.ShowsBeyondLimits);
        Assert.True(settings.FollowsDynamicUcs);
        Assert.Equal(10, settings.MinorLinesPerMajorLine);
        AssertPoint(new CadPoint3D(-20.0, -10.0, 0.0), settings.Limits.Min);
        AssertPoint(new CadPoint3D(80.0, 60.0, 0.0), settings.Limits.Max);
    }

    [Fact]
    public void AdaptivePlanCoarsensBothAxesByMajorCadenceAndKeepsAffineOrigin()
    {
        CadPlanGridDisplaySettings settings = CreateSettings(
            spacingX: 1.0,
            spacingY: 2.0,
            rotationRadians: Math.PI / 6.0,
            isAdaptive: true,
            allowsSubdivision: false,
            showsBeyondLimits: true,
            cadence: 5);
        var viewport = new CadPlanViewport(
            CadPoint3D.Zero,
            new Vector2(200.0f, 120.0f),
            new Vector2(7.0f, -3.0f),
            1.0f);

        Assert.True(CadPlanGridDisplayPlan.TryCreate(settings, viewport, out var plan));

        Assert.Equal(new Vector2(25.0f, 50.0f), plan.Spacing);
        Assert.Equal(5, plan.MinorLinesPerMajorLine);
        Vector2 transformedOrigin = Vector2.Transform(Vector2.Zero, plan.Transform);
        Vector2 expectedOrigin = viewport.WorldToScreen(settings.Origin);
        AssertVector(expectedOrigin, transformedOrigin, 0.0001f);
        Assert.Equal(new Rect(0.0f, 0.0f, 200.0f, 120.0f), plan.ScreenClip);
    }

    [Fact]
    public void AdaptiveSubdivisionAndDrawingLimitsRemainBounded()
    {
        CadPlanGridDisplaySettings settings = CreateSettings(
            spacingX: 100.0,
            spacingY: 200.0,
            rotationRadians: 0.0,
            isAdaptive: true,
            allowsSubdivision: true,
            showsBeyondLimits: false,
            cadence: 5,
            limits: new CadBounds3D(
                new CadPoint3D(-20.0, -10.0, 0.0),
                new CadPoint3D(30.0, 15.0, 0.0)));
        var viewport = new CadPlanViewport(
            CadPoint3D.Zero,
            new Vector2(200.0f, 100.0f),
            Vector2.Zero,
            1.0f);

        Assert.True(CadPlanGridDisplayPlan.TryCreate(settings, viewport, out var plan));

        Assert.Equal(new Vector2(20.0f, 40.0f), plan.Spacing);
        Assert.Equal(new Rect(80.0f, 35.0f, 50.0f, 25.0f), plan.ScreenClip);
    }

    [Fact]
    public void IsometricAndEdgeOnGridPlanesFailClosed()
    {
        CadPlanGridDisplaySettings isometric = CreateSettings(
            style: CadPlanGridDisplayStyle.Isometric);
        var edgeOn = new CadPlanGridDisplaySettings(
            true,
            CadPlanGridDisplayStyle.RectangularDots,
            CadPoint3D.Zero,
            new CadPoint3D(1.0, 0.0, 0.0),
            new CadPoint3D(0.0, 0.0, 1.0),
            1.0,
            1.0,
            true,
            false,
            true,
            false,
            5,
            new CadBounds3D(CadPoint3D.Zero, new CadPoint3D(10.0, 10.0, 0.0)));
        var viewport = new CadPlanViewport(
            CadPoint3D.Zero,
            new Vector2(100.0f),
            Vector2.Zero,
            1.0f);

        Assert.False(CadPlanGridDisplayPlan.TryCreate(isometric, viewport, out _));
        Assert.False(CadPlanGridDisplayPlan.TryCreate(edgeOn, viewport, out _));
    }

    [Fact]
    public void WarmPlanCreationAllocatesNoManagedMemory()
    {
        CadPlanGridDisplaySettings settings = CreateSettings(
            rotationRadians: Math.PI / 7.0);
        var viewport = new CadPlanViewport(
            new CadPoint3D(1_000_000.0, -2_000_000.0, 0.0),
            new Vector2(800.0f, 600.0f),
            new Vector2(13.0f, -9.0f),
            2.0f);
        Assert.True(CadPlanGridDisplayPlan.TryCreate(settings, viewport, out _));

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allCreated = true;
        for (int index = 0; index < 1_024; index++)
        {
            allCreated &= CadPlanGridDisplayPlan.TryCreate(settings, viewport, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allCreated);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SharedCanvasRecordsGridBeforeRetainedCadPictureAsOneDraw()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(new XYZ(-10.0, 0.0, 0.0), new XYZ(10.0, 0.0, 0.0)));
        VPort active = document.VPorts[VPort.DefaultName];
        active.ShowGrid = true;
        active.IsometricSnap = false;
        active.GridSpacing = new XY(2.0, 4.0);
        active.GridFlags = (GridFlags)(1 | 2);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(new CadDocumentSession(document));
            canvas.Arrange(new Rect(0.0f, 0.0f, 400.0f, 300.0f));
            var context = new DrawingContext();

            canvas.OnRender(context);

            RenderCommand[] draws = context.Commands
                .Where(command => command.Type is
                    RenderCommandType.DrawDeviceDotGrid or
                    RenderCommandType.DrawPicture)
                .ToArray();
            Assert.Equal(2, draws.Length);
            Assert.Equal(RenderCommandType.DrawDeviceDotGrid, draws[0].Type);
            Assert.Equal(1.0f, draws[0].RadiusX);
            Assert.Equal(5.0f, draws[0].RadiusY);
            Assert.Equal(RenderCommandType.DrawPicture, draws[1].Type);

            canvas.PlanGridPresentationStyle = CadPlanGridPresentationStyle.Dots;
            var dottedContext = new DrawingContext();
            canvas.OnRender(dottedContext);
            RenderCommand dotted = Assert.Single(
                dottedContext.Commands,
                command => command.Type == RenderCommandType.DrawDeviceDotGrid);
            Assert.Equal(0.75f, dotted.RadiusX);
            Assert.Equal(0.0f, dotted.RadiusY);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void GridDisplayEditUsesOneGenerationAndExactUndoRedo()
    {
        var document = new CadDocument(ACadVersion.AC1032);
        VPort active = document.VPorts[VPort.DefaultName];
        active.ShowGrid = true;
        active.GridSpacing = new XY(2.0, 4.0);
        active.GridFlags = (GridFlags)(1 | 8 | 32);
        active.MinorGridLinesPerMajorGridLine = 5;
        active.SnapOn = true;
        active.SnapSpacing = new XY(3.0, 6.0);
        active.SnapBasePoint = new XY(7.0, 9.0);
        active.SnapRotation = 0.25;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var values = new CadPlanGridDisplayEditValues(
            false,
            0.0,
            0.0,
            true,
            true,
            false,
            17);

        ulong applied = history.Execute(
            new CadSetPlanGridDisplayCommand(values));

        Assert.Equal(1UL, applied);
        Assert.Equal(values, session.Read(CadPlanGridDisplayEditValues.Capture));
        Assert.Equal(
            (GridFlags)(2 | 4 | 8 | 32),
            session.Read(d => d.VPorts[VPort.DefaultName].GridFlags));
        Assert.Equal(new XY(3.0, 6.0),
            session.Read(d => d.VPorts[VPort.DefaultName].SnapSpacing));
        Assert.Equal(new XY(7.0, 9.0),
            session.Read(d => d.VPorts[VPort.DefaultName].SnapBasePoint));
        Assert.Equal(0.25,
            session.Read(d => d.VPorts[VPort.DefaultName].SnapRotation));
        Assert.True(session.Read(d => d.VPorts[VPort.DefaultName].SnapOn));
        CadPlanGridDisplaySettings appliedDisplay =
            new CadSnapshotCompiler().Compile(session).PlanGridDisplaySettings;
        Assert.Equal(3.0, appliedDisplay.SpacingX);
        Assert.Equal(6.0, appliedDisplay.SpacingY);

        Assert.True(history.TryUndo(out ulong undone));
        Assert.Equal(2UL, undone);
        Assert.True(session.Read(d => d.VPorts[VPort.DefaultName].ShowGrid));
        Assert.Equal(new XY(2.0, 4.0),
            session.Read(d => d.VPorts[VPort.DefaultName].GridSpacing));
        Assert.Equal((GridFlags)(1 | 8 | 32),
            session.Read(d => d.VPorts[VPort.DefaultName].GridFlags));
        Assert.Equal(5,
            session.Read(d => d.VPorts[VPort.DefaultName]
                .MinorGridLinesPerMajorGridLine));

        Assert.True(history.TryRedo(out ulong redone));
        Assert.Equal(3UL, redone);
        Assert.Equal(values, session.Read(CadPlanGridDisplayEditValues.Capture));
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetPlanGridDisplayCommand(values)));
        Assert.Equal(redone, session.ContentGeneration);
    }

    [Fact]
    public void GridDisplayEditValuesRejectMalformedPersistedRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadPlanGridDisplayEditValues(
                true, -1.0, 1.0, true, false, true, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadPlanGridDisplayEditValues(
                true, 1.0, double.NaN, true, false, true, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadPlanGridDisplayEditValues(
                true, 1.0, 1.0, true, false, true, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadPlanGridDisplayEditValues(
                true, 1.0, 1.0, true, false, true, 101));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task EditedGridDisplaySurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(
            ACadVersion.AC1032);
        var expected = new CadPlanGridDisplayEditValues(
            false,
            2.5,
            7.25,
            true,
            true,
            false,
            17);
        new CadDocumentHistory(session).Execute(
            new CadSetPlanGridDisplayCommand(expected));
        using var stream = new MemoryStream();
        var store = new CadDocumentStore();

        await store.SaveAsync(
            session,
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"grid-display.{format.ToString().ToLowerInvariant()}");

        Assert.Equal(
            expected,
            loaded.Session.Read(CadPlanGridDisplayEditValues.Capture));
        CadPlanGridDisplaySettings display =
            new CadSnapshotCompiler().Compile(loaded.Session)
                .PlanGridDisplaySettings;
        Assert.Equal(2.5, display.SpacingX);
        Assert.Equal(7.25, display.SpacingY);
        Assert.False(display.IsVisible);
        Assert.True(display.IsAdaptive);
        Assert.True(display.AllowsSubdivision);
        Assert.False(display.ShowsBeyondLimits);
        Assert.Equal(17, display.MinorLinesPerMajorLine);
    }

    [Fact]
    public void SharedViewEditsPersistedGridWithoutChangingPointSnap()
    {
        var document = new CadDocument(ACadVersion.AC1032);
        VPort active = document.VPorts[VPort.DefaultName];
        active.ShowGrid = true;
        active.GridSpacing = new XY(2.0, 4.0);
        active.GridFlags = GridFlags._1 | GridFlags._2;
        active.MinorGridLinesPerMajorGridLine = 5;
        active.SnapOn = true;
        active.SnapSpacing = new XY(3.0, 6.0);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);

            Assert.True(view.PlanGridDisplayCheckBox.IsChecked);
            Assert.Equal("2", view.PlanGridUnitXInput.Text);
            Assert.Equal("4", view.PlanGridUnitYInput.Text);
            Assert.True(view.PlanGridAdaptiveCheckBox.IsChecked);
            Assert.False(view.PlanGridSubdivisionCheckBox.IsChecked);
            Assert.True(view.PlanGridBeyondLimitsCheckBox.IsChecked);
            Assert.Equal("5", view.PlanGridMajorInput.Text);
            Assert.False(view.PlanGridDotsCheckBox.IsChecked);
            Assert.Equal(
                CadPlanGridPresentationStyle.Lines,
                view.Canvas.PlanGridPresentationStyle);

            ulong generationBeforeStyleChange = session.ContentGeneration;
            view.PlanGridDotsCheckBox.IsChecked = true;
            Assert.Equal(
                CadPlanGridPresentationStyle.Dots,
                view.Canvas.PlanGridPresentationStyle);
            Assert.Equal(generationBeforeStyleChange, session.ContentGeneration);
            view.PlanGridDotsCheckBox.IsChecked = false;
            Assert.Equal(
                CadPlanGridPresentationStyle.Lines,
                view.Canvas.PlanGridPresentationStyle);
            Assert.Equal(generationBeforeStyleChange, session.ContentGeneration);

            view.PlanGridDisplayCheckBox.IsChecked = false;
            view.PlanGridUnitXInput.Text = "0";
            view.PlanGridUnitYInput.Text = "0";
            view.PlanGridAdaptiveCheckBox.IsChecked = true;
            view.PlanGridSubdivisionCheckBox.IsChecked = true;
            view.PlanGridBeyondLimitsCheckBox.IsChecked = false;
            view.PlanGridMajorInput.Text = "17";

            Assert.True(view.ApplyPlanGridDisplayButton.IsEnabled);
            view.ApplyPlanGridDisplayButton.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Key.Enter,
            });

            CadPlanGridDisplayEditValues persisted =
                session.Read(CadPlanGridDisplayEditValues.Capture);
            Assert.False(persisted.IsVisible);
            Assert.Equal(0.0, persisted.GridUnitX);
            Assert.Equal(0.0, persisted.GridUnitY);
            Assert.True(persisted.IsAdaptive);
            Assert.True(persisted.AllowsSubdivision);
            Assert.False(persisted.ShowsBeyondLimits);
            Assert.Equal(17, persisted.MinorLinesPerMajorLine);
            Assert.True(session.Read(d => d.VPorts[VPort.DefaultName].SnapOn));
            Assert.Equal(new XY(3.0, 6.0),
                session.Read(d => d.VPorts[VPort.DefaultName].SnapSpacing));
            Assert.True(view.Canvas.IsPlanGridSnapEnabled);
            Assert.Equal(3.0, view.Canvas.PlanGridDisplaySettings.SpacingX);
            Assert.Equal(6.0, view.Canvas.PlanGridDisplaySettings.SpacingY);
            Assert.False(view.ApplyPlanGridDisplayButton.IsEnabled);

            Assert.True(view.Canvas.TryUndo());
            Assert.True(view.PlanGridDisplayCheckBox.IsChecked);
            Assert.Equal("2", view.PlanGridUnitXInput.Text);
            Assert.Equal("4", view.PlanGridUnitYInput.Text);
            Assert.True(view.Canvas.IsPlanGridSnapEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    private static CadPlanGridDisplaySettings CreateSettings(
        double spacingX = 10.0,
        double spacingY = 10.0,
        double rotationRadians = 0.0,
        bool isAdaptive = true,
        bool allowsSubdivision = false,
        bool showsBeyondLimits = true,
        int cadence = 5,
        CadPlanGridDisplayStyle style = CadPlanGridDisplayStyle.RectangularDots,
        CadBounds3D? limits = null)
    {
        double cosine = Math.Cos(rotationRadians);
        double sine = Math.Sin(rotationRadians);
        return new CadPlanGridDisplaySettings(
            true,
            style,
            new CadPoint3D(10.0, -20.0, 0.0),
            new CadPoint3D(cosine, sine, 0.0),
            new CadPoint3D(-sine, cosine, 0.0),
            spacingX,
            spacingY,
            isAdaptive,
            allowsSubdivision,
            showsBeyondLimits,
            false,
            cadence,
            limits ?? new CadBounds3D(
                new CadPoint3D(-100.0, -100.0, 0.0),
                new CadPoint3D(100.0, 100.0, 0.0)));
    }

    private static void AssertPoint(
        CadPoint3D expected,
        CadPoint3D actual,
        double tolerance = 1e-10)
    {
        Assert.InRange(Math.Abs(actual.X - expected.X), 0.0, tolerance);
        Assert.InRange(Math.Abs(actual.Y - expected.Y), 0.0, tolerance);
        Assert.InRange(Math.Abs(actual.Z - expected.Z), 0.0, tolerance);
    }

    private static void AssertVector(Vector2 expected, Vector2 actual, float tolerance)
    {
        Assert.InRange(Math.Abs(actual.X - expected.X), 0.0f, tolerance);
        Assert.InRange(Math.Abs(actual.Y - expected.Y), 0.0f, tolerance);
    }
}

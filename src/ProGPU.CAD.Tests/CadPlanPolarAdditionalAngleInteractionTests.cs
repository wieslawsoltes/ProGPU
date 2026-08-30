using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using Microsoft.UI.Xaml;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadPlanPolarAdditionalAngleInteractionTests
{
    [Fact]
    public void SharedControlsReevaluatePendingPointerAndRejectInvalidList()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(
            new CSMath.XYZ(-100.0, 0.0, 0.0),
            new CSMath.XYZ(100.0, 0.0, 0.0)));
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = view.Canvas.CurrentViewport;
            Click(view.Canvas, viewport.WorldToScreen(CadPoint3D.Zero));
            view.Canvas.ObjectSnapModes = CadObjectSnapModes.None;
            view.PlanPolarTrackingCheckBox.IsChecked = true;
            view.PlanPolarAdditionalAnglesInput.Text = "25";
            view.PlanPolarAdditionalAnglesCheckBox.IsChecked = true;
            Assert.True(view.Canvas.BeginSelectionPointTransform(
                CadPointTransformOperation.Move));
            Assert.True(view.Canvas.TryAcceptSelectionPointTransformInput(
                "0,0",
                out _));
            Vector2 pointer = viewport.WorldToScreen(PointAtDegrees(25.5, 10.0));
            view.Canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = pointer,
            });

            CadPlanPolarTrackingResult first =
                view.Canvas.PendingPointTransformPolarTracking!.Value;
            Assert.True(first.IsAdditionalAngle);
            Assert.Equal(25.0, ToDegrees(first.AngleRadians), 10);
            Assert.Equal(0UL, session.ContentGeneration);

            view.PlanPolarAdditionalAnglesInput.Text = "26";

            CadPlanPolarTrackingResult refreshed =
                view.Canvas.PendingPointTransformPolarTracking!.Value;
            Assert.True(refreshed.IsAdditionalAngle);
            Assert.Equal(26.0, ToDegrees(refreshed.AngleRadians), 10);
            Assert.Equal(0UL, session.ContentGeneration);

            view.PlanPolarAdditionalAnglesInput.Text =
                "0;1;2;3;4;5;6;7;8;9;10";

            Assert.False(view.Canvas.UsePlanPolarAdditionalAngles);
            Assert.False(view.PlanPolarAdditionalAnglesCheckBox.IsChecked);
            Assert.Null(view.Canvas.PendingPointTransformPolarTracking);
            Assert.Equal(0UL, session.ContentGeneration);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void AdditionalPathComposesWithPolarSnapAndExactMove()
    {
        var document = new CadDocument();
        var line = new Line(
            new CSMath.XYZ(-2.0, 0.0, 0.0),
            new CSMath.XYZ(2.0, 0.0, 0.0));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = canvas.CurrentViewport;
            Click(canvas, viewport.WorldToScreen(CadPoint3D.Zero));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            canvas.IsPlanPolarTrackingEnabled = true;
            canvas.SetPlanPolarAdditionalAngles(
                CadPlanPolarAdditionalAngles.FromDegrees([25.0]));
            canvas.UsePlanPolarAdditionalAngles = true;
            canvas.PlanPolarSnapDistance = 4.0;
            canvas.IsPlanPolarSnapEnabled = true;
            Assert.True(canvas.BeginSelectionPointTransform(
                CadPointTransformOperation.Move));
            Assert.True(canvas.TryAcceptSelectionPointTransformInput(
                "0,0",
                out _));
            Vector2 pointer = viewport.WorldToScreen(PointAtDegrees(25.2, 10.4));
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = pointer,
            });

            CadPlanPolarTrackingResult tracked =
                canvas.PendingPointTransformPolarTracking!.Value;
            Assert.True(tracked.IsAdditionalAngle);
            Assert.True(tracked.IsDistanceSnapped);
            Assert.Equal(25.0, ToDegrees(tracked.AngleRadians), 10);
            Assert.Equal(12.0, tracked.Distance, 10);
            Click(canvas, pointer);

            Assert.Equal(1UL, session.ContentGeneration);
            double dx = 12.0 * Math.Cos(ToRadians(25.0));
            double dy = 12.0 * Math.Sin(ToRadians(25.0));
            Assert.Equal(-2.0 + dx, line.StartPoint.X, 10);
            Assert.Equal(dy, line.StartPoint.Y, 10);
            Assert.Equal(2.0 + dx, line.EndPoint.X, 10);
            Assert.Equal(dy, line.EndPoint.Y, 10);
            Assert.True(canvas.UsePlanPolarAdditionalAngles);
            Assert.Equal(1, canvas.PlanPolarAdditionalAngles.Count);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void ObjectSnapStillPrecedesAdditionalPolarPath()
    {
        CadPoint3D endpoint = PointAtDegrees(27.0, 10.0);
        var document = new CadDocument();
        var line = new Line(
            CSMath.XYZ.Zero,
            new CSMath.XYZ(endpoint.X, endpoint.Y, endpoint.Z));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = canvas.CurrentViewport;
            Click(canvas, viewport.WorldToScreen(endpoint * 0.5));
            canvas.ObjectSnapModes = CadObjectSnapModes.Endpoint;
            canvas.IsPlanPolarTrackingEnabled = true;
            canvas.SetPlanPolarAdditionalAngles(
                CadPlanPolarAdditionalAngles.FromDegrees([25.0]));
            canvas.UsePlanPolarAdditionalAngles = true;
            Assert.True(canvas.BeginSelectionPointTransform(
                CadPointTransformOperation.Move));
            Assert.True(canvas.TryAcceptSelectionPointTransformInput(
                "0,0",
                out _));
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = viewport.WorldToScreen(endpoint),
            });

            Assert.Equal(
                endpoint,
                canvas.PendingPointTransformObjectSnap!.Value.Point);
            Assert.Null(canvas.PendingPointTransformPolarTracking);
            Assert.Equal(0UL, session.ContentGeneration);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void DirectDistancePreservesTypedLengthOnAdditionalPath()
    {
        var document = new CadDocument();
        var line = new Line(
            new CSMath.XYZ(-2.0, 0.0, 0.0),
            new CSMath.XYZ(2.0, 0.0, 0.0));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = canvas.CurrentViewport;
            Click(canvas, viewport.WorldToScreen(CadPoint3D.Zero));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            canvas.IsPlanPolarTrackingEnabled = true;
            canvas.SetPlanPolarAdditionalAngles(
                CadPlanPolarAdditionalAngles.FromDegrees([25.0]));
            canvas.UsePlanPolarAdditionalAngles = true;
            Assert.True(canvas.BeginSelectionPointTransform(
                CadPointTransformOperation.Move));
            Assert.True(canvas.TryAcceptSelectionPointTransformInput(
                "0,0",
                out _));
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = viewport.WorldToScreen(PointAtDegrees(25.2, 10.4)),
            });
            Assert.True(
                canvas.PendingPointTransformPolarTracking!.Value
                    .IsAdditionalAngle);

            Assert.True(canvas.TryAcceptSelectionPointTransformInput(
                "10",
                out string? error));

            Assert.Null(error);
            Assert.Equal(1UL, session.ContentGeneration);
            double dx = 10.0 * Math.Cos(ToRadians(25.0));
            double dy = 10.0 * Math.Sin(ToRadians(25.0));
            Assert.Equal(-2.0 + dx, line.StartPoint.X, 10);
            Assert.Equal(dy, line.StartPoint.Y, 10);
            Assert.Equal(2.0 + dx, line.EndPoint.X, 10);
            Assert.Equal(dy, line.EndPoint.Y, 10);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    private static void Click(CadSampleCanvas canvas, Vector2 position)
    {
        canvas.OnPointerPressed(new PointerRoutedEventArgs
        {
            Position = position,
            IsLeftButtonPressed = true,
        });
        canvas.OnPointerReleased(new PointerRoutedEventArgs
        {
            Position = position,
        });
    }

    private static CadPoint3D PointAtDegrees(double degrees, double distance)
    {
        double radians = ToRadians(degrees);
        return new CadPoint3D(
            Math.Cos(radians) * distance,
            Math.Sin(radians) * distance,
            0.0);
    }

    private static double ToDegrees(double radians) =>
        radians * (180.0 / Math.PI);

    private static double ToRadians(double degrees) =>
        degrees * (Math.PI / 180.0);
}

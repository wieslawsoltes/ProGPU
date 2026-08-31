using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadPolylineAuthoringInteractionTests
{
    [Fact]
    public void SharedCanvasPublishesStraightAndTangentArcAsOnePolyline()
    {
        var session = new CadDocumentSession(new CadDocument());
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginPolylineAuthoring());
            Assert.True(canvas.TryAcceptPolylineAuthoringInput("0,0,3", out _));
            Assert.True(canvas.TryAcceptPolylineAuthoringInput("10,0,3", out _));
            canvas.PolylineAuthoringMode = CadPolylineAuthoringMode.TangentArc;
            Assert.True(canvas.TryAcceptPolylineAuthoringInput("20,10,3", out _));

            Assert.Equal(2, canvas.PendingPolylineSegmentCount);
            Assert.True(canvas.CanClosePolylineAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(session.Read(document => document.Entities.ToArray()));
            Assert.True(canvas.CompletePolylineAuthoring(close: true, out _));

            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, canvas.UndoCount);
            LwPolyline polyline = Assert.Single(session.Read(document =>
                document.Entities.OfType<LwPolyline>().ToArray()));
            Assert.True(polyline.IsClosed);
            Assert.Equal(3, polyline.Vertices.Count);
            Assert.Equal(Math.Sqrt(2.0) - 1.0, polyline.Vertices[1].Bulge, 12);
            Assert.NotEqual(0.0, polyline.Vertices[2].Bulge);

            Assert.True(canvas.TryUndo());
            Assert.Empty(session.Read(document => document.Entities.ToArray()));
            Assert.True(canvas.TryRedo());
            Assert.Same(polyline, Assert.Single(session.Read(document =>
                document.Entities.OfType<LwPolyline>().ToArray())));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void RelativePolarUsesActualArcEndTangentForDirectDistance()
    {
        var session = new CadDocumentSession(new CadDocument());
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            canvas.PlanPolarTrackingIncrementDegrees = 45.0;
            canvas.PlanPolarAngleMeasurement =
                CadPlanPolarAngleMeasurement.RelativeToLastSegment;
            canvas.IsPlanPolarTrackingEnabled = true;
            Assert.True(canvas.BeginPolylineAuthoring());
            Assert.True(canvas.TryAcceptPolylineAuthoringInput("0,0", out _));
            Assert.True(canvas.TryAcceptPolylineAuthoringInput("10,0", out _));
            canvas.PolylineAuthoringMode = CadPolylineAuthoringMode.TangentArc;
            Assert.True(canvas.TryAcceptPolylineAuthoringInput("20,10", out _));
            CadPoint3D current = canvas.PendingPolylineCurrentPoint!.Value;
            double trackedAngle = Math.PI * 0.75;
            CadPoint3D pointer = current + new CadPoint3D(
                7.0 * Math.Cos(trackedAngle),
                7.0 * Math.Sin(trackedAngle),
                0.0);

            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = canvas.CurrentViewport.WorldToScreen(pointer),
            });

            CadPlanPolarTrackingResult polar =
                canvas.PendingPointTransformPolarTracking!.Value;
            Assert.True(polar.IsRelativeIncrement);
            Assert.InRange(
                Math.Abs(polar.AngleRadians - trackedAngle),
                0.0,
                1e-12);
            Assert.True(canvas.TryAcceptPolylineAuthoringInput("5", out _));
            CadPoint3D delta =
                canvas.PendingPolylineCurrentPoint!.Value - current;
            Assert.InRange(
                Math.Abs(Math.Sqrt(CadPoint3D.Dot(delta, delta)) - 5.0),
                0.0,
                1e-12);
            Assert.True(canvas.CompletePolylineAuthoring(false, out _));
            LwPolyline polyline = Assert.Single(session.Read(document =>
                document.Entities.OfType<LwPolyline>().ToArray()));
            Assert.Equal(Math.Sqrt(2.0) - 1.0, polyline.Vertices[2].Bulge, 12);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedButtonsAndKeyboardExposeModesUndoCloseAndFinish()
    {
        var session = new CadDocumentSession(new CadDocument());
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(view.PolylineButton.IsEnabled);
            PressEnter(view.PolylineButton);
            Assert.True(view.Canvas.IsPolylineAuthoring);
            Assert.True(view.Canvas.TryAcceptPolylineAuthoringInput("0,0", out _));
            Assert.True(view.Canvas.TryAcceptPolylineAuthoringInput("10,0", out _));
            Assert.True(view.PolylineArcModeButton.IsEnabled);
            PressEnter(view.PolylineArcModeButton);
            Assert.Equal(
                CadPolylineAuthoringMode.TangentArc,
                view.Canvas.PolylineAuthoringMode);
            Assert.True(view.Canvas.TryAcceptPolylineAuthoringInput("20,10", out _));
            Assert.True(view.PolylineUndoButton.IsEnabled);
            PressEnter(view.PolylineUndoButton);
            Assert.Equal(1, view.Canvas.PendingPolylineSegmentCount);
            Assert.True(view.Canvas.TryAcceptPolylineAuthoringInput("20,10", out _));

            var lineMode = new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.L };
            view.OnKeyDown(lineMode);
            Assert.True(lineMode.Handled);
            Assert.Equal(
                CadPolylineAuthoringMode.Line,
                view.Canvas.PolylineAuthoringMode);
            var close = new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.C };
            view.OnKeyDown(close);

            Assert.True(close.Handled);
            Assert.False(view.Canvas.IsPolylineAuthoring);
            LwPolyline polyline = Assert.Single(session.Read(document =>
                document.Entities.OfType<LwPolyline>().ToArray()));
            Assert.True(polyline.IsClosed);
            Assert.Equal(3, polyline.Vertices.Count);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void NonzeroPlinewidFailsWithoutPublishingAndKeepsPromptRecoverable()
    {
        var document = new CadDocument();
        document.Header.PolylineWidthDefault = 2.0;
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginPolylineAuthoring());
            Assert.True(canvas.TryAcceptPolylineAuthoringInput("0,0", out _));
            Assert.True(canvas.TryAcceptPolylineAuthoringInput("10,0", out _));

            Assert.False(canvas.CompletePolylineAuthoring(
                close: false,
                out string? error));

            Assert.Contains("wide-polyline", error, StringComparison.OrdinalIgnoreCase);
            Assert.True(canvas.IsPolylineAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(document.Entities);
            Assert.Equal(0, canvas.UndoCount);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void PointerEndpointStaysOnTypedFirstPointElevation()
    {
        var session = new CadDocumentSession(new CadDocument());
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginPolylineAuthoring());
            Assert.True(canvas.TryAcceptPolylineAuthoringInput("0,0,7", out _));
            Vector2 endpoint = canvas.CurrentViewport.WorldToScreen(
                new CadPoint3D(8, 0, 7));

            Click(canvas, endpoint);

            Assert.Equal(new CadPoint3D(8, 0, 7), canvas.PendingPolylineCurrentPoint);
            Assert.True(canvas.CompletePolylineAuthoring(false, out _));
            LwPolyline polyline = Assert.Single(session.Read(document =>
                document.Entities.OfType<LwPolyline>().ToArray()));
            Assert.Equal(7.0, polyline.Elevation);
            Assert.Equal(8.0, polyline.Vertices[1].Location.X);
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

    private static void PressEnter(Microsoft.UI.Xaml.Controls.Button button) =>
        button.OnKeyDown(new KeyRoutedEventArgs
        {
            Key = Silk.NET.Input.Key.Enter,
        });
}

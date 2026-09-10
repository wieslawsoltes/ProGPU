using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadLineAuthoringInteractionTests
{
    [Fact]
    public void TypedSequenceRemainsTransientUntilCloseAndUsesOneHistoryEntry()
    {
        var document = new CadDocument();
        var layer = new Layer("LINES");
        document.Layers.Add(layer);
        document.Header.CurrentLayerName = layer.Name;
        document.Header.CurrentEntityColor = ACadSharp.Color.Green;
        document.Header.CurrentEntityLineWeight = LineWeightType.W30;
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginLineAuthoring());

            Assert.True(canvas.TryAcceptLineAuthoringInput("1,2,3", out _));
            Assert.True(canvas.TryAcceptLineAuthoringInput("@4,0,0", out _));
            Assert.True(canvas.TryAcceptLineAuthoringInput("@0,5,0", out _));

            Assert.Equal(2, canvas.PendingLineSegmentCount);
            Assert.True(canvas.CanCloseLineAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(document.Entities);
            Assert.True(canvas.UndoLineAuthoringSegment());
            Assert.Equal(1, canvas.PendingLineSegmentCount);
            Assert.True(canvas.TryAcceptLineAuthoringInput("@0,6,0", out _));
            Assert.True(canvas.CompleteLineAuthoring(
                close: true,
                out string? error));
            Assert.Null(error);

            Assert.False(canvas.IsLineAuthoring);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, canvas.UndoCount);
            Line[] lines = document.Entities.OfType<Line>().ToArray();
            Assert.Equal(3, lines.Length);
            AssertPoint(new XYZ(1, 2, 3), lines[0].StartPoint);
            AssertPoint(new XYZ(5, 2, 3), lines[0].EndPoint);
            AssertPoint(new XYZ(5, 2, 3), lines[1].StartPoint);
            AssertPoint(new XYZ(5, 8, 3), lines[1].EndPoint);
            AssertPoint(new XYZ(5, 8, 3), lines[2].StartPoint);
            AssertPoint(new XYZ(1, 2, 3), lines[2].EndPoint);
            Assert.All(lines, line =>
            {
                Assert.Same(layer, line.Layer);
                Assert.Equal(ACadSharp.Color.Green, line.Color);
                Assert.Equal(LineWeightType.W30, line.LineWeight);
            });

            Assert.True(canvas.TryUndo());
            Assert.Empty(document.Entities);
            Assert.True(canvas.TryRedo());
            Assert.Equal(3, document.Entities.OfType<Line>().Count());
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void RelativePolarAndDirectDistanceUseLastAcceptedLineSegment()
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
            Assert.True(canvas.BeginLineAuthoring());
            Assert.True(canvas.TryAcceptLineAuthoringInput("0,0", out _));
            double referenceAngle = Math.PI / 6.0;
            Assert.True(canvas.TryAcceptLineAuthoringInput(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{4.0 * Math.Cos(referenceAngle):G17},{4.0 * Math.Sin(referenceAngle):G17}"),
                out _));
            CadPoint3D current = canvas.PendingLineCurrentPoint!.Value;
            double trackedAngle = referenceAngle + (Math.PI / 4.0);
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
            Assert.True(canvas.TryAcceptLineAuthoringInput("5", out _));
            CadPoint3D accepted = canvas.PendingLineCurrentPoint!.Value;
            CadPoint3D delta = accepted - current;
            Assert.InRange(Math.Abs(Math.Sqrt(CadPoint3D.Dot(delta, delta)) - 5.0), 0.0, 1e-12);
            Assert.InRange(
                Math.Abs(Math.Atan2(delta.Y, delta.X) - trackedAngle),
                0.0,
                1e-12);
            Assert.True(canvas.CompleteLineAuthoring(false, out _));
            Assert.Equal(2, session.Read(document =>
                document.Entities.OfType<Line>().Count()));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void LoadedObjectSnapOverridesGridForLineFirstPoint()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(new XYZ(-3, 0, 0), new XYZ(3, 0, 0)));
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.Endpoint;
            canvas.IsPlanGridSnapEnabled = true;
            Assert.True(canvas.BeginLineAuthoring());
            Vector2 nearEndpoint = canvas.CurrentViewport.WorldToScreen(
                new CadPoint3D(3, 0, 0)) + new Vector2(3, 2);

            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = nearEndpoint,
            });

            Assert.Equal(
                new CadPoint3D(3, 0, 0),
                canvas.PendingPointTransformObjectSnap!.Value.Point);
            Assert.Null(canvas.PendingPointTransformGridSnap);
            Click(canvas, nearEndpoint);
            Assert.Equal(new CadPoint3D(3, 0, 0), canvas.PendingLineFirstPoint);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.True(canvas.CompleteLineAuthoring(false, out _));
            Assert.Equal(0UL, session.ContentGeneration);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewButtonsAndKeyboardExposeLineUndoCloseAndFinish()
    {
        var session = new CadDocumentSession(new CadDocument());
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(view.LineButton.IsEnabled);
            Assert.False(view.LineUndoButton.IsEnabled);
            PressEnter(view.LineButton);
            Assert.True(view.Canvas.IsLineAuthoring);
            Assert.True(view.LineFinishButton.IsEnabled);
            Assert.False(view.LineCloseButton.IsEnabled);

            view.PointTransformInput.Text = "0,0";
            view.PointTransformInput.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Enter,
            });
            view.PointTransformInput.Text = "4,0";
            view.PointTransformInput.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Enter,
            });
            Assert.True(view.LineUndoButton.IsEnabled);
            PressEnter(view.LineUndoButton);
            Assert.Equal(0, view.Canvas.PendingLineSegmentCount);

            view.PointTransformInput.Text = "4,0";
            view.PointTransformInput.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Enter,
            });
            view.PointTransformInput.Text = "4,3";
            view.PointTransformInput.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Enter,
            });
            Assert.True(view.LineCloseButton.IsEnabled);
            var close = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.C,
            };
            view.OnKeyDown(close);

            Assert.True(close.Handled);
            Assert.False(view.Canvas.IsLineAuthoring);
            Assert.Equal(3, session.Read(document =>
                document.Entities.OfType<Line>().Count()));
            Assert.Equal(1, view.Canvas.UndoCount);
            Assert.True(view.LineButton.IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void RelativePolarProfileToggleDoesNotEditTheDrawing()
    {
        var session = new CadDocumentSession(new CadDocument());
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.PlanPolarRelativeCheckBox.IsChecked = true;

            Assert.Equal(
                CadPlanPolarAngleMeasurement.RelativeToLastSegment,
                view.Canvas.PlanPolarAngleMeasurement);
            Assert.Equal(0UL, session.ContentGeneration);

            view.PlanPolarRelativeCheckBox.IsChecked = false;
            Assert.Equal(
                CadPlanPolarAngleMeasurement.Absolute,
                view.Canvas.PlanPolarAngleMeasurement);
            Assert.Equal(0UL, session.ContentGeneration);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void EscapeFinishesAcceptedSegmentsWithoutClosingTheSequence()
    {
        var session = new CadDocumentSession(new CadDocument());
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            PressEnter(view.LineButton);
            Assert.True(view.Canvas.TryAcceptLineAuthoringInput("0,0", out _));
            Assert.True(view.Canvas.TryAcceptLineAuthoringInput("3,0", out _));

            var escape = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Escape,
            };
            view.OnKeyDown(escape);

            Assert.True(escape.Handled);
            Assert.False(view.Canvas.IsLineAuthoring);
            Line line = Assert.Single(session.Read(document =>
                document.Entities.OfType<Line>().ToArray()));
            AssertPoint(new XYZ(0, 0, 0), line.StartPoint);
            AssertPoint(new XYZ(3, 0, 0), line.EndPoint);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
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

    private static void AssertPoint(XYZ expected, XYZ actual)
    {
        Assert.Equal(expected.X, actual.X, 10);
        Assert.Equal(expected.Y, actual.Y, 10);
        Assert.Equal(expected.Z, actual.Z, 10);
    }
}

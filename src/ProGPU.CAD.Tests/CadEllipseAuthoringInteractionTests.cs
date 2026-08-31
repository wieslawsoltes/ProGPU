using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Types.Units;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadEllipseAuthoringInteractionTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void TypedAxisDistanceCreatesOneTransactionalFullEllipse()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginEllipseAuthoring(
                CadEllipseAuthoringMode.AxisEndpointsDistance,
                CadEllipseArcInputMode.Full));

            Accept(canvas, "-4,0,3", "4,0,3");
            Assert.True(canvas.IsEllipseAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Accept(canvas, "0,2,3");

            Assert.False(canvas.IsEllipseAuthoring);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, canvas.UndoCount);
            Ellipse ellipse = Assert.Single(document.Entities.OfType<Ellipse>());
            Assert.Equal(new XYZ(0, 0, 3), ellipse.Center);
            Assert.Equal(new XYZ(4, 0, 0), ellipse.MajorAxisEndPoint);
            AssertClose(0.5, ellipse.RadiusRatio);
            AssertClose(0.0, ellipse.StartParameter);
            AssertClose(Math.Tau, ellipse.EndParameter);
            Assert.True(canvas.TryUndo());
            Assert.Empty(document.Entities);
            Assert.True(canvas.TryRedo());
            Assert.Same(ellipse, Assert.Single(document.Entities));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void RotationAndParameterScalarsCommitWithoutCursorMotion()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginEllipseAuthoring(
                CadEllipseAuthoringMode.CenterRotation,
                CadEllipseArcInputMode.Parameter));
            Accept(canvas, "0,0,5", "4,0,5");

            Assert.True(canvas.CanAcceptEllipseAuthoringInput("60"));
            Accept(canvas, "60", "0", "90");

            Ellipse ellipse = Assert.Single(document.Entities.OfType<Ellipse>());
            Assert.Equal(new XYZ(0, 0, 5), ellipse.Center);
            Assert.Equal(new XYZ(4, 0, 0), ellipse.MajorAxisEndPoint);
            AssertClose(0.5, ellipse.RadiusRatio);
            AssertClose(0.0, ellipse.StartParameter);
            AssertClose(Math.PI / 2.0, ellipse.EndParameter);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void AngleDirectionsHonorAngleBaseAndClockwiseDirection()
    {
        var document = new CadDocument();
        document.Header.AngleBase = Math.PI / 2.0;
        document.Header.AngularDirection = AngularDirection.ClockWise;
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginEllipseAuthoring(
                CadEllipseAuthoringMode.CenterDistance,
                CadEllipseArcInputMode.Angle));
            Accept(canvas, "0,0,4", "4,0,4", "0,2,4", "0", "90");

            Ellipse ellipse = Assert.Single(document.Entities.OfType<Ellipse>());
            AssertClose(Math.PI / 2.0, ellipse.StartParameter);
            AssertClose(0.0, ellipse.EndParameter);
            AssertClose(Math.PI * 1.5,
                PositiveSweep(
                    ellipse.StartParameter,
                    ellipse.EndParameter));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void OtherAxisDirectDistanceUsesCenterBaseAndPreservesElevation()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginEllipseAuthoring(
                CadEllipseAuthoringMode.CenterDistance,
                CadEllipseArcInputMode.Full));
            Accept(canvas, "10,20,7", "14,20,7");
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = canvas.CurrentViewport.WorldToScreen(
                    new CadPoint3D(10, 23, 7)),
            });

            Assert.True(canvas.CanAcceptEllipseAuthoringInput("2"));
            Accept(canvas, "2");

            Ellipse ellipse = Assert.Single(document.Entities.OfType<Ellipse>());
            Assert.Equal(new XYZ(10, 20, 7), ellipse.Center);
            Assert.Equal(new XYZ(4, 0, 0), ellipse.MajorAxisEndPoint);
            AssertClose(0.5, ellipse.RadiusRatio);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void InvalidRotationAndThicknessFailureKeepPromptRecoverable()
    {
        var document = new CadDocument();
        document.Header.ThicknessDefault = 2.0;
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginEllipseAuthoring(
                CadEllipseAuthoringMode.CenterRotation,
                CadEllipseArcInputMode.Full));
            Accept(canvas, "0,0", "4,0");

            Assert.False(canvas.TryAcceptEllipseAuthoringInput(
                "90",
                out string? rotationError));
            Assert.Contains("edge-on", rotationError,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, canvas.PendingEllipseAcceptedInputCount);
            Assert.False(canvas.TryAcceptEllipseAuthoringInput(
                "60",
                out string? thicknessError));
            Assert.Contains("THICKNESS", thicknessError,
                StringComparison.Ordinal);
            Assert.True(canvas.IsEllipseAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(document.Entities);

            document.Header.ThicknessDefault = 0.0;
            Accept(canvas, "60");
            Assert.Single(document.Entities.OfType<Ellipse>());
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedSelectorsExposeCompleteMatrixAndEscapeCancels()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.Equal(4, view.EllipseModeSelector.Items.Count);
            Assert.Equal(4, view.EllipseArcInputSelector.Items.Count);

            for (int modeIndex = 0;
                modeIndex < view.EllipseModeSelector.Items.Count;
                modeIndex++)
            {
                for (int arcIndex = 0;
                    arcIndex < view.EllipseArcInputSelector.Items.Count;
                    arcIndex++)
                {
                    view.EllipseModeSelector.SelectedIndex = modeIndex;
                    view.EllipseArcInputSelector.SelectedIndex = arcIndex;
                    CadEllipseAuthoringMode mode = Assert.IsType<
                        CadEllipseAuthoringMode>(Assert.IsType<ComboBoxItem>(
                            view.EllipseModeSelector.SelectedItem).Tag);
                    CadEllipseArcInputMode arcInputMode = Assert.IsType<
                        CadEllipseArcInputMode>(Assert.IsType<ComboBoxItem>(
                            view.EllipseArcInputSelector.SelectedItem).Tag);
                    PressEnter(view.EllipseButton);
                    Assert.Equal(mode, view.Canvas.PendingEllipseAuthoringMode);
                    Assert.Equal(arcInputMode,
                        view.Canvas.PendingEllipseArcInputMode);
                    var escape = new KeyRoutedEventArgs
                    {
                        Key = Silk.NET.Input.Key.Escape,
                    };
                    view.OnKeyDown(escape);
                    Assert.True(escape.Handled);
                    Assert.False(view.Canvas.IsEllipseAuthoring);
                }
            }

            Assert.Empty(document.Entities);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void FullPreviewRecordsOneTransformedAnalyticEllipse()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginEllipseAuthoring(
                CadEllipseAuthoringMode.CenterDistance,
                CadEllipseArcInputMode.Full));
            Accept(canvas, "0,0", "4,0");
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = canvas.CurrentViewport.WorldToScreen(
                    new CadPoint3D(0, 2, 0)),
            });
            var drawing = new DrawingContext();

            canvas.OnRender(drawing);

            RenderCommand command = Assert.Single(
                drawing.Commands,
                item => item.Type == RenderCommandType.DrawEllipse);
            Assert.NotEqual(Matrix4x4.Identity, command.Transform);
            Assert.True(canvas.IsEllipseAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(document.Entities);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void EllipticalArcPreviewRecordsOneAnalyticArcWithoutMutation()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginEllipseAuthoring(
                CadEllipseAuthoringMode.CenterDistance,
                CadEllipseArcInputMode.Angle));
            Accept(canvas, "0,0", "4,0", "0,2", "0");
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = canvas.CurrentViewport.WorldToScreen(
                    new CadPoint3D(0, 2, 0)),
            });
            var drawing = new DrawingContext();

            canvas.OnRender(drawing);

            RenderCommand command = Assert.Single(
                drawing.Commands,
                item => item.Type == RenderCommandType.DrawPath);
            PathFigure figure = Assert.Single(command.Path!.Figures);
            ArcSegment segment = Assert.IsType<ArcSegment>(
                Assert.Single(figure.Segments));
            Assert.Equal(SweepDirection.Counterclockwise,
                segment.SweepDirection);
            Assert.False(segment.IsLargeArc);
            Assert.NotEqual(Matrix4x4.Identity, command.Transform);
            Assert.True(canvas.IsEllipseAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(document.Entities);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    private static void Accept(CadSampleCanvas canvas, params string[] inputs)
    {
        foreach (string input in inputs)
        {
            Assert.True(canvas.TryAcceptEllipseAuthoringInput(
                input,
                out string? error),
                error);
        }
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);

    private static double PositiveSweep(double start, double end)
    {
        double sweep = (end - start) % Math.Tau;
        return sweep < 0.0 ? sweep + Math.Tau : sweep;
    }

    private static void PressEnter(Button button) =>
        button.OnKeyDown(new KeyRoutedEventArgs
        {
            Key = Silk.NET.Input.Key.Enter,
        });
}

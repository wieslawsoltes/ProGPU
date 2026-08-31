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
public sealed class CadArcAuthoringInteractionTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void TypedThreePointCreatesOneTransactionalArc()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginArcAuthoring(
                CadArcAuthoringMode.ThreePoint));

            Assert.True(canvas.TryAcceptArcAuthoringInput("1,0,3", out _));
            Assert.True(canvas.TryAcceptArcAuthoringInput("0,1,3", out _));
            Assert.True(canvas.IsArcAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.True(canvas.TryAcceptArcAuthoringInput("-1,0,3", out _));

            Assert.False(canvas.IsArcAuthoring);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, canvas.UndoCount);
            Arc arc = Assert.Single(document.Entities.OfType<Arc>());
            Assert.Equal(new XYZ(0, 0, 3), arc.Center);
            AssertClose(1.0, arc.Radius);
            AssertClose(0.0, arc.StartAngle);
            AssertClose(Math.PI, arc.EndAngle);
            Assert.True(canvas.TryUndo());
            Assert.Empty(document.Entities);
            Assert.True(canvas.TryRedo());
            Assert.Same(arc, Assert.Single(document.Entities));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    public static TheoryData<
        CadArcAuthoringMode,
        string,
        string,
        string,
        double,
        double,
        double,
        double> ScalarModes => new()
    {
        { CadArcAuthoringMode.CenterStartAngle, "0,0,5", "2,0,5", "90", 0, 0, 2, Math.PI / 2 },
        { CadArcAuthoringMode.StartCenterAngle, "2,0,5", "0,0,5", "90", 0, 0, 2, Math.PI / 2 },
        { CadArcAuthoringMode.CenterStartChord, "0,0,5", "2,0,5", "2", 0, 0, 2, Math.PI / 3 },
        { CadArcAuthoringMode.StartCenterChord, "2,0,5", "0,0,5", "-2", 0, 0, 2, Math.PI * 5 / 3 },
        { CadArcAuthoringMode.StartEndAngle, "1,0,5", "0,1,5", "90", 0, 0, 1, Math.PI / 2 },
        { CadArcAuthoringMode.StartEndDirection, "0,0,5", "1,1,5", "0", 0, 1, 1, Math.PI / 2 },
        { CadArcAuthoringMode.StartEndRadius, "0,0,5", "1,0,5", "-1", 0.5, -0.8660254037844386, 1, Math.PI * 5 / 3 },
    };

    [Theory]
    [MemberData(nameof(ScalarModes))]
    public void TypedScalarModesCommitWithoutCursorMotion(
        CadArcAuthoringMode mode,
        string first,
        string second,
        string scalar,
        double centerX,
        double centerY,
        double radius,
        double sweep)
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginArcAuthoring(mode));
            Assert.True(canvas.TryAcceptArcAuthoringInput(first, out _));
            Assert.True(canvas.TryAcceptArcAuthoringInput(second, out _));

            Assert.True(canvas.CanAcceptArcAuthoringInput(scalar));
            Assert.True(canvas.TryAcceptArcAuthoringInput(scalar, out _));

            Arc arc = Assert.Single(document.Entities.OfType<Arc>());
            AssertClose(centerX, arc.Center.X);
            AssertClose(centerY, arc.Center.Y);
            AssertClose(5.0, arc.Center.Z);
            AssertClose(radius, arc.Radius);
            AssertClose(sweep, PositiveSweep(arc.StartAngle, arc.EndAngle));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void DirectionModeAcceptsPointOrNumericAngle()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginArcAuthoring(
                CadArcAuthoringMode.StartEndDirection));
            Accept(canvas, "0,0,2", "1,1,2", "1,0,2");
            Arc pointArc = Assert.Single(document.Entities.OfType<Arc>());
            AssertClose(0.0, pointArc.Center.X);
            AssertClose(1.0, pointArc.Center.Y);

            Assert.True(canvas.BeginArcAuthoring(
                CadArcAuthoringMode.StartEndDirection));
            Accept(canvas, "3,0,2", "4,1,2", "0");
            Arc angleArc = document.Entities.OfType<Arc>().Last();
            AssertClose(3.0, angleArc.Center.X);
            AssertClose(1.0, angleArc.Center.Y);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void AngularDirectionAppliesToIncludedAngleScalar()
    {
        var document = new CadDocument();
        document.Header.AngularDirection = AngularDirection.ClockWise;
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginArcAuthoring(
                CadArcAuthoringMode.CenterStartAngle));
            Accept(canvas, "0,0,4", "2,0,4", "90");

            Arc arc = Assert.Single(document.Entities.OfType<Arc>());
            AssertClose(Math.PI * 1.5, arc.StartAngle);
            AssertClose(0.0, arc.EndAngle);
            AssertClose(Math.PI / 2.0,
                PositiveSweep(arc.StartAngle, arc.EndAngle));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void CollinearThreePointFailureRetainsPromptForCorrectedFinalPoint()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginArcAuthoring(
                CadArcAuthoringMode.ThreePoint));
            Assert.True(canvas.TryAcceptArcAuthoringInput("0,0,4", out _));
            Assert.True(canvas.TryAcceptArcAuthoringInput("10,0,4", out _));

            Assert.False(canvas.TryAcceptArcAuthoringInput(
                "20,0,4",
                out string? error));

            Assert.Contains("non-collinear", error, StringComparison.OrdinalIgnoreCase);
            Assert.True(canvas.IsArcAuthoring);
            Assert.Equal(2, canvas.PendingArcPointCount);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.True(canvas.TryAcceptArcAuthoringInput("5,5,4", out _));
            Assert.Single(document.Entities.OfType<Arc>());
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void NonzeroThicknessFailsWithoutPublishingAndKeepsPromptRecoverable()
    {
        var document = new CadDocument();
        document.Header.ThicknessDefault = 2.0;
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginArcAuthoring(
                CadArcAuthoringMode.CenterStartAngle));
            Accept(canvas, "0,0", "4,0");

            Assert.False(canvas.TryAcceptArcAuthoringInput(
                "90",
                out string? error));

            Assert.Contains("THICKNESS", error, StringComparison.Ordinal);
            Assert.True(canvas.IsArcAuthoring);
            Assert.Equal(2, canvas.PendingArcPointCount);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(document.Entities);
            document.Header.ThicknessDefault = 0.0;
            Assert.True(canvas.TryAcceptArcAuthoringInput("90", out _));
            Assert.Single(document.Entities.OfType<Arc>());
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedSelectorExposesEveryModeAndEscapeCancels()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.Equal(10, view.ArcModeSelector.Items.Count);

            for (int index = 0; index < view.ArcModeSelector.Items.Count; index++)
            {
                view.ArcModeSelector.SelectedIndex = index;
                CadArcAuthoringMode mode = Assert.IsType<CadArcAuthoringMode>(
                    Assert.IsType<ComboBoxItem>(
                        view.ArcModeSelector.SelectedItem).Tag);
                PressEnter(view.ArcButton);
                Assert.Equal(mode, view.Canvas.PendingArcAuthoringMode);
                var escape = new KeyRoutedEventArgs
                {
                    Key = Silk.NET.Input.Key.Escape,
                };
                view.OnKeyDown(escape);
                Assert.True(escape.Handled);
                Assert.False(view.Canvas.IsArcAuthoring);
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
    public void PointFinalDirectDistancePreservesFirstPointElevation()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginArcAuthoring(
                CadArcAuthoringMode.CenterStartEnd));
            Assert.True(canvas.TryAcceptArcAuthoringInput("0,0,7", out _));
            Assert.True(canvas.TryAcceptArcAuthoringInput("4,0,7", out _));
            Vector2 pointer = canvas.CurrentViewport.WorldToScreen(
                new CadPoint3D(0, 3, 7));
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = pointer,
            });

            Assert.True(canvas.TryAcceptArcAuthoringInput("5", out _));

            Arc arc = Assert.Single(document.Entities.OfType<Arc>());
            Assert.Equal(new XYZ(0, 0, 7), arc.Center);
            AssertClose(4.0, arc.Radius);
            AssertClose(Math.PI / 2.0,
                PositiveSweep(arc.StartAngle, arc.EndAngle));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void PointFinalPreviewRecordsOneAnalyticArcWithoutModelMutation()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginArcAuthoring(
                CadArcAuthoringMode.ThreePoint));
            Assert.True(canvas.TryAcceptArcAuthoringInput("1,0", out _));
            Assert.True(canvas.TryAcceptArcAuthoringInput("0,1", out _));
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = canvas.CurrentViewport.WorldToScreen(
                    new CadPoint3D(-1, 0, 0)),
            });
            var drawing = new DrawingContext();

            canvas.OnRender(drawing);

            RenderCommand command = Assert.Single(
                drawing.Commands,
                item => item.Type == RenderCommandType.DrawPath);
            PathFigure figure = Assert.Single(command.Path!.Figures);
            ArcSegment segment = Assert.IsType<ArcSegment>(
                Assert.Single(figure.Segments));
            Assert.Equal(SweepDirection.Clockwise, segment.SweepDirection);
            Assert.False(segment.IsLargeArc);
            Assert.True(canvas.IsArcAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(document.Entities);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SignedScalarParserIsBoundedInvariantAndFinite()
    {
        Assert.True(CadArcScalarInput.TryParse("-12.5", out var scalar));
        Assert.Equal(-12.5, scalar.Value);
        Assert.False(CadArcScalarInput.TryParse("12,5", out _));
        Assert.False(CadArcScalarInput.TryParse("NaN", out _));
        Assert.False(CadArcScalarInput.TryParse(
            new string('1', CadArcScalarInput.MaximumCodeUnits + 1),
            out _));
    }

    private static void Accept(CadSampleCanvas canvas, params string[] inputs)
    {
        foreach (string input in inputs)
        {
            Assert.True(canvas.TryAcceptArcAuthoringInput(input, out _));
        }
    }

    private static double PositiveSweep(double start, double end)
    {
        double sweep = (end - start) % Math.Tau;
        return sweep < 0.0 ? sweep + Math.Tau : sweep;
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);

    private static void PressEnter(Microsoft.UI.Xaml.Controls.Button button) =>
        button.OnKeyDown(new KeyRoutedEventArgs
        {
            Key = Silk.NET.Input.Key.Enter,
        });
}

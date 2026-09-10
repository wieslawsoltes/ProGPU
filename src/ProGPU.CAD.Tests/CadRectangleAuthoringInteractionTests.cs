using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Types.Units;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadRectangleAuthoringInteractionTests
{
    private const double Tolerance = 1e-8;

    [Theory]
    [InlineData(CadRectangleConstructionMode.DiagonalCorners)]
    [InlineData(CadRectangleConstructionMode.Dimensions)]
    [InlineData(CadRectangleConstructionMode.Area)]
    public void TypedConstructionModesCreateOneTransactionalPolyline(
        CadRectangleConstructionMode mode)
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadRectangleConstruction construction = mode switch
            {
                CadRectangleConstructionMode.DiagonalCorners =>
                    CadRectangleConstruction.DiagonalCorners,
                CadRectangleConstructionMode.Dimensions =>
                    CadRectangleConstruction.Dimensions(10, 6),
                CadRectangleConstructionMode.Area =>
                    CadRectangleConstruction.FromArea(
                        60,
                        CadRectangleKnownDimension.Length,
                        10),
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            Assert.True(canvas.BeginRectangleAuthoring(
                construction,
                CadRectangleCornerTreatment.Sharp,
                rotationDegrees: 0));

            Accept(canvas, "0,0,3");
            Assert.True(canvas.IsRectangleAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Accept(canvas, "10,6,3");

            Assert.False(canvas.IsRectangleAuthoring);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, canvas.UndoCount);
            LwPolyline polyline = Assert.Single(document.Entities.OfType<LwPolyline>());
            Assert.True(polyline.IsClosed);
            Assert.Equal(4, polyline.Vertices.Count);
            Assert.Equal(3.0, polyline.Elevation);
            AssertClose(10.0, polyline.Vertices[1].Location.X);
            AssertClose(6.0, polyline.Vertices[2].Location.Y);
            Assert.True(canvas.TryUndo());
            Assert.Empty(document.Entities);
            Assert.True(canvas.TryRedo());
            Assert.Same(polyline, Assert.Single(document.Entities));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void RotationHonorsAngleBaseAndClockwiseDirection()
    {
        var document = new CadDocument();
        document.Header.AngleBase = Math.PI / 2.0;
        document.Header.AngularDirection = AngularDirection.ClockWise;
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(new CadDocumentSession(document));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginRectangleAuthoring(
                CadRectangleConstruction.Dimensions(8, 4),
                CadRectangleCornerTreatment.Sharp,
                rotationDegrees: 30));
            Accept(canvas, "0,0", "2,2");

            LwPolyline polyline = Assert.Single(document.Entities.OfType<LwPolyline>());
            double edgeX = polyline.Vertices[1].Location.X -
                polyline.Vertices[0].Location.X;
            double edgeY = polyline.Vertices[1].Location.Y -
                polyline.Vertices[0].Location.Y;
            AssertClose(Math.PI / 3.0, Math.Atan2(edgeY, edgeX));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void FilletPreviewRetainsFourExactAnalyticArcsWithoutMutation()
    {
        var session = new CadDocumentSession(new CadDocument());
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginRectangleAuthoring(
                CadRectangleConstruction.DiagonalCorners,
                CadRectangleCornerTreatment.Fillet(2),
                rotationDegrees: 15));
            Accept(canvas, "0,0");
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = canvas.CurrentViewport.WorldToScreen(
                    new CadPoint3D(12, 9, 0)),
            });
            var drawing = new DrawingContext();

            canvas.OnRender(drawing);

            RenderCommand preview = Assert.Single(
                drawing.Commands,
                command => command.Type == RenderCommandType.DrawPath &&
                    command.Path?.Figures.Count == 1 &&
                    command.Path.Figures[0].Segments.Count == 8 &&
                    command.Path.Figures[0].Segments
                        .OfType<ArcSegment>().Count() == 4);
            Assert.NotNull(preview.Path);
            Assert.True(canvas.IsRectangleAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(session.Read(document => document.Entities.ToArray()));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void DirectDistanceUsesPointerDirectionAndPreservesElevation()
    {
        var document = new CadDocument();
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(new CadDocumentSession(document));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginRectangleAuthoring(
                CadRectangleConstruction.DiagonalCorners,
                CadRectangleCornerTreatment.Sharp,
                rotationDegrees: 0));
            Accept(canvas, "3,4,9");
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = canvas.CurrentViewport.WorldToScreen(
                    new CadPoint3D(6, 8, 9)),
            });

            Assert.True(canvas.CanAcceptRectangleAuthoringInput("10"));
            Accept(canvas, "10");

            LwPolyline polyline = Assert.Single(document.Entities.OfType<LwPolyline>());
            Assert.Equal(9.0, polyline.Elevation);
            AssertClose(9.0, polyline.Vertices[1].Location.X);
            AssertClose(12.0, polyline.Vertices[2].Location.Y);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedControlsValidateSettingsAndEscapeCancels()
    {
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(new CadDocumentSession(new CadDocument()));
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.Equal(3, view.RectangleConstructionSelector.Items.Count);
            Assert.Equal(3, view.RectangleCornerSelector.Items.Count);
            Assert.Equal(2, view.RectangleAreaDimensionSelector.Items.Count);

            view.RectangleConstructionSelector.SelectedIndex = 1;
            view.RectangleValuesInput.Text = "invalid";
            Assert.False(view.RectangleButton.IsEnabled);
            view.RectangleCornerSelector.SelectedIndex = 2;
            view.RectangleValuesInput.Text = "4,4";
            view.RectangleCornerValuesInput.Text = "3";
            Assert.False(view.RectangleButton.IsEnabled);
            view.RectangleValuesInput.Text = "12,8";
            view.RectangleCornerValuesInput.Text = "2";
            view.RectangleRotationInput.Text = "30";
            Assert.True(view.RectangleButton.IsEnabled);

            PressEnter(view.RectangleButton);
            Assert.True(view.Canvas.IsRectangleAuthoring);
            Assert.Equal(
                CadRectangleConstructionMode.Dimensions,
                view.Canvas.PendingRectangleConstruction?.Mode);
            Assert.Equal(
                CadRectangleCornerMode.Fillet,
                view.Canvas.RectangleCornerTreatment.Mode);
            AssertClose(Math.PI / 6.0, view.Canvas.RectangleRotationRadians);

            var escape = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Escape,
            };
            view.OnKeyDown(escape);
            Assert.True(escape.Handled);
            Assert.False(view.Canvas.IsRectangleAuthoring);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void AreaControlsSelectKnownWidthAndIncludeChamferArea()
    {
        var document = new CadDocument();
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(new CadDocumentSession(document));
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            view.RectangleConstructionSelector.SelectedIndex = 2;
            view.RectangleAreaDimensionSelector.SelectedIndex = 1;
            view.RectangleValuesInput.Text = "96,8";
            view.RectangleCornerSelector.SelectedIndex = 1;
            view.RectangleCornerValuesInput.Text = "2,1";
            Assert.True(view.RectangleButton.IsEnabled);
            PressEnter(view.RectangleButton);

            Accept(view.Canvas, "0,0", "1,1");

            LwPolyline polyline = Assert.Single(document.Entities.OfType<LwPolyline>());
            Assert.Equal(8, polyline.Vertices.Count);
            double outerLength = polyline.Vertices[3].Location.X -
                polyline.Vertices[6].Location.X;
            AssertClose(12.5, outerLength);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void FillModeOffFilletPublicationCompletesWithAnalyticWideOutline()
    {
        var document = new CadDocument();
        document.Header.PolylineWidthDefault = 2.0;
        document.Header.FillMode = false;
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginRectangleAuthoring(
                CadRectangleConstruction.Dimensions(10, 6),
                CadRectangleCornerTreatment.Fillet(1),
                rotationDegrees: 0));
            Accept(canvas, "0,0");

            Assert.True(canvas.TryAcceptRectangleAuthoringInput(
                "1,1",
                out string? error), error);
            Assert.False(canvas.IsRectangleAuthoring);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(
                2.0,
                Assert.Single(document.Entities.OfType<LwPolyline>()).ConstantWidth);
            CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
            Assert.False(Assert.Single(snapshot.Polylines.ToArray()).IsFillEnabled);
            using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
            RenderCommand outline = Assert.Single(
                scene.DrawingContext.Commands.ToArray());
            Assert.Contains(
                outline.Path!.Figures.SelectMany(figure => figure.Segments),
                segment => segment is ArcSegment);
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
            Assert.True(
                canvas.TryAcceptRectangleAuthoringInput(input, out string? error),
                error);
        }
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);

    private static void PressEnter(Button button) =>
        button.OnKeyDown(new KeyRoutedEventArgs
        {
            Key = Silk.NET.Input.Key.Enter,
        });
}

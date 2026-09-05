using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadPolygonAuthoringInteractionTests
{
    private const double Tolerance = 1e-8;

    [Theory]
    [InlineData(CadPolygonAuthoringMode.Inscribed, "0,0,3", "-1,-1,3")]
    [InlineData(CadPolygonAuthoringMode.Circumscribed, "0,0,3", "0,-1,3")]
    [InlineData(CadPolygonAuthoringMode.Edge, "-1,-1,3", "1,-1,3")]
    public void TypedModesCreateOneTransactionalClosedPolyline(
        CadPolygonAuthoringMode mode,
        string first,
        string final)
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginPolygonAuthoring(4, mode));

            Accept(canvas, first);
            Assert.True(canvas.IsPolygonAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Accept(canvas, final);

            Assert.False(canvas.IsPolygonAuthoring);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, canvas.UndoCount);
            LwPolyline polyline = Assert.Single(document.Entities.OfType<LwPolyline>());
            Assert.True(polyline.IsClosed);
            Assert.Equal(4, polyline.Vertices.Count);
            Assert.Equal(3.0, polyline.Elevation);
            Assert.All(polyline.Vertices, vertex => Assert.Equal(0.0, vertex.Bulge));
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
    public void NumericRadiusUsesCurrentSnapRotationWithoutCursorMotion()
    {
        var document = new CadDocument();
        document.VPorts[VPort.DefaultName].SnapRotation = Math.PI / 6.0;
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginPolygonAuthoring(
                4,
                CadPolygonAuthoringMode.Circumscribed));
            Accept(canvas, "10,20,7");

            Assert.True(canvas.CanAcceptPolygonAuthoringInput("2"));
            Accept(canvas, "2");

            LwPolyline polyline = Assert.Single(document.Entities.OfType<LwPolyline>());
            double edgeX = polyline.Vertices[1].Location.X -
                polyline.Vertices[0].Location.X;
            double edgeY = polyline.Vertices[1].Location.Y -
                polyline.Vertices[0].Location.Y;
            AssertClose(Math.PI / 6.0, Math.Atan2(edgeY, edgeX));
            Assert.Equal(7.0, polyline.Elevation);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)1)]
    [InlineData((short)2)]
    public void NumericRadiusRecoversRectangularSnapRotationFromEveryIsoplane(
        short snapIsoPair)
    {
        var document = new CadDocument();
        VPort active = document.VPorts[VPort.DefaultName];
        active.IsometricSnap = true;
        active.SnapIsoPair = snapIsoPair;
        active.SnapSpacing = new CSMath.XY(1.0, 1.0);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(canvas.BeginPolygonAuthoring(
                4,
                CadPolygonAuthoringMode.Circumscribed));
            Accept(canvas, "0,0", "3");

            LwPolyline polyline = Assert.Single(document.Entities.OfType<LwPolyline>());
            double edgeX = polyline.Vertices[1].Location.X -
                polyline.Vertices[0].Location.X;
            double edgeY = polyline.Vertices[1].Location.Y -
                polyline.Vertices[0].Location.Y;
            AssertClose(0.0, Math.Atan2(edgeY, edgeX));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void EdgeDirectDistanceUsesPointerDirectionAndPreservesElevation()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginPolygonAuthoring(6, CadPolygonAuthoringMode.Edge));
            Accept(canvas, "3,4,9");
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = canvas.CurrentViewport.WorldToScreen(
                    new CadPoint3D(8, 4, 9)),
            });

            Assert.True(canvas.CanAcceptPolygonAuthoringInput("5"));
            Accept(canvas, "5");

            LwPolyline polyline = Assert.Single(document.Entities.OfType<LwPolyline>());
            AssertClose(3.0, polyline.Vertices[0].Location.X);
            AssertClose(4.0, polyline.Vertices[0].Location.Y);
            AssertClose(8.0, polyline.Vertices[1].Location.X);
            AssertClose(4.0, polyline.Vertices[1].Location.Y);
            Assert.Equal(9.0, polyline.Elevation);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void PointerPreviewReplaysOneRetainedTransformedPolygon()
    {
        var session = new CadDocumentSession(new CadDocument());
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginPolygonAuthoring(1024, CadPolygonAuthoringMode.Inscribed));
            Accept(canvas, "0,0");
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = canvas.CurrentViewport.WorldToScreen(new CadPoint3D(5, 0, 0)),
            });
            var drawing = new DrawingContext();

            canvas.OnRender(drawing);

            RenderCommand preview = Assert.Single(
                drawing.Commands,
                command => command.Type == RenderCommandType.DrawPicture &&
                    command.Transform != Matrix4x4.Identity &&
                    command.Picture is { CommandCount: 1 } picture &&
                    picture.GetCommand(0).Type == RenderCommandType.DrawPath &&
                    picture.GetCommand(0).Path?.Figures[0].Segments.Count == 1023);
            Assert.NotNull(preview.Picture);
            Assert.True(canvas.IsPolygonAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedSelectorValidatesSideBoundsAndEscapeCancelsEveryMode()
    {
        var session = new CadDocumentSession(new CadDocument());
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.Equal(3, view.PolygonModeSelector.Items.Count);
            view.PolygonSideCountInput.Text = "2";
            Assert.False(view.PolygonButton.IsEnabled);
            view.PolygonSideCountInput.Text = "1024";
            Assert.True(view.PolygonButton.IsEnabled);

            for (int index = 0; index < view.PolygonModeSelector.Items.Count; index++)
            {
                view.PolygonModeSelector.SelectedIndex = index;
                CadPolygonAuthoringMode mode = Assert.IsType<CadPolygonAuthoringMode>(
                    Assert.IsType<ComboBoxItem>(view.PolygonModeSelector.SelectedItem).Tag);
                PressEnter(view.PolygonButton);
                Assert.Equal(mode, view.Canvas.PendingPolygonAuthoringMode);
                Assert.Equal(1024, view.Canvas.PendingPolygonSideCount);
                var escape = new KeyRoutedEventArgs
                {
                    Key = Silk.NET.Input.Key.Escape,
                };
                view.OnKeyDown(escape);
                Assert.True(escape.Handled);
                Assert.False(view.Canvas.IsPolygonAuthoring);
            }
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void FillModeOffPublicationCompletesWithRetainedWideOutline()
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
            Assert.True(canvas.BeginPolygonAuthoring(5, CadPolygonAuthoringMode.Inscribed));
            Accept(canvas, "0,0");

            Assert.True(canvas.TryAcceptPolygonAuthoringInput("5", out string? error), error);
            Assert.False(canvas.IsPolygonAuthoring);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(
                2.0,
                Assert.Single(document.Entities.OfType<LwPolyline>()).ConstantWidth);
            CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
            Assert.False(Assert.Single(snapshot.Polylines.ToArray()).IsFillEnabled);
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
            Assert.True(canvas.TryAcceptPolygonAuthoringInput(input, out string? error), error);
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

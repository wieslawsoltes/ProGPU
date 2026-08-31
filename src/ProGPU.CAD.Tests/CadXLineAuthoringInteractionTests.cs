using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadXLineAuthoringInteractionTests
{
    [Fact]
    public void TypedThroughPointsKeepFixedPointAndPublishOneAtomicEdit()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginXLineAuthoring());

            Assert.True(canvas.TryAcceptXLineAuthoringInput("1,2,3", out _));
            Assert.True(canvas.TryAcceptXLineAuthoringInput("@4,0,0", out _));
            Assert.True(canvas.TryAcceptXLineAuthoringInput("@0,5,0", out _));

            Assert.Equal(new CadPoint3D(1, 2, 3), canvas.PendingXLineFirstPoint);
            Assert.Equal(2, canvas.PendingXLineCount);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(document.Entities);
            Assert.True(canvas.UndoXLineAuthoringLine());
            Assert.Equal(1, canvas.PendingXLineCount);
            Assert.True(canvas.TryAcceptXLineAuthoringInput("@-3,0,0", out _));
            Assert.True(canvas.CompleteXLineAuthoring(out string? error), error);

            Assert.False(canvas.IsXLineAuthoring);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, canvas.UndoCount);
            XLine[] lines = document.Entities.OfType<XLine>().ToArray();
            Assert.Equal(2, lines.Length);
            Assert.All(lines, line => Assert.Equal(
                new CSMath.XYZ(1, 2, 3),
                line.FirstPoint));
            AssertDirection(new CadPoint3D(1, 0, 0), lines[0]);
            AssertDirection(new CadPoint3D(-1, 0, 0), lines[1]);

            Assert.True(canvas.TryUndo());
            Assert.Empty(document.Entities);
            Assert.True(canvas.TryRedo());
            Assert.Equal(lines, document.Entities.OfType<XLine>().ToArray());
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void AcceptedPreviewIsRetainedTwoSidedAndRefreshesForViewportPan()
    {
        var session = new CadDocumentSession(new CadDocument());
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginXLineAuthoring());
            Assert.True(canvas.TryAcceptXLineAuthoringInput("0,0", out _));
            Assert.True(canvas.TryAcceptXLineAuthoringInput("1,0", out _));
            Assert.True(canvas.TryAcceptXLineAuthoringInput("0,1", out _));

            var firstDrawing = new DrawingContext();
            canvas.OnRender(firstDrawing);
            GpuPicture firstPreview = GetXLinePreview(firstDrawing, 2);
            RenderCommand path = Assert.Single(firstPreview.Commands);
            Assert.Equal(RenderCommandType.DrawPath, path.Type);
            Assert.Equal(2, path.Path!.Figures.Count);
            foreach (PathFigure figure in path.Path.Figures)
            {
                LineSegment segment = Assert.IsType<LineSegment>(
                    Assert.Single(figure.Segments));
                Assert.True(Vector2.Distance(figure.StartPoint, segment.Point) > 500);
            }

            canvas.OnPointerPressed(new PointerRoutedEventArgs
            {
                Position = new Vector2(400, 300),
                IsMiddleButtonPressed = true,
            });
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = new Vector2(450, 300),
            });
            canvas.OnPointerReleased(new PointerRoutedEventArgs
            {
                Position = new Vector2(450, 300),
            });
            var pannedDrawing = new DrawingContext();
            canvas.OnRender(pannedDrawing);
            GpuPicture pannedPreview = GetXLinePreview(pannedDrawing, 2);

            Assert.NotSame(firstPreview, pannedPreview);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(session.Read(document => document.Entities.ToArray()));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void ObjectSnapAndDirectDistanceResolveFromTheCommonPoint()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(
            new CSMath.XYZ(4, 3, 0),
            new CSMath.XYZ(9, 3, 0)));
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.Endpoint;
            Assert.True(canvas.BeginXLineAuthoring());
            Assert.True(canvas.TryAcceptXLineAuthoringInput("1,3", out _));
            Vector2 endpoint = canvas.CurrentViewport.WorldToScreen(
                new CadPoint3D(4, 3, 0));
            Vector2 nearEndpoint = endpoint + new Vector2(2, -2);
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = nearEndpoint,
            });
            Assert.Equal(
                new CadPoint3D(4, 3, 0),
                canvas.PendingPointTransformObjectSnap!.Value.Point);
            Click(canvas, nearEndpoint);

            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            CadPoint3D pointer = new(1, 8, 0);
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = canvas.CurrentViewport.WorldToScreen(pointer),
            });
            Assert.True(canvas.TryAcceptXLineAuthoringInput("5", out _));
            Assert.True(canvas.CompleteXLineAuthoring(out _));

            XLine[] lines = document.Entities.OfType<XLine>().ToArray();
            Assert.Equal(2, lines.Length);
            AssertDirection(new CadPoint3D(1, 0, 0), lines[0]);
            AssertDirection(new CadPoint3D(0, 1, 0), lines[1]);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedButtonsAndKeyboardExposeUndoEnterAndEscape()
    {
        var session = new CadDocumentSession(new CadDocument());
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(view.XLineButton.IsEnabled);
            PressEnter(view.XLineButton);
            Assert.True(view.Canvas.IsXLineAuthoring);
            Assert.True(view.XLineFinishButton.IsEnabled);
            Assert.False(view.XLineUndoButton.IsEnabled);

            Enter(view, "0,0");
            Enter(view, "4,0");
            Assert.True(view.XLineUndoButton.IsEnabled);
            PressEnter(view.XLineUndoButton);
            Assert.Equal(0, view.Canvas.PendingXLineCount);
            Enter(view, "0,4");

            var escape = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Escape,
            };
            view.OnKeyDown(escape);

            Assert.True(escape.Handled);
            Assert.False(view.Canvas.IsXLineAuthoring);
            XLine line = Assert.Single(session.Read(document =>
                document.Entities.OfType<XLine>().ToArray()));
            AssertDirection(new CadPoint3D(0, 1, 0), line);
            Assert.Equal(1, view.Canvas.UndoCount);
            Assert.True(view.XLineButton.IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void EmptyEnterEndsCommandWithoutChangingDocumentGeneration()
    {
        var session = new CadDocumentSession(new CadDocument());
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            PressEnter(view.XLineButton);

            view.PointTransformInput.Text = string.Empty;
            view.PointTransformInput.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Enter,
            });

            Assert.False(view.Canvas.IsXLineAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(session.Read(document => document.Entities.ToArray()));
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    private static GpuPicture GetXLinePreview(
        DrawingContext drawing,
        int expectedFigureCount) =>
        Assert.Single(
            drawing.Commands,
            command => command.Type == RenderCommandType.DrawPicture &&
                command.Picture is { CommandCount: 1 } picture &&
                picture.GetCommand(0).Type == RenderCommandType.DrawPath &&
                picture.GetCommand(0).Path?.Figures.Count == expectedFigureCount)
            .Picture!;

    private static void Enter(CadSampleView view, string text)
    {
        view.PointTransformInput.Text = text;
        view.PointTransformInput.OnKeyDown(new KeyRoutedEventArgs
        {
            Key = Silk.NET.Input.Key.Enter,
        });
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

    private static void AssertDirection(CadPoint3D expected, XLine line)
    {
        Assert.Equal(expected.X, line.Direction.X, 12);
        Assert.Equal(expected.Y, line.Direction.Y, 12);
        Assert.Equal(expected.Z, line.Direction.Z, 12);
    }
}

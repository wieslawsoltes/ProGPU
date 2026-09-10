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
public sealed class CadRayAuthoringInteractionTests
{
    [Fact]
    public void TypedThroughPointsKeepFixedStartAndPublishOneAtomicEdit()
    {
        var document = new CadDocument();
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginRayAuthoring());

            Assert.True(canvas.TryAcceptRayAuthoringInput("1,2,3", out _));
            Assert.True(canvas.TryAcceptRayAuthoringInput("@4,0,0", out _));
            Assert.True(canvas.TryAcceptRayAuthoringInput("@0,5,0", out _));

            Assert.Equal(new CadPoint3D(1, 2, 3), canvas.PendingRayStartPoint);
            Assert.Equal(2, canvas.PendingRayCount);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(document.Entities);
            Assert.True(canvas.UndoRayAuthoringRay());
            Assert.Equal(1, canvas.PendingRayCount);
            Assert.True(canvas.TryAcceptRayAuthoringInput("@-3,0,0", out _));
            Assert.True(canvas.CompleteRayAuthoring(out string? error), error);

            Assert.False(canvas.IsRayAuthoring);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, canvas.UndoCount);
            Ray[] rays = document.Entities.OfType<Ray>().ToArray();
            Assert.Equal(2, rays.Length);
            Assert.All(rays, ray => Assert.Equal(
                new CSMath.XYZ(1, 2, 3),
                ray.StartPoint));
            AssertDirection(new CadPoint3D(1, 0, 0), rays[0]);
            AssertDirection(new CadPoint3D(-1, 0, 0), rays[1]);

            Assert.True(canvas.TryUndo());
            Assert.Empty(document.Entities);
            Assert.True(canvas.TryRedo());
            Assert.Equal(rays, document.Entities.OfType<Ray>().ToArray());
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void AcceptedPreviewIsRetainedClippedAndRefreshesForViewportPan()
    {
        var session = new CadDocumentSession(new CadDocument());
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            canvas.ObjectSnapModes = CadObjectSnapModes.None;
            Assert.True(canvas.BeginRayAuthoring());
            Assert.True(canvas.TryAcceptRayAuthoringInput("0,0", out _));
            Assert.True(canvas.TryAcceptRayAuthoringInput("1,0", out _));
            Assert.True(canvas.TryAcceptRayAuthoringInput("0,1", out _));

            var firstDrawing = new DrawingContext();
            canvas.OnRender(firstDrawing);
            GpuPicture firstPreview = GetRayPreview(firstDrawing, 2);
            RenderCommand path = Assert.Single(firstPreview.Commands);
            Assert.Equal(RenderCommandType.DrawPath, path.Type);
            Assert.Equal(2, path.Path!.Figures.Count);

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
            var resizedDrawing = new DrawingContext();
            canvas.OnRender(resizedDrawing);
            GpuPicture resizedPreview = GetRayPreview(resizedDrawing, 2);

            Assert.NotSame(firstPreview, resizedPreview);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(session.Read(document => document.Entities.ToArray()));
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void ObjectSnapAndDirectDistanceResolveFromTheCommonStart()
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
            Assert.True(canvas.BeginRayAuthoring());
            Assert.True(canvas.TryAcceptRayAuthoringInput("1,3", out _));
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
            Assert.True(canvas.TryAcceptRayAuthoringInput("5", out _));
            Assert.True(canvas.CompleteRayAuthoring(out _));

            Ray[] rays = document.Entities.OfType<Ray>().ToArray();
            Assert.Equal(2, rays.Length);
            AssertDirection(new CadPoint3D(1, 0, 0), rays[0]);
            AssertDirection(new CadPoint3D(0, 1, 0), rays[1]);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedButtonsAndKeyboardExposeRayUndoEnterAndEscape()
    {
        var session = new CadDocumentSession(new CadDocument());
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Assert.True(view.RayButton.IsEnabled);
            PressEnter(view.RayButton);
            Assert.True(view.Canvas.IsRayAuthoring);
            Assert.True(view.RayFinishButton.IsEnabled);
            Assert.False(view.RayUndoButton.IsEnabled);

            Enter(view, "0,0");
            Enter(view, "4,0");
            Assert.True(view.RayUndoButton.IsEnabled);
            PressEnter(view.RayUndoButton);
            Assert.Equal(0, view.Canvas.PendingRayCount);
            Enter(view, "0,4");

            var escape = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Escape,
            };
            view.OnKeyDown(escape);

            Assert.True(escape.Handled);
            Assert.False(view.Canvas.IsRayAuthoring);
            Ray ray = Assert.Single(session.Read(document =>
                document.Entities.OfType<Ray>().ToArray()));
            AssertDirection(new CadPoint3D(0, 1, 0), ray);
            Assert.Equal(1, view.Canvas.UndoCount);
            Assert.True(view.RayButton.IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void EmptyEnterEndsRayWithoutChangingTheDocumentGeneration()
    {
        var session = new CadDocumentSession(new CadDocument());
        var view = new CadSampleView();
        try
        {
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            PressEnter(view.RayButton);

            view.PointTransformInput.Text = string.Empty;
            view.PointTransformInput.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Enter,
            });

            Assert.False(view.Canvas.IsRayAuthoring);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Empty(session.Read(document => document.Entities.ToArray()));
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    private static GpuPicture GetRayPreview(
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

    private static void AssertDirection(CadPoint3D expected, Ray ray)
    {
        Assert.Equal(expected.X, ray.Direction.X, 12);
        Assert.Equal(expected.Y, ray.Direction.Y, 12);
        Assert.Equal(expected.Z, ray.Direction.Z, 12);
    }
}

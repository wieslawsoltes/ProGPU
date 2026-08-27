using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Microsoft.UI.Xaml;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadSampleSelectionTests
{
    [Fact]
    public void LeftClickSelectsSemanticHandleAndEmptyClickClearsIt()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(-10, 0, 0), new XYZ(10, 0, 0));
        document.Entities.Add(line);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(new CadDocumentSession(document));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Vector2 lineScreen = canvas.CurrentViewport.WorldToScreen(CadPoint3D.Zero);
            int selectionChangeCount = 0;
            canvas.SelectionChanged += (_, _) => selectionChangeCount++;

            Click(canvas, lineScreen);

            Assert.Equal(1, selectionChangeCount);
            Assert.Equal(1, canvas.SelectedHandleCount);
            Assert.Equal(line.Handle, canvas.SelectedHandles.Span[0]);
            Assert.Equal(CadBoundsSelectionMode.Crossing, canvas.LastSelectionMode);
            Assert.False(canvas.LastSelectionWasTruncated);
            var context = new DrawingContext();
            canvas.OnRender(context);
            RenderCommand[] commands = context.Commands.ToArray();
            Assert.Contains(commands, command => command.Type == RenderCommandType.DrawPicture);
            Assert.Equal(
                2,
                commands.Count(command => command.Type == RenderCommandType.DrawRect));

            Click(canvas, new Vector2(20, 20));

            Assert.Equal(2, selectionChangeCount);
            Assert.Equal(0, canvas.SelectedHandleCount);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void ActiveDragRecordsTransientOverlayAndCancelDoesNotSelect()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(new XYZ(-10, 0, 0), new XYZ(10, 0, 0)));
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(new CadDocumentSession(document));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = canvas.CurrentViewport;
            Vector2 start = viewport.WorldToScreen(new CadPoint3D(-5, 1, 0));
            Vector2 end = viewport.WorldToScreen(new CadPoint3D(5, -1, 0));

            canvas.OnPointerPressed(new PointerRoutedEventArgs
            {
                Position = start,
                IsLeftButtonPressed = true,
            });
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = end,
                IsLeftButtonPressed = true,
            });
            var context = new DrawingContext();
            canvas.OnRender(context);

            Assert.Equal(
                2,
                context.Commands.Count(command => command.Type == RenderCommandType.DrawRect));
            Assert.Equal(0, canvas.SelectedHandleCount);
            Assert.Null(canvas.LastSelectionMode);

            canvas.OnPointerCanceled(new PointerRoutedEventArgs { Position = end });

            Assert.Equal(0, canvas.SelectedHandleCount);
            Assert.Null(canvas.LastSelectionMode);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void DragDirectionChoosesWindowOrCrossingSelection()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(new XYZ(-5, 0, 0), new XYZ(-1, 0, 0)));
        var crossingLine = new Line(new XYZ(2, 0, 0), new XYZ(8, 0, 0));
        document.Entities.Add(crossingLine);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(new CadDocumentSession(document));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = canvas.CurrentViewport;
            Vector2 leftTop = viewport.WorldToScreen(new CadPoint3D(0, 1, 0));
            Vector2 rightBottom = viewport.WorldToScreen(new CadPoint3D(5, -1, 0));

            Drag(canvas, leftTop, rightBottom);

            Assert.Equal(CadBoundsSelectionMode.Window, canvas.LastSelectionMode);
            Assert.Equal(0, canvas.SelectedHandleCount);

            Drag(canvas, rightBottom with { Y = leftTop.Y }, leftTop with { Y = rightBottom.Y });

            Assert.Equal(CadBoundsSelectionMode.Crossing, canvas.LastSelectionMode);
            Assert.Equal(1, canvas.SelectedHandleCount);
            Assert.Equal(crossingLine.Handle, canvas.SelectedHandles.Span[0]);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void MiddleDragPansWithoutChangingSelection()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(-10, 0, 0), new XYZ(10, 0, 0));
        document.Entities.Add(line);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(new CadDocumentSession(document));
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Vector2 before = canvas.CurrentViewport.WorldToScreen(CadPoint3D.Zero);
            Click(canvas, before);

            canvas.OnPointerPressed(new PointerRoutedEventArgs
            {
                Position = new Vector2(100, 100),
                IsMiddleButtonPressed = true,
            });
            canvas.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = new Vector2(125, 130),
                IsMiddleButtonPressed = true,
            });
            canvas.OnPointerReleased(new PointerRoutedEventArgs
            {
                Position = new Vector2(125, 130),
            });

            Vector2 after = canvas.CurrentViewport.WorldToScreen(CadPoint3D.Zero);
            Assert.Equal(before + new Vector2(25, 30), after);
            Assert.Equal(1, canvas.SelectedHandleCount);
            Assert.Equal(line.Handle, canvas.SelectedHandles.Span[0]);
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

    private static void Drag(
        CadSampleCanvas canvas,
        Vector2 start,
        Vector2 end)
    {
        canvas.OnPointerPressed(new PointerRoutedEventArgs
        {
            Position = start,
            IsLeftButtonPressed = true,
        });
        canvas.OnPointerMoved(new PointerRoutedEventArgs
        {
            Position = end,
            IsLeftButtonPressed = true,
        });
        canvas.OnPointerReleased(new PointerRoutedEventArgs
        {
            Position = end,
        });
    }
}

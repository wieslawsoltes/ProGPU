using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
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

    [Fact]
    public void SelectedEntitiesMoveAsOneGenerationAndUndoRedoRebuildTheScene()
    {
        var document = new CadDocument();
        var first = new Line(new XYZ(-10, 0, 0), new XYZ(-5, 0, 0));
        var second = new Line(new XYZ(5, 0, 0), new XYZ(10, 0, 0));
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport initialViewport = canvas.CurrentViewport;
            Vector2 initialFirstScreen = initialViewport.WorldToScreen(
                new CadPoint3D(-10, 0, 0));
            Drag(
                canvas,
                initialViewport.WorldToScreen(new CadPoint3D(-12, 2, 0)),
                initialViewport.WorldToScreen(new CadPoint3D(12, -2, 0)));
            var initialContext = new DrawingContext();
            canvas.OnRender(initialContext);
            GpuPicture initialPicture = initialContext.Commands
                .Single(command => command.Type == RenderCommandType.DrawPicture)
                .Picture!;
            int editStateChanges = 0;
            canvas.EditStateChanged += (_, _) => editStateChanges++;

            Assert.Equal(2, canvas.SelectedHandleCount);
            Assert.True(canvas.TranslateSelection(new CadPoint3D(10, 3, 0)));

            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(session.ContentGeneration, canvas.CurrentSnapshot!.ContentGeneration);
            Assert.Equal(new XYZ(0, 3, 0), first.StartPoint);
            Assert.Equal(new XYZ(15, 3, 0), second.StartPoint);
            Assert.Equal(2, canvas.SelectedHandleCount);
            Assert.Equal(1, canvas.UndoCount);
            Assert.Equal(0, canvas.RedoCount);
            Assert.Equal(1, editStateChanges);
            Assert.Throws<ObjectDisposedException>(() => initialPicture.Clone());
            var movedContext = new DrawingContext();
            canvas.OnRender(movedContext);
            GpuPicture movedPicture = movedContext.Commands
                .Single(command => command.Type == RenderCommandType.DrawPicture)
                .Picture!;
            Assert.NotSame(initialPicture, movedPicture);
            using (GpuPicture ownershipProbe = movedPicture.Clone())
            {
                Assert.Equal(movedPicture.CommandCount, ownershipProbe.CommandCount);
            }
            Vector2 movedFirstScreen = canvas.CurrentViewport.WorldToScreen(
                new CadPoint3D(0, 3, 0));
            Vector2 expectedMovedFirstScreen = initialFirstScreen + new Vector2(
                10 * initialViewport.Zoom,
                -3 * initialViewport.Zoom);
            Assert.Equal(expectedMovedFirstScreen.X, movedFirstScreen.X, 4);
            Assert.Equal(expectedMovedFirstScreen.Y, movedFirstScreen.Y, 4);

            Assert.True(canvas.TryUndo());

            Assert.Equal(2UL, session.ContentGeneration);
            Assert.Equal(session.ContentGeneration, canvas.CurrentSnapshot.ContentGeneration);
            Assert.Equal(new XYZ(-10, 0, 0), first.StartPoint);
            Vector2 undoneFirstScreen = canvas.CurrentViewport.WorldToScreen(
                new CadPoint3D(-10, 0, 0));
            Assert.Equal(initialFirstScreen.X, undoneFirstScreen.X, 4);
            Assert.Equal(initialFirstScreen.Y, undoneFirstScreen.Y, 4);
            Assert.Equal(0, canvas.UndoCount);
            Assert.Equal(1, canvas.RedoCount);

            Assert.True(canvas.TryRedo());

            Assert.Equal(3UL, session.ContentGeneration);
            Assert.Equal(session.ContentGeneration, canvas.CurrentSnapshot.ContentGeneration);
            Assert.Equal(new XYZ(0, 3, 0), first.StartPoint);
            Assert.Equal(1, canvas.UndoCount);
            Assert.Equal(0, canvas.RedoCount);
            Assert.Equal(3, editStateChanges);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void MoveWithoutSelectionDoesNotPublishAnEdit()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);

            Assert.False(canvas.TranslateSelection(new CadPoint3D(1, 0, 0)));
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Equal(0, canvas.UndoCount);
            Assert.Equal(0, canvas.RedoCount);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void DeleteSelectionClearsSemanticHandlesAndRoundTripsAsOneEdit()
    {
        var document = new CadDocument();
        var first = new Line(new XYZ(-10, 0, 0), new XYZ(-5, 0, 0));
        var second = new Line(new XYZ(5, 0, 0), new XYZ(10, 0, 0));
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = canvas.CurrentViewport;
            Drag(
                canvas,
                viewport.WorldToScreen(new CadPoint3D(-12, 2, 0)),
                viewport.WorldToScreen(new CadPoint3D(12, -2, 0)));
            int selectionChanges = 0;
            int editStateChanges = 0;
            canvas.SelectionChanged += (_, _) => selectionChanges++;
            canvas.EditStateChanged += (_, _) => editStateChanges++;

            Assert.Equal(2, canvas.SelectedHandleCount);
            Assert.True(canvas.DeleteSelection());

            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(session.ContentGeneration, canvas.CurrentSnapshot!.ContentGeneration);
            Assert.Equal(0, canvas.SelectedHandleCount);
            Assert.Empty(canvas.CurrentSnapshot.Entities.ToArray());
            Assert.Empty(document.Entities);
            Assert.Equal(1, selectionChanges);
            Assert.Equal(1, editStateChanges);
            Assert.Equal(1, canvas.UndoCount);
            Assert.Equal(0, canvas.RedoCount);

            Assert.True(canvas.TryUndo());

            Assert.Equal(2UL, session.ContentGeneration);
            Assert.Equal(2, document.Entities.Count);
            Assert.Equal(2, canvas.CurrentSnapshot.Entities.Length);
            Assert.Equal(0, canvas.SelectedHandleCount);
            Assert.Equal(0, canvas.UndoCount);
            Assert.Equal(1, canvas.RedoCount);

            Assert.True(canvas.TryRedo());

            Assert.Equal(3UL, session.ContentGeneration);
            Assert.Empty(document.Entities);
            Assert.Empty(canvas.CurrentSnapshot.Entities.ToArray());
            Assert.Equal(1, canvas.UndoCount);
            Assert.Equal(0, canvas.RedoCount);
            Assert.Equal(3, editStateChanges);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SelectedEntitiesRotateAndScaleAroundOneCompleteBoundsCenter()
    {
        var document = new CadDocument();
        var first = new Line(new XYZ(0, 0, 0), new XYZ(2, 0, 0));
        var second = new Line(new XYZ(2, 2, 0), new XYZ(4, 2, 0));
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport initialViewport = canvas.CurrentViewport;
            CadPoint3D pivot = new(2, 1, 0);
            Vector2 initialPivotScreen = initialViewport.WorldToScreen(pivot);
            Drag(
                canvas,
                initialViewport.WorldToScreen(new CadPoint3D(-1, 3, 0)),
                initialViewport.WorldToScreen(new CadPoint3D(5, -1, 0)));

            Assert.Equal(2, canvas.SelectedHandleCount);
            Assert.True(canvas.RotateSelection(Math.PI / 2.0));

            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(session.ContentGeneration, canvas.CurrentSnapshot!.ContentGeneration);
            AssertPoint(new XYZ(3, -1, 0), first.StartPoint);
            AssertPoint(new XYZ(3, 1, 0), first.EndPoint);
            AssertPoint(new XYZ(1, 1, 0), second.StartPoint);
            AssertPoint(new XYZ(1, 3, 0), second.EndPoint);
            Assert.Equal(2, canvas.SelectedHandleCount);
            AssertVector(initialPivotScreen, canvas.CurrentViewport.WorldToScreen(pivot));

            Assert.True(canvas.ScaleSelection(2.0));

            Assert.Equal(2UL, session.ContentGeneration);
            Assert.Equal(session.ContentGeneration, canvas.CurrentSnapshot.ContentGeneration);
            AssertPoint(new XYZ(4, -3, 0), first.StartPoint);
            AssertPoint(new XYZ(4, 1, 0), first.EndPoint);
            AssertPoint(new XYZ(0, 1, 0), second.StartPoint);
            AssertPoint(new XYZ(0, 5, 0), second.EndPoint);
            AssertVector(initialPivotScreen, canvas.CurrentViewport.WorldToScreen(pivot));
            Assert.Equal(2, canvas.UndoCount);

            Assert.True(canvas.TryUndo());
            AssertPoint(new XYZ(3, -1, 0), first.StartPoint);
            Assert.True(canvas.TryUndo());
            AssertPoint(XYZ.Zero, first.StartPoint);
            Assert.Equal(0, canvas.UndoCount);
            Assert.Equal(2, canvas.RedoCount);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void ExpandedRootTransformUsesTheCompleteSemanticSelectionBounds()
    {
        var document = new CadDocument();
        var block = new BlockRecord("SHELL_TRANSFORM_ROOT");
        block.Entities.Add(new Line(new XYZ(-10, 0, 0), new XYZ(-8, 0, 0)));
        block.Entities.Add(new Line(new XYZ(8, 0, 0), new XYZ(10, 0, 0)));
        var insert = new Insert(block) { InsertPoint = new XYZ(20, 5, 0) };
        document.Entities.Add(insert);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            Click(
                canvas,
                canvas.CurrentViewport.WorldToScreen(new CadPoint3D(11, 5, 0)));

            Assert.Equal(1, canvas.SelectedHandleCount);
            Assert.Equal(insert.Handle, canvas.SelectedHandles.Span[0]);
            Assert.True(canvas.RotateSelection(Math.PI));

            AssertPoint(new XYZ(20, 5, 0), insert.InsertPoint);
            Assert.Equal(Math.PI, Math.Abs(insert.Rotation), 12);
            Assert.Equal(1UL, session.ContentGeneration);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewMoveAndHistoryButtonsTrackCanvasState()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(-2, 0, 0), new XYZ(2, 0, 0));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            Button movePositiveX = FindButton(view, "+X");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");
            TextBox moveStep = DescendantsAndSelf(view)
                .OfType<TextBox>()
                .Single(textBox => textBox.Text == "1");
            Assert.False(movePositiveX.IsEnabled);
            Assert.False(undo.IsEnabled);
            Assert.False(redo.IsEnabled);

            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Click(
                view.Canvas,
                view.Canvas.CurrentViewport.WorldToScreen(CadPoint3D.Zero));

            Assert.True(movePositiveX.IsEnabled);
            moveStep.Text = "not-a-number";
            movePositiveX.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Enter,
            });
            Assert.Equal(new XYZ(-2, 0, 0), line.StartPoint);
            Assert.Equal(0, view.Canvas.UndoCount);

            moveStep.Text = "1";
            movePositiveX.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Enter,
            });

            Assert.Equal(new XYZ(-1, 0, 0), line.StartPoint);
            Assert.True(undo.IsEnabled);
            Assert.False(redo.IsEnabled);

            undo.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Enter,
            });

            Assert.Equal(new XYZ(-2, 0, 0), line.StartPoint);
            Assert.False(undo.IsEnabled);
            Assert.True(redo.IsEnabled);

            redo.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Enter,
            });

            Assert.Equal(new XYZ(-1, 0, 0), line.StartPoint);
            Assert.True(undo.IsEnabled);
            Assert.False(redo.IsEnabled);
        }
        finally
        {
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewDeleteButtonAndKeyUseTheSameAtomicHistoryAction()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(-2, 0, 0), new XYZ(2, 0, 0));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            Button delete = FindButton(view, "Delete");
            Button undo = FindButton(view, "Undo");
            Assert.False(delete.IsEnabled);

            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Click(
                view.Canvas,
                view.Canvas.CurrentViewport.WorldToScreen(CadPoint3D.Zero));

            Assert.True(delete.IsEnabled);
            PressEnter(delete);

            Assert.Empty(document.Entities);
            Assert.Equal(0, view.Canvas.SelectedHandleCount);
            Assert.True(undo.IsEnabled);
            PressEnter(undo);

            Assert.Single(document.Entities);
            Assert.Equal(0, view.Canvas.SelectedHandleCount);
            Line restored = Assert.IsType<Line>(document.Entities.Single());
            Click(
                view.Canvas,
                view.Canvas.CurrentViewport.WorldToScreen(CadPoint3D.Zero));
            Assert.Equal(restored.Handle, view.Canvas.SelectedHandles.Span[0]);
            var deleteKey = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Delete,
            };

            view.OnKeyDown(deleteKey);

            Assert.True(deleteKey.Handled);
            Assert.Empty(document.Entities);
            Assert.Equal(3UL, session.ContentGeneration);
            Assert.Equal(0, view.Canvas.SelectedHandleCount);
        }
        finally
        {
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewDrawOrderButtonsPreserveSelectionAndUseHistory()
    {
        var document = new CadDocument();
        var first = new Line(new XYZ(-10, 0, 0), new XYZ(-6, 0, 0));
        var second = new Line(new XYZ(6, 0, 0), new XYZ(10, 0, 0));
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            Button toBack = FindButton(view, "To back");
            Button toFront = FindButton(view, "To front");
            Assert.False(toBack.IsEnabled);
            Assert.False(toFront.IsEnabled);

            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Click(
                view.Canvas,
                view.Canvas.CurrentViewport.WorldToScreen(
                    new CadPoint3D(-8, 0, 0)));

            Assert.Equal(first.Handle, view.Canvas.SelectedHandles.Span[0]);
            Assert.True(toBack.IsEnabled);
            Assert.True(toFront.IsEnabled);
            PressEnter(toFront);

            Assert.Equal(new double[] { 6, -10 },
                view.Canvas.CurrentSnapshot!.Lines.ToArray().Select(line => line.Start.X));
            Assert.Equal(first.Handle, view.Canvas.SelectedHandles.Span[0]);
            Assert.Equal(1, view.Canvas.UndoCount);

            PressEnter(toBack);

            Assert.Equal(new double[] { -10, 6 },
                view.Canvas.CurrentSnapshot.Lines.ToArray().Select(line => line.Start.X));
            Assert.Equal(first.Handle, view.Canvas.SelectedHandles.Span[0]);
            Assert.Equal(2, view.Canvas.UndoCount);
            Assert.True(view.Canvas.TryUndo());
            Assert.Equal(new double[] { 6, -10 },
                view.Canvas.CurrentSnapshot.Lines.ToArray().Select(line => line.Start.X));
        }
        finally
        {
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void CanvasAccumulatesDisjointDrawOrderReferencesAndCommitsOnce()
    {
        var document = new CadDocument();
        var selected = new Line(new XYZ(-30, 0, 0), new XYZ(-28, 0, 0));
        var firstReference = new Line(new XYZ(-10, 0, 0), new XYZ(-8, 0, 0));
        var secondReference = new Line(new XYZ(10, 0, 0), new XYZ(12, 0, 0));
        var last = new Line(new XYZ(30, 0, 0), new XYZ(32, 0, 0));
        document.Entities.Add(selected);
        document.Entities.Add(firstReference);
        document.Entities.Add(secondReference);
        document.Entities.Add(last);
        var session = new CadDocumentSession(document);
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = canvas.CurrentViewport;
            Click(
                canvas,
                viewport.WorldToScreen(new CadPoint3D(-29, 0, 0)));
            int pickChanges = 0;
            canvas.DrawOrderReferencePickChanged += (_, _) => pickChanges++;

            Assert.True(canvas.BeginSelectionDrawOrderReferencePick(
                CadDrawOrderPlacement.BringAbove));

            Assert.Equal(
                CadDrawOrderPlacement.BringAbove,
                canvas.PendingDrawOrderPlacement);
            Assert.Equal(0, canvas.DrawOrderReferenceHandleCount);
            Assert.Equal(0UL, session.ContentGeneration);

            Click(
                canvas,
                viewport.WorldToScreen(new CadPoint3D(-29, 0, 0)));

            Assert.Equal(0, canvas.DrawOrderReferenceHandleCount);
            Assert.Equal(selected.Handle, canvas.SelectedHandles.Span[0]);
            Assert.False(canvas.CommitSelectionDrawOrderReferencePick());
            Assert.Equal(0UL, session.ContentGeneration);

            Click(
                canvas,
                viewport.WorldToScreen(new CadPoint3D(-9, 0, 0)));
            Drag(
                canvas,
                viewport.WorldToScreen(new CadPoint3D(9, 1, 0)),
                viewport.WorldToScreen(new CadPoint3D(13, -1, 0)));

            Assert.Equal(2, canvas.DrawOrderReferenceHandleCount);
            Assert.Equal(
                new[] { firstReference.Handle, secondReference.Handle },
                canvas.DrawOrderReferenceHandles.ToArray());
            Assert.Equal(CadBoundsSelectionMode.Window,
                canvas.LastDrawOrderReferenceSelectionMode);
            Assert.False(canvas.LastDrawOrderReferenceSelectionWasTruncated);
            Assert.Equal(0, canvas.LastDrawOrderReferenceUnsupportedPrimitiveCount);
            Assert.Equal(0UL, session.ContentGeneration);
            var context = new DrawingContext();
            canvas.OnRender(context);
            Assert.Equal(
                3,
                context.Commands.Count(command =>
                    command.Type == RenderCommandType.DrawRect));

            Assert.True(canvas.CommitSelectionDrawOrderReferencePick());

            Assert.Null(canvas.PendingDrawOrderPlacement);
            Assert.Equal(0, canvas.DrawOrderReferenceHandleCount);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, canvas.UndoCount);
            Assert.Equal(selected.Handle, canvas.SelectedHandles.Span[0]);
            Assert.Equal(
                new double[] { -10, 10, -30, 30 },
                canvas.CurrentSnapshot!.Lines.ToArray().Select(line => line.Start.X));
            Assert.True(pickChanges >= 5);

            Assert.True(canvas.TryUndo());
            Assert.Equal(
                new double[] { -30, -10, 10, 30 },
                canvas.CurrentSnapshot.Lines.ToArray().Select(line => line.Start.X));
            Assert.Equal(selected.Handle, canvas.SelectedHandles.Span[0]);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewAboveUnderPromptUsesEnterAndEscapeWithoutPartialEdits()
    {
        var document = new CadDocument();
        var first = new Line(new XYZ(-30, 0, 0), new XYZ(-28, 0, 0));
        var reference = new Line(new XYZ(-10, 0, 0), new XYZ(-8, 0, 0));
        var third = new Line(new XYZ(10, 0, 0), new XYZ(12, 0, 0));
        var selected = new Line(new XYZ(30, 0, 0), new XYZ(32, 0, 0));
        document.Entities.Add(first);
        document.Entities.Add(reference);
        document.Entities.Add(third);
        document.Entities.Add(selected);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            Button above = FindButton(view, "Above…");
            Button under = FindButton(view, "Under…");
            Button toFront = FindButton(view, "To front");
            Assert.False(above.IsEnabled);
            Assert.False(under.IsEnabled);

            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            CadPlanViewport viewport = view.Canvas.CurrentViewport;
            Click(
                view.Canvas,
                viewport.WorldToScreen(new CadPoint3D(31, 0, 0)));

            Assert.True(above.IsEnabled);
            Assert.True(under.IsEnabled);
            PressEnter(under);

            Assert.Equal(
                CadDrawOrderPlacement.SendUnder,
                view.Canvas.PendingDrawOrderPlacement);
            Assert.False(toFront.IsEnabled);
            Click(
                view.Canvas,
                viewport.WorldToScreen(new CadPoint3D(-9, 0, 0)));
            var enter = new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.Enter };

            view.OnKeyDown(enter);

            Assert.True(enter.Handled);
            Assert.Null(view.Canvas.PendingDrawOrderPlacement);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(
                new double[] { -30, 30, -10, 10 },
                view.Canvas.CurrentSnapshot!.Lines.ToArray().Select(line => line.Start.X));
            Assert.Equal(selected.Handle, view.Canvas.SelectedHandles.Span[0]);
            Assert.True(above.IsEnabled);

            PressEnter(above);
            Assert.NotNull(view.Canvas.PendingDrawOrderPlacement);
            var escape = new KeyRoutedEventArgs { Key = Silk.NET.Input.Key.Escape };

            view.OnKeyDown(escape);

            Assert.True(escape.Handled);
            Assert.Null(view.Canvas.PendingDrawOrderPlacement);
            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(1, view.Canvas.UndoCount);
            Assert.True(toFront.IsEnabled);
        }
        finally
        {
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewRotateAndScaleControlsValidateAndExecuteTransforms()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(-2, 0, 0), new XYZ(2, 0, 0));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            Button rotateCounterclockwise = FindButton(view, "↺");
            Button scaleUp = FindButton(view, "×");
            Button scaleDown = FindButton(view, "÷");
            TextBox rotationStep = DescendantsAndSelf(view)
                .OfType<TextBox>()
                .Single(textBox => textBox.Text == "15");
            TextBox scaleFactor = DescendantsAndSelf(view)
                .OfType<TextBox>()
                .Single(textBox => textBox.Text == "2");
            Assert.False(rotateCounterclockwise.IsEnabled);
            Assert.False(scaleUp.IsEnabled);
            Assert.False(scaleDown.IsEnabled);

            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Click(
                view.Canvas,
                view.Canvas.CurrentViewport.WorldToScreen(CadPoint3D.Zero));

            Assert.True(rotateCounterclockwise.IsEnabled);
            Assert.True(scaleUp.IsEnabled);
            Assert.True(scaleDown.IsEnabled);
            rotationStep.Text = "invalid";
            PressEnter(rotateCounterclockwise);
            AssertPoint(new XYZ(-2, 0, 0), line.StartPoint);
            Assert.Equal(0UL, session.ContentGeneration);

            rotationStep.Text = "90";
            PressEnter(rotateCounterclockwise);
            AssertPoint(new XYZ(0, -2, 0), line.StartPoint);
            Assert.Equal(1UL, session.ContentGeneration);

            scaleFactor.Text = "1";
            PressEnter(scaleUp);
            AssertPoint(new XYZ(0, -2, 0), line.StartPoint);
            Assert.Equal(1UL, session.ContentGeneration);

            scaleFactor.Text = "2";
            PressEnter(scaleUp);
            AssertPoint(new XYZ(0, -4, 0), line.StartPoint);
            Assert.Equal(2UL, session.ContentGeneration);

            PressEnter(scaleDown);
            AssertPoint(new XYZ(0, -2, 0), line.StartPoint);
            Assert.Equal(3UL, session.ContentGeneration);
            Assert.Equal(3, view.Canvas.UndoCount);
        }
        finally
        {
            view.Canvas.FireUnloaded();
        }
    }

    private static Button FindButton(Visual root, string label) =>
        DescendantsAndSelf(root)
            .OfType<Button>()
            .Single(button => button.Content is TextBlock text && text.Text == label);

    private static IEnumerable<Visual> DescendantsAndSelf(Visual visual)
    {
        yield return visual;
        if (visual is not ContainerVisual container)
        {
            yield break;
        }
        foreach (Visual child in container.Children)
        {
            foreach (Visual descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }

    private static void PressEnter(Button button) =>
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

    private static void AssertVector(Vector2 expected, Vector2 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
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

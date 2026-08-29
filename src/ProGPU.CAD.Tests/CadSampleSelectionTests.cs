using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using ProGPU.Scene.Native;
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
    public void SelectedEntitiesCopyAsOneEditAndPreserveTheSourceSelection()
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
            ulong[] selectedSources = canvas.SelectedHandles.ToArray();

            Assert.Equal(2, selectedSources.Length);
            Assert.True(canvas.DuplicateSelection(new CadPoint3D(0, 5, 0)));

            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(4, document.Entities.Count);
            Assert.Equal(4, canvas.CurrentSnapshot!.Entities.Length);
            Assert.Equal(selectedSources, canvas.SelectedHandles.ToArray());
            Assert.Contains(
                document.Entities.OfType<Line>(),
                line => line.StartPoint == new XYZ(-10, 5, 0) &&
                    line.EndPoint == new XYZ(-5, 5, 0));
            Assert.Contains(
                document.Entities.OfType<Line>(),
                line => line.StartPoint == new XYZ(5, 5, 0) &&
                    line.EndPoint == new XYZ(10, 5, 0));
            Assert.Equal(1, canvas.UndoCount);

            Assert.True(canvas.TryUndo());

            Assert.Equal(2UL, session.ContentGeneration);
            Assert.Equal(2, document.Entities.Count);
            Assert.Equal(selectedSources, canvas.SelectedHandles.ToArray());

            Assert.True(canvas.TryRedo());

            Assert.Equal(3UL, session.ContentGeneration);
            Assert.Equal(4, document.Entities.Count);
            Assert.Equal(selectedSources, canvas.SelectedHandles.ToArray());
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SelectionPropertiesCaptureMixedValuesAndRoundTripRenderedEdits()
    {
        var document = new CadDocument();
        var first = new Line(new XYZ(-10, 0, 0), new XYZ(-5, 0, 0))
        {
            Color = ACadSharp.Color.ByLayer,
            LineWeight = LineWeightType.W0,
        };
        var second = new Line(new XYZ(5, 0, 0), new XYZ(10, 0, 0))
        {
            Color = ACadSharp.Color.Blue,
            LineWeight = LineWeightType.ByLayer,
        };
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

            CadSelectionGeneralProperties mixed =
                canvas.CaptureSelectionGeneralProperties();
            Assert.Equal(2, mixed.SelectionCount);
            Assert.Null(mixed.CommonColor);
            Assert.Null(mixed.CommonLineWeight);
            var color = new ACadSharp.Color(12, 34, 56);

            Assert.True(canvas.SetSelectionColor(color));
            Assert.True(canvas.SetSelectionLineWeight(LineWeightType.W100));

            Assert.Equal(2UL, session.ContentGeneration);
            Assert.Equal(2, canvas.SelectedHandleCount);
            Assert.Equal(color, first.Color);
            Assert.Equal(color, second.Color);
            Assert.Equal(LineWeightType.W100, first.LineWeight);
            Assert.Equal(LineWeightType.W100, second.LineWeight);
            CadSelectionGeneralProperties common =
                canvas.CaptureSelectionGeneralProperties();
            Assert.Equal(color, common.CommonColor);
            Assert.Equal(LineWeightType.W100, common.CommonLineWeight);
            Assert.All(canvas.CurrentSnapshot!.Styles.ToArray(), style =>
            {
                Assert.Equal((byte)12, style.Red);
                Assert.Equal((byte)34, style.Green);
                Assert.Equal((byte)56, style.Blue);
                Assert.Equal(1.0, style.LineWeightMillimeters);
            });

            Assert.True(canvas.TryUndo());

            CadSelectionGeneralProperties weightUndone =
                canvas.CaptureSelectionGeneralProperties();
            Assert.Equal(color, weightUndone.CommonColor);
            Assert.Null(weightUndone.CommonLineWeight);
            Assert.Equal(LineWeightType.W0, first.LineWeight);
            Assert.Equal(LineWeightType.ByLayer, second.LineWeight);

            Assert.True(canvas.TryUndo());

            CadSelectionGeneralProperties colorUndone =
                canvas.CaptureSelectionGeneralProperties();
            Assert.Null(colorUndone.CommonColor);
            Assert.Null(colorUndone.CommonLineWeight);
            Assert.Equal(ACadSharp.Color.ByLayer, first.Color);
            Assert.Equal(ACadSharp.Color.Blue, second.Color);

            Assert.True(canvas.TryRedo());
            Assert.True(canvas.TryRedo());
            Assert.Equal(color, first.Color);
            Assert.Equal(LineWeightType.W100, second.LineWeight);
            Assert.Equal(6UL, session.ContentGeneration);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewEditsCompleteSelectionColorAndLineweightWithMixedState()
    {
        var document = new CadDocument();
        var first = new Line(new XYZ(-10, 0, 0), new XYZ(-5, 0, 0))
        {
            Color = ACadSharp.Color.Red,
            LineWeight = LineWeightType.W0,
        };
        var second = new Line(new XYZ(5, 0, 0), new XYZ(10, 0, 0))
        {
            Color = ACadSharp.Color.ByLayer,
            LineWeight = LineWeightType.ByLayer,
        };
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_280, 850));
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 1_280, 624));
            CadPlanViewport viewport = view.Canvas.CurrentViewport;
            Drag(
                view.Canvas,
                viewport.WorldToScreen(new CadPoint3D(-12, 2, 0)),
                viewport.WorldToScreen(new CadPoint3D(12, -2, 0)));
            Button setColor = FindButton(view, "Set color");
            Button setLineWeight = FindButton(view, "Set lineweight");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");

            Assert.Equal("*VARIES*", view.SelectionColorInput.Text);
            Assert.Equal(
                "*VARIES*",
                Assert.IsType<ComboBoxItem>(
                    view.SelectionLineWeightSelector.SelectedItem).Text);
            Assert.False(setColor.IsEnabled);
            Assert.False(setLineWeight.IsEnabled);

            view.SelectionColorInput.Text = "#0C2238";
            Assert.True(setColor.IsEnabled);
            PressEnter(setColor);

            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(2, view.Canvas.SelectedHandleCount);
            Assert.Equal(new ACadSharp.Color(12, 34, 56), first.Color);
            Assert.Equal("#0C2238", view.SelectionColorInput.Text);
            view.SelectionLineWeightSelector.SelectedItem =
                view.SelectionLineWeightSelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Tag is LineWeightType.W100);
            Assert.True(setLineWeight.IsEnabled);
            PressEnter(setLineWeight);

            Assert.Equal(2UL, session.ContentGeneration);
            Assert.Equal(LineWeightType.W100, first.LineWeight);
            Assert.Equal(LineWeightType.W100, second.LineWeight);
            Assert.Equal(
                "1.00 mm",
                Assert.IsType<ComboBoxItem>(
                    view.SelectionLineWeightSelector.SelectedItem).Text);
            Assert.True(undo.IsEnabled);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Set lineweight 1.00 mm on 2 selected entity(s)",
                    StringComparison.Ordinal));

            PressEnter(undo);

            Assert.Equal(
                "*VARIES*",
                Assert.IsType<ComboBoxItem>(
                    view.SelectionLineWeightSelector.SelectedItem).Text);
            Assert.Equal("#0C2238", view.SelectionColorInput.Text);
            PressEnter(undo);
            Assert.Equal("*VARIES*", view.SelectionColorInput.Text);

            PressEnter(redo);
            PressEnter(redo);
            Assert.Equal("#0C2238", view.SelectionColorInput.Text);
            Assert.Equal(LineWeightType.W100, second.LineWeight);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SelectionExtendedPropertiesCaptureCatalogAndRoundTripRenderedEdits()
    {
        var document = new CadDocument();
        var targetLayer = new Layer("TARGET_LAYER")
        {
            Color = new ACadSharp.Color(3),
        };
        var targetLineType = new LineType("TARGET_LTYPE");
        document.Layers.Add(targetLayer);
        document.LineTypes.Add(targetLineType);
        var first = new Line(new XYZ(-10, 0, 0), new XYZ(-5, 0, 0))
        {
            LineTypeScale = 1.0,
            Transparency = Transparency.ByLayer,
        };
        var second = new Line(new XYZ(5, 0, 0), new XYZ(10, 0, 0))
        {
            Layer = targetLayer,
            LineType = LineType.Continuous,
            LineTypeScale = 2.0,
            Transparency = Transparency.ByBlock,
        };
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

            CadSelectionGeneralProperties mixed =
                canvas.CaptureSelectionGeneralProperties();
            Assert.Null(mixed.CommonLayerName);
            Assert.Null(mixed.CommonLineTypeName);
            Assert.Null(mixed.CommonLineTypeScale);
            Assert.Null(mixed.CommonTransparency);
            CadSelectionPropertyCatalog catalog =
                canvas.CaptureSelectionPropertyCatalog();
            Assert.Equal(0UL, catalog.ContentGeneration);
            Assert.Equal(
                catalog.LayerNames.ToArray().OrderBy(static name => name),
                catalog.LayerNames.ToArray());
            Assert.Contains("TARGET_LAYER", catalog.LayerNames.ToArray());
            Assert.Contains("TARGET_LTYPE", catalog.LineTypeNames.ToArray());

            Assert.True(canvas.SetSelectionLayer("target_layer"));
            Assert.True(canvas.SetSelectionLineType("target_ltype"));
            Assert.True(canvas.SetSelectionLineTypeScale(2.5));
            Assert.True(canvas.SetSelectionTransparency(new Transparency(30)));

            Assert.Equal(4UL, session.ContentGeneration);
            Assert.Equal(2, canvas.SelectedHandleCount);
            Assert.Same(targetLayer, first.Layer);
            Assert.Same(targetLayer, second.Layer);
            Assert.Same(targetLineType, first.LineType);
            Assert.Same(targetLineType, second.LineType);
            Assert.Equal(2.5, first.LineTypeScale);
            Assert.Equal(2.5, second.LineTypeScale);
            Assert.Equal((short)30, first.Transparency.Value);
            Assert.Equal((short)30, second.Transparency.Value);
            CadSelectionGeneralProperties common =
                canvas.CaptureSelectionGeneralProperties();
            Assert.Equal("TARGET_LAYER", common.CommonLayerName);
            Assert.Equal("TARGET_LTYPE", common.CommonLineTypeName);
            Assert.Equal(2.5, common.CommonLineTypeScale);
            Assert.Equal((short)30, common.CommonTransparency?.Value);
            Assert.All(canvas.CurrentSnapshot!.Styles.ToArray(), style =>
            {
                Assert.Equal("TARGET_LTYPE", style.LineTypeName);
                Assert.Equal(2.5, style.LineTypeScale);
                Assert.Equal((byte)178, style.Alpha);
            });

            Assert.True(canvas.TryUndo());
            Assert.True(canvas.TryUndo());
            Assert.True(canvas.TryUndo());
            Assert.True(canvas.TryUndo());
            CadSelectionGeneralProperties undone =
                canvas.CaptureSelectionGeneralProperties();
            Assert.Null(undone.CommonLayerName);
            Assert.Null(undone.CommonLineTypeName);
            Assert.Null(undone.CommonLineTypeScale);
            Assert.Null(undone.CommonTransparency);

            Assert.True(canvas.TryRedo());
            Assert.True(canvas.TryRedo());
            Assert.True(canvas.TryRedo());
            Assert.True(canvas.TryRedo());
            Assert.Equal(12UL, session.ContentGeneration);
            Assert.Equal((short)30, second.Transparency.Value);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewEditsExtendedPropertiesAndRefreshesMixedState()
    {
        var document = new CadDocument();
        var targetLayer = new Layer("UI_LAYER");
        var targetLineType = new LineType("UI_LTYPE");
        document.Layers.Add(targetLayer);
        document.LineTypes.Add(targetLineType);
        var first = new Line(new XYZ(-10, 0, 0), new XYZ(-5, 0, 0))
        {
            Transparency = Transparency.ByLayer,
        };
        var second = new Line(new XYZ(5, 0, 0), new XYZ(10, 0, 0))
        {
            Layer = targetLayer,
            LineType = LineType.Continuous,
            LineTypeScale = 2.0,
            Transparency = Transparency.ByBlock,
        };
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_280, 900));
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 1_280, 610));
            CadPlanViewport viewport = view.Canvas.CurrentViewport;
            Drag(
                view.Canvas,
                viewport.WorldToScreen(new CadPoint3D(-12, 2, 0)),
                viewport.WorldToScreen(new CadPoint3D(12, -2, 0)));
            Button setLayer = FindButton(view, "Set layer");
            Button setLineType = FindButton(view, "Set linetype");
            Button setScale = FindButton(view, "Set scale");
            Button setTransparency = FindButton(view, "Set transparency");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");

            Assert.Equal(
                "*VARIES*",
                Assert.IsType<ComboBoxItem>(
                    view.SelectionLayerSelector.SelectedItem).Text);
            Assert.Equal(
                "*VARIES*",
                Assert.IsType<ComboBoxItem>(
                    view.SelectionLineTypeSelector.SelectedItem).Text);
            Assert.Equal("*VARIES*", view.SelectionLineTypeScaleInput.Text);
            Assert.Equal("*VARIES*", view.SelectionTransparencyInput.Text);
            Assert.False(setLayer.IsEnabled);
            Assert.False(setLineType.IsEnabled);
            Assert.False(setScale.IsEnabled);
            Assert.False(setTransparency.IsEnabled);

            view.SelectionLayerSelector.SelectedItem =
                FindNamedPropertyChoice(view.SelectionLayerSelector, "UI_LAYER");
            Assert.True(setLayer.IsEnabled);
            PressEnter(setLayer);
            Assert.Same(targetLayer, first.Layer);

            view.SelectionLineTypeSelector.SelectedItem =
                FindNamedPropertyChoice(view.SelectionLineTypeSelector, "UI_LTYPE");
            Assert.True(setLineType.IsEnabled);
            PressEnter(setLineType);
            Assert.Same(targetLineType, second.LineType);

            view.SelectionLineTypeScaleInput.Text = "2.5";
            Assert.True(setScale.IsEnabled);
            PressEnter(setScale);
            Assert.Equal(2.5, first.LineTypeScale);

            view.SelectionTransparencyInput.Text = "30";
            Assert.True(setTransparency.IsEnabled);
            PressEnter(setTransparency);

            Assert.Equal(4UL, session.ContentGeneration);
            Assert.Equal(2, view.Canvas.SelectedHandleCount);
            Assert.Equal((short)30, first.Transparency.Value);
            Assert.Equal("UI_LAYER", SelectedPropertyText(view.SelectionLayerSelector));
            Assert.Equal("UI_LTYPE", SelectedPropertyText(view.SelectionLineTypeSelector));
            Assert.Equal("2.5", view.SelectionLineTypeScaleInput.Text);
            Assert.Equal("30", view.SelectionTransparencyInput.Text);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Set transparency 30 on 2 selected entity(s)",
                    StringComparison.Ordinal));

            PressEnter(undo);
            PressEnter(undo);
            PressEnter(undo);
            PressEnter(undo);
            Assert.Equal("*VARIES*", SelectedPropertyText(view.SelectionLayerSelector));
            Assert.Equal("*VARIES*", SelectedPropertyText(view.SelectionLineTypeSelector));
            Assert.Equal("*VARIES*", view.SelectionLineTypeScaleInput.Text);
            Assert.Equal("*VARIES*", view.SelectionTransparencyInput.Text);

            PressEnter(redo);
            PressEnter(redo);
            PressEnter(redo);
            PressEnter(redo);
            Assert.Equal("UI_LAYER", SelectedPropertyText(view.SelectionLayerSelector));
            Assert.Equal("UI_LTYPE", SelectedPropertyText(view.SelectionLineTypeSelector));
            Assert.Equal("2.5", view.SelectionLineTypeScaleInput.Text);
            Assert.Equal("30", view.SelectionTransparencyInput.Text);

            foreach (string invalid in new[]
            {
                string.Empty,
                "*VARIES*",
                "0",
                "-1",
                "NaN",
                "Infinity",
            })
            {
                view.SelectionLineTypeScaleInput.Text = invalid;
                Assert.False(setScale.IsEnabled);
            }
            foreach (string invalid in new[]
            {
                string.Empty,
                "*VARIES*",
                "-1",
                "91",
                "1.5",
                "Opaque",
            })
            {
                view.SelectionTransparencyInput.Text = invalid;
                Assert.False(setTransparency.IsEnabled);
            }
            foreach (string valid in new[] { "ByLayer", "byblock", "0", "90" })
            {
                view.SelectionTransparencyInput.Text = valid;
                Assert.True(setTransparency.IsEnabled);
            }
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewEditsVisibilityAndSignedSolidThicknessWithTypedState()
    {
        var document = new CadDocument();
        var first = new Solid(
            new XYZ(-10, -2, 0),
            new XYZ(-6, -2, 0),
            new XYZ(-10, 2, 0),
            new XYZ(-6, 2, 0))
        {
            Thickness = 1.0,
        };
        var second = new Solid(
            new XYZ(6, -2, 0),
            new XYZ(10, -2, 0),
            new XYZ(6, 2, 0),
            new XYZ(10, 2, 0))
        {
            Thickness = -1.0,
        };
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_360, 900));
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 1_360, 610));
            CadPlanViewport viewport = view.Canvas.CurrentViewport;
            Drag(
                view.Canvas,
                viewport.WorldToScreen(new CadPoint3D(-12, 4, 0)),
                viewport.WorldToScreen(new CadPoint3D(12, -4, 0)));
            Button setVisibility = FindButton(view, "Set visibility");
            Button setThickness = FindButton(view, "Set thickness");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");

            CadSelectionGeneralProperties mixed =
                view.Canvas.CaptureSelectionGeneralProperties();
            Assert.Equal(2, mixed.SelectionCount);
            Assert.False(mixed.CommonIsInvisible);
            Assert.True(mixed.AllSelectedEntitiesAreSolids);
            Assert.Null(mixed.CommonSolidThickness);
            Assert.Equal("Visible", SelectedPropertyText(
                view.SelectionVisibilitySelector));
            Assert.Equal("*VARIES*", view.SelectionSolidThicknessInput.Text);
            Assert.False(setThickness.IsEnabled);

            second.IsInvisible = true;
            Assert.Null(view.Canvas
                .CaptureSelectionGeneralProperties()
                .CommonIsInvisible);
            second.IsInvisible = false;

            view.SelectionVisibilitySelector.SelectedItem =
                view.SelectionVisibilitySelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Tag is false);
            Assert.True(setVisibility.IsEnabled);
            PressEnter(setVisibility);

            Assert.Equal(1UL, session.ContentGeneration);
            Assert.True(first.IsInvisible);
            Assert.True(second.IsInvisible);
            Assert.Empty(view.Canvas.CurrentSnapshot!.Entities.ToArray());
            Assert.Equal(2, view.Canvas.SelectedHandleCount);
            Assert.Equal("Hidden", SelectedPropertyText(
                view.SelectionVisibilitySelector));

            PressEnter(undo);
            Assert.False(first.IsInvisible);
            Assert.False(second.IsInvisible);
            Assert.Equal(2, view.Canvas.CurrentSnapshot!.Entities.Length);
            Assert.Equal(2, view.Canvas.SelectedHandleCount);

            view.SelectionSolidThicknessInput.Text = "-2.5";
            Assert.True(setThickness.IsEnabled);
            PressEnter(setThickness);

            Assert.Equal(3UL, session.ContentGeneration);
            Assert.Equal(-2.5, first.Thickness, 12);
            Assert.Equal(-2.5, second.Thickness, 12);
            Assert.Equal("-2.5", view.SelectionSolidThicknessInput.Text);
            Assert.Equal(
                24,
                new CadMesh3DSceneCompiler()
                    .Compile(view.Canvas.CurrentSnapshot!)
                    .Statistics
                    .TriangleCount);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Set SOLID thickness -2.5 on 2 selected entity(s)",
                    StringComparison.Ordinal));

            PressEnter(undo);
            Assert.Equal("*VARIES*", view.SelectionSolidThicknessInput.Text);
            Assert.Equal(1.0, first.Thickness, 12);
            Assert.Equal(-1.0, second.Thickness, 12);
            PressEnter(redo);
            Assert.Equal("-2.5", view.SelectionSolidThicknessInput.Text);

            foreach (string invalid in new[]
            {
                string.Empty,
                "*VARIES*",
                "NaN",
                "Infinity",
            })
            {
                view.SelectionSolidThicknessInput.Text = invalid;
                Assert.False(setThickness.IsEnabled);
            }
            foreach (string valid in new[] { "0", "4.25", "-4.25" })
            {
                view.SelectionSolidThicknessInput.Text = valid;
                Assert.True(setThickness.IsEnabled);
            }

            var lineDocument = new CadDocument();
            lineDocument.Entities.Add(new Line(
                new XYZ(-5, 0, 0),
                new XYZ(5, 0, 0)));
            view.Canvas.Load(new CadDocumentSession(lineDocument));
            view.Canvas.Arrange(new Rect(0, 0, 1_360, 610));
            Click(view.Canvas, view.Canvas.CurrentViewport.WorldToScreen(CadPoint3D.Zero));

            CadSelectionGeneralProperties inapplicable =
                view.Canvas.CaptureSelectionGeneralProperties();
            Assert.False(inapplicable.AllSelectedEntitiesAreSolids);
            Assert.Null(inapplicable.CommonSolidThickness);
            Assert.Equal("N/A", view.SelectionSolidThicknessInput.Text);
            Assert.False(view.SelectionSolidThicknessInput.IsEnabled);
            Assert.False(setThickness.IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewEditsLayerVisibilityAndPlotEligibilityIndependently()
    {
        var document = new CadDocument();
        var targetLayer = new Layer("TARGET_STATE")
        {
            IsOn = true,
            PlotFlag = true,
        };
        document.Layers.Add(targetLayer);
        document.Entities.Add(new Line(
            new XYZ(-5, 0, 0),
            new XYZ(5, 0, 0))
        {
            Layer = targetLayer,
        });
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_280, 900));
            view.Canvas.Load(session);
            view.LayerStateSelector.SelectedItem =
                FindNamedPropertyChoice(
                    view.LayerStateSelector,
                    "TARGET_STATE");
            Button setVisibility = FindButton(view, "Set layer visibility");
            Button setPlot = FindButton(view, "Set layer plot");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");

            CadLayerGeneralProperties initial =
                view.Canvas.CaptureLayerGeneralProperties("target_state");
            Assert.Equal("TARGET_STATE", initial.Name);
            Assert.True(initial.IsOn);
            Assert.True(initial.IsPlottable);
            Assert.False(initial.IsFrozen);
            Assert.False(initial.IsLocked);
            Assert.Equal("On", SelectedPropertyText(
                view.LayerVisibilitySelector));
            Assert.Equal("Plot", SelectedPropertyText(view.LayerPlotSelector));

            view.LayerVisibilitySelector.SelectedItem =
                view.LayerVisibilitySelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Tag is false);
            PressEnter(setVisibility);

            Assert.Equal(1UL, session.ContentGeneration);
            Assert.False(targetLayer.IsOn);
            Assert.Empty(view.Canvas.CurrentSnapshot!.Entities.ToArray());
            Assert.Equal("TARGET_STATE", SelectedPropertyText(
                view.LayerStateSelector));
            Assert.Equal("Off", SelectedPropertyText(
                view.LayerVisibilitySelector));
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Set layer TARGET_STATE Off as one edit",
                    StringComparison.Ordinal));

            PressEnter(undo);
            Assert.True(targetLayer.IsOn);
            Assert.Single(view.Canvas.CurrentSnapshot!.Entities.ToArray());
            Assert.Equal("On", SelectedPropertyText(
                view.LayerVisibilitySelector));

            view.LayerPlotSelector.SelectedItem =
                view.LayerPlotSelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Tag is false);
            PressEnter(setPlot);

            Assert.Equal(3UL, session.ContentGeneration);
            Assert.False(targetLayer.PlotFlag);
            Assert.Single(view.Canvas.CurrentSnapshot!.Entities.ToArray());
            CadDocumentSnapshot plotting = new CadSnapshotCompiler().Compile(
                session,
                new CadSnapshotOptions
                {
                    IncludeNonPlottableLayers = false,
                });
            Assert.Empty(plotting.Entities.ToArray());
            Assert.Equal("No plot", SelectedPropertyText(view.LayerPlotSelector));

            PressEnter(undo);
            Assert.True(targetLayer.PlotFlag);
            Assert.Equal("Plot", SelectedPropertyText(view.LayerPlotSelector));
            PressEnter(redo);
            Assert.False(targetLayer.PlotFlag);
            Assert.Equal("No plot", SelectedPropertyText(view.LayerPlotSelector));

            Assert.Throws<InvalidOperationException>(() =>
                view.Canvas.CaptureLayerGeneralProperties("MISSING"));
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewEditsLayerFreezeAndLockWithSelectionAuthorization()
    {
        var document = new CadDocument();
        var targetLayer = new Layer("TARGET_BEHAVIOR");
        document.Layers.Add(targetLayer);
        var line = new Line(new XYZ(-5, 0, 0), new XYZ(5, 0, 0))
        {
            Layer = targetLayer,
        };
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_280, 900));
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 1_280, 542));
            Click(
                view.Canvas,
                view.Canvas.CurrentViewport.WorldToScreen(CadPoint3D.Zero));
            Assert.Equal(1, view.Canvas.SelectedHandleCount);
            Assert.True(view.Canvas
                .CaptureSelectionGeneralProperties()
                .AllSelectedEntitiesAreUnlocked);
            view.LayerStateSelector.SelectedItem =
                FindNamedPropertyChoice(
                    view.LayerStateSelector,
                    "TARGET_BEHAVIOR");
            Button setFreeze = FindButton(view, "Set layer freeze");
            Button setLock = FindButton(view, "Set layer lock");
            Button setLayerColor = FindButton(view, "Set layer color");
            Button undo = FindButton(view, "Undo");
            Button delete = FindButton(view, "Delete");
            Button movePositiveX = FindButton(view, "+X");

            Assert.Equal("Thawed", SelectedPropertyText(
                view.LayerFreezeSelector));
            Assert.Equal("Unlocked", SelectedPropertyText(
                view.LayerLockSelector));
            view.LayerFreezeSelector.SelectedItem =
                view.LayerFreezeSelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Tag is true);
            PressEnter(setFreeze);

            Assert.True((targetLayer.Flags & LayerFlags.Frozen) != 0);
            Assert.Empty(view.Canvas.CurrentSnapshot!.Entities.ToArray());
            Assert.Equal(1, view.Canvas.SelectedHandleCount);
            Assert.Equal("TARGET_BEHAVIOR", SelectedPropertyText(
                view.LayerStateSelector));
            Assert.Equal("Frozen", SelectedPropertyText(
                view.LayerFreezeSelector));
            PressEnter(undo);
            Assert.False((targetLayer.Flags & LayerFlags.Frozen) != 0);
            Assert.Single(view.Canvas.CurrentSnapshot!.Entities.ToArray());

            view.LayerLockSelector.SelectedItem =
                view.LayerLockSelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Tag is true);
            PressEnter(setLock);

            Assert.True((targetLayer.Flags & LayerFlags.Locked) != 0);
            Assert.Single(view.Canvas.CurrentSnapshot!.Entities.ToArray());
            Assert.False(view.Canvas
                .CaptureSelectionGeneralProperties()
                .AllSelectedEntitiesAreUnlocked);
            Assert.False(delete.IsEnabled);
            Assert.False(movePositiveX.IsEnabled);
            Assert.False(view.SelectionColorInput.IsEnabled);
            Assert.True(setLock.IsEnabled);
            Assert.True(setLayerColor.IsEnabled);
            Assert.Throws<InvalidOperationException>(() =>
                view.Canvas.TranslateSelection(new CadPoint3D(1, 0, 0)));
            Assert.Equal(new XYZ(-5, 0, 0), line.StartPoint);

            view.LayerLockSelector.SelectedItem =
                view.LayerLockSelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Tag is false);
            PressEnter(setLock);
            Assert.False((targetLayer.Flags & LayerFlags.Locked) != 0);
            Assert.True(delete.IsEnabled);
            Assert.True(movePositiveX.IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewEditsExplicitLayerStyleAndInheritedReplay()
    {
        var document = new CadDocument();
        var dash = new LineType("TARGET_DASH");
        dash.AddSegment(new LineType.Segment { Length = 2.0 });
        dash.AddSegment(new LineType.Segment { Length = -1.0 });
        document.LineTypes.Add(dash);
        var targetLayer = new Layer("TARGET_STYLE")
        {
            Color = ACadSharp.Color.Red,
            LineWeight = LineWeightType.W50,
            LineType = document.LineTypes.Continuous,
        };
        document.Layers.Add(targetLayer);
        document.Entities.Add(new Line(
            new XYZ(-5, 0, 0),
            new XYZ(5, 0, 0))
        {
            Layer = targetLayer,
            Color = ACadSharp.Color.ByLayer,
            LineWeight = LineWeightType.ByLayer,
            LineType = document.LineTypes.ByLayer,
        });
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_280, 940));
            view.Canvas.Load(session);
            view.LayerStateSelector.SelectedItem =
                FindNamedPropertyChoice(
                    view.LayerStateSelector,
                    "TARGET_STYLE");
            Button setColor = FindButton(view, "Set layer color");
            Button setLineWeight = FindButton(view, "Set layer lineweight");
            Button setLineType = FindButton(view, "Set layer linetype");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");

            CadLayerGeneralProperties initial =
                view.Canvas.CaptureLayerGeneralProperties("target_style");
            Assert.Equal(ACadSharp.Color.Red, initial.Color);
            Assert.Equal(LineWeightType.W50, initial.LineWeight);
            Assert.Equal(LineType.ContinuousName, initial.LineTypeName);
            Assert.Equal("ACI 1", view.LayerColorInput.Text);
            Assert.Equal("0.50 mm", SelectedPropertyText(
                view.LayerLineWeightSelector));
            Assert.Equal(LineType.ContinuousName, SelectedPropertyText(
                view.LayerLineTypeSelector));
            Assert.DoesNotContain(
                view.LayerLineTypeSelector.Items.OfType<ComboBoxItem>(),
                item => item.Tag is string name &&
                    (name.Equals(LineType.ByLayerName, StringComparison.OrdinalIgnoreCase) ||
                     name.Equals(LineType.ByBlockName, StringComparison.OrdinalIgnoreCase)));

            view.LayerColorInput.Text = "#0C2238";
            PressEnter(setColor);

            Assert.Equal(1UL, session.ContentGeneration);
            Assert.True(targetLayer.Color.IsTrueColor);
            Assert.Equal((byte)12, targetLayer.Color.R);
            Assert.Equal((byte)34, targetLayer.Color.G);
            Assert.Equal((byte)56, targetLayer.Color.B);
            CadStrokeStyle colored = Assert.Single(
                view.Canvas.CurrentSnapshot!.Styles.ToArray());
            Assert.Equal((byte)12, colored.Red);
            Assert.Equal((byte)34, colored.Green);
            Assert.Equal((byte)56, colored.Blue);
            Assert.Equal("#0C2238", view.LayerColorInput.Text);

            view.LayerLineWeightSelector.SelectedItem =
                view.LayerLineWeightSelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Tag is LineWeightType.W100);
            PressEnter(setLineWeight);

            Assert.Equal(2UL, session.ContentGeneration);
            Assert.Equal(LineWeightType.W100, targetLayer.LineWeight);
            Assert.Equal(
                1.0,
                Assert.Single(view.Canvas.CurrentSnapshot!.Styles.ToArray())
                    .LineWeightMillimeters);

            view.LayerLineTypeSelector.SelectedItem =
                FindNamedPropertyChoice(
                    view.LayerLineTypeSelector,
                    "TARGET_DASH");
            PressEnter(setLineType);

            Assert.Equal(3UL, session.ContentGeneration);
            Assert.Same(dash, targetLayer.LineType);
            Assert.Equal(
                "TARGET_DASH",
                Assert.Single(view.Canvas.CurrentSnapshot!.Styles.ToArray())
                    .LineTypeName);
            CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
                view.Canvas.CurrentSnapshot);
            using (GpuPicture picture = scene.CreatePicture())
            {
                Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
                    picture,
                    96U,
                    view.Canvas.CurrentSnapshot.ContentGeneration,
                    out NativeCompiledPicture? native,
                    out NativePictureCompileFailure failure),
                    failure.ToString());
                Assert.NotNull(native);
                Assert.True(native.SourceCommandCount > 0);
                Assert.True(native.GeometryPrimitiveCount > 0);
            }
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Set layer TARGET_STYLE linetype TARGET_DASH as one edit",
                    StringComparison.Ordinal));

            PressEnter(undo);
            Assert.Same(document.LineTypes.Continuous, targetLayer.LineType);
            Assert.Equal(LineType.ContinuousName, SelectedPropertyText(
                view.LayerLineTypeSelector));
            PressEnter(redo);
            Assert.Same(dash, targetLayer.LineType);
            Assert.Equal("TARGET_DASH", SelectedPropertyText(
                view.LayerLineTypeSelector));

            view.LayerColorInput.Text = "ByLayer";
            Assert.False(setColor.IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewRefreshesPropertyCatalogAcrossEqualGenerationDocuments()
    {
        var firstDocument = new CadDocument();
        firstDocument.Layers.Add(new Layer("FIRST_LAYER"));
        firstDocument.LineTypes.Add(new LineType("FIRST_LINETYPE"));
        firstDocument.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        var secondDocument = new CadDocument();
        secondDocument.Layers.Add(new Layer("SECOND_LAYER"));
        secondDocument.LineTypes.Add(new LineType("SECOND_LINETYPE"));
        secondDocument.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_280, 900));
            view.Canvas.Load(new CadDocumentSession(firstDocument));
            Assert.Contains(
                view.SelectionLayerSelector.Items.OfType<ComboBoxItem>(),
                item => item.Tag is string name && name == "FIRST_LAYER");
            Assert.Contains(
                view.LayerStateSelector.Items.OfType<ComboBoxItem>(),
                item => item.Tag is string name && name == "FIRST_LAYER");
            Assert.Contains(
                view.LayerLineTypeSelector.Items.OfType<ComboBoxItem>(),
                item => item.Tag is string name && name == "FIRST_LINETYPE");

            view.Canvas.Load(new CadDocumentSession(secondDocument));

            Assert.DoesNotContain(
                view.SelectionLayerSelector.Items.OfType<ComboBoxItem>(),
                item => item.Tag is string name && name == "FIRST_LAYER");
            Assert.Contains(
                view.SelectionLayerSelector.Items.OfType<ComboBoxItem>(),
                item => item.Tag is string name && name == "SECOND_LAYER");
            Assert.DoesNotContain(
                view.LayerStateSelector.Items.OfType<ComboBoxItem>(),
                item => item.Tag is string name && name == "FIRST_LAYER");
            Assert.Contains(
                view.LayerStateSelector.Items.OfType<ComboBoxItem>(),
                item => item.Tag is string name && name == "SECOND_LAYER");
            Assert.DoesNotContain(
                view.LayerLineTypeSelector.Items.OfType<ComboBoxItem>(),
                item => item.Tag is string name && name == "FIRST_LINETYPE");
            Assert.Contains(
                view.LayerLineTypeSelector.Items.OfType<ComboBoxItem>(),
                item => item.Tag is string name && name == "SECOND_LINETYPE");
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewAcceptsCompleteSupportedColorSyntaxAndStandardLineweights()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(-2, 0, 0), new XYZ(2, 0, 0))
        {
            LineWeight = LineWeightType.ByDIPs,
        };
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_280, 850));
            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 1_280, 624));
            Click(
                view.Canvas,
                view.Canvas.CurrentViewport.WorldToScreen(CadPoint3D.Zero));
            Assert.Equal(
                "ByDIPs (unsupported)",
                Assert.IsType<ComboBoxItem>(
                    view.SelectionLineWeightSelector.SelectedItem).Text);
            Button setColor = FindButton(view, "Set color");
            var cases = new (string Input, ACadSharp.Color Expected)[]
            {
                ("ByLayer", ACadSharp.Color.ByLayer),
                ("byblock", ACadSharp.Color.ByBlock),
                ("ACI 255", new ACadSharp.Color(255)),
                ("1", ACadSharp.Color.Red),
                ("#000000", new ACadSharp.Color(0, 0, 0)),
                ("#FFFFFF", new ACadSharp.Color(255, 255, 255)),
            };
            foreach ((string input, ACadSharp.Color expected) in cases)
            {
                view.SelectionColorInput.Text = input;
                Assert.True(setColor.IsEnabled);
                PressEnter(setColor);
                Assert.Equal(expected, line.Color);
            }

            Assert.Equal((ulong)cases.Length, session.ContentGeneration);
            foreach (string invalid in new[]
            {
                string.Empty,
                "*VARIES*",
                "ByEntity",
                "ACI 0",
                "ACI 256",
                "#12345",
                "#GG0000",
            })
            {
                view.SelectionColorInput.Text = invalid;
                Assert.False(setColor.IsEnabled);
            }

            ACadSharp.LineWeightType[] choices =
                view.SelectionLineWeightSelector.Items
                    .OfType<ComboBoxItem>()
                    .Where(item => item.Tag is ACadSharp.LineWeightType)
                    .Select(item => (ACadSharp.LineWeightType)item.Tag!)
                    .ToArray();
            ACadSharp.LineWeightType[] expectedChoices =
                Enum.GetValues<ACadSharp.LineWeightType>()
                    .Where(value => value != ACadSharp.LineWeightType.ByDIPs)
                    .ToArray();
            Assert.Equal(expectedChoices.Length, choices.Length);
            Assert.Equal(choices.Length, choices.Distinct().Count());
            Assert.All(expectedChoices, value => Assert.Contains(value, choices));
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task SelectionGeneralPropertyEditsSurviveDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        var layer = new Layer("PROPERTY_LAYER");
        var lineType = new LineType("PROPERTY_LTYPE");
        document.Layers.Add(layer);
        document.LineTypes.Add(lineType);
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadSetEntityColorCommand(
            [line.Handle],
            new ACadSharp.Color(12, 34, 56)));
        history.Execute(new CadSetEntityLineWeightCommand(
            [line.Handle],
            LineWeightType.W100));
        history.Execute(new CadSetEntityLayerCommand(
            [line.Handle],
            layer.Name));
        history.Execute(new CadSetEntityLineTypeCommand(
            [line.Handle],
            lineType.Name));
        history.Execute(new CadSetEntityLineTypeScaleCommand(
            [line.Handle],
            2.5));
        history.Execute(new CadSetEntityTransparencyCommand(
            [line.Handle],
            new Transparency(30)));
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            session,
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"selection-properties.{format.ToString().ToLowerInvariant()}");
        (ACadSharp.Color Color,
            LineWeightType LineWeight,
            string Layer,
            string LineType,
            double LineTypeScale,
            short Transparency) restored =
            loaded.Session.Read(loadedDocument =>
            {
                Entity entity = loadedDocument.Entities.Single();
                return (
                    entity.Color,
                    entity.LineWeight,
                    entity.Layer.Name,
                    entity.LineType.Name,
                    entity.LineTypeScale,
                    entity.Transparency.Value);
            });

        Assert.Equal(new ACadSharp.Color(12, 34, 56), restored.Color);
        Assert.Equal(LineWeightType.W100, restored.LineWeight);
        Assert.Equal("PROPERTY_LAYER", restored.Layer);
        Assert.Equal("PROPERTY_LTYPE", restored.LineType);
        Assert.Equal(2.5, restored.LineTypeScale);
        Assert.Equal((short)30, restored.Transparency);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task MultiObjectCopySurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        var first = new Line(new XYZ(1, 2, 3), new XYZ(4, 5, 6));
        var second = new Line(new XYZ(-3, -2, 1), new XYZ(-1, 4, 1));
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadDuplicateModelSpaceEntitiesCommand(
            [first.Handle, second.Handle],
            new CadPoint3D(10, -2, 4),
            "Copy selection"));
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            session,
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"multi-copy.{format.ToString().ToLowerInvariant()}");
        XYZ[] starts = loaded.Session.Read(loadedDocument =>
            loadedDocument.Entities
                .OfType<Line>()
                .Select(static line => line.StartPoint)
                .ToArray());

        Assert.Equal(4, starts.Length);
        Assert.Contains(new XYZ(1, 2, 3), starts);
        Assert.Contains(new XYZ(-3, -2, 1), starts);
        Assert.Contains(new XYZ(11, 0, 7), starts);
        Assert.Contains(new XYZ(7, -4, 5), starts);
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
    public void SharedViewCopyButtonsValidateAndExecuteOneSelectionSetEdit()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(-2, 0, 0), new XYZ(2, 0, 0));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var view = new CadSampleView();
        try
        {
            Button copyPositiveX = FindButton(view, "Copy +X");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");
            TextBox moveStep = DescendantsAndSelf(view)
                .OfType<TextBox>()
                .Single(textBox => textBox.Text == "1");
            Assert.False(copyPositiveX.IsEnabled);

            view.Canvas.Load(session);
            view.Canvas.Arrange(new Rect(0, 0, 800, 600));
            Click(
                view.Canvas,
                view.Canvas.CurrentViewport.WorldToScreen(CadPoint3D.Zero));

            ulong sourceHandle = line.Handle;
            Assert.True(copyPositiveX.IsEnabled);
            moveStep.Text = "invalid";
            PressEnter(copyPositiveX);
            Assert.Single(document.Entities);
            Assert.Equal(0UL, session.ContentGeneration);

            moveStep.Text = "3";
            PressEnter(copyPositiveX);

            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(2, document.Entities.Count);
            Assert.Equal([sourceHandle], view.Canvas.SelectedHandles.ToArray());
            Assert.Contains(
                document.Entities.OfType<Line>(),
                entity => entity.StartPoint == new XYZ(1, 0, 0) &&
                    entity.EndPoint == new XYZ(5, 0, 0));
            Assert.True(undo.IsEnabled);

            PressEnter(undo);
            Assert.Single(document.Entities);
            Assert.Equal([sourceHandle], view.Canvas.SelectedHandles.ToArray());
            Assert.True(redo.IsEnabled);

            PressEnter(redo);
            Assert.Equal(2, document.Entities.Count);
            Assert.Equal([sourceHandle], view.Canvas.SelectedHandles.ToArray());
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

    private static ComboBoxItem FindNamedPropertyChoice(
        ComboBox selector,
        string name) =>
        selector.Items
            .OfType<ComboBoxItem>()
            .Single(item => item.Tag is string value &&
                value.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string SelectedPropertyText(ComboBox selector) =>
        Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text;

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

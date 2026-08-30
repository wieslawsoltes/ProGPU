using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Objects;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using Xunit;
using ACadLayout = ACadSharp.Objects.Layout;

namespace ProGPU.CAD.Tests;

[CollectionDefinition("CAD sample UI", DisableParallelization = true)]
public sealed class CadSampleUiCollectionDefinition
{
}

[Collection("CAD sample UI")]
public sealed class CadSamplePrintPreviewTests
{
    [Fact]
    public void AciSevenAdaptsToPaperButExplicitTrueWhiteDoesNot()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        document.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 0))
        {
            Color = new ACadSharp.Color(
                byte.MaxValue,
                byte.MaxValue,
                byte.MaxValue),
        });
        var session = new CadDocumentSession(document);
        var compiler = new CadSnapshotCompiler();

        CadDocumentSnapshot model = compiler.Compile(session);
        CadDocumentSnapshot paper = compiler.Compile(
            session,
            new CadSnapshotOptions
            {
                DrawingBackgroundColor = new CadColor32(
                    byte.MaxValue,
                    byte.MaxValue,
                    byte.MaxValue),
            });

        AssertStyleColor(model, 0, byte.MaxValue, byte.MaxValue, byte.MaxValue);
        AssertStyleColor(model, 1, byte.MaxValue, byte.MaxValue, byte.MaxValue);
        AssertStyleColor(paper, 0, 0, 0, 0);
        AssertStyleColor(paper, 1, byte.MaxValue, byte.MaxValue, byte.MaxValue);
    }

    [Fact]
    public void CanvasCompilesPlotOrderedWhitePaperPlanWithoutEditingDocument()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add ordered preview lines", document =>
        {
            document.Header.EntitySortingFlags = ObjectSortingFlags.Disabled;
            var first = new Line(new XYZ(10, 0, 0), new XYZ(11, 0, 0));
            var second = new Line(new XYZ(20, 0, 0), new XYZ(21, 0, 0));
            var third = new Line(new XYZ(30, 0, 0), new XYZ(31, 0, 0));
            document.Entities.Add(first);
            document.Entities.Add(second);
            document.Entities.Add(third);
            SortEntitiesTable order = document.ModelSpace.CreateSortEntitiesTable();
            order.Add(first, 30);
            order.Add(second, 10);
            order.Add(third, 20);
        });
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);

            Assert.False(canvas.CurrentSnapshot!.IsPlotOrderCompatible);
            using CadPrintPlan plan = canvas.CreateA4PrintPlan(96.0f);

            Assert.Equal(1UL, session.ContentGeneration);
            Assert.Equal(session.ContentGeneration, plan.ContentGeneration);
            Assert.Equal(96.0f, plan.OutputDpi);
            Assert.Equal(new CadPrintPixelSize(794, 1123), plan.PageSizePixels);
            Assert.Equal(3, plan.SceneStatistics.RecordedEntityCount);
            using GpuPicture page = plan.CreatePagePicture();
            GpuPicture content = page.GetCommand(1).Picture!;
            RenderCommand[] lines = content.Commands.ToArray();
            Assert.Equal(3, lines.Length);
            Assert.Equal(
                new[] { -10.5f, -0.5f, 9.5f },
                lines.Select(command => command.Position.X));
            Assert.All(lines, command =>
            {
                var brush = Assert.IsType<SolidColorBrush>(command.Pen!.Brush);
                Assert.Equal(new Vector4(0, 0, 0, 1), brush.Color);
            });
            Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
                content,
                96U,
                plan.ContentGeneration,
                out NativeCompiledPicture? native,
                out NativePictureCompileFailure failure),
                failure.ToString());
            Assert.NotNull(native);
            Assert.Equal(3, native.SourceCommandCount);
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void PreviewRetainsOneToOnePageAndSurvivesSourcePlanDisposal()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 10, 0)));
        var canvas = new CadSampleCanvas();
        var preview = new CadPrintPreviewCanvas();
        try
        {
            canvas.Load(new CadDocumentSession(document));
            preview.Arrange(new Rect(0, 0, 800, 600));
            float dpi = CadPrintPreviewCanvas.CalculateFitOutputDpi(preview.Size);
            CadPrintPlan plan = canvas.CreateA4PrintPlan(dpi);
            CadPrintPixelSize pageSize = plan.PageSizePixels;
            CadPrintPixelRect printableArea = plan.PrintableAreaPixels;

            preview.Load(plan);
            plan.Dispose();

            Assert.True(preview.HasPage);
            Assert.Equal(pageSize, preview.PageSizePixels);
            Assert.Equal(printableArea, preview.PrintableAreaPixels);
            Assert.Equal(dpi, preview.OutputDpi);
            Assert.True(pageSize.Width <= 752);
            Assert.True(pageSize.Height <= 552);
            Rect page = preview.PageViewportRect;
            Assert.Equal(pageSize.Width, page.Width);
            Assert.Equal(pageSize.Height, page.Height);
            var context = new DrawingContext();
            preview.OnRender(context);
            RenderCommand paper = context.Commands.Single(command =>
                command.Type == RenderCommandType.DrawRect &&
                command.Rect == page);
            var paperBrush = Assert.IsType<SolidColorBrush>(paper.Brush);
            Assert.Equal(new Vector4(1, 1, 1, 1), paperBrush.Color);
            RenderCommand replay = context.Commands.Single(command =>
                command.Type == RenderCommandType.DrawPicture);
            Assert.Equal(page.X, replay.Transform.M41);
            Assert.Equal(page.Y, replay.Transform.M42);
            using GpuPicture ownershipProbe = replay.Picture!.Clone();
            Assert.Equal(3, ownershipProbe.CommandCount);

            preview.Clear();

            Assert.False(preview.HasPage);
            Assert.Equal(0.0f, preview.OutputDpi);
            Assert.Null(preview.SourcePageSetupName);
        }
        finally
        {
            preview.FireUnloaded();
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewTogglesRetainedPreviewAndEscapeReturnsToPlan()
    {
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_000, 800));
            Button previewButton = FindButton(view, "Print preview");
            Button openButton = FindButton(view, "Open DXF/DWG");
            Button fitButton = FindButton(view, "Fit");
            ulong generation = view.Canvas.CurrentSession!.ContentGeneration;
            ComboBox selector = view.PageSetupSelector;

            Assert.Equal(4, selector.Items.Count);
            Assert.Contains(
                "Layout: Model (ProGPU A3 landscape)",
                Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text,
                StringComparison.Ordinal);

            Assert.True(previewButton.IsEnabled);
            PressEnter(previewButton);

            Assert.Equal(Visibility.Collapsed, view.Canvas.Visibility);
            Assert.Equal(Visibility.Visible, view.PrintPreview.Visibility);
            Assert.True(view.PrintPreview.HasPage);
            Assert.Equal(generation, view.PrintPreview.ContentGeneration);
            Assert.Equal("Model", view.PrintPreview.SourcePageSetupName);
            Assert.True(
                view.PrintPreview.PageSizePixels.Width >
                view.PrintPreview.PageSizePixels.Height);
            Assert.Equal("Plan view", ((TextBlock)previewButton.Content!).Text);
            Assert.False(openButton.IsEnabled);
            Assert.False(fitButton.IsEnabled);
            Assert.True(selector.IsEnabled);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Layout Model print preview",
                    StringComparison.Ordinal));
            Assert.Equal(generation, view.Canvas.CurrentSession.ContentGeneration);

            selector.SelectedItem = selector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text.Contains(
                    "unsupported CADPAGE119",
                    StringComparison.Ordinal));

            Assert.Equal(Visibility.Visible, view.Canvas.Visibility);
            Assert.False(view.PrintPreview.HasPage);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains("CADPAGE119", StringComparison.Ordinal));

            selector.SelectedItem = selector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text.StartsWith(
                    "Named: A4 portrait",
                    StringComparison.Ordinal));
            PressEnter(previewButton);

            Assert.True(view.PrintPreview.HasPage);
            Assert.Equal("A4 portrait", view.PrintPreview.SourcePageSetupName);
            Assert.True(
                view.PrintPreview.PageSizePixels.Height >
                view.PrintPreview.PageSizePixels.Width);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Named A4 portrait print preview",
                    StringComparison.Ordinal));
            var escape = new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Escape,
            };

            view.OnKeyDown(escape);

            Assert.True(escape.Handled);
            Assert.Equal(Visibility.Visible, view.Canvas.Visibility);
            Assert.Equal(Visibility.Collapsed, view.PrintPreview.Visibility);
            Assert.False(view.PrintPreview.HasPage);
            Assert.Equal("Print preview", ((TextBlock)previewButton.Content!).Text);
            Assert.True(openButton.IsEnabled);
            Assert.True(fitButton.IsEnabled);
            Assert.Equal(generation, view.Canvas.CurrentSession.ContentGeneration);

            selector.SelectedItem = selector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text == "A4 model extents (fallback)");
            PressEnter(previewButton);

            Assert.True(view.PrintPreview.HasPage);
            Assert.Null(view.PrintPreview.SourcePageSetupName);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "A4 model-extents fallback print preview",
                    StringComparison.Ordinal));
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewExposesPdfAndPngOutputForTheSelectedPageSetup()
    {
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_000, 800));
            Button pdf = FindButton(view, "Export PDF");
            Button png = FindButton(view, "Export PNG");

            Assert.True(pdf.IsEnabled);
            Assert.True(png.IsEnabled);

            view.PageSetupSelector.SelectedItem = view.PageSetupSelector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text.Contains(
                    "unsupported CADPAGE119",
                    StringComparison.Ordinal));

            Assert.False(pdf.IsEnabled);
            Assert.False(png.IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void CanvasCompilesGenerationMatchedPageSetupAndRejectsStaleSelection()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 10, 0))
        {
            Transparency = new Transparency(30),
        });
        ConfigureSupported(
            document.Layouts[ACadLayout.ModelLayoutName],
            paperWidth: 120,
            paperHeight: 80);
        var session = new CadDocumentSession(document);
        session.Edit("initialize page-setup generation", static _ => { });
        var canvas = new CadSampleCanvas();
        try
        {
            canvas.Load(session);
            CadPageSetupSnapshot setup = canvas.CreatePageSetupCatalog()
                .FindLayout(ACadLayout.ModelLayoutName)!;

            using CadPrintPlan plan = canvas.CreatePageSetupPrintPlan(setup, 254);

            Assert.Equal(ACadLayout.ModelLayoutName, plan.SourcePageSetupName);
            Assert.Equal(
                CadPrintTransparencyMode.PreserveRetainedAlpha,
                plan.TransparencyMode);
            Assert.Equal(new CadPrintPixelSize(1200, 800), plan.PageSizePixels);
            Assert.Equal(session.ContentGeneration, plan.ContentGeneration);
            using GpuPicture page = plan.CreatePagePicture();
            GpuPicture content = page.GetCommand(1).Picture!;
            var lineBrush = Assert.IsType<SolidColorBrush>(
                content.GetCommand(0).Pen!.Brush);
            Assert.Equal(new Vector4(0, 0, 0, 178.0f / 255.0f), lineBrush.Color);
            Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
                content,
                254U,
                plan.ContentGeneration,
                out NativeCompiledPicture? native,
                out NativePictureCompileFailure failure),
                failure.ToString());
            Assert.NotNull(native);

            session.Edit("advance page-setup generation", static _ => { });

            Assert.Throws<InvalidOperationException>(() =>
                canvas.CreatePageSetupPrintPlan(setup, 254).Dispose());
        }
        finally
        {
            canvas.FireUnloaded();
        }
    }

    [Fact]
    public void UnsupportedPageSetupIsVisibleAndNeverSilentlyFallsBack()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureSupported(model, paperWidth: 210, paperHeight: 297);
        model.ShadePlotMode = ShadePlotMode.Rendered;
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_000, 800));
            view.Canvas.Load(new CadDocumentSession(document));
            ComboBox selector = view.PageSetupSelector;
            ComboBoxItem unsupported = selector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text.Contains(
                    "unsupported CADPAGE110",
                    StringComparison.Ordinal));

            selector.SelectedItem = unsupported;
            PressEnter(FindButton(view, "Print preview"));

            Assert.Equal(Visibility.Visible, view.Canvas.Visibility);
            Assert.Equal(Visibility.Collapsed, view.PrintPreview.Visibility);
            Assert.False(view.PrintPreview.HasPage);
            Assert.Null(view.PrintPreview.SourcePageSetupName);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains("CADPAGE110", StringComparison.Ordinal));
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void NamedPageSetupSelectionSurvivesGenerationReplacement()
    {
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_000, 800));
            ComboBox selector = view.PageSetupSelector;
            ComboBoxItem named = selector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text.StartsWith(
                    "Named: A4 portrait",
                    StringComparison.Ordinal));
            selector.SelectedItem = named;
            CadDocumentSession session = view.Canvas.CurrentSession!;

            session.Edit("advance representative generation", static _ => { });
            view.Canvas.Load(session);

            var selected = Assert.IsType<ComboBoxItem>(selector.SelectedItem);
            Assert.StartsWith(
                "Named: A4 portrait",
                selected.Text,
                StringComparison.Ordinal);
            PressEnter(FindButton(view, "Print preview"));
            Assert.Equal(session.ContentGeneration, view.PrintPreview.ContentGeneration);
            Assert.Equal("A4 portrait", view.PrintPreview.SourcePageSetupName);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewAppliesNamedPageSetupToModelWithUndoRedo()
    {
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_000, 800));
            ComboBox selector = view.PageSetupSelector;
            selector.SelectedItem = selector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text.StartsWith(
                    "Named: A4 portrait",
                    StringComparison.Ordinal));
            Button apply = FindButton(view, "Apply to Model");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");
            ulong originalGeneration = view.Canvas.CurrentSession!.ContentGeneration;

            Assert.True(apply.IsEnabled);
            PressEnter(apply);

            Assert.Equal(
                checked(originalGeneration + 1),
                view.Canvas.CurrentSession.ContentGeneration);
            Assert.Equal(1, view.Canvas.UndoCount);
            Assert.Equal(0, view.Canvas.RedoCount);
            Assert.Equal(
                "A4 portrait",
                view.Canvas.CreatePageSetupCatalog()
                    .FindLayout(ACadLayout.ModelLayoutName)!
                    .PageSetupName);
            Assert.StartsWith(
                "Named: A4 portrait",
                Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text,
                StringComparison.Ordinal);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Applied named page setup 'A4 portrait' to Model",
                    StringComparison.Ordinal));

            PressEnter(undo);

            Assert.Equal(
                "ProGPU A3 landscape",
                view.Canvas.CreatePageSetupCatalog()
                    .FindLayout(ACadLayout.ModelLayoutName)!
                    .PageSetupName);
            Assert.Equal(0, view.Canvas.UndoCount);
            Assert.Equal(1, view.Canvas.RedoCount);
            Assert.StartsWith(
                "Named: A4 portrait",
                Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text,
                StringComparison.Ordinal);

            PressEnter(redo);

            Assert.Equal(
                "A4 portrait",
                view.Canvas.CreatePageSetupCatalog()
                    .FindLayout(ACadLayout.ModelLayoutName)!
                    .PageSetupName);
            Assert.Equal(1, view.Canvas.UndoCount);
            Assert.Equal(0, view.Canvas.RedoCount);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewCreatesNamedPageSetupFromModelWithUndoRedo()
    {
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_000, 800));
            ComboBox selector = view.PageSetupSelector;
            TextBox nameInput = view.PageSetupNameInput;
            Button create = FindButton(view, "Save named setup");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");
            CadPageSetupSnapshot model = view.Canvas.CreatePageSetupCatalog()
                .FindLayout(ACadLayout.ModelLayoutName)!;
            int originalCount = selector.Items.Count;
            ulong originalGeneration = view.Canvas.CurrentSession!.ContentGeneration;
            nameInput.Text = "Archived Model output";

            Assert.True(create.IsEnabled);
            PressEnter(create);

            Assert.Equal(originalCount + 1, selector.Items.Count);
            Assert.Equal(
                checked(originalGeneration + 1),
                view.Canvas.CurrentSession.ContentGeneration);
            CadPageSetupSnapshot created = view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("Archived Model output")!;
            Assert.Equal(model.PaperWidthMillimeters, created.PaperWidthMillimeters);
            Assert.Equal(model.PaperHeightMillimeters, created.PaperHeightMillimeters);
            Assert.Equal(model.Rotation, created.Rotation);
            Assert.Equal("Archived Model output", created.PageSetupName);
            Assert.StartsWith(
                "Named: Archived Model output",
                Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text,
                StringComparison.Ordinal);
            Assert.False(create.IsEnabled);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Saved Model plot settings as named page setup 'Archived Model output'",
                    StringComparison.Ordinal));

            PressEnter(undo);

            Assert.Equal(originalCount, selector.Items.Count);
            Assert.Null(view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("Archived Model output"));
            Assert.StartsWith(
                "Layout: Model",
                Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text,
                StringComparison.Ordinal);
            Assert.True(create.IsEnabled);

            PressEnter(redo);

            Assert.Equal(originalCount + 1, selector.Items.Count);
            Assert.NotNull(view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("Archived Model output"));
            Assert.StartsWith(
                "Layout: Model",
                Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text,
                StringComparison.Ordinal);
            Assert.False(create.IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewUpdatesSelectedNamedPageSetupFromModelWithUndoRedo()
    {
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_000, 800));
            ComboBox selector = view.PageSetupSelector;
            selector.SelectedItem = selector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text.StartsWith(
                    "Named: A4 portrait",
                    StringComparison.Ordinal));
            Button update = FindButton(view, "Update selected");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");
            CadPageSetupSnapshot model = view.Canvas.CreatePageSetupCatalog()
                .FindLayout(ACadLayout.ModelLayoutName)!;
            CadPageSetupSnapshot original = view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("A4 portrait")!;
            ulong originalGeneration = view.Canvas.CurrentSession!.ContentGeneration;

            Assert.True(update.IsEnabled);
            Assert.NotEqual(model.PaperWidthMillimeters, original.PaperWidthMillimeters);
            PressEnter(update);

            Assert.Equal(
                checked(originalGeneration + 1),
                view.Canvas.CurrentSession.ContentGeneration);
            CadPageSetupSnapshot updated = view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("A4 portrait")!;
            Assert.Equal("A4 portrait", updated.Name);
            Assert.Equal("A4 portrait", updated.PageSetupName);
            Assert.Equal(model.PaperWidthMillimeters, updated.PaperWidthMillimeters);
            Assert.Equal(model.PaperHeightMillimeters, updated.PaperHeightMillimeters);
            Assert.Equal(model.Rotation, updated.Rotation);
            Assert.StartsWith(
                "Named: A4 portrait",
                Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text,
                StringComparison.Ordinal);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Updated named page setup 'A4 portrait' from Model",
                    StringComparison.Ordinal));

            PressEnter(undo);

            CadPageSetupSnapshot undone = view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("A4 portrait")!;
            Assert.Equal(original.PaperWidthMillimeters, undone.PaperWidthMillimeters);
            Assert.Equal(original.PaperHeightMillimeters, undone.PaperHeightMillimeters);
            Assert.Equal(original.Rotation, undone.Rotation);
            Assert.StartsWith(
                "Named: A4 portrait",
                Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text,
                StringComparison.Ordinal);

            PressEnter(redo);

            CadPageSetupSnapshot redone = view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("A4 portrait")!;
            Assert.Equal(model.PaperWidthMillimeters, redone.PaperWidthMillimeters);
            Assert.Equal(model.PaperHeightMillimeters, redone.PaperHeightMillimeters);
            Assert.Equal(model.Rotation, redone.Rotation);
            Assert.StartsWith(
                "Named: A4 portrait",
                Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text,
                StringComparison.Ordinal);

            selector.SelectedItem = selector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text.StartsWith(
                    "Layout: Model",
                    StringComparison.Ordinal));
            Assert.False(update.IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewEditsSelectedPageSetupFieldsWithUndoRedo()
    {
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_000, 800));
            ComboBox selector = view.PageSetupSelector;
            selector.SelectedItem = selector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text.StartsWith(
                    "Layout: Model",
                    StringComparison.Ordinal));
            CadPageSetupSnapshot original = view.Canvas.CreatePageSetupCatalog()
                .FindLayout(ACadLayout.ModelLayoutName)!;
            Button apply = FindButton(view, "Apply fields");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");
            ulong originalGeneration = view.Canvas.CurrentSession!.ContentGeneration;

            Assert.Equal(
                original.PaperWidthMillimeters.ToString(
                    "0.###",
                    System.Globalization.CultureInfo.InvariantCulture),
                view.PageSetupPaperWidthInput.Text);
            Assert.False(apply.IsEnabled);

            view.PageSetupPaperWidthInput.Text = "500";
            view.PageSetupPaperHeightInput.Text = "300";
            view.PageSetupRotationSelector.SelectedItem =
                view.PageSetupRotationSelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Tag is
                        CadPageRotation.CounterClockwise90);
            view.PageSetupPlotAreaSelector.SelectedItem =
                view.PageSetupPlotAreaSelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Tag is CadPlotAreaKind.Limits);
            view.PageSetupCenterCheckBox.IsChecked = !original.CenterPlot;
            view.PageSetupLineweightsCheckBox.IsChecked =
                !original.PrintLineweights;

            Assert.True(apply.IsEnabled);
            PressEnter(apply);

            Assert.Equal(
                checked(originalGeneration + 1),
                view.Canvas.CurrentSession.ContentGeneration);
            CadPageSetupSnapshot edited = view.Canvas.CreatePageSetupCatalog()
                .FindLayout(ACadLayout.ModelLayoutName)!;
            Assert.Equal(500, edited.PaperWidthMillimeters);
            Assert.Equal(300, edited.PaperHeightMillimeters);
            Assert.Equal(
                CadPageRotation.CounterClockwise90,
                edited.Rotation);
            Assert.Equal(CadPlotAreaKind.Limits, edited.PlotArea);
            Assert.Equal(!original.CenterPlot, edited.CenterPlot);
            Assert.Equal(!original.PrintLineweights, edited.PrintLineweights);
            Assert.DoesNotContain(
                "unsupported CADPAGE112",
                view.PageSetupSelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Text.StartsWith(
                        "Layout: Model",
                        StringComparison.Ordinal))
                    .Text,
                StringComparison.Ordinal);
            Assert.False(apply.IsEnabled);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Edited layout 'Model' fields as one edit",
                    StringComparison.Ordinal));

            PressEnter(undo);

            CadPageSetupSnapshot undone = view.Canvas.CreatePageSetupCatalog()
                .FindLayout(ACadLayout.ModelLayoutName)!;
            Assert.Equal(original.PaperWidthMillimeters, undone.PaperWidthMillimeters);
            Assert.Equal(original.PaperHeightMillimeters, undone.PaperHeightMillimeters);
            Assert.Equal(original.Rotation, undone.Rotation);
            Assert.Equal(original.PlotArea, undone.PlotArea);
            Assert.Equal(original.CenterPlot, undone.CenterPlot);
            Assert.Equal(original.PrintLineweights, undone.PrintLineweights);

            PressEnter(redo);

            CadPageSetupSnapshot redone = view.Canvas.CreatePageSetupCatalog()
                .FindLayout(ACadLayout.ModelLayoutName)!;
            Assert.Equal(500, redone.PaperWidthMillimeters);
            Assert.Equal(300, redone.PaperHeightMillimeters);
            Assert.Equal(CadPlotAreaKind.Limits, redone.PlotArea);
            Assert.DoesNotContain(
                "unsupported CADPAGE112",
                view.PageSetupSelector.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Text.StartsWith(
                        "Layout: Model",
                        StringComparison.Ordinal))
                    .Text,
                StringComparison.Ordinal);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewDeletesUnassignedNamedPageSetupWithUndoRedo()
    {
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_000, 800));
            ComboBox selector = view.PageSetupSelector;
            ComboBoxItem FindNamed() => selector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text.StartsWith(
                    "Named: A4 portrait",
                    StringComparison.Ordinal));
            selector.SelectedItem = FindNamed();
            Button delete = FindButton(view, "Delete setup");
            Button apply = FindButton(view, "Apply to Model");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");
            int originalCount = selector.Items.Count;
            ulong originalGeneration = view.Canvas.CurrentSession!.ContentGeneration;

            Assert.True(delete.IsEnabled);
            PressEnter(delete);

            Assert.Equal(originalCount - 1, selector.Items.Count);
            Assert.Equal(
                checked(originalGeneration + 1),
                view.Canvas.CurrentSession.ContentGeneration);
            Assert.Null(view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("A4 portrait"));
            Assert.StartsWith(
                "Layout: Model",
                Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text,
                StringComparison.Ordinal);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Deleted named page setup 'A4 portrait'",
                    StringComparison.Ordinal));

            PressEnter(undo);

            Assert.Equal(originalCount, selector.Items.Count);
            Assert.NotNull(view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("A4 portrait"));
            selector.SelectedItem = FindNamed();
            Assert.True(delete.IsEnabled);

            PressEnter(redo);

            Assert.Equal(originalCount - 1, selector.Items.Count);
            Assert.Null(view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("A4 portrait"));

            PressEnter(undo);
            selector.SelectedItem = FindNamed();
            Assert.True(delete.IsEnabled);
            PressEnter(apply);
            Assert.False(delete.IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewRenamesAssignedNamedPageSetupWithUndoRedo()
    {
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_000, 800));
            ComboBox selector = view.PageSetupSelector;
            ComboBoxItem FindNamed(string name) => selector.Items
                .OfType<ComboBoxItem>()
                .Single(item => item.Text.StartsWith(
                    $"Named: {name}",
                    StringComparison.Ordinal));
            selector.SelectedItem = FindNamed("A4 portrait");
            TextBox nameInput = view.PageSetupNameInput;
            Button apply = FindButton(view, "Apply to Model");
            Button rename = FindButton(view, "Rename selected");
            Button delete = FindButton(view, "Delete setup");
            Button undo = FindButton(view, "Undo");
            Button redo = FindButton(view, "Redo");
            ulong originalGeneration = view.Canvas.CurrentSession!.ContentGeneration;

            PressEnter(apply);
            nameInput.Text = "Published A4 output";
            Assert.True(rename.IsEnabled);
            Assert.False(delete.IsEnabled);
            PressEnter(rename);

            Assert.Equal(
                checked(originalGeneration + 2),
                view.Canvas.CurrentSession.ContentGeneration);
            Assert.Null(view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("A4 portrait"));
            Assert.NotNull(view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("Published A4 output"));
            Assert.Equal(
                "Published A4 output",
                view.Canvas.CreatePageSetupCatalog()
                    .FindLayout(ACadLayout.ModelLayoutName)!
                    .PageSetupName);
            Assert.StartsWith(
                "Named: Published A4 output",
                Assert.IsType<ComboBoxItem>(selector.SelectedItem).Text,
                StringComparison.Ordinal);
            Assert.False(rename.IsEnabled);
            Assert.False(delete.IsEnabled);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Renamed page setup 'A4 portrait' to 'Published A4 output'",
                    StringComparison.Ordinal));

            PressEnter(undo);

            Assert.NotNull(view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("A4 portrait"));
            Assert.Null(view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("Published A4 output"));
            Assert.Equal(
                "A4 portrait",
                view.Canvas.CreatePageSetupCatalog()
                    .FindLayout(ACadLayout.ModelLayoutName)!
                    .PageSetupName);
            selector.SelectedItem = FindNamed("A4 portrait");
            Assert.True(rename.IsEnabled);

            PressEnter(redo);

            Assert.NotNull(view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("Published A4 output"));
            Assert.Equal(
                "Published A4 output",
                view.Canvas.CreatePageSetupCatalog()
                    .FindLayout(ACadLayout.ModelLayoutName)!
                    .PageSetupName);
            selector.SelectedItem = FindNamed("Published A4 output");
            Assert.False(rename.IsEnabled);
            Assert.False(delete.IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    private static Button FindButton(Visual root, string label) =>
        DescendantsAndSelf(root)
            .OfType<Button>()
            .Single(button =>
                button.Content is TextBlock text && text.Text == label);

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

    private static void AssertStyleColor(
        CadDocumentSnapshot snapshot,
        int entityIndex,
        byte red,
        byte green,
        byte blue)
    {
        CadEntityHeader entity = snapshot.Entities.Span[entityIndex];
        CadStrokeStyle style = snapshot.Styles.Span[entity.StyleIndex];
        Assert.Equal(red, style.Red);
        Assert.Equal(green, style.Green);
        Assert.Equal(blue, style.Blue);
    }

    private static void ConfigureSupported(
        PlotSettings setup,
        double paperWidth,
        double paperHeight)
    {
        setup.Flags =
            (setup.Flags & PlotFlags.ModelType) |
            PlotFlags.PrintLineweights |
            PlotFlags.PlotCentered |
            PlotFlags.UseStandardScale;
        setup.PaperWidth = paperWidth;
        setup.PaperHeight = paperHeight;
        setup.UnprintableMargin = new PaperMargin(5, 5, 5, 5);
        setup.PaperUnits = PlotPaperUnits.Millimeters;
        setup.PaperRotation = PlotRotation.NoRotation;
        setup.PlotType = PlotType.DrawingExtents;
        setup.ScaledFit = ScaledType.ScaledToFit;
        setup.NumeratorScale = 1;
        setup.DenominatorScale = 1;
        setup.ShadePlotMode = ShadePlotMode.Wireframe;
        setup.StyleSheet = string.Empty;
    }
}

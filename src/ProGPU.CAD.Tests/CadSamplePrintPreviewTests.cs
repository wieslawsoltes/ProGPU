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

            Assert.True(previewButton.IsEnabled);
            PressEnter(previewButton);

            Assert.Equal(Visibility.Collapsed, view.Canvas.Visibility);
            Assert.Equal(Visibility.Visible, view.PrintPreview.Visibility);
            Assert.True(view.PrintPreview.HasPage);
            Assert.Equal(generation, view.PrintPreview.ContentGeneration);
            Assert.Equal("Plan view", ((TextBlock)previewButton.Content!).Text);
            Assert.False(openButton.IsEnabled);
            Assert.False(fitButton.IsEnabled);
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "A4 model-extents print preview",
                    StringComparison.Ordinal));
            Assert.Equal(generation, view.Canvas.CurrentSession.ContentGeneration);
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
}

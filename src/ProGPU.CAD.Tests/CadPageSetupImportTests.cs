using ACadSharp;
using ACadSharp.Objects;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using Xunit;
using ACadLayout = ACadSharp.Objects.Layout;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadPageSetupImportTests
{
    [Fact]
    public void RejectConflictPreflightsWholeBatchWithoutMutation()
    {
        CadDocumentSession source = CreateSource(
            ("Alpha", 210, PlotRotation.NoRotation),
            ("Shared", 297, PlotRotation.Degrees90));
        var targetDocument = new CadDocument();
        CadDictionary targetDictionary = GetPageSetups(targetDocument);
        var existing = new PlotSettings("SHARED");
        Configure(existing, 500, PlotRotation.Degrees180);
        targetDictionary.Add(existing);
        ulong handle = existing.Handle;
        var target = new CadDocumentSession(targetDocument);
        var history = new CadDocumentHistory(target);
        CadImportNamedPageSetupsCommand command =
            CadImportNamedPageSetupsCommand.CaptureAll(source);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => history.Execute(command));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.False(targetDictionary.ContainsKey("Alpha"));
        Assert.Same(existing, targetDictionary.GetEntry<PlotSettings>("Shared"));
        Assert.Equal(handle, existing.Handle);
        Assert.Equal(500, existing.PaperWidth);
        Assert.Equal(0UL, target.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void ReplaceImportOwnsSourceSnapshotAndRoundTripsIdentityAtomically()
    {
        CadDocumentSession source = CreateSource(
            ("Alpha", 210, PlotRotation.NoRotation),
            ("Shared", 297, PlotRotation.Degrees90));
        CadImportNamedPageSetupsCommand command =
            CadImportNamedPageSetupsCommand.CaptureAll(
                source,
                CadPageSetupImportConflictPolicy.ReplaceExisting);
        source.Edit("Mutate source after capture", document =>
            GetPageSetups(document)
                .GetEntry<PlotSettings>("Shared")
                .PaperWidth = 999);

        var targetDocument = new CadDocument();
        CadDictionary targetDictionary = GetPageSetups(targetDocument);
        var existing = new PlotSettings("SHARED");
        Configure(existing, 500, PlotRotation.Degrees180);
        targetDictionary.Add(existing);
        ulong existingHandle = existing.Handle;
        ACadLayout model = targetDocument.Layouts[ACadLayout.ModelLayoutName];
        model.PageName = "SHARED";
        model.PaperWidth = 612;
        var target = new CadDocumentSession(targetDocument);
        var history = new CadDocumentHistory(target);

        ulong applyGeneration = history.Execute(command);

        Assert.Equal(1UL, applyGeneration);
        Assert.Equal(2, command.ImportedCount);
        Assert.Equal(1, command.CreatedCount);
        Assert.Equal(1, command.ReplacedCount);
        Assert.Same(existing, targetDictionary.GetEntry<PlotSettings>("Shared"));
        Assert.Equal("SHARED", existing.Name);
        Assert.Equal("SHARED", existing.PageName);
        Assert.Equal(existingHandle, existing.Handle);
        Assert.Equal(297, existing.PaperWidth);
        Assert.Equal(PlotRotation.Degrees90, existing.PaperRotation);
        PlotSettings created = targetDictionary.GetEntry<PlotSettings>("Alpha");
        ulong firstCreatedHandle = created.Handle;
        Assert.NotEqual(0UL, firstCreatedHandle);
        Assert.Equal(210, created.PaperWidth);
        Assert.Equal("SHARED", model.PageName);
        Assert.Equal(612, model.PaperWidth);

        Assert.True(history.TryUndo(out ulong undoGeneration));

        Assert.Equal(2UL, undoGeneration);
        Assert.False(targetDictionary.ContainsKey("Alpha"));
        Assert.Null(created.Owner);
        Assert.Equal(0UL, created.Handle);
        Assert.Same(existing, targetDictionary.GetEntry<PlotSettings>("Shared"));
        Assert.Equal("SHARED", existing.Name);
        Assert.Equal("SHARED", existing.PageName);
        Assert.Equal(existingHandle, existing.Handle);
        Assert.Equal(500, existing.PaperWidth);
        Assert.Equal(PlotRotation.Degrees180, existing.PaperRotation);
        Assert.Equal("SHARED", model.PageName);
        Assert.Equal(612, model.PaperWidth);

        Assert.True(history.TryRedo(out ulong redoGeneration));

        Assert.Equal(3UL, redoGeneration);
        Assert.Same(created, targetDictionary.GetEntry<PlotSettings>("Alpha"));
        Assert.NotEqual(0UL, created.Handle);
        Assert.NotEqual(firstCreatedHandle, created.Handle);
        Assert.Same(existing, targetDictionary.GetEntry<PlotSettings>("Shared"));
        Assert.Equal("SHARED", existing.Name);
        Assert.Equal("SHARED", existing.PageName);
        Assert.Equal(existingHandle, existing.Handle);
        Assert.Equal(297, existing.PaperWidth);
        Assert.Equal("SHARED", model.PageName);
        Assert.Equal(612, model.PaperWidth);
    }

    [Fact]
    public void SelectedImportIsCaseInsensitiveAndRejectsInvalidSelections()
    {
        CadDocumentSession source = CreateSource(
            ("Alpha", 210, PlotRotation.NoRotation),
            ("Beta", 420, PlotRotation.Degrees270));
        var targetDocument = new CadDocument();
        var target = new CadDocumentSession(targetDocument);
        var history = new CadDocumentHistory(target);

        CadImportNamedPageSetupsCommand command =
            CadImportNamedPageSetupsCommand.Capture(source, ["beta"]);
        history.Execute(command);

        CadDictionary targetDictionary = GetPageSetups(targetDocument);
        Assert.False(targetDictionary.ContainsKey("Alpha"));
        Assert.Equal(420, targetDictionary.GetEntry<PlotSettings>("Beta").PaperWidth);
        Assert.Throws<InvalidOperationException>(() =>
            CadImportNamedPageSetupsCommand.Capture(source, []));
        Assert.Throws<ArgumentException>(() =>
            CadImportNamedPageSetupsCommand.Capture(source, ["Alpha", "ALPHA"]));
        Assert.Throws<InvalidOperationException>(() =>
            CadImportNamedPageSetupsCommand.Capture(source, ["Missing"]));
        Assert.Throws<ArgumentException>(() =>
            CadImportNamedPageSetupsCommand.Capture(
                source,
                [new string(
                    'P',
                    CadImportNamedPageSetupsCommand.MaximumStringCodeUnits + 1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CadImportNamedPageSetupsCommand.CaptureAll(
                source,
                (CadPageSetupImportConflictPolicy)byte.MaxValue));
        Assert.Throws<InvalidOperationException>(() =>
            CadImportNamedPageSetupsCommand.CaptureAll(
                new CadDocumentSession(new CadDocument())));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task ImportedPageSetupsSurviveDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        CadDocumentSession source = CreateSource(
            ("Imported A3", 420, PlotRotation.Degrees270));
        var target = new CadDocumentSession(new CadDocument(ACadVersion.AC1032));
        new CadDocumentHistory(target).Execute(
            CadImportNamedPageSetupsCommand.CaptureAll(source));
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            target,
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"imported-page-setup.{format.ToString().ToLowerInvariant()}");
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(loaded.Session)
            .FindNamedOverride("Imported A3")!;

        Assert.Equal(420, setup.PaperWidthMillimeters);
        Assert.Equal(297, setup.PaperHeightMillimeters);
        Assert.Equal(CadPageRotation.CounterClockwise270, setup.Rotation);
        Assert.Equal("Imported A3", setup.PageSetupName);
    }

    [Fact]
    public void SharedViewImportsLoadedSourceAndExposesExplicitConflictActions()
    {
        var view = new CadSampleView();
        try
        {
            view.Arrange(new Rect(0, 0, 1_280, 800));
            Button import = FindButton(view, "Import setups");
            Button replace = FindButton(view, "Import / replace");
            Assert.True(import.IsEnabled);
            Assert.True(replace.IsEnabled);
            CadDocumentSession source = CreateSource(
                ("Office output", 594, PlotRotation.Degrees180));

            CadPageSetupImportResult result = view.ImportPageSetups(
                source,
                CadPageSetupImportConflictPolicy.Reject,
                "office.dwg",
                diagnosticCount: 2);

            Assert.Equal(1, result.ImportedCount);
            Assert.Equal(1, result.CreatedCount);
            Assert.NotNull(view.Canvas.CreatePageSetupCatalog()
                .FindNamedOverride("Office output"));
            Assert.Contains(
                DescendantsAndSelf(view).OfType<TextBlock>(),
                text => text.Text.Contains(
                    "Imported 1 named page setup(s) from office.dwg as one edit",
                    StringComparison.Ordinal));
            Assert.True(FindButton(view, "Undo").IsEnabled);
        }
        finally
        {
            view.PrintPreview.FireUnloaded();
            view.Canvas.FireUnloaded();
        }
    }

    private static CadDocumentSession CreateSource(
        params (string Name, double Width, PlotRotation Rotation)[] definitions)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        CadDictionary dictionary = GetPageSetups(document);
        foreach ((string name, double width, PlotRotation rotation) in definitions)
        {
            var setup = new PlotSettings(name);
            Configure(setup, width, rotation);
            dictionary.Add(setup);
        }
        return new CadDocumentSession(document);
    }

    private static CadDictionary GetPageSetups(CadDocument document) =>
        document.RootDictionary.GetEntry<CadDictionary>(
            CadDictionary.AcadPlotSettings);

    private static void Configure(
        PlotSettings setup,
        double width,
        PlotRotation rotation)
    {
        setup.PageName = setup.Name;
        setup.PaperWidth = width;
        setup.PaperHeight = 297;
        setup.UnprintableMargin = new PaperMargin(1, 2, 3, 4);
        setup.PaperUnits = PlotPaperUnits.Millimeters;
        setup.PaperRotation = rotation;
        setup.PlotType = PlotType.DrawingExtents;
        setup.NumeratorScale = 1;
        setup.DenominatorScale = 2;
        setup.ScaledFit = ScaledType._17;
        setup.StandardScale = 0.5;
        setup.ShadePlotMode = ShadePlotMode.Wireframe;
        setup.ShadePlotResolutionMode = ShadePlotResolutionMode.Presentation;
        setup.ShadePlotDPI = 600;
        setup.SystemPrinterName = "ProGPU PDF";
        setup.PaperSize = "ISO";
        setup.PlotViewName = "Output";
        setup.StyleSheet = "monochrome.ctb";
        setup.PaperImageOrigin = new XY(5, 6);
        setup.Flags = PlotFlags.PrintLineweights |
            PlotFlags.PlotCentered |
            PlotFlags.UseStandardScale |
            PlotFlags.ModelType;
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
}

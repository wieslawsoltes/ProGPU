using ACadSharp;
using ACadSharp.Objects;
using CSMath;
using Xunit;
using ACadLayout = ACadSharp.Objects.Layout;

namespace ProGPU.CAD.Tests;

public sealed class CadPageSetupFieldEditingTests
{
    [Fact]
    public void CommandRejectsEmptyOversizedAndInvalidPatches()
    {
        Assert.Throws<ArgumentException>(() =>
            new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                " ",
                new CadPageSetupFieldPatch { CenterPlot = true }));
        Assert.Throws<ArgumentException>(() =>
            new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                new string(
                    'L',
                    CadEditPageSetupFieldsCommand.MaximumNameCodeUnits + 1),
                new CadPageSetupFieldPatch { CenterPlot = true }));
        Assert.Throws<ArgumentException>(() =>
            new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                ACadLayout.ModelLayoutName,
                new CadPageSetupFieldPatch()));
        Assert.Throws<ArgumentException>(() =>
            new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                ACadLayout.ModelLayoutName,
                new CadPageSetupFieldPatch
                {
                    DeviceName = new string(
                        'D',
                        CadEditPageSetupFieldsCommand.MaximumStringCodeUnits + 1),
                }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                ACadLayout.ModelLayoutName,
                new CadPageSetupFieldPatch { PaperWidthMillimeters = 0.0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                ACadLayout.ModelLayoutName,
                new CadPageSetupFieldPatch
                {
                    UnprintableMargins = new CadPageMargins(1, -1, 1, 1),
                }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                ACadLayout.ModelLayoutName,
                new CadPageSetupFieldPatch { PaperUnit = CadPageUnit.Unknown }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                ACadLayout.ModelLayoutName,
                new CadPageSetupFieldPatch { StandardScaleCode = 33 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                ACadLayout.ModelLayoutName,
                new CadPageSetupFieldPatch { ShadeDpi = 99 }));
    }

    [Fact]
    public void LayoutFieldEditPreservesIdentityAndRoundTripsUndoRedo()
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureOriginal(model);
        PlotState original = PlotState.Capture(model);
        ulong originalHandle = model.Handle;
        int originalTabOrder = model.TabOrder;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        ulong editGeneration = history.Execute(
            new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                ACadLayout.ModelLayoutName,
                CreateFullPatch()));

        Assert.Equal(1UL, editGeneration);
        Assert.Same(model, document.Layouts[ACadLayout.ModelLayoutName]);
        Assert.Equal(originalHandle, model.Handle);
        Assert.Equal(originalTabOrder, model.TabOrder);
        Assert.Equal("Original page", model.PageName);
        Assert.True((model.Flags & PlotFlags.ModelType) != 0);
        AssertEditedState(model);

        Assert.True(history.TryUndo(out ulong undoGeneration));
        Assert.Equal(2UL, undoGeneration);
        Assert.Equal(original, PlotState.Capture(model));
        Assert.Same(model, document.Layouts[ACadLayout.ModelLayoutName]);

        Assert.True(history.TryRedo(out ulong redoGeneration));
        Assert.Equal(3UL, redoGeneration);
        AssertEditedState(model);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void NamedFieldEditPreservesIdentityAndTargetSpace()
    {
        var document = new CadDocument();
        var named = new PlotSettings("Model output");
        ConfigureOriginal(named);
        CadDictionary dictionary = document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings);
        dictionary.Add(named);
        ulong originalHandle = named.Handle;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadEditPageSetupFieldsCommand(
            CadPageSetupSourceKind.NamedOverride,
            named.Name,
            new CadPageSetupFieldPatch
            {
                MediaName = "ISO_A2",
                PaperWidthMillimeters = 594,
                PaperHeightMillimeters = 420,
                Rotation = CadPageRotation.Degrees180,
                CenterPlot = false,
            }));

        Assert.Same(named, dictionary.GetEntry<PlotSettings>(named.Name));
        Assert.Same(dictionary, named.Owner);
        Assert.Equal(originalHandle, named.Handle);
        Assert.Equal("Model output", named.Name);
        Assert.Equal("Original page", named.PageName);
        Assert.True((named.Flags & PlotFlags.ModelType) != 0);
        Assert.Equal("ISO_A2", named.PaperSize);
        Assert.Equal(594, named.PaperWidth);
        Assert.Equal(420, named.PaperHeight);
        Assert.Equal(PlotRotation.Degrees180, named.PaperRotation);
        Assert.False((named.Flags & PlotFlags.PlotCentered) != 0);
    }

    [Fact]
    public void ContextualAndNoOpFailuresDoNotAdvanceGeneration()
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureOriginal(model);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        PlotState original = PlotState.Capture(model);

        InvalidOperationException missingView = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                ACadLayout.ModelLayoutName,
                new CadPageSetupFieldPatch
                {
                    PlotArea = CadPlotAreaKind.NamedView,
                    NamedView = string.Empty,
                })));
        Assert.Contains("view name", missingView.Message, StringComparison.OrdinalIgnoreCase);

        InvalidOperationException noOp = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.Layout,
                ACadLayout.ModelLayoutName,
                new CadPageSetupFieldPatch { CenterPlot = true })));
        Assert.Contains("already has", noOp.Message, StringComparison.OrdinalIgnoreCase);

        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.NamedOverride,
                "Missing output",
                new CadPageSetupFieldPatch { CenterPlot = false })));
        Assert.Contains("does not exist", missing.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(original, PlotState.Capture(model));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task FieldEditedNamedSetupSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        var named = new PlotSettings("Persisted output");
        ConfigureOriginal(named);
        document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings)
            .Add(named);
        var session = new CadDocumentSession(document);
        new CadDocumentHistory(session).Execute(
            new CadEditPageSetupFieldsCommand(
                CadPageSetupSourceKind.NamedOverride,
                named.Name,
                CreateFullPatch()));
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
            sourceName: $"field-edited-page-setup.{format.ToString().ToLowerInvariant()}");
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(loaded.Session)
            .FindNamedOverride(named.Name)!;

        Assert.Equal("Persisted output", setup.Name);
        Assert.Equal("Original page", setup.PageSetupName);
        Assert.Equal(CadPageTargetSpace.Model, setup.TargetSpace);
        Assert.Equal("ProGPU PDF.pc3", setup.DeviceName);
        Assert.Equal("ISO_A3", setup.MediaName);
        Assert.Equal(420, setup.PaperWidthMillimeters);
        Assert.Equal(297, setup.PaperHeightMillimeters);
        Assert.Equal(new CadPageMargins(5, 6, 7, 8), setup.UnprintableMargins);
        Assert.Equal(9, setup.PlotOriginXMillimeters);
        Assert.Equal(10, setup.PlotOriginYMillimeters);
        Assert.Equal(11, setup.PaperImageOriginX);
        Assert.Equal(12, setup.PaperImageOriginY);
        Assert.Equal(CadPageUnit.Millimeters, setup.PaperUnit);
        Assert.Equal(CadPageRotation.CounterClockwise270, setup.Rotation);
        Assert.Equal(CadPlotAreaKind.Window, setup.PlotArea);
        Assert.Equal(new CadPlotRectangle(1, 2, 301, 202), setup.PlotWindow);
        Assert.False(setup.CenterPlot);
        Assert.False(setup.UseStandardScale);
        Assert.Equal(20, setup.StandardScaleCode);
        Assert.Equal(0.1, setup.StandardScaleFactor);
        Assert.Equal(1, setup.PaperUnitsNumerator);
        Assert.Equal(10, setup.DrawingUnitsDenominator);
        Assert.False(setup.PrintLineweights);
        Assert.True(setup.ScaleLineweights);
        Assert.True(setup.ApplyPlotStyles);
        Assert.True(setup.ShowPlotStyles);
        Assert.True(setup.RemoveHiddenLines);
        Assert.True(setup.PlotViewportBorders);
        Assert.False(setup.DrawViewportsFirst);
        Assert.Equal("monochrome.ctb", setup.StyleSheet);
        Assert.Equal(CadShadeOutputKind.Rendered, setup.ShadeOutput);
        Assert.Equal(CadShadeResolutionKind.Custom, setup.ShadeResolution);
        Assert.Equal(1200, setup.ShadeDpi);
    }

    private static CadPageSetupFieldPatch CreateFullPatch() => new()
    {
        DeviceName = "ProGPU PDF.pc3",
        MediaName = "ISO_A3",
        PaperWidthMillimeters = 420,
        PaperHeightMillimeters = 297,
        UnprintableMargins = new CadPageMargins(5, 6, 7, 8),
        PlotOriginXMillimeters = 9,
        PlotOriginYMillimeters = 10,
        PaperImageOriginX = 11,
        PaperImageOriginY = 12,
        PaperUnit = CadPageUnit.Millimeters,
        Rotation = CadPageRotation.CounterClockwise270,
        PlotArea = CadPlotAreaKind.Window,
        PlotWindow = new CadPlotRectangle(1, 2, 301, 202),
        NamedView = "Ignored while plotting a window",
        CenterPlot = false,
        UseStandardScale = false,
        StandardScaleCode = 20,
        StandardScaleFactor = 0.1,
        PaperUnitsNumerator = 1,
        DrawingUnitsDenominator = 10,
        PrintLineweights = false,
        ScaleLineweights = true,
        ApplyPlotStyles = true,
        ShowPlotStyles = true,
        RemoveHiddenLines = true,
        PlotViewportBorders = true,
        DrawViewportsFirst = false,
        StyleSheet = "monochrome.ctb",
        ShadeOutput = CadShadeOutputKind.Rendered,
        ShadeResolution = CadShadeResolutionKind.Custom,
        ShadeDpi = 1200,
    };

    private static void ConfigureOriginal(PlotSettings setup)
    {
        setup.PageName = "Original page";
        setup.SystemPrinterName = "Original device";
        setup.PaperSize = "ISO_A4";
        setup.PaperWidth = 210;
        setup.PaperHeight = 297;
        setup.UnprintableMargin = new PaperMargin(1, 2, 3, 4);
        setup.PlotOriginX = 3;
        setup.PlotOriginY = 4;
        setup.PaperImageOrigin = new XY(5, 6);
        setup.PaperImageOriginX = 5;
        setup.PaperImageOriginY = 6;
        setup.PaperUnits = PlotPaperUnits.Inches;
        setup.PaperRotation = PlotRotation.Degrees90;
        setup.PlotType = PlotType.DrawingExtents;
        setup.PlotViewName = "Original view";
        setup.WindowLowerLeftX = -1;
        setup.WindowLowerLeftY = -2;
        setup.WindowUpperLeftX = 10;
        setup.WindowUpperLeftY = 20;
        setup.NumeratorScale = 2;
        setup.DenominatorScale = 3;
        setup.ScaledFit = ScaledType._17;
        setup.StandardScale = 0.5;
        setup.StyleSheet = "original.ctb";
        setup.ShadePlotMode = ShadePlotMode.Wireframe;
        setup.ShadePlotResolutionMode = ShadePlotResolutionMode.Presentation;
        setup.ShadePlotDPI = 600;
        setup.Flags = PlotFlags.PrintLineweights |
            PlotFlags.PlotCentered |
            PlotFlags.UseStandardScale |
            PlotFlags.DrawViewportsFirst |
            PlotFlags.ModelType;
    }

    private static void AssertEditedState(PlotSettings setup)
    {
        Assert.Equal("ProGPU PDF.pc3", setup.SystemPrinterName);
        Assert.Equal("ISO_A3", setup.PaperSize);
        Assert.Equal(420, setup.PaperWidth);
        Assert.Equal(297, setup.PaperHeight);
        Assert.Equal(5, setup.UnprintableMargin.Left);
        Assert.Equal(6, setup.UnprintableMargin.Bottom);
        Assert.Equal(7, setup.UnprintableMargin.Right);
        Assert.Equal(8, setup.UnprintableMargin.Top);
        Assert.Equal(9, setup.PlotOriginX);
        Assert.Equal(10, setup.PlotOriginY);
        Assert.Equal(new XY(11, 12), setup.PaperImageOrigin);
        Assert.Equal(11, setup.PaperImageOriginX);
        Assert.Equal(12, setup.PaperImageOriginY);
        Assert.Equal(PlotPaperUnits.Millimeters, setup.PaperUnits);
        Assert.Equal(PlotRotation.Degrees270, setup.PaperRotation);
        Assert.Equal(PlotType.Window, setup.PlotType);
        Assert.Equal(1, setup.WindowLowerLeftX);
        Assert.Equal(2, setup.WindowLowerLeftY);
        Assert.Equal(301, setup.WindowUpperLeftX);
        Assert.Equal(202, setup.WindowUpperLeftY);
        Assert.Equal("Ignored while plotting a window", setup.PlotViewName);
        Assert.Equal(1, setup.NumeratorScale);
        Assert.Equal(10, setup.DenominatorScale);
        Assert.Equal(ScaledType._20, setup.ScaledFit);
        Assert.Equal(0.1, setup.StandardScale);
        Assert.Equal("monochrome.ctb", setup.StyleSheet);
        Assert.Equal(ShadePlotMode.Rendered, setup.ShadePlotMode);
        Assert.Equal(ShadePlotResolutionMode.Custom, setup.ShadePlotResolutionMode);
        Assert.Equal(1200, setup.ShadePlotDPI);
        Assert.False((setup.Flags & PlotFlags.PlotCentered) != 0);
        Assert.False((setup.Flags & PlotFlags.UseStandardScale) != 0);
        Assert.False((setup.Flags & PlotFlags.PrintLineweights) != 0);
        Assert.True((setup.Flags & PlotFlags.ScaleLineweights) != 0);
        Assert.True((setup.Flags & PlotFlags.PlotPlotStyles) != 0);
        Assert.True((setup.Flags & PlotFlags.ShowPlotStyles) != 0);
        Assert.True((setup.Flags & PlotFlags.PlotHidden) != 0);
        Assert.True((setup.Flags & PlotFlags.PlotViewportBorders) != 0);
        Assert.False((setup.Flags & PlotFlags.DrawViewportsFirst) != 0);
        Assert.True((setup.Flags & PlotFlags.ModelType) != 0);
    }

    private readonly record struct PlotState(
        double DenominatorScale,
        PlotFlags Flags,
        double NumeratorScale,
        string? PageName,
        double PaperHeight,
        XY PaperImageOrigin,
        double PaperImageOriginX,
        double PaperImageOriginY,
        PlotRotation PaperRotation,
        string? PaperSize,
        PlotPaperUnits PaperUnits,
        double PaperWidth,
        double PlotOriginX,
        double PlotOriginY,
        PlotType PlotType,
        string? PlotViewName,
        ScaledType ScaledFit,
        short ShadePlotDpi,
        ulong ShadePlotIdHandle,
        ShadePlotMode ShadePlotMode,
        ShadePlotResolutionMode ShadePlotResolutionMode,
        double StandardScale,
        string? StyleSheet,
        string? SystemPrinterName,
        PaperMargin UnprintableMargin,
        double WindowLowerLeftX,
        double WindowLowerLeftY,
        double WindowUpperLeftX,
        double WindowUpperLeftY)
    {
        public static PlotState Capture(PlotSettings source) => new(
            source.DenominatorScale,
            source.Flags,
            source.NumeratorScale,
            source.PageName,
            source.PaperHeight,
            source.PaperImageOrigin,
            source.PaperImageOriginX,
            source.PaperImageOriginY,
            source.PaperRotation,
            source.PaperSize,
            source.PaperUnits,
            source.PaperWidth,
            source.PlotOriginX,
            source.PlotOriginY,
            source.PlotType,
            source.PlotViewName,
            source.ScaledFit,
            source.ShadePlotDPI,
            source.ShadePlotIDHandle,
            source.ShadePlotMode,
            source.ShadePlotResolutionMode,
            source.StandardScale,
            source.StyleSheet,
            source.SystemPrinterName,
            source.UnprintableMargin,
            source.WindowLowerLeftX,
            source.WindowLowerLeftY,
            source.WindowUpperLeftX,
            source.WindowUpperLeftY);
    }
}

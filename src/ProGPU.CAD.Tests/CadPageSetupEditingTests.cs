using ACadSharp;
using ACadSharp.Objects;
using CSMath;
using Xunit;
using ACadLayout = ACadSharp.Objects.Layout;

namespace ProGPU.CAD.Tests;

public sealed class CadPageSetupEditingTests
{
    [Fact]
    public void CommandRejectsEmptyAndOversizedNames()
    {
        Assert.Throws<ArgumentException>(() =>
            new CadApplyNamedPageSetupCommand(string.Empty, "Named"));
        Assert.Throws<ArgumentException>(() =>
            new CadApplyNamedPageSetupCommand("Model", " "));
        Assert.Throws<ArgumentException>(() =>
            new CadApplyNamedPageSetupCommand(
                new string('L', CadApplyNamedPageSetupCommand.MaximumNameCodeUnits + 1),
                "Named"));
        Assert.Throws<ArgumentException>(() =>
            new CadApplyNamedPageSetupCommand(
                "Model",
                new string('P', CadApplyNamedPageSetupCommand.MaximumNameCodeUnits + 1)));
    }

    [Fact]
    public void ApplyNamedPageSetupPreservesLayoutIdentityAndRoundTripsUndoRedo()
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        Configure(
            model,
            pageName: "Original A3",
            width: 420,
            height: 297,
            rotation: PlotRotation.NoRotation,
            modelType: true);
        model.TabOrder = 17;
        model.MinLimits = new XY(-10, -20);
        model.MaxLimits = new XY(700, 500);
        var named = new PlotSettings("Named A4 portrait");
        Configure(
            named,
            pageName: "Named A4 portrait",
            width: 210,
            height: 297,
            rotation: PlotRotation.Degrees90,
            modelType: true);
        named.SystemPrinterName = "ProGPU PDF";
        named.PaperSize = "ISO_A4";
        named.PlotOriginX = 7;
        named.PlotOriginY = 9;
        named.PaperImageOrigin = new XY(3, 4);
        named.PaperImageOriginX = 5;
        named.PaperImageOriginY = 6;
        named.PlotViewName = "Output view";
        named.StyleSheet = "monochrome.ctb";
        named.WindowLowerLeftX = 11;
        named.WindowLowerLeftY = 12;
        named.WindowUpperLeftX = 90;
        named.WindowUpperLeftY = 91;
        named.ShadePlotIDHandle = 0x1234;
        document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings)
            .Add(named);
        PlotState original = PlotState.Capture(model);
        ACadSharp.Tables.BlockRecord block = model.AssociatedBlock;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        ulong appliedGeneration = history.Execute(
            new CadApplyNamedPageSetupCommand(
                ACadLayout.ModelLayoutName,
                named.Name));

        Assert.Equal(1UL, appliedGeneration);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.Same(block, model.AssociatedBlock);
        Assert.Equal(ACadLayout.ModelLayoutName, model.Name);
        Assert.Equal(17, model.TabOrder);
        Assert.Equal(new XY(-10, -20), model.MinLimits);
        Assert.Equal(new XY(700, 500), model.MaxLimits);
        Assert.Equal(PlotState.Capture(named), PlotState.Capture(model));
        Assert.Equal("Named A4 portrait", named.Name);
        Assert.Equal(
            "Named A4 portrait",
            new CadPageSetupCatalogCompiler()
                .Compile(session)
                .FindLayout(ACadLayout.ModelLayoutName)!
                .PageSetupName);

        Assert.True(history.TryUndo(out ulong undoGeneration));
        Assert.Equal(2UL, undoGeneration);
        Assert.Equal(original, PlotState.Capture(model));
        Assert.Same(block, model.AssociatedBlock);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(1, history.RedoCount);

        Assert.True(history.TryRedo(out ulong redoGeneration));
        Assert.Equal(3UL, redoGeneration);
        Assert.Equal(PlotState.Capture(named), PlotState.Capture(model));
        Assert.Same(block, model.AssociatedBlock);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void ApplyNamedPageSetupRejectsModelPaperMismatchTransactionally()
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        Configure(
            model,
            pageName: "Original",
            width: 420,
            height: 297,
            rotation: PlotRotation.NoRotation,
            modelType: true);
        var paperSetup = new PlotSettings("Paper only");
        Configure(
            paperSetup,
            pageName: "Paper only",
            width: 210,
            height: 297,
            rotation: PlotRotation.NoRotation,
            modelType: false);
        document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings)
            .Add(paperSetup);
        PlotState original = PlotState.Capture(model);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadApplyNamedPageSetupCommand(
                ACadLayout.ModelLayoutName,
                paperSetup.Name)));

        Assert.Contains("paper space", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.Equal(original, PlotState.Capture(model));
    }

    [Theory]
    [InlineData("Missing layout", "Named", "Layout 'Missing layout' does not exist")]
    [InlineData("Model", "Missing setup", "Named page setup 'Missing setup' does not exist")]
    public void ApplyNamedPageSetupRejectsMissingObjectsWithoutAdvancingGeneration(
        string layoutName,
        string pageSetupName,
        string expectedMessage)
    {
        var document = new CadDocument();
        var named = new PlotSettings("Named");
        named.Flags |= PlotFlags.ModelType;
        document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings)
            .Add(named);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadApplyNamedPageSetupCommand(
                layoutName,
                pageSetupName)));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AppliedNamedPageSetupSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        Configure(
            model,
            pageName: "Original",
            width: 420,
            height: 297,
            rotation: PlotRotation.NoRotation,
            modelType: true);
        var named = new PlotSettings("A4 output");
        Configure(
            named,
            pageName: "A4 output",
            width: 210,
            height: 297,
            rotation: PlotRotation.Degrees90,
            modelType: true);
        document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings)
            .Add(named);
        var session = new CadDocumentSession(document);
        new CadDocumentHistory(session).Execute(
            new CadApplyNamedPageSetupCommand(
                ACadLayout.ModelLayoutName,
                named.Name));
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
            sourceName: $"page-setup.{format.ToString().ToLowerInvariant()}");
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(loaded.Session)
            .FindLayout(ACadLayout.ModelLayoutName)!;

        Assert.Equal("A4 output", setup.PageSetupName);
        Assert.Equal(210, setup.PaperWidthMillimeters);
        Assert.Equal(297, setup.PaperHeightMillimeters);
        Assert.Equal(CadPageRotation.CounterClockwise90, setup.Rotation);
    }

    private static void Configure(
        PlotSettings setup,
        string pageName,
        double width,
        double height,
        PlotRotation rotation,
        bool modelType)
    {
        setup.PageName = pageName;
        setup.PaperWidth = width;
        setup.PaperHeight = height;
        setup.UnprintableMargin = new PaperMargin(1, 2, 3, 4);
        setup.PaperUnits = PlotPaperUnits.Millimeters;
        setup.PaperRotation = rotation;
        setup.PlotType = PlotType.DrawingExtents;
        setup.NumeratorScale = 2;
        setup.DenominatorScale = 3;
        setup.ScaledFit = ScaledType._17;
        setup.StandardScale = 0.5;
        setup.ShadePlotMode = ShadePlotMode.Wireframe;
        setup.ShadePlotResolutionMode = ShadePlotResolutionMode.Presentation;
        setup.ShadePlotDPI = 600;
        setup.Flags = PlotFlags.PrintLineweights |
            PlotFlags.PlotCentered |
            PlotFlags.UseStandardScale |
            (modelType ? PlotFlags.ModelType : 0);
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

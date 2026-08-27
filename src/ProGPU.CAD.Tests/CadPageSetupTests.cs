using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using Xunit;
using ACadLayout = ACadSharp.Objects.Layout;

namespace ProGPU.CAD.Tests;

public sealed class CadPageSetupTests
{
    [Fact]
    public void CatalogOwnsStringsAndOrdersLayoutsBeforeNamedOverrides()
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        model.PageName = new string("Model page".AsSpan());
        CadDictionary dictionary = document.RootDictionary.GetEntry<CadDictionary>(
            CadDictionary.AcadPlotSettings);
        dictionary.Add(new PlotSettings("Zulu"));
        dictionary.Add(new PlotSettings("Alpha"));
        var session = new CadDocumentSession(document);

        CadPageSetupCatalog catalog = new CadPageSetupCatalogCompiler().Compile(session);

        Assert.Equal(4, catalog.Setups.Length);
        Assert.Equal(ACadLayout.ModelLayoutName, catalog.Setups.Span[0].Name);
        Assert.Equal(CadPageTargetSpace.Model, catalog.Setups.Span[0].TargetSpace);
        Assert.Equal(CadPageTargetSpace.Paper, catalog.Setups.Span[1].TargetSpace);
        Assert.Equal("Alpha", catalog.Setups.Span[2].Name);
        Assert.Equal("Zulu", catalog.Setups.Span[3].Name);
        Assert.All(
            catalog.Setups.ToArray()[..2],
            setup => Assert.Equal(CadPageSetupSourceKind.Layout, setup.SourceKind));
        Assert.All(
            catalog.Setups.ToArray()[2..],
            setup => Assert.Equal(CadPageSetupSourceKind.NamedOverride, setup.SourceKind));

        CadPageSetupSnapshot modelSnapshot = catalog.FindLayout("model")!;
        Assert.Equal("Model page", modelSnapshot.PageSetupName);
        Assert.NotSame(model.PageName, modelSnapshot.PageSetupName);
        session.Edit("mutate source page setup", cad =>
        {
            cad.Layouts[ACadLayout.ModelLayoutName].PageName = "Changed";
            cad.RootDictionary
                .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings)
                .GetEntry<PlotSettings>("Alpha")
                .SystemPrinterName = "Changed printer";
        });

        Assert.Equal(0UL, catalog.ContentGeneration);
        Assert.Equal("Model page", modelSnapshot.PageSetupName);
        Assert.Equal(string.Empty, catalog.FindNamedOverride("alpha")!.DeviceName);
        Assert.Empty(catalog.Diagnostics.ToArray());
    }

    [Fact]
    public void ModelExtentsCustomMillimeterSetupCompilesRetainedPrintPlan()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 10, 0)));
        ConfigureSupported(document.Layouts[ACadLayout.ModelLayoutName]);
        var session = new CadDocumentSession(document);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(session)
            .FindLayout(ACadLayout.ModelLayoutName)!;
        CadPageSetupPrintOptionsResult result = new CadPageSetupPrintOptionsCompiler().Compile(
            setup,
            new CadPageSetupPrintOptionsCompilerOptions { OutputDpi = 254 });

        Assert.True(result.IsSupported);
        Assert.Empty(result.Diagnostics.ToArray());
        CadPrintPlanOptions options = result.PrintOptions!;
        Assert.Equal(2.0, options.ModelUnitsPerMillimeter, 12);
        Assert.Equal(CadPrintPlacementMode.PrintableAreaOffset, options.PlacementMode);
        Assert.Equal(3.0, options.PlotOffsetXMillimeters);
        Assert.Equal(4.0, options.PlotOffsetYMillimeters);

        using CadPrintPlan plan = new CadPrintPlanCompiler().CompileFromPageSetup(
            snapshot,
            result);
        Assert.Equal(ACadLayout.ModelLayoutName, plan.SourcePageSetupName);
        Assert.Equal(new CadPrintPixelSize(1000, 500), plan.PageSizePixels);
        Assert.Equal(new CadPrintPixelRect(50, 80, 880, 360), plan.PrintableAreaPixels);
        Assert.Equal(5.0f, plan.PixelsPerModelUnit);
        Assert.Equal(2.0, plan.ModelUnitsPerMillimeter, 12);

        session.Edit("advance content generation", static _ => { });
        CadDocumentSnapshot newerSnapshot = new CadSnapshotCompiler().Compile(session);
        Assert.Throws<InvalidOperationException>(() =>
            new CadPrintPlanCompiler().CompileFromPageSetup(newerSnapshot, result));
    }

    [Fact]
    public void InchCustomScaleAndStandardFitPreserveSourceSemantics()
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureSupported(model);
        model.PaperUnits = PlotPaperUnits.Inches;
        model.NumeratorScale = 0.5;
        model.DenominatorScale = 127;
        var session = new CadDocumentSession(document);
        CadPageSetupSnapshot custom = new CadPageSetupCatalogCompiler()
            .Compile(session)
            .FindLayout(ACadLayout.ModelLayoutName)!;

        CadPageSetupPrintOptionsResult customResult =
            new CadPageSetupPrintOptionsCompiler().Compile(custom);

        Assert.True(customResult.IsSupported);
        Assert.Equal(CadPrintScaleMode.ModelUnitsPerMillimeter, customResult.PrintOptions!.ScaleMode);
        Assert.Equal(10.0, customResult.PrintOptions.ModelUnitsPerMillimeter, 12);

        session.Edit("switch to standard fit", cad =>
        {
            ACadLayout edited = cad.Layouts[ACadLayout.ModelLayoutName];
            edited.Flags |= PlotFlags.UseStandardScale;
            edited.ScaledFit = ScaledType.ScaledToFit;
        });
        CadPageSetupSnapshot fit = new CadPageSetupCatalogCompiler()
            .Compile(session)
            .FindLayout(ACadLayout.ModelLayoutName)!;
        CadPageSetupPrintOptionsResult fitResult =
            new CadPageSetupPrintOptionsCompiler().Compile(fit);

        Assert.True(fitResult.IsSupported);
        Assert.Equal(CadPrintScaleMode.FitToPrintableArea, fitResult.PrintOptions!.ScaleMode);
    }

    [Theory]
    [InlineData(PlotType.Window, "CADPAGE104")]
    [InlineData(PlotType.DrawingLimits, "CADPAGE105")]
    [InlineData(PlotType.LastScreenDisplay, "CADPAGE106")]
    [InlineData(PlotType.View, "CADPAGE107")]
    [InlineData(PlotType.LayoutInformation, "CADPAGE108")]
    public void CoordinateDependentPlotAreasFailWithSpecificDiagnostics(
        PlotType plotType,
        string expectedCode)
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureSupported(model);
        model.PlotType = plotType;
        model.WindowLowerLeftX = 10;
        model.WindowLowerLeftY = 20;
        model.WindowUpperLeftX = 30;
        model.WindowUpperLeftY = 40;
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(new CadDocumentSession(document))
            .FindLayout(ACadLayout.ModelLayoutName)!;

        CadPageSetupPrintOptionsResult result =
            new CadPageSetupPrintOptionsCompiler().Compile(setup);

        Assert.False(result.IsSupported);
        Assert.Contains(result.Diagnostics.ToArray(), item => item.Code == expectedCode);
        if (plotType == PlotType.Window)
        {
            Assert.Equal(new CadPlotRectangle(10, 20, 30, 40), setup.PlotWindow);
            Assert.Contains("DCS", result.Diagnostics.Span[0].Message);
        }
    }

    [Fact]
    public void UnsupportedPoliciesBudgetsAndCancellationFailExplicitly()
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureSupported(model);
        model.PaperRotation = PlotRotation.Degrees90;
        model.ShadePlotMode = ShadePlotMode.Rendered;
        model.Flags &= ~PlotFlags.PrintLineweights;
        model.Flags |= PlotFlags.ScaleLineweights | PlotFlags.PlotPlotStyles;
        model.StyleSheet = "office.ctb";
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(new CadDocumentSession(document))
            .FindLayout(ACadLayout.ModelLayoutName)!;

        CadPageSetupPrintOptionsResult result =
            new CadPageSetupPrintOptionsCompiler().Compile(setup);

        Assert.False(result.IsSupported);
        string[] codes = result.Diagnostics.ToArray().Select(item => item.Code).ToArray();
        Assert.Contains("CADPAGE102", codes);
        Assert.Contains("CADPAGE110", codes);
        Assert.Contains("CADPAGE112", codes);
        Assert.Contains("CADPAGE113", codes);
        Assert.Contains("CADPAGE114", codes);
        Assert.Throws<NotSupportedException>(() =>
            new CadPrintPlanCompiler().CompileFromPageSetup(
                new CadSnapshotCompiler().Compile(new CadDocumentSession(document)),
                result));

        Assert.Throws<InvalidDataException>(() =>
            new CadPageSetupCatalogCompiler().Compile(
                new CadDocumentSession(new CadDocument()),
                new CadPageSetupCatalogOptions { MaxSetups = 1 }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new CadPageSetupCatalogCompiler().Compile(
                new CadDocumentSession(new CadDocument()),
                cancellationToken: cancellation.Token));

        var transparentDocument = new CadDocument();
        ConfigureSupported(transparentDocument.Layouts[ACadLayout.ModelLayoutName]);
        transparentDocument.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX)
        {
            Transparency = new Transparency(25),
        });
        var transparentSession = new CadDocumentSession(transparentDocument);
        CadDocumentSnapshot transparentSnapshot =
            new CadSnapshotCompiler().Compile(transparentSession);
        CadPageSetupPrintOptionsResult transparentSetup =
            new CadPageSetupPrintOptionsCompiler().Compile(
                new CadPageSetupCatalogCompiler()
                    .Compile(transparentSession)
                    .FindLayout(ACadLayout.ModelLayoutName)!);
        NotSupportedException transparencyError = Assert.Throws<NotSupportedException>(() =>
            new CadPrintPlanCompiler().CompileFromPageSetup(
                transparentSnapshot,
                transparentSetup));
        Assert.Contains("CADPAGE118", transparencyError.Message);
    }

    private static void ConfigureSupported(PlotSettings setup)
    {
        setup.Flags = PlotFlags.PrintLineweights;
        setup.PaperWidth = 100;
        setup.PaperHeight = 50;
        setup.UnprintableMargin = new PaperMargin(5, 6, 7, 8);
        setup.PlotOriginX = 3;
        setup.PlotOriginY = 4;
        setup.PaperUnits = PlotPaperUnits.Millimeters;
        setup.PaperRotation = PlotRotation.NoRotation;
        setup.PlotType = PlotType.DrawingExtents;
        setup.NumeratorScale = 1;
        setup.DenominatorScale = 2;
        setup.ShadePlotMode = ShadePlotMode.Wireframe;
        setup.StyleSheet = string.Empty;
    }
}

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
    [MemberData(nameof(StandardScaleCases))]
    public void EveryPersistedStandardScaleLowersFromItsCanonicalFactor(
        int standardScaleCode,
        double standardScaleFactor)
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureSupported(model);
        model.Flags |= PlotFlags.UseStandardScale;
        model.ScaledFit = (ScaledType)standardScaleCode;
        model.StandardScale = standardScaleFactor;
        model.NumeratorScale = 37;
        model.DenominatorScale = 11;
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(new CadDocumentSession(document))
            .FindLayout(ACadLayout.ModelLayoutName)!;

        CadPageSetupPrintOptionsResult result =
            new CadPageSetupPrintOptionsCompiler().Compile(setup);

        Assert.True(result.IsSupported);
        Assert.Empty(result.Diagnostics.ToArray());
        Assert.Equal(
            1.0 / standardScaleFactor,
            result.PrintOptions!.ModelUnitsPerMillimeter,
            12);
    }

    [Theory]
    [InlineData(17, 1.0, "CADPAGE122")]
    [InlineData(17, double.NaN, "CADPAGE122")]
    [InlineData(33, 1.0, "CADPAGE115")]
    public void InvalidStandardScaleStateFailsClosed(
        int standardScaleCode,
        double standardScaleFactor,
        string diagnosticCode)
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureSupported(model);
        model.Flags |= PlotFlags.UseStandardScale;
        model.ScaledFit = (ScaledType)standardScaleCode;
        model.StandardScale = standardScaleFactor;
        model.NumeratorScale = 1.0;
        model.DenominatorScale = 2.0;
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(new CadDocumentSession(document))
            .FindLayout(ACadLayout.ModelLayoutName)!;

        CadPageSetupPrintOptionsResult result =
            new CadPageSetupPrintOptionsCompiler().Compile(setup);

        Assert.False(result.IsSupported);
        Assert.Null(result.PrintOptions);
        Assert.Contains(result.Diagnostics.ToArray(), item => item.Code == diagnosticCode);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task StandardScaleLoweringSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureSupported(model);
        model.Flags |= PlotFlags.UseStandardScale;
        model.ScaledFit = ScaledType._6;
        model.StandardScale = 1.0 / 96.0;
        model.NumeratorScale = 7;
        model.DenominatorScale = 13;
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            new CadDocumentSession(document),
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"standard-scale.{format.ToString().ToLowerInvariant()}");
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(loaded.Session)
            .FindLayout(ACadLayout.ModelLayoutName)!;

        CadPageSetupPrintOptionsResult result =
            new CadPageSetupPrintOptionsCompiler().Compile(setup);

        Assert.True(
            result.IsSupported,
            string.Join(
                "; ",
                result.Diagnostics.ToArray().Select(item => $"{item.Code}: {item.Message}")) +
                $" Stored code/factor: {setup.StandardScaleCode}/{setup.StandardScaleFactor:R}");
        Assert.Equal(6, setup.StandardScaleCode);
        Assert.Equal(1.0 / 96.0, setup.StandardScaleFactor, 15);
        Assert.Equal(96.0, result.PrintOptions!.ModelUnitsPerMillimeter, 12);
    }

    [Theory]
    [InlineData(PlotRotation.NoRotation, CadPageRotation.Degrees0, 1000, 500)]
    [InlineData(PlotRotation.Degrees90, CadPageRotation.CounterClockwise90, 500, 1000)]
    [InlineData(PlotRotation.Degrees180, CadPageRotation.Degrees180, 1000, 500)]
    [InlineData(PlotRotation.Degrees270, CadPageRotation.CounterClockwise270, 500, 1000)]
    public void EveryDefinedPageRotationLowersIntoTheRetainedPrintPlan(
        PlotRotation sourceRotation,
        CadPageRotation expectedRotation,
        int expectedWidth,
        int expectedHeight)
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 10, 0)));
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureSupported(model);
        model.PaperRotation = sourceRotation;
        var session = new CadDocumentSession(document);
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(session)
            .FindLayout(ACadLayout.ModelLayoutName)!;

        CadPageSetupPrintOptionsResult result = new CadPageSetupPrintOptionsCompiler().Compile(
            setup,
            new CadPageSetupPrintOptionsCompilerOptions { OutputDpi = 254 });

        Assert.True(result.IsSupported);
        Assert.Equal(expectedRotation, result.PrintOptions!.Rotation);
        using CadPrintPlan plan = new CadPrintPlanCompiler().CompileFromPageSetup(
            new CadSnapshotCompiler().Compile(session),
            result);
        Assert.Equal(expectedRotation, plan.Rotation);
        Assert.Equal(new CadPrintPixelSize(expectedWidth, expectedHeight), plan.PageSizePixels);
    }

    [Fact]
    public void UnknownPageRotationFailsWithTheSpecificDiagnostic()
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureSupported(model);
        model.PaperRotation = (PlotRotation)99;
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(new CadDocumentSession(document))
            .FindLayout(ACadLayout.ModelLayoutName)!;

        CadPageSetupPrintOptionsResult result =
            new CadPageSetupPrintOptionsCompiler().Compile(setup);

        Assert.False(result.IsSupported);
        Assert.Contains(result.Diagnostics.ToArray(), item => item.Code == "CADPAGE102");
    }

    [Theory]
    [InlineData(PlotType.Window, "CADPAGE104")]
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
    public void ModelLayoutDrawingLimitsLowerAsExplicitWorldBounds()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(new XYZ(-100, -100, 0), new XYZ(500, 500, 0)));
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureSupported(model);
        model.PlotType = PlotType.DrawingLimits;
        model.MinLimits = new XY(10, 20);
        model.MaxLimits = new XY(110, 70);
        var session = new CadDocumentSession(document);
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(session)
            .FindLayout(ACadLayout.ModelLayoutName)!;

        CadPageSetupPrintOptionsResult result =
            new CadPageSetupPrintOptionsCompiler().Compile(setup);

        Assert.True(result.IsSupported);
        Assert.Equal(new CadPlotRectangle(10, 20, 110, 70), setup.LayoutLimits);
        Assert.Equal(
            new CadBounds3D(
                new CadPoint3D(10, 20, 0),
                new CadPoint3D(110, 70, 0)),
            result.PrintOptions!.PlotBounds);
        using CadPrintPlan plan = new CadPrintPlanCompiler().CompileFromPageSetup(
            new CadSnapshotCompiler().Compile(session),
            result);
        Assert.Equal(result.PrintOptions.PlotBounds, plan.PlotBounds);
    }

    [Fact]
    public void NamedPageSetupDrawingLimitsFailsWithoutLayoutGeometry()
    {
        var document = new CadDocument();
        var named = new PlotSettings("Model limits")
        {
            PlotType = PlotType.DrawingLimits,
        };
        ConfigureSupported(named);
        named.PlotType = PlotType.DrawingLimits;
        named.Flags |= PlotFlags.ModelType;
        document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings)
            .Add(named);
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(new CadDocumentSession(document))
            .FindNamedOverride(named.Name)!;

        CadPageSetupPrintOptionsResult result =
            new CadPageSetupPrintOptionsCompiler().Compile(setup);

        Assert.False(result.IsSupported);
        Assert.Contains(result.Diagnostics.ToArray(), item => item.Code == "CADPAGE105");
        Assert.Null(result.PrintOptions);
    }

    [Fact]
    public void UnsupportedPoliciesBudgetsAndCancellationFailExplicitly()
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        ConfigureSupported(model);
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

    public static TheoryData<int, double> StandardScaleCases => new()
    {
        { 1, 1.0 / 1_536.0 },
        { 2, 1.0 / 768.0 },
        { 3, 1.0 / 384.0 },
        { 4, 1.0 / 192.0 },
        { 5, 1.0 / 128.0 },
        { 6, 1.0 / 96.0 },
        { 7, 1.0 / 64.0 },
        { 8, 1.0 / 48.0 },
        { 9, 1.0 / 32.0 },
        { 10, 1.0 / 24.0 },
        { 11, 1.0 / 16.0 },
        { 12, 1.0 / 12.0 },
        { 13, 1.0 / 4.0 },
        { 14, 1.0 / 2.0 },
        { 15, 1.0 },
        { 16, 1.0 },
        { 17, 1.0 / 2.0 },
        { 18, 1.0 / 4.0 },
        { 19, 1.0 / 8.0 },
        { 20, 1.0 / 10.0 },
        { 21, 1.0 / 16.0 },
        { 22, 1.0 / 20.0 },
        { 23, 1.0 / 30.0 },
        { 24, 1.0 / 40.0 },
        { 25, 1.0 / 50.0 },
        { 26, 1.0 / 100.0 },
        { 27, 2.0 },
        { 28, 4.0 },
        { 29, 8.0 },
        { 30, 10.0 },
        { 31, 100.0 },
        { 32, 1_000.0 },
    };
}

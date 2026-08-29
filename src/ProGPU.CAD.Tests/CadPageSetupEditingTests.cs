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
        Assert.Throws<ArgumentException>(() =>
            new CadCreateNamedPageSetupCommand(string.Empty, "Named"));
        Assert.Throws<ArgumentException>(() =>
            new CadCreateNamedPageSetupCommand("Model", " "));
        Assert.Throws<ArgumentException>(() =>
            new CadCreateNamedPageSetupCommand(
                new string('L', CadCreateNamedPageSetupCommand.MaximumNameCodeUnits + 1),
                "Named"));
        Assert.Throws<ArgumentException>(() =>
            new CadCreateNamedPageSetupCommand(
                "Model",
                new string('P', CadCreateNamedPageSetupCommand.MaximumNameCodeUnits + 1)));
        Assert.Throws<ArgumentException>(() =>
            new CadUpdateNamedPageSetupFromLayoutCommand(string.Empty, "Named"));
        Assert.Throws<ArgumentException>(() =>
            new CadUpdateNamedPageSetupFromLayoutCommand("Model", " "));
        Assert.Throws<ArgumentException>(() =>
            new CadUpdateNamedPageSetupFromLayoutCommand(
                new string(
                    'L',
                    CadUpdateNamedPageSetupFromLayoutCommand.MaximumNameCodeUnits + 1),
                "Named"));
        Assert.Throws<ArgumentException>(() =>
            new CadUpdateNamedPageSetupFromLayoutCommand(
                "Model",
                new string(
                    'P',
                    CadUpdateNamedPageSetupFromLayoutCommand.MaximumNameCodeUnits + 1)));
        Assert.Throws<ArgumentException>(() =>
            new CadDeleteNamedPageSetupCommand(" "));
        Assert.Throws<ArgumentException>(() =>
            new CadDeleteNamedPageSetupCommand(new string(
                'P',
                CadDeleteNamedPageSetupCommand.MaximumNameCodeUnits + 1)));
    }

    [Fact]
    public void UpdateNamedPageSetupPreservesIdentityAndRoundTripsUndoRedo()
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        Configure(
            model,
            pageName: "Live Model settings",
            width: 420,
            height: 297,
            rotation: PlotRotation.Degrees180,
            modelType: true);
        model.SystemPrinterName = "Updated ProGPU PDF";
        model.PaperSize = "ISO_A3";
        model.PlotOriginX = 7;
        model.PlotOriginY = 9;
        model.StyleSheet = "updated.ctb";
        var named = new PlotSettings("Stable output");
        Configure(
            named,
            pageName: "Stable output",
            width: 210,
            height: 297,
            rotation: PlotRotation.Degrees90,
            modelType: true);
        named.SystemPrinterName = "Original PDF";
        named.PaperSize = "ISO_A4";
        named.StyleSheet = "original.ctb";
        CadDictionary dictionary = document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings);
        dictionary.Add(named);
        PlotState modelState = PlotState.Capture(model);
        PlotState original = PlotState.Capture(named);
        ulong originalHandle = named.Handle;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        ulong updatedGeneration = history.Execute(
            new CadUpdateNamedPageSetupFromLayoutCommand(
                ACadLayout.ModelLayoutName,
                named.Name));

        Assert.Equal(1UL, updatedGeneration);
        Assert.Same(named, dictionary.GetEntry<PlotSettings>("Stable output"));
        Assert.Same(dictionary, named.Owner);
        Assert.Equal(originalHandle, named.Handle);
        Assert.Equal("Stable output", named.Name);
        Assert.Equal("Stable output", named.PageName);
        Assert.Equal(
            modelState with { PageName = "Stable output" },
            PlotState.Capture(named));
        Assert.Equal(modelState, PlotState.Capture(model));

        Assert.True(history.TryUndo(out ulong undoGeneration));

        Assert.Equal(2UL, undoGeneration);
        Assert.Equal(original, PlotState.Capture(named));
        Assert.Same(named, dictionary.GetEntry<PlotSettings>("Stable output"));
        Assert.Equal(originalHandle, named.Handle);

        Assert.True(history.TryRedo(out ulong redoGeneration));

        Assert.Equal(3UL, redoGeneration);
        Assert.Equal(
            modelState with { PageName = "Stable output" },
            PlotState.Capture(named));
        Assert.Same(named, dictionary.GetEntry<PlotSettings>("Stable output"));
        Assert.Equal(originalHandle, named.Handle);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void UpdateNamedPageSetupRejectsTargetSpaceMismatchTransactionally()
    {
        var document = new CadDocument();
        var paperSetup = new PlotSettings("Paper output");
        Configure(
            paperSetup,
            pageName: "Paper output",
            width: 297,
            height: 210,
            rotation: PlotRotation.NoRotation,
            modelType: false);
        document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings)
            .Add(paperSetup);
        PlotState original = PlotState.Capture(paperSetup);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadUpdateNamedPageSetupFromLayoutCommand(
                ACadLayout.ModelLayoutName,
                paperSetup.Name)));

        Assert.Contains("paper space", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(original, PlotState.Capture(paperSetup));
    }

    [Theory]
    [InlineData("Missing layout", "Existing output", "Layout 'Missing layout' does not exist")]
    [InlineData("Model", "Missing output", "Named page setup 'Missing output' does not exist")]
    public void UpdateNamedPageSetupRejectsMissingObjectsWithoutAdvancingGeneration(
        string layoutName,
        string pageSetupName,
        string expectedMessage)
    {
        var document = new CadDocument();
        var existing = new PlotSettings("Existing output");
        existing.Flags |= PlotFlags.ModelType;
        document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings)
            .Add(existing);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadUpdateNamedPageSetupFromLayoutCommand(
                layoutName,
                pageSetupName)));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task UpdatedNamedPageSetupSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        Configure(
            model,
            pageName: "Source settings",
            width: 420,
            height: 297,
            rotation: PlotRotation.Degrees270,
            modelType: true);
        model.SystemPrinterName = "Updated ProGPU PDF";
        model.PaperSize = "ISO_A3";
        var named = new PlotSettings("Persisted output");
        Configure(
            named,
            pageName: "Persisted output",
            width: 210,
            height: 297,
            rotation: PlotRotation.Degrees90,
            modelType: true);
        document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings)
            .Add(named);
        var session = new CadDocumentSession(document);
        new CadDocumentHistory(session).Execute(
            new CadUpdateNamedPageSetupFromLayoutCommand(
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
            sourceName: $"updated-page-setup.{format.ToString().ToLowerInvariant()}");
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(loaded.Session)
            .FindNamedOverride("Persisted output")!;

        Assert.Equal("Persisted output", setup.Name);
        Assert.Equal("Persisted output", setup.PageSetupName);
        Assert.Equal(CadPageTargetSpace.Model, setup.TargetSpace);
        Assert.Equal(420, setup.PaperWidthMillimeters);
        Assert.Equal(297, setup.PaperHeightMillimeters);
        Assert.Equal(CadPageRotation.CounterClockwise270, setup.Rotation);
        Assert.Equal("Updated ProGPU PDF", setup.DeviceName);
        Assert.Equal("ISO_A3", setup.MediaName);
    }

    [Fact]
    public void DeleteNamedPageSetupRetainsObjectAcrossUndoRedo()
    {
        var document = new CadDocument();
        var named = new PlotSettings("Retained output");
        Configure(
            named,
            pageName: named.Name,
            width: 210,
            height: 297,
            rotation: PlotRotation.Degrees90,
            modelType: true);
        CadDictionary dictionary = document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings);
        dictionary.Add(named);
        ulong originalHandle = named.Handle;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadDeleteNamedPageSetupCommand(named.Name);

        ulong deletedGeneration = history.Execute(command);

        Assert.Equal(1UL, deletedGeneration);
        Assert.Same(named, command.DeletedPageSetup);
        Assert.False(dictionary.ContainsKey(named.Name));
        Assert.Null(named.Owner);
        Assert.Equal(0UL, named.Handle);
        Assert.False(document.TryGetCadObject<CadObject>(originalHandle, out _));
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);

        Assert.True(history.TryUndo(out ulong undoGeneration));

        Assert.Equal(2UL, undoGeneration);
        Assert.True(dictionary.TryGetEntry(named.Name, out PlotSettings restored));
        Assert.Same(named, restored);
        Assert.Same(dictionary, restored.Owner);
        Assert.NotEqual(0UL, restored.Handle);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(1, history.RedoCount);

        Assert.True(history.TryRedo(out ulong redoGeneration));

        Assert.Equal(3UL, redoGeneration);
        Assert.False(dictionary.ContainsKey(named.Name));
        Assert.Null(named.Owner);
        Assert.Equal(0UL, named.Handle);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void DeleteNamedPageSetupRejectsAssignedAndMissingSetupsTransactionally()
    {
        var document = new CadDocument();
        var named = new PlotSettings("Assigned output");
        named.Flags |= PlotFlags.ModelType;
        CadDictionary dictionary = document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings);
        dictionary.Add(named);
        document.Layouts[ACadLayout.ModelLayoutName].PageName =
            "ASSIGNED OUTPUT";
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        InvalidOperationException assigned = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadDeleteNamedPageSetupCommand(named.Name)));
        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadDeleteNamedPageSetupCommand("Missing output")));

        Assert.Contains("assigned to layout 'Model'", assigned.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", missing.Message, StringComparison.Ordinal);
        Assert.Same(named, dictionary.GetEntry<PlotSettings>(named.Name));
        Assert.Same(dictionary, named.Owner);
        Assert.NotEqual(0UL, named.Handle);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task DeletedNamedPageSetupStaysAbsentAfterDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        CadDictionary dictionary = document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings);
        dictionary.Add(new PlotSettings("Deleted output"));
        dictionary.Add(new PlotSettings("Retained output"));
        var session = new CadDocumentSession(document);
        new CadDocumentHistory(session).Execute(
            new CadDeleteNamedPageSetupCommand("Deleted output"));
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
            sourceName: $"deleted-page-setup.{format.ToString().ToLowerInvariant()}");
        CadPageSetupCatalog catalog = new CadPageSetupCatalogCompiler()
            .Compile(loaded.Session);

        Assert.Null(catalog.FindNamedOverride("Deleted output"));
        Assert.NotNull(catalog.FindNamedOverride("Retained output"));
    }

    [Fact]
    public void CreateNamedPageSetupCopiesPlotContractAndRetainsIdentityAcrossUndoRedo()
    {
        var document = new CadDocument();
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        Configure(
            model,
            pageName: "Live Model settings",
            width: 420,
            height: 297,
            rotation: PlotRotation.Degrees180,
            modelType: true);
        model.SystemPrinterName = "ProGPU PDF";
        model.PaperSize = "ISO_A3";
        model.PlotOriginX = 7;
        model.PlotOriginY = 9;
        model.PaperImageOrigin = new XY(3, 4);
        model.PaperImageOriginX = 5;
        model.PaperImageOriginY = 6;
        model.PlotViewName = "Output view";
        model.StyleSheet = "monochrome.ctb";
        model.WindowLowerLeftX = 11;
        model.WindowLowerLeftY = 12;
        model.WindowUpperLeftX = 90;
        model.WindowUpperLeftY = 91;
        model.ShadePlotIDHandle = 0x1234;
        PlotState original = PlotState.Capture(model);
        ACadSharp.Tables.BlockRecord block = model.AssociatedBlock;
        CadDictionary dictionary = document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadCreateNamedPageSetupCommand(
            ACadLayout.ModelLayoutName,
            "Archived Model output");

        ulong createdGeneration = history.Execute(command);

        Assert.Equal(1UL, createdGeneration);
        Assert.True(dictionary.TryGetEntry(
            "Archived Model output",
            out PlotSettings created));
        Assert.Same(command.CreatedPageSetup, created);
        Assert.Same(dictionary, created.Owner);
        Assert.NotEqual(0UL, created.Handle);
        Assert.Equal("Archived Model output", created.Name);
        Assert.Equal("Archived Model output", created.PageName);
        Assert.Equal(
            original with { PageName = "Archived Model output" },
            PlotState.Capture(created));
        Assert.Equal(original, PlotState.Capture(model));
        Assert.Same(block, model.AssociatedBlock);

        Assert.True(history.TryUndo(out ulong undoGeneration));

        Assert.Equal(2UL, undoGeneration);
        Assert.False(dictionary.ContainsKey("Archived Model output"));
        Assert.Null(created.Owner);
        Assert.Equal(0UL, created.Handle);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(1, history.RedoCount);

        Assert.True(history.TryRedo(out ulong redoGeneration));

        Assert.Equal(3UL, redoGeneration);
        Assert.True(dictionary.TryGetEntry(
            "Archived Model output",
            out PlotSettings restored));
        Assert.Same(created, restored);
        Assert.Same(dictionary, restored.Owner);
        Assert.NotEqual(0UL, restored.Handle);
        Assert.Equal(original, PlotState.Capture(model));
        Assert.Same(block, model.AssociatedBlock);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void CreateNamedPageSetupRejectsDuplicateAndMissingLayoutTransactionally()
    {
        var document = new CadDocument();
        CadDictionary dictionary = document.RootDictionary
            .GetEntry<CadDictionary>(CadDictionary.AcadPlotSettings);
        dictionary.Add(new PlotSettings("Existing output"));
        int originalCount = dictionary.Count();
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        InvalidOperationException duplicate = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadCreateNamedPageSetupCommand(
                ACadLayout.ModelLayoutName,
                "EXISTING OUTPUT")));
        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadCreateNamedPageSetupCommand(
                "Missing layout",
                "New output")));

        Assert.Contains("already exists", duplicate.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", missing.Message, StringComparison.Ordinal);
        Assert.Equal(originalCount, dictionary.Count());
        Assert.False(dictionary.ContainsKey("New output"));
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void CreateNamedPageSetupDerivesPaperTargetFromLayoutIdentity()
    {
        var document = new CadDocument();
        ACadLayout paper = document.Layouts[ACadLayout.PaperLayoutName];
        Configure(
            paper,
            pageName: "Paper source",
            width: 297,
            height: 210,
            rotation: PlotRotation.NoRotation,
            modelType: true);
        var session = new CadDocumentSession(document);

        new CadDocumentHistory(session).Execute(
            new CadCreateNamedPageSetupCommand(
                ACadLayout.PaperLayoutName,
                "Paper output"));

        CadPageSetupSnapshot created = new CadPageSetupCatalogCompiler()
            .Compile(session)
            .FindNamedOverride("Paper output")!;
        Assert.Equal(CadPageTargetSpace.Paper, created.TargetSpace);
        Assert.Equal("Paper output", created.PageSetupName);
        Assert.Equal(297, created.PaperWidthMillimeters);
        Assert.Equal(210, created.PaperHeightMillimeters);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task CreatedNamedPageSetupSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        ACadLayout model = document.Layouts[ACadLayout.ModelLayoutName];
        Configure(
            model,
            pageName: "Source settings",
            width: 420,
            height: 297,
            rotation: PlotRotation.Degrees270,
            modelType: true);
        model.SystemPrinterName = "ProGPU PDF";
        model.PaperSize = "ISO_A3";
        var session = new CadDocumentSession(document);
        new CadDocumentHistory(session).Execute(
            new CadCreateNamedPageSetupCommand(
                ACadLayout.ModelLayoutName,
                "Persisted Model output"));
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
            sourceName: $"created-page-setup.{format.ToString().ToLowerInvariant()}");
        CadPageSetupSnapshot setup = new CadPageSetupCatalogCompiler()
            .Compile(loaded.Session)
            .FindNamedOverride("Persisted Model output")!;

        Assert.Equal("Persisted Model output", setup.Name);
        Assert.Equal("Persisted Model output", setup.PageSetupName);
        Assert.Equal(CadPageTargetSpace.Model, setup.TargetSpace);
        Assert.Equal(420, setup.PaperWidthMillimeters);
        Assert.Equal(297, setup.PaperHeightMillimeters);
        Assert.Equal(CadPageRotation.CounterClockwise270, setup.Rotation);
        Assert.Equal("ProGPU PDF", setup.DeviceName);
        Assert.Equal("ISO_A3", setup.MediaName);
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

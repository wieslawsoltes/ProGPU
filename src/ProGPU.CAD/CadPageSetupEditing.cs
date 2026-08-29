using ACadSharp;
using ACadSharp.Objects;
using CSMath;

namespace ProGPU.CAD;

/// <summary>
/// Applies one named page setup to an existing layout as a reversible edit.
/// </summary>
/// <remarks>
/// The command copies only the fixed plot-settings contract. Layout identity,
/// block ownership, tab order, limits, extents, UCS, and viewport state remain
/// untouched. Construction is O(N) time and storage for at most 8,192 copied
/// target/source name code units. Apply, Undo, and Redo are O(1) time and
/// retain a fixed number of plot values and existing immutable strings.
/// </remarks>
public sealed class CadApplyNamedPageSetupCommand : CadEditCommand
{
    public const int MaximumNameCodeUnits = 4_096;

    private readonly string _targetLayoutName;
    private readonly string _namedPageSetupName;
    private Layout? _layout;
    private CadPlotSettingsState? _previousState;
    private CadPlotSettingsState? _appliedState;

    public string TargetLayoutName => _targetLayoutName;

    public string NamedPageSetupName => _namedPageSetupName;

    public CadApplyNamedPageSetupCommand(
        string targetLayoutName,
        string namedPageSetupName,
        string description = "Apply named page setup")
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLayoutName);
        ArgumentException.ThrowIfNullOrWhiteSpace(namedPageSetupName);
        if (targetLayoutName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The target layout name exceeds the command ownership budget.",
                nameof(targetLayoutName));
        }
        if (namedPageSetupName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The named page-setup name exceeds the command ownership budget.",
                nameof(namedPageSetupName));
        }
        _targetLayoutName = new string(targetLayoutName.AsSpan());
        _namedPageSetupName = new string(namedPageSetupName.AsSpan());
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (isRedo)
        {
            Layout retainedLayout = GetRetainedLayout(document);
            CadPlotSettingsState redoState = _appliedState ??
                throw new InvalidOperationException(
                    "The page-setup command has not been applied.");
            ApplyTransactional(retainedLayout, redoState);
            return;
        }

        Layout layout = ResolveLayout(document, _targetLayoutName);
        PlotSettings namedPageSetup = ResolveNamedPageSetup(
            document,
            _namedPageSetupName);
        bool layoutTargetsModel = !layout.IsPaperSpace;
        bool setupTargetsModel =
            (namedPageSetup.Flags & PlotFlags.ModelType) != 0;
        if (layoutTargetsModel != setupTargetsModel)
        {
            throw new InvalidOperationException(
                $"Named page setup '{_namedPageSetupName}' targets " +
                $"{(setupTargetsModel ? "model" : "paper")} space and cannot be " +
                $"applied to {(layoutTargetsModel ? "model" : "paper")}-space " +
                $"layout '{_targetLayoutName}'.");
        }

        CadPlotSettingsState previous = CadPlotSettingsState.Capture(layout);
        CadPlotSettingsState appliedState = CadPlotSettingsState.Capture(namedPageSetup)
            .WithModelType(layoutTargetsModel);
        ApplyTransactional(layout, appliedState);
        _layout = layout;
        _previousState = previous;
        _appliedState = appliedState;
    }

    internal override void Revert(CadDocument document)
    {
        Layout layout = GetRetainedLayout(document);
        CadPlotSettingsState previous = _previousState ??
            throw new InvalidOperationException(
                "The page-setup command has not been applied.");
        ApplyTransactional(layout, previous);
    }

    private Layout GetRetainedLayout(CadDocument document)
    {
        Layout layout = _layout ??
            throw new InvalidOperationException(
                "The page-setup command has not been applied.");
        Layout current = ResolveLayout(document, _targetLayoutName);
        if (!ReferenceEquals(layout, current))
        {
            throw new InvalidOperationException(
                $"Layout '{_targetLayoutName}' is no longer the retained layout.");
        }
        return layout;
    }

    private static Layout ResolveLayout(CadDocument document, string name)
    {
        if (document.Layouts is null ||
            !document.Layouts.TryGet(name, out Layout layout))
        {
            throw new InvalidOperationException(
                $"Layout '{name}' does not exist.");
        }
        return layout;
    }

    private static PlotSettings ResolveNamedPageSetup(
        CadDocument document,
        string name)
    {
        if (document.RootDictionary is null ||
            !document.RootDictionary.TryGetEntry(
                CadDictionary.AcadPlotSettings,
                out CadDictionary pageSetups) ||
            !pageSetups.TryGetEntry(name, out PlotSettings pageSetup) ||
            pageSetup is Layout)
        {
            throw new InvalidOperationException(
                $"Named page setup '{name}' does not exist.");
        }
        return pageSetup;
    }

    private static void ApplyTransactional(
        PlotSettings target,
        CadPlotSettingsState desired)
    {
        CadPlotSettingsState rollback = CadPlotSettingsState.Capture(target);
        try
        {
            desired.ApplyTo(target);
        }
        catch (Exception applyException)
        {
            try
            {
                rollback.ApplyTo(target);
            }
            catch (Exception rollbackException)
            {
                throw new InvalidOperationException(
                    "Applying page setup failed and its rollback also failed.",
                    new AggregateException(applyException, rollbackException));
            }
            throw;
        }
    }

}

/// <summary>
/// Creates one named page setup from an existing layout as a reversible edit.
/// </summary>
/// <remarks>
/// The created object owns a fixed copy of the source layout's plot contract
/// and never retains layout geometry. Construction is O(N) time and storage for
/// at most 8,192 source/new-name code units. Apply, Undo, and Redo are O(1) and
/// retain one bounded PLOTSETTINGS object.
/// </remarks>
public sealed class CadCreateNamedPageSetupCommand : CadEditCommand
{
    public const int MaximumNameCodeUnits = 4_096;

    private readonly string _sourceLayoutName;
    private readonly string _newPageSetupName;
    private CadDictionary? _pageSetups;
    private PlotSettings? _createdPageSetup;

    public string SourceLayoutName => _sourceLayoutName;

    public string NewPageSetupName => _newPageSetupName;

    /// <summary>The retained setup after the command is first applied.</summary>
    public PlotSettings? CreatedPageSetup => _createdPageSetup;

    public CadCreateNamedPageSetupCommand(
        string sourceLayoutName,
        string newPageSetupName,
        string description = "Create named page setup")
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLayoutName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPageSetupName);
        if (sourceLayoutName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The source layout name exceeds the command ownership budget.",
                nameof(sourceLayoutName));
        }
        if (newPageSetupName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The new page-setup name exceeds the command ownership budget.",
                nameof(newPageSetupName));
        }
        _sourceLayoutName = new string(sourceLayoutName.AsSpan());
        _newPageSetupName = new string(newPageSetupName.AsSpan());
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        EnsureNameAvailable(pageSetups);

        if (isRedo)
        {
            if (!ReferenceEquals(_pageSetups, pageSetups))
            {
                throw new InvalidOperationException(
                    "The ACAD_PLOTSETTINGS dictionary is no longer the retained dictionary.");
            }
            PlotSettings retained = _createdPageSetup ??
                throw new InvalidOperationException(
                    "The page-setup command has not been applied.");
            if (retained.Owner is not null || retained.Handle != 0)
            {
                throw new InvalidOperationException(
                    $"Named page setup '{_newPageSetupName}' is not detached.");
            }
            AddTransactional(pageSetups, retained);
            return;
        }

        Layout source = ResolveLayout(document, _sourceLayoutName);
        bool modelType = !source.IsPaperSpace;
        var created = new PlotSettings(_newPageSetupName);
        CadPlotSettingsState.Capture(source)
            .WithModelType(modelType)
            .WithPageName(_newPageSetupName)
            .ApplyTo(created);
        AddTransactional(pageSetups, created);
        _pageSetups = pageSetups;
        _createdPageSetup = created;
    }

    internal override void Revert(CadDocument document)
    {
        CadDictionary pageSetups = ResolvePageSetupDictionary(document);
        if (!ReferenceEquals(_pageSetups, pageSetups))
        {
            throw new InvalidOperationException(
                "The ACAD_PLOTSETTINGS dictionary is no longer the retained dictionary.");
        }
        PlotSettings created = _createdPageSetup ??
            throw new InvalidOperationException(
                "The page-setup command has not been applied.");
        if (!pageSetups.TryGetEntry(_newPageSetupName, out PlotSettings current) ||
            !ReferenceEquals(created, current))
        {
            throw new InvalidOperationException(
                $"Named page setup '{_newPageSetupName}' is no longer the retained setup.");
        }
        if (!pageSetups.Remove(_newPageSetupName, out NonGraphicalObject removed) ||
            !ReferenceEquals(created, removed))
        {
            throw new InvalidOperationException(
                $"Named page setup '{_newPageSetupName}' could not be removed.");
        }
    }

    private void EnsureNameAvailable(CadDictionary pageSetups)
    {
        if (pageSetups.ContainsKey(_newPageSetupName))
        {
            throw new InvalidOperationException(
                $"Named page setup '{_newPageSetupName}' already exists.");
        }
    }

    private static void AddTransactional(
        CadDictionary pageSetups,
        PlotSettings pageSetup)
    {
        try
        {
            pageSetups.Add(pageSetup);
        }
        catch
        {
            if (pageSetups.TryGetEntry(pageSetup.Name, out PlotSettings current) &&
                ReferenceEquals(pageSetup, current))
            {
                pageSetups.Remove(pageSetup.Name);
            }
            throw;
        }
    }

    private static Layout ResolveLayout(CadDocument document, string name)
    {
        if (document.Layouts is null ||
            !document.Layouts.TryGet(name, out Layout layout))
        {
            throw new InvalidOperationException(
                $"Layout '{name}' does not exist.");
        }
        return layout;
    }

    private static CadDictionary ResolvePageSetupDictionary(CadDocument document)
    {
        if (document.RootDictionary is null ||
            !document.RootDictionary.TryGetEntry(
                CadDictionary.AcadPlotSettings,
                out CadDictionary pageSetups))
        {
            throw new InvalidOperationException(
                "The document has no ACAD_PLOTSETTINGS dictionary.");
        }
        return pageSetups;
    }
}

internal readonly record struct CadPlotSettingsState(
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
    public static CadPlotSettingsState Capture(PlotSettings source) => new(
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

    public CadPlotSettingsState WithModelType(bool modelType) => this with
    {
        Flags = modelType
            ? Flags | PlotFlags.ModelType
            : Flags & ~PlotFlags.ModelType,
    };

    public CadPlotSettingsState WithPageName(string pageName) => this with
    {
        PageName = pageName,
    };

    public void ApplyTo(PlotSettings target)
    {
        target.DenominatorScale = DenominatorScale;
        target.Flags = Flags;
        target.NumeratorScale = NumeratorScale;
        target.PageName = PageName!;
        target.PaperHeight = PaperHeight;
        target.PaperImageOrigin = PaperImageOrigin;
        target.PaperImageOriginX = PaperImageOriginX;
        target.PaperImageOriginY = PaperImageOriginY;
        target.PaperRotation = PaperRotation;
        target.PaperSize = PaperSize!;
        target.PaperUnits = PaperUnits;
        target.PaperWidth = PaperWidth;
        target.PlotOriginX = PlotOriginX;
        target.PlotOriginY = PlotOriginY;
        target.PlotType = PlotType;
        target.PlotViewName = PlotViewName!;
        target.ScaledFit = ScaledFit;
        target.ShadePlotDPI = ShadePlotDpi;
        target.ShadePlotIDHandle = ShadePlotIdHandle;
        target.ShadePlotMode = ShadePlotMode;
        target.ShadePlotResolutionMode = ShadePlotResolutionMode;
        target.StandardScale = StandardScale;
        target.StyleSheet = StyleSheet!;
        target.SystemPrinterName = SystemPrinterName!;
        target.UnprintableMargin = UnprintableMargin;
        target.WindowLowerLeftX = WindowLowerLeftX;
        target.WindowLowerLeftY = WindowLowerLeftY;
        target.WindowUpperLeftX = WindowUpperLeftX;
        target.WindowUpperLeftY = WindowUpperLeftY;
    }
}

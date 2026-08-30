using ACadSharp;
using ACadSharp.Objects;

namespace ProGPU.CAD;

public enum CadPageSetupSourceKind : byte
{
    Layout = 0,
    NamedOverride = 1,
}

public enum CadPageTargetSpace : byte
{
    Model = 0,
    Paper = 1,
}

public enum CadPageUnit : byte
{
    Inches = 0,
    Millimeters = 1,
    DevicePixels = 2,
    Unknown = byte.MaxValue,
}

public enum CadPageRotation : byte
{
    Degrees0 = 0,
    CounterClockwise90 = 1,
    Degrees180 = 2,
    CounterClockwise270 = 3,
    Unknown = byte.MaxValue,
}

public enum CadPlotAreaKind : byte
{
    Display = 0,
    Extents = 1,
    Limits = 2,
    NamedView = 3,
    Window = 4,
    Layout = 5,
    Unknown = byte.MaxValue,
}

public enum CadShadeOutputKind : byte
{
    CurrentDisplay = 0,
    Wireframe = 1,
    Hidden = 2,
    Rendered = 3,
    Unknown = byte.MaxValue,
}

public enum CadShadeResolutionKind : byte
{
    Draft = 0,
    Preview = 1,
    Normal = 2,
    Presentation = 3,
    Maximum = 4,
    Custom = 5,
    Unknown = byte.MaxValue,
}

public readonly record struct CadPageMargins(
    double LeftMillimeters,
    double BottomMillimeters,
    double RightMillimeters,
    double TopMillimeters);

/// <summary>A detached two-dimensional source rectangle.</summary>
public readonly record struct CadPlotRectangle(
    double MinimumX,
    double MinimumY,
    double MaximumX,
    double MaximumY)
{
    public bool IsFiniteAndOrdered =>
        double.IsFinite(MinimumX) &&
        double.IsFinite(MinimumY) &&
        double.IsFinite(MaximumX) &&
        double.IsFinite(MaximumY) &&
        MinimumX <= MaximumX &&
        MinimumY <= MaximumY;
}

/// <summary>
/// An immutable, generation-tagged copy of one layout or named page setup.
/// </summary>
/// <remarks>
/// This type owns all strings and exposes no ACadSharp object. Plot-window values
/// remain source coordinate values; they are not mislabeled as WCS coordinates.
/// </remarks>
public sealed class CadPageSetupSnapshot
{
    public ulong ContentGeneration { get; internal init; }

    public CadPageSetupSourceKind SourceKind { get; internal init; }

    public CadPageTargetSpace TargetSpace { get; internal init; }

    public string Name { get; internal init; } = string.Empty;

    public string PageSetupName { get; internal init; } = string.Empty;

    public string DeviceName { get; internal init; } = string.Empty;

    public string MediaName { get; internal init; } = string.Empty;

    public string NamedView { get; internal init; } = string.Empty;

    public string StyleSheet { get; internal init; } = string.Empty;

    public int TabOrder { get; internal init; }

    public double PaperWidthMillimeters { get; internal init; }

    public double PaperHeightMillimeters { get; internal init; }

    public CadPageMargins UnprintableMargins { get; internal init; }

    public double PlotOriginXMillimeters { get; internal init; }

    public double PlotOriginYMillimeters { get; internal init; }

    public double PaperImageOriginX { get; internal init; }

    public double PaperImageOriginY { get; internal init; }

    public CadPageUnit PaperUnit { get; internal init; }

    public CadPageRotation Rotation { get; internal init; }

    public CadPlotAreaKind PlotArea { get; internal init; }

    public CadPlotRectangle PlotWindow { get; internal init; }

    public CadPlotRectangle LayoutLimits { get; internal init; }

    public CadPlotRectangle LayoutExtents { get; internal init; }

    public bool HasLayoutGeometry { get; internal init; }

    public bool CenterPlot { get; internal init; }

    public bool UseStandardScale { get; internal init; }

    public int StandardScaleCode { get; internal init; }

    public double StandardScaleFactor { get; internal init; }

    public double PaperUnitsNumerator { get; internal init; }

    public double DrawingUnitsDenominator { get; internal init; }

    public bool PrintLineweights { get; internal init; }

    public bool ScaleLineweights { get; internal init; }

    public bool ApplyPlotStyles { get; internal init; }

    public bool ShowPlotStyles { get; internal init; }

    public bool RemoveHiddenLines { get; internal init; }

    public bool PlotViewportBorders { get; internal init; }

    public bool DrawViewportsFirst { get; internal init; }

    public CadShadeOutputKind ShadeOutput { get; internal init; }

    public CadShadeResolutionKind ShadeResolution { get; internal init; }

    public short ShadeDpi { get; internal init; }

    private CadPageSetupSnapshot()
    {
    }

    internal static CadPageSetupSnapshot Create(
        PlotSettings source,
        ulong contentGeneration,
        CadPageSetupSourceKind sourceKind,
        CadPageTargetSpace targetSpace,
        int tabOrder,
        CadPlotRectangle layoutLimits,
        CadPlotRectangle layoutExtents,
        bool hasLayoutGeometry,
        Func<string?, string> copyText)
    {
        PlotFlags flags = source.Flags;
        PaperMargin margins = source.UnprintableMargin;
        return new CadPageSetupSnapshot
        {
            ContentGeneration = contentGeneration,
            SourceKind = sourceKind,
            TargetSpace = targetSpace,
            Name = copyText(source.Name),
            PageSetupName = copyText(source.PageName),
            DeviceName = copyText(source.SystemPrinterName),
            MediaName = copyText(source.PaperSize),
            NamedView = copyText(source.PlotViewName),
            StyleSheet = copyText(source.StyleSheet),
            TabOrder = tabOrder,
            PaperWidthMillimeters = source.PaperWidth,
            PaperHeightMillimeters = source.PaperHeight,
            UnprintableMargins = new CadPageMargins(
                margins.Left,
                margins.Bottom,
                margins.Right,
                margins.Top),
            PlotOriginXMillimeters = source.PlotOriginX,
            PlotOriginYMillimeters = source.PlotOriginY,
            PaperImageOriginX = source.PaperImageOrigin.X,
            PaperImageOriginY = source.PaperImageOrigin.Y,
            PaperUnit = MapPaperUnit(source.PaperUnits),
            Rotation = MapRotation(source.PaperRotation),
            PlotArea = MapPlotArea(source.PlotType),
            PlotWindow = new CadPlotRectangle(
                source.WindowLowerLeftX,
                source.WindowLowerLeftY,
                source.WindowUpperLeftX,
                source.WindowUpperLeftY),
            LayoutLimits = layoutLimits,
            LayoutExtents = layoutExtents,
            HasLayoutGeometry = hasLayoutGeometry,
            CenterPlot = (flags & PlotFlags.PlotCentered) != 0,
            UseStandardScale = (flags & PlotFlags.UseStandardScale) != 0,
            StandardScaleCode = (int)source.ScaledFit,
            StandardScaleFactor = source.StandardScale,
            PaperUnitsNumerator = source.NumeratorScale,
            DrawingUnitsDenominator = source.DenominatorScale,
            PrintLineweights = (flags & PlotFlags.PrintLineweights) != 0,
            ScaleLineweights = (flags & PlotFlags.ScaleLineweights) != 0,
            ApplyPlotStyles = (flags & PlotFlags.PlotPlotStyles) != 0,
            ShowPlotStyles = (flags & PlotFlags.ShowPlotStyles) != 0,
            RemoveHiddenLines = (flags & PlotFlags.PlotHidden) != 0,
            PlotViewportBorders = (flags & PlotFlags.PlotViewportBorders) != 0,
            DrawViewportsFirst = (flags & PlotFlags.DrawViewportsFirst) != 0,
            ShadeOutput = MapShadeOutput(source.ShadePlotMode),
            ShadeResolution = MapShadeResolution(source.ShadePlotResolutionMode),
            ShadeDpi = source.ShadePlotDPI,
        };
    }

    private static CadPageUnit MapPaperUnit(PlotPaperUnits value) => value switch
    {
        PlotPaperUnits.Inches => CadPageUnit.Inches,
        PlotPaperUnits.Millimeters => CadPageUnit.Millimeters,
        PlotPaperUnits.Pixels => CadPageUnit.DevicePixels,
        _ => CadPageUnit.Unknown,
    };

    private static CadPageRotation MapRotation(PlotRotation value) => value switch
    {
        PlotRotation.NoRotation => CadPageRotation.Degrees0,
        PlotRotation.Degrees90 => CadPageRotation.CounterClockwise90,
        PlotRotation.Degrees180 => CadPageRotation.Degrees180,
        PlotRotation.Degrees270 => CadPageRotation.CounterClockwise270,
        _ => CadPageRotation.Unknown,
    };

    private static CadPlotAreaKind MapPlotArea(PlotType value) => value switch
    {
        PlotType.LastScreenDisplay => CadPlotAreaKind.Display,
        PlotType.DrawingExtents => CadPlotAreaKind.Extents,
        PlotType.DrawingLimits => CadPlotAreaKind.Limits,
        PlotType.View => CadPlotAreaKind.NamedView,
        PlotType.Window => CadPlotAreaKind.Window,
        PlotType.LayoutInformation => CadPlotAreaKind.Layout,
        _ => CadPlotAreaKind.Unknown,
    };

    private static CadShadeOutputKind MapShadeOutput(ShadePlotMode value) => value switch
    {
        ShadePlotMode.AsDisplayed => CadShadeOutputKind.CurrentDisplay,
        ShadePlotMode.Wireframe => CadShadeOutputKind.Wireframe,
        ShadePlotMode.Hidden => CadShadeOutputKind.Hidden,
        ShadePlotMode.Rendered => CadShadeOutputKind.Rendered,
        _ => CadShadeOutputKind.Unknown,
    };

    private static CadShadeResolutionKind MapShadeResolution(
        ShadePlotResolutionMode value) => value switch
        {
            ShadePlotResolutionMode.Draft => CadShadeResolutionKind.Draft,
            ShadePlotResolutionMode.Preview => CadShadeResolutionKind.Preview,
            ShadePlotResolutionMode.Normal => CadShadeResolutionKind.Normal,
            ShadePlotResolutionMode.Presentation => CadShadeResolutionKind.Presentation,
            ShadePlotResolutionMode.Maximum => CadShadeResolutionKind.Maximum,
            ShadePlotResolutionMode.Custom => CadShadeResolutionKind.Custom,
            _ => CadShadeResolutionKind.Unknown,
        };
}

public sealed class CadPageSetupCatalogOptions
{
    public int MaxSetups { get; init; } = 256;

    public int MaxCodeUnitsPerString { get; init; } = 4_096;

    public int MaxTotalStringCodeUnits { get; init; } = 65_536;

    public int DiagnosticLimit { get; init; } = 256;
}

/// <summary>An immutable collection of layout and named page-setup state.</summary>
public sealed class CadPageSetupCatalog
{
    private readonly CadPageSetupSnapshot[] _setups;
    private readonly CadDiagnostic[] _diagnostics;

    public ulong ContentGeneration { get; }

    public ReadOnlyMemory<CadPageSetupSnapshot> Setups => _setups;

    public ReadOnlyMemory<CadDiagnostic> Diagnostics => _diagnostics;

    internal CadPageSetupCatalog(
        ulong contentGeneration,
        CadPageSetupSnapshot[] setups,
        CadDiagnostic[] diagnostics)
    {
        ContentGeneration = contentGeneration;
        _setups = setups;
        _diagnostics = diagnostics;
    }

    public CadPageSetupSnapshot? FindLayout(string name) =>
        Find(name, CadPageSetupSourceKind.Layout);

    public CadPageSetupSnapshot? FindNamedOverride(string name) =>
        Find(name, CadPageSetupSourceKind.NamedOverride);

    private CadPageSetupSnapshot? Find(
        string name,
        CadPageSetupSourceKind sourceKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (CadPageSetupSnapshot setup in _setups)
        {
            if (setup.SourceKind == sourceKind &&
                string.Equals(setup.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return setup;
            }
        }

        return null;
    }
}

/// <summary>
/// Copies ACadSharp layout/page-setup objects into bounded ProGPU-owned state.
/// </summary>
/// <remarks>
/// Compilation is O(L log L + S) time and O(L + S) storage for L setups and S
/// copied UTF-16 code units. The mutable document is held only by the session's
/// synchronous capture callback.
/// </remarks>
public sealed class CadPageSetupCatalogCompiler
{
    public CadPageSetupCatalog Compile(
        CadDocumentSession session,
        CadPageSetupCatalogOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new CadPageSetupCatalogOptions();
        ValidateOptions(options);

        return session.Capture((document, generation) =>
            CompileCore(document, generation, options, cancellationToken));
    }

    private static CadPageSetupCatalog CompileCore(
        CadDocument document,
        ulong contentGeneration,
        CadPageSetupCatalogOptions options,
        CancellationToken cancellationToken)
    {
        var setups = new List<CadPageSetupSnapshot>(4);
        var diagnostics = new List<CadDiagnostic>(Math.Min(options.DiagnosticLimit, 16));
        int totalStringCodeUnits = 0;

        string CopyText(string? source)
        {
            source ??= string.Empty;
            if (source.Length > options.MaxCodeUnitsPerString ||
                totalStringCodeUnits > options.MaxTotalStringCodeUnits - source.Length)
            {
                throw new InvalidDataException(
                    "CAD page-setup strings exceed the configured ownership budget.");
            }

            totalStringCodeUnits += source.Length;
            return new string(source.AsSpan());
        }

        if (document.Layouts is null)
        {
            AddDiagnostic(
                diagnostics,
                options.DiagnosticLimit,
                new CadDiagnostic(
                    CadDiagnosticSeverity.Warning,
                    "CADPAGE001",
                    "The document has no ACAD_LAYOUT dictionary."));
        }
        else
        {
            foreach (Layout layout in document.Layouts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureCapacity(setups.Count, options.MaxSetups);
                setups.Add(CadPageSetupSnapshot.Create(
                    layout,
                    contentGeneration,
                    CadPageSetupSourceKind.Layout,
                    layout.IsPaperSpace
                        ? CadPageTargetSpace.Paper
                        : CadPageTargetSpace.Model,
                    layout.TabOrder,
                    new CadPlotRectangle(
                        layout.MinLimits.X,
                        layout.MinLimits.Y,
                        layout.MaxLimits.X,
                        layout.MaxLimits.Y),
                    new CadPlotRectangle(
                        layout.MinExtents.X,
                        layout.MinExtents.Y,
                        layout.MaxExtents.X,
                        layout.MaxExtents.Y),
                    hasLayoutGeometry: true,
                    CopyText));
            }
        }

        if (document.RootDictionary is not null &&
            document.RootDictionary.TryGetEntry(
                CadDictionary.AcadPlotSettings,
                out CadDictionary pageSetupDictionary))
        {
            foreach (NonGraphicalObject entry in pageSetupDictionary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is not PlotSettings pageSetup || entry is Layout)
                {
                    AddDiagnostic(
                        diagnostics,
                        options.DiagnosticLimit,
                        new CadDiagnostic(
                            CadDiagnosticSeverity.Warning,
                            "CADPAGE002",
                            $"Named page-setup entry '{entry.Name}' is not a PLOTSETTINGS object."));
                    continue;
                }

                EnsureCapacity(setups.Count, options.MaxSetups);
                setups.Add(CadPageSetupSnapshot.Create(
                    pageSetup,
                    contentGeneration,
                    CadPageSetupSourceKind.NamedOverride,
                    (pageSetup.Flags & PlotFlags.ModelType) != 0
                        ? CadPageTargetSpace.Model
                        : CadPageTargetSpace.Paper,
                    tabOrder: -1,
                    default,
                    default,
                    hasLayoutGeometry: false,
                    CopyText));
            }
        }

        setups.Sort(PageSetupComparer.Instance);
        return new CadPageSetupCatalog(
            contentGeneration,
            setups.ToArray(),
            diagnostics.ToArray());
    }

    private static void EnsureCapacity(int currentCount, int maximum)
    {
        if (currentCount >= maximum)
        {
            throw new InvalidDataException(
                "The document exceeds the configured layout/page-setup count budget.");
        }
    }

    private static void AddDiagnostic(
        List<CadDiagnostic> diagnostics,
        int limit,
        CadDiagnostic diagnostic)
    {
        if (diagnostics.Count < limit)
        {
            diagnostics.Add(diagnostic);
        }
    }

    private static void ValidateOptions(CadPageSetupCatalogOptions options)
    {
        if (options.MaxSetups <= 0 ||
            options.MaxCodeUnitsPerString <= 0 ||
            options.MaxTotalStringCodeUnits <= 0 ||
            options.DiagnosticLimit < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Page-setup count, string, and diagnostic budgets must be valid.");
        }
    }

    private sealed class PageSetupComparer : IComparer<CadPageSetupSnapshot>
    {
        public static PageSetupComparer Instance { get; } = new();

        public int Compare(CadPageSetupSnapshot? left, CadPageSetupSnapshot? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left is null)
            {
                return -1;
            }
            if (right is null)
            {
                return 1;
            }

            int sourceOrder = left.SourceKind.CompareTo(right.SourceKind);
            if (sourceOrder != 0)
            {
                return sourceOrder;
            }

            if (left.SourceKind == CadPageSetupSourceKind.Layout)
            {
                int targetOrder = left.TargetSpace.CompareTo(right.TargetSpace);
                if (targetOrder != 0)
                {
                    return targetOrder;
                }

                int tabOrder = left.TabOrder.CompareTo(right.TabOrder);
                if (tabOrder != 0)
                {
                    return tabOrder;
                }
            }

            return StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        }
    }
}

public sealed class CadPageSetupPrintOptionsCompilerOptions
{
    public float OutputDpi { get; init; } = 300.0f;

    public float LineWeightScale { get; init; } = 1.0f;

    /// <summary>
    /// Resolves a page setup that disables assigned object/layer lineweights.
    /// Reject is the fidelity-safe default because the thinnest printable width
    /// belongs to the selected output device.
    /// </summary>
    public CadDisabledLineWeightPolicy DisabledLineWeightPolicy { get; init; }

    public long MaxPagePixelCount { get; init; } =
        CadPrintPlanOptions.DefaultMaxPagePixelCount;
}

public enum CadDisabledLineWeightPolicy : byte
{
    Reject = 0,
    DeviceHairline = 1,
}

/// <summary>
/// The result of applying one page setup to the currently supported print-plan
/// contract. Unsupported policies are returned as explicit error diagnostics.
/// </summary>
public sealed class CadPageSetupPrintOptionsResult
{
    private readonly CadDiagnostic[] _diagnostics;

    public ulong ContentGeneration { get; }

    public string PageSetupName { get; }

    public CadPrintPlanOptions? PrintOptions { get; }

    public bool IsSupported => PrintOptions is not null;

    public ReadOnlyMemory<CadDiagnostic> Diagnostics => _diagnostics;

    internal CadPageSetupPrintOptionsResult(
        ulong contentGeneration,
        string pageSetupName,
        CadPrintPlanOptions? printOptions,
        CadDiagnostic[] diagnostics)
    {
        ContentGeneration = contentGeneration;
        PageSetupName = pageSetupName;
        PrintOptions = printOptions;
        _diagnostics = diagnostics;
    }
}

/// <summary>
/// Lowers a fidelity-complete model-space setup or a physical inch/millimeter
/// 1:1 paper-layout setup into retained print options.
/// </summary>
public sealed class CadPageSetupPrintOptionsCompiler
{
    private const double MillimetersPerInch = 25.4;

    public CadPageSetupPrintOptionsResult Compile(
        CadPageSetupSnapshot pageSetup,
        CadPageSetupPrintOptionsCompilerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageSetup);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new CadPageSetupPrintOptionsCompilerOptions();
        ValidateOptions(options);

        var diagnostics = new List<CadDiagnostic>(8);
        bool isPaperLayout = pageSetup.TargetSpace == CadPageTargetSpace.Paper;
        if (pageSetup.TargetSpace is not (CadPageTargetSpace.Model or CadPageTargetSpace.Paper))
        {
            AddError(
                diagnostics,
                "CADPAGE101",
                "The page setup contains an unknown target-space value.");
        }

        if (pageSetup.Rotation == CadPageRotation.Unknown)
        {
            AddError(
                diagnostics,
                "CADPAGE102",
                "The source page setup contains an unknown page-rotation value.");
        }

        if (pageSetup.PaperUnit is CadPageUnit.DevicePixels or CadPageUnit.Unknown)
        {
            AddError(
                diagnostics,
                "CADPAGE103",
                "Pixel or unknown paper units require a raster-device scale contract.");
        }

        if (isPaperLayout && pageSetup.PlotArea != CadPlotAreaKind.Layout)
        {
            AddError(
                diagnostics,
                "CADPAGE119",
                "The retained paper-space print contract currently requires PlotType Layout.");
        }
        else switch (pageSetup.PlotArea)
        {
            case CadPlotAreaKind.Extents:
                break;
            case CadPlotAreaKind.Window:
                AddError(
                    diagnostics,
                    "CADPAGE104",
                    "Stored plot-window coordinates are DCS values and require the matching saved view transform before WCS lowering.");
                break;
            case CadPlotAreaKind.Limits:
                if (!pageSetup.HasLayoutGeometry ||
                    !pageSetup.LayoutLimits.IsFiniteAndOrdered)
                {
                    AddError(
                        diagnostics,
                        "CADPAGE105",
                        "Drawing limits require finite WCS limits from a layout-owned page setup.");
                }
                break;
            case CadPlotAreaKind.Display:
                AddError(
                    diagnostics,
                    "CADPAGE106",
                    "Display plotting requires an explicitly captured model-space view.");
                break;
            case CadPlotAreaKind.NamedView:
                AddError(
                    diagnostics,
                    "CADPAGE107",
                    "Named-view plotting requires typed view-table and DCS camera lowering.");
                break;
            case CadPlotAreaKind.Layout:
                if (!isPaperLayout)
                {
                    AddError(
                        diagnostics,
                        "CADPAGE108",
                        "Model-space output cannot use the paper-layout plot area.");
                }
                break;
            default:
                AddError(
                    diagnostics,
                    "CADPAGE109",
                    "The source page setup contains an unknown plot-area value.");
                break;
        }

        if (pageSetup.ShadeOutput != CadShadeOutputKind.Wireframe)
        {
            AddError(
                diagnostics,
                "CADPAGE110",
                "Only an explicit wireframe shade policy matches the current retained plan-scene contract.");
        }
        if (pageSetup.RemoveHiddenLines)
        {
            AddError(
                diagnostics,
                "CADPAGE111",
                "Hidden-line removal requires a depth-aware print compiler.");
        }
        CadPrintLineWeightMode lineWeightMode =
            CadPrintLineWeightMode.ObjectLineWeights;
        if (!pageSetup.PrintLineweights &&
            options.DisabledLineWeightPolicy ==
                CadDisabledLineWeightPolicy.Reject)
        {
            AddError(
                diagnostics,
                "CADPAGE112",
                "Disabled object lineweights require an explicit output-device hairline policy.");
        }
        else if (!pageSetup.PrintLineweights)
        {
            lineWeightMode = CadPrintLineWeightMode.DeviceHairline;
        }
        if (pageSetup.ScaleLineweights && !isPaperLayout)
        {
            AddError(
                diagnostics,
                "CADPAGE113",
                "Scale-lineweights applies only to paper layouts; AutoCAD disables it for model-space output.");
        }
        if (pageSetup.ApplyPlotStyles && HasAppliedStyleSheet(pageSetup.StyleSheet))
        {
            AddError(
                diagnostics,
                "CADPAGE114",
                $"Plot style sheet '{pageSetup.StyleSheet}' requires CTB/STB resolution before output.");
        }

        ValidatePaper(pageSetup, diagnostics);

        double standardScaleFactor = double.NaN;
        if (!isPaperLayout &&
            pageSetup.UseStandardScale &&
            pageSetup.StandardScaleCode != 0)
        {
            if (!TryGetStandardScaleFactor(
                    pageSetup.StandardScaleCode,
                    out standardScaleFactor))
            {
                AddError(
                    diagnostics,
                    "CADPAGE115",
                    "The source page setup contains an unknown standard-scale selection.");
            }
            else if (!MatchesStandardScaleFactor(
                         pageSetup.StandardScaleFactor,
                         standardScaleFactor))
            {
                AddError(
                    diagnostics,
                    "CADPAGE122",
                    "The stored standard-scale factor does not match the selected standard-scale code.");
            }
        }

        CadPrintScaleMode scaleMode = CadPrintScaleMode.FitToPrintableArea;
        double modelUnitsPerMillimeter = 1.0;
        if (isPaperLayout)
        {
            modelUnitsPerMillimeter = pageSetup.PaperUnit switch
            {
                CadPageUnit.Inches => 1.0 / MillimetersPerInch,
                _ => 1.0,
            };
            if (pageSetup.CenterPlot)
            {
                AddError(
                    diagnostics,
                    "CADPAGE121",
                    "Centered paper-layout plotting requires a separate device-origin contract.");
            }
            scaleMode = CadPrintScaleMode.ModelUnitsPerMillimeter;
        }
        else if (!(pageSetup.UseStandardScale && pageSetup.StandardScaleCode == 0))
        {
            double paperUnitMillimeters = pageSetup.PaperUnit switch
            {
                CadPageUnit.Inches => MillimetersPerInch,
                CadPageUnit.Millimeters => 1.0,
                _ => double.NaN,
            };
            bool hasResolvedScale = !pageSetup.UseStandardScale ||
                double.IsFinite(standardScaleFactor);
            if (hasResolvedScale)
            {
                modelUnitsPerMillimeter = pageSetup.UseStandardScale
                    ? 1.0 / (standardScaleFactor * paperUnitMillimeters)
                    : pageSetup.DrawingUnitsDenominator /
                        (pageSetup.PaperUnitsNumerator * paperUnitMillimeters);
                if (!double.IsFinite(modelUnitsPerMillimeter) ||
                    modelUnitsPerMillimeter <= 0.0)
                {
                    AddError(
                        diagnostics,
                        "CADPAGE116",
                        "The resolved print scale is not finite and positive.");
                }
            }

            scaleMode = CadPrintScaleMode.ModelUnitsPerMillimeter;
        }

        if (diagnostics.Count != 0)
        {
            return new CadPageSetupPrintOptionsResult(
                pageSetup.ContentGeneration,
                pageSetup.Name,
                printOptions: null,
                diagnostics.ToArray());
        }

        CadPageMargins margins = pageSetup.UnprintableMargins;
        CadBounds3D? plotBounds = pageSetup.PlotArea == CadPlotAreaKind.Limits
            ? new CadBounds3D(
                new CadPoint3D(
                    pageSetup.LayoutLimits.MinimumX,
                    pageSetup.LayoutLimits.MinimumY,
                    0.0),
                new CadPoint3D(
                    pageSetup.LayoutLimits.MaximumX,
                    pageSetup.LayoutLimits.MaximumY,
                    0.0))
            : null;
        var printOptions = new CadPrintPlanOptions
        {
            SourcePageSetupName = pageSetup.Name,
            PaperWidthMillimeters = pageSetup.PaperWidthMillimeters,
            PaperHeightMillimeters = pageSetup.PaperHeightMillimeters,
            MarginLeftMillimeters = margins.LeftMillimeters,
            MarginTopMillimeters = margins.TopMillimeters,
            MarginRightMillimeters = margins.RightMillimeters,
            MarginBottomMillimeters = margins.BottomMillimeters,
            Rotation = pageSetup.Rotation,
            OutputDpi = options.OutputDpi,
            PlotBounds = plotBounds,
            ScaleMode = scaleMode,
            ModelUnitsPerMillimeter = modelUnitsPerMillimeter,
            PlacementMode = pageSetup.CenterPlot
                ? CadPrintPlacementMode.Centered
                : CadPrintPlacementMode.PrintableAreaOffset,
            PlotOffsetXMillimeters = pageSetup.PlotOriginXMillimeters,
            PlotOffsetYMillimeters = pageSetup.PlotOriginYMillimeters,
            LineWeightScale = options.LineWeightScale,
            LineWeightMode = lineWeightMode,
            MaxPagePixelCount = options.MaxPagePixelCount,
        };
        return new CadPageSetupPrintOptionsResult(
            pageSetup.ContentGeneration,
            pageSetup.Name,
            printOptions,
            Array.Empty<CadDiagnostic>());
    }

    private static void ValidatePaper(
        CadPageSetupSnapshot pageSetup,
        List<CadDiagnostic> diagnostics)
    {
        CadPageMargins margins = pageSetup.UnprintableMargins;
        bool valid = IsFinitePositive(pageSetup.PaperWidthMillimeters) &&
            IsFinitePositive(pageSetup.PaperHeightMillimeters) &&
            IsFiniteNonNegative(margins.LeftMillimeters) &&
            IsFiniteNonNegative(margins.BottomMillimeters) &&
            IsFiniteNonNegative(margins.RightMillimeters) &&
            IsFiniteNonNegative(margins.TopMillimeters) &&
            margins.LeftMillimeters + margins.RightMillimeters <
                pageSetup.PaperWidthMillimeters &&
            margins.TopMillimeters + margins.BottomMillimeters <
                pageSetup.PaperHeightMillimeters &&
            double.IsFinite(pageSetup.PlotOriginXMillimeters) &&
            double.IsFinite(pageSetup.PlotOriginYMillimeters);
        if (!valid)
        {
            AddError(
                diagnostics,
                "CADPAGE117",
                "Paper dimensions, margins, and plot origin must describe a finite positive printable area.");
        }
    }

    private static bool HasAppliedStyleSheet(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "None", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0.0;

    private static bool IsFiniteNonNegative(double value) =>
        double.IsFinite(value) && value >= 0.0;

    private static bool MatchesStandardScaleFactor(
        double storedFactor,
        double expectedFactor)
    {
        if (!double.IsFinite(storedFactor))
        {
            return false;
        }

        double tolerance = Math.Max(1e-15, Math.Abs(expectedFactor) * 1e-12);
        return Math.Abs(storedFactor - expectedFactor) <= tolerance;
    }

    private static bool TryGetStandardScaleFactor(int code, out double factor)
    {
        factor = code switch
        {
            1 => 1.0 / 1_536.0,
            2 => 1.0 / 768.0,
            3 => 1.0 / 384.0,
            4 => 1.0 / 192.0,
            5 => 1.0 / 128.0,
            6 => 1.0 / 96.0,
            7 => 1.0 / 64.0,
            8 => 1.0 / 48.0,
            9 => 1.0 / 32.0,
            10 => 1.0 / 24.0,
            11 => 1.0 / 16.0,
            12 => 1.0 / 12.0,
            13 => 1.0 / 4.0,
            14 => 1.0 / 2.0,
            15 or 16 => 1.0,
            17 => 1.0 / 2.0,
            18 => 1.0 / 4.0,
            19 => 1.0 / 8.0,
            20 => 1.0 / 10.0,
            21 => 1.0 / 16.0,
            22 => 1.0 / 20.0,
            23 => 1.0 / 30.0,
            24 => 1.0 / 40.0,
            25 => 1.0 / 50.0,
            26 => 1.0 / 100.0,
            27 => 2.0,
            28 => 4.0,
            29 => 8.0,
            30 => 10.0,
            31 => 100.0,
            32 => 1_000.0,
            _ => double.NaN,
        };
        return double.IsFinite(factor);
    }

    private static void AddError(
        List<CadDiagnostic> diagnostics,
        string code,
        string message) =>
        diagnostics.Add(new CadDiagnostic(CadDiagnosticSeverity.Error, code, message));

    private static void ValidateOptions(CadPageSetupPrintOptionsCompilerOptions options)
    {
        if (!float.IsFinite(options.OutputDpi) || options.OutputDpi <= 0.0f ||
            !float.IsFinite(options.LineWeightScale) || options.LineWeightScale <= 0.0f ||
            !Enum.IsDefined(options.DisabledLineWeightPolicy) ||
            options.MaxPagePixelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Output DPI, lineweight scale, and page-pixel budget must be positive.");
        }
    }
}

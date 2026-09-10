using ACadSharp;
using ACadSharp.Objects;
using CSMath;

namespace ProGPU.CAD;

/// <summary>
/// A bounded, init-only set of persisted plot-setting fields to
/// replace on one layout or named page setup.
/// </summary>
/// <remarks>
/// A null property means "leave the persisted value unchanged". String values
/// may be empty and are copied by <see cref="CadEditPageSetupFieldsCommand"/>.
/// Page-setup identity, target-space kind, object ownership, handles, layout
/// geometry, and viewport state are deliberately outside this contract.
/// </remarks>
public sealed class CadPageSetupFieldPatch
{
    public string? DeviceName { get; init; }

    public string? MediaName { get; init; }

    public double? PaperWidthMillimeters { get; init; }

    public double? PaperHeightMillimeters { get; init; }

    public CadPageMargins? UnprintableMargins { get; init; }

    public double? PlotOriginXMillimeters { get; init; }

    public double? PlotOriginYMillimeters { get; init; }

    public double? PaperImageOriginX { get; init; }

    public double? PaperImageOriginY { get; init; }

    public CadPageUnit? PaperUnit { get; init; }

    public CadPageRotation? Rotation { get; init; }

    public CadPlotAreaKind? PlotArea { get; init; }

    public CadPlotRectangle? PlotWindow { get; init; }

    public string? NamedView { get; init; }

    public bool? CenterPlot { get; init; }

    public bool? UseStandardScale { get; init; }

    public int? StandardScaleCode { get; init; }

    public double? StandardScaleFactor { get; init; }

    public double? PaperUnitsNumerator { get; init; }

    public double? DrawingUnitsDenominator { get; init; }

    public bool? PrintLineweights { get; init; }

    public bool? ScaleLineweights { get; init; }

    public bool? ApplyPlotStyles { get; init; }

    public bool? ShowPlotStyles { get; init; }

    public bool? RemoveHiddenLines { get; init; }

    public bool? PlotViewportBorders { get; init; }

    public bool? DrawViewportsFirst { get; init; }

    public string? StyleSheet { get; init; }

    public CadShadeOutputKind? ShadeOutput { get; init; }

    public CadShadeResolutionKind? ShadeResolution { get; init; }

    public short? ShadeDpi { get; init; }

    internal bool HasChanges =>
        DeviceName is not null ||
        MediaName is not null ||
        PaperWidthMillimeters.HasValue ||
        PaperHeightMillimeters.HasValue ||
        UnprintableMargins.HasValue ||
        PlotOriginXMillimeters.HasValue ||
        PlotOriginYMillimeters.HasValue ||
        PaperImageOriginX.HasValue ||
        PaperImageOriginY.HasValue ||
        PaperUnit.HasValue ||
        Rotation.HasValue ||
        PlotArea.HasValue ||
        PlotWindow.HasValue ||
        NamedView is not null ||
        CenterPlot.HasValue ||
        UseStandardScale.HasValue ||
        StandardScaleCode.HasValue ||
        StandardScaleFactor.HasValue ||
        PaperUnitsNumerator.HasValue ||
        DrawingUnitsDenominator.HasValue ||
        PrintLineweights.HasValue ||
        ScaleLineweights.HasValue ||
        ApplyPlotStyles.HasValue ||
        ShowPlotStyles.HasValue ||
        RemoveHiddenLines.HasValue ||
        PlotViewportBorders.HasValue ||
        DrawViewportsFirst.HasValue ||
        StyleSheet is not null ||
        ShadeOutput.HasValue ||
        ShadeResolution.HasValue ||
        ShadeDpi.HasValue;
}

/// <summary>
/// Replaces selected fixed plot-setting fields on a layout or named page setup
/// as one reversible edit.
/// </summary>
/// <remarks>
/// Construction is O(S) time and storage for at most 20,480 copied string code
/// units. First Apply, Undo, and Redo are O(1) and retain two fixed plot-state
/// records plus the target object. All requested values are validated before
/// the target is mutated, and apply itself is transactional. The command does
/// not change page-setup identity, layout identity, handles, ownership, model
/// versus paper target space, or any layout/viewport geometry.
/// </remarks>
public sealed class CadEditPageSetupFieldsCommand : CadEditCommand
{
    public const int MaximumNameCodeUnits = 4_096;

    public const int MaximumStringCodeUnits = 4_096;

    private readonly CadPageSetupSourceKind _targetKind;
    private readonly string _targetName;
    private readonly CadPageSetupFieldPatch _patch;
    private PlotSettings? _target;
    private CadPlotSettingsState? _previousState;
    private CadPlotSettingsState? _appliedState;

    public CadPageSetupSourceKind TargetKind => _targetKind;

    public string TargetName => _targetName;

    public CadEditPageSetupFieldsCommand(
        CadPageSetupSourceKind targetKind,
        string targetName,
        CadPageSetupFieldPatch patch,
        string description = "Edit page setup fields")
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentNullException.ThrowIfNull(patch);
        if (!Enum.IsDefined(targetKind))
        {
            throw new ArgumentOutOfRangeException(nameof(targetKind));
        }
        if (targetName.Length > MaximumNameCodeUnits)
        {
            throw new ArgumentException(
                "The page-setup target name exceeds the command ownership budget.",
                nameof(targetName));
        }
        if (!patch.HasChanges)
        {
            throw new ArgumentException(
                "At least one page-setup field must be supplied.",
                nameof(patch));
        }

        _targetKind = targetKind;
        _targetName = new string(targetName.AsSpan());
        _patch = CapturePatch(patch);
        ValidateIntrinsicValues(_patch);
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (isRedo)
        {
            PlotSettings retained = GetRetainedTarget(document);
            CadPlotSettingsState redoState = _appliedState ??
                throw new InvalidOperationException(
                    "The page-setup field command has not been applied.");
            CadPlotSettingsState.ApplyTransactional(retained, redoState);
            return;
        }

        PlotSettings target = ResolveTarget(document, _targetKind, _targetName);
        CadPlotSettingsState previous = CadPlotSettingsState.Capture(target);
        CadPlotSettingsState applied = ApplyPatch(previous, _patch);
        ValidateContextualValues(applied, _patch);
        if (applied == previous)
        {
            throw new InvalidOperationException(
                $"Page setup '{_targetName}' already has the requested values.");
        }

        CadPlotSettingsState.ApplyTransactional(target, applied);
        _target = target;
        _previousState = previous;
        _appliedState = applied;
    }

    internal override void Revert(CadDocument document)
    {
        PlotSettings retained = GetRetainedTarget(document);
        CadPlotSettingsState previous = _previousState ??
            throw new InvalidOperationException(
                "The page-setup field command has not been applied.");
        CadPlotSettingsState.ApplyTransactional(retained, previous);
    }

    private PlotSettings GetRetainedTarget(CadDocument document)
    {
        PlotSettings retained = _target ??
            throw new InvalidOperationException(
                "The page-setup field command has not been applied.");
        PlotSettings current = ResolveTarget(document, _targetKind, _targetName);
        if (!ReferenceEquals(retained, current))
        {
            throw new InvalidOperationException(
                $"Page setup '{_targetName}' is no longer the retained target.");
        }
        return retained;
    }

    private static PlotSettings ResolveTarget(
        CadDocument document,
        CadPageSetupSourceKind targetKind,
        string targetName)
    {
        if (targetKind == CadPageSetupSourceKind.Layout)
        {
            if (document.Layouts is null ||
                !document.Layouts.TryGet(targetName, out Layout layout))
            {
                throw new InvalidOperationException(
                    $"Layout '{targetName}' does not exist.");
            }
            return layout;
        }

        if (document.RootDictionary is null ||
            !document.RootDictionary.TryGetEntry(
                CadDictionary.AcadPlotSettings,
                out CadDictionary pageSetups) ||
            !pageSetups.TryGetEntry(targetName, out PlotSettings pageSetup) ||
            pageSetup is Layout)
        {
            throw new InvalidOperationException(
                $"Named page setup '{targetName}' does not exist.");
        }
        return pageSetup;
    }

    private static CadPageSetupFieldPatch CapturePatch(
        CadPageSetupFieldPatch patch) => new()
        {
            DeviceName = CopyBounded(patch.DeviceName, nameof(patch.DeviceName)),
            MediaName = CopyBounded(patch.MediaName, nameof(patch.MediaName)),
            PaperWidthMillimeters = patch.PaperWidthMillimeters,
            PaperHeightMillimeters = patch.PaperHeightMillimeters,
            UnprintableMargins = patch.UnprintableMargins,
            PlotOriginXMillimeters = patch.PlotOriginXMillimeters,
            PlotOriginYMillimeters = patch.PlotOriginYMillimeters,
            PaperImageOriginX = patch.PaperImageOriginX,
            PaperImageOriginY = patch.PaperImageOriginY,
            PaperUnit = patch.PaperUnit,
            Rotation = patch.Rotation,
            PlotArea = patch.PlotArea,
            PlotWindow = patch.PlotWindow,
            NamedView = CopyBounded(patch.NamedView, nameof(patch.NamedView)),
            CenterPlot = patch.CenterPlot,
            UseStandardScale = patch.UseStandardScale,
            StandardScaleCode = patch.StandardScaleCode,
            StandardScaleFactor = patch.StandardScaleFactor,
            PaperUnitsNumerator = patch.PaperUnitsNumerator,
            DrawingUnitsDenominator = patch.DrawingUnitsDenominator,
            PrintLineweights = patch.PrintLineweights,
            ScaleLineweights = patch.ScaleLineweights,
            ApplyPlotStyles = patch.ApplyPlotStyles,
            ShowPlotStyles = patch.ShowPlotStyles,
            RemoveHiddenLines = patch.RemoveHiddenLines,
            PlotViewportBorders = patch.PlotViewportBorders,
            DrawViewportsFirst = patch.DrawViewportsFirst,
            StyleSheet = CopyBounded(patch.StyleSheet, nameof(patch.StyleSheet)),
            ShadeOutput = patch.ShadeOutput,
            ShadeResolution = patch.ShadeResolution,
            ShadeDpi = patch.ShadeDpi,
        };

    private static string? CopyBounded(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }
        if (value.Length > MaximumStringCodeUnits)
        {
            throw new ArgumentException(
                "The page-setup string exceeds the command ownership budget.",
                parameterName);
        }
        return new string(value.AsSpan());
    }

    private static void ValidateIntrinsicValues(CadPageSetupFieldPatch patch)
    {
        ValidatePositiveFinite(
            patch.PaperWidthMillimeters,
            nameof(patch.PaperWidthMillimeters));
        ValidatePositiveFinite(
            patch.PaperHeightMillimeters,
            nameof(patch.PaperHeightMillimeters));
        ValidateFinite(
            patch.PlotOriginXMillimeters,
            nameof(patch.PlotOriginXMillimeters));
        ValidateFinite(
            patch.PlotOriginYMillimeters,
            nameof(patch.PlotOriginYMillimeters));
        ValidateFinite(
            patch.PaperImageOriginX,
            nameof(patch.PaperImageOriginX));
        ValidateFinite(
            patch.PaperImageOriginY,
            nameof(patch.PaperImageOriginY));
        ValidatePositiveFinite(
            patch.StandardScaleFactor,
            nameof(patch.StandardScaleFactor));
        ValidatePositiveFinite(
            patch.PaperUnitsNumerator,
            nameof(patch.PaperUnitsNumerator));
        ValidatePositiveFinite(
            patch.DrawingUnitsDenominator,
            nameof(patch.DrawingUnitsDenominator));

        if (patch.UnprintableMargins is CadPageMargins margins &&
            (!double.IsFinite(margins.LeftMillimeters) || margins.LeftMillimeters < 0.0 ||
             !double.IsFinite(margins.BottomMillimeters) || margins.BottomMillimeters < 0.0 ||
             !double.IsFinite(margins.RightMillimeters) || margins.RightMillimeters < 0.0 ||
             !double.IsFinite(margins.TopMillimeters) || margins.TopMillimeters < 0.0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(patch.UnprintableMargins),
                "Unprintable margins must be finite and non-negative.");
        }
        if (patch.PlotWindow is CadPlotRectangle window && !window.IsFiniteAndOrdered)
        {
            throw new ArgumentOutOfRangeException(
                nameof(patch.PlotWindow),
                "The plot window must be finite and ordered.");
        }
        ValidateKnownEnum(
            patch.PaperUnit,
            CadPageUnit.Unknown,
            nameof(patch.PaperUnit));
        ValidateKnownEnum(
            patch.Rotation,
            CadPageRotation.Unknown,
            nameof(patch.Rotation));
        ValidateKnownEnum(
            patch.PlotArea,
            CadPlotAreaKind.Unknown,
            nameof(patch.PlotArea));
        ValidateKnownEnum(
            patch.ShadeOutput,
            CadShadeOutputKind.Unknown,
            nameof(patch.ShadeOutput));
        ValidateKnownEnum(
            patch.ShadeResolution,
            CadShadeResolutionKind.Unknown,
            nameof(patch.ShadeResolution));

        if (patch.StandardScaleCode is int scaleCode &&
            !Enum.IsDefined((ScaledType)scaleCode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(patch.StandardScaleCode),
                "The standard plot-scale code is not defined.");
        }
        if (patch.ShadeDpi is short shadeDpi &&
            (shadeDpi < 100 || shadeDpi > 32_767))
        {
            throw new ArgumentOutOfRangeException(
                nameof(patch.ShadeDpi),
                "Shade DPI must be between 100 and 32767.");
        }
    }

    private static void ValidateContextualValues(
        CadPlotSettingsState desired,
        CadPageSetupFieldPatch patch)
    {
        if ((patch.PlotArea.HasValue || patch.PlotWindow.HasValue) &&
            desired.PlotType == PlotType.Window &&
            !new CadPlotRectangle(
                desired.WindowLowerLeftX,
                desired.WindowLowerLeftY,
                desired.WindowUpperLeftX,
                desired.WindowUpperLeftY).IsFiniteAndOrdered)
        {
            throw new InvalidOperationException(
                "A Window plot area requires a finite, ordered plot window.");
        }
        if ((patch.PlotArea.HasValue || patch.NamedView is not null) &&
            desired.PlotType == PlotType.View &&
            string.IsNullOrWhiteSpace(desired.PlotViewName))
        {
            throw new InvalidOperationException(
                "A NamedView plot area requires a non-empty view name.");
        }
    }

    private static CadPlotSettingsState ApplyPatch(
        CadPlotSettingsState state,
        CadPageSetupFieldPatch patch)
    {
        PlotFlags flags = state.Flags;
        flags = SetFlag(flags, PlotFlags.PlotCentered, patch.CenterPlot);
        flags = SetFlag(flags, PlotFlags.UseStandardScale, patch.UseStandardScale);
        flags = SetFlag(flags, PlotFlags.PrintLineweights, patch.PrintLineweights);
        flags = SetFlag(flags, PlotFlags.ScaleLineweights, patch.ScaleLineweights);
        flags = SetFlag(flags, PlotFlags.PlotPlotStyles, patch.ApplyPlotStyles);
        flags = SetFlag(flags, PlotFlags.ShowPlotStyles, patch.ShowPlotStyles);
        flags = SetFlag(flags, PlotFlags.PlotHidden, patch.RemoveHiddenLines);
        flags = SetFlag(flags, PlotFlags.PlotViewportBorders, patch.PlotViewportBorders);
        flags = SetFlag(flags, PlotFlags.DrawViewportsFirst, patch.DrawViewportsFirst);

        double imageOriginX = patch.PaperImageOriginX ?? state.PaperImageOrigin.X;
        double imageOriginY = patch.PaperImageOriginY ?? state.PaperImageOrigin.Y;
        XY imageOrigin = patch.PaperImageOriginX.HasValue || patch.PaperImageOriginY.HasValue
            ? new XY(imageOriginX, imageOriginY)
            : state.PaperImageOrigin;

        return state with
        {
            DenominatorScale = patch.DrawingUnitsDenominator ?? state.DenominatorScale,
            Flags = flags,
            NumeratorScale = patch.PaperUnitsNumerator ?? state.NumeratorScale,
            PaperHeight = patch.PaperHeightMillimeters ?? state.PaperHeight,
            PaperImageOrigin = imageOrigin,
            PaperImageOriginX = patch.PaperImageOriginX ?? state.PaperImageOriginX,
            PaperImageOriginY = patch.PaperImageOriginY ?? state.PaperImageOriginY,
            PaperRotation = patch.Rotation.HasValue
                ? MapRotation(patch.Rotation.Value)
                : state.PaperRotation,
            PaperSize = patch.MediaName ?? state.PaperSize,
            PaperUnits = patch.PaperUnit.HasValue
                ? MapPaperUnit(patch.PaperUnit.Value)
                : state.PaperUnits,
            PaperWidth = patch.PaperWidthMillimeters ?? state.PaperWidth,
            PlotOriginX = patch.PlotOriginXMillimeters ?? state.PlotOriginX,
            PlotOriginY = patch.PlotOriginYMillimeters ?? state.PlotOriginY,
            PlotType = patch.PlotArea.HasValue
                ? MapPlotArea(patch.PlotArea.Value)
                : state.PlotType,
            PlotViewName = patch.NamedView ?? state.PlotViewName,
            ScaledFit = patch.StandardScaleCode.HasValue
                ? (ScaledType)patch.StandardScaleCode.Value
                : state.ScaledFit,
            ShadePlotDpi = patch.ShadeDpi ?? state.ShadePlotDpi,
            ShadePlotMode = patch.ShadeOutput.HasValue
                ? MapShadeOutput(patch.ShadeOutput.Value)
                : state.ShadePlotMode,
            ShadePlotResolutionMode = patch.ShadeResolution.HasValue
                ? MapShadeResolution(patch.ShadeResolution.Value)
                : state.ShadePlotResolutionMode,
            StandardScale = patch.StandardScaleFactor ?? state.StandardScale,
            StyleSheet = patch.StyleSheet ?? state.StyleSheet,
            SystemPrinterName = patch.DeviceName ?? state.SystemPrinterName,
            UnprintableMargin = patch.UnprintableMargins is CadPageMargins margins
                ? new PaperMargin(
                    margins.LeftMillimeters,
                    margins.BottomMillimeters,
                    margins.RightMillimeters,
                    margins.TopMillimeters)
                : state.UnprintableMargin,
            WindowLowerLeftX = patch.PlotWindow?.MinimumX ?? state.WindowLowerLeftX,
            WindowLowerLeftY = patch.PlotWindow?.MinimumY ?? state.WindowLowerLeftY,
            WindowUpperLeftX = patch.PlotWindow?.MaximumX ?? state.WindowUpperLeftX,
            WindowUpperLeftY = patch.PlotWindow?.MaximumY ?? state.WindowUpperLeftY,
        };
    }

    private static PlotFlags SetFlag(
        PlotFlags flags,
        PlotFlags mask,
        bool? enabled)
    {
        if (!enabled.HasValue)
        {
            return flags;
        }
        return enabled.Value ? flags | mask : flags & ~mask;
    }

    private static void ValidateFinite(double? value, string parameterName)
    {
        if (value.HasValue && !double.IsFinite(value.Value))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The page-setup value must be finite.");
        }
    }

    private static void ValidatePositiveFinite(double? value, string parameterName)
    {
        if (value.HasValue && (!double.IsFinite(value.Value) || value.Value <= 0.0))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The page-setup value must be finite and positive.");
        }
    }

    private static void ValidateKnownEnum<T>(
        T? value,
        T unknown,
        string parameterName)
        where T : struct, Enum
    {
        if (value.HasValue &&
            (!Enum.IsDefined(value.Value) || EqualityComparer<T>.Default.Equals(value.Value, unknown)))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The page-setup enum value is not supported.");
        }
    }

    private static PlotPaperUnits MapPaperUnit(CadPageUnit value) => value switch
    {
        CadPageUnit.Inches => PlotPaperUnits.Inches,
        CadPageUnit.Millimeters => PlotPaperUnits.Millimeters,
        CadPageUnit.DevicePixels => PlotPaperUnits.Pixels,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static PlotRotation MapRotation(CadPageRotation value) => value switch
    {
        CadPageRotation.Degrees0 => PlotRotation.NoRotation,
        CadPageRotation.CounterClockwise90 => PlotRotation.Degrees90,
        CadPageRotation.Degrees180 => PlotRotation.Degrees180,
        CadPageRotation.CounterClockwise270 => PlotRotation.Degrees270,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static PlotType MapPlotArea(CadPlotAreaKind value) => value switch
    {
        CadPlotAreaKind.Display => PlotType.LastScreenDisplay,
        CadPlotAreaKind.Extents => PlotType.DrawingExtents,
        CadPlotAreaKind.Limits => PlotType.DrawingLimits,
        CadPlotAreaKind.NamedView => PlotType.View,
        CadPlotAreaKind.Window => PlotType.Window,
        CadPlotAreaKind.Layout => PlotType.LayoutInformation,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ShadePlotMode MapShadeOutput(CadShadeOutputKind value) => value switch
    {
        CadShadeOutputKind.CurrentDisplay => ShadePlotMode.AsDisplayed,
        CadShadeOutputKind.Wireframe => ShadePlotMode.Wireframe,
        CadShadeOutputKind.Hidden => ShadePlotMode.Hidden,
        CadShadeOutputKind.Rendered => ShadePlotMode.Rendered,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ShadePlotResolutionMode MapShadeResolution(
        CadShadeResolutionKind value) => value switch
        {
            CadShadeResolutionKind.Draft => ShadePlotResolutionMode.Draft,
            CadShadeResolutionKind.Preview => ShadePlotResolutionMode.Preview,
            CadShadeResolutionKind.Normal => ShadePlotResolutionMode.Normal,
            CadShadeResolutionKind.Presentation => ShadePlotResolutionMode.Presentation,
            CadShadeResolutionKind.Maximum => ShadePlotResolutionMode.Maximum,
            CadShadeResolutionKind.Custom => ShadePlotResolutionMode.Custom,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
}

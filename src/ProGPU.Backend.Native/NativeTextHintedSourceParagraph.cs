using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

/// <summary>Explicit physical-em capture policy, not an inferred WPF Display policy.</summary>
public enum NativeSourceEmPolicy : uint { Exact26Dot6, NearestHalfUp, NearestTiesToEven, FloatCaptureNearestHalfUp }
public enum NativeSourceAdvancePolicy : uint { Unchanged, PhysicalTiesToEven, SourceIdealUnits }
public enum NativeSourceOffsetPolicy : uint { Unchanged, SourceIdealUnits }

public partial struct NativeHintedSourceOptions
{
    public static unsafe NativeHintedSourceOptions Create(double emSize, double pixelsPerDip, double maximumWidth,
        double lineHeight, double tabOrigin, NativeSourceEmPolicy emPolicy, NativeSourceAdvancePolicy advancePolicy,
        bool allowEmergencyBreak, bool measureIntrinsicWidths = false,
        NativeSourceOffsetPolicy offsetPolicy = NativeSourceOffsetPolicy.Unchanged) => new()
        {
            AbiVersion = NativeMethods.AbiVersion, StructSize = (uint)sizeof(NativeHintedSourceOptions), Version = 1,
            EmSize = emSize, PixelsPerDip = pixelsPerDip, MaximumWidth = maximumWidth, LineHeight = lineHeight,
            TabOrigin = tabOrigin, EmPolicy = (uint)emPolicy, AdvancePolicy = (uint)advancePolicy,
            AllowEmergencyBreak = allowEmergencyBreak ? 1U : 0U,
            Flags = measureIntrinsicWidths ? 1U : 0U,
            OffsetPolicy = (uint)offsetPolicy,
        };
}

/// <summary>
/// Owns one original source-double generation. Raster snapshots carry original
/// IDs and owners only; authoritative source geometry and interaction stay double.
/// No provider Display advertisement or intrinsic-width capability is implied.
/// </summary>
public sealed unsafe class NativeHintedSourceParagraph : IDisposable
{
    private readonly NativeHintedParagraph _paragraph;
    private readonly NativeHintedSourceStyle[] _styles;
    private readonly NativeHintedSourceLogicalMetrics[] _logical;
    private readonly NativeHintedSourceGlyphMetrics[] _glyphs;
    private readonly NativeHintedSourceLineMetrics[] _lines;
    private readonly NativeHintedSourceClusterBox[] _boxes;
    private readonly NativeHintedSourceCaretStop[] _carets;

    internal NativeHintedSourceParagraph(nint handle)
    {
        NativeMethods.HintedSourceParagraphView view = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.BorrowHintedSourceParagraph(handle, &view), "source paragraph borrow");
        NativeHintedParagraphCounts counts = default;
        NativeTextParagraphResult diagnostic = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.GetHintedParagraphCounts(handle, &counts, &diagnostic), "source paragraph counts");
        if (view.Options.AbiVersion != NativeMethods.AbiVersion || view.Options.StructSize != sizeof(NativeHintedSourceOptions) ||
            view.Options.Version != 1 || (view.Options.Flags & ~1U) != 0 || view.Options.OffsetPolicy > 1 ||
            view.Options.EmPolicy > 3 || view.Options.AdvancePolicy > 2 ||
            view.StyleCount != counts.StyleCount || view.LogicalCount != counts.LogicalGlyphCount ||
            view.GlyphCount != counts.PositionedGlyphCount || view.LineCount != counts.LineCount ||
            counts.ClusterBoxCount != 0 || counts.CaretStopCount != 0)
            throw new InvalidOperationException("The source-double snapshot does not match its original format generation.");
        Options = view.Options;
        _styles = Copy<NativeHintedSourceStyle>(view.Styles, view.StyleCount);
        _logical = Copy<NativeHintedSourceLogicalMetrics>(view.LogicalMetrics, view.LogicalCount);
        _glyphs = Copy<NativeHintedSourceGlyphMetrics>(view.GlyphMetrics, view.GlyphCount);
        _lines = Copy<NativeHintedSourceLineMetrics>(view.LineMetrics, view.LineCount);
        _boxes = Copy<NativeHintedSourceClusterBox>(view.Boxes, view.BoxCount);
        _carets = Copy<NativeHintedSourceCaretStop>(view.Carets, view.CaretCount);
        if ((view.Options.Flags & 1U) != 0)
        {
            NativeHintedSourceIntrinsicWidths widths = default;
            NativeHintedParagraph.ThrowForStatus(NativeMethods.GetHintedSourceIntrinsicWidths(handle, &widths), "source intrinsic widths");
            IntrinsicWidths = widths;
        }
        // Last throwing construction operation transfers the sole native owner.
        // The factory destroys the raw handle if any preceding snapshot fails.
        _paragraph = new NativeHintedParagraph(handle, sourceGeometry: true);
    }

    private static T[] Copy<T>(nuint pointer, uint count) where T : unmanaged
    {
        int length = checked((int)count);
        nuint bytes = checked((nuint)length * (nuint)sizeof(T));
        if ((pointer == 0 && length != 0) || pointer > nuint.MaxValue - bytes)
            throw new InvalidOperationException("The native source snapshot has an invalid borrowed range.");
        return new ReadOnlySpan<T>((void*)pointer, length).ToArray();
    }

    public NativeHintedSourceOptions Options { get; }
    public NativeHintedSourceIntrinsicWidths? IntrinsicWidths { get; }
    public ReadOnlySpan<NativeHintedSourceStyle> SourceStyles => _styles;
    public ReadOnlySpan<NativeHintedSourceLogicalMetrics> LogicalMetrics => _logical;
    public ReadOnlySpan<NativeHintedSourceGlyphMetrics> GlyphMetrics => _glyphs;
    public ReadOnlySpan<NativeHintedSourceLineMetrics> LineMetrics => _lines;
    public ReadOnlySpan<NativeHintedSourceClusterBox> Boxes => _boxes;
    public ReadOnlySpan<NativeHintedSourceCaretStop> Carets => _carets;
    public ReadOnlySpan<NativeTextScalar> SourceScalars => _paragraph.SourceScalars;
    public ReadOnlySpan<NativeHintedParagraphRun> Runs => _paragraph.Runs;
    public ReadOnlySpan<NativeTextShapingGlyph> LogicalGlyphs => _paragraph.LogicalGlyphs;
    public ReadOnlySpan<NativePositionedTextGlyph> RasterGlyphs => _paragraph.Glyphs;
    public ReadOnlySpan<NativePositionedTextLine> RasterLines => _paragraph.Lines;
    public ReadOnlySpan<NativeHintedParagraphGlyphOwner> PositionedOwners => _paragraph.PositionedOwners;
    public ReadOnlySpan<int> ClusterEnds => _paragraph.ClusterEnds;
    public ReadOnlySpan<sbyte> BidiLevels => _paragraph.BidiLevels;

    public NativeHintedSourceHit HitTest(double x, double y)
    {
        using var use = _paragraph.AcquireSourceUse();
        NativeHintedSourceHit hit = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.HitTestHintedSourceParagraph(use.Handle, x, y, &hit), "source hit test");
        return hit;
    }

    public NativeHintedSourceCaretStop GetCaret(int inputPosition, bool trailing)
    {
        using var use = _paragraph.AcquireSourceUse();
        NativeHintedSourceCaretStop caret = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.GetHintedSourceCaret(use.Handle, inputPosition, trailing ? 1U : 0U, &caret), "source caret");
        return caret;
    }

    public NativeHintedSourceHit HitTestLine(int lineIndex, double distance)
    {
        using var use = _paragraph.AcquireSourceUse();
        NativeHintedSourceHit hit = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.HitTestHintedSourceLine(use.Handle, checked((uint)lineIndex), distance, &hit), "source line hit test");
        return hit;
    }

    public NativeHintedSourceCaretStop GetLineCaret(int lineIndex, int inputPosition, bool trailing)
    {
        using var use = _paragraph.AcquireSourceUse();
        NativeHintedSourceCaretStop caret = default;
        NativeHintedParagraph.ThrowForStatus(NativeMethods.GetHintedSourceLineCaret(use.Handle, checked((uint)lineIndex), inputPosition,
            trailing ? 1U : 0U, &caret), "source line caret");
        return caret;
    }

    public int GetLineSelection(int lineIndex, int start, int end, Span<NativeHintedSourceRectangle> rectangles)
    {
        using var use = _paragraph.AcquireSourceUse();
        uint written = 0;
        fixed (NativeHintedSourceRectangle* output = rectangles)
            NativeHintedParagraph.ThrowForStatus(NativeMethods.GetHintedSourceLineSelection(use.Handle, checked((uint)lineIndex), start, end,
                output, checked((uint)rectangles.Length), &written), "source line selection");
        return checked((int)written);
    }

    public int GetSelection(int start, int end, Span<NativeHintedSourceRectangle> rectangles)
    {
        using var use = _paragraph.AcquireSourceUse();
        uint written = 0;
        fixed (NativeHintedSourceRectangle* output = rectangles)
            NativeHintedParagraph.ThrowForStatus(NativeMethods.GetHintedSourceSelection(use.Handle, start, end, output,
                checked((uint)rectangles.Length), &written), "source selection");
        return checked((int)written);
    }

    public NativeHintedSourceParagraph Reflow(int inputStart, double maximumWidth)
    {
        using var use = _paragraph.AcquireSourceUse();
        nint handle = 0;
        try
        {
            NativeHintedParagraph.ThrowForStatus(NativeMethods.ReflowHintedSourceParagraph(use.Handle, inputStart, maximumWidth, &handle), "source reflow");
            if (handle == 0) throw new InvalidOperationException("Native source reflow returned no owner.");
            return new NativeHintedSourceParagraph(handle);
        }
        catch { if (handle != 0) NativeMethods.DestroyHintedParagraph(handle); throw; }
    }

    public NativeHintedGlyphResource PrepareGlyphResourceWithNominalMetrics(
        NativeHintedProjectionPolicy projection = NativeHintedProjectionPolicy.Automatic,
        NativeHintedCoverage coverage = NativeHintedCoverage.Strict)
    {
        float rasterDpi = (float)Options.PixelsPerDip;
        if (!float.IsFinite(rasterDpi) || (double)rasterDpi != Options.PixelsPerDip)
            throw new NotSupportedException("The original source DPI is not representable by the current raster target ABI.");
        return _paragraph.PrepareGlyphResourceWithNominalMetrics(rasterDpi, projection, coverage);
    }

    public void Dispose() => _paragraph.Dispose();
}

public sealed unsafe partial class NativeTextShapingContext
{
    public NativeHintedSourceParagraph LayoutHintedSourceParagraph(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        ReadOnlySpan<NativeTextStyleMetrics> metrics, ReadOnlySpan<NativeHintedParagraphDeviceStyle> deviceStyles,
        in NativeHintedSourceOptions sourceOptions, ReadOnlySpan<NativeHintedSourceStyle> sourceStyles,
        ReadOnlySpan<int> variationCoordinates16_16 = default)
    {
        ValidateHintedShapeResources(in input);
        if (metrics.Length != styles.Length || deviceStyles.Length != styles.Length || sourceStyles.Length != styles.Length)
            throw new ArgumentException("Every original style requires source metrics, device configuration and original doubles.");
        using var use = _owner.Acquire();
        nint paragraph = 0;
        try
        {
            fixed (NativeTextScalar* scalars = input.Input)
            fixed (NativeTextScalar* pre = input.PreContext)
            fixed (NativeTextScalar* post = input.PostContext)
            fixed (NativeTextFeature* features = input.Features)
            fixed (short* coordinates = input.NormalizedCoordinates)
            fixed (NativeTextStyleRun* styleData = styles)
            fixed (NativeTextStyleMetrics* metricData = metrics)
            fixed (NativeHintedParagraphDeviceStyle* deviceData = deviceStyles)
            fixed (NativeHintedSourceStyle* sourceData = sourceStyles)
            fixed (int* variations = variationCoordinates16_16)
            {
                var shaping = NativeTextShapingInterop.CreateRequest(in input, null, scalars, pre, post, features, coordinates, null, includeOwnedResources: false);
                var layout = CreateParagraphLayoutOptions(in input, in options);
                var source = sourceOptions;
                NativeTextParagraphResult result = default;
                NativeHintedParagraph.ThrowForStatus(NativeMethods.LayoutHintedSourceParagraph(use.Handle, &shaping, &layout,
                    styleData, checked((uint)styles.Length), metricData, deviceData, checked((uint)deviceStyles.Length),
                    variations, checked((uint)variationCoordinates16_16.Length), &source, sourceData, checked((uint)sourceStyles.Length),
                    &paragraph, &result), "source paragraph layout");
            }
            if (paragraph == 0) throw new InvalidOperationException("Native source layout returned no owner.");
            return new NativeHintedSourceParagraph(paragraph);
        }
        catch { if (paragraph != 0) NativeMethods.DestroyHintedParagraph(paragraph); throw; }
    }

    public NativeHintedSourceParagraph LayoutHintedSourceParagraph(ReadOnlySpan<char> text, NativeTextDirection direction,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextParagraphStyle> styles,
        ReadOnlySpan<NativeTextStyleMetrics> metrics, ReadOnlySpan<NativeHintedParagraphDeviceStyle> deviceStyles,
        in NativeHintedSourceOptions sourceOptions, ReadOnlySpan<NativeHintedSourceStyle> sourceStyles,
        ReadOnlySpan<NativeTextFeature> features = default, ReadOnlySpan<int> variationCoordinates16_16 = default,
        ReadOnlySpan<short> normalizedCoordinates = default)
    {
        var scalars = new NativeTextScalar[text.Length];
        int count = NativeTextParagraphSnapshot.DecodeUtf16(text, scalars);
        var mapped = NativeTextParagraphSnapshot.MapStyles(styles, scalars.AsSpan(0, count), text.Length);
        var input = new NativeTextShapeInput(default, scalars.AsSpan(0, count), direction: direction,
            features: features, normalizedCoordinates: normalizedCoordinates);
        return LayoutHintedSourceParagraph(in input, in options, mapped, metrics, deviceStyles,
            in sourceOptions, sourceStyles, variationCoordinates16_16);
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_layout_hinted_source_paragraph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus LayoutHintedSourceParagraph(nint context, NativeTextShapeRequest* shaping,
        NativeTextLayoutOptions* layout, NativeTextStyleRun* styles, uint styleCount, NativeTextStyleMetrics* metrics,
        NativeHintedParagraphDeviceStyle* devices, uint deviceCount, int* variations, uint variationCount,
        NativeHintedSourceOptions* options, NativeHintedSourceStyle* sourceStyles, uint sourceStyleCount, nint* paragraph, NativeTextParagraphResult* result);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_source_paragraph_borrow")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BorrowHintedSourceParagraph(nint paragraph, HintedSourceParagraphView* view);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_source_paragraph_get_intrinsic_widths")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetHintedSourceIntrinsicWidths(nint paragraph, NativeHintedSourceIntrinsicWidths* widths);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_source_paragraph_reflow")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus ReflowHintedSourceParagraph(nint paragraph, int inputStart, double maximumWidth, nint* reflowed);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_source_paragraph_hit_test")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus HitTestHintedSourceParagraph(nint paragraph, double x, double y, NativeHintedSourceHit* hit);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_source_paragraph_get_caret")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetHintedSourceCaret(nint paragraph, int inputPosition, uint trailing, NativeHintedSourceCaretStop* caret);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_source_paragraph_get_selection")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetHintedSourceSelection(nint paragraph, int start, int end,
        NativeHintedSourceRectangle* rectangles, uint capacity, uint* written);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_source_paragraph_hit_test_line")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus HitTestHintedSourceLine(nint paragraph, uint lineIndex, double distance, NativeHintedSourceHit* hit);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_source_paragraph_get_line_caret")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetHintedSourceLineCaret(nint paragraph, uint lineIndex, int position, uint trailing, NativeHintedSourceCaretStop* caret);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_source_paragraph_get_line_selection")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetHintedSourceLineSelection(nint paragraph, uint lineIndex, int start, int end,
        NativeHintedSourceRectangle* rectangles, uint capacity, uint* written);
}

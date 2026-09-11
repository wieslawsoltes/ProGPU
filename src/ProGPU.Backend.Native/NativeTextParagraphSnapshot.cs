using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace ProGPU.Backend.Native;

/// <summary>
/// Retained CPU-owned paragraph output for source text editors. Shaping, bidi,
/// line breaking, positioning and interaction stay in the native implementations.
/// Styles select context-owned faces; composition remains in the shared native pipeline.
/// </summary>
public sealed class NativeTextParagraphSnapshot
{
    public const uint TabGlyphId = uint.MaxValue;
    public const uint InlineObjectGlyphId = uint.MaxValue - 1;
    public bool HasMeasuredLines { get; }
    /// <summary>Source-ordered non-ink object placements; array index is the caller's object index.</summary>
    public ReadOnlyMemory<NativeTextInlineObjectPlacement> InlineObjects { get; }
    public ReadOnlyMemory<NativePositionedTextGlyph> Glyphs { get; }
    public ReadOnlyMemory<NativePositionedTextLine> Lines { get; }
    /// <summary>Explicit native row/top frames for excluded layout; Lines then contains fragments, not rows.</summary>
    public ReadOnlyMemory<NativeTextFragmentPlacement> Fragments { get; }
    /// <summary>Native extents and counts for excluded layout; never sum fragment heights to recover them.</summary>
    public NativeTextParagraphResult? FragmentLayout { get; }
    /// <summary>Source-ordered native boxes retained independently of parent text fragments.</summary>
    public ReadOnlyMemory<NativeTextFloatingPlacement> FloatingPlacements { get; }
    /// <summary>Owned original UTF-16 events, paired by index with FloatingPlacements.</summary>
    public ReadOnlyMemory<NativeTextParagraphFloat> FloatingItems { get; }
    /// <summary>Float-inclusive extents; FragmentLayout continues describing parent text only.</summary>
    public NativeTextFloatingResult? FloatingLayout { get; }
    public ReadOnlyMemory<int> ClusterEnds { get; }
    public ReadOnlyMemory<sbyte> BidiLevels { get; }
    public ReadOnlyMemory<NativeTextClusterBox> Boxes { get; }
    public ReadOnlyMemory<NativeTextCaretStop> Carets { get; }
    public NativeTextIntrinsicWidths? IntrinsicWidths { get; }
    public NativeTextCollapsedRange? CollapsedRange { get; }

    /// <summary>Moves an index within this retained fragment generation using native physical navigation.</summary>
    public NativeRendererStatus MoveFragmentCaret(uint currentIndex, NativeTextCaretMovement direction,
        sbyte paragraphLevel, float preferredX, out uint nextIndex)
    {
        if (FragmentLayout is null)
            throw new InvalidOperationException("Fragment navigation requires an excluded paragraph snapshot.");
        return NativeTextInteractionInterop.MoveFragmentCaret(Carets.Span, Fragments.Span,
            currentIndex, direction, paragraphLevel, preferredX, out nextIndex);
    }

    private NativeTextParagraphSnapshot(NativePositionedTextGlyph[] glyphs,
        NativePositionedTextLine[] lines, int[] ends, sbyte[] levels,
        ReadOnlyMemory<NativeTextClusterBox> boxes, ReadOnlyMemory<NativeTextCaretStop> carets,
        NativeTextIntrinsicWidths? intrinsicWidths = null, NativeTextCollapsedRange? collapsedRange = null,
        bool measuredLines = false, NativeTextInlineObjectPlacement[]? inlineObjects = null,
        NativeTextFragmentPlacement[]? fragments = null, NativeTextParagraphResult? fragmentLayout = null,
        NativeTextFloatingPlacement[]? floatingPlacements = null, NativeTextFloatingResult? floatingLayout = null,
        NativeTextParagraphFloat[]? floatingItems = null)
    {
        Glyphs = glyphs; Lines = lines; ClusterEnds = ends; BidiLevels = levels;
        Boxes = boxes; Carets = carets;
        IntrinsicWidths = intrinsicWidths;
        CollapsedRange = collapsedRange;
        HasMeasuredLines = measuredLines;
        InlineObjects = inlineObjects ?? [];
        Fragments = fragments ?? [];
        FragmentLayout = fragmentLayout;
        FloatingPlacements = floatingPlacements ?? [];
        FloatingLayout = floatingLayout;
        FloatingItems = floatingItems ?? [];
    }

    public static NativeTextParagraphSnapshot Create(NativeTextShapingContext context,
        ReadOnlySpan<char> text, NativeTextDirection direction, in NativeTextParagraphOptions options,
        ReadOnlySpan<NativeTextFeature> features = default,
        ReadOnlySpan<NativeTextParagraphStyle> styles = default,
        float incrementalTab = 0, float tabOrigin = 0, bool measureIntrinsicWidths = false,
        NativeTextWrapping wrapping = NativeTextWrapping.Emergency)
        => CreateCore(context, text, direction, in options, features, styles, incrementalTab, tabOrigin,
            measureIntrinsicWidths, wrapping, null, null);

    /// <summary>
    /// Measures explicit UTF-16 U+FFFC objects with source-owned style metrics.
    /// Native shaping, line placement and interaction share one retained result.
    /// Objects and style ranges must be ordered and cover their actual source input.
    /// </summary>
    public static NativeTextParagraphSnapshot CreateWithInlineObjects(NativeTextShapingContext context,
        ReadOnlySpan<char> text, NativeTextDirection direction, in NativeTextParagraphOptions options,
        ReadOnlySpan<NativeTextParagraphStyle> styles, ReadOnlySpan<NativeTextStyleMetrics> styleMetrics,
        ReadOnlySpan<NativeTextParagraphInlineObject> inlineObjects,
        ReadOnlySpan<NativeTextFeature> features = default,
        float incrementalTab = 0, float tabOrigin = 0, bool measureIntrinsicWidths = false,
        NativeTextWrapping wrapping = NativeTextWrapping.Emergency)
        => CreateCore(context, text, direction, in options, features, styles, incrementalTab, tabOrigin,
            measureIntrinsicWidths, wrapping, null, null, true, styleMetrics, inlineObjects);

    /// <summary>Retains native exclusion fragments and their interaction in one source generation.</summary>
    public static NativeTextParagraphSnapshot CreateWithExclusionsAt(NativeTextShapingContext context,
        ReadOnlySpan<char> text, NativeTextDirection direction, in NativeTextParagraphOptions options,
        ReadOnlySpan<NativeTextParagraphStyle> styles, ReadOnlySpan<NativeTextStyleMetrics> styleMetrics,
        ReadOnlySpan<NativeTextParagraphInlineObject> inlineObjects,
        in NativeTextExclusionOptions exclusionOptions, ReadOnlySpan<NativeTextExclusionRectangle> exclusions,
        double originY, ReadOnlySpan<NativeTextFeature> features = default,
        float incrementalTab = 0, float tabOrigin = 0, bool measureIntrinsicWidths = false,
        NativeTextWrapping wrapping = NativeTextWrapping.Emergency)
        => CreateCore(context, text, direction, in options, features, styles, incrementalTab, tabOrigin,
            measureIntrinsicWidths, wrapping, null, null, true, styleMetrics, inlineObjects,
            exclusionOptions, exclusions, originY);

    /// <summary>Retains source-ordered floats, parent fragments and native interaction in one generation.</summary>
    public static NativeTextParagraphSnapshot CreateWithFloats(NativeTextShapingContext context,
        ReadOnlySpan<char> text, NativeTextDirection direction, in NativeTextParagraphOptions options,
        ReadOnlySpan<NativeTextParagraphStyle> styles, ReadOnlySpan<NativeTextStyleMetrics> styleMetrics,
        ReadOnlySpan<NativeTextParagraphInlineObject> inlineObjects,
        in NativeTextFloatingOptions floatingOptions, ReadOnlySpan<NativeTextParagraphFloat> floats,
        ReadOnlySpan<NativeTextExclusionRectangle> exclusions = default,
        ReadOnlySpan<NativeTextFeature> features = default, float incrementalTab = 0, float tabOrigin = 0,
        NativeTextWrapping wrapping = NativeTextWrapping.Emergency)
        => CreateCore(context, text, direction, in options, features, styles, incrementalTab, tabOrigin,
            false, wrapping, null, null, true, styleMetrics, inlineObjects,
            null, exclusions, floatingOptions.OriginY, floatingOptions, floats);

    /// <summary>Retains zero-origin exclusion fragments and their interaction.</summary>
    public static NativeTextParagraphSnapshot CreateWithExclusions(NativeTextShapingContext context,
        ReadOnlySpan<char> text, NativeTextDirection direction, in NativeTextParagraphOptions options,
        ReadOnlySpan<NativeTextParagraphStyle> styles, ReadOnlySpan<NativeTextStyleMetrics> styleMetrics,
        ReadOnlySpan<NativeTextParagraphInlineObject> inlineObjects,
        in NativeTextExclusionOptions exclusionOptions, ReadOnlySpan<NativeTextExclusionRectangle> exclusions,
        ReadOnlySpan<NativeTextFeature> features = default,
        float incrementalTab = 0, float tabOrigin = 0, bool measureIntrinsicWidths = false,
        NativeTextWrapping wrapping = NativeTextWrapping.Emergency)
        => CreateCore(context, text, direction, in options, features, styles, incrementalTab, tabOrigin,
            measureIntrinsicWidths, wrapping, null, null, true, styleMetrics, inlineObjects,
            exclusionOptions, exclusions);

    /// <summary>
    /// Reuses the native paragraph composer with the original text/font/style domain.
    /// Original source cluster metadata remains authoritative; the sign is a separate
    /// interaction item, not part of the last visible source cluster.
    /// </summary>
    public static NativeTextParagraphSnapshot CreateCollapsed(NativeTextShapingContext context,
        ReadOnlySpan<char> text, NativeTextDirection direction, in NativeTextParagraphOptions options,
        NativeTextParagraphSnapshot original, in NativeTextCollapseRequest collapse,
        ReadOnlySpan<NativeTextFeature> features = default, ReadOnlySpan<NativeTextParagraphStyle> styles = default,
        float incrementalTab = 0, float tabOrigin = 0, NativeTextWrapping wrapping = NativeTextWrapping.Emergency)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.HasMeasuredLines)
            throw new NotSupportedException("Measured paragraph collapse requires an explicit sign-metric contract.");
        if (text.IsEmpty || original.CollapsedRange != null || (uint)collapse.LineIndex >= original.Lines.Length ||
            !float.IsFinite(collapse.Width) || collapse.Width < 0 ||
            !float.IsFinite(collapse.SymbolWidth) || collapse.SymbolWidth < 0 ||
            collapse.Trimming is not NativeTextTrimming.CharacterEllipsis and not NativeTextTrimming.WordEllipsis ||
            options.MaximumLines != 0 || options.Trimming != NativeTextTrimming.None)
            throw new ArgumentException("Collapse requires an original untruncated line, finite widths and a supported granularity.");
        var collapsedOptions = options with { MaximumLines = checked((uint)collapse.LineIndex + 1),
            Trimming = collapse.Trimming, EllipsisGlyphId = 0, EllipsisAdvance = collapse.SymbolWidth / options.Scale };
        return CreateCore(context, text, direction, in collapsedOptions, features, styles, incrementalTab, tabOrigin,
            false, wrapping, original, collapse);
    }

    private static NativeTextParagraphSnapshot CreateCore(NativeTextShapingContext context,
        ReadOnlySpan<char> text, NativeTextDirection direction, in NativeTextParagraphOptions options,
        ReadOnlySpan<NativeTextFeature> features, ReadOnlySpan<NativeTextParagraphStyle> styles,
        float incrementalTab, float tabOrigin, bool measureIntrinsicWidths, NativeTextWrapping wrapping,
        NativeTextParagraphSnapshot? original, NativeTextCollapseRequest? collapse,
        bool measuredLines = false, ReadOnlySpan<NativeTextStyleMetrics> styleMetrics = default,
        ReadOnlySpan<NativeTextParagraphInlineObject> inlineObjects = default,
        NativeTextExclusionOptions? exclusionOptions = null,
        ReadOnlySpan<NativeTextExclusionRectangle> exclusions = default, double? originY = null,
        NativeTextFloatingOptions? floatingOptions = null, ReadOnlySpan<NativeTextParagraphFloat> floats = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (originY.HasValue && (!double.IsFinite(originY.Value) || originY.Value < 0 || originY.Value > float.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(originY));
        if (measuredLines && (styleMetrics.Length != styles.Length || (!text.IsEmpty && styles.IsEmpty)))
            throw new ArgumentException("Measured paragraphs require explicit styles and one metric per style.");
        if (wrapping is not NativeTextWrapping.Emergency and not NativeTextWrapping.WholeWord)
            throw new ArgumentOutOfRangeException(nameof(wrapping));
        if (!float.IsFinite(incrementalTab) || incrementalTab < 0 || !float.IsFinite(tabOrigin))
            throw new ArgumentException("Tab interval and origin must be finite; the interval cannot be negative.");
        if (text.Length > 1 << 20 || (collapse == null && (options.MaximumLines != 0 ||
            options.Trimming != NativeTextTrimming.None)))
            throw new ArgumentException("Editor snapshots require an untruncated paragraph within the input budget.");
        bool fragmented = exclusionOptions.HasValue || floatingOptions.HasValue;
        if (text.IsEmpty && !floatingOptions.HasValue)
        {
            if (exclusionOptions.HasValue)
                throw new NotSupportedException("Empty excluded paragraphs require an explicit native empty-row placement contract.");
            if (!styles.IsEmpty || !inlineObjects.IsEmpty)
                throw new ArgumentException("An empty paragraph cannot contain style ranges or inline objects.");
            if (measuredLines && (!float.IsFinite(options.LineHeight) || options.LineHeight < 0))
                throw new ArgumentException("Measured line height must be finite and nonnegative.");
            return new([], [new NativePositionedTextLine { Height = options.LineHeight }], [], [],
                ReadOnlyMemory<NativeTextClusterBox>.Empty, ReadOnlyMemory<NativeTextCaretStop>.Empty,
                measureIntrinsicWidths ? new NativeTextIntrinsicWidths { StructSize = (uint)Unsafe.SizeOf<NativeTextIntrinsicWidths>() } : null,
                measuredLines: measuredLines);
        }
        var scalars = new NativeTextScalar[text.Length];
        int count = DecodeUtf16(text, scalars);
        var nativeStyles = MapStyles(styles, scalars.AsSpan(0, count), text.Length);
        var nativeObjects = measuredLines ? MapInlineObjects(inlineObjects, scalars.AsSpan(0, count)) : [];
        var nativeFloats = floatingOptions.HasValue ? MapFloats(floats, scalars.AsSpan(0, count), text.Length) : [];
        var input = new NativeTextShapeInput(default, scalars.AsSpan(0, count), direction: direction, features: features);
        var flow = new NativeTextFlowOptions { IncrementalTab = incrementalTab, TabOrigin = tabOrigin };
        NativeTextParagraphRequirements required;
        var exclusion = exclusionOptions.GetValueOrDefault();
        var floating = floatingOptions.GetValueOrDefault();
        Check(floatingOptions.HasValue ? context.GetFloatingFlowParagraphRequirements(in input, in options,
            nativeStyles, in flow, styleMetrics, nativeObjects, in floating, nativeFloats, exclusions, out required) :
            exclusionOptions.HasValue && originY.HasValue ? context.GetExcludedFlowParagraphRequirementsAt(in input, in options,
            nativeStyles, in flow, styleMetrics, nativeObjects, in exclusion, exclusions, originY.Value, out required) :
            exclusionOptions.HasValue ? context.GetExcludedFlowParagraphRequirements(in input, in options,
            nativeStyles, in flow, styleMetrics, nativeObjects, in exclusion, exclusions, out required) :
            measuredLines ? context.GetInlineFlowParagraphRequirements(in input, in options, nativeStyles, in flow,
            styleMetrics, nativeObjects, out required) :
            incrementalTab > 0 ? context.GetFlowParagraphRequirements(in input, in options, nativeStyles, in flow, out required) :
            context.GetStyledParagraphRequirements(in input, in options, nativeStyles, out required));
        var glyphBuffer = new NativePositionedTextGlyph[checked((int)required.GlyphCapacity)];
        var lineBuffer = new NativePositionedTextLine[checked((int)required.LineCapacity)];
        var fragmentBuffer = fragmented ? new NativeTextFragmentPlacement[lineBuffer.Length] : [];
        var floatBuffer = new NativeTextFloatingPlacement[nativeFloats.Length];
        NativeTextFloatingResult? floatingResult = null;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(checked((int)required.ScratchBytes));
        NativeTextParagraphResult result;
        NativeTextIntrinsicWidths? intrinsicWidths = null;
        try
        {
            if (floatingOptions.HasValue)
            {
                Check(context.LayoutFloatingFlowParagraph(in input, in options, nativeStyles, in flow,
                    styleMetrics, nativeObjects, in floating, nativeFloats, exclusions, glyphBuffer, lineBuffer,
                    fragmentBuffer, floatBuffer, scratch, wrapping, out result, out var fittedFloats));
                if (fittedFloats.FloatCount != nativeFloats.Length)
                    throw new InvalidOperationException("Untruncated floating paragraph did not consume every source event.");
                floatingResult = fittedFloats;
            }
            else if (exclusionOptions.HasValue)
            {
                NativeTextIntrinsicWidths widths;
                Check(originY.HasValue ? context.LayoutExcludedFlowParagraphAt(in input, in options, nativeStyles, in flow, styleMetrics,
                    nativeObjects, in exclusion, exclusions, originY.Value, glyphBuffer, lineBuffer, fragmentBuffer, scratch,
                    wrapping, measureIntrinsicWidths, out result, out widths) :
                    context.LayoutExcludedFlowParagraph(in input, in options, nativeStyles, in flow, styleMetrics,
                    nativeObjects, in exclusion, exclusions, glyphBuffer, lineBuffer, fragmentBuffer, scratch,
                    wrapping, measureIntrinsicWidths, out result, out widths));
                if (measureIntrinsicWidths) intrinsicWidths = widths;
            }
            else if (measuredLines)
            {
                Check(context.LayoutInlineFlowParagraph(in input, in options, nativeStyles, in flow, styleMetrics,
                    nativeObjects, glyphBuffer, lineBuffer, scratch, wrapping, measureIntrinsicWidths,
                    out result, out var widths));
                if (measureIntrinsicWidths) intrinsicWidths = widths;
            }
            else if (collapse is { } collapsed)
                Check(context.LayoutCollapsedFlowParagraph(in input, in options, nativeStyles, in flow,
                    glyphBuffer, lineBuffer, scratch, wrapping, collapsed.Width, out result));
            else if (measureIntrinsicWidths || wrapping != NativeTextWrapping.Emergency)
            {
                Check(context.LayoutConfiguredFlowParagraph(in input, in options, nativeStyles, in flow,
                    glyphBuffer, lineBuffer, scratch, wrapping, measureIntrinsicWidths, out result, out var widths));
                if (measureIntrinsicWidths) intrinsicWidths = widths;
            }
            else
                Check(incrementalTab > 0 ? context.LayoutFlowParagraph(in input, in options, nativeStyles, in flow, glyphBuffer, lineBuffer, scratch, out result) :
                    context.LayoutStyledParagraph(in input, in options, nativeStyles, glyphBuffer, lineBuffer, scratch, out result));
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
        // Output arrays are retained ownership, not per-frame replay materialization.
        Array.Resize(ref glyphBuffer, checked((int)result.GlyphCount));
        Array.Resize(ref lineBuffer, checked((int)result.LineCount));
        if (fragmented) Array.Resize(ref fragmentBuffer, lineBuffer.Length);

        if (original != null && collapse is { } collapsedRequest)
            return BuildCollapsed(original, glyphBuffer, lineBuffer, collapsedRequest, direction);

        Check(NativeTextBidiInterop.GetRequirements(scalars.AsSpan(0, count), out var bidiRequired));
        var scalarLevels = new NativeTextBidiLevel[checked((int)bidiRequired.LevelCapacity)];
        scratch = ArrayPool<byte>.Shared.Rent(checked((int)bidiRequired.ScratchBytes));
        try
        {
            Check(NativeTextBidiInterop.Resolve(scalars.AsSpan(0, count),
                direction == NativeTextDirection.RightToLeft ? 1 : direction == NativeTextDirection.LeftToRight ? 0 : -1,
                scalarLevels, scratch, out var bidiResult));
            if (bidiResult.LevelCount != count) throw new InvalidOperationException("Native bidi metadata is incomplete.");
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }

        var ends = new int[glyphBuffer.Length];
        var levels = new sbyte[glyphBuffer.Length];
        // Shape output is in visual order. Sorting cluster keys, not glyphs,
        // recovers logical successor boundaries, including ligatures/surrogates.
        var clusters = new int[glyphBuffer.Length + 1];
        for (int i = 0; i < glyphBuffer.Length; i++) clusters[i] = glyphBuffer[i].Cluster;
        clusters[^1] = text.Length;
        Array.Sort(clusters);
        for (int i = 0; i < glyphBuffer.Length; i++)
        {
            int cluster = glyphBuffer[i].Cluster;
            if (cluster < 0 || cluster >= text.Length) throw new InvalidOperationException("Native glyph has no source cluster.");
            int lo = 0, hi = clusters.Length;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (clusters[mid] <= cluster) lo = mid + 1; else hi = mid; }
            if (lo == clusters.Length) throw new InvalidOperationException("Native cluster has no logical end.");
            ends[i] = clusters[lo];
            lo = 0; hi = count;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (scalarLevels[mid].InputIndex < cluster) lo = mid + 1; else hi = mid; }
            if (lo == count || scalarLevels[lo].InputIndex != cluster)
                throw new InvalidOperationException("Native cluster is not a UTF scalar boundary.");
            levels[i] = scalarLevels[lo].Level;
        }
        for (int i = 0; i < lineBuffer.Length; i++)
        {
            ref var line = ref lineBuffer[i];
            int end = line.InputStart;
            for (int g = checked((int)line.GlyphStart); g < line.GlyphStart + line.GlyphCount; g++)
                end = Math.Max(end, ends[g]);
            line.InputEnd = end;
        }
        var interaction = new NativeTextInteractionInput(glyphBuffer, lineBuffer, ends, levels);
        NativeTextInteractionRequirements interactionRequired;
        if (fragmented)
        {
            // The fragment C ABI bounds outputs by G boxes and 2G caret stops;
            // ordinary requirements validate a line-height prefix that fragments do not have.
            interactionRequired = new() { ClusterBoxCapacity = checked((uint)glyphBuffer.Length),
                CaretStopCapacity = checked((uint)glyphBuffer.Length * 2) };
        }
        else
            Check(measuredLines ? NativeTextInteractionInterop.GetMeasuredRequirements(in interaction, out interactionRequired) :
                NativeTextInteractionInterop.GetRequirements(in interaction, out interactionRequired));
        var boxes = new NativeTextClusterBox[checked((int)interactionRequired.ClusterBoxCapacity)];
        var carets = new NativeTextCaretStop[checked((int)interactionRequired.CaretStopCapacity)];
        NativeTextInteractionResult interactionResult;
        Check(fragmented ? NativeTextInteractionInterop.BuildFragments(in interaction, fragmentBuffer,
            boxes, carets, out interactionResult) :
            measuredLines ? NativeTextInteractionInterop.BuildMeasured(in interaction, boxes, carets, out interactionResult) :
            NativeTextInteractionInterop.Build(in interaction, boxes, carets, out interactionResult));
        return new(glyphBuffer, lineBuffer, ends, levels,
            boxes.AsMemory(0, checked((int)interactionResult.ClusterBoxCount)),
            carets.AsMemory(0, checked((int)interactionResult.CaretStopCount)), intrinsicWidths,
            measuredLines: measuredLines, inlineObjects: measuredLines
                ? BuildInlinePlacements(inlineObjects, glyphBuffer, lineBuffer) : null,
            fragments: fragmentBuffer, fragmentLayout: fragmented ? result : null,
            floatingPlacements: floatBuffer, floatingLayout: floatingResult, floatingItems: floats.ToArray());
    }

    internal static NativeTextFloatingItem[] MapFloats(ReadOnlySpan<NativeTextParagraphFloat> floats,
        ReadOnlySpan<NativeTextScalar> scalars, int textLength)
    {
        if (floats.Length > 1 << 20) throw new ArgumentException("Floating event budget exceeded.", nameof(floats));
        var result = floats.IsEmpty ? [] : new NativeTextFloatingItem[floats.Length];
        // Ordered merge is dependency-bound, O(S+A); source decoding retains its intrinsic path.
        int scalar = 0, previous = 0;
        for (int i = 0; i < floats.Length; ++i)
        {
            var item = floats[i];
            if (item.Position < previous || item.Position > textLength)
                throw new ArgumentException("Floating events must be ordered UTF-16 boundaries.", nameof(floats));
            while (scalar < scalars.Length && scalars[scalar].InputIndex < item.Position) ++scalar;
            if (item.Position != textLength &&
                (scalar == scalars.Length || scalars[scalar].InputIndex != item.Position))
                throw new ArgumentException("A floating event cannot split a UTF-16 scalar.", nameof(floats));
            result[i] = new NativeTextFloatingItem { ScalarIndex = checked((uint)scalar),
                Width = item.Width, Height = item.Height, Alignment = item.Alignment };
            previous = item.Position;
        }
        return result;
    }

    internal static NativeTextInlineObject[] MapInlineObjects(ReadOnlySpan<NativeTextParagraphInlineObject> objects,
        ReadOnlySpan<NativeTextScalar> scalars)
    {
        if (objects.Length > scalars.Length)
            throw new ArgumentException("Inline object count exceeds the source scalar count.");
        var result = objects.IsEmpty ? [] : new NativeTextInlineObject[objects.Length];
        int index = 0;
        for (int scalar = 0; scalar < scalars.Length; ++scalar)
        {
            if (scalars[scalar].CodePoint != 0xfffc) continue;
            if (index == objects.Length || objects[index].Position != scalars[scalar].InputIndex)
                throw new ArgumentException("Inline objects must cover every U+FFFC in UTF-16 source order.");
            var value = objects[index];
            result[index++] = new NativeTextInlineObject { ScalarIndex = (uint)scalar,
                Width = value.Width, Ascent = value.Ascent, Descent = value.Descent };
        }
        if (index != objects.Length)
            throw new ArgumentException("Inline objects must identify unique actual U+FFFC scalars.");
        return result;
    }

    private static NativeTextInlineObjectPlacement[] BuildInlinePlacements(
        ReadOnlySpan<NativeTextParagraphInlineObject> objects,
        ReadOnlySpan<NativePositionedTextGlyph> glyphs, ReadOnlySpan<NativePositionedTextLine> lines)
    {
        if (objects.IsEmpty) return [];
        var result = new NativeTextInlineObjectPlacement[objects.Length];
        Array.Fill(result, new NativeTextInlineObjectPlacement(-1, -1, -1, 0, 0, 0, 0));
        int found = 0;
        for (int line = 0; line < lines.Length; ++line)
        {
            var info = lines[line];
            int end = checked((int)(info.GlyphStart + info.GlyphCount));
            for (int glyphIndex = checked((int)info.GlyphStart); glyphIndex < end; ++glyphIndex)
            {
                var glyph = glyphs[glyphIndex];
                if (glyph.GlyphId != InlineObjectGlyphId) continue;
                int lo = 0, hi = objects.Length;
                while (lo < hi) { int mid = lo + (hi - lo) / 2;
                    if (objects[mid].Position < glyph.Cluster) lo = mid + 1; else hi = mid; }
                if (lo == objects.Length || objects[lo].Position != glyph.Cluster ||
                    result[lo].InputPosition != -1 || glyph.FontIndex != uint.MaxValue ||
                    glyph.AdvanceX != objects[lo].Width)
                    throw new InvalidOperationException("Native inline object identity or metrics changed.");
                var source = objects[lo];
                result[lo] = new(source.Position, glyphIndex, line, glyph.X,
                    info.BaselineY - source.Ascent, source.Width, source.Ascent + source.Descent);
                ++found;
            }
        }
        if (found != objects.Length) throw new InvalidOperationException("Native inline object output is incomplete.");
        return result;
    }

    private static NativeTextParagraphSnapshot BuildCollapsed(NativeTextParagraphSnapshot original,
        NativePositionedTextGlyph[] glyphs, NativePositionedTextLine[] lines,
        NativeTextCollapseRequest request, NativeTextDirection direction)
    {
        if (lines.Length != request.LineIndex + 1)
            throw new InvalidOperationException("Collapsed layout changed the source line count.");
        var sourceLine = original.Lines.Span[request.LineIndex];
        var lookup = new int[original.Glyphs.Length];
        for (int i = 0; i < lookup.Length; ++i)
        {
            uint logical = original.Glyphs.Span[i].GlyphIndex;
            if (logical >= lookup.Length) throw new InvalidOperationException("Original glyph topology is incomplete.");
            lookup[logical] = i;
        }
        int sign = -1;
        var ends = new int[glyphs.Length]; var levels = new sbyte[glyphs.Length];
        for (int i = 0; i < glyphs.Length; ++i)
        {
            ref var glyph = ref glyphs[i];
            if (glyph.GlyphIndex == uint.MaxValue)
            {
                if (sign >= 0 || glyph.Cluster < sourceLine.InputStart || glyph.Cluster >= sourceLine.InputEnd)
                    throw new InvalidOperationException("Collapsed sign has no unique hidden source range.");
                sign = i; ends[i] = sourceLine.InputEnd;
                levels[i] = direction == NativeTextDirection.RightToLeft ? (sbyte)1 : (sbyte)0;
                continue;
            }
            if (glyph.GlyphIndex >= lookup.Length) throw new InvalidOperationException("Collapsed glyph has no original source.");
            int source = lookup[glyph.GlyphIndex]; var prior = original.Glyphs.Span[source];
            if (glyph.Cluster != prior.Cluster || glyph.GlyphId != prior.GlyphId || glyph.FontIndex != prior.FontIndex ||
                glyph.AdvanceX != prior.AdvanceX || glyph.AdvanceY != prior.AdvanceY)
                throw new InvalidOperationException("Collapse must retain the original text, font, style and tab domain.");
            ends[i] = original.ClusterEnds.Span[source]; levels[i] = original.BidiLevels.Span[source];
        }
        if (sign < 0) throw new InvalidOperationException("Native collapse did not emit a collapsing symbol.");
        for (int i = 0; i < lines.Length; ++i)
        {
            var prior = original.Lines.Span[i];
            if (lines[i].InputStart != prior.InputStart || (i < request.LineIndex &&
                (lines[i].GlyphCount != prior.GlyphCount || lines[i].Width != prior.Width)))
                throw new InvalidOperationException("Collapse reflowed a preceding source line.");
            lines[i].InputEnd = prior.InputEnd;
        }
        var interaction = new NativeTextInteractionInput(glyphs, lines, ends, levels);
        Check(NativeTextInteractionInterop.GetRequirements(in interaction, out var required));
        var boxes = new NativeTextClusterBox[checked((int)required.ClusterBoxCapacity)];
        var carets = new NativeTextCaretStop[checked((int)required.CaretStopCapacity)];
        Check(NativeTextInteractionInterop.Build(in interaction, boxes, carets, out var result));
        return new(glyphs, lines, ends, levels, boxes.AsMemory(0, checked((int)result.ClusterBoxCount)),
            carets.AsMemory(0, checked((int)result.CaretStopCount)), collapsedRange:
            new(request.LineIndex, glyphs[sign].Cluster, sourceLine.InputEnd, sign));
    }

    internal static NativeTextStyleRun[] MapStyles(ReadOnlySpan<NativeTextParagraphStyle> styles,
        ReadOnlySpan<NativeTextScalar> scalars, int textLength)
    {
        if (styles.IsEmpty) return [];
        var result = new NativeTextStyleRun[styles.Length];
        int source = 0, scalar = 0;
        for (int i = 0; i < styles.Length; i++)
        {
            var style = styles[i];
            if (style.Start != source || style.Length <= 0 || style.Length > textLength - source)
                throw new ArgumentException("Styles must partition the complete UTF-16 input in logical order.");
            int first = scalar;
            source += style.Length;
            while (scalar < scalars.Length && scalars[scalar].InputIndex < source) scalar++;
            if (first == scalar || (scalar < scalars.Length ? scalars[scalar].InputIndex : textLength) != source)
                throw new ArgumentException("A style boundary cannot split a UTF-16 scalar.");
            result[i] = new NativeTextStyleRun { ScalarStart = (uint)first, ScalarCount = (uint)(scalar - first),
                FontIndex = style.FontIndex, Scale = style.Scale, FeatureStart = style.FeatureStart,
                FeatureCount = style.FeatureCount, Language = style.Language };
        }
        if (source != textLength) throw new ArgumentException("Styles must cover the complete input.");
        return result;
    }

    internal static int DecodeUtf16(ReadOnlySpan<char> text, Span<NativeTextScalar> output)
    {
        if (output.Length < text.Length) throw new ArgumentException("The scalar capacity must cover UTF-16 length.");
        int source = 0, written = 0;
        var units = MemoryMarshal.Cast<char, ushort>(text);
        var words = MemoryMarshal.Cast<NativeTextScalar, uint>(output);
        while (source < text.Length)
        {
            if (source <= text.Length - 8)
            {
                var pairs = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(units), (nuint)source);
                var expected = Vector128.Create((ushort)0xd800, 0xdc00, 0xd800, 0xdc00, 0xd800, 0xdc00, 0xd800, 0xdc00);
                if (Vector128.EqualsAll(pairs & Vector128.Create((ushort)0xfc00), expected))
                {
                    var high = Vector128.WidenLower(Vector128.Shuffle(pairs,
                        Vector128.Create((ushort)0, 2, 4, 6, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue)));
                    var low = Vector128.WidenLower(Vector128.Shuffle(pairs,
                        Vector128.Create((ushort)1, 3, 5, 7, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue)));
                    WriteFour(((high - Vector128.Create(0xd800U)) << 10) + low - Vector128.Create(0xdc00U) + Vector128.Create(0x10000U),
                        source, 2, words.Slice(written * 4));
                    source += 8; written += 4; continue;
                }
            }
            // Four independent BMP lanes; surrogate sequences take the bounded
            // stateful Rune decoder below and then rejoin this intrinsic path.
            if (source <= text.Length - 4)
            {
                var value = Vector64.LoadUnsafe(ref MemoryMarshal.GetReference(units), (nuint)source).ToVector128();
                if (!Vector128.EqualsAny(value & Vector128.Create((ushort)0xf800), Vector128.Create((ushort)0xd800)))
                {
                    WriteFour(Vector128.WidenLower(value), source, 1, words.Slice(written * 4));
                    source += 4; written += 4; continue;
                }
            }
            var status = Rune.DecodeFromUtf16(text[source..], out var rune, out int consumed);
            if (status != OperationStatus.Done) { rune = Rune.ReplacementChar; consumed = 1; }
            output[written++] = new((uint)rune.Value, (uint)source, (ushort)consumed);
            source += consumed;
        }
        return written;
    }

    private static void WriteFour(Vector128<uint> codepoints, int source, int length, Span<uint> words)
    {
        uint metadata = BitConverter.IsLittleEndian ? (uint)length : (uint)length << 16;
        for (int lane = 0; lane < 4; lane++)
        {
            var record = Vector128.Shuffle(codepoints, Vector128.Create((uint)lane, uint.MaxValue, uint.MaxValue, uint.MaxValue)) |
                Vector128.Create(0U, (uint)(source + lane * length), metadata, 0U);
            record.CopyTo(words.Slice(lane * 4, 4));
        }
    }

    internal static void Check(NativeRendererStatus status)
    {
        if (status != NativeRendererStatus.Success)
            throw new InvalidOperationException($"Native text operation failed: {status}.");
    }
}

/// <summary>Explicit face/feature domain over UTF-16 input; ranges must partition the paragraph.</summary>
public readonly record struct NativeTextParagraphStyle(int Start, int Length, uint FontIndex,
    float Scale, uint FeatureStart = 0, uint FeatureCount = 0, uint Language = 0);

/// <summary>One measured non-ink U+FFFC at a UTF-16 position, not a native scalar index.</summary>
public readonly record struct NativeTextParagraphInlineObject(int Position, float Width, float Ascent, float Descent);

/// <summary>Measured float at an original UTF-16 boundary. Alignment uses the native 0/1/2 left/center/right contract.</summary>
public readonly record struct NativeTextParagraphFloat(int Position, float Width, float Height, uint Alignment);

/// <summary>Owned non-ink placement in paragraph coordinates, paired with its original source/glyph/line.</summary>
public readonly record struct NativeTextInlineObjectPlacement(int InputPosition, int GlyphIndex, int LineIndex,
    float X, float Y, float Width, float Height);

public readonly record struct NativeTextCollapseRequest(int LineIndex, float Width, float SymbolWidth, NativeTextTrimming Trimming);
public readonly record struct NativeTextCollapsedRange(int LineIndex, int Start, int End, int SymbolGlyphIndex);

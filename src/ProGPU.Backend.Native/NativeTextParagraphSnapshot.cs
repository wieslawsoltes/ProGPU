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
    public ReadOnlyMemory<NativePositionedTextGlyph> Glyphs { get; }
    public ReadOnlyMemory<NativePositionedTextLine> Lines { get; }
    public ReadOnlyMemory<int> ClusterEnds { get; }
    public ReadOnlyMemory<sbyte> BidiLevels { get; }
    public ReadOnlyMemory<NativeTextClusterBox> Boxes { get; }
    public ReadOnlyMemory<NativeTextCaretStop> Carets { get; }
    public NativeTextIntrinsicWidths? IntrinsicWidths { get; }

    private NativeTextParagraphSnapshot(NativePositionedTextGlyph[] glyphs,
        NativePositionedTextLine[] lines, int[] ends, sbyte[] levels,
        ReadOnlyMemory<NativeTextClusterBox> boxes, ReadOnlyMemory<NativeTextCaretStop> carets,
        NativeTextIntrinsicWidths? intrinsicWidths = null)
    {
        Glyphs = glyphs; Lines = lines; ClusterEnds = ends; BidiLevels = levels;
        Boxes = boxes; Carets = carets;
        IntrinsicWidths = intrinsicWidths;
    }

    public static NativeTextParagraphSnapshot Create(NativeTextShapingContext context,
        ReadOnlySpan<char> text, NativeTextDirection direction, in NativeTextParagraphOptions options,
        ReadOnlySpan<NativeTextFeature> features = default,
        ReadOnlySpan<NativeTextParagraphStyle> styles = default,
        float incrementalTab = 0, float tabOrigin = 0, bool measureIntrinsicWidths = false,
        NativeTextWrapping wrapping = NativeTextWrapping.Emergency)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (wrapping is not NativeTextWrapping.Emergency and not NativeTextWrapping.WholeWord)
            throw new ArgumentOutOfRangeException(nameof(wrapping));
        if (!float.IsFinite(incrementalTab) || incrementalTab < 0 || !float.IsFinite(tabOrigin))
            throw new ArgumentException("Tab interval and origin must be finite; the interval cannot be negative.");
        if (text.Length > 1 << 20 || options.MaximumLines != 0 ||
            options.Trimming != NativeTextTrimming.None)
            throw new ArgumentException("Editor snapshots require an untruncated paragraph within the input budget.");
        if (text.IsEmpty)
        {
            if (!styles.IsEmpty) throw new ArgumentException("An empty paragraph cannot contain nonempty style ranges.");
            return new([], [new NativePositionedTextLine { Height = options.LineHeight }], [], [],
                ReadOnlyMemory<NativeTextClusterBox>.Empty, ReadOnlyMemory<NativeTextCaretStop>.Empty,
                measureIntrinsicWidths ? new NativeTextIntrinsicWidths { StructSize = (uint)Unsafe.SizeOf<NativeTextIntrinsicWidths>() } : null);
        }
        var scalars = new NativeTextScalar[text.Length];
        int count = DecodeUtf16(text, scalars);
        var nativeStyles = MapStyles(styles, scalars.AsSpan(0, count), text.Length);
        var input = new NativeTextShapeInput(default, scalars.AsSpan(0, count), direction: direction, features: features);
        var flow = new NativeTextFlowOptions { IncrementalTab = incrementalTab, TabOrigin = tabOrigin };
        NativeTextParagraphRequirements required;
        Check(incrementalTab > 0 ? context.GetFlowParagraphRequirements(in input, in options, nativeStyles, in flow, out required) :
            context.GetStyledParagraphRequirements(in input, in options, nativeStyles, out required));
        var glyphBuffer = new NativePositionedTextGlyph[checked((int)required.GlyphCapacity)];
        var lineBuffer = new NativePositionedTextLine[checked((int)required.LineCapacity)];
        byte[] scratch = ArrayPool<byte>.Shared.Rent(checked((int)required.ScratchBytes));
        NativeTextParagraphResult result;
        NativeTextIntrinsicWidths? intrinsicWidths = null;
        try
        {
            if (measureIntrinsicWidths || wrapping != NativeTextWrapping.Emergency)
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
        Check(NativeTextInteractionInterop.GetRequirements(in interaction, out var interactionRequired));
        var boxes = new NativeTextClusterBox[checked((int)interactionRequired.ClusterBoxCapacity)];
        var carets = new NativeTextCaretStop[checked((int)interactionRequired.CaretStopCapacity)];
        Check(NativeTextInteractionInterop.Build(in interaction, boxes, carets, out var interactionResult));
        return new(glyphBuffer, lineBuffer, ends, levels,
            boxes.AsMemory(0, checked((int)interactionResult.ClusterBoxCount)),
            carets.AsMemory(0, checked((int)interactionResult.CaretStopCount)), intrinsicWidths);
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

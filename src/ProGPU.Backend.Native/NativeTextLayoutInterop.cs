using System.Runtime.CompilerServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// Caller-owned options for one synchronous native horizontal layout pass.
/// Glyph metrics remain in the design-unit convention returned by native shaping.
/// </summary>
public readonly ref struct NativeTextLayoutInput
{
    public NativeTextLayoutInput(
        ReadOnlySpan<NativeTextShapingGlyph> glyphs,
        ReadOnlySpan<NativeTextLineBreakKind> breaksAfter,
        float scale,
        float maximumWidth = 0,
        float lineHeight = 0,
        uint maximumLines = 0,
        NativeTextDirection direction = NativeTextDirection.LeftToRight,
        NativeTextTrimming trimming = NativeTextTrimming.None,
        NativeTextAlignment alignment = NativeTextAlignment.Left,
        uint ellipsisGlyphId = 0,
        float ellipsisAdvance = 0)
    {
        Glyphs = glyphs;
        BreaksAfter = breaksAfter;
        Scale = scale;
        MaximumWidth = maximumWidth;
        LineHeight = lineHeight;
        MaximumLines = maximumLines;
        Direction = direction;
        Trimming = trimming;
        Alignment = alignment;
        EllipsisGlyphId = ellipsisGlyphId;
        EllipsisAdvance = ellipsisAdvance;
    }

    public ReadOnlySpan<NativeTextShapingGlyph> Glyphs { get; }
    public ReadOnlySpan<NativeTextLineBreakKind> BreaksAfter { get; }
    public float Scale { get; }
    public float MaximumWidth { get; }
    public float LineHeight { get; }
    public uint MaximumLines { get; }
    public NativeTextDirection Direction { get; }
    public NativeTextTrimming Trimming { get; }
    public NativeTextAlignment Alignment { get; }
    public uint EllipsisGlyphId { get; }
    public float EllipsisAdvance { get; }
}

/// <summary>
/// Batched allocation-free binding to the ProGPU C++ positioned text layout.
/// </summary>
public static unsafe class NativeTextLayoutInterop
{
    public static NativeRendererStatus GetRequirements(
        in NativeTextLayoutInput input,
        out NativeTextLayoutRequirements requirements)
    {
        requirements = new NativeTextLayoutRequirements
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextLayoutRequirements>()
        };
        fixed (NativeTextShapingGlyph* glyphs = input.Glyphs)
        fixed (NativeTextLineBreakKind* breaksAfter = input.BreaksAfter)
        {
            NativeTextLayoutRequest request = CreateRequest(
                in input, glyphs, (byte*)breaksAfter);
            return NativeMethods.GetTextLayoutRequirements(
                &request,
                (NativeTextLayoutRequirements*)Unsafe.AsPointer(ref requirements));
        }
    }

    public static NativeRendererStatus Layout(
        in NativeTextLayoutInput input,
        Span<NativePositionedTextGlyph> glyphs,
        Span<NativePositionedTextLine> lines,
        Span<byte> scratch,
        out NativeTextLayoutResult result)
    {
        result = new NativeTextLayoutResult
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextLayoutResult>()
        };
        fixed (NativeTextShapingGlyph* inputGlyphs = input.Glyphs)
        fixed (NativeTextLineBreakKind* breaksAfter = input.BreaksAfter)
        fixed (NativePositionedTextGlyph* outputGlyphs = glyphs)
        fixed (NativePositionedTextLine* outputLines = lines)
        fixed (byte* scratchData = scratch)
        {
            NativeTextLayoutRequest request = CreateRequest(
                in input, inputGlyphs, (byte*)breaksAfter);
            return NativeMethods.LayoutText(
                &request,
                outputGlyphs,
                checked((uint)glyphs.Length),
                outputLines,
                checked((uint)lines.Length),
                scratchData,
                checked((nuint)scratch.Length),
                (NativeTextLayoutResult*)Unsafe.AsPointer(ref result));
        }
    }

    private static NativeTextLayoutRequest CreateRequest(
        in NativeTextLayoutInput input,
        NativeTextShapingGlyph* glyphs,
        byte* breaksAfter) => new()
    {
        StructSize = (uint)Unsafe.SizeOf<NativeTextLayoutRequest>(),
        AbiVersion = NativeMethods.AbiVersion,
        Glyphs = (nuint)glyphs,
        GlyphCount = checked((uint)input.Glyphs.Length),
        BreaksAfter = (nuint)breaksAfter,
        BreakCount = checked((uint)input.BreaksAfter.Length),
        Scale = input.Scale,
        MaximumWidth = input.MaximumWidth,
        LineHeight = input.LineHeight,
        MaximumLines = input.MaximumLines,
        Direction = (uint)input.Direction,
        Trimming = (uint)input.Trimming,
        Alignment = (uint)input.Alignment,
        EllipsisGlyphId = input.EllipsisGlyphId,
        EllipsisAdvance = input.EllipsisAdvance
    };
}

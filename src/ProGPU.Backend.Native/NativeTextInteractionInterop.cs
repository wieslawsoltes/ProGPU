using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

/// <summary>Metadata is per positioned glyph, with original UTF input offsets.</summary>
public readonly ref struct NativeTextInteractionInput(
    ReadOnlySpan<NativePositionedTextGlyph> glyphs,
    ReadOnlySpan<NativePositionedTextLine> lines,
    ReadOnlySpan<int> clusterEnds,
    ReadOnlySpan<sbyte> bidiLevels)
{
    public ReadOnlySpan<NativePositionedTextGlyph> Glyphs { get; } = glyphs;
    public ReadOnlySpan<NativePositionedTextLine> Lines { get; } = lines;
    public ReadOnlySpan<int> ClusterEnds { get; } = clusterEnds;
    public ReadOnlySpan<sbyte> BidiLevels { get; } = bidiLevels;
}

/// <summary>
/// Borrowed, caller-owned access to shared C++ cluster/caret/hit/selection work.
/// Buffers must not overlap. Publish output only on Success, using returned counts.
/// No device, context handle, glyph repacking or per-glyph calls are involved.
/// </summary>
public static unsafe class NativeTextInteractionInterop
{
    public static NativeRendererStatus GetRequirements(in NativeTextInteractionInput input,
        out NativeTextInteractionRequirements result)
        => GetRequirementsCore(in input, out result, false);

    /// <summary>Uses measured paragraph baselines and the prefix of actual line heights.</summary>
    public static NativeRendererStatus GetMeasuredRequirements(in NativeTextInteractionInput input,
        out NativeTextInteractionRequirements result)
        => GetRequirementsCore(in input, out result, true);

    private static NativeRendererStatus GetRequirementsCore(in NativeTextInteractionInput input,
        out NativeTextInteractionRequirements result, bool measuredLines)
    {
        result = new() { StructSize = (uint)Unsafe.SizeOf<NativeTextInteractionRequirements>() };
        fixed (NativePositionedTextGlyph* glyphs = input.Glyphs)
        fixed (NativePositionedTextLine* lines = input.Lines)
        fixed (int* ends = input.ClusterEnds)
        fixed (sbyte* levels = input.BidiLevels)
        fixed (NativeTextInteractionRequirements* output = &result)
        {
            var request = Request(in input, glyphs, lines, ends, levels);
            return measuredLines
                ? NativeMethods.GetMeasuredTextInteractionRequirements(&request, output)
                : NativeMethods.GetTextInteractionRequirements(&request, output);
        }
    }

    public static NativeRendererStatus Build(in NativeTextInteractionInput input,
        Span<NativeTextClusterBox> boxes, Span<NativeTextCaretStop> carets,
        out NativeTextInteractionResult result)
        => BuildCore(in input, boxes, carets, out result, false);

    /// <summary>Builds interaction at measured line tops without repacking positioned glyphs.</summary>
    public static NativeRendererStatus BuildMeasured(in NativeTextInteractionInput input,
        Span<NativeTextClusterBox> boxes, Span<NativeTextCaretStop> carets,
        out NativeTextInteractionResult result)
        => BuildCore(in input, boxes, carets, out result, true);

    /// <summary>Uses retained fragment tops and row identity without stacking fragments.</summary>
    public static NativeRendererStatus BuildFragments(in NativeTextInteractionInput input,
        ReadOnlySpan<NativeTextFragmentPlacement> fragments,
        Span<NativeTextClusterBox> boxes, Span<NativeTextCaretStop> carets,
        out NativeTextInteractionResult result)
    {
        result = new() { StructSize = (uint)Unsafe.SizeOf<NativeTextInteractionResult>() };
        fixed (NativePositionedTextGlyph* glyphs = input.Glyphs)
        fixed (NativePositionedTextLine* lines = input.Lines)
        fixed (int* ends = input.ClusterEnds)
        fixed (sbyte* levels = input.BidiLevels)
        fixed (NativeTextFragmentPlacement* placements = fragments)
        fixed (NativeTextClusterBox* boxOutput = boxes)
        fixed (NativeTextCaretStop* caretOutput = carets)
        fixed (NativeTextInteractionResult* output = &result)
        {
            var request = Request(in input, glyphs, lines, ends, levels);
            return NativeMethods.BuildFragmentTextInteraction(&request, placements, (uint)fragments.Length,
                boxOutput, (uint)boxes.Length, caretOutput, (uint)carets.Length, output);
        }
    }

    private static NativeRendererStatus BuildCore(in NativeTextInteractionInput input,
        Span<NativeTextClusterBox> boxes, Span<NativeTextCaretStop> carets,
        out NativeTextInteractionResult result, bool measuredLines)
    {
        result = new() { StructSize = (uint)Unsafe.SizeOf<NativeTextInteractionResult>() };
        fixed (NativePositionedTextGlyph* glyphs = input.Glyphs)
        fixed (NativePositionedTextLine* lines = input.Lines)
        fixed (int* ends = input.ClusterEnds)
        fixed (sbyte* levels = input.BidiLevels)
        fixed (NativeTextClusterBox* boxOutput = boxes)
        fixed (NativeTextCaretStop* caretOutput = carets)
        fixed (NativeTextInteractionResult* output = &result)
        {
            var request = Request(in input, glyphs, lines, ends, levels);
            return measuredLines
                ? NativeMethods.BuildMeasuredTextInteraction(&request, boxOutput, (uint)boxes.Length,
                    caretOutput, (uint)carets.Length, output)
                : NativeMethods.BuildTextInteraction(&request, boxOutput, (uint)boxes.Length,
                    caretOutput, (uint)carets.Length, output);
        }
    }

    public static NativeRendererStatus HitTest(ReadOnlySpan<NativeTextClusterBox> boxes,
        float x, float y, out NativeTextHitTestResult result)
    {
        result = default;
        fixed (NativeTextClusterBox* input = boxes)
        fixed (NativeTextHitTestResult* output = &result)
            return NativeMethods.HitTestTextInteraction(input, (uint)boxes.Length, x, y, output);
    }

    public static NativeRendererStatus GetCaret(ReadOnlySpan<NativeTextCaretStop> carets,
        int position, bool trailingAffinity, out NativeTextCaretStop result)
    {
        result = default;
        fixed (NativeTextCaretStop* input = carets)
        fixed (NativeTextCaretStop* output = &result)
            return NativeMethods.GetTextInteractionCaret(input, (uint)carets.Length, position,
                trailingAffinity ? (byte)1 : (byte)0, output);
    }

    /// <summary>Direction is -1 (previous), 0 (current), or 1 (next), in visual order.</summary>
    public static NativeRendererStatus MoveCaret(ReadOnlySpan<NativeTextCaretStop> carets,
        int position, bool trailingAffinity, int direction, out NativeTextCaretStop result)
    {
        result = default;
        fixed (NativeTextCaretStop* input = carets)
        fixed (NativeTextCaretStop* output = &result)
            return NativeMethods.MoveTextInteractionCaret(input, (uint)carets.Length, position,
                trailingAffinity ? (byte)1 : (byte)0, direction, output);
    }

    /// <summary>Moves within one retained fragment generation; paragraph level is 0 or 1.</summary>
    public static NativeRendererStatus MoveFragmentCaret(ReadOnlySpan<NativeTextCaretStop> carets,
        ReadOnlySpan<NativeTextFragmentPlacement> fragments, uint currentIndex,
        NativeTextCaretMovement direction, sbyte paragraphLevel, float preferredX, out uint nextIndex)
    {
        nextIndex = 0;
        fixed (NativeTextCaretStop* input = carets)
        fixed (NativeTextFragmentPlacement* placements = fragments)
        fixed (uint* output = &nextIndex)
            return NativeMethods.MoveFragmentTextInteractionCaret(input, (uint)carets.Length,
                placements, (uint)fragments.Length, currentIndex, (uint)direction, paragraphLevel,
                preferredX, output);
    }

    public static NativeRendererStatus GetSelection(ReadOnlySpan<NativeTextClusterBox> boxes,
        int start, int end, Span<NativeTextRectangle> rectangles, out uint written)
    {
        written = 0;
        fixed (NativeTextClusterBox* input = boxes)
        fixed (NativeTextRectangle* output = rectangles)
        fixed (uint* count = &written)
            return NativeMethods.GetTextInteractionSelection(input, (uint)boxes.Length,
                start, end, output, (uint)rectangles.Length, count);
    }

    private static NativeTextInteractionRequest Request(in NativeTextInteractionInput input,
        NativePositionedTextGlyph* glyphs, NativePositionedTextLine* lines, int* ends, sbyte* levels) => new()
    {
        StructSize = (uint)Unsafe.SizeOf<NativeTextInteractionRequest>(),
        AbiVersion = NativeMethods.AbiVersion,
        Glyphs = (nuint)glyphs, GlyphCount = (uint)input.Glyphs.Length,
        Lines = (nuint)lines, LineCount = (uint)input.Lines.Length,
        ClusterEnds = (nuint)ends, ClusterEndCount = (uint)input.ClusterEnds.Length,
        BidiLevels = (nuint)levels, BidiLevelCount = (uint)input.BidiLevels.Length
    };
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_interaction_build_fragments")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BuildFragmentTextInteraction(NativeTextInteractionRequest* request,
        NativeTextFragmentPlacement* fragments, uint fragmentCount,
        NativeTextClusterBox* boxes, uint boxCapacity, NativeTextCaretStop* carets, uint caretCapacity,
        NativeTextInteractionResult* result);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_interaction_get_measured_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetMeasuredTextInteractionRequirements(
        NativeTextInteractionRequest* request, NativeTextInteractionRequirements* result);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_interaction_build_measured")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BuildMeasuredTextInteraction(NativeTextInteractionRequest* request,
        NativeTextClusterBox* boxes, uint boxCapacity, NativeTextCaretStop* carets, uint caretCapacity,
        NativeTextInteractionResult* result);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_interaction_get_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetTextInteractionRequirements(
        NativeTextInteractionRequest* request, NativeTextInteractionRequirements* result);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_interaction_build")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BuildTextInteraction(NativeTextInteractionRequest* request,
        NativeTextClusterBox* boxes, uint boxCapacity, NativeTextCaretStop* carets, uint caretCapacity,
        NativeTextInteractionResult* result);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_interaction_hit_test")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus HitTestTextInteraction(NativeTextClusterBox* boxes,
        uint count, float x, float y, NativeTextHitTestResult* result);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_interaction_get_caret")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetTextInteractionCaret(NativeTextCaretStop* carets,
        uint count, int position, byte trailing, NativeTextCaretStop* result);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_interaction_move_caret")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus MoveTextInteractionCaret(NativeTextCaretStop* carets,
        uint count, int position, byte trailing, int direction, NativeTextCaretStop* result);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_interaction_move_fragment_caret")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus MoveFragmentTextInteractionCaret(NativeTextCaretStop* carets,
        uint count, NativeTextFragmentPlacement* fragments, uint fragmentCount, uint currentIndex,
        uint direction, sbyte paragraphLevel, float preferredX, uint* nextIndex);
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_interaction_get_selection")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetTextInteractionSelection(NativeTextClusterBox* boxes,
        uint count, int start, int end, NativeTextRectangle* rectangles, uint capacity, uint* written);
}

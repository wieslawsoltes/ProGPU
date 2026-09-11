using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public sealed unsafe partial class NativeTextShapingContext
{
    /// <summary>Styles partition scalar indices, not UTF-16 indices. Font indices are owned context faces.</summary>
    public NativeRendererStatus GetStyledParagraphRequirements(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        out NativeTextParagraphRequirements requirements)
    {
        using var use = _owner.Acquire();
        requirements = new() { StructSize = (uint)Unsafe.SizeOf<NativeTextParagraphRequirements>() };
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* pre = input.PreContext)
        fixed (NativeTextScalar* post = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        fixed (NativeTextStyleRun* styleData = styles)
        fixed (NativeTextParagraphRequirements* output = &requirements)
        {
            var shaping = NativeTextShapingInterop.CreateRequest(in input, null, scalars, pre, post,
                features, coordinates, null, includeOwnedResources: false);
            var layout = CreateParagraphLayoutOptions(in input, in options);
            return NativeMethods.GetStyledParagraphRequirements(use.Handle, &shaping, &layout,
                styleData, checked((uint)styles.Length), output);
        }
    }

    /// <summary>Shared native bidi/script/style shaping and floating-point mixed-scale wrapping.</summary>
    public NativeRendererStatus LayoutStyledParagraph(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        Span<NativePositionedTextGlyph> glyphs, Span<NativePositionedTextLine> lines,
        Span<byte> scratch, out NativeTextParagraphResult result)
    {
        using var use = _owner.Acquire();
        result = new() { StructSize = (uint)Unsafe.SizeOf<NativeTextParagraphResult>() };
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* pre = input.PreContext)
        fixed (NativeTextScalar* post = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        fixed (NativeTextStyleRun* styleData = styles)
        fixed (NativePositionedTextGlyph* positioned = glyphs)
        fixed (NativePositionedTextLine* positionedLines = lines)
        fixed (byte* scratchData = scratch)
        fixed (NativeTextParagraphResult* output = &result)
        {
            var shaping = NativeTextShapingInterop.CreateRequest(in input, null, scalars, pre, post,
                features, coordinates, null, includeOwnedResources: false);
            var layout = CreateParagraphLayoutOptions(in input, in options);
            return NativeMethods.LayoutStyledParagraph(use.Handle, &shaping, &layout, styleData,
                checked((uint)styles.Length), positioned, checked((uint)glyphs.Length),
                positionedLines, checked((uint)lines.Length), scratchData, checked((nuint)scratch.Length), output);
        }
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_get_styled_paragraph_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetStyledParagraphRequirements(nint context,
        NativeTextShapeRequest* shaping, NativeTextLayoutOptions* layout, NativeTextStyleRun* styles,
        uint styleCount, NativeTextParagraphRequirements* result);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_layout_styled_paragraph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus LayoutStyledParagraph(nint context,
        NativeTextShapeRequest* shaping, NativeTextLayoutOptions* layout, NativeTextStyleRun* styles,
        uint styleCount, NativePositionedTextGlyph* glyphs, uint glyphCapacity,
        NativePositionedTextLine* lines, uint lineCapacity, void* scratch, nuint scratchSize,
        NativeTextParagraphResult* result);
}

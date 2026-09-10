using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public sealed unsafe partial class NativeTextShapingContext
{
    public NativeRendererStatus GetFlowParagraphRequirements(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        in NativeTextFlowOptions flowOptions, out NativeTextParagraphRequirements requirements)
        => GetFlowParagraphRequirementsCore(in input, in options, styles, in flowOptions, out requirements);

    public NativeRendererStatus GetInlineFlowParagraphRequirements(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        in NativeTextFlowOptions flowOptions, ReadOnlySpan<NativeTextStyleMetrics> metrics,
        ReadOnlySpan<NativeTextInlineObject> objects, out NativeTextParagraphRequirements requirements)
        => GetFlowParagraphRequirementsCore(in input, in options, styles, in flowOptions,
            out requirements, true, metrics, objects);

    private NativeRendererStatus GetFlowParagraphRequirementsCore(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        in NativeTextFlowOptions flowOptions, out NativeTextParagraphRequirements requirements,
        bool inline = false, ReadOnlySpan<NativeTextStyleMetrics> metrics = default,
        ReadOnlySpan<NativeTextInlineObject> objects = default)
    {
        if (inline && metrics.Length != styles.Length)
            throw new ArgumentException("Each style requires one metric pair.", nameof(metrics));
        using var use = _owner.Acquire();
        requirements = new() { StructSize = (uint)Unsafe.SizeOf<NativeTextParagraphRequirements>() };
        var flow = flowOptions; flow.StructSize = (uint)Unsafe.SizeOf<NativeTextFlowOptions>();
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* pre = input.PreContext)
        fixed (NativeTextScalar* post = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        fixed (NativeTextStyleRun* styleData = styles)
        fixed (NativeTextStyleMetrics* metricData = metrics)
        fixed (NativeTextInlineObject* objectData = objects)
        fixed (NativeTextParagraphRequirements* output = &requirements)
        {
            var shaping = NativeTextShapingInterop.CreateRequest(in input, null, scalars, pre, post,
                features, coordinates, null, includeOwnedResources: false);
            var layout = CreateParagraphLayoutOptions(in input, in options);
            if (inline)
                return NativeMethods.GetInlineFlowParagraphRequirements(use.Handle, &shaping, &layout, styleData,
                    checked((uint)styles.Length), &flow, metricData, objectData, checked((uint)objects.Length), output);
            return NativeMethods.GetFlowParagraphRequirements(use.Handle, &shaping, &layout, styleData,
                checked((uint)styles.Length), &flow, output);
        }
    }

    public NativeRendererStatus LayoutFlowParagraph(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        in NativeTextFlowOptions flowOptions, Span<NativePositionedTextGlyph> glyphs,
        Span<NativePositionedTextLine> lines, Span<byte> scratch, out NativeTextParagraphResult result)
        => LayoutFlowParagraphCore(in input, in options, styles, in flowOptions, glyphs, lines, scratch,
            NativeTextWrapping.Emergency, false, out result, out _);

    public NativeRendererStatus LayoutConfiguredFlowParagraph(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        in NativeTextFlowOptions flowOptions, Span<NativePositionedTextGlyph> glyphs,
        Span<NativePositionedTextLine> lines, Span<byte> scratch, NativeTextWrapping wrapping,
        bool measureIntrinsicWidths, out NativeTextParagraphResult result,
        out NativeTextIntrinsicWidths widths)
        => LayoutFlowParagraphCore(in input, in options, styles, in flowOptions, glyphs, lines, scratch,
            wrapping, measureIntrinsicWidths, out result, out widths);

    private NativeRendererStatus LayoutFlowParagraphCore(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        in NativeTextFlowOptions flowOptions, Span<NativePositionedTextGlyph> glyphs,
        Span<NativePositionedTextLine> lines, Span<byte> scratch, NativeTextWrapping wrapping, bool measure,
        out NativeTextParagraphResult result, out NativeTextIntrinsicWidths widths, float? collapseWidth = null,
        bool inline = false, ReadOnlySpan<NativeTextStyleMetrics> metrics = default,
        ReadOnlySpan<NativeTextInlineObject> objects = default)
    {
        if (inline && metrics.Length != styles.Length)
            throw new ArgumentException("Each style requires one metric pair.", nameof(metrics));
        using var use = _owner.Acquire();
        result = new() { StructSize = (uint)Unsafe.SizeOf<NativeTextParagraphResult>() };
        widths = new() { StructSize = (uint)Unsafe.SizeOf<NativeTextIntrinsicWidths>() };
        var flow = flowOptions; flow.StructSize = (uint)Unsafe.SizeOf<NativeTextFlowOptions>();
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* pre = input.PreContext)
        fixed (NativeTextScalar* post = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        fixed (NativeTextStyleRun* styleData = styles)
        fixed (NativeTextStyleMetrics* metricData = metrics)
        fixed (NativeTextInlineObject* objectData = objects)
        fixed (NativePositionedTextGlyph* positioned = glyphs)
        fixed (NativePositionedTextLine* positionedLines = lines)
        fixed (byte* scratchData = scratch)
        fixed (NativeTextParagraphResult* output = &result)
        fixed (NativeTextIntrinsicWidths* measured = &widths)
        {
            var shaping = NativeTextShapingInterop.CreateRequest(in input, null, scalars, pre, post,
                features, coordinates, null, includeOwnedResources: false);
            var layout = CreateParagraphLayoutOptions(in input, in options);
            if (inline)
                return NativeMethods.LayoutInlineFlowParagraph(use.Handle, &shaping, &layout, styleData,
                    checked((uint)styles.Length), &flow, metricData, objectData, checked((uint)objects.Length),
                    positioned, checked((uint)glyphs.Length), positionedLines, checked((uint)lines.Length),
                    scratchData, checked((nuint)scratch.Length), output, (uint)wrapping, measure ? measured : null);
            if (collapseWidth is { } width)
                return NativeMethods.LayoutCollapsedFlowParagraph(use.Handle, &shaping, &layout, styleData,
                    checked((uint)styles.Length), &flow, positioned, checked((uint)glyphs.Length),
                    positionedLines, checked((uint)lines.Length), scratchData, checked((nuint)scratch.Length), output,
                    (uint)wrapping, width);
            if (measure || wrapping != NativeTextWrapping.Emergency)
                return NativeMethods.LayoutConfiguredFlowParagraph(use.Handle, &shaping, &layout, styleData,
                checked((uint)styles.Length), &flow, positioned, checked((uint)glyphs.Length),
                positionedLines, checked((uint)lines.Length), scratchData, checked((nuint)scratch.Length), output,
                (uint)wrapping, measure ? measured : null);
            return NativeMethods.LayoutFlowParagraph(use.Handle, &shaping, &layout, styleData,
                checked((uint)styles.Length), &flow, positioned, checked((uint)glyphs.Length),
                positionedLines, checked((uint)lines.Length), scratchData, checked((nuint)scratch.Length), output);
        }
    }

    public NativeRendererStatus LayoutCollapsedFlowParagraph(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        in NativeTextFlowOptions flowOptions, Span<NativePositionedTextGlyph> glyphs,
        Span<NativePositionedTextLine> lines, Span<byte> scratch, NativeTextWrapping wrapping,
        float collapseWidth, out NativeTextParagraphResult result)
        => LayoutFlowParagraphCore(in input, in options, styles, in flowOptions, glyphs, lines, scratch,
            wrapping, false, out result, out _, collapseWidth);

    public NativeRendererStatus LayoutInlineFlowParagraph(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        in NativeTextFlowOptions flowOptions, ReadOnlySpan<NativeTextStyleMetrics> metrics,
        ReadOnlySpan<NativeTextInlineObject> objects, Span<NativePositionedTextGlyph> glyphs,
        Span<NativePositionedTextLine> lines, Span<byte> scratch, NativeTextWrapping wrapping,
        bool measureIntrinsicWidths, out NativeTextParagraphResult result, out NativeTextIntrinsicWidths widths)
        => LayoutFlowParagraphCore(in input, in options, styles, in flowOptions, glyphs, lines, scratch,
            wrapping, measureIntrinsicWidths, out result, out widths, null, true, metrics, objects);
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_get_inline_flow_paragraph_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetInlineFlowParagraphRequirements(nint context,
        NativeTextShapeRequest* shaping, NativeTextLayoutOptions* layout, NativeTextStyleRun* styles,
        uint styleCount, NativeTextFlowOptions* flow, NativeTextStyleMetrics* metrics,
        NativeTextInlineObject* objects, uint objectCount, NativeTextParagraphRequirements* result);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_layout_inline_flow_paragraph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus LayoutInlineFlowParagraph(nint context,
        NativeTextShapeRequest* shaping, NativeTextLayoutOptions* layout, NativeTextStyleRun* styles,
        uint styleCount, NativeTextFlowOptions* flow, NativeTextStyleMetrics* metrics,
        NativeTextInlineObject* objects, uint objectCount, NativePositionedTextGlyph* glyphs, uint glyphCapacity,
        NativePositionedTextLine* lines, uint lineCapacity, void* scratch, nuint scratchSize,
        NativeTextParagraphResult* result, uint wrapping, NativeTextIntrinsicWidths* widths);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_layout_collapsed_flow_paragraph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus LayoutCollapsedFlowParagraph(nint context,
        NativeTextShapeRequest* shaping, NativeTextLayoutOptions* layout, NativeTextStyleRun* styles,
        uint styleCount, NativeTextFlowOptions* flow, NativePositionedTextGlyph* glyphs, uint glyphCapacity,
        NativePositionedTextLine* lines, uint lineCapacity, void* scratch, nuint scratchSize,
        NativeTextParagraphResult* result, uint wrapping, float collapseWidth);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_get_flow_paragraph_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetFlowParagraphRequirements(nint context,
        NativeTextShapeRequest* shaping, NativeTextLayoutOptions* layout, NativeTextStyleRun* styles,
        uint styleCount, NativeTextFlowOptions* flow, NativeTextParagraphRequirements* result);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_layout_flow_paragraph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus LayoutFlowParagraph(nint context,
        NativeTextShapeRequest* shaping, NativeTextLayoutOptions* layout, NativeTextStyleRun* styles,
        uint styleCount, NativeTextFlowOptions* flow, NativePositionedTextGlyph* glyphs, uint glyphCapacity,
        NativePositionedTextLine* lines, uint lineCapacity, void* scratch, nuint scratchSize, NativeTextParagraphResult* result);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_layout_configured_flow_paragraph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus LayoutConfiguredFlowParagraph(nint context,
        NativeTextShapeRequest* shaping, NativeTextLayoutOptions* layout, NativeTextStyleRun* styles,
        uint styleCount, NativeTextFlowOptions* flow, NativePositionedTextGlyph* glyphs, uint glyphCapacity,
        NativePositionedTextLine* lines, uint lineCapacity, void* scratch, nuint scratchSize,
        NativeTextParagraphResult* result, uint wrapping, NativeTextIntrinsicWidths* widths);
}

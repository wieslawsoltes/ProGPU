using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public sealed unsafe partial class NativeTextShapingContext
{
    /// <summary>Gets one arena size for source-ordered floating paragraph shaping and layout.</summary>
    public NativeRendererStatus GetFloatingFlowParagraphRequirements(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        in NativeTextFlowOptions flowOptions, ReadOnlySpan<NativeTextStyleMetrics> metrics,
        ReadOnlySpan<NativeTextInlineObject> objects, in NativeTextFloatingOptions floatingOptions,
        ReadOnlySpan<NativeTextFloatingItem> events, ReadOnlySpan<NativeTextExclusionRectangle> exclusions,
        out NativeTextParagraphRequirements requirements)
    {
        if (metrics.Length != styles.Length)
            throw new ArgumentException("Each style requires one metric pair.", nameof(metrics));
        using var use = _owner.Acquire();
        requirements = new() { StructSize = (uint)Unsafe.SizeOf<NativeTextParagraphRequirements>() };
        var flow = flowOptions; flow.StructSize = (uint)Unsafe.SizeOf<NativeTextFlowOptions>();
        var floating = floatingOptions; floating.StructSize = (uint)Unsafe.SizeOf<NativeTextFloatingOptions>();
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* pre = input.PreContext)
        fixed (NativeTextScalar* post = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        fixed (NativeTextStyleRun* styleData = styles)
        fixed (NativeTextStyleMetrics* metricData = metrics)
        fixed (NativeTextInlineObject* objectData = objects)
        fixed (NativeTextFloatingItem* eventData = events)
        fixed (NativeTextExclusionRectangle* exclusionData = exclusions)
        fixed (NativeTextParagraphRequirements* output = &requirements)
        {
            var shaping = NativeTextShapingInterop.CreateRequest(in input, null, scalars, pre, post,
                features, coordinates, null, includeOwnedResources: false);
            var layout = CreateParagraphLayoutOptions(in input, in options);
            return NativeMethods.GetFloatingFlowParagraphRequirements(use.Handle, &shaping, &layout,
                styleData, checked((uint)styles.Length), &flow, metricData, objectData, checked((uint)objects.Length),
                &floating, eventData, checked((uint)events.Length), exclusionData, checked((uint)exclusions.Length), output);
        }
    }

    /// <summary>
    /// Shapes and fits one complete floating paragraph under a single context lease.
    /// Event indices are scalar boundaries, not UTF-16 offsets. Lines are fragments;
    /// result describes parent text and floatingResult also includes child extents.
    /// </summary>
    public NativeRendererStatus LayoutFloatingFlowParagraph(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        in NativeTextFlowOptions flowOptions, ReadOnlySpan<NativeTextStyleMetrics> metrics,
        ReadOnlySpan<NativeTextInlineObject> objects, in NativeTextFloatingOptions floatingOptions,
        ReadOnlySpan<NativeTextFloatingItem> events, ReadOnlySpan<NativeTextExclusionRectangle> exclusions,
        Span<NativePositionedTextGlyph> glyphs, Span<NativePositionedTextLine> lines,
        Span<NativeTextFragmentPlacement> fragments, Span<NativeTextFloatingPlacement> floats,
        Span<byte> scratch, NativeTextWrapping wrapping, out NativeTextParagraphResult result,
        out NativeTextFloatingResult floatingResult)
    {
        if (metrics.Length != styles.Length)
            throw new ArgumentException("Each style requires one metric pair.", nameof(metrics));
        using var use = _owner.Acquire();
        result = new() { StructSize = (uint)Unsafe.SizeOf<NativeTextParagraphResult>() };
        floatingResult = new() { StructSize = (uint)Unsafe.SizeOf<NativeTextFloatingResult>() };
        var flow = flowOptions; flow.StructSize = (uint)Unsafe.SizeOf<NativeTextFlowOptions>();
        var floating = floatingOptions; floating.StructSize = (uint)Unsafe.SizeOf<NativeTextFloatingOptions>();
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* pre = input.PreContext)
        fixed (NativeTextScalar* post = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        fixed (NativeTextStyleRun* styleData = styles)
        fixed (NativeTextStyleMetrics* metricData = metrics)
        fixed (NativeTextInlineObject* objectData = objects)
        fixed (NativeTextFloatingItem* eventData = events)
        fixed (NativeTextExclusionRectangle* exclusionData = exclusions)
        fixed (NativePositionedTextGlyph* glyphData = glyphs)
        fixed (NativePositionedTextLine* lineData = lines)
        fixed (NativeTextFragmentPlacement* fragmentData = fragments)
        fixed (NativeTextFloatingPlacement* floatData = floats)
        fixed (byte* scratchData = scratch)
        fixed (NativeTextParagraphResult* output = &result)
        fixed (NativeTextFloatingResult* floatOutput = &floatingResult)
        {
            var shaping = NativeTextShapingInterop.CreateRequest(in input, null, scalars, pre, post,
                features, coordinates, null, includeOwnedResources: false);
            var layout = CreateParagraphLayoutOptions(in input, in options);
            return NativeMethods.LayoutFloatingFlowParagraph(use.Handle, &shaping, &layout,
                styleData, checked((uint)styles.Length), &flow, metricData, objectData, checked((uint)objects.Length),
                &floating, eventData, checked((uint)events.Length), exclusionData, checked((uint)exclusions.Length),
                glyphData, checked((uint)glyphs.Length), lineData, checked((uint)lines.Length),
                fragmentData, checked((uint)fragments.Length), floatData, checked((uint)floats.Length),
                scratchData, checked((nuint)scratch.Length), output, floatOutput, (uint)wrapping);
        }
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_get_floating_flow_paragraph_requirements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetFloatingFlowParagraphRequirements(nint context,
        NativeTextShapeRequest* shaping, NativeTextLayoutOptions* layout, NativeTextStyleRun* styles,
        uint styleCount, NativeTextFlowOptions* flow, NativeTextStyleMetrics* metrics,
        NativeTextInlineObject* objects, uint objectCount, NativeTextFloatingOptions* options,
        NativeTextFloatingItem* events, uint eventCount, NativeTextExclusionRectangle* exclusions,
        uint exclusionCount, NativeTextParagraphRequirements* requirements);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_layout_floating_flow_paragraph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus LayoutFloatingFlowParagraph(nint context,
        NativeTextShapeRequest* shaping, NativeTextLayoutOptions* layout, NativeTextStyleRun* styles,
        uint styleCount, NativeTextFlowOptions* flow, NativeTextStyleMetrics* metrics,
        NativeTextInlineObject* objects, uint objectCount, NativeTextFloatingOptions* options,
        NativeTextFloatingItem* events, uint eventCount, NativeTextExclusionRectangle* exclusions,
        uint exclusionCount, NativePositionedTextGlyph* glyphs, uint glyphCapacity,
        NativePositionedTextLine* lines, uint lineCapacity, NativeTextFragmentPlacement* fragments,
        uint fragmentCapacity, NativeTextFloatingPlacement* floats, uint floatCapacity,
        void* scratch, nuint scratchSize, NativeTextParagraphResult* result,
        NativeTextFloatingResult* floatingResult, uint wrapping);
}

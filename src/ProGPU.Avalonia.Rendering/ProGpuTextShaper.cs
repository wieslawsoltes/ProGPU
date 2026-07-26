using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using ShapingOpenTypeTag = ProGPU.Text.Shaping.OpenTypeTag;

namespace Avalonia.ProGpu
{
    /// <summary>
    /// Shapes Avalonia text runs with ProGPU's deterministic managed OpenType
    /// shaper. The implementation is CPU-only, reflection-free, and safe for
    /// trimming and NativeAOT.
    /// </summary>
    public sealed class ProGpuTextShaper : ITextShaperImpl
    {
        [ThreadStatic]
        private static ShapingBuffer? s_buffer;

        public ShapedBuffer ShapeText(ReadOnlyMemory<char> text, TextShaperOptions options)
        {
#if AVALONIA11
            var typeface = options.Typeface as ProGpuTypeface
                ?? throw new NotSupportedException(
                    "ProGPU text shaping requires a glyph typeface created by the ProGPU font manager.");
            var glyphTypeface = options.Typeface;
#else
            var glyphTypeface = options.GlyphTypeface;
            var shapingTypeface = glyphTypeface.TextShaperTypeface as ProGpuTextShaperTypeface
                ?? throw new NotSupportedException(
                    "The glyph typeface was not created by the ProGPU text shaper.");
            var typeface = shapingTypeface.PlatformTypeface;
#endif
            if (text.IsEmpty)
            {
                return new ShapedBuffer(
                    text,
                    0,
                    glyphTypeface,
                    options.FontRenderingEmSize,
                    options.BidiLevel);
            }

            ReadOnlyMemory<char> containingText = GetContainingMemory(text, out int start, out int length);
            ReadOnlyMemory<char> preContext = containingText[..start];
            ReadOnlyMemory<char> postContext = containingText[(start + length)..];
            ReadOnlyMemory<ShapingFeature> features = CreateFeatures(options);
            var request = new ShapingRequest(
                (options.BidiLevel & 1) == 0
                    ? ShapingDirection.LeftToRight
                    : ShapingDirection.RightToLeft,
                ShapingOpenTypeTag.DefaultScript,
                (options.Culture ?? CultureInfo.CurrentCulture).Name,
                ShapingClusterLevel.MonotoneGraphemes,
                ShapingBufferFlags.None,
                features,
                preContext,
                postContext);
            ShapingBuffer buffer = s_buffer ??= new ShapingBuffer();

            CpuOpenTypeShaper.Instance.Shape(text.Span, typeface.ShapingFace, request, buffer);

            double textScale = options.FontRenderingEmSize / typeface.ShapingFace.UnitsPerEm;
            var shapedBuffer = new ShapedBuffer(
                text,
                buffer.Count,
                glyphTypeface,
                options.FontRenderingEmSize,
                options.BidiLevel);

            ReadOnlySpan<ShapingGlyph> glyphs = buffer.Glyphs;
            for (var index = 0; index < glyphs.Length; index++)
            {
                ShapingGlyph glyph = glyphs[index];
                ushort glyphIndex = checked((ushort)glyph.GlyphId);
                double glyphAdvance = glyph.AdvanceX * textScale + options.LetterSpacing;
                var glyphOffset = new Vector(
                    glyph.OffsetX * textScale,
                    glyph.OffsetY * textScale);

                if ((uint)glyph.Cluster < (uint)text.Length && text.Span[glyph.Cluster] == '\t')
                {
                    glyphIndex = typeface.Font.GetGlyphIndex(' ');
                    glyphAdvance = options.IncrementalTabWidth > 0
                        ? options.IncrementalTabWidth
                        : 4 * typeface.ShapingFace.GetHorizontalAdvance(glyphIndex) * textScale;
                }

                shapedBuffer[index] = new Avalonia.Media.TextFormatting.GlyphInfo(
                    glyphIndex,
                    glyph.Cluster,
                    glyphAdvance,
                    glyphOffset);
            }

            return shapedBuffer;
        }

#if !AVALONIA11
        public ITextShaperTypeface CreateTypeface(GlyphTypeface glyphTypeface)
        {
            ArgumentNullException.ThrowIfNull(glyphTypeface);
            if (glyphTypeface.PlatformTypeface is not ProGpuTypeface platformTypeface)
            {
                throw new NotSupportedException(
                    "ProGPU text shaping requires a glyph typeface created by the ProGPU font manager.");
            }
            return new ProGpuTextShaperTypeface(platformTypeface);
        }

        private sealed class ProGpuTextShaperTypeface : ITextShaperTypeface
        {
            public ProGpuTextShaperTypeface(ProGpuTypeface platformTypeface)
            {
                PlatformTypeface = platformTypeface;
            }

            public ProGpuTypeface PlatformTypeface { get; }

            public void Dispose()
            {
                // The Avalonia GlyphTypeface owns the platform typeface. This
                // adapter only borrows its immutable parsed font.
            }
        }
#endif

        private static ReadOnlyMemory<ShapingFeature> CreateFeatures(TextShaperOptions options)
        {
            if (options.FontFeatures is not { Count: > 0 } requested)
            {
                return ReadOnlyMemory<ShapingFeature>.Empty;
            }

            var features = new ShapingFeature[requested.Count];
            for (var index = 0; index < requested.Count; index++)
            {
                FontFeature feature = requested[index];
                if (!ShapingOpenTypeTag.TryParse(feature.Tag, out ShapingOpenTypeTag tag))
                {
                    throw new ArgumentException(
                        $"'{feature.Tag}' is not a four-character OpenType feature tag.",
                        nameof(options));
                }

                uint start = feature.Start < 0 ? 0u : checked((uint)feature.Start);
                uint end = feature.End < 0 ? uint.MaxValue : checked((uint)feature.End);
                features[index] = new ShapingFeature(tag, unchecked((uint)feature.Value), start, end);
            }

            return features;
        }

        private static ReadOnlyMemory<char> GetContainingMemory(
            ReadOnlyMemory<char> memory,
            out int start,
            out int length)
        {
            if (MemoryMarshal.TryGetString(memory, out string? containingString, out start, out length))
            {
                return containingString.AsMemory();
            }

            if (MemoryMarshal.TryGetArray(memory, out ArraySegment<char> segment))
            {
                start = segment.Offset;
                length = segment.Count;
                return segment.Array.AsMemory();
            }

            if (MemoryMarshal.TryGetMemoryManager(
                    memory,
                    out System.Buffers.MemoryManager<char>? memoryManager,
                    out start,
                    out length))
            {
                return memoryManager.Memory;
            }

            throw new InvalidOperationException(
                "Text memory is not backed by a string, array, or memory manager.");
        }
    }
}

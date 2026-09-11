# Neutral inline text contract

The retained native paragraph snapshot now has an explicit neutral consumer
capability: IPortableInlineTextFormatting.FormatInline. Text-only providers
remain source-compatible and do not implicitly claim object support.

The caller supplies explicit style ranges, matching physical-font ascent/descent
metrics and ordered UTF-16 U+FFFC object measurements. Spans are borrowed only
during the call. Returned IPortableInlineTextParagraph exposes source-ordered
owned placements, line-top coordinates and separate baseline offsets.
PortableTextGlyph.IsInlineObject identifies non-ink items; its addition preserves
the existing positional constructor and deconstruction. Consumers must never
resolve object sentinel fonts or submit those glyph ids to an atlas.

The native C++ paragraph still owns shaping, variable line placement and
interaction. The interface does not introduce a second composer, source-local
geometry or a fallback path. Ordinary formatting keeps its existing convention.
Measured collapse remains unavailable without sign metrics.

This change supports the WPF provider adapter; it does not itself admit source
InlineUIContainer, Figure/Floater or complete the unchanged application.
The interop project builds with zero warnings/errors. Focused contract tests
cover legacy glyph construction/deconstruction and explicit capability detection.
Full package/platform qualification remains separate.

#ifndef PROGPU_NATIVE_TEXT_FLOW_H
#define PROGPU_NATIVE_TEXT_FLOW_H
#include "progpu_native_text_styles.h"
#ifdef __cplusplus
extern "C" {
#endif

/* PROGPU_CSHARP_STRUCT: Public.NativeTextFlowOptions */
typedef struct progpu_native_text_flow_options {
    uint32_t struct_size;
    float incremental_tab;
    float tab_origin;
    uint32_t reserved;
} progpu_native_text_flow_options;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextIntrinsicWidths */
typedef struct progpu_native_text_intrinsic_widths {
    uint32_t struct_size;
    float minimum;
    float maximum;
    uint32_t reserved;
} progpu_native_text_intrinsic_widths;

/* Mirrors NativeTextWrapping in the managed public enum contract. */
typedef enum progpu_native_text_wrapping {
    PROGPU_NATIVE_TEXT_WRAPPING_EMERGENCY = 0,
    PROGPU_NATIVE_TEXT_WRAPPING_WHOLE_WORD = 1
} progpu_native_text_wrapping;

/* Positive incremental_tab enables fixed leading-edge tab stops in DIPs.
 * tab_origin is text-start indentation relative to that grid. U+0009 remains
 * one non-ink positioned item with glyph_id == UINT32_MAX, its original cluster,
 * actual resolved advance and source face index. Never submit it to a glyph atlas.
 * Zero keeps the previous layout behavior. Custom stops/leaders are not part of
 * this contract. Trimming uses resolved tab advances. Inputs are synchronously borrowed. */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_get_flow_paragraph_requirements(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    uint32_t style_count, const progpu_native_text_flow_options* flow,
    progpu_native_text_paragraph_requirements* requirements);

PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_layout_flow_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    uint32_t style_count, const progpu_native_text_flow_options* flow,
    progpu_native_positioned_text_glyph* glyphs, uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, uint32_t line_capacity,
    void* scratch, size_t scratch_size, progpu_native_text_paragraph_result* result);

/* Same capacities and output as layout_flow_paragraph, with optional intrinsic widths
 * measured from the very same logical shaped glyphs before visual reordering.
 * Caller initializes non-null widths.struct_size. Widths are published only on success.
 * Intrinsic measurement rejects truncated layouts. No additional shaping or scratch
 * is needed. WHOLE_WORD permits overflow until a legal shaping-safe break rather
 * than emergency cluster splitting; zero width remains unbounded in either mode. */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_layout_configured_flow_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    uint32_t style_count, const progpu_native_text_flow_options* flow,
    progpu_native_positioned_text_glyph* glyphs, uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, uint32_t line_capacity,
    void* scratch, size_t scratch_size, progpu_native_text_paragraph_result* result,
    uint32_t wrapping, progpu_native_text_intrinsic_widths* widths);

/* Same capacities as flow layout. Preserve maximum_width for original line breaks;
 * collapse_width constrains only the final maximum_lines line, and may be zero.
 * Requires positive maximum_lines and non-NONE trimming. A synthetic sign has
 * glyph_index UINT32_MAX, cluster at the first hidden source boundary and, for
 * RTL paragraphs, lies to the left of retained content. Its caller-owned actual
 * glyph/font/style may be drawn separately using the returned sign geometry.
 * This does not replace the original paragraph's source/interaction metadata. */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_layout_collapsed_flow_paragraph(
    progpu_native_text_context* context, const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout, const progpu_native_text_style_run* styles,
    uint32_t style_count, const progpu_native_text_flow_options* flow,
    progpu_native_positioned_text_glyph* glyphs, uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, uint32_t line_capacity,
    void* scratch, size_t scratch_size, progpu_native_text_paragraph_result* result,
    uint32_t wrapping, float collapse_width);
#ifdef __cplusplus
}
#endif
#endif

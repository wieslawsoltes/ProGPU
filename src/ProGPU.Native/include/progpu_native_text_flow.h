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

/* Positive incremental_tab enables fixed leading-edge tab stops in DIPs.
 * tab_origin is text-start indentation relative to that grid. U+0009 remains
 * one non-ink positioned item with glyph_id == UINT32_MAX, its original cluster,
 * actual resolved advance and source face index. Never submit it to a glyph atlas.
 * Zero keeps the previous layout behavior. Custom stops/leaders and tab trimming
 * are not part of this contract. All inputs are synchronous borrowed data. */
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
#ifdef __cplusplus
}
#endif
#endif

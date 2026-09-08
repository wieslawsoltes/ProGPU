#ifndef PROGPU_NATIVE_TEXT_STYLES_H
#define PROGPU_NATIVE_TEXT_STYLES_H
#include "progpu_native.h"
#ifdef __cplusplus
extern "C" {
#endif

/* PROGPU_CSHARP_STRUCT: Public.NativeTextStyleRun */
typedef struct progpu_native_text_style_run {
    uint32_t scalar_start;
    uint32_t scalar_count;
    uint32_t font_index;
    float scale;
    uint32_t feature_start;
    uint32_t feature_count;
    uint32_t language;
    uint32_t reserved;
} progpu_native_text_style_run;

/* Synchronous borrowed style runs partition every input scalar exactly once,
 * in logical order. font_index names a face already owned by this context;
 * explicit styled faces do not silently enter the primary-face fallback chain.
 * scale is DIP/design-unit, independently for each run. Feature ranges select
 * entries in shaping.features, and language is the run's OpenType language tag.
 * Empty styles retain ordinary paragraph behavior. Line height/paragraph
 * direction/wrapping/alignment stay paragraph-wide. Existing context ownership
 * and scratch/result publication rules apply; no style pointers are retained. */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_get_styled_paragraph_requirements(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout,
    const progpu_native_text_style_run* styles, uint32_t style_count,
    progpu_native_text_paragraph_requirements* requirements);

PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_layout_styled_paragraph(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout,
    const progpu_native_text_style_run* styles, uint32_t style_count,
    progpu_native_positioned_text_glyph* glyphs, uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines, uint32_t line_capacity,
    void* scratch, size_t scratch_size,
    progpu_native_text_paragraph_result* result);

#ifdef __cplusplus
}
#endif
#endif

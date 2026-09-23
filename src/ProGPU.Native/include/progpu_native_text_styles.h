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
    /* Unicode scalar for the culture's zero digit. Zero disables number
     * substitution. PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_CONTEXTUAL makes
     * substitution depend on the nearest preceding strong character. */
    uint32_t digit_substitution;
    /* Optional source number-symbol scalars. Zero preserves the corresponding
     * ASCII source symbol. Source indices and lengths are never rewritten. */
    uint32_t percent;
    uint32_t group_separator;
    uint32_t decimal_separator;
} progpu_native_text_style_run;

#define PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_SCALAR_MASK 0x001FFFFFu
#define PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_CONTEXTUAL 0x80000000u
#define PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_SOURCE_BIDI 0x40000000u

/* Resolves metadata using the same digit preprocessing as styled paragraphs.
 * Only source scalar ranges and digit_substitution are consumed from styles;
 * fonts, scales and OpenType features have no role in UAX #9 resolution.
 * Scratch requirements are unchanged from progpu_native_text_get_bidi_requirements.
 * Input scalars remain borrowed/read-only; outputs retain original source indices.
 * All input/style/output/scratch/result ranges must be disjoint. Aliased ranges
 * are rejected before writing any output, scratch or result field. */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_resolve_styled_bidi(
    const progpu_native_text_scalar* input, uint32_t input_count,
    int32_t requested_paragraph_level,
    const progpu_native_text_style_run* styles, uint32_t style_count,
    progpu_native_text_bidi_level* levels, uint32_t level_capacity,
    void* scratch, size_t scratch_size, progpu_native_text_bidi_result* result);

/* Synchronous borrowed style runs partition every input scalar exactly once,
 * in logical order. font_index names a face already owned by this context;
 * explicit styled faces do not silently enter the primary-face fallback chain.
 * scale is DIP/design-unit, independently for each run. Feature ranges select
 * entries in shaping.features, and language is the run's OpenType language tag.
 * digit_substitution and number-symbol fields retain original input ranges
 * while selecting rendered scalars before script, fallback and shaping. By
 * default bidi also sees substituted scalars. SOURCE_BIDI retains original
 * scalar bidi classes for source systems that substitute after itemization.
 * Its low 21
 * bits contain a valid zero-digit scalar followed by nine consecutive decimal
 * scalars; the contextual flag selects it only after Arabic strong context.
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

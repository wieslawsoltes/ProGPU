#pragma once

#include "progpu_native_text_hinting.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Optional source geometry version 1. Existing hinting records and entrypoints
 * are unchanged. This is an explicit capture policy, NOT Display admission.
 * All unknown versions, flags, policies and reserved fields reject atomically.
 * Offset policy 0 retains raw device offsets; no other policy is admitted yet. */
typedef enum progpu_native_source_em_policy {
    PROGPU_NATIVE_SOURCE_EM_EXACT_26_6 = 0,
    PROGPU_NATIVE_SOURCE_EM_NEAREST_HALF_UP = 1,
    PROGPU_NATIVE_SOURCE_EM_NEAREST_TIES_TO_EVEN = 2
} progpu_native_source_em_policy;
typedef enum progpu_native_source_advance_policy {
    PROGPU_NATIVE_SOURCE_ADVANCE_UNCHANGED = 0,
    PROGPU_NATIVE_SOURCE_ADVANCE_PHYSICAL_TIES_TO_EVEN = 1
} progpu_native_source_advance_policy;

typedef enum progpu_native_source_options_flags {
    PROGPU_NATIVE_SOURCE_MEASURE_INTRINSIC_WIDTHS = 1
} progpu_native_source_options_flags;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceIntrinsicWidths */
typedef struct progpu_native_hinted_source_intrinsic_widths {
    double minimum;
    double maximum;
} progpu_native_hinted_source_intrinsic_widths;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceOptions */
typedef struct progpu_native_hinted_source_options {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t version;
    uint32_t flags;
    double em_size;
    double pixels_per_dip;
    double maximum_width;
    double line_height;
    double tab_origin;
    uint32_t em_policy;
    uint32_t advance_policy;
    uint32_t offset_policy;
    uint32_t allow_emergency_break;
} progpu_native_hinted_source_options;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceStyle */
typedef struct progpu_native_hinted_source_style {
    double em_size;
    double ascent;
    double descent;
} progpu_native_hinted_source_style;

/* Original logical metrics are after selected advance policy, Y down. IDs,
 * ranges, raw owners and full levels are in the ordinary format snapshot. */
/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceLogicalMetrics */
typedef struct progpu_native_hinted_source_logical_metrics {
    double advance_x;
    double advance_y;
    double offset_x;
    double offset_y;
} progpu_native_hinted_source_logical_metrics;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceGlyphMetrics */
typedef struct progpu_native_hinted_source_glyph_metrics {
    double x;
    double y;
    double advance_x;
    double advance_y;
    int32_t cluster;
    uint32_t reserved;
} progpu_native_hinted_source_glyph_metrics;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceLineMetrics */
typedef struct progpu_native_hinted_source_line_metrics {
    double width;
    double top;
    double height;
    double baseline_offset;
    double baseline_y;
    double origin_x;
    uint32_t glyph_start;
    uint32_t glyph_count;
} progpu_native_hinted_source_line_metrics;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceClusterBox */
typedef struct progpu_native_hinted_source_cluster_box {
    int32_t input_start;
    int32_t input_end;
    uint32_t line_index;
    int8_t bidi_level;
    uint8_t reserved0;
    uint8_t reserved1;
    uint8_t reserved2;
    double x;
    double y;
    double width;
    double height;
} progpu_native_hinted_source_cluster_box;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceCaretStop */
typedef struct progpu_native_hinted_source_caret_stop {
    int32_t input_position;
    uint32_t line_index;
    double x;
    double y;
    double height;
    int8_t bidi_level;
    uint8_t trailing;
    uint8_t reserved0;
    uint8_t reserved1;
} progpu_native_hinted_source_caret_stop;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceRectangle */
typedef struct progpu_native_hinted_source_rectangle {
    double x;
    double y;
    double width;
    double height;
} progpu_native_hinted_source_rectangle;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceHit */
typedef struct progpu_native_hinted_source_hit {
    int32_t input_position;
    uint32_t line_index;
    /* PROGPU_CSHARP_TYPE: NativeHintedSourceRectangle */
    progpu_native_hinted_source_rectangle bounds;
    int8_t bidi_level;
    uint8_t trailing;
    uint8_t inside;
    uint8_t reserved;
} progpu_native_hinted_source_hit;

/* Borrow only under the existing paragraph's destruction-excluding use lease.
 * All pointers refer to one immutable generation owned by that exact handle.
 * Counts match the ordinary source/logical/positioned snapshot. No imported
 * pointer or font/context borrow survives creation. Old float interaction is
 * not a substitute for these records and is not populated for this lane. */
/* PROGPU_CSHARP_STRUCT: NativeMethods.HintedSourceParagraphView */
typedef struct progpu_native_hinted_source_paragraph_view {
    /* PROGPU_CSHARP_TYPE: NativeHintedSourceOptions */
    progpu_native_hinted_source_options options;
    uint32_t style_count;
    uint32_t logical_count;
    uint32_t glyph_count;
    uint32_t line_count;
    uint32_t box_count;
    uint32_t caret_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_source_style* styles;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_source_logical_metrics* logical_metrics;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_source_glyph_metrics* glyph_metrics;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_source_line_metrics* line_metrics;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_source_cluster_box* boxes;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_source_caret_stop* carets;
} progpu_native_hinted_source_paragraph_view;

/* Exact original double source frame plus the separately admitted existing
 * raster translation. A nonrepresentable raster mapping stays UNSUPPORTED. */
/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceRunFrame */
typedef struct progpu_native_hinted_source_run_frame {
    uint32_t line_index;
    uint32_t font_index;
    int32_t bidi_level;
    uint32_t reserved;
    double paragraph_baseline_y;
    /* PROGPU_CSHARP_TYPE: NativeHintedSourceGlyphOffset */
    progpu_native_hinted_source_glyph_offset source_baseline_origin;
    /* PROGPU_CSHARP_TYPE: NativeHintedSourceGlyphOffset */
    progpu_native_hinted_source_glyph_offset paragraph_origin;
    progpu_native_point raster_paragraph_origin;
} progpu_native_hinted_source_run_frame;

/* Original source/style doubles are paired with strictly matching old raster
 * shadow records and the exact caller-selected device capture. No policy is
 * inferred from OS/font/DPI, and no old source API enables this lane implicitly.
 * Tabs/objects/trimming/justification and unsupported boundary forms reject.
 * Result and diagnostic stay untouched on every failure. */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_layout_hinted_source_paragraph(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout,
    const progpu_native_text_style_run* styles, uint32_t style_count,
    const progpu_native_text_style_metrics* source_metrics,
    const progpu_native_hinted_paragraph_device_style* device_styles, uint32_t device_style_count,
    const int32_t* variation_coordinates_16_16, uint32_t variation_count,
    const progpu_native_hinted_source_options* source_options,
    const progpu_native_hinted_source_style* source_styles, uint32_t source_style_count,
    progpu_native_hinted_paragraph** paragraph,
    progpu_native_text_paragraph_result* paragraph_result);

PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_source_paragraph_borrow(
    const progpu_native_hinted_paragraph* paragraph,
    progpu_native_hinted_source_paragraph_view* view);
/* Optional original whole-paragraph measurement, never current-line width.
 * Requires MEASURE_INTRINSIC_WIDTHS at creation. Reflow preserves its values. */
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_source_paragraph_get_intrinsic_widths(
    const progpu_native_hinted_paragraph* paragraph,
    progpu_native_hinted_source_intrinsic_widths* widths);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_source_paragraph_reflow(
    const progpu_native_hinted_paragraph* paragraph, int32_t input_start,
    double maximum_width, progpu_native_hinted_paragraph** reflowed);

/* Same retained double interaction, never raster shadows or managed rebuilds.
 * Entire output capacities must be disjoint from owned allocations. Failure,
 * including insufficient selection capacity, leaves all outputs untouched. */
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_source_paragraph_hit_test(
    const progpu_native_hinted_paragraph* paragraph, double x, double y,
    progpu_native_hinted_source_hit* hit);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_source_paragraph_get_caret(
    const progpu_native_hinted_paragraph* paragraph, int32_t input_position, uint32_t trailing,
    progpu_native_hinted_source_caret_stop* caret);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_source_paragraph_get_selection(
    const progpu_native_hinted_paragraph* paragraph, int32_t input_start, int32_t input_end,
    progpu_native_hinted_source_rectangle* rectangles, uint32_t capacity, uint32_t* written);

/* Line-scoped variants select retained native ranges, preserving separate
 * affinities at a shared soft-wrap source boundary. Invalid lines fail atomically. */
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_source_paragraph_hit_test_line(
    const progpu_native_hinted_paragraph* paragraph, uint32_t line_index, double x,
    progpu_native_hinted_source_hit* hit);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_source_paragraph_get_line_caret(
    const progpu_native_hinted_paragraph* paragraph, uint32_t line_index, int32_t input_position, uint32_t trailing,
    progpu_native_hinted_source_caret_stop* caret);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_source_paragraph_get_line_selection(
    const progpu_native_hinted_paragraph* paragraph, uint32_t line_index, int32_t input_start, int32_t input_end,
    progpu_native_hinted_source_rectangle* rectangles, uint32_t capacity, uint32_t* written);

/* Source GlyphRun nominal-offset convention remains independent from the raw
 * logical device offsets. Copy derives it from owned DOUBLE writer positions,
 * original hmtx/UPM/em arithmetic and selected occurrence order. Binding checks
 * full native levels (not only parity), exact raw run/font/source identity and
 * original source em/DPI. No float-promoted advance or nominal width substitute.
 * Both operations require an original source paragraph resource with captured
 * nominal metrics, never an imported foreign resource or an old raw paragraph. */
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_glyph_resource_copy_source_metrics(
    const progpu_native_hinted_glyph_resource* resource,
    const uint32_t* positioned_indices, uint32_t glyph_count,
    double source_em_size, double source_pixels_per_dip,
    double* advances, uint32_t advance_capacity,
    progpu_native_hinted_source_glyph_offset* offsets, uint32_t offset_capacity);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_glyph_resource_validate_source_run(
    const progpu_native_hinted_glyph_resource* resource,
    const uint32_t* positioned_indices, uint32_t glyph_count,
    double source_em_size, double source_pixels_per_dip,
    progpu_native_hinted_source_glyph_offset source_baseline_origin,
    const double* advances, const progpu_native_hinted_source_glyph_offset* offsets,
    progpu_native_hinted_source_run_frame* frame);

#ifdef __cplusplus
}
#endif

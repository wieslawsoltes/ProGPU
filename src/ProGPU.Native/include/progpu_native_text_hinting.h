#pragma once

#include <stdint.h>
#include "progpu_native.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct progpu_native_hinted_batch progpu_native_hinted_batch;
typedef struct progpu_native_hinted_run progpu_native_hinted_run;

/* PROGPU_CSHARP_STRUCT: NativeMethods.HintedFontRequest */
typedef struct progpu_native_hinted_font_request {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t font_index;
    uint32_t x_pixels_per_em_26_6;
    uint32_t y_pixels_per_em_26_6;
    uint32_t interpreter;
    uint32_t x_phase_26_6;
    uint32_t y_phase_26_6;
    uint32_t variation_count;
    uint32_t reserved;
} progpu_native_hinted_font_request;

/* Fixed-width snapshot records. These types do not create a font context,
 * advertise a provider capability, or admit source Display mode. */
/* PROGPU_CSHARP_STRUCT: Public.NativeHintedPoint */
typedef struct progpu_native_hinted_point {
    int64_t x_26_6;
    int64_t y_26_6;
} progpu_native_hinted_point;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedGlyph */
typedef struct progpu_native_hinted_glyph {
    uint32_t glyph_index;
    uint32_t point_offset;
    uint32_t point_count;
    uint32_t contour_offset;
    uint32_t contour_count;
    uint32_t outline_flags;
    int64_t advance_x_26_6;
    int64_t advance_y_26_6;
    int64_t horizontal_bearing_x_26_6;
    int64_t horizontal_bearing_y_26_6;
    int64_t width_26_6;
    int64_t height_26_6;
    int64_t horizontal_advance_26_6;
    int64_t vertical_bearing_x_26_6;
    int64_t vertical_bearing_y_26_6;
    int64_t vertical_advance_26_6;
    int64_t linear_horizontal_advance_16_16;
    int64_t linear_vertical_advance_16_16;
    int64_t left_side_bearing_delta_26_6;
    int64_t right_side_bearing_delta_26_6;
} progpu_native_hinted_glyph;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedBatchCounts */
typedef struct progpu_native_hinted_batch_counts {
    uint32_t glyphs;
    uint32_t points;
    uint32_t contours;
} progpu_native_hinted_batch_counts;

/* Borrow the caller's exclusive live context lease for capture only. Inputs
 * are synchronous and not retained. Success publishes one independently owned
 * immutable generation; failure leaves *batch unchanged. The caller retains
 * responsibility for any prior handle in that output slot. Interpreter is
 * exactly 35 or 40; variation coordinates are original-order signed 16.16.
 * Unsupported/absent dependencies fail, never substitute Ideal or a bitmap. */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_capture_hinted_batch(
    progpu_native_text_context* context,
    const progpu_native_hinted_font_request* request,
    const int32_t* variation_coordinates_16_16,
    const uint32_t* glyph_indices,
    uint32_t glyph_count,
    progpu_native_hinted_batch** batch);

/* Counts and every output/tail remain unchanged on failure. Copy transfers one
 * complete retained generation without executing fonts, allocating or doing GPU
 * work. Tags use point offsets; contour ends are original glyph-local indices.
 * Caller leases must exclude concurrent destroy. Destroy(NULL) is permitted. */
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_batch_get_counts(
    const progpu_native_hinted_batch* batch, progpu_native_hinted_batch_counts* counts);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_batch_copy(
    const progpu_native_hinted_batch* batch,
    progpu_native_hinted_glyph* glyphs, uint32_t glyph_capacity,
    progpu_native_hinted_point* points, uint32_t point_capacity,
    uint8_t* tags, uint32_t tag_capacity,
    int32_t* contour_ends, uint32_t contour_capacity);
PROGPU_NATIVE_API void progpu_native_hinted_batch_destroy(progpu_native_hinted_batch* batch);

/* Explicit owned-context device shaping under the caller's exclusive context
 * lease. The shape request must omit font bytes, face index and normalization
 * bytes: the selected context font and context normalization own those inputs.
 * Success publishes one immutable positioned run and its ordered post-GSUB
 * outline capture; failure leaves *run unchanged. The output slot is never
 * read; any prior handle's destruction remains the caller's responsibility.
 * This additive boundary does not admit source Display mode. */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_shape_hinted_run(
    progpu_native_text_context* context,
    const progpu_native_hinted_font_request* hint_request,
    const int32_t* variation_coordinates_16_16,
    const progpu_native_text_shape_request* shape_request,
    progpu_native_hinted_run** run);

/* Every copy/count output and unused slot remains unchanged on failure.
 * Positioned glyph metrics are signed device 26.6 units; Y uses the existing
 * C Y-down convention; outlines retain original captured Y-up coordinates.
 * Descriptor
 * indices identify exact capture slots, including repeated glyph IDs.
 * Returned handles survive context retirement. Copies allocate/execute no
 * fonts and do no GPU work; caller leases exclude concurrent destruction. */
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_run_get_counts(
    const progpu_native_hinted_run* run, uint32_t* shaped_glyph_count,
    progpu_native_hinted_batch_counts* outline_counts);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_run_copy_glyphs(
    const progpu_native_hinted_run* run,
    progpu_native_text_shaping_glyph* glyphs, uint32_t glyph_capacity,
    uint32_t* descriptor_indices, uint32_t descriptor_capacity);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_run_copy_outlines(
    const progpu_native_hinted_run* run,
    progpu_native_hinted_glyph* glyphs, uint32_t glyph_capacity,
    progpu_native_hinted_point* points, uint32_t point_capacity,
    uint8_t* tags, uint32_t tag_capacity,
    int32_t* contour_ends, uint32_t contour_capacity);
PROGPU_NATIVE_API void progpu_native_hinted_run_destroy(progpu_native_hinted_run* run);

#ifdef __cplusplus
}
#endif

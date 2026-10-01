#pragma once

#include <stdint.h>
#include "progpu_native.h"
#include "progpu_native_mil.h"
#include "progpu_native_text_styles.h"
#include "progpu_native_text_flow.h"
#include "progpu_native_text_interaction.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct progpu_native_hinted_batch progpu_native_hinted_batch;
typedef struct progpu_native_hinted_run progpu_native_hinted_run;
typedef struct progpu_native_hinted_paragraph progpu_native_hinted_paragraph;
typedef struct progpu_native_hinted_paragraph_frame progpu_native_hinted_paragraph_frame;
typedef struct progpu_native_hinted_glyph_resource progpu_native_hinted_glyph_resource;

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

/* Explicit device selection per ORIGINAL style. Source scale is identity, not
 * device em selection. Axes address a synchronous flat signed 16.16 array in
 * original fvar order; repeated/shared ranges are permitted. */
/* PROGPU_CSHARP_STRUCT: Public.NativeHintedParagraphDeviceStyle */
typedef struct progpu_native_hinted_paragraph_device_style {
    uint32_t font_index;
    float source_scale;
    float logical_units_per_physical_pixel;
    uint32_t x_pixels_per_em_26_6;
    uint32_t y_pixels_per_em_26_6;
    uint32_t interpreter;
    uint32_t x_phase_26_6;
    uint32_t y_phase_26_6;
    uint32_t variation_start;
    uint32_t variation_count;
    uint32_t reserved;
} progpu_native_hinted_paragraph_device_style;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedParagraphCounts */
typedef struct progpu_native_hinted_paragraph_counts {
    uint32_t source_scalar_count;
    uint32_t admitted_scalar_count;
    uint32_t style_count;
    uint32_t run_count;
    uint32_t logical_glyph_count;
    uint32_t positioned_glyph_count;
    uint32_t line_count;
    uint32_t cluster_box_count;
    uint32_t caret_stop_count;
} progpu_native_hinted_paragraph_counts;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedParagraphRun */
typedef struct progpu_native_hinted_paragraph_run {
    uint32_t scalar_start;
    uint32_t scalar_count;
    uint32_t logical_start;
    uint32_t logical_count;
    uint32_t font_index;
    uint32_t style_index;
    int32_t bidi_level;
    float source_scale;
    float logical_units_per_physical_pixel;
    uint32_t source_descriptor_count;
} progpu_native_hinted_paragraph_run;

/* Slots, NOT glyph-ID lookup: repeated draws/descriptors remain distinct. */
/* PROGPU_CSHARP_STRUCT: Public.NativeHintedParagraphGlyphOwner */
typedef struct progpu_native_hinted_paragraph_glyph_owner {
    uint32_t run_index;
    uint32_t run_glyph_index;
    uint32_t descriptor_index;
} progpu_native_hinted_paragraph_glyph_owner;

/* One bulk copy of the owned formatted generation. Each capacity declares the
 * FULL writable range, including unused tails; every range must be disjoint
 * from this record, the owner and every other output. Original/admitted scalars
 * retain original source positions. Logical glyph metrics are device 26.6
 * Y-down; glyph_scales are the exact writer conversion, units/pixel / 64.
 * Positioned levels are the actual writer's L1/L2-used levels. */
/* PROGPU_CSHARP_STRUCT: NativeMethods.HintedParagraphFormatBuffers */
typedef struct progpu_native_hinted_paragraph_format_buffers {
    uint32_t struct_size;
    uint32_t reserved;
    /* PROGPU_CSHARP_TYPE: nuint */
    progpu_native_text_scalar* source_scalars;
    uint32_t source_scalar_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    progpu_native_text_scalar* admitted_scalars;
    uint32_t admitted_scalar_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    progpu_native_text_bidi_level* scalar_levels;
    uint32_t scalar_level_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    progpu_native_text_style_run* styles;
    uint32_t style_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    progpu_native_text_style_metrics* source_metrics;
    uint32_t source_metric_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    progpu_native_hinted_paragraph_run* runs;
    uint32_t run_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    progpu_native_text_shaping_glyph* logical_glyphs;
    uint32_t logical_glyph_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    progpu_native_hinted_paragraph_glyph_owner* logical_owners;
    uint32_t logical_owner_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    int32_t* logical_cluster_ends;
    uint32_t logical_cluster_end_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    int8_t* logical_bidi_levels;
    uint32_t logical_bidi_level_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    float* glyph_scales;
    uint32_t glyph_scale_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    progpu_native_positioned_text_glyph* positioned_glyphs;
    uint32_t positioned_glyph_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    progpu_native_hinted_paragraph_glyph_owner* positioned_owners;
    uint32_t positioned_owner_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    int32_t* positioned_cluster_ends;
    uint32_t positioned_cluster_end_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    int8_t* positioned_bidi_levels;
    uint32_t positioned_bidi_level_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    progpu_native_positioned_text_line* lines;
    uint32_t line_capacity;
    /* PROGPU_CSHARP_TYPE: nuint */
    float* line_origins;
    uint32_t line_origin_capacity;
} progpu_native_hinted_paragraph_format_buffers;

typedef enum progpu_native_hinted_projection_policy {
    PROGPU_NATIVE_HINTED_PROJECTION_AUTOMATIC = 0,
    PROGPU_NATIVE_HINTED_PROJECTION_NATIVE_COMPUTE = 1,
    PROGPU_NATIVE_HINTED_PROJECTION_GPU_SHADER = 2,
    PROGPU_NATIVE_HINTED_PROJECTION_INTRINSIC_SIMD = 3,
    PROGPU_NATIVE_HINTED_PROJECTION_SCALAR_REFERENCE = 4
} progpu_native_hinted_projection_policy;
typedef enum progpu_native_hinted_outline_coverage {
    PROGPU_NATIVE_HINTED_COVERAGE_STRICT = 0,
    PROGPU_NATIVE_HINTED_COVERAGE_NONZERO_VECTOR = 1,
    /* Nonzero antialiased vector coverage retains B/W dropout metadata without
     * executing FreeType scan conversion. Existing coverage policies stay exact. */
    PROGPU_NATIVE_HINTED_COVERAGE_ANTIALIASED_VECTOR = 2
} progpu_native_hinted_outline_coverage;

/* The view remains borrowed under the ORIGINAL selected renderer contract.
 * Every positioned run, including no-ink, needs exact reciprocal/product DPI
 * admission. Coverage is explicit; strict is the unchanged default policy. */
/* PROGPU_CSHARP_STRUCT: NativeMethods.HintedParagraphFrameRequest */
typedef struct progpu_native_hinted_paragraph_frame_request {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    float dpi_scale;
    uintptr_t target_view;
    progpu_native_point logical_origin;
    progpu_native_color clear_color;
    uint32_t projection_policy;
    uint32_t coverage;
    uint32_t reserved;
} progpu_native_hinted_paragraph_frame_request;

/* Target-independent preparation over the original formatted generation.
 * No view, clear color, paint or source origin is fabricated for this resource. */
/* PROGPU_CSHARP_STRUCT: NativeMethods.HintedGlyphResourceRequest */
typedef struct progpu_native_hinted_glyph_resource_request {
    uint32_t abi_version;
    uint32_t struct_size;
    float dpi_scale;
    uint32_t projection_policy;
    uint32_t coverage;
    uint32_t reserved;
} progpu_native_hinted_glyph_resource_request;

/* Offsets address the one original-byte arena, never a parsed table or name. */
/* PROGPU_CSHARP_STRUCT: Public.NativeHintedGlyphFontSource */
typedef struct progpu_native_hinted_glyph_font_source {
    uint32_t byte_offset;
    uint32_t byte_count;
    uint32_t face_index;
    uint32_t units_per_em;
} progpu_native_hinted_glyph_font_source;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedGlyphRunSlice */
typedef struct progpu_native_hinted_glyph_run_slice {
    uint32_t source_start;
    uint32_t source_count;
    uint32_t run_start;
    uint32_t run_count;
    uint32_t outline_start;
    uint32_t outline_count;
    uint32_t segment_start;
    uint32_t segment_count;
} progpu_native_hinted_glyph_run_slice;

/* PROGPU_CSHARP_STRUCT: Public.NativeHintedGlyphOutlineOwner */
typedef struct progpu_native_hinted_glyph_outline_owner {
    uint32_t run_index;
    uint32_t descriptor_index;
} progpu_native_hinted_glyph_outline_owner;

/* Original hmtx horizontal advance, NOT hinted device or positioned/GPOS advance.
 * Each record addresses one original positioned occurrence, including no-ink
 * and repeated glyphs. Font/face/UPM and owner identity remain in the same
 * resource's ordinary view. Coordinate-bearing instances are not admitted. */
/* PROGPU_CSHARP_STRUCT: Public.NativeHintedGlyphNominalMetrics */
typedef struct progpu_native_hinted_glyph_nominal_metrics {
    uint32_t positioned_index;
    uint32_t font_index;
    uint32_t glyph_id;
    uint32_t advance_width_design_units;
} progpu_native_hinted_glyph_nominal_metrics;

/* Separate additive view: no existing resource record or import ABI changes.
 * Borrow under the original destruction-excluding resource lease. */
/* PROGPU_CSHARP_STRUCT: NativeMethods.HintedGlyphNominalMetricsView */
typedef struct progpu_native_hinted_glyph_nominal_metrics_view {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t metric_count;
    uint32_t reserved;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_glyph_nominal_metrics* metrics;
} progpu_native_hinted_glyph_nominal_metrics_view;

/* A read-only flat borrow held by an ORIGINAL producer-library lifetime lease
 * excluding destruction. Immutable cached records admit concurrent readers.
 * A renderer receives only these records, never the producer's opaque handle.
 * Counts declare readable spans; paragraph counts size the original-format
 * arrays below. Every positioned occurrence, including UINT32_MAX no-ink,
 * survives. All pointers retire with the resource, not its source context.
 * This is a synchronous CPU import contract, not source Display admission. */
/* PROGPU_CSHARP_STRUCT: NativeMethods.HintedGlyphResourceView */
typedef struct progpu_native_hinted_glyph_resource_view {
    uint32_t abi_version;
    uint32_t struct_size;
    float dpi_scale;
    uint32_t projection_policy;
    uint32_t coverage;
    uint32_t source_digit_bidi;
    int32_t paragraph_level;
    uint32_t shaping_direction;
    uint32_t shaping_flags;
    /* PROGPU_CSHARP_TYPE: NativeHintedParagraphCounts */
    progpu_native_hinted_paragraph_counts counts;
    /* PROGPU_CSHARP_TYPE: NativeTextParagraphResult */
    progpu_native_text_paragraph_result result;
    /* PROGPU_CSHARP_TYPE: NativeTextLayoutOptions */
    progpu_native_text_layout_options layout;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_glyph_font_source* font_sources;
    uint32_t font_source_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const uint8_t* font_bytes;
    uint32_t font_byte_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_paragraph_device_style* device_styles;
    /* PROGPU_CSHARP_TYPE: nuint */
    const int32_t* variation_coordinates_16_16;
    uint32_t variation_coordinate_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const int16_t* normalized_coordinates;
    uint32_t normalized_coordinate_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_glyph_outline* outlines;
    uint32_t outline_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_path_segment* segments;
    uint32_t segment_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_glyph_run_slice* run_slices;
    /* PROGPU_CSHARP_TYPE: nuint */
    const uint32_t* source_outline_indices;
    uint32_t source_outline_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const uint32_t* run_outline_indices;
    uint32_t run_outline_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_glyph_outline_owner* outline_owners;
    /* PROGPU_CSHARP_TYPE: nuint */
    const uint32_t* positioned_outline_indices;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_scalar* source_scalars;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_scalar* admitted_scalars;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_bidi_level* scalar_levels;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_style_run* styles;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_style_metrics* source_metrics;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_paragraph_run* runs;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_shaping_glyph* logical_glyphs;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_paragraph_glyph_owner* logical_owners;
    /* PROGPU_CSHARP_TYPE: nuint */
    const int32_t* logical_cluster_ends;
    /* PROGPU_CSHARP_TYPE: nuint */
    const int8_t* logical_bidi_levels;
    /* PROGPU_CSHARP_TYPE: nuint */
    const float* glyph_scales;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_positioned_text_glyph* positioned_glyphs;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_paragraph_glyph_owner* positioned_owners;
    /* PROGPU_CSHARP_TYPE: nuint */
    const int32_t* positioned_cluster_ends;
    /* PROGPU_CSHARP_TYPE: nuint */
    const int8_t* positioned_bidi_levels;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_positioned_text_line* lines;
    /* PROGPU_CSHARP_TYPE: nuint */
    const float* line_origins;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_cluster_box* boxes;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_caret_stop* carets;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_scalar* pre_context;
    uint32_t pre_context_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_scalar* post_context;
    uint32_t post_context_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_feature* features;
    uint32_t feature_count;
} progpu_native_hinted_glyph_resource_view;

/* One binding selects explicit original occurrences, not glyph IDs or phases.
 * Only the identity basis and exact source-provided ink bounds are admitted. */
/* PROGPU_CSHARP_STRUCT: Public.NativeMilHintedGlyphBinding */
typedef struct progpu_native_mil_hinted_glyph_binding {
    uint32_t glyph_run_handle;
    uint32_t resource_index;
    uint32_t font_index;
    uint32_t positioned_index_start;
    uint32_t positioned_index_count;
    uint32_t reserved;
    progpu_native_point logical_origin;
    /* PROGPU_CSHARP_TYPE: Matrix3x2 */
    progpu_native_affine_2d basis;
} progpu_native_mil_hinted_glyph_binding;

/* Preparation and borrowing touch only the producer library. The resource
 * retains original geometry, format, interaction and bytes independently of
 * the paragraph/context. Publication is atomic and rejects reachable aliases. */
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_paragraph_prepare_glyph_resource(
    const progpu_native_hinted_paragraph* paragraph,
    const progpu_native_hinted_glyph_resource_request* request,
    progpu_native_hinted_glyph_resource** resource);
/* Explicit nominal-metric preparation. Missing original hmtx advances or any
 * selected design/context normalized coordinates return Unsupported, never an
 * invented advance. All preparation must succeed before publishing resource. */
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_paragraph_prepare_glyph_resource_with_nominal_metrics(
    const progpu_native_hinted_paragraph* paragraph,
    const progpu_native_hinted_glyph_resource_request* request,
    progpu_native_hinted_glyph_resource** resource);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_glyph_resource_borrow(
    const progpu_native_hinted_glyph_resource* resource,
    progpu_native_hinted_glyph_resource_view* view);
/* Ordinary preparation has no nominal view and returns Unsupported. Failures
 * leave the output untouched, including aliases into all retained storage. */
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_glyph_resource_borrow_nominal_metrics(
    const progpu_native_hinted_glyph_resource* resource,
    progpu_native_hinted_glyph_nominal_metrics_view* view);
PROGPU_NATIVE_API void progpu_native_hinted_glyph_resource_destroy(progpu_native_hinted_glyph_resource* resource);

/* Canonical commands, flat resource imports and all bindings form ONE staged
 * update. Any later invalid record preserves prior graph/cache/metrics. No
 * producer handle crosses providers, no per-glyph native calls are required,
 * and there is no external output buffer to alias immutable input storage. */
PROGPU_NATIVE_API progpu_native_mil_status progpu_native_mil_channel_apply_with_hinted_glyph_resources(
    progpu_native_mil_channel* channel,
    const uint8_t* batch_bytes, size_t batch_size,
    const progpu_native_hinted_glyph_resource_view* resources, uint32_t resource_count,
    const progpu_native_mil_hinted_glyph_binding* bindings, uint32_t binding_count,
    const uint32_t* positioned_indices, uint32_t positioned_index_count);

/* Borrow one exclusive live context lease. Source metrics/device styles have
 * exactly style_count entries; device_style_count must equal style_count.
 * Success owns the ORIGINAL producer/writer generation AND its measured
 * interaction. Horizontal only; unsupported source contracts fail explicitly.
 * Fresh output slots are never read; prior handles remain caller responsibility.
 * ALL outputs/tails remain unchanged on failure, including paragraph_result.
 * This API does not admit source Display/defaults or empty-hard-row navigation. */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_layout_hinted_paragraph(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout,
    const progpu_native_text_style_run* styles, uint32_t style_count,
    const progpu_native_text_style_metrics* source_metrics,
    const progpu_native_hinted_paragraph_device_style* device_styles, uint32_t device_style_count,
    const int32_t* variation_coordinates_16_16, uint32_t variation_count,
    progpu_native_hinted_paragraph** paragraph,
    progpu_native_text_paragraph_result* paragraph_result);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_paragraph_get_counts(
    const progpu_native_hinted_paragraph* paragraph,
    progpu_native_hinted_paragraph_counts* counts,
    progpu_native_text_paragraph_result* paragraph_result);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_paragraph_copy_format(
    const progpu_native_hinted_paragraph* paragraph,
    const progpu_native_hinted_paragraph_format_buffers* buffers);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_paragraph_copy_interaction(
    const progpu_native_hinted_paragraph* paragraph,
    progpu_native_text_cluster_box* boxes, uint32_t box_capacity,
    progpu_native_text_caret_stop* carets, uint32_t caret_capacity);
PROGPU_NATIVE_API void progpu_native_hinted_paragraph_destroy(progpu_native_hinted_paragraph* paragraph);

/* Pure owned CPU preparation, no engine/font execution or C++ cross-module
 * ownership. Borrow returns only the ORIGINAL flat glyph-frame wire. Retain
 * the frame's originating-module lease through selected RenderGlyphs, and
 * exclude concurrent destroy. Never cast this handle into another module.
 * Copies/counts/borrow allocate nothing, preserve tails and are failure-atomic.
 * Destroy(NULL) is permitted; handles survive context/paragraph retirement. */
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_paragraph_prepare_frame(
    const progpu_native_hinted_paragraph* paragraph,
    const progpu_native_hinted_paragraph_frame_request* request,
    const progpu_native_color* style_colors, uint32_t style_color_count,
    progpu_native_hinted_paragraph_frame** frame);
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_paragraph_frame_borrow(
    const progpu_native_hinted_paragraph_frame* frame, progpu_native_glyph_frame* wire_frame);
PROGPU_NATIVE_API void progpu_native_hinted_paragraph_frame_destroy(progpu_native_hinted_paragraph_frame* frame);

#ifdef __cplusplus
}
#endif

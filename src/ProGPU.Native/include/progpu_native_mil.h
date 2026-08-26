#pragma once

#include "progpu_native.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct progpu_native_mil_channel progpu_native_mil_channel;

typedef enum progpu_native_mil_status {
    PROGPU_NATIVE_MIL_STATUS_SUCCESS = 0,
    PROGPU_NATIVE_MIL_STATUS_END_OF_BATCH = 1,
    PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT = 2,
    PROGPU_NATIVE_MIL_STATUS_MALFORMED_BATCH = 3,
    PROGPU_NATIVE_MIL_STATUS_UNKNOWN_COMMAND = 4,
    PROGPU_NATIVE_MIL_STATUS_UNSUPPORTED_COMMAND = 5,
    PROGPU_NATIVE_MIL_STATUS_DUPLICATE_HANDLE = 6,
    PROGPU_NATIVE_MIL_STATUS_INVALID_HANDLE = 7,
    PROGPU_NATIVE_MIL_STATUS_INVALID_RESOURCE_TYPE = 8,
    PROGPU_NATIVE_MIL_STATUS_RESOURCE_TYPE_MISMATCH = 9,
    PROGPU_NATIVE_MIL_STATUS_INVALID_GRAPH = 10,
    PROGPU_NATIVE_MIL_STATUS_CAPACITY_EXCEEDED = 11
} progpu_native_mil_status;

typedef struct progpu_native_mil_batch_metrics {
    uint32_t struct_size;
    uint32_t command_count;
    uint32_t supported_command_count;
    uint32_t unsupported_command_count;
    uint32_t created_resource_count;
    uint32_t deleted_resource_count;
    uint32_t updated_resource_count;
    uint32_t total_bytes;
} progpu_native_mil_batch_metrics;

typedef struct progpu_native_mil_visual_snapshot {
    uint32_t struct_size;
    uint32_t handle;
    double offset_x;
    double offset_y;
    double opacity;
    uint32_t content_handle;
    uint32_t child_count;
} progpu_native_mil_visual_snapshot;

typedef struct progpu_native_mil_target_snapshot {
    uint32_t struct_size;
    uint32_t handle;
    uint32_t root_handle;
    float clear_red;
    float clear_green;
    float clear_blue;
    float clear_alpha;
    uint32_t flags;
} progpu_native_mil_target_snapshot;

typedef struct progpu_native_mil_scene_metrics {
    uint32_t struct_size;
    uint32_t visual_count;
    uint32_t rectangle_count;
    uint32_t brush_count;
    uint32_t maximum_visual_depth;
    uint32_t ellipse_count;
    uint64_t stream_bytes;
    uint32_t rounded_rectangle_count;
    uint32_t line_count;
} progpu_native_mil_scene_metrics;

typedef enum progpu_native_mil_glyph_style_simulations {
    PROGPU_NATIVE_MIL_GLYPH_STYLE_NONE = 0,
    PROGPU_NATIVE_MIL_GLYPH_STYLE_BOLD = 1U << 0U,
    PROGPU_NATIVE_MIL_GLYPH_STYLE_ITALIC = 1U << 1U
} progpu_native_mil_glyph_style_simulations;

PROGPU_NATIVE_API progpu_native_mil_status progpu_native_mil_channel_create(
    progpu_native_mil_channel** channel);
PROGPU_NATIVE_API void progpu_native_mil_channel_destroy(
    progpu_native_mil_channel* channel);
PROGPU_NATIVE_API progpu_native_mil_status progpu_native_mil_channel_apply(
    progpu_native_mil_channel* channel,
    const void* batch,
    size_t batch_size,
    progpu_native_mil_batch_metrics* metrics);
/*
 * Binds copied RGBA8 pixels to a canonical TYPE_BITMAPSOURCE handle. This is
 * the portable replacement for MilCmdBitmapSource's process-local WIC pointer.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_bitmap_source_rgba8(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height,
    uint32_t row_bytes,
    const void* pixels,
    size_t pixel_size);
/*
 * Binds exact local Drawing bounds to a canonical TYPE_DRAWINGIMAGE handle.
 * The canonical packet carries only the referenced Drawing handle.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_drawing_image_bounds(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    double x,
    double y,
    double width,
    double height);
/*
 * Binds exact source-built Visual descendant bounds for target-space
 * BitmapCache page sizing and bounded effect isolation. The canonical Visual,
 * cache, and effect packets do not carry this compositor-derived metadata.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_visual_cache_bounds(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    double x,
    double y,
    double width,
    double height);
/*
 * Binds copied SFNT/TTC bytes to a canonical TYPE_GLYPHRUN handle. This is
 * the portable replacement for MilCmdGlyphRunCreate's process-local
 * IDWriteFont pointer. style_simulations is a bitwise combination of
 * progpu_native_mil_glyph_style_simulations.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_glyph_run_font_sfnt(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t face_index,
    uint32_t style_simulations,
    const void* font_data,
    size_t font_size);
PROGPU_NATIVE_API size_t progpu_native_mil_channel_get_resource_count(
    const progpu_native_mil_channel* channel);
PROGPU_NATIVE_API uint8_t progpu_native_mil_channel_has_resource(
    const progpu_native_mil_channel* channel,
    uint32_t handle);
PROGPU_NATIVE_API uint32_t progpu_native_mil_channel_get_resource_type(
    const progpu_native_mil_channel* channel,
    uint32_t handle);
PROGPU_NATIVE_API uint64_t progpu_native_mil_channel_get_resource_generation(
    const progpu_native_mil_channel* channel,
    uint32_t handle);
PROGPU_NATIVE_API uint8_t progpu_native_mil_channel_get_visual(
    const progpu_native_mil_channel* channel,
    uint32_t handle,
    progpu_native_mil_visual_snapshot* snapshot);
PROGPU_NATIVE_API uint8_t progpu_native_mil_channel_get_visual_child(
    const progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t index,
    uint32_t* child_handle);
PROGPU_NATIVE_API uint8_t progpu_native_mil_channel_get_target(
    const progpu_native_mil_channel* channel,
    uint32_t handle,
    progpu_native_mil_target_snapshot* snapshot);
/*
 * Compiles a target to the shared ProGPU semantic scene stream. Pass a null
 * destination and zero capacity to query the required byte count. A short
 * destination returns CAPACITY_EXCEEDED and still reports the required count.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_build_scene(
    const progpu_native_mil_channel* channel,
    uint32_t target_handle,
    uint64_t scene_id,
    uint64_t generation,
    void* destination,
    size_t destination_size,
    size_t* bytes_written,
    progpu_native_mil_scene_metrics* metrics);

#ifdef __cplusplus
}
#endif

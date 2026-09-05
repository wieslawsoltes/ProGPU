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

typedef enum progpu_native_mil_scene_build_request_flags {
    PROGPU_NATIVE_MIL_SCENE_BUILD_REQUEST_NONE = 0,
    PROGPU_NATIVE_MIL_SCENE_BUILD_REQUEST_VISUAL_BRUSH = 1U << 0U
} progpu_native_mil_scene_build_request_flags;

typedef enum progpu_native_mil_scene_build_result_flags {
    PROGPU_NATIVE_MIL_SCENE_BUILD_RESULT_NONE = 0,
    PROGPU_NATIVE_MIL_SCENE_BUILD_RESULT_NEEDS_MORE_CYCLES = 1U << 0U
} progpu_native_mil_scene_build_result_flags;

/*
 * Versioned frame context for stateful scene compilation. request_serial is
 * the idempotency key: a size query and its subsequent copy must use the same
 * nonzero value. monotonic_time_nanoseconds belongs to the caller's monotonic
 * clock domain and must never be wall-clock time.
 */
typedef struct progpu_native_mil_scene_build_request {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t target_handle;
    uint32_t reserved0;
    uint64_t scene_id;
    uint64_t generation;
    double dpi_scale_x;
    double dpi_scale_y;
    uint64_t monotonic_time_nanoseconds;
    uint64_t request_serial;
} progpu_native_mil_scene_build_request;

typedef struct progpu_native_mil_scene_build_result {
    uint32_t struct_size;
    uint32_t flags;
    uint64_t request_serial;
    uint64_t next_due_time_nanoseconds;
    uint64_t stream_bytes;
} progpu_native_mil_scene_build_result;

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
 * The _with_dpi variants retain source DPI atomically with content. Both axes
 * must be finite and positive and yield finite natural dimensions. Existing
 * entry points remain ABI-compatible and bind at 96 DPI. DPI never changes
 * physical pixel dimensions, row stride, or explicit ImageDrawing destinations.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height,
    uint32_t row_bytes,
    const void* pixels,
    size_t pixel_size,
    double dpi_x,
    double dpi_y);
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
 * Declares a canonical TYPE_BITMAPSOURCE as a live external image. The scene
 * carries dimensions only; the compositor binds a typed same-device texture
 * view before installation.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_bitmap_source_external_image_with_dpi(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height,
    double dpi_x,
    double dpi_y);
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_bitmap_source_external_image(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height);
/*
 * Binds copied front-buffer pixels to TYPE_DOUBLEBUFFEREDBITMAP. This replaces
 * the process-local CSwDoubleBufferedBitmap pointer used by WriteableBitmap.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_double_buffered_bitmap_rgba8_with_dpi(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height,
    uint32_t row_bytes,
    const void* pixels,
    size_t pixel_size,
    double dpi_x,
    double dpi_y);
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_double_buffered_bitmap_rgba8(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height,
    uint32_t row_bytes,
    const void* pixels,
    size_t pixel_size);
/*
 * Declares TYPE_DOUBLEBUFFEREDBITMAP front-buffer content as a live typed
 * same-device image. Copy-forward synchronization completes before binding.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_double_buffered_bitmap_external_image_with_dpi(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height,
    double dpi_x,
    double dpi_y);
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_double_buffered_bitmap_external_image(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height);
/*
 * Declares a canonical TYPE_MEDIAPLAYER as a live external image. The scene
 * remains pointer-free; the compositor binds the same-device texture view by
 * the emitted scene resource identity before installation.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_media_player_external_image(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height);
/*
 * Declares canonical TYPE_D3DIMAGE content as a live external image. Portable
 * MilCmdD3DImage/MilCmdD3DImagePresent packets carry zero process handles;
 * the typed texture lease owns synchronization and content_version identifies
 * the presented retained content.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_d3d_image_external_image(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height,
    uint64_t content_version);
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
 * Binds exact local DrawingGroup content bounds for spatial opacity-mask
 * mapping and bounded group composition. Canonical MIL carries child handles
 * but not this Drawing-derived metadata.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_drawing_group_bounds(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    double x,
    double y,
    double width,
    double height);
/*
 * Binds exact source-built Visual descendant bounds for target-space
 * BitmapCache page sizing, bounded effect isolation, and bounded Visual
 * opacity/opacity-mask groups. The canonical Visual, cache, and effect packets
 * do not carry this compositor-derived metadata.
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
 * Binds one pointer-free flattened 3D scene to a canonical
 * TYPE_VIEWPORT3DVISUAL handle. Source-built WPF owns camera/model traversal;
 * ProGPU retains and executes the resulting camera, mesh, vertex, and index
 * payload without managed-object or process-local pointer dependencies.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_viewport3d_scene(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    const progpu_native_scene_camera_3d* camera,
    progpu_native_image_rect viewport,
    const progpu_native_scene_mesh_3d* meshes,
    size_t mesh_count,
    const progpu_native_scene_mesh_3d_vertex* vertices,
    size_t vertex_count,
    const uint32_t* indices,
    size_t index_count);
/* Extended retained 3D binding with a copied bounded MIL light array. Mesh
 * light_offset/light_count ranges address this array; the legacy entry point
 * remains valid when every range is zero. */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_viewport3d_scene_lights(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    const progpu_native_scene_camera_3d* camera,
    progpu_native_image_rect viewport,
    const progpu_native_scene_mesh_3d* meshes,
    size_t mesh_count,
    const progpu_native_scene_mesh_3d_vertex* vertices,
    size_t vertex_count,
    const uint32_t* indices,
    size_t index_count,
    const progpu_native_scene_light_3d* lights,
    size_t light_count);
/* Extended retained 3D binding with exactly one canonical solid, linear, or
 * radial material brush per mesh. Gradient stop ranges address the copied
 * gradient_stops array. Both arrays remain pointer-free after this call. */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_set_viewport3d_scene_materials(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    const progpu_native_scene_camera_3d* camera,
    progpu_native_image_rect viewport,
    const progpu_native_scene_mesh_3d* meshes,
    size_t mesh_count,
    const progpu_native_scene_mesh_3d_vertex* vertices,
    size_t vertex_count,
    const uint32_t* indices,
    size_t index_count,
    const progpu_native_scene_light_3d* lights,
    size_t light_count,
    const progpu_native_scene_brush* materials,
    size_t material_count,
    const progpu_native_scene_gradient_stop* gradient_stops,
    size_t gradient_stop_count);
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

/*
 * Append-only stateful build entry point. Repeating an identical request
 * returns the same retained stream without advancing state twice. The legacy
 * build_scene entry point remains ABI-compatible and stateless.
 */
PROGPU_NATIVE_API progpu_native_mil_status
progpu_native_mil_channel_build_scene_with_request(
    progpu_native_mil_channel* channel,
    const progpu_native_mil_scene_build_request* request,
    void* destination,
    size_t destination_size,
    size_t* bytes_written,
    progpu_native_mil_scene_metrics* metrics,
    progpu_native_mil_scene_build_result* build_result);

#ifdef __cplusplus
}
#endif

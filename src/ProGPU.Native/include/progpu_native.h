#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(PROGPU_NATIVE_BUILD)
#    define PROGPU_NATIVE_API __declspec(dllexport)
#  else
#    define PROGPU_NATIVE_API __declspec(dllimport)
#  endif
#else
#  define PROGPU_NATIVE_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
extern "C" {
#endif

typedef struct progpu_native_engine progpu_native_engine;

enum {
    PROGPU_NATIVE_ABI_VERSION = 1U,
    PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05 = 1U,
    PROGPU_NATIVE_CAPABILITY_SOLID_RECT_BATCH = 1ULL << 0U,
    PROGPU_NATIVE_CAPABILITY_SHARED_VECTOR_SHADER = 1ULL << 1U,
    PROGPU_NATIVE_CAPABILITY_EXTERNAL_TARGET = 1ULL << 2U,
    PROGPU_NATIVE_CAPABILITY_INDEXED_ANALYTIC_BATCH = 1ULL << 3U,
    PROGPU_NATIVE_CAPABILITY_AFFINE_2D = 1ULL << 4U,
    PROGPU_NATIVE_CAPABILITY_INDEXED_GEOMETRY_BATCH = 1ULL << 5U,
    PROGPU_NATIVE_CAPABILITY_DEVICE_STROKES = 1ULL << 6U
};

typedef enum progpu_native_status {
    PROGPU_NATIVE_STATUS_SUCCESS = 0,
    PROGPU_NATIVE_STATUS_INVALID_ARGUMENT = 1,
    PROGPU_NATIVE_STATUS_UNSUPPORTED = 2,
    PROGPU_NATIVE_STATUS_OUT_OF_MEMORY = 3,
    PROGPU_NATIVE_STATUS_WRONG_THREAD = 4,
    PROGPU_NATIVE_STATUS_DEVICE_LOST = 5,
    PROGPU_NATIVE_STATUS_INTERNAL_ERROR = 6
} progpu_native_status;

typedef enum progpu_native_texture_format {
    PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM = 1,
    PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM = 2,
    PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM_SRGB = 3,
    PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM_SRGB = 4
} progpu_native_texture_format;

typedef struct progpu_native_engine_info {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t backend_abi;
    uint32_t reserved;
    uint64_t capabilities;
    char name[64];
} progpu_native_engine_info;

/*
 * The device and queue are opaque handles from the exact WebGPU C ABI named by
 * backend_abi. The engine retains both handles until it is destroyed. A build
 * must reject any other ABI rather than reinterpret incompatible descriptors.
 */
typedef struct progpu_native_engine_options {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t backend_abi;
    uint32_t target_format;
    uintptr_t device;
    uintptr_t queue;
    uint64_t flags;
} progpu_native_engine_options;

typedef struct progpu_native_color {
    float r;
    float g;
    float b;
    float a;
} progpu_native_color;

typedef struct progpu_native_rect {
    float x;
    float y;
    float width;
    float height;
    progpu_native_color color;
} progpu_native_rect;

typedef enum progpu_native_primitive_kind {
    PROGPU_NATIVE_PRIMITIVE_RECTANGLE = 0,
    PROGPU_NATIVE_PRIMITIVE_ELLIPSE = 1,
    PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE = 2
} progpu_native_primitive_kind;

enum {
    PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED = 1U << 0U,
    PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE = 1U << 1U,
    PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE = 1U << 2U
};

typedef enum progpu_native_geometry_primitive_kind {
    PROGPU_NATIVE_GEOMETRY_LINE = 0,
    PROGPU_NATIVE_GEOMETRY_TRIANGLE = 1,
    PROGPU_NATIVE_GEOMETRY_QUADRILATERAL = 2
} progpu_native_geometry_primitive_kind;

typedef struct progpu_native_point {
    float x;
    float y;
} progpu_native_point;

/*
 * System.Numerics-compatible row-vector affine transform:
 * x' = x*m11 + y*m21 + m31; y' = x*m12 + y*m22 + m32.
 */
typedef struct progpu_native_affine_2d {
    float m11;
    float m12;
    float m21;
    float m22;
    float m31;
    float m32;
} progpu_native_affine_2d;

/*
 * One analytic draw record. A zero stroke_thickness selects a fill; a positive
 * value selects a centered local-coordinate stroke. Each record may carry an
 * independent affine transform while the whole batch remains one indexed draw.
 */
typedef struct progpu_native_analytic_primitive {
    uint32_t kind;
    uint32_t flags;
    float x;
    float y;
    float width;
    float height;
    float corner_radius;
    float stroke_thickness;
    progpu_native_color color;
    progpu_native_affine_2d transform;
} progpu_native_analytic_primitive;

/*
 * A geometry record uses p0/p1 for a flat-cap line, p0..p2 for a filled
 * triangle, or p0..p3 for a filled quadrilateral. A normal line stroke scales
 * with transform; HAIRLINE selects one framebuffer pixel and
 * FIXED_DEVICE_STROKE keeps stroke_thickness in framebuffer pixels. The two
 * device-stroke flags are mutually exclusive and apply only to lines.
 */
typedef struct progpu_native_geometry_primitive {
    uint32_t kind;
    uint32_t flags;
    progpu_native_point p0;
    progpu_native_point p1;
    progpu_native_point p2;
    progpu_native_point p3;
    float stroke_thickness;
    float reserved;
    progpu_native_color color;
    progpu_native_affine_2d transform;
} progpu_native_geometry_primitive;

/*
 * width and height are physical target pixels. Rectangle coordinates are
 * logical pixels and dpi_scale maps logical coordinates to physical pixels.
 * target_view is borrowed for the duration of the call.
 */
typedef struct progpu_native_frame {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    float dpi_scale;
    uintptr_t target_view;
    progpu_native_color clear_color;
    const progpu_native_rect* rects;
    size_t rect_count;
} progpu_native_frame;

typedef struct progpu_native_frame_metrics {
    uint32_t struct_size;
    uint32_t draw_call_count;
    uint32_t vertex_count;
    uint32_t reserved;
    uint64_t vertex_upload_bytes;
    uint64_t uniform_upload_bytes;
    uint64_t submission_count;
} progpu_native_frame_metrics;

typedef struct progpu_native_analytic_frame {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    float dpi_scale;
    uintptr_t target_view;
    progpu_native_color clear_color;
    const progpu_native_analytic_primitive* primitives;
    size_t primitive_count;
} progpu_native_analytic_frame;

typedef struct progpu_native_analytic_frame_metrics {
    uint32_t struct_size;
    uint32_t draw_call_count;
    uint32_t vertex_count;
    uint32_t index_count;
    uint64_t vertex_upload_bytes;
    uint64_t index_upload_bytes;
    uint64_t uniform_upload_bytes;
    uint64_t submission_count;
} progpu_native_analytic_frame_metrics;

typedef struct progpu_native_geometry_frame {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    float dpi_scale;
    uintptr_t target_view;
    progpu_native_color clear_color;
    const progpu_native_geometry_primitive* primitives;
    size_t primitive_count;
} progpu_native_geometry_frame;

typedef struct progpu_native_geometry_frame_metrics {
    uint32_t struct_size;
    uint32_t draw_call_count;
    uint32_t vertex_count;
    uint32_t index_count;
    uint64_t vertex_upload_bytes;
    uint64_t index_upload_bytes;
    uint64_t brush_upload_bytes;
    uint64_t uniform_upload_bytes;
    uint64_t submission_count;
} progpu_native_geometry_frame_metrics;

PROGPU_NATIVE_API uint32_t progpu_native_get_abi_version(void);
PROGPU_NATIVE_API uint8_t progpu_native_get_info(
    progpu_native_engine_info* info);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_create(
    const progpu_native_engine_options* options,
    progpu_native_engine** engine);
PROGPU_NATIVE_API void progpu_native_engine_destroy(
    progpu_native_engine* engine);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_render(
    progpu_native_engine* engine,
    const progpu_native_frame* frame,
    progpu_native_frame_metrics* metrics);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_render_analytic(
    progpu_native_engine* engine,
    const progpu_native_analytic_frame* frame,
    progpu_native_analytic_frame_metrics* metrics);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_render_geometry(
    progpu_native_engine* engine,
    const progpu_native_geometry_frame* frame,
    progpu_native_geometry_frame_metrics* metrics);
PROGPU_NATIVE_API size_t progpu_native_engine_get_last_error(
    const progpu_native_engine* engine,
    char* destination,
    size_t destination_size);

#ifdef __cplusplus
}
#endif

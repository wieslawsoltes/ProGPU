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
    PROGPU_NATIVE_CAPABILITY_DEVICE_STROKES = 1ULL << 6U,
    PROGPU_NATIVE_CAPABILITY_BEZIER_STROKES = 1ULL << 7U,
    PROGPU_NATIVE_CAPABILITY_STROKE_CAPS = 1ULL << 8U,
    PROGPU_NATIVE_CAPABILITY_CONNECTED_STROKES = 1ULL << 9U,
    PROGPU_NATIVE_CAPABILITY_SPLINE_STROKES = 1ULL << 10U
};

enum {
    PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH = 1U << 0U
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
    PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE = 1U << 2U,
    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT = 3U,
    PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK = 3U << 3U,
    PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT = 5U,
    PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK = 3U << 5U
};

typedef enum progpu_native_stroke_cap {
    PROGPU_NATIVE_STROKE_CAP_FLAT = 0,
    PROGPU_NATIVE_STROKE_CAP_SQUARE = 1,
    PROGPU_NATIVE_STROKE_CAP_ROUND = 2,
    PROGPU_NATIVE_STROKE_CAP_TRIANGLE = 3
} progpu_native_stroke_cap;

typedef enum progpu_native_stroke_join {
    PROGPU_NATIVE_STROKE_JOIN_MITER = 0,
    PROGPU_NATIVE_STROKE_JOIN_BEVEL = 1,
    PROGPU_NATIVE_STROKE_JOIN_ROUND = 2
} progpu_native_stroke_join;

enum {
    PROGPU_NATIVE_POLYLINE_FLAG_EDGE_ALIASED = 1U << 0U,
    PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE = 1U << 1U,
    PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE = 1U << 2U,
    PROGPU_NATIVE_POLYLINE_START_CAP_SHIFT = 3U,
    PROGPU_NATIVE_POLYLINE_START_CAP_MASK = 3U << 3U,
    PROGPU_NATIVE_POLYLINE_END_CAP_SHIFT = 5U,
    PROGPU_NATIVE_POLYLINE_END_CAP_MASK = 3U << 5U,
    PROGPU_NATIVE_POLYLINE_JOIN_SHIFT = 7U,
    PROGPU_NATIVE_POLYLINE_JOIN_MASK = 3U << 7U,
    PROGPU_NATIVE_POLYLINE_FLAG_CLOSED = 1U << 9U
};

typedef enum progpu_native_geometry_primitive_kind {
    PROGPU_NATIVE_GEOMETRY_LINE = 0,
    PROGPU_NATIVE_GEOMETRY_TRIANGLE = 1,
    PROGPU_NATIVE_GEOMETRY_QUADRILATERAL = 2,
    PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER = 3,
    PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER = 4
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
 * triangle or quadratic Bezier stroke, and p0..p3 for a filled quadrilateral
 * or cubic Bezier stroke. A normal stroke scales with transform; HAIRLINE
 * selects one framebuffer pixel and
 * FIXED_DEVICE_STROKE keeps stroke_thickness in framebuffer pixels. The two
 * device-stroke flags are mutually exclusive and apply only to stroked lines
 * and curves. The start/end cap fields are packed into their documented flag
 * masks and are ignored by filled records.
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
 * A connected stroke borrows a contiguous range from geometry_frame.points.
 * Open records apply their endpoint caps; closed records ignore caps and join
 * the last point back to the first. The point arena remains caller-owned only
 * until progpu_native_engine_render_geometry returns.
 */
typedef struct progpu_native_polyline {
    size_t point_offset;
    size_t point_count;
    progpu_native_color color;
    progpu_native_affine_2d transform;
    float stroke_thickness;
    float miter_limit;
    uint32_t flags;
    uint32_t reserved;
} progpu_native_polyline;

/*
 * A B-spline/NURBS stroke reuses progpu_native_polyline for its control-point
 * range and stroke state. Knots and optional weights borrow ranges from
 * geometry_frame.doubles. A zero weight_count selects unit weights.
 */
typedef struct progpu_native_spline {
    progpu_native_polyline stroke;
    size_t knot_offset;
    size_t knot_count;
    size_t weight_offset;
    size_t weight_count;
    uint32_t degree;
    uint32_t reserved;
} progpu_native_spline;

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
    uint32_t flags;
    uint32_t reserved;
    const progpu_native_point* points;
    size_t point_count;
    const progpu_native_polyline* polylines;
    size_t polyline_count;
    const double* doubles;
    size_t double_count;
    const progpu_native_spline* splines;
    size_t spline_count;
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
    uint64_t payload_hash;
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

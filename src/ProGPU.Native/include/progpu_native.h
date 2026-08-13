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
    PROGPU_NATIVE_ABI_VERSION = 3U,
    PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05 = 1U,
    PROGPU_NATIVE_BACKEND_ABI_DAWN_WEBSCENE_2026_07 = 2U,
    PROGPU_NATIVE_BACKEND_ABI_BROWSER_WEBGPU_2025_10 = 3U
};

/* Capability masks are explicitly 64-bit C constants. Anonymous enum
 * underlying types are implementation-defined and MSVC rejects bits 32+
 * under /WX even when the initializer uses an unsigned-long-long literal. */
#define PROGPU_NATIVE_CAPABILITY_SOLID_RECT_BATCH (UINT64_C(1) << 0U)
#define PROGPU_NATIVE_CAPABILITY_SHARED_VECTOR_SHADER (UINT64_C(1) << 1U)
#define PROGPU_NATIVE_CAPABILITY_EXTERNAL_TARGET (UINT64_C(1) << 2U)
#define PROGPU_NATIVE_CAPABILITY_INDEXED_ANALYTIC_BATCH (UINT64_C(1) << 3U)
#define PROGPU_NATIVE_CAPABILITY_AFFINE_2D (UINT64_C(1) << 4U)
#define PROGPU_NATIVE_CAPABILITY_INDEXED_GEOMETRY_BATCH (UINT64_C(1) << 5U)
#define PROGPU_NATIVE_CAPABILITY_DEVICE_STROKES (UINT64_C(1) << 6U)
#define PROGPU_NATIVE_CAPABILITY_BEZIER_STROKES (UINT64_C(1) << 7U)
#define PROGPU_NATIVE_CAPABILITY_STROKE_CAPS (UINT64_C(1) << 8U)
#define PROGPU_NATIVE_CAPABILITY_CONNECTED_STROKES (UINT64_C(1) << 9U)
#define PROGPU_NATIVE_CAPABILITY_SPLINE_STROKES (UINT64_C(1) << 10U)
#define PROGPU_NATIVE_CAPABILITY_DASHED_STROKES (UINT64_C(1) << 11U)
#define PROGPU_NATIVE_CAPABILITY_RETAINED_GEOMETRY_REPLAY (UINT64_C(1) << 12U)
#define PROGPU_NATIVE_CAPABILITY_PATH_FILL_ATLAS (UINT64_C(1) << 13U)
#define PROGPU_NATIVE_CAPABILITY_POSITIONED_GLYPH_ATLAS (UINT64_C(1) << 14U)
#define PROGPU_NATIVE_CAPABILITY_RESIZABLE_ATLASES (UINT64_C(1) << 15U)
#define PROGPU_NATIVE_CAPABILITY_RETAINED_RGBA_IMAGE (UINT64_C(1) << 16U)
#define PROGPU_NATIVE_CAPABILITY_EXTERNAL_RGBA_VIEW (UINT64_C(1) << 17U)
#define PROGPU_NATIVE_CAPABILITY_EXTERNAL_IMAGE_MASK (UINT64_C(1) << 18U)
#define PROGPU_NATIVE_CAPABILITY_EXPLICIT_QUEUE_TIMELINE (UINT64_C(1) << 19U)
#define PROGPU_NATIVE_CAPABILITY_FRAME_DRAW_STATE (UINT64_C(1) << 20U)
#define PROGPU_NATIVE_CAPABILITY_GROUP_OPACITY (UINT64_C(1) << 21U)
#define PROGPU_NATIVE_CAPABILITY_COMMON_GROUP_MASK (UINT64_C(1) << 22U)
#define PROGPU_NATIVE_CAPABILITY_ANALYTIC_ROUNDED_GROUP_MASK (UINT64_C(1) << 23U)
#define PROGPU_NATIVE_CAPABILITY_RETAINED_VECTOR_CLIP_CHAIN (UINT64_C(1) << 24U)
#define PROGPU_NATIVE_CAPABILITY_GROUP_GAUSSIAN_BLUR (UINT64_C(1) << 25U)
#define PROGPU_NATIVE_CAPABILITY_GROUP_DROP_SHADOW (UINT64_C(1) << 26U)
#define PROGPU_NATIVE_CAPABILITY_BOUNDED_GROUP_EFFECT_CHAIN (UINT64_C(1) << 27U)
#define PROGPU_NATIVE_CAPABILITY_GROUP_BLEND_MODES (UINT64_C(1) << 28U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_SCENE_SNAPSHOTS (UINT64_C(1) << 29U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_SCENE_RENDERING (UINT64_C(1) << 30U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_RETAINED_BRUSHES (UINT64_C(1) << 31U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_RETAINED_TEXT_STYLES (UINT64_C(1) << 32U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_COLOR_GLYPH_ATLAS (UINT64_C(1) << 33U)

#if defined(__cplusplus)
enum : uint32_t {
#else
enum {
#endif
    PROGPU_NATIVE_SCENE_STREAM_MAGIC = 0x31534750U,
    PROGPU_NATIVE_SCENE_STREAM_VERSION = 1U,
    PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER = 0x01020304U,
    PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH = 64U,
    PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES = 256U * 1024U * 1024U,
    PROGPU_NATIVE_SCENE_MAX_COMMANDS = 1024U * 1024U,
    PROGPU_NATIVE_SCENE_MAX_RESOURCES = 256U * 1024U,
    PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS = 16U,
    PROGPU_NATIVE_SCENE_MAX_LAYER_BYTES = 256U * 1024U * 1024U,
    PROGPU_NATIVE_SCENE_MAX_BRUSHES = 1024U * 1024U,
    PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS = 64U * 1024U,
    PROGPU_NATIVE_SCENE_MAX_DRAW_BRUSH_INDICES = 1024U * 1024U,
    PROGPU_NATIVE_SCENE_MAX_TEXT_STYLES = 1024U * 1024U,
    PROGPU_NATIVE_SCENE_NO_INDEX = 0xffffffffU,
    PROGPU_NATIVE_SCENE_RECORD_REQUIRED = 1U << 0U,
    PROGPU_NATIVE_SCENE_GLYPH_STYLED = 1U << 1U,
    PROGPU_NATIVE_SCENE_COLOR_GLYPH_BITMAPS = 1U << 2U,
    PROGPU_NATIVE_SCENE_METRICS_SNAPSHOT_REUSED = 1U << 0U
};

typedef enum progpu_native_scene_resource_kind {
    PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH = 1,
    PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH = 2,
    PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN = 3,
    PROGPU_NATIVE_SCENE_RESOURCE_IMAGE = 4,
    PROGPU_NATIVE_SCENE_RESOURCE_STATE = 5,
    PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK = 6,
    PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN = 7,
    PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE = 8,
    PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE = 9
} progpu_native_scene_resource_kind;

typedef enum progpu_native_scene_text_rendering_mode {
    PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE = 0,
    PROGPU_NATIVE_SCENE_TEXT_ALIASED = 1,
    PROGPU_NATIVE_SCENE_TEXT_CLEARTYPE = 2
} progpu_native_scene_text_rendering_mode;

/* Values intentionally match ProGPU.Scene.GpuBrush and Vector.wgsl. */
typedef enum progpu_native_scene_brush_kind {
    PROGPU_NATIVE_SCENE_BRUSH_SOLID = 0,
    PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT = 1,
    PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT = 2,
    PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT = 5,
    PROGPU_NATIVE_SCENE_BRUSH_SWEEP_GRADIENT = 6,
    PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE = 7
} progpu_native_scene_brush_kind;

enum {
    PROGPU_NATIVE_SCENE_PERLIN_TABLE_RECORDS = 512U,
    PROGPU_NATIVE_SCENE_MAX_PERLIN_OCTAVES = 255U
};

typedef enum progpu_native_scene_gradient_spread {
    PROGPU_NATIVE_SCENE_GRADIENT_PAD = 0,
    PROGPU_NATIVE_SCENE_GRADIENT_REFLECT = 1,
    PROGPU_NATIVE_SCENE_GRADIENT_REPEAT = 2,
    PROGPU_NATIVE_SCENE_GRADIENT_DECAL = 3
} progpu_native_scene_gradient_spread;

typedef enum progpu_native_scene_gradient_interpolation {
    PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB = 0,
    PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SCRGB = 1
} progpu_native_scene_gradient_interpolation;

typedef enum progpu_native_scene_layer_mask_kind {
    PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE = 1,
    PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP = 2
} progpu_native_scene_layer_mask_kind;

typedef enum progpu_native_scene_command_kind {
    PROGPU_NATIVE_SCENE_COMMAND_SAVE = 1,
    PROGPU_NATIVE_SCENE_COMMAND_RESTORE = 2,
    PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER = 3,
    PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER = 4,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC = 16,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH = 17,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN = 18,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE = 19
} progpu_native_scene_command_kind;

typedef enum progpu_native_scene_validation_error {
    PROGPU_NATIVE_SCENE_VALIDATION_NONE = 0,
    PROGPU_NATIVE_SCENE_VALIDATION_HEADER = 1,
    PROGPU_NATIVE_SCENE_VALIDATION_RANGE = 2,
    PROGPU_NATIVE_SCENE_VALIDATION_RECORD = 3,
    PROGPU_NATIVE_SCENE_VALIDATION_ID = 4,
    PROGPU_NATIVE_SCENE_VALIDATION_STACK = 5,
    PROGPU_NATIVE_SCENE_VALIDATION_VALUE = 6,
    PROGPU_NATIVE_SCENE_VALIDATION_GENERATION = 7,
    PROGPU_NATIVE_SCENE_VALIDATION_UNSUPPORTED = 8
} progpu_native_scene_validation_error;

enum {
    PROGPU_NATIVE_DRAW_STATE_CLIP_RECT = 1U << 0U
};

enum {
    PROGPU_NATIVE_SCENE_STATE_CLIP_RECT = 1U << 0U
};

enum {
    PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX = 1U << 0U
};

enum {
    PROGPU_NATIVE_SCENE_LAYER_BOUNDS = 1U << 0U,
    PROGPU_NATIVE_SCENE_LAYER_BACKDROP = 1U << 1U,
    PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION = 1U << 2U
};

typedef enum progpu_native_image_sampling {
    PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST = 0,
    PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR = 1,
    PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC = 2
} progpu_native_image_sampling;

typedef enum progpu_native_group_mask_kind {
    PROGPU_NATIVE_GROUP_MASK_NONE = 0,
    PROGPU_NATIVE_GROUP_MASK_TEXTURE = 1,
    PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE = 2,
    PROGPU_NATIVE_GROUP_MASK_VECTOR_CLIP_CHAIN = 3
} progpu_native_group_mask_kind;

typedef enum progpu_native_clip_operation {
    PROGPU_NATIVE_CLIP_INTERSECT = 0,
    PROGPU_NATIVE_CLIP_DIFFERENCE = 1
} progpu_native_clip_operation;

typedef enum progpu_native_group_effect_kind {
    PROGPU_NATIVE_GROUP_EFFECT_NONE = 0,
    PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR = 1,
    PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW = 2
} progpu_native_group_effect_kind;

/* Values intentionally match ProGPU.Backend.GpuBlendMode. */
typedef enum progpu_native_blend_mode {
    PROGPU_NATIVE_BLEND_SRC_OVER = 0,
    PROGPU_NATIVE_BLEND_SRC = 1,
    PROGPU_NATIVE_BLEND_DST = 2,
    PROGPU_NATIVE_BLEND_SRC_IN = 3,
    PROGPU_NATIVE_BLEND_DST_IN = 4,
    PROGPU_NATIVE_BLEND_SRC_OUT = 5,
    PROGPU_NATIVE_BLEND_DST_OUT = 6,
    PROGPU_NATIVE_BLEND_SRC_ATOP = 7,
    PROGPU_NATIVE_BLEND_DST_ATOP = 8,
    PROGPU_NATIVE_BLEND_XOR = 9,
    PROGPU_NATIVE_BLEND_DST_OVER = 10,
    PROGPU_NATIVE_BLEND_MULTIPLY = 11,
    PROGPU_NATIVE_BLEND_SCREEN = 12,
    PROGPU_NATIVE_BLEND_DARKEN = 13,
    PROGPU_NATIVE_BLEND_LIGHTEN = 14,
    PROGPU_NATIVE_BLEND_EXCLUSION = 15,
    PROGPU_NATIVE_BLEND_PLUS = 16,
    PROGPU_NATIVE_BLEND_CLEAR = 17,
    PROGPU_NATIVE_BLEND_OVERLAY = 18,
    PROGPU_NATIVE_BLEND_COLOR_DODGE = 19,
    PROGPU_NATIVE_BLEND_COLOR_BURN = 20,
    PROGPU_NATIVE_BLEND_HARD_LIGHT = 21,
    PROGPU_NATIVE_BLEND_SOFT_LIGHT = 22,
    PROGPU_NATIVE_BLEND_DIFFERENCE = 23,
    PROGPU_NATIVE_BLEND_HUE = 24,
    PROGPU_NATIVE_BLEND_SATURATION = 25,
    PROGPU_NATIVE_BLEND_COLOR = 26,
    PROGPU_NATIVE_BLEND_LUMINOSITY = 27,
    PROGPU_NATIVE_BLEND_MODULATE = 28
} progpu_native_blend_mode;

enum {
    PROGPU_NATIVE_MAX_GROUP_EFFECTS = 8U
};

typedef enum progpu_native_mask_texture_format {
    PROGPU_NATIVE_MASK_TEXTURE_R8_UNORM = 1,
    PROGPU_NATIVE_MASK_TEXTURE_RGBA8_UNORM = 2,
    PROGPU_NATIVE_MASK_TEXTURE_BGRA8_UNORM = 3
} progpu_native_mask_texture_format;

typedef enum progpu_native_image_source_flags {
    PROGPU_NATIVE_IMAGE_SOURCE_UPLOAD_RGBA8 = 0,
    PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW = 1U << 0U
} progpu_native_image_source_flags;

enum {
    PROGPU_NATIVE_GEOMETRY_FRAME_CAPTURE_PAYLOAD_HASH = 1U << 0U,
    PROGPU_NATIVE_GEOMETRY_FRAME_RETAIN_COMPILED_PAYLOAD = 1U << 1U
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
 * Version-one semantic scene streams are little-endian, pointer-free blobs.
 * Every offset is absolute from the first header byte. Table strides permit
 * append-only record growth while the declared struct_size keeps readers from
 * interpreting a newer record layout as the version-one prefix.
 */
typedef struct progpu_native_scene_header {
    uint32_t struct_size;
    uint32_t magic;
    uint32_t stream_version;
    uint32_t endian_marker;
    uint32_t flags;
    uint32_t total_size;
    uint64_t scene_id;
    uint64_t generation;
    uint32_t command_offset;
    uint32_t command_count;
    uint32_t command_stride;
    uint32_t resource_offset;
    uint32_t resource_count;
    uint32_t resource_stride;
    uint32_t arena_offset;
    uint32_t arena_size;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_scene_header;

typedef struct progpu_native_scene_resource {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t reserved;
    uint64_t resource_id;
    uint64_t generation;
    uint32_t payload_offset;
    uint32_t payload_size;
    uint32_t auxiliary_offset;
    uint32_t auxiliary_size;
} progpu_native_scene_resource;

typedef struct progpu_native_scene_command {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t reserved;
    uint64_t command_id;
    uint32_t state_index;
    uint32_t resource_index;
    uint32_t payload_offset;
    uint32_t payload_size;
    float bounds_x;
    float bounds_y;
    float bounds_width;
    float bounds_height;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_scene_command;

typedef struct progpu_native_scene_metrics {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t command_count;
    uint32_t resource_count;
    uint32_t draw_count;
    uint32_t maximum_stack_depth;
    uint32_t validation_error;
    uint32_t error_offset;
    uint64_t scene_id;
    uint64_t generation;
    uint64_t snapshot_bytes;
    uint64_t payload_bytes;
} progpu_native_scene_metrics;

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

typedef struct progpu_native_image_rect {
    float x;
    float y;
    float width;
    float height;
} progpu_native_image_rect;

/*
 * Pointer-free semantic draw state. Transform and opacity are absolute for
 * the state resource: a SAVE command carrying this state makes it current
 * until its matching RESTORE, while a draw command carrying it overrides the
 * current state for that draw only. The clip rectangle is expressed in
 * logical target coordinates and is enabled by
 * PROGPU_NATIVE_SCENE_STATE_CLIP_RECT. Reserved fields must remain zero.
 */
typedef struct progpu_native_scene_state {
    uint32_t struct_size;
    uint32_t flags;
    progpu_native_affine_2d transform;
    float opacity;
    uint32_t reserved;
    progpu_native_image_rect clip_rect;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_scene_state;

/*
 * Pointer-free semantic isolated-layer descriptor stored directly in one
 * PUSH_LAYER command payload. Bounds are logical target coordinates and are
 * enabled by PROGPU_NATIVE_SCENE_LAYER_BOUNDS; an absent bound means the full
 * target. Opacity and blend mode are applied once when the layer is restored.
 * Mask/effect indices are optional typed resource-table references. A mask
 * references PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK and an effect references
 * PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN; NO_INDEX disables each feature.
 * Revisions are caller-owned retained identities; zero disables reuse hints.
 */
typedef struct progpu_native_scene_layer {
    uint32_t struct_size;
    uint32_t flags;
    progpu_native_image_rect bounds;
    float opacity;
    uint32_t blend_mode;
    uint32_t mask_resource_index;
    uint32_t effect_resource_index;
    uint64_t content_revision;
    uint64_t composite_revision;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_scene_layer;

/*
 * Pointer-free semantic layer mask. The first additive kind is an analytic
 * rounded rectangle in logical target coordinates. The transform maps mask
 * local coordinates to logical target coordinates; radii are normalized by
 * the executor using the same bounded CSS side-fit rule as common masks.
 * Resource generation is the immutable retained identity. All reserved fields
 * and flags remain zero.
 */
typedef struct progpu_native_scene_layer_mask {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t reserved;
    progpu_native_image_rect bounds;
    progpu_native_affine_2d transform;
    float corner_radii_x[4];
    float corner_radii_y[4];
    float opacity;
    uint32_t reserved0;
    uint32_t reserved1;
    uint32_t reserved2;
} progpu_native_scene_layer_mask;

/*
 * Pointer-free retained R8 coverage mask metadata. The auxiliary resource
 * span owns the row-strided coverage bytes. Bounds are mask-local logical
 * coordinates and transform maps those coordinates into logical target
 * coordinates. The executor inverts that affine once when compiling the
 * immutable replay span; rotation, anisotropic scale, and shear therefore do
 * not require a CPU resample. Sampling accepts nearest or linear only.
 */
typedef struct progpu_native_scene_layer_coverage_mask {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t width;
    uint32_t height;
    uint32_t row_bytes;
    uint32_t sampling;
    uint32_t reserved0;
    progpu_native_image_rect bounds;
    progpu_native_affine_2d transform;
    float opacity;
    uint32_t reserved1;
} progpu_native_scene_layer_coverage_mask;

/*
 * Version-one upload-backed image command payload. Its resource payload is a
 * tightly owned RGBA8 byte span. External views remain on the existing typed
 * image API until a device-domain scene resource registry is introduced.
 */
typedef struct progpu_native_scene_image_draw {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t image_width;
    uint32_t image_height;
    uint32_t row_bytes;
    uint32_t sampling;
    progpu_native_image_rect source_rect;
    progpu_native_image_rect destination_rect;
    progpu_native_affine_2d transform;
    float opacity;
    uint32_t reserved;
} progpu_native_scene_image_draw;

/*
 * Optional suffix required by semantic image draws whose sampling mode is
 * CUBIC. The B/C parameters feed the production Texture.wgsl fixed 4x4
 * Mitchell-Netravali kernel. Other sampling modes must not carry this suffix.
 */
typedef struct progpu_native_scene_image_sampling_options {
    uint32_t struct_size;
    uint32_t flags;
    float cubic_b;
    float cubic_c;
} progpu_native_scene_image_sampling_options;

/*
 * Optional exact suffix selected by PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX.
 * Rows and offset form a 4x5 affine transform over straight RGBA. The two
 * reserved words preserve 16-byte GPU-uniform alignment.
 */
typedef struct progpu_native_scene_image_color_matrix {
    uint32_t struct_size;
    uint32_t flags;
    float red[4];
    float green[4];
    float blue[4];
    float alpha[4];
    float offset[4];
    uint32_t reserved[2];
} progpu_native_scene_image_color_matrix;

/*
 * Exact pointer-free storage layout consumed by production Vector.wgsl.
 * A brush-table resource stores one or more of these records in payload and
 * its optional auxiliary span stores exact gradient-stop records. StopOffset
 * is local to that resource. The semantic compiler packs referenced brushes
 * and stops into one retained scene-wide GPU page and rewrites indices once.
 *
 * The native semantic lane accepts solid, linear, radial, two-point conical,
 * sweep, and Perlin-noise brushes. Perlin overloads StartPoint/EndPoint/Center
 * as base frequency, stitch period, and tile size; Radius is the normalized
 * seed, StopCount is the bounded octave count, and SpreadMethod 0/1 selects
 * fractal/turbulence noise. Interpolation 0 selects the bounded hash fallback;
 * interpolation 1 references exactly 512 packed permutation/gradient records
 * at StopOffset. Hatch remains an explicit extension command rather than a
 * semantic brush kind and therefore fails closed here.
 */
typedef struct progpu_native_scene_brush {
    uint32_t type;
    float opacity;
    progpu_native_point start_point;
    progpu_native_point end_point;
    progpu_native_point center;
    float radius;
    uint32_t stop_count;
    float radius_y;
    uint32_t spread_method;
    uint32_t color_interpolation_mode;
    uint32_t stop_offset;
    uint32_t reserved0;
    uint32_t reserved1;
    progpu_native_color colors[8];
    float offsets0[4];
    float offsets1[4];
    float coordinate_transform0[4];
    float coordinate_transform1[4];
} progpu_native_scene_brush;

typedef struct progpu_native_scene_gradient_stop {
    progpu_native_color color;
    float offset;
    uint32_t reserved0;
    uint32_t reserved1;
    uint32_t reserved2;
} progpu_native_scene_gradient_stop;

/*
 * Optional DRAW_ANALYTIC/DRAW_PATH command payload prefix. Exactly
 * brush_count uint32 indices follow this header in the same payload span.
 * Each index addresses the named brush-table resource and corresponds to one
 * primitive/path record in source order. The compact map permits one retained
 * brush to be reused by an arbitrary number of records without duplicating a
 * 256-byte GPU brush. Reserved fields must remain zero.
 */
typedef struct progpu_native_scene_draw_brushes {
    uint32_t struct_size;
    uint32_t brush_resource_index;
    uint32_t brush_count;
    uint32_t reserved;
} progpu_native_scene_draw_brushes;

/* Exact retained storage layout consumed by production Text.wgsl. */
typedef struct progpu_native_scene_text_style {
    progpu_native_color color;
    uint32_t text_rendering_mode;
    uint32_t reserved0;
    uint32_t reserved1;
    uint32_t reserved2;
} progpu_native_scene_text_style;

/*
 * Optional styled DRAW_GLYPH_RUN payload. Exactly glyph_count positioned-glyph
 * records follow this prefix. Legacy glyph commands remain a raw glyph array;
 * this form is selected by PROGPU_NATIVE_SCENE_GLYPH_STYLED.
 */
typedef struct progpu_native_scene_glyph_draw {
    uint32_t struct_size;
    uint32_t style_resource_index;
    uint32_t style_index;
    uint32_t glyph_count;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_scene_glyph_draw;

/*
 * Semantic path/glyph resource records use fixed 64-bit arena indices rather
 * than host-sized size_t. Current 64-bit native packages consume these
 * records zero-copy; a future wasm32 build translates the fixed prefix while
 * preserving the same version-one byte stream.
 */
typedef struct progpu_native_scene_path_fill {
    uint64_t segment_offset;
    uint64_t segment_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    progpu_native_color color;
    progpu_native_affine_2d transform;
    uint32_t fill_rule;
    uint32_t sample_grid;
} progpu_native_scene_path_fill;

typedef struct progpu_native_scene_glyph_outline {
    uint64_t segment_offset;
    uint64_t segment_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    float raster_scale;
    float subpixel_x;
} progpu_native_scene_glyph_outline;

/*
 * One decoded straight-alpha RGBA8 color-glyph bitmap. pixel_offset is
 * relative to the owning glyph resource's auxiliary byte span. Font parsing,
 * shaping, and image decoding stay outside the native renderer; C++ only
 * validates, packs, uploads, and replays the decoded pixels.
 */
typedef struct progpu_native_scene_color_glyph_bitmap {
    uint64_t pixel_offset;
    uint32_t width;
    uint32_t height;
    uint32_t row_bytes;
    uint32_t reserved0;
    float bear_x;
    float bear_y;
    float render_width;
    float render_height;
    uint32_t reserved1;
    uint32_t reserved2;
} progpu_native_scene_color_glyph_bitmap;

typedef struct progpu_native_scene_frame {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    float dpi_scale;
    uintptr_t target_view;
    progpu_native_color clear_color;
    uint64_t scene_id;
    uint64_t generation;
} progpu_native_scene_frame;

typedef struct progpu_native_scene_frame_metrics {
    uint32_t struct_size;
    uint32_t command_count;
    uint32_t draw_call_count;
    uint32_t family_switch_count;
    uint64_t submission_count;
    uint64_t vertex_upload_bytes;
    uint64_t index_upload_bytes;
    uint64_t texture_upload_bytes;
    uint64_t uniform_upload_bytes;
    uint64_t coverage_staging_bytes;
    uint64_t payload_hash;
    uint64_t brush_upload_bytes;
    uint64_t gradient_stop_upload_bytes;
    uint64_t text_style_upload_bytes;
    uint64_t color_glyph_upload_bytes;
} progpu_native_scene_frame_metrics;

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
    /* Zero selects a solid stroke; otherwise this is dash_style index + 1. */
    uint32_t dash_style;
} progpu_native_polyline;

/*
 * A reusable dash style borrows alternating on/off multipliers from
 * geometry_frame.doubles. Values and offset are multiplied by the resolved
 * pen thickness. Odd interval counts repeat logically to an even count.
 */
typedef struct progpu_native_dash_style {
    size_t interval_offset;
    size_t interval_count;
    double offset;
    uint32_t cap;
    uint32_t reserved;
} progpu_native_dash_style;

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

typedef enum progpu_native_path_segment_kind {
    PROGPU_NATIVE_PATH_SEGMENT_LINE = 0,
    PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC = 1,
    PROGPU_NATIVE_PATH_SEGMENT_CUBIC = 2,
    PROGPU_NATIVE_PATH_SEGMENT_ARC = 3
} progpu_native_path_segment_kind;

typedef enum progpu_native_fill_rule {
    PROGPU_NATIVE_FILL_RULE_NON_ZERO = 0,
    PROGPU_NATIVE_FILL_RULE_EVEN_ODD = 1
} progpu_native_fill_rule;

/*
 * Exact storage layout consumed by PathRasterizer.wgsl. Arc records store the
 * resolved center in p2, radii in p3, and theta1/delta-theta/rotation radians
 * as float bit patterns in pad0..pad2. Callers may therefore resolve SVG arcs
 * once and transfer a compact immutable segment stream without flattening.
 */
typedef struct progpu_native_path_segment {
    progpu_native_point p0;
    progpu_native_point p1;
    progpu_native_point p2;
    progpu_native_point p3;
    uint32_t kind;
    uint32_t pad0;
    uint32_t pad1;
    uint32_t pad2;
} progpu_native_path_segment;

/*
 * A filled path borrows a contiguous segment range. Bounds are the exact local
 * coverage bounds including analytic curve extrema. The renderer selects a
 * transform-aware atlas resolution, rasterizes with a 4x4 or 8x8 sample grid,
 * and draws one affine coverage quad with the supplied solid color.
 */
typedef struct progpu_native_path_fill {
    size_t segment_offset;
    size_t segment_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    progpu_native_color color;
    progpu_native_affine_2d transform;
    uint32_t fill_rule;
    uint32_t sample_grid;
} progpu_native_path_fill;

/*
 * One ordered clip node borrows a contiguous segment range. Bounds are exact
 * local curve-extrema bounds and transform maps local coordinates to logical
 * target coordinates. The existing path compute rasterizer preserves lines,
 * quadratics, cubics, analytic arcs, fill rules, and the 4x4/8x8 AA contract.
 */
typedef struct progpu_native_clip_path {
    size_t segment_offset;
    size_t segment_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    progpu_native_affine_2d transform;
    uint32_t fill_rule;
    uint32_t sample_grid;
    uint32_t operation;
    uint32_t reserved;
} progpu_native_clip_path;

/*
 * Immutable caller-owned clip payload borrowed only for one render call.
 * revision lives on the containing group-mask descriptor and is the retained
 * identity. Nodes are evaluated in order from an initially full mask.
 */
typedef struct progpu_native_clip_chain {
    uint32_t struct_size;
    uint32_t flags;
    const progpu_native_clip_path* paths;
    size_t path_count;
    const progpu_native_path_segment* segments;
    size_t segment_count;
} progpu_native_clip_chain;

/*
 * One immutable glyph outline references line, quadratic, or cubic records in
 * the shared segment arena. Bounds are in font design units. raster_scale maps
 * design units to physical atlas pixels; subpixel_x is the quarter-pixel
 * horizontal phase in [0, 0.75]. Shaping and line layout remain caller-owned.
 */
typedef struct progpu_native_glyph_outline {
    size_t segment_offset;
    size_t segment_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    float raster_scale;
    float subpixel_x;
} progpu_native_glyph_outline;

/*
 * A positioned glyph references one outline. position and bases are logical
 * coordinates after shaping. atlas_to_logical_scale preserves transform-aware
 * raster sizing; bold_offset and italic_skew match the production Text.wgsl
 * presentation contract. This baseline lane is solid grayscale text.
 */
typedef struct progpu_native_positioned_glyph {
    uint32_t outline_index;
    uint32_t reserved;
    progpu_native_point position;
    progpu_native_point basis_x;
    progpu_native_point basis_y;
    progpu_native_color color;
    float atlas_to_logical_scale;
    float bold_offset;
    float italic_skew;
    float reserved2;
} progpu_native_positioned_glyph;

/*
 * Optional mask applied once to the pooled frame-family result. Texture masks
 * borrow a same-device filterable texture view and map its red channel over
 * destination_rect. Rounded-rectangle masks use bounds and corner radii in
 * local coordinates plus a local-to-logical affine transform. The native
 * engine retains a texture view when it becomes the active sampled mask; the
 * producer keeps the underlying texture alive until replacement or engine
 * destruction. Vector clip chains borrow an ordered, versioned retained path
 * payload and are composed into a reusable target-sized R8 mask.
 */
typedef struct progpu_native_group_mask {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t reserved;
    uintptr_t external_view;
    uint32_t width;
    uint32_t height;
    uint32_t sampling;
    uint32_t texture_format;
    uint32_t revision;
    uint32_t reserved2;
    progpu_native_image_rect destination_rect;
    progpu_native_image_rect bounds;
    progpu_native_affine_2d transform;
    float corner_radii_x[4];
    float corner_radii_y[4];
    float opacity;
    uint32_t reserved3;
    const progpu_native_clip_chain* clip_chain;
} progpu_native_group_mask;

/*
 * One retained effect applied to the pooled frame-family result before its
 * final mask/opacity composite. The revision identifies immutable effect
 * parameters independently from group content. Gaussian sigma and drop-shadow
 * offset are expressed in logical coordinates and converted to physical pixels
 * with frame DPI. Drop-shadow color is straight-alpha linear RGBA.
 *
 * The original 32-byte Gaussian prefix remains accepted. Drop shadow requires
 * the full descriptor so older callers cannot accidentally select it without
 * supplying offset and color.
 */
typedef struct progpu_native_group_effect {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t revision;
    float sigma_x;
    float sigma_y;
    uint32_t reserved;
    uint32_t reserved2;
    float offset_x;
    float offset_y;
    float color_r;
    float color_g;
    float color_b;
    float color_a;
} progpu_native_group_effect;

/*
 * Pointer-free semantic effect-chain header. The resource payload contains
 * exactly this header and its auxiliary arena contains effect_count exact
 * progpu_native_group_effect records in caller order. The resource generation
 * and revision together identify the immutable chain without retaining a
 * caller pointer.
 */
typedef struct progpu_native_scene_effect_chain {
    uint32_t struct_size;
    uint32_t effect_count;
    uint32_t revision;
    uint32_t reserved;
} progpu_native_scene_effect_chain;

/*
 * A bounded linear retained effect chain. Effects are evaluated in array
 * order, so effects[1] consumes effects[0]'s output. The engine copies all
 * descriptors before returning and never retains caller memory. revision
 * identifies the complete immutable chain independently from group content.
 */
typedef struct progpu_native_group_effect_chain {
    uint32_t struct_size;
    uint32_t effect_count;
    uint32_t revision;
    uint32_t reserved;
    const progpu_native_group_effect* effects;
} progpu_native_group_effect_chain;

/*
 * Optional per-draw state shared by every frame family. opacity multiplies
 * primitive alpha. group_opacity composites the whole frame family through a
 * pooled transparent layer. A nonzero caller-owned group_revision permits the
 * layer pixels to be reused until the caller changes that revision. clip_rect
 * is expressed in logical target coordinates and is applied to the final
 * group composite rather than baked into retained layer pixels.
 *
 * struct_size keeps ABI-v3 append compatibility: the original 32-byte prefix
 * defaults group_opacity to one and group_revision to zero; the 40-byte
 * prefix has group state but no common mask; the 48/44-byte mask prefix has
 * no group effect. The 56/48-byte effect prefix has no effect chain. The
 * 64/52-byte effect-chain prefix defaults group_blend_mode to SrcOver.
 */
typedef struct progpu_native_draw_state {
    uint32_t struct_size;
    uint32_t flags;
    float opacity;
    uint32_t reserved;
    progpu_native_image_rect clip_rect;
    float group_opacity;
    uint32_t group_revision;
    const progpu_native_group_mask* group_mask;
    const progpu_native_group_effect* group_effect;
    const progpu_native_group_effect_chain* group_effect_chain;
    uint32_t group_blend_mode;
    uint32_t reserved2;
} progpu_native_draw_state;

typedef struct progpu_native_layer_metrics {
    uint32_t struct_size;
    uint32_t texture_width;
    uint32_t texture_height;
    uint32_t texture_generation;
    uint32_t allocation_count;
    uint32_t content_pass_count;
    uint32_t composite_pass_count;
    uint32_t cache_hit;
    uint64_t texture_bytes;
    uint64_t vertex_upload_bytes;
    uint64_t uniform_upload_bytes;
    uint32_t mask_kind;
    uint32_t mask_revision;
    uint32_t mask_bind_group_generation;
    uint32_t mask_bind_group_cache_hit;
    uint64_t mask_uniform_upload_bytes;
    uint32_t clip_path_count;
    uint32_t clip_rasterized_path_count;
    uint32_t clip_pass_count;
    uint32_t clip_cache_hit;
    uint64_t clip_path_upload_bytes;
    uint64_t clip_coverage_staging_bytes;
    uint64_t clip_texture_bytes;
    uint32_t effect_kind;
    uint32_t effect_revision;
    uint32_t effect_pass_count;
    uint32_t effect_cache_hit;
    uint64_t effect_uniform_upload_bytes;
    uint64_t effect_texture_bytes;
    uint32_t effect_count;
    uint32_t effect_chain_revision;
    uint32_t effect_texture_generation;
    uint32_t effect_allocation_count;
    uint32_t blend_mode;
    uint32_t blend_source_pass_count;
    uint32_t blend_pipeline_cache_hit;
    uint32_t blend_source_texture_generation;
    uint32_t blend_source_allocation_count;
    uint32_t reserved;
    uint64_t blend_source_texture_bytes;
} progpu_native_layer_metrics;

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
    const progpu_native_draw_state* draw_state;
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
    const progpu_native_draw_state* draw_state;
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
    /* Nonzero caller-owned content revision when retention is requested. */
    uint32_t reserved;
    const progpu_native_point* points;
    size_t point_count;
    const progpu_native_polyline* polylines;
    size_t polyline_count;
    const double* doubles;
    size_t double_count;
    const progpu_native_dash_style* dash_styles;
    size_t dash_style_count;
    const progpu_native_spline* splines;
    size_t spline_count;
    const progpu_native_draw_state* draw_state;
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

typedef struct progpu_native_path_frame {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    float dpi_scale;
    uintptr_t target_view;
    progpu_native_color clear_color;
    const progpu_native_path_fill* paths;
    size_t path_count;
    const progpu_native_path_segment* segments;
    size_t segment_count;
    uint32_t flags;
    /* Nonzero caller-owned content revision when retention is requested. */
    uint32_t content_revision;
    const progpu_native_draw_state* draw_state;
} progpu_native_path_frame;

typedef struct progpu_native_path_frame_metrics {
    uint32_t struct_size;
    uint32_t draw_call_count;
    uint32_t vertex_count;
    uint32_t index_count;
    uint32_t rasterized_path_count;
    uint32_t atlas_width;
    uint32_t atlas_height;
    uint32_t atlas_generation;
    uint64_t vertex_upload_bytes;
    uint64_t index_upload_bytes;
    uint64_t brush_upload_bytes;
    uint64_t path_upload_bytes;
    uint64_t coverage_staging_bytes;
    uint64_t uniform_upload_bytes;
    uint64_t submission_count;
    uint64_t payload_hash;
} progpu_native_path_frame_metrics;

typedef struct progpu_native_glyph_frame {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    float dpi_scale;
    uintptr_t target_view;
    progpu_native_color clear_color;
    const progpu_native_glyph_outline* outlines;
    size_t outline_count;
    const progpu_native_path_segment* segments;
    size_t segment_count;
    const progpu_native_positioned_glyph* glyphs;
    size_t glyph_count;
    uint32_t flags;
    uint32_t content_revision;
    const progpu_native_draw_state* draw_state;
} progpu_native_glyph_frame;

typedef struct progpu_native_glyph_frame_metrics {
    uint32_t struct_size;
    uint32_t draw_call_count;
    uint32_t glyph_count;
    uint32_t rasterized_glyph_count;
    uint32_t atlas_width;
    uint32_t atlas_height;
    uint32_t atlas_generation;
    uint32_t atlas_growth_count;
    uint64_t instance_upload_bytes;
    uint64_t outline_upload_bytes;
    uint64_t coverage_staging_bytes;
    uint64_t uniform_upload_bytes;
    uint64_t submission_count;
    uint64_t payload_hash;
} progpu_native_glyph_frame_metrics;

typedef struct progpu_native_image_frame {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    float dpi_scale;
    uintptr_t target_view;
    progpu_native_color clear_color;
    const uint8_t* rgba_pixels;
    size_t pixel_bytes;
    uint32_t image_width;
    uint32_t image_height;
    uint32_t row_bytes;
    uint32_t sampling;
    uint32_t image_revision;
    uint32_t content_revision;
    progpu_native_image_rect source_rect;
    progpu_native_image_rect destination_rect;
    progpu_native_affine_2d transform;
    float opacity;
    uint32_t reserved;
    /*
     * When PROGPU_NATIVE_IMAGE_SOURCE_EXTERNAL_VIEW is set, this is a
     * borrowed same-device WGPUTextureView. The engine retains the view until
     * it is replaced or destroyed. The caller must keep the underlying
     * texture alive and not destroy it during that interval.
     */
    uintptr_t external_source_view;
    uint32_t source_flags;
    uint32_t reserved2;
    /*
     * Optional borrowed same-device filterable mask view. Its red channel is
     * sampled over mask_destination_rect and multiplies source alpha. The
     * engine retains the view under the same lifetime rule as the source.
     */
    uintptr_t external_mask_view;
    uint32_t mask_width;
    uint32_t mask_height;
    progpu_native_image_rect mask_destination_rect;
    uint32_t mask_revision;
    uint32_t mask_sampling;
    const progpu_native_draw_state* draw_state;
} progpu_native_image_frame;

typedef struct progpu_native_image_frame_metrics {
    uint32_t struct_size;
    uint32_t draw_call_count;
    uint32_t vertex_count;
    uint32_t index_count;
    uint32_t texture_generation;
    uint32_t reserved;
    uint64_t vertex_upload_bytes;
    uint64_t index_upload_bytes;
    uint64_t texture_upload_bytes;
    uint64_t uniform_upload_bytes;
    uint64_t submission_count;
    uint64_t payload_hash;
} progpu_native_image_frame_metrics;

PROGPU_NATIVE_API uint32_t progpu_native_get_abi_version(void);
PROGPU_NATIVE_API uint8_t progpu_native_get_info(
    progpu_native_engine_info* info);
PROGPU_NATIVE_API progpu_native_status progpu_native_scene_validate(
    const void* stream,
    size_t stream_size,
    progpu_native_scene_metrics* metrics);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_create(
    const progpu_native_engine_options* options,
    progpu_native_engine** engine);
PROGPU_NATIVE_API void progpu_native_engine_destroy(
    progpu_native_engine* engine);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_update_scene(
    progpu_native_engine* engine,
    const void* stream,
    size_t stream_size,
    progpu_native_scene_metrics* metrics);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_render_scene(
    progpu_native_engine* engine,
    const progpu_native_scene_frame* frame,
    progpu_native_scene_frame_metrics* metrics);
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
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_render_paths(
    progpu_native_engine* engine,
    const progpu_native_path_frame* frame,
    progpu_native_path_frame_metrics* metrics);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_render_glyphs(
    progpu_native_engine* engine,
    const progpu_native_glyph_frame* frame,
    progpu_native_glyph_frame_metrics* metrics);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_render_image(
    progpu_native_engine* engine,
    const progpu_native_image_frame* frame,
    progpu_native_image_frame_metrics* metrics);
/*
 * Returns the backend submission index of the most recently submitted frame.
 * The zero value means that this engine has not submitted work yet.
 */
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_get_last_submission(
    progpu_native_engine* engine,
    uint64_t* submission_index);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_get_layer_metrics(
    progpu_native_engine* engine,
    progpu_native_layer_metrics* metrics);
/*
 * Polls or waits for one submission from this engine. This is the consumer
 * fence used by external-image owners before recycling a borrowed texture.
 * Calls are owner-thread affine and perform no allocation.
 */
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_poll_submission(
    progpu_native_engine* engine,
    uint64_t submission_index,
    uint8_t wait,
    uint8_t* complete);
PROGPU_NATIVE_API size_t progpu_native_engine_get_last_error(
    const progpu_native_engine* engine,
    char* destination,
    size_t destination_size);

#ifdef __cplusplus
}
#endif

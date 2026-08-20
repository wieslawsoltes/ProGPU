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
typedef struct progpu_native_text_context progpu_native_text_context;

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
#define PROGPU_NATIVE_CAPABILITY_DEVICE_LOSS_RECREATION (UINT64_C(1) << 34U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_GEOMETRY_BATCH (UINT64_C(1) << 35U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_POINT_BATCH (UINT64_C(1) << 36U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_VERTEX_MESH (UINT64_C(1) << 37U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_STROKE_BATCH (UINT64_C(1) << 38U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_LINE_3D_BATCH (UINT64_C(1) << 39U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_MESH_3D_BATCH (UINT64_C(1) << 40U)
#define PROGPU_NATIVE_CAPABILITY_BULK_TEXT_SHAPING (UINT64_C(1) << 41U)
#define PROGPU_NATIVE_CAPABILITY_BULK_TEXT_LAYOUT (UINT64_C(1) << 42U)
#define PROGPU_NATIVE_CAPABILITY_BULK_TEXT_LINE_BREAKING (UINT64_C(1) << 43U)
#define PROGPU_NATIVE_CAPABILITY_BULK_TEXT_BIDI (UINT64_C(1) << 44U)
#define PROGPU_NATIVE_CAPABILITY_BULK_TEXT_PARAGRAPH (UINT64_C(1) << 45U)
#define PROGPU_NATIVE_CAPABILITY_BULK_TEXT_VERTICAL_LAYOUT (UINT64_C(1) << 46U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_IMAGE_PATCH_BATCH (UINT64_C(1) << 47U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_IMAGE_MIPMAP_SAMPLING (UINT64_C(1) << 48U)
#define PROGPU_NATIVE_CAPABILITY_IMAGE_FRAME_MIPMAP_SAMPLING (UINT64_C(1) << 49U)
#define PROGPU_NATIVE_CAPABILITY_SEMANTIC_VECTOR_CLIP_MASK (UINT64_C(1) << 50U)

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
    PROGPU_NATIVE_SCENE_MAX_IMAGE_PATCHES = 64U * 1024U,
    PROGPU_NATIVE_SCENE_MAX_TEXT_STYLES = 1024U * 1024U,
    PROGPU_NATIVE_SCENE_NO_INDEX = 0xffffffffU,
    PROGPU_NATIVE_SCENE_RECORD_REQUIRED = 1U << 0U,
    PROGPU_NATIVE_SCENE_GLYPH_STYLED = 1U << 1U,
    PROGPU_NATIVE_SCENE_COLOR_GLYPH_BITMAPS = 1U << 2U,
    PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE = 1U << 3U,
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
    PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE = 9,
    PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH = 10,
    PROGPU_NATIVE_SCENE_RESOURCE_POINT_BATCH = 11,
    PROGPU_NATIVE_SCENE_RESOURCE_VERTEX_MESH = 12,
    PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH = 13,
    PROGPU_NATIVE_SCENE_RESOURCE_LINE_3D_BATCH = 14,
    PROGPU_NATIVE_SCENE_RESOURCE_MESH_3D_BATCH = 15
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
    PROGPU_NATIVE_SCENE_BRUSH_HATCH_PATTERN = 3,
    PROGPU_NATIVE_SCENE_BRUSH_CROSS_HATCH = 4,
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
    PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP = 2,
    PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN = 3,
    PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN = 4,
    PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH = 5,
    PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE = 6,
    PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY = 7
} progpu_native_scene_layer_mask_kind;

enum {
    PROGPU_NATIVE_SCENE_MAX_ANALYTIC_MASKS = 4U
};

typedef enum progpu_native_scene_command_kind {
    PROGPU_NATIVE_SCENE_COMMAND_SAVE = 1,
    PROGPU_NATIVE_SCENE_COMMAND_RESTORE = 2,
    PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER = 3,
    PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER = 4,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC = 16,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH = 17,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN = 18,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE = 19,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY = 20,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_POINT_BATCH = 21,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH = 22,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH = 23,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH = 24,
    PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH = 25
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
    PROGPU_NATIVE_SCENE_STATE_CLIP_RECT = 1U << 0U,
    PROGPU_NATIVE_SCENE_STATE_MASK = 1U << 1U
};

enum {
    PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX = 1U << 0U,
    PROGPU_NATIVE_SCENE_IMAGE_EFFECT = 1U << 1U,
    /* Snap each fully transformed destination corner to the target DPI grid. */
    PROGPU_NATIVE_SCENE_IMAGE_SNAP_TO_PIXELS = 1U << 2U,
    /* Source RGB channels are already multiplied by source alpha. */
    PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED = 1U << 3U,
    /* A bounded patch-batch suffix follows all other image suffixes. */
    PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH = 1U << 4U
};

enum {
    /* Apply Skia-compatible luma-to-alpha after the affine transform. */
    PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX_LUMINANCE_TO_ALPHA = 1U << 0U
};

enum {
    PROGPU_NATIVE_POINT_BATCH_EDGE_ALIASED = 1U << 0U,
    PROGPU_NATIVE_POINT_BATCH_ROUND = 1U << 1U,
    PROGPU_NATIVE_POINT_BATCH_HAIRLINE = 1U << 2U,
    PROGPU_NATIVE_POINT_BATCH_FIXED_DEVICE_RADIUS = 1U << 3U
};

typedef enum progpu_native_vertex_mesh_topology {
    PROGPU_NATIVE_VERTEX_MESH_TRIANGLES = 0,
    PROGPU_NATIVE_VERTEX_MESH_TRIANGLE_STRIP = 1,
    PROGPU_NATIVE_VERTEX_MESH_TRIANGLE_FAN = 2
} progpu_native_vertex_mesh_topology;

enum {
    PROGPU_NATIVE_VERTEX_MESH_EDGE_ALIASED = 1U << 0U
};

enum {
    PROGPU_NATIVE_SCENE_LAYER_BOUNDS = 1U << 0U,
    PROGPU_NATIVE_SCENE_LAYER_BACKDROP = 1U << 1U,
    PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION = 1U << 2U
};

typedef enum progpu_native_image_sampling {
    PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST = 0,
    PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR = 1,
    PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC = 2,
    PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR_MIPMAP = 3,
    PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_LINEAR_MIP_NEAREST = 4,
    PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_NEAREST_MIP_LINEAR = 5,
    PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_NEAREST_MIP_NEAREST = 6,
    PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_LINEAR_MIP_LINEAR = 7,
    PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_LINEAR_MIP_NEAREST = 8,
    PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_NEAREST_MIP_LINEAR = 9
} progpu_native_image_sampling;

typedef enum progpu_native_scene_image_patch_kind {
    PROGPU_NATIVE_SCENE_IMAGE_PATCH_TEXTURE = 0,
    PROGPU_NATIVE_SCENE_IMAGE_PATCH_FIXED_COLOR = 1,
    PROGPU_NATIVE_SCENE_IMAGE_PATCH_ATLAS_COLOR = 2
} progpu_native_scene_image_patch_kind;

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

typedef enum progpu_native_text_direction {
    PROGPU_NATIVE_TEXT_DIRECTION_UNSPECIFIED = 0,
    PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT = 1,
    PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT = 2,
    PROGPU_NATIVE_TEXT_DIRECTION_TOP_TO_BOTTOM = 3,
    PROGPU_NATIVE_TEXT_DIRECTION_BOTTOM_TO_TOP = 4
} progpu_native_text_direction;

typedef enum progpu_native_text_cluster_level {
    PROGPU_NATIVE_TEXT_CLUSTER_MONOTONE_GRAPHEMES = 0,
    PROGPU_NATIVE_TEXT_CLUSTER_MONOTONE_CHARACTERS = 1,
    PROGPU_NATIVE_TEXT_CLUSTER_CHARACTERS = 2,
    PROGPU_NATIVE_TEXT_CLUSTER_GRAPHEMES = 3
} progpu_native_text_cluster_level;

enum {
    PROGPU_NATIVE_TEXT_BUFFER_BEGINNING_OF_TEXT = 1U << 0U,
    PROGPU_NATIVE_TEXT_BUFFER_END_OF_TEXT = 1U << 1U,
    PROGPU_NATIVE_TEXT_BUFFER_PRESERVE_DEFAULT_IGNORABLES = 1U << 2U,
    PROGPU_NATIVE_TEXT_BUFFER_REMOVE_DEFAULT_IGNORABLES = 1U << 3U,
    PROGPU_NATIVE_TEXT_BUFFER_DO_NOT_INSERT_DOTTED_CIRCLE = 1U << 4U,
    PROGPU_NATIVE_TEXT_BUFFER_VERIFY = 1U << 5U,
    PROGPU_NATIVE_TEXT_BUFFER_PRODUCE_UNSAFE_TO_CONCAT = 1U << 6U,
    PROGPU_NATIVE_TEXT_BUFFER_PRODUCE_SAFE_TO_INSERT_TATWEEL = 1U << 7U
};

enum {
    PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES = 1U << 0U
};

/* PROGPU_CSHARP_STRUCT: Public.NativeTextScalar */
typedef struct progpu_native_text_scalar {
    uint32_t code_point;
    uint32_t input_index;
    uint16_t input_length;
    uint8_t canonical_combining_class;
    uint8_t reserved;
    uint32_t script;
} progpu_native_text_scalar;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextFeature */
typedef struct progpu_native_text_feature {
    uint32_t tag;
    uint32_t value;
    uint32_t start;
    uint32_t end;
} progpu_native_text_feature;

/* Design-unit glyph metrics in the managed public Y-down convention. */
/* PROGPU_CSHARP_STRUCT: Public.NativeTextShapingGlyph */
typedef struct progpu_native_text_shaping_glyph {
    uint32_t glyph_id;
    uint32_t code_point;
    int32_t cluster;
    uint32_t flags;
    int32_t advance_x;
    int32_t advance_y;
    int32_t offset_x;
    int32_t offset_y;
} progpu_native_text_shaping_glyph;

/*
 * One synchronous, allocation-free shaping request. Every pointer is borrowed
 * only for the duration of get-requirements or shape and may be null only when
 * its paired count is zero. Tags use big-endian OpenType byte order. Input and
 * context records preserve UTF input ranges while native code recomputes their
 * Unicode properties from code_point before use.
 */
/* PROGPU_CSHARP_STRUCT: Public.NativeTextShapeRequest */
typedef struct progpu_native_text_shape_request {
    uint32_t struct_size;
    uint32_t abi_version;
    /* PROGPU_CSHARP_TYPE: nuint */
    const uint8_t* font_data;
    size_t font_size;
    uint32_t face_index;
    uint32_t flags;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_scalar* input;
    uint32_t input_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_scalar* pre_context;
    uint32_t pre_context_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_scalar* post_context;
    uint32_t post_context_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_feature* features;
    uint32_t feature_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const int16_t* normalized_coordinates;
    uint32_t normalized_coordinate_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const uint8_t* normalization_data;
    size_t normalization_data_size;
    uint32_t unicode_script;
    uint32_t language;
    uint32_t direction;
    uint32_t cluster_level;
    uint32_t buffer_flags;
    uint32_t alternate_value;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_text_shape_request;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextShapeRequirements */
typedef struct progpu_native_text_shape_requirements {
    uint32_t struct_size;
    uint32_t glyph_capacity;
    uint32_t scratch_alignment;
    uint32_t error_code;
    uint64_t scratch_bytes;
} progpu_native_text_shape_requirements;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextShapeResult */
typedef struct progpu_native_text_shape_result {
    uint32_t struct_size;
    uint32_t glyph_count;
    uint32_t error_code;
    uint32_t reserved;
    uint64_t scratch_bytes_used;
} progpu_native_text_shape_result;

typedef enum progpu_native_text_trimming {
    PROGPU_NATIVE_TEXT_TRIMMING_NONE = 0,
    PROGPU_NATIVE_TEXT_TRIMMING_CHARACTER_ELLIPSIS = 1,
    PROGPU_NATIVE_TEXT_TRIMMING_WORD_ELLIPSIS = 2
} progpu_native_text_trimming;

typedef enum progpu_native_text_alignment {
    PROGPU_NATIVE_TEXT_ALIGNMENT_LEFT = 0,
    PROGPU_NATIVE_TEXT_ALIGNMENT_CENTER = 1,
    PROGPU_NATIVE_TEXT_ALIGNMENT_RIGHT = 2,
    PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY = 3
} progpu_native_text_alignment;

typedef enum progpu_native_text_line_break_kind {
    PROGPU_NATIVE_TEXT_LINE_BREAK_PROHIBITED = 0,
    PROGPU_NATIVE_TEXT_LINE_BREAK_OPPORTUNITY = 1,
    PROGPU_NATIVE_TEXT_LINE_BREAK_MANDATORY = 2
} progpu_native_text_line_break_kind;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextLayoutRequest */
typedef struct progpu_native_text_layout_request {
    uint32_t struct_size;
    uint32_t abi_version;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_shaping_glyph* glyphs;
    uint32_t glyph_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const uint8_t* breaks_after;
    uint32_t break_count;
    float scale;
    float maximum_width;
    float line_height;
    uint32_t maximum_lines;
    uint32_t direction;
    uint32_t trimming;
    uint32_t alignment;
    uint32_t ellipsis_glyph_id;
    float ellipsis_advance;
    uint32_t reserved;
} progpu_native_text_layout_request;

/* PROGPU_CSHARP_STRUCT: Public.NativePositionedTextGlyph */
typedef struct progpu_native_positioned_text_glyph {
    uint32_t glyph_index;
    uint32_t glyph_id;
    uint32_t font_index;
    int32_t cluster;
    float x;
    float y;
    float advance_x;
    float advance_y;
} progpu_native_positioned_text_glyph;

/* PROGPU_CSHARP_STRUCT: Public.NativePositionedTextLine */
typedef struct progpu_native_positioned_text_line {
    uint32_t glyph_start;
    uint32_t glyph_count;
    int32_t input_start;
    int32_t input_end;
    float width;
    float baseline_y;
    float height;
    uint8_t clipped;
    uint8_t reserved0;
    uint8_t reserved1;
    uint8_t reserved2;
} progpu_native_positioned_text_line;

/* PROGPU_CSHARP_STRUCT: Public.NativePositionedTextColumn */
typedef struct progpu_native_positioned_text_column {
    uint32_t glyph_start;
    uint32_t glyph_count;
    int32_t input_start;
    int32_t input_end;
    float height;
    float x;
    float width;
    uint8_t clipped;
    uint8_t reserved0;
    uint8_t reserved1;
    uint8_t reserved2;
} progpu_native_positioned_text_column;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextLayoutRequirements */
typedef struct progpu_native_text_layout_requirements {
    uint32_t struct_size;
    uint32_t glyph_capacity;
    uint32_t line_capacity;
    uint32_t scratch_alignment;
    uint32_t error_code;
    uint32_t reserved;
    uint64_t scratch_bytes;
} progpu_native_text_layout_requirements;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextLayoutResult */
typedef struct progpu_native_text_layout_result {
    uint32_t struct_size;
    uint32_t glyph_count;
    uint32_t line_count;
    uint32_t error_code;
    float content_width;
    float content_height;
    float measured_width;
    float measured_height;
    uint64_t scratch_bytes_used;
} progpu_native_text_layout_result;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextVerticalLayoutRequirements */
typedef struct progpu_native_text_vertical_layout_requirements {
    uint32_t struct_size;
    uint32_t glyph_capacity;
    uint32_t column_capacity;
    uint32_t scratch_alignment;
    uint32_t error_code;
    uint32_t reserved;
    uint64_t scratch_bytes;
} progpu_native_text_vertical_layout_requirements;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextVerticalLayoutResult */
typedef struct progpu_native_text_vertical_layout_result {
    uint32_t struct_size;
    uint32_t glyph_count;
    uint32_t column_count;
    uint32_t error_code;
    float content_width;
    float content_height;
    float measured_width;
    float measured_height;
    uint64_t scratch_bytes_used;
} progpu_native_text_vertical_layout_result;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextLineBreakRequirements */
typedef struct progpu_native_text_line_break_requirements {
    uint32_t struct_size;
    uint32_t break_capacity;
    uint32_t scratch_alignment;
    uint32_t error_code;
    uint64_t scratch_bytes;
} progpu_native_text_line_break_requirements;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextLineBreakResult */
typedef struct progpu_native_text_line_break_result {
    uint32_t struct_size;
    uint32_t break_count;
    uint32_t error_code;
    uint32_t reserved;
    uint64_t scratch_bytes_used;
} progpu_native_text_line_break_result;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextBidiLevel */
typedef struct progpu_native_text_bidi_level {
    uint32_t input_index;
    uint16_t input_length;
    int8_t level;
    uint8_t reserved;
} progpu_native_text_bidi_level;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextBidiRequirements */
typedef struct progpu_native_text_bidi_requirements {
    uint32_t struct_size;
    uint32_t level_capacity;
    uint32_t scratch_alignment;
    uint32_t error_code;
    uint64_t scratch_bytes;
} progpu_native_text_bidi_requirements;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextBidiResult */
typedef struct progpu_native_text_bidi_result {
    uint32_t struct_size;
    uint32_t level_count;
    int32_t paragraph_level;
    uint32_t error_code;
    uint64_t scratch_bytes_used;
} progpu_native_text_bidi_result;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextLayoutOptions */
typedef struct progpu_native_text_layout_options {
    uint32_t struct_size;
    float scale;
    float maximum_width;
    float line_height;
    uint32_t maximum_lines;
    uint32_t direction;
    uint32_t trimming;
    uint32_t alignment;
    uint32_t ellipsis_glyph_id;
    float ellipsis_advance;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_text_layout_options;

typedef enum progpu_native_text_paragraph_stage {
    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_NONE = 0,
    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_BIDI = 1,
    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LINE_BREAK = 2,
    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING = 3,
    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_CLUSTER_MAP = 4,
    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT = 5
} progpu_native_text_paragraph_stage;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextParagraphRequirements */
typedef struct progpu_native_text_paragraph_requirements {
    uint32_t struct_size;
    uint32_t glyph_capacity;
    uint32_t line_capacity;
    uint32_t scratch_alignment;
    uint32_t error_code;
    uint32_t error_stage;
    uint64_t scratch_bytes;
} progpu_native_text_paragraph_requirements;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextParagraphResult */
typedef struct progpu_native_text_paragraph_result {
    uint32_t struct_size;
    uint32_t glyph_count;
    uint32_t line_count;
    uint32_t shaped_glyph_count;
    int32_t paragraph_level;
    uint32_t error_code;
    uint32_t error_stage;
    uint32_t shaping_run_count;
    uint32_t cached_plan_count;
    uint32_t plan_build_count;
    float content_width;
    float content_height;
    float measured_width;
    float measured_height;
    uint64_t scratch_bytes_used;
} progpu_native_text_paragraph_result;

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
/* PROGPU_CSHARP_STRUCT: NativeMethods.SceneHeader */
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

/* PROGPU_CSHARP_STRUCT: NativeMethods.SceneResource */
typedef struct progpu_native_scene_resource {
    uint32_t struct_size;
    /* PROGPU_CSHARP_TYPE: NativeSceneResourceKind */
    uint32_t kind;
    /* PROGPU_CSHARP_TYPE: NativeSceneRecordFlags */
    uint32_t flags;
    uint32_t reserved;
    uint64_t resource_id;
    uint64_t generation;
    uint32_t payload_offset;
    uint32_t payload_size;
    uint32_t auxiliary_offset;
    uint32_t auxiliary_size;
} progpu_native_scene_resource;

/* PROGPU_CSHARP_STRUCT: NativeMethods.SceneCommand */
typedef struct progpu_native_scene_command {
    uint32_t struct_size;
    /* PROGPU_CSHARP_TYPE: NativeSceneCommandKind */
    uint32_t kind;
    /* PROGPU_CSHARP_TYPE: NativeSceneRecordFlags */
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

/* PROGPU_CSHARP_STRUCT: NativeMethods.SceneMetrics */
typedef struct progpu_native_scene_metrics {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t command_count;
    uint32_t resource_count;
    uint32_t draw_count;
    uint32_t maximum_stack_depth;
    /* PROGPU_CSHARP_TYPE: NativeSceneValidationError */
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
/* PROGPU_CSHARP_STRUCT: NativeMethods.EngineOptions */
typedef struct progpu_native_engine_options {
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t backend_abi;
    /* PROGPU_CSHARP_TYPE: NativeRendererTextureFormat */
    uint32_t target_format;
    uintptr_t device;
    uintptr_t queue;
    uint64_t flags;
} progpu_native_engine_options;

/*
 * Same-device image views are bound outside the immutable pointer-free scene
 * stream. Flags carries progpu_native_scene_external_image_role. The engine
 * retains each view until the complete table is replaced.
 */
/* PROGPU_CSHARP_STRUCT: NativeMethods.SceneExternalImageBinding */
typedef struct progpu_native_scene_external_image_binding {
    uint32_t struct_size;
    uint32_t flags;
    uint64_t resource_id;
    uint64_t generation;
    uintptr_t texture_view;
    uint32_t width;
    uint32_t height;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_scene_external_image_binding;

typedef enum progpu_native_scene_external_image_role {
    PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE_PRIMARY = 0,
    PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE_CHROMA = 1,
    PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE_MASK = 2
} progpu_native_scene_external_image_role;

/* PROGPU_CSHARP_STRUCT: NativeMethods.NativeColor */
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
    PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER = 4,
    /*
     * One periodic dot-grid quad. p0 is the local bounds origin, p1 is the
     * bounds extent, p2 is phase, and p3 is {spacing, radius}. The shared
     * vector shader performs constant bounded work per covered fragment.
     */
    PROGPU_NATIVE_GEOMETRY_DOT_GRID = 5,
    /*
     * Exact retained elliptical-arc stroke. p0 is the local center, p1/p2 are
     * the already-resolved local ellipse axes, and p3 is
     * {theta1, delta-theta}. The ordinary conformal lane evaluates one
     * analytic GPU quad;
     * non-conformal local outlines are expanded once when the scene changes.
     */
    PROGPU_NATIVE_GEOMETRY_ARC = 6,
    /*
     * Connected-path adornments. PATH_CAP stores center/direction in p0/p1,
     * p2.x is one for a start cap, and the cap kind uses START_CAP_MASK.
     * PATH_JOIN stores point/incoming/outgoing in p0/p1/p2, p3.x is the miter
     * limit, and the join kind uses START_CAP_MASK (the enum values coincide).
     */
    PROGPU_NATIVE_GEOMETRY_PATH_CAP = 7,
    PROGPU_NATIVE_GEOMETRY_PATH_JOIN = 8
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
 * PROGPU_NATIVE_SCENE_STATE_CLIP_RECT. When
 * PROGPU_NATIVE_SCENE_STATE_MASK is set, mask_resource_index references a
 * preceding LAYER_MASK resource and coverage is applied independently to each
 * draw. The index must be zero when the flag is absent. Reserved fields must
 * remain zero.
 */
typedef struct progpu_native_scene_state {
    uint32_t struct_size;
    uint32_t flags;
    progpu_native_affine_2d transform;
    float opacity;
    uint32_t reserved;
    progpu_native_image_rect clip_rect;
    uint32_t mask_resource_index;
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
 * Pointer-free fixed-capacity intersection of two to four analytic masks.
 * Each active entry is a canonical rounded-rectangle record; unused trailing
 * entries are all-zero. The bounded representation keeps validation, upload,
 * shader evaluation, and replay storage O(1) without an auxiliary allocation.
 */
typedef struct progpu_native_scene_layer_mask_chain {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t mask_count;
    progpu_native_scene_layer_mask masks[PROGPU_NATIVE_SCENE_MAX_ANALYTIC_MASKS];
} progpu_native_scene_layer_mask_chain;

/*
 * Pointer-free retained vector-mask prefix. The auxiliary span contains
 * path_count progpu_native_scene_clip_path records followed immediately by
 * segment_count progpu_native_path_segment records and boolean_node_count
 * progpu_native_scene_path_boolean_node records. All counts and referenced
 * ranges are bounded and validated before native GPU allocation. Ordered clip
 * paths are composed from an initially full mask. A path with a boolean
 * program evaluates that canonical postfix program inside the shared
 * PathRasterizer.wgsl pass before ClipCompose.wgsl applies the outer path.
 */
typedef struct progpu_native_scene_layer_vector_mask {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t path_count;
    uint32_t segment_count;
    float opacity;
    uint32_t boolean_node_count;
    uint32_t reserved1;
} progpu_native_scene_layer_vector_mask;

/*
 * Version-one upload-backed or external image command payload. Sampling values
 * cover the complete ProGPU.Scene.TextureSamplingMode contract. max_anisotropy is canonical
 * one except for LINEAR_MIPMAP, where values two through sixteen select the
 * matching WebGPU anisotropic sampler. Legacy zero is accepted as one.
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
    uint32_t max_anisotropy;
} progpu_native_scene_image_draw;

/*
 * Optional bounded patch suffix selected by
 * PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH. The header is followed by exactly
 * patch_count fixed-width records. Each record is compiled to one quad while
 * the complete suffix remains one retained image command and one GPU draw.
 */
typedef struct progpu_native_scene_image_patch_batch {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t patch_count;
    uint32_t reserved;
} progpu_native_scene_image_patch_batch;

typedef struct progpu_native_scene_image_patch {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t color_blend_mode;
    uint32_t flags;
    progpu_native_image_rect source_rect;
    progpu_native_image_rect destination_rect;
    progpu_native_affine_2d transform;
    float color[4];
} progpu_native_scene_image_patch;

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
 * Rows and offset form a 4x5 affine transform over straight RGBA. The optional
 * luminance flag converts the transformed straight RGB to transparent black
 * with luminance times source alpha in alpha. The two reserved words preserve
 * 16-byte GPU-uniform alignment.
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
 * Optional exact suffix selected by PROGPU_NATIVE_SCENE_IMAGE_EFFECT. The
 * first 288 bytes match the production ImageEffect.wgsl uniform layout; the
 * trailing 16-byte contract footer keeps validation metadata out of the GPU
 * hot path. This additive version supports one RGB source, fused color
 * operations, luminance-to-alpha, spherical projection, role-keyed planar YUV
 * and texture-mask resources, and shared filterable or Tier-1 unfilterable
 * Gaussian prepasses.
 */
typedef enum progpu_native_scene_image_effect_flags {
    PROGPU_NATIVE_SCENE_IMAGE_EFFECT_UNFILTERABLE_PLANAR = 1U << 0
} progpu_native_scene_image_effect_flags;

typedef struct progpu_native_scene_image_effect {
    float color_matrix_red[4];
    float color_matrix_green[4];
    float color_matrix_blue[4];
    float color_matrix_alpha[4];
    float color_matrix_offset[4];
    float effects0[4];
    float effects1[4];
    float texture0[4];
    float flags0[4];
    float yuv_range[4];
    float yuv_red[4];
    float yuv_green[4];
    float yuv_blue[4];
    float spherical0[4];
    float spherical_uv_rect[4];
    float spherical_rotation0[4];
    float spherical_rotation1[4];
    float spherical_rotation2[4];
    uint32_t struct_size;
    uint32_t flags;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_scene_image_effect;

/*
 * Exact pointer-free storage layout consumed by production Vector.wgsl.
 * A brush-table resource stores one or more of these records in payload and
 * its optional auxiliary span stores exact gradient-stop records. StopOffset
 * is local to that resource. The semantic compiler packs referenced brushes
 * and stops into one retained scene-wide GPU page and rewrites indices once.
 *
 * The native semantic lane accepts solid, linear, radial, hatch, cross-hatch,
 * two-point conical, sweep, and Perlin-noise brushes. Perlin overloads StartPoint/EndPoint/Center
 * as base frequency, stitch period, and tile size; Radius is the normalized
 * seed, StopCount is the bounded octave count, and SpreadMethod 0/1 selects
 * fractal/turbulence noise. Interpolation 0 selects the bounded hash fallback;
 * interpolation 1 references exactly 512 packed permutation/gradient records
 * at StopOffset. Hatch kinds use Radius as angle and Center as spacing/thickness.
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
 * Pointer-free retained brush opacity mask. Bounds are mask-local logical
 * coordinates and transform maps them into logical target coordinates. The
 * embedded 256-byte brush is the exact canonical Vector.wgsl material record;
 * StopOffset is resource-local and must be zero. The auxiliary span contains
 * exactly gradient_stop_count progpu_native_scene_gradient_stop records.
 * The executor rasterizes the rectangle into an R8 texture entirely on the
 * GPU through the shared fs_mask_unmasked shader and retains that texture for
 * stable replay. All flags and reserved fields remain zero.
 */
typedef struct progpu_native_scene_layer_brush_mask {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t gradient_stop_count;
    progpu_native_image_rect bounds;
    progpu_native_affine_2d transform;
    float opacity;
    uint32_t reserved0;
    progpu_native_scene_brush brush;
} progpu_native_scene_layer_brush_mask;

/*
 * Pointer-free retained stroked-geometry opacity mask. PrimitiveOffset and
 * PrimitiveCount address progpu_native_geometry_primitive records in the
 * owning resource auxiliary arena. Bounds/transform retain the caller mask
 * storage boundary; the embedded canonical brush and its shared stop range
 * preserve the exact pen material. The executor expands the proven geometry
 * primitives and rasterizes their alpha through production Vector.wgsl.
 */
typedef struct progpu_native_scene_layer_geometry_mask {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t primitive_offset;
    uint32_t primitive_count;
    uint32_t gradient_stop_count;
    uint32_t reserved0;
    uint32_t reserved1;
    progpu_native_image_rect bounds;
    progpu_native_affine_2d transform;
    float opacity;
    uint32_t reserved2;
    progpu_native_scene_brush brush;
} progpu_native_scene_layer_geometry_mask;

/*
 * Pointer-free retained intersection of arbitrary GPU-generated masks. The
 * auxiliary span contains brush_mask_count brush-mask records, followed by
 * geometry_mask_count geometry-mask records, geometry_primitive_count
 * geometry primitives, path_count clip paths, segment_count path segments,
 * boolean_node_count postfix nodes, and gradient_stop_count shared gradient
 * records. A non-empty vector range contributes one component; each brush or
 * geometry record contributes one component. Primitive and stop offsets
 * address their shared resource-local spans.
 * The executor evaluates every component on the GPU and multiplies their R8
 * coverage through the canonical ClipCompose.wgsl program. All fields remain
 * fixed-width on wasm32 and native hosts.
 */
typedef struct progpu_native_scene_layer_composite_mask {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t component_count;
    uint32_t brush_mask_count;
    uint32_t path_count;
    uint32_t segment_count;
    uint32_t boolean_node_count;
    uint32_t gradient_stop_count;
    float opacity;
    uint32_t geometry_mask_count;
    uint32_t geometry_primitive_count;
} progpu_native_scene_layer_composite_mask;

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
 * records zero-copy; wasm32 translates the fixed prefix while preserving the
 * same version-one byte stream. Path segment ranges are resource-local and
 * may share or overlap an earlier densely covered range, allowing repeated
 * transformed instances to retain one immutable outline. Boolean-program
 * ranges remain canonical, contiguous, and independently bounded.
 */
typedef struct progpu_native_scene_path_fill {
    uint64_t segment_offset;
    uint64_t segment_count;
    /* Optional resource-local canonical postfix program; zero count means a
       conventional single-path fill and requires a zero offset. */
    uint64_t boolean_node_offset;
    uint64_t boolean_node_count;
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

/* PROGPU_CSHARP_STRUCT: NativeMethods.SceneFrame */
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

/* PROGPU_CSHARP_STRUCT: NativeMethods.SceneFrameMetrics */
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
 * device-stroke flags are mutually exclusive and apply only to stroked lines,
 * curves, arcs, caps, and joins. The start/end cap fields are packed into
 * their documented flag masks and are ignored by filled records.
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
 * Compact pointer-free retained point-batch record. PointOffset/PointCount
 * address the owning POINT_BATCH resource's auxiliary progpu_native_point
 * array. A changed scene expands each point to one vector quad in O(N) time;
 * stable replay retains the packed GPU page and uploads nothing. Radius is
 * local for ordinary points and exactly 0.5 for device-space hairlines.
 */
typedef struct progpu_native_scene_point_batch {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t point_offset;
    uint32_t point_count;
    float radius;
    float reserved;
    progpu_native_color color;
    progpu_native_affine_2d transform;
} progpu_native_scene_point_batch;

/*
 * Compact pointer-free retained vertex-mesh record. VertexOffset/VertexCount
 * address a tightly packed progpu_native_scene_mesh_vertex prefix in the
 * owning resource auxiliary arena. IndexOffset/IndexCount address the uint16
 * suffix that follows the complete vertex prefix. Changed scenes expand the
 * selected topology into the shared vector page; stable replay uploads zero.
 */
typedef struct progpu_native_scene_vertex_mesh {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t topology;
    uint32_t color_blend_mode;
    uint32_t vertex_offset;
    uint32_t vertex_count;
    uint32_t index_offset;
    uint32_t index_count;
    progpu_native_affine_2d transform;
    uint32_t reserved[2];
} progpu_native_scene_vertex_mesh;

typedef struct progpu_native_scene_mesh_vertex {
    progpu_native_point position;
    progpu_native_point texture_coordinate;
    progpu_native_color color;
} progpu_native_scene_mesh_vertex;

/* PROGPU_CSHARP_STRUCT: Public.NativePoint3D */
typedef struct progpu_native_point_3d {
    float x;
    float y;
    float z;
    float reserved;
} progpu_native_point_3d;

/* PROGPU_CSHARP_STRUCT: Public.NativeFloat4 */
typedef struct progpu_native_float_4 {
    float x;
    float y;
    float z;
    float w;
} progpu_native_float_4;

/* Row-major System.Numerics-compatible matrix storage. Shared WGSL reads the
 * uploaded columns after the backend's existing matrix upload conversion. */
/* PROGPU_CSHARP_STRUCT: Public.NativeMatrix4x4 */
typedef struct progpu_native_matrix_4x4 {
    float m11;
    float m12;
    float m13;
    float m14;
    float m21;
    float m22;
    float m23;
    float m24;
    float m31;
    float m32;
    float m33;
    float m34;
    float m41;
    float m42;
    float m43;
    float m44;
} progpu_native_matrix_4x4;

/* One immutable camera payload shared by line/ACIS and mesh commands. */
/* PROGPU_CSHARP_STRUCT: Public.NativeSceneCamera3D */
typedef struct progpu_native_scene_camera_3d {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t reserved0;
    uint32_t reserved1;
    progpu_native_matrix_4x4 projection;
    progpu_native_matrix_4x4 view;
    progpu_native_point_3d camera_position;
} progpu_native_scene_camera_3d;

/* A line/ACIS edge remains in local 3D coordinates. The full affine/projective
 * transform and camera are evaluated by WebGPU; compilation never projects or
 * expands the edge on the CPU. Thickness is in physical framebuffer pixels. */
/* PROGPU_CSHARP_STRUCT: Public.NativeSceneLine3D */
typedef struct progpu_native_scene_line_3d {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t reserved0;
    uint32_t reserved1;
    progpu_native_point_3d start;
    progpu_native_point_3d end;
    /* PROGPU_CSHARP_TYPE: Vector4 */ progpu_native_color color;
    float thickness;
    float opacity;
    uint32_t reserved2;
    uint32_t reserved3;
    progpu_native_matrix_4x4 transform;
} progpu_native_scene_line_3d;

typedef enum progpu_native_mesh_3d_topology {
    PROGPU_NATIVE_MESH_3D_TRIANGLES = 0,
    PROGPU_NATIVE_MESH_3D_TRIANGLE_STRIP = 1
} progpu_native_mesh_3d_topology;

typedef enum progpu_native_mesh_3d_render_mode {
    PROGPU_NATIVE_MESH_3D_SOLID = 0,
    PROGPU_NATIVE_MESH_3D_WIREFRAME = 1,
    PROGPU_NATIVE_MESH_3D_SOLID_WIREFRAME = 2
} progpu_native_mesh_3d_render_mode;

/* PROGPU_CSHARP_STRUCT: Public.NativeSceneMesh3DVertex */
typedef struct progpu_native_scene_mesh_3d_vertex {
    progpu_native_point_3d position;
    progpu_native_point_3d normal;
    progpu_native_point texture_coordinate;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_scene_mesh_3d_vertex;

/* Retained mesh ranges address the owning resource auxiliary arena: all
 * vertices form its prefix and all uint32 indices form its suffix. Material
 * fields mirror the proven managed GpuMesh3DRecord baseline without texture
 * handles; texture leases remain an explicit follow-up resource family. */
/* PROGPU_CSHARP_STRUCT: Public.NativeSceneMesh3D */
typedef struct progpu_native_scene_mesh_3d {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t topology;
    uint32_t render_mode;
    uint32_t vertex_offset;
    uint32_t vertex_count;
    uint32_t index_offset;
    uint32_t index_count;
    progpu_native_matrix_4x4 model_transform;
    progpu_native_matrix_4x4 normal_transform;
    /* PROGPU_CSHARP_TYPE: Vector4 */ progpu_native_color color;
    progpu_native_float_4 light_direction;
    progpu_native_float_4 ambient_color;
    progpu_native_float_4 specular_color;
    progpu_native_float_4 material_ambient;
    float opacity;
    uint32_t shading_mode;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_scene_mesh_3d;

typedef enum progpu_native_scene_stroke_kind {
    PROGPU_NATIVE_SCENE_STROKE_POLYLINE = 0,
    PROGPU_NATIVE_SCENE_STROKE_SPLINE = 1
} progpu_native_scene_stroke_kind;

/*
 * Pointer-free retained connected-stroke descriptor. Points address the
 * resource auxiliary point prefix. Knots, optional rational weights, and dash
 * intervals address its contiguous double suffix in that canonical order.
 */
typedef struct progpu_native_scene_stroke {
    uint32_t struct_size;
    uint32_t kind;
    uint32_t flags;
    uint32_t degree;
    uint64_t point_offset;
    uint64_t point_count;
    uint64_t knot_offset;
    uint64_t knot_count;
    uint64_t weight_offset;
    uint64_t weight_count;
    uint64_t dash_interval_offset;
    uint64_t dash_interval_count;
    progpu_native_color color;
    progpu_native_affine_2d transform;
    float stroke_thickness;
    float miter_limit;
    double dash_offset;
    uint32_t start_cap;
    uint32_t end_cap;
    uint32_t line_join;
    uint32_t dash_cap;
    uint32_t reserved[2];
} progpu_native_scene_stroke;

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
    /* Optional frame-local canonical postfix program; zero count means a
       conventional single-path fill and requires a zero offset. */
    size_t boolean_node_offset;
    size_t boolean_node_count;
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
 * One ordered clip node borrows a contiguous segment range and an optional
 * canonical postfix boolean program. Bounds are exact local curve-extrema
 * bounds and transform maps local coordinates to logical target coordinates.
 * The existing path compute rasterizer preserves lines, quadratics, cubics,
 * analytic arcs, fill rules, boolean operations, and the 4x4/8x8 AA contract.
 */
typedef struct progpu_native_clip_path {
    size_t segment_offset;
    size_t segment_count;
    size_t boolean_node_offset;
    size_t boolean_node_count;
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
 * Pointer-free semantic-scene equivalent of progpu_native_clip_path. Fixed
 * 64-bit arena indices keep the retained byte stream identical on 32-bit and
 * 64-bit hosts; the C++ consumer translates them once when compiling GPU
 * coverage resources.
 */
typedef struct progpu_native_scene_clip_path {
    uint64_t segment_offset;
    uint64_t segment_count;
    uint64_t boolean_node_offset;
    uint64_t boolean_node_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    progpu_native_affine_2d transform;
    uint32_t fill_rule;
    uint32_t sample_grid;
    uint32_t operation;
    uint32_t reserved;
} progpu_native_scene_clip_path;

/*
 * One postfix/RPN boolean-program instruction. Leaf records borrow a
 * contiguous segment range and carry exact local bounds/fill state. Empty and
 * operation records require every range, bound, fill, and reserved field to be
 * zero. Programs are bounded to 63 instructions and a 16-mask stack, matching
 * the canonical PathRasterizer.wgsl contract.
 */
typedef enum progpu_native_path_boolean_node_kind {
    PROGPU_NATIVE_PATH_BOOLEAN_LEAF = 0,
    PROGPU_NATIVE_PATH_BOOLEAN_EMPTY = 1,
    PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE = 2,
    PROGPU_NATIVE_PATH_BOOLEAN_INTERSECT = 3,
    PROGPU_NATIVE_PATH_BOOLEAN_UNION = 4,
    PROGPU_NATIVE_PATH_BOOLEAN_XOR = 5,
    PROGPU_NATIVE_PATH_BOOLEAN_REVERSE_DIFFERENCE = 6
} progpu_native_path_boolean_node_kind;

typedef struct progpu_native_path_boolean_node {
    size_t segment_offset;
    size_t segment_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    uint32_t fill_rule;
    uint32_t kind;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_path_boolean_node;

typedef struct progpu_native_scene_path_boolean_node {
    uint64_t segment_offset;
    uint64_t segment_count;
    float min_x;
    float min_y;
    float max_x;
    float max_y;
    uint32_t fill_rule;
    uint32_t kind;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_scene_path_boolean_node;

/*
 * Immutable caller-owned clip payload borrowed only for one render call.
 * revision lives on the containing group-mask descriptor and is the retained
 * identity. Paths are evaluated in order from an initially full mask. The
 * optional boolean-node arena is also immutable and caller-owned.
 */
typedef struct progpu_native_clip_chain {
    uint32_t struct_size;
    uint32_t flags;
    const progpu_native_clip_path* paths;
    size_t path_count;
    const progpu_native_path_segment* segments;
    size_t segment_count;
    const progpu_native_path_boolean_node* boolean_nodes;
    size_t boolean_node_count;
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
    /* Optional canonical postfix programs referenced by path records. */
    const progpu_native_path_boolean_node* boolean_nodes;
    size_t boolean_node_count;
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
    /*
     * Additive full-sampler extension. Older callers ending at draw_state use
     * cubic B=0/C=0.5 and anisotropy one. Zero anisotropy canonicalizes to
     * one; LinearMipmap otherwise accepts one through sixteen and every other
     * mode requires one. Upload-backed images own only
     * the base mip while an external view may expose a producer-owned chain.
     */
    float cubic_b;
    float cubic_c;
    uint32_t max_anisotropy;
    uint32_t reserved3;
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
/*
 * Owner-thread host hook for an asynchronously reported device loss. It is
 * idempotent and performs no WebGPU calls. Every subsequent GPU operation
 * fails with DEVICE_LOST while scene updates may still replace the retained
 * CPU snapshot. Recreate transactionally clones that snapshot into a fresh
 * device-domain engine; the caller destroys the terminal source afterwards.
 */
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_mark_device_lost(
    progpu_native_engine* engine);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_recreate(
    const progpu_native_engine* source,
    const progpu_native_engine_options* options,
    progpu_native_engine** replacement);
PROGPU_NATIVE_API progpu_native_status progpu_native_engine_update_scene(
    progpu_native_engine* engine,
    const void* stream,
    size_t stream_size,
    progpu_native_scene_metrics* metrics);
PROGPU_NATIVE_API progpu_native_status
progpu_native_engine_bind_scene_external_images(
    progpu_native_engine* engine,
    const progpu_native_scene_external_image_binding* bindings,
    size_t binding_count);
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

/*
 * Bulk managed/native text boundary. Requirements performs complete request,
 * font, and capacity validation without allocation. Shape performs one run in
 * caller-owned storage and never retains any supplied pointer.
 */
PROGPU_NATIVE_API progpu_native_status
progpu_native_text_get_shape_requirements(
    const progpu_native_text_shape_request* request,
    progpu_native_text_shape_requirements* requirements);
PROGPU_NATIVE_API progpu_native_status progpu_native_text_shape(
    const progpu_native_text_shape_request* request,
    progpu_native_text_shaping_glyph* glyphs,
    uint32_t glyph_capacity,
    void* scratch,
    size_t scratch_size,
    progpu_native_text_shape_result* result);
/*
 * Retained high-performance path. Creation owns immutable snapshots of the
 * font and optional normalization plan. The context caches one exact shaping
 * plan and is single-thread-affine; stable runs allocate neither natively nor
 * in managed code. Run requests may leave font/normalization fields empty.
 */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_create(
    uint32_t abi_version,
    const uint8_t* font_data,
    size_t font_size,
    uint32_t face_index,
    const uint8_t* normalization_data,
    size_t normalization_data_size,
    progpu_native_text_context** context);
PROGPU_NATIVE_API void progpu_native_text_context_destroy(
    progpu_native_text_context* context);
/* Adds one immutable fallback face snapshot during context initialization.
 * Returned indices start at one; zero always identifies the primary face. */
PROGPU_NATIVE_API progpu_native_status
progpu_native_text_context_add_fallback_font(
    progpu_native_text_context* context,
    const uint8_t* font_data,
    size_t font_size,
    uint32_t face_index,
    uint64_t identity,
    uint32_t* font_index);
PROGPU_NATIVE_API progpu_native_status
progpu_native_text_context_get_shape_requirements(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* request,
    progpu_native_text_shape_requirements* requirements);
PROGPU_NATIVE_API progpu_native_status progpu_native_text_context_shape(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* request,
    progpu_native_text_shaping_glyph* glyphs,
    uint32_t glyph_capacity,
    void* scratch,
    size_t scratch_size,
    progpu_native_text_shape_result* result);

/* Bulk horizontal layout over a previously shaped run. Break values use
 * progpu_native_text_line_break_kind numeric values (0 prohibited,
 * 1 opportunity, 2 mandatory). Input/output records are fixed-layout and are
 * consumed synchronously without per-glyph marshaling or pointer retention. */
PROGPU_NATIVE_API progpu_native_status
progpu_native_text_layout_get_requirements(
    const progpu_native_text_layout_request* request,
    progpu_native_text_layout_requirements* requirements);
PROGPU_NATIVE_API progpu_native_status progpu_native_text_layout(
    const progpu_native_text_layout_request* request,
    progpu_native_positioned_text_glyph* glyphs,
    uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines,
    uint32_t line_capacity,
    void* scratch,
    size_t scratch_size,
    progpu_native_text_layout_result* result);

/* Bulk vertical positioned-column layout over a previously shaped run. */
PROGPU_NATIVE_API progpu_native_status
progpu_native_text_vertical_layout_get_requirements(
    const progpu_native_text_layout_request* request,
    progpu_native_text_vertical_layout_requirements* requirements);
PROGPU_NATIVE_API progpu_native_status progpu_native_text_vertical_layout(
    const progpu_native_text_layout_request* request,
    progpu_native_positioned_text_glyph* glyphs,
    uint32_t glyph_capacity,
    progpu_native_positioned_text_column* columns,
    uint32_t column_capacity,
    void* scratch,
    size_t scratch_size,
    progpu_native_text_vertical_layout_result* result);

/* Unicode 17 UAX #14 default line-break resolution. The scalar records retain
 * original UTF input ranges; canonical/script metadata is recomputed from the
 * code point. Output bytes use progpu_native_text_line_break_kind values. */
PROGPU_NATIVE_API progpu_native_status
progpu_native_text_get_line_break_requirements(
    const progpu_native_text_scalar* input,
    uint32_t input_count,
    progpu_native_text_line_break_requirements* requirements);
PROGPU_NATIVE_API progpu_native_status progpu_native_text_resolve_line_breaks(
    const progpu_native_text_scalar* input,
    uint32_t input_count,
    uint8_t* breaks_after,
    uint32_t break_capacity,
    void* scratch,
    size_t scratch_size,
    progpu_native_text_line_break_result* result);

/* Unicode 17 UAX #9 paragraph resolution. requested_paragraph_level accepts
 * -1 for first-strong auto resolution, 0 for LTR, or 1 for RTL. */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_get_bidi_requirements(
    const progpu_native_text_scalar* input,
    uint32_t input_count,
    progpu_native_text_bidi_requirements* requirements);
PROGPU_NATIVE_API progpu_native_status progpu_native_text_resolve_bidi(
    const progpu_native_text_scalar* input,
    uint32_t input_count,
    int32_t requested_paragraph_level,
    progpu_native_text_bidi_level* levels,
    uint32_t level_capacity,
    void* scratch,
    size_t scratch_size,
    progpu_native_text_bidi_result* result);

/* One retained paragraph crossing for a single font face: UAX #9 resolution,
 * UAX #14 boundaries, per-bidi-run shaping, cluster-preserving logical
 * assembly, per-line visual reordering, wrapping, and positioned output. */
PROGPU_NATIVE_API progpu_native_status
progpu_native_text_context_get_paragraph_requirements(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout,
    progpu_native_text_paragraph_requirements* requirements);
PROGPU_NATIVE_API progpu_native_status
progpu_native_text_context_layout_paragraph(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout,
    progpu_native_positioned_text_glyph* glyphs,
    uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines,
    uint32_t line_capacity,
    void* scratch,
    size_t scratch_size,
    progpu_native_text_paragraph_result* result);

#ifdef __cplusplus
}
#endif

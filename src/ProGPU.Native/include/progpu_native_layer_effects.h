#pragma once

#include "progpu_native.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Effects on the pixels of a layer, as passes of the device: each reads premultiplied RGBA8Unorm
 * textures texel for texel and writes the area at the top left of an RGBA8Unorm target, cleared
 * first. A pass is submitted when the call returns, so the queue runs it after the work that drew
 * its inputs and before the work that samples its output.
 *
 * The object belongs to the owner thread of its engine and must be destroyed before the engine.
 */
typedef struct progpu_native_layer_effects progpu_native_layer_effects;

/* A program of the host: WGSL with the entry points vs_main and fs_main. */
typedef struct progpu_native_layer_effect_program progpu_native_layer_effect_program;

enum {
    PROGPU_NATIVE_LAYER_EFFECT_MAX_LIGHTS = 8,
    PROGPU_NATIVE_LAYER_EFFECT_MAX_KERNEL_ELEMENTS = 25,
    PROGPU_NATIVE_LAYER_EFFECT_MAX_KERNEL_SIDE = 5,
    PROGPU_NATIVE_LAYER_EFFECT_MAX_PROGRAM_IMAGES = 8
};

/* The views of a pass. An input a pass does not read is 0. */
typedef struct progpu_native_layer_effect_target {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    uint32_t reserved;
    uintptr_t output_view;
    uintptr_t first_view;
    uintptr_t second_view;
} progpu_native_layer_effect_target;

typedef enum progpu_native_layer_color_matrix_flags {
    /* The matrix takes the premultiplied colour as it is; otherwise the straight colour, and the
     * result is premultiplied again. */
    PROGPU_NATIVE_LAYER_COLOR_MATRIX_PREMULTIPLIED_INPUT = 1
} progpu_native_layer_color_matrix_flags;

/* out = [R, G, B, A, 1] * rows: row i is what input channel i adds to each output channel, the
 * fifth row the offsets. */
typedef struct progpu_native_layer_color_matrix {
    uint32_t struct_size;
    uint32_t flags;
    float rows[5][4];
} progpu_native_layer_color_matrix;

typedef enum progpu_native_layer_convolution_flags {
    /* The alpha is kept and the straight colours are convolved. */
    PROGPU_NATIVE_LAYER_CONVOLUTION_KEEP_ALPHA = 1,
    /* What lies beyond the area is its edge pixels repeated, rather than transparent. */
    PROGPU_NATIVE_LAYER_CONVOLUTION_REPEAT_EDGES = 2
} progpu_native_layer_convolution_flags;

/* The sum of the pixels around each output pixel by the kernel turned by half a turn (SVG
 * feConvolveMatrix), times gain, plus bias. The target element is (columns / 2, rows / 2). */
typedef struct progpu_native_layer_convolution {
    uint32_t struct_size;
    uint32_t flags;
    uint32_t columns;
    uint32_t rows;
    float gain;
    float bias;
    float elements[PROGPU_NATIVE_LAYER_EFFECT_MAX_KERNEL_ELEMENTS];
    uint32_t reserved;
} progpu_native_layer_convolution;

typedef enum progpu_native_layer_composite_operation {
    /* A blend mode of the second input over the first. */
    PROGPU_NATIVE_LAYER_COMPOSITE_BLEND = 0,
    /* k[0] first second + k[1] first + k[2] second + k[3], per channel. */
    PROGPU_NATIVE_LAYER_COMPOSITE_ARITHMETIC = 1,
    /* as (ad - Cd) + (1 - as) Cd, the alpha of the first input kept. */
    PROGPU_NATIVE_LAYER_COMPOSITE_MASK_INVERT = 2
} progpu_native_layer_composite_operation;

/* The modes the blend layers of a scene do not have, by their D2D1_BLEND_MODE values. */
typedef enum progpu_native_layer_blend_mode {
    PROGPU_NATIVE_LAYER_BLEND_DISSOLVE = 4,
    PROGPU_NATIVE_LAYER_BLEND_LINEAR_BURN = 6,
    PROGPU_NATIVE_LAYER_BLEND_DARKER_COLOR = 7,
    PROGPU_NATIVE_LAYER_BLEND_LIGHTER_COLOR = 8,
    PROGPU_NATIVE_LAYER_BLEND_VIVID_LIGHT = 14,
    PROGPU_NATIVE_LAYER_BLEND_LINEAR_LIGHT = 15,
    PROGPU_NATIVE_LAYER_BLEND_PIN_LIGHT = 16,
    PROGPU_NATIVE_LAYER_BLEND_HARD_MIX = 17,
    PROGPU_NATIVE_LAYER_BLEND_SUBTRACT = 24,
    PROGPU_NATIVE_LAYER_BLEND_DIVISION = 25
} progpu_native_layer_blend_mode;

/* The Dissolve grid is in the coordinates of the drawing; pixel_to_drawing maps a pixel of the
 * area to them. */
typedef struct progpu_native_layer_composite {
    uint32_t struct_size;
    uint32_t operation;
    uint32_t blend_mode;
    uint32_t grid_columns;
    uint32_t grid_rows;
    uint32_t reserved;
    float k[4];
    progpu_native_image_rect grid;
    progpu_native_affine_2d pixel_to_drawing;
} progpu_native_layer_composite;

typedef enum progpu_native_layer_light_kind {
    PROGPU_NATIVE_LAYER_LIGHT_POINT = 1,
    PROGPU_NATIVE_LAYER_LIGHT_SPOT = 2
} progpu_native_layer_light_kind;

/* A light on a flat surface facing +Z. The light at a point is its colour (between the cones of a
 * spot, from outer_color to color) times the cone, the attenuation 1 / (c + l d + q d^2) with d
 * raised to min_cutoff and no light beyond max_cutoff when has_max_cutoff, and N.L; a specular
 * highlight (N.H)^shine is added by the amount of the lighting. */
typedef struct progpu_native_layer_light {
    uint32_t kind;
    uint32_t has_max_cutoff;
    float cos_inner_cone;
    float cos_outer_cone;
    float color[3];
    float outer_color[3];
    float position[3];
    float direction[3];
    float attenuation_constant;
    float attenuation_linear;
    float attenuation_quadratic;
    float min_cutoff;
    float max_cutoff;
    /* From the coordinates of the drawing to the space of the light: x' = x row[0] + y row[1] +
     * row[2] for each of the three rows x, y and w. */
    float to_light[3][3];
} progpu_native_layer_light;

typedef enum progpu_native_layer_lighting_output {
    /* The light clamped to 0..1, opaque. */
    PROGPU_NATIVE_LAYER_LIGHTING_FILL = 0,
    /* The first input with each colour channel times the light clamped to 0..max_factor. */
    PROGPU_NATIVE_LAYER_LIGHTING_MULTIPLY = 1
} progpu_native_layer_lighting_output;

typedef struct progpu_native_layer_lighting {
    uint32_t struct_size;
    uint32_t output;
    uint32_t light_count;
    uint32_t origin_x;
    uint32_t origin_y;
    float constant_light[3];
    float diffuse;
    float specular;
    float shine;
    float max_factor;
    /* From a device pixel (the pixel of the area plus the origin) to the drawing. */
    progpu_native_affine_2d pixel_to_drawing;
    progpu_native_layer_light lights[PROGPU_NATIVE_LAYER_EFFECT_MAX_LIGHTS];
} progpu_native_layer_lighting;

/* The first input times a factor image (the second input), sampled between the centres of its
 * samples; a sample of 1 stands for scale. Nothing outside the unit square of the image is kept. */
typedef struct progpu_native_layer_factor_multiply {
    uint32_t struct_size;
    float scale;
    progpu_native_affine_2d pixel_to_unit;
} progpu_native_layer_factor_multiply;

/* The noise of the backdrop materials: colour times a random number per device pixel, at opacity,
 * premultiplied. */
typedef struct progpu_native_layer_material_noise {
    uint32_t struct_size;
    float opacity;
    float color[3];
    float origin_x;
    float origin_y;
} progpu_native_layer_material_noise;

PROGPU_NATIVE_API progpu_native_status progpu_native_layer_effects_create(
    progpu_native_engine* engine,
    progpu_native_layer_effects** effects);
PROGPU_NATIVE_API void progpu_native_layer_effects_destroy(progpu_native_layer_effects* effects);

PROGPU_NATIVE_API progpu_native_status progpu_native_layer_effects_color_matrix(
    progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target,
    const progpu_native_layer_color_matrix* matrix);
PROGPU_NATIVE_API progpu_native_status progpu_native_layer_effects_convolution(
    progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target,
    const progpu_native_layer_convolution* convolution);
PROGPU_NATIVE_API progpu_native_status progpu_native_layer_effects_composite(
    progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target,
    const progpu_native_layer_composite* composite);
PROGPU_NATIVE_API progpu_native_status progpu_native_layer_effects_lighting(
    progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target,
    const progpu_native_layer_lighting* lighting);
PROGPU_NATIVE_API progpu_native_status progpu_native_layer_effects_factor_multiply(
    progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target,
    const progpu_native_layer_factor_multiply* factors);
PROGPU_NATIVE_API progpu_native_status progpu_native_layer_effects_material_noise(
    progpu_native_layer_effects* effects,
    const progpu_native_layer_effect_target* target,
    const progpu_native_layer_material_noise* noise);

/*
 * A program of the host. Its bindings in group 0: 0 a uniform block of uniform_size bytes, 1 a
 * uniform block of one vec4, 2 the content texture and 3 and up its images, all read with
 * textureLoad. The device reports a program it refuses through its error scopes.
 */
PROGPU_NATIVE_API progpu_native_status progpu_native_layer_effects_create_program(
    progpu_native_layer_effects* effects,
    const char* wgsl,
    size_t wgsl_size,
    uint32_t uniform_size,
    uint32_t image_count,
    progpu_native_layer_effect_program** program);
PROGPU_NATIVE_API void progpu_native_layer_effect_program_destroy(
    progpu_native_layer_effect_program* program);
/* Runs a program over target.first_view into target.output_view. */
PROGPU_NATIVE_API progpu_native_status progpu_native_layer_effects_run_program(
    progpu_native_layer_effects* effects,
    progpu_native_layer_effect_program* program,
    const progpu_native_layer_effect_target* target,
    const void* uniforms,
    size_t uniform_size,
    const float pass_block[4],
    const uintptr_t* image_views,
    uint32_t image_count);

#ifdef __cplusplus
}
#endif

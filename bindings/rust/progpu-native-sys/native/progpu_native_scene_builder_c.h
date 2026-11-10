/*
 * A C ABI over the retained scene builder (progpu::native::semantic_scene_builder,
 * progpu_native_scene_builder.hpp), for hosts that cannot call C++: every function is a thin
 * wrapper of the builder method of the same name, with spans as pointer and count. The records
 * are the C records of progpu_native.h. A function returns nonzero on success; on failure
 * progpu_native_scene_builder_last_error gives the builder's scene_build_error.
 *
 * A builder is owned by one thread. Spans are borrowed for the call only.
 */
#ifndef PROGPU_NATIVE_SCENE_BUILDER_C_H
#define PROGPU_NATIVE_SCENE_BUILDER_C_H

#include <stddef.h>
#include <stdint.h>

#include "progpu_native.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct progpu_native_scene_builder progpu_native_scene_builder;

progpu_native_scene_builder* progpu_native_scene_builder_create(uint64_t scene_id, uint64_t generation);
void progpu_native_scene_builder_destroy(progpu_native_scene_builder* builder);
uint32_t progpu_native_scene_builder_last_error(const progpu_native_scene_builder* builder);

int progpu_native_scene_builder_reserve(progpu_native_scene_builder* builder, uint32_t command_count,
    uint32_t resource_count, uint64_t arena_bytes);
int progpu_native_scene_builder_reset(progpu_native_scene_builder* builder, uint64_t scene_id, uint64_t generation);
int progpu_native_scene_builder_advance_generation(progpu_native_scene_builder* builder, uint64_t generation);
int progpu_native_scene_builder_set_resource_identity(progpu_native_scene_builder* builder,
    uint32_t resource_index, uint64_t resource_id, uint64_t generation);

int progpu_native_scene_builder_add_solid_brush(progpu_native_scene_builder* builder,
    progpu_native_color color, float opacity, uint32_t* brush_index);
int progpu_native_scene_builder_add_brush(progpu_native_scene_builder* builder,
    const progpu_native_scene_brush* brush, const progpu_native_scene_gradient_stop* stops, size_t stop_count,
    uint32_t* brush_index);
int progpu_native_scene_builder_add_state(progpu_native_scene_builder* builder,
    const progpu_native_scene_state* state, uint32_t* resource_index);
int progpu_native_scene_builder_add_rgba8_image(progpu_native_scene_builder* builder, uint32_t width,
    uint32_t height, uint32_t row_bytes, const void* pixels, size_t pixel_bytes, uint32_t* resource_index);
int progpu_native_scene_builder_add_bgra8_image(progpu_native_scene_builder* builder, uint32_t width,
    uint32_t height, uint32_t row_bytes, const void* pixels, size_t pixel_bytes, uint32_t* resource_index);
int progpu_native_scene_builder_add_external_image(progpu_native_scene_builder* builder, uint32_t width,
    uint32_t height, uint32_t* resource_index);
int progpu_native_scene_builder_add_text_style(progpu_native_scene_builder* builder,
    const progpu_native_scene_text_style* style, uint32_t* style_index);
int progpu_native_scene_builder_add_glyph_outlines(progpu_native_scene_builder* builder,
    const progpu_native_scene_glyph_outline* outlines, size_t outline_count,
    const progpu_native_path_segment* segments, size_t segment_count, uint32_t* resource_index);
/* A glyph resource that names outlines registered with the engine
   (progpu_native_engine_register_glyph_outlines); a positioned glyph's outline_index is an index
   into keys. Only for progpu_native_scene_builder_draw_glyph_run. */
int progpu_native_scene_builder_add_glyph_keys(progpu_native_scene_builder* builder,
    const progpu_native_scene_glyph_key* keys, size_t key_count, uint32_t* resource_index);
int progpu_native_scene_builder_add_axis_aligned_clip_mask(progpu_native_scene_builder* builder,
    progpu_native_image_rect bounds, uint32_t* resource_index);
int progpu_native_scene_builder_add_rounded_rectangle_mask(progpu_native_scene_builder* builder,
    const progpu_native_scene_layer_mask* mask, uint32_t* resource_index);
/* Two to PROGPU_NATIVE_SCENE_MAX_ANALYTIC_MASKS rounded rectangle masks that all apply: the mask of
   clips nested in each other, evaluated per draw without a mask texture. */
int progpu_native_scene_builder_add_analytic_mask_chain(progpu_native_scene_builder* builder,
    const progpu_native_scene_layer_mask* masks, size_t mask_count, uint32_t* resource_index);
int progpu_native_scene_builder_add_vector_clip_mask(progpu_native_scene_builder* builder,
    const progpu_native_scene_clip_path* paths, size_t path_count, const progpu_native_path_segment* segments,
    size_t segment_count, float opacity, uint32_t* resource_index);
int progpu_native_scene_builder_add_effect_chain(progpu_native_scene_builder* builder,
    const progpu_native_group_effect* effects, size_t effect_count, uint32_t revision, uint32_t* resource_index);

int progpu_native_scene_builder_save(progpu_native_scene_builder* builder, uint32_t state_resource_index);
int progpu_native_scene_builder_restore(progpu_native_scene_builder* builder);
int progpu_native_scene_builder_clear_target(progpu_native_scene_builder* builder, progpu_native_color color);
int progpu_native_scene_builder_push_layer(progpu_native_scene_builder* builder,
    const progpu_native_scene_layer* layer);
int progpu_native_scene_builder_pop_layer(progpu_native_scene_builder* builder);

int progpu_native_scene_builder_draw_analytic(progpu_native_scene_builder* builder,
    const progpu_native_analytic_primitive* primitives, size_t primitive_count, const uint32_t* brush_indices,
    size_t brush_index_count, progpu_native_image_rect bounds, uint32_t state_resource_index);
int progpu_native_scene_builder_draw_geometry(progpu_native_scene_builder* builder,
    const progpu_native_geometry_primitive* primitives, size_t primitive_count, const uint32_t* brush_indices,
    size_t brush_index_count, progpu_native_image_rect bounds, uint32_t state_resource_index);
int progpu_native_scene_builder_draw_strokes(progpu_native_scene_builder* builder,
    const progpu_native_scene_stroke* strokes, size_t stroke_count, const progpu_native_point* points,
    size_t point_count, const double* doubles, size_t double_count, const uint32_t* brush_indices,
    size_t brush_index_count, progpu_native_image_rect bounds, uint32_t state_resource_index);
int progpu_native_scene_builder_draw_paths(progpu_native_scene_builder* builder,
    const progpu_native_scene_path_fill* paths, size_t path_count, const progpu_native_path_segment* segments,
    size_t segment_count, const uint32_t* brush_indices, size_t brush_index_count, progpu_native_image_rect bounds,
    uint32_t state_resource_index);
int progpu_native_scene_builder_draw_image(progpu_native_scene_builder* builder, uint32_t image_resource_index,
    const progpu_native_scene_image_draw* image, progpu_native_image_rect bounds, uint32_t state_resource_index,
    const progpu_native_scene_image_sampling_options* sampling_options);
int progpu_native_scene_builder_draw_glyph_run(progpu_native_scene_builder* builder,
    uint32_t glyph_resource_index, const progpu_native_positioned_glyph* glyphs, size_t glyph_count,
    progpu_native_image_rect bounds, uint32_t state_resource_index, uint32_t text_style_index);

size_t progpu_native_scene_builder_required_stream_size(const progpu_native_scene_builder* builder);
int progpu_native_scene_builder_build_into(const progpu_native_scene_builder* builder, void* destination,
    size_t destination_size, size_t* bytes_written);

progpu_native_affine_2d progpu_native_scene_builder_identity_transform(void);
progpu_native_scene_state progpu_native_scene_builder_identity_state(void);

#ifdef __cplusplus
}
#endif

#endif

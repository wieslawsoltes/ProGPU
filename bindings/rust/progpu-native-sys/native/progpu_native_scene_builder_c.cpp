// The C ABI over the retained scene builder (progpu_native_scene_builder_c.h).

#include "progpu_native_scene_builder_c.h"

#include <cstddef>
#include <new>
#include <span>

#include "progpu_native_scene_builder.hpp"

using progpu::native::semantic_scene_builder;

struct progpu_native_scene_builder {
    semantic_scene_builder builder;
    explicit progpu_native_scene_builder(std::uint64_t scene_id, std::uint64_t generation)
        : builder(scene_id, generation) {}
};

namespace {

template <class T>
std::span<const T> span_of(const T* data, std::size_t count) noexcept {
    return count == 0 ? std::span<const T>{} : std::span<const T>{data, count};
}

std::span<const std::byte> bytes_of(const void* data, std::size_t count) noexcept {
    return count == 0 ? std::span<const std::byte>{}
                      : std::span<const std::byte>{static_cast<const std::byte*>(data), count};
}

int result(bool ok) noexcept { return ok ? 1 : 0; }

} // namespace

extern "C" {

progpu_native_scene_builder* progpu_native_scene_builder_create(uint64_t scene_id, uint64_t generation) {
    return new (std::nothrow) progpu_native_scene_builder(scene_id, generation);
}

void progpu_native_scene_builder_destroy(progpu_native_scene_builder* builder) { delete builder; }

uint32_t progpu_native_scene_builder_last_error(const progpu_native_scene_builder* builder) {
    return static_cast<uint32_t>(builder->builder.last_error());
}

int progpu_native_scene_builder_reserve(progpu_native_scene_builder* builder, uint32_t command_count,
    uint32_t resource_count, uint64_t arena_bytes) {
    return result(builder->builder.reserve(command_count, resource_count, arena_bytes));
}

int progpu_native_scene_builder_reset(progpu_native_scene_builder* builder, uint64_t scene_id, uint64_t generation) {
    return result(builder->builder.reset(scene_id, generation));
}

int progpu_native_scene_builder_advance_generation(progpu_native_scene_builder* builder, uint64_t generation) {
    return result(builder->builder.advance_generation(generation));
}

int progpu_native_scene_builder_set_resource_identity(progpu_native_scene_builder* builder,
    uint32_t resource_index, uint64_t resource_id, uint64_t generation) {
    return result(builder->builder.set_resource_identity(resource_index, resource_id, generation));
}

int progpu_native_scene_builder_add_solid_brush(progpu_native_scene_builder* builder,
    progpu_native_color color, float opacity, uint32_t* brush_index) {
    return result(builder->builder.add_solid_brush(color, opacity, *brush_index));
}

int progpu_native_scene_builder_add_brush(progpu_native_scene_builder* builder,
    const progpu_native_scene_brush* brush, const progpu_native_scene_gradient_stop* stops, size_t stop_count,
    uint32_t* brush_index) {
    return result(builder->builder.add_brush(*brush, span_of(stops, stop_count), *brush_index));
}

int progpu_native_scene_builder_add_state(progpu_native_scene_builder* builder,
    const progpu_native_scene_state* state, uint32_t* resource_index) {
    return result(builder->builder.add_state(*state, *resource_index));
}

int progpu_native_scene_builder_add_rgba8_image(progpu_native_scene_builder* builder, uint32_t width,
    uint32_t height, uint32_t row_bytes, const void* pixels, size_t pixel_bytes, uint32_t* resource_index) {
    return result(builder->builder.add_rgba8_image(width, height, row_bytes, bytes_of(pixels, pixel_bytes), *resource_index));
}

int progpu_native_scene_builder_add_bgra8_image(progpu_native_scene_builder* builder, uint32_t width,
    uint32_t height, uint32_t row_bytes, const void* pixels, size_t pixel_bytes, uint32_t* resource_index) {
    return result(builder->builder.add_bgra8_image(width, height, row_bytes, bytes_of(pixels, pixel_bytes), *resource_index));
}

int progpu_native_scene_builder_add_external_image(progpu_native_scene_builder* builder, uint32_t width,
    uint32_t height, uint32_t* resource_index) {
    return result(builder->builder.add_external_image(width, height, *resource_index));
}

int progpu_native_scene_builder_add_text_style(progpu_native_scene_builder* builder,
    const progpu_native_scene_text_style* style, uint32_t* style_index) {
    return result(builder->builder.add_text_style(*style, *style_index));
}

int progpu_native_scene_builder_add_glyph_outlines(progpu_native_scene_builder* builder,
    const progpu_native_scene_glyph_outline* outlines, size_t outline_count,
    const progpu_native_path_segment* segments, size_t segment_count, uint32_t* resource_index) {
    return result(builder->builder.add_glyph_outlines(
        span_of(outlines, outline_count), span_of(segments, segment_count), *resource_index));
}

int progpu_native_scene_builder_add_glyph_keys(progpu_native_scene_builder* builder,
    const progpu_native_scene_glyph_key* keys, size_t key_count, uint32_t* resource_index) {
    return result(builder->builder.add_glyph_keys(span_of(keys, key_count), *resource_index));
}

int progpu_native_scene_builder_add_axis_aligned_clip_mask(progpu_native_scene_builder* builder,
    progpu_native_image_rect bounds, uint32_t* resource_index) {
    return result(builder->builder.add_axis_aligned_clip_mask(bounds, *resource_index));
}

int progpu_native_scene_builder_add_rounded_rectangle_mask(progpu_native_scene_builder* builder,
    const progpu_native_scene_layer_mask* mask, uint32_t* resource_index) {
    return result(builder->builder.add_rounded_rectangle_mask(*mask, *resource_index));
}

int progpu_native_scene_builder_add_analytic_mask_chain(progpu_native_scene_builder* builder,
    const progpu_native_scene_layer_mask* masks, size_t mask_count, uint32_t* resource_index) {
    return result(builder->builder.add_analytic_mask_chain(span_of(masks, mask_count), *resource_index));
}

int progpu_native_scene_builder_add_vector_clip_mask(progpu_native_scene_builder* builder,
    const progpu_native_scene_clip_path* paths, size_t path_count, const progpu_native_path_segment* segments,
    size_t segment_count, float opacity, uint32_t* resource_index) {
    return result(builder->builder.add_vector_clip_mask(
        span_of(paths, path_count), span_of(segments, segment_count), opacity, *resource_index));
}

int progpu_native_scene_builder_add_effect_chain(progpu_native_scene_builder* builder,
    const progpu_native_group_effect* effects, size_t effect_count, uint32_t revision, uint32_t* resource_index) {
    return result(builder->builder.add_effect_chain(span_of(effects, effect_count), revision, *resource_index));
}

int progpu_native_scene_builder_save(progpu_native_scene_builder* builder, uint32_t state_resource_index) {
    return result(builder->builder.save(state_resource_index));
}

int progpu_native_scene_builder_restore(progpu_native_scene_builder* builder) {
    return result(builder->builder.restore());
}

int progpu_native_scene_builder_clear_target(progpu_native_scene_builder* builder, progpu_native_color color) {
    return result(builder->builder.clear_target(color));
}

int progpu_native_scene_builder_push_layer(progpu_native_scene_builder* builder,
    const progpu_native_scene_layer* layer) {
    return result(builder->builder.push_layer(*layer));
}

int progpu_native_scene_builder_pop_layer(progpu_native_scene_builder* builder) {
    return result(builder->builder.pop_layer());
}

int progpu_native_scene_builder_draw_analytic(progpu_native_scene_builder* builder,
    const progpu_native_analytic_primitive* primitives, size_t primitive_count, const uint32_t* brush_indices,
    size_t brush_index_count, progpu_native_image_rect bounds, uint32_t state_resource_index) {
    return result(builder->builder.draw_analytic(span_of(primitives, primitive_count),
        span_of(brush_indices, brush_index_count), bounds, state_resource_index));
}

int progpu_native_scene_builder_draw_geometry(progpu_native_scene_builder* builder,
    const progpu_native_geometry_primitive* primitives, size_t primitive_count, const uint32_t* brush_indices,
    size_t brush_index_count, progpu_native_image_rect bounds, uint32_t state_resource_index) {
    return result(builder->builder.draw_geometry(span_of(primitives, primitive_count),
        span_of(brush_indices, brush_index_count), bounds, state_resource_index));
}

int progpu_native_scene_builder_draw_strokes(progpu_native_scene_builder* builder,
    const progpu_native_scene_stroke* strokes, size_t stroke_count, const progpu_native_point* points,
    size_t point_count, const double* doubles, size_t double_count, const uint32_t* brush_indices,
    size_t brush_index_count, progpu_native_image_rect bounds, uint32_t state_resource_index) {
    return result(builder->builder.draw_strokes(span_of(strokes, stroke_count), span_of(points, point_count),
        span_of(doubles, double_count), span_of(brush_indices, brush_index_count), bounds, state_resource_index));
}

int progpu_native_scene_builder_draw_paths(progpu_native_scene_builder* builder,
    const progpu_native_scene_path_fill* paths, size_t path_count, const progpu_native_path_segment* segments,
    size_t segment_count, const uint32_t* brush_indices, size_t brush_index_count, progpu_native_image_rect bounds,
    uint32_t state_resource_index) {
    return result(builder->builder.draw_paths(span_of(paths, path_count), span_of(segments, segment_count),
        span_of(brush_indices, brush_index_count), bounds, state_resource_index));
}

int progpu_native_scene_builder_draw_image(progpu_native_scene_builder* builder, uint32_t image_resource_index,
    const progpu_native_scene_image_draw* image, progpu_native_image_rect bounds, uint32_t state_resource_index,
    const progpu_native_scene_image_sampling_options* sampling_options) {
    return result(builder->builder.draw_image(image_resource_index, *image, bounds, state_resource_index, sampling_options));
}

int progpu_native_scene_builder_draw_glyph_run(progpu_native_scene_builder* builder,
    uint32_t glyph_resource_index, const progpu_native_positioned_glyph* glyphs, size_t glyph_count,
    progpu_native_image_rect bounds, uint32_t state_resource_index, uint32_t text_style_index) {
    return result(builder->builder.draw_glyph_run(
        glyph_resource_index, span_of(glyphs, glyph_count), bounds, state_resource_index, text_style_index));
}

size_t progpu_native_scene_builder_required_stream_size(const progpu_native_scene_builder* builder) {
    return builder->builder.required_stream_size();
}

int progpu_native_scene_builder_build_into(const progpu_native_scene_builder* builder, void* destination,
    size_t destination_size, size_t* bytes_written) {
    std::size_t written = 0;
    const bool ok = builder->builder.build_into(
        std::span<std::byte>{static_cast<std::byte*>(destination), destination_size}, written);
    *bytes_written = written;
    return result(ok);
}

progpu_native_affine_2d progpu_native_scene_builder_identity_transform(void) {
    return semantic_scene_builder::identity_transform();
}

progpu_native_scene_state progpu_native_scene_builder_identity_state(void) {
    return semantic_scene_builder::identity_state();
}

} // extern "C"

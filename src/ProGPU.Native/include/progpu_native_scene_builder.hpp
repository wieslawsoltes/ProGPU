#ifndef PROGPU_NATIVE_SCENE_BUILDER_HPP
#define PROGPU_NATIVE_SCENE_BUILDER_HPP

#include "progpu_native.h"
#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <vector>

namespace progpu::native {

enum class scene_build_error : std::uint32_t {
    none = 0U,
    invalid_argument,
    invalid_state,
    unbalanced_stack,
    capacity_exceeded,
    out_of_memory
};

struct scene_build_metrics final {
    std::uint32_t command_count = 0U;
    std::uint32_t resource_count = 0U;
    std::uint32_t brush_count = 0U;
    std::uint32_t text_style_count = 0U;
    std::uint32_t maximum_stack_depth = 0U;
    std::uint64_t arena_bytes = 0U;
    std::uint64_t stream_bytes = 0U;
};

struct shaped_text_scene_options final {
    progpu_native_point basis_x{1.0F, 0.0F};
    progpu_native_point basis_y{0.0F, 1.0F};
    progpu_native_color color{1.0F, 1.0F, 1.0F, 1.0F};
    float atlas_to_logical_scale = 1.0F;
    float bold_offset = 0.0F;
    float italic_skew = 0.0F;
};

/*
 * Standalone retained-scene recorder/compiler for native C++ clients.
 * Recording is O(C + R + P) in commands, resources, and payload bytes.
 * build() performs one deterministic O(C + R + P) pointer-free serialization.
 * Stable replay owns no builder work: callers retain the resulting byte vector
 * and submit one update only when its generation changes.
 */
class semantic_scene_builder final {
public:
    explicit semantic_scene_builder(
        std::uint64_t scene_id,
        std::uint64_t generation = 1U);
    ~semantic_scene_builder();

    semantic_scene_builder(semantic_scene_builder&&) noexcept;
    semantic_scene_builder& operator=(semantic_scene_builder&&) noexcept;
    semantic_scene_builder(const semantic_scene_builder&) = delete;
    semantic_scene_builder& operator=(const semantic_scene_builder&) = delete;

    bool reserve(
        std::uint32_t command_count,
        std::uint32_t resource_count,
        std::uint64_t arena_bytes) noexcept;
    bool reset(std::uint64_t scene_id, std::uint64_t generation) noexcept;
    bool advance_generation(std::uint64_t generation) noexcept;
    bool set_resource_identity(
        std::uint32_t resource_index,
        std::uint64_t resource_id,
        std::uint64_t generation) noexcept;

    bool add_solid_brush(
        progpu_native_color color,
        float opacity,
        std::uint32_t& brush_index) noexcept;
    bool add_brush(
        const progpu_native_scene_brush& brush,
        std::span<const progpu_native_scene_gradient_stop> gradient_stops,
        std::uint32_t& brush_index) noexcept;
    bool add_state(
        const progpu_native_scene_state& state,
        std::uint32_t& resource_index) noexcept;
    bool add_guideline_set(
        std::span<const double> guidelines_x,
        std::span<const double> guidelines_y,
        std::uint32_t& resource_index,
        bool composite_only = false,
        bool per_point = false) noexcept;
    bool add_rgba8_image(
        std::uint32_t width,
        std::uint32_t height,
        std::uint32_t row_bytes,
        std::span<const std::byte> pixels,
        std::uint32_t& resource_index) noexcept;
    bool add_external_image(
        std::uint32_t width,
        std::uint32_t height,
        std::uint32_t& resource_index) noexcept;
    bool update_rgba8_image(
        std::uint32_t resource_index,
        std::uint32_t width,
        std::uint32_t height,
        std::uint32_t row_bytes,
        std::span<const std::byte> pixels,
        std::uint64_t resource_generation) noexcept;
    bool add_text_style(
        const progpu_native_scene_text_style& style,
        std::uint32_t& style_index) noexcept;
    bool add_hit_test_index(
        std::span<const progpu_native_hit_test_primitive> primitives,
        std::span<const progpu_native_hit_test_node> nodes,
        std::span<const std::uint32_t> primitive_indices,
        std::span<const progpu_native_path_segment> path_segments,
        std::uint32_t& resource_index) noexcept;
    bool add_glyph_outlines(
        std::span<const progpu_native_scene_glyph_outline> outlines,
        std::span<const progpu_native_path_segment> segments,
        std::uint32_t& resource_index) noexcept;
    bool add_color_glyph_bitmaps(
        std::span<const progpu_native_scene_color_glyph_bitmap> bitmaps,
        std::span<const std::byte> rgba_pixels,
        std::uint32_t& resource_index) noexcept;
    bool add_rounded_rectangle_mask(
        const progpu_native_scene_layer_mask& mask,
        std::uint32_t& resource_index) noexcept;
    bool add_coverage_mask(
        const progpu_native_scene_layer_coverage_mask& mask,
        std::span<const std::byte> coverage,
        std::uint32_t& resource_index) noexcept;
    // Records one canonical typed brush mask and its inline gradient stops.
    bool add_brush_mask(
        const progpu_native_scene_layer_brush_mask& mask,
        std::span<const progpu_native_scene_gradient_stop> gradient_stops,
        std::uint32_t& resource_index) noexcept;
    bool add_analytic_mask_chain(
        std::span<const progpu_native_scene_layer_mask> masks,
        std::uint32_t& resource_index) noexcept;
    bool add_vector_clip_mask(
        std::span<const progpu_native_scene_clip_path> paths,
        std::span<const progpu_native_path_segment> segments,
        float opacity,
        std::uint32_t& resource_index) noexcept;
    bool add_vector_clip_mask(
        std::span<const progpu_native_scene_clip_path> paths,
        std::span<const progpu_native_path_segment> segments,
        std::span<const progpu_native_scene_path_boolean_node> boolean_nodes,
        float opacity,
        std::uint32_t& resource_index) noexcept;
    bool add_effect_chain(
        std::span<const progpu_native_group_effect> effects,
        std::uint32_t revision,
        std::uint32_t& resource_index) noexcept;

    bool save(
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;
    bool restore() noexcept;
    bool push_layer(const progpu_native_scene_layer& layer) noexcept;
    bool pop_layer() noexcept;

    bool draw_analytic(
        std::span<const progpu_native_analytic_primitive> primitives,
        std::span<const std::uint32_t> brush_indices,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;

    bool draw_geometry(
        std::span<const progpu_native_geometry_primitive> primitives,
        std::span<const std::uint32_t> brush_indices,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;

    bool draw_strokes(
        std::span<const progpu_native_scene_stroke> strokes,
        std::span<const progpu_native_point> points,
        std::span<const double> doubles,
        std::span<const std::uint32_t> brush_indices,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;

    bool draw_lines_3d(
        std::span<const progpu_native_scene_line_3d> lines,
        const progpu_native_scene_camera_3d& camera,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;

    bool draw_meshes_3d(
        std::span<const progpu_native_scene_mesh_3d> meshes,
        std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
        std::span<const std::uint32_t> indices,
        std::span<const progpu_native_scene_light_3d> lights,
        std::span<const progpu_native_scene_brush> materials,
        std::span<const progpu_native_scene_gradient_stop> gradient_stops,
        const progpu_native_scene_camera_3d& camera,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;

    bool draw_meshes_3d(
        std::span<const progpu_native_scene_mesh_3d> meshes,
        std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
        std::span<const std::uint32_t> indices,
        std::span<const progpu_native_scene_light_3d> lights,
        const progpu_native_scene_camera_3d& camera,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;

    bool draw_meshes_3d(
        std::span<const progpu_native_scene_mesh_3d> meshes,
        std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
        std::span<const std::uint32_t> indices,
        const progpu_native_scene_camera_3d& camera,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;

    bool draw_paths(
        std::span<const progpu_native_scene_path_fill> paths,
        std::span<const progpu_native_path_segment> segments,
        std::span<const std::uint32_t> brush_indices,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX,
        std::span<const progpu_native_scene_path_boolean_node>
            boolean_nodes = {}) noexcept;

    bool draw_image(
        std::uint32_t image_resource_index,
        const progpu_native_scene_image_draw& image,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX,
        const progpu_native_scene_image_sampling_options*
            sampling_options = nullptr,
        const progpu_native_scene_image_color_matrix*
            color_matrix = nullptr,
        const progpu_native_scene_image_effect*
            effect = nullptr) noexcept;

    bool draw_image_patches(
        std::uint32_t image_resource_index,
        const progpu_native_scene_image_draw& image,
        std::span<const progpu_native_scene_image_patch> patches,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX,
        const progpu_native_scene_image_sampling_options*
            sampling_options = nullptr,
        const progpu_native_scene_image_color_matrix*
            color_matrix = nullptr,
        const progpu_native_scene_image_effect*
            effect = nullptr) noexcept;

    bool draw_glyph_run(
        std::uint32_t glyph_resource_index,
        std::span<const progpu_native_positioned_glyph> glyphs,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX,
        std::uint32_t text_style_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;

    bool draw_shaped_text_run(
        std::uint32_t glyph_resource_index,
        std::span<const text::shaping_glyph> shaped_glyphs,
        std::span<const text::positioned_text_glyph> positioned_glyphs,
        std::span<progpu_native_positioned_glyph> conversion_scratch,
        const shaped_text_scene_options& options,
        progpu_native_image_rect bounds,
        std::span<const std::uint32_t> glyph_to_outline = {},
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX,
        std::uint32_t text_style_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;

    bool build(
        std::vector<std::byte>& stream,
        scene_build_metrics* metrics = nullptr) const noexcept;
    std::size_t required_stream_size() const noexcept;
    bool build_into(
        std::span<std::byte> destination,
        std::size_t& bytes_written,
        scene_build_metrics* metrics = nullptr) const noexcept;

    scene_build_error last_error() const noexcept;
    std::uint64_t scene_id() const noexcept;
    std::uint64_t generation() const noexcept;

    static progpu_native_affine_2d identity_transform() noexcept;
    static progpu_native_scene_state identity_state() noexcept;

private:
    bool try_measure_stream(
        std::uint32_t& command_offset,
        std::uint32_t& resource_offset,
        std::uint32_t& arena_offset,
        std::uint32_t& total_size) const noexcept;

    bool append_3d_command(
        std::uint32_t resource_kind,
        std::uint32_t command_kind,
        std::vector<std::byte> payload,
        std::vector<std::byte> auxiliary,
        const progpu_native_scene_camera_3d& camera,
        std::span<const std::uint32_t> material_brush_indices,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index);

    struct implementation;
    std::unique_ptr<implementation> implementation_;
};

} // namespace progpu::native

#endif

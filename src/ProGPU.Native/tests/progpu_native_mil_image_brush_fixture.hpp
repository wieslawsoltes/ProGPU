#pragma once

#include "progpu_native_mil_visual_clip_fixture.hpp"

namespace progpu::native::tests {

enum class mil_brush_fixture_source { bitmap, drawing_image, drawing, visual };

struct mil_image_brush_fixture_options {
    std::uint32_t stretch{1U};
    std::uint32_t tile_mode{};
    std::array<double, 4U> viewbox{0.0, 0.0, 1.0, 1.0};
    std::uint32_t viewbox_units{1U};
    double dpi_x{96.0};
    double dpi_y{96.0};
    double opacity{1.0};
    bool rotate{};
    bool relative_scale{};
    bool skew{};
    bool linear{};
    mil_brush_fixture_source source{mil_brush_fixture_source::bitmap};
    bool source_cycle{};
};

inline bool build_mil_image_brush_fixture(std::vector<std::byte>& scene,
    const mil_image_brush_fixture_options& options, std::uint64_t scene_id) {
    using mil::command;
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
    std::vector<std::byte> batch;
    packet(batch, command::channel_create_resource, 1U, 39U);
    packet(batch, command::visual_create, 1U);
    packet(batch, command::visual_set_render_options, 1U, 0x03U,
        1U, 0U, options.linear ? 1U : 3U, 0U, 0U, 0U);
    packet(batch, command::channel_create_resource, 2U, 43U);
    const bool vector_source = options.source != mil_brush_fixture_source::bitmap;
    const bool drawing_brush = options.source == mil_brush_fixture_source::drawing;
    const bool visual_brush = options.source == mil_brush_fixture_source::visual;
    packet(batch, command::channel_create_resource, 3U,
        visual_brush ? 39U : drawing_brush ? 87U : vector_source ? 59U : 95U);
    packet(batch, command::channel_create_resource, 4U, 47U);
    packet(batch, command::generic_target_create, 4U,
        std::uint64_t{0U}, std::uint64_t{0U}, 64U, 64U, 0U);
    packet(batch, command::target_set_root, 4U, 1U);
    packet(batch, command::channel_create_resource, 5U, visual_brush ? 82U : drawing_brush ? 81U : 80U);
    if (vector_source) {
        packet(batch, command::channel_create_resource, 8U, 69U);
        packet(batch, command::rectangle_geometry, 8U,
            0.0, 0.0, 10.0, 20.0, 20.0, 10.0, 0U, 0U, 0U, 0U);
        packet(batch, command::channel_create_resource, 9U, 75U);
        packet(batch, command::solid_color_brush, 9U, 1.0,
            progpu_native_color{1.0F, 0.0F, 0.0F, 1.0F}, 0U, 0U, 0U, 0U);
        if (!drawing_brush) packet(batch, command::channel_create_resource, 10U, 87U);
        packet(batch, command::geometry_drawing, drawing_brush ? 3U : 10U,
            options.source_cycle ? 5U : 9U, 0U, 8U);
        if (visual_brush) {
            packet(batch, command::visual_create, 3U);
            packet(batch, command::channel_create_resource, 12U, 43U);
            std::vector<std::byte> source_commands;
            packet(source_commands, command::draw_drawing, 10U, 0U);
            append(batch, static_cast<std::uint32_t>(16U + source_commands.size()));
            append(batch, static_cast<std::uint32_t>(command::render_data));
            append(batch, 12U);
            append(batch, static_cast<std::uint32_t>(source_commands.size()));
            batch.insert(batch.end(), source_commands.begin(), source_commands.end());
            packet(batch, command::visual_set_content, 3U, 12U);
        } else if (!drawing_brush) packet(batch, command::drawing_image, 3U, 10U);
    }
    if (options.rotate || options.skew) {
        packet(batch, command::channel_create_resource, 6U, 66U);
        packet(batch, command::matrix_transform, 6U,
            options.skew ? 1.0 : 0.0, options.skew ? 0.0 : 1.0,
            options.skew ? 0.5 : -1.0, options.skew ? 1.0 : 0.0,
            options.skew ? -16.0 : 64.0, 0.0, 0U);
    }
    if (options.relative_scale) {
        packet(batch, command::channel_create_resource, 7U, 66U);
        packet(batch, command::matrix_transform, 7U,
            0.5, 0.0, 0.0, 0.5, 0.25, 0.25, 0U);
    }
    packet(batch, visual_brush ? command::visual_brush : drawing_brush ? command::drawing_brush : command::image_brush, 5U, options.opacity,
        std::array{0.0, 0.0, 1.0, 1.0}, options.viewbox, 0.707, 1.414,
        0U, options.rotate || options.skew ? 6U : 0U, options.relative_scale ? 7U : 0U,
        1U, options.viewbox_units, 0U, 0U, options.stretch,
        options.tile_mode, 1U, 1U, 0U, 3U);
    std::vector<std::byte> nested;
    packet(nested, command::draw_rectangle, 8.0, 8.0, 48.0, 48.0, 5U, 0U);
    append(batch, static_cast<std::uint32_t>(16U + nested.size()));
    append(batch, static_cast<std::uint32_t>(command::render_data));
    append(batch, 2U);
    append(batch, static_cast<std::uint32_t>(nested.size()));
    batch.insert(batch.end(), nested.begin(), nested.end());
    packet(batch, command::visual_set_content, 1U, 2U);
    progpu_native_mil_channel* raw = nullptr;
    if (progpu_native_mil_channel_create(&raw) != PROGPU_NATIVE_MIL_STATUS_SUCCESS)
        return false;
    mil_clip_channel channel(raw);
    if (progpu_native_mil_channel_apply(raw, batch.data(), batch.size(), nullptr)
        != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    if (visual_brush && progpu_native_mil_channel_set_visual_cache_bounds(raw,
        3U, 10.0, 20.0, 20.0, 10.0) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    constexpr std::array<std::uint8_t, 8U> pixels{255, 0, 0, 255, 0, 0, 255, 255};
    if (!vector_source && progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(raw,
        3U, 2U, 1U, 8U, pixels.data(), pixels.size(), options.dpi_x, options.dpi_y)
        != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    return serialize_mil_visual_clip_fixture(raw, scene_id, 1U, scene);
}

} // namespace progpu::native::tests

#pragma once

#include "progpu_native_mil_visual_clip_fixture.hpp"

namespace progpu::native::tests {

enum class mil_brush_fixture_source { bitmap, drawing_image, drawing, visual };
enum class mil_brush_fixture_shape { rectangle, ellipse, rounded_rectangle, path, group, combined, line, line_geometry, glyphs };

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
    mil_brush_fixture_shape shape{mil_brush_fixture_shape::rectangle};
    bool inherited_clip{};
    bool paint_transform{};
    std::span<const std::byte> path_figures{};
    std::array<double, 4U> viewport{0.0, 0.0, 1.0, 1.0};
    bool fant{};
    bool pen{};
    bool dashed{};
    std::uint32_t cap{PROGPU_NATIVE_STROKE_CAP_ROUND};
    std::uint32_t end_cap{4U};
    std::uint32_t dash_cap{4U};
    bool guidelines{};
    bool nested_group{};
    std::uint32_t combined_mode{3U};
    bool solid_pen{};
    bool fill_with_pen{};
    bool group_combined{};
    bool identical_combined_operands{};
    bool zero_length_line{};
    double dash_offset{0.25};
    std::array<double, 2U> fixed_extent{48.0, 48.0};
    std::uint32_t line_join{PROGPU_NATIVE_STROKE_JOIN_ROUND};
    bool collapsed_group{};
    bool gradient_pen{};
    bool multiple_guidelines{};
    bool static_guidelines{};
    std::span<const std::byte> glyph_commands{};
    std::span<const std::byte> glyph_font{};
    std::uint32_t glyph_style{};
    bool glyph_drawing{};
    std::span<const std::byte> glyph_brush_commands{};
    double target_dpi_scale_x{1.0};
    double target_dpi_scale_y{1.0};
    bool opacity_mask{};
    bool drawing_group_mask{};
    bool missing_group_bounds{};
    bool visual_mask{};
    bool cached_visual{};
    bool visual_effect{};
    bool visual_guidelines{};
    bool missing_visual_bounds{};
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
        1U, 0U, options.fant ? 2U : options.linear ? 1U : 3U, 0U, 0U, 0U);
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
        options.viewport, options.viewbox, 0.707, 1.414,
        0U, options.rotate || options.skew ? 6U : 0U, options.relative_scale ? 7U : 0U,
        1U, options.viewbox_units, 0U, 0U, options.stretch,
        options.tile_mode, 1U, 1U, 0U, 3U);
    if (options.visual_mask) {
        packet(batch, command::visual_set_alpha_mask, 1U, 5U);
        packet(batch, command::visual_set_alpha, 1U, 0.75);
        if (options.cached_visual) {
            packet(batch, command::channel_create_resource, 34U, 94U);
            packet(batch, command::bitmap_cache, 34U, 1.5, 0U, 0U, 0U);
            packet(batch, command::visual_set_cache_mode, 1U, 34U);
        }
        if (options.visual_effect) {
            packet(batch, command::channel_create_resource, 35U, 36U);
            packet(batch, command::blur_effect, 35U, 3.0, 0U, 0U, 1U);
            packet(batch, command::visual_set_effect, 1U, 35U);
        }
        if (options.visual_guidelines) {
            if (options.multiple_guidelines)
                packet(batch, command::visual_set_guideline_collection, 1U,
                    std::uint16_t{0U}, std::uint16_t{0U}, std::uint16_t{2U}, std::uint16_t{0U}, 8.25F, 56.75F);
            else packet(batch, command::visual_set_guideline_collection, 1U,
                std::uint16_t{0U}, std::uint16_t{0U}, std::uint16_t{1U}, std::uint16_t{0U}, 8.25F);
        }
    }
    if (options.pen) {
        if (options.gradient_pen) {
            packet(batch, command::channel_create_resource, 19U, 77U);
            packet(batch, command::linear_gradient_brush, 19U, 0.5,
                0.0, 0.0, 1.0, 1.0, 0U, 0U, 0U, 0U, 1U, 0U, 48U, 0U, 0U,
                0.0, progpu_native_color{1.0F, 0.0F, 0.0F, 1.0F},
                1.0, progpu_native_color{0.0F, 0.0F, 1.0F, 1.0F});
        } else if (options.solid_pen) {
            packet(batch, command::channel_create_resource, 19U, 75U);
            packet(batch, command::solid_color_brush, 19U, 1.0,
                progpu_native_color{0.0F, 1.0F, 0.0F, 1.0F}, 0U, 0U, 0U, 0U);
        }
        packet(batch, command::channel_create_resource, 20U, 85U);
        if (options.dashed) {
            packet(batch, command::channel_create_resource, 21U, 84U);
            packet(batch, command::dash_style, 21U, options.dash_offset, 0U, 16U, 2.0, 1.0);
        }
        packet(batch, command::pen, 20U, 4.0, 10.0, options.solid_pen || options.gradient_pen ? 19U : 5U, 0U,
            options.cap, options.end_cap < 4U ? options.end_cap : options.cap,
            options.dash_cap < 4U ? options.dash_cap : options.cap,
            options.line_join, options.dashed ? 21U : 0U);
    }
    const std::uint32_t fill_handle = options.pen && !options.fill_with_pen ? 0U : 5U;
    const std::uint32_t pen_handle = options.pen ? 20U : 0U;
    std::vector<std::byte> nested;
    if (options.guidelines) {
        if (options.multiple_guidelines || options.static_guidelines) {
            packet(batch, command::channel_create_resource, 27U, 92U);
            if (options.multiple_guidelines)
                packet(batch, command::guideline_set, 27U, 0U, 16U, 0U, 0.25, 32.25);
            else
                packet(batch, command::guideline_set, 27U, 0U, 8U, 0U, 0.25);
            packet(nested, command::push_guideline_set, 27U, 0U);
        } else packet(nested, command::push_guideline_y1, 0.25);
    }
    if (options.inherited_clip) {
        packet(batch, command::channel_create_resource, 13U, 70U);
        packet(batch, command::ellipse_geometry, 13U,
            24.0, 24.0, 32.0, 32.0, 0U, 0U, 0U, 0U);
        if (options.visual_mask) packet(batch, command::visual_set_clip, 1U, 13U);
        else packet(nested, command::push_clip, 13U, 0U);
    }
    if (options.paint_transform) {
        packet(batch, command::channel_create_resource, 14U, 66U);
        packet(batch, command::matrix_transform, 14U,
            0.8, 0.2, -0.1, 0.8, 8.0, 0.0, 0U);
        if (options.visual_mask) packet(batch, command::visual_set_transform, 1U, 14U);
        else packet(nested, command::push_transform, 14U, 0U);
    }
    if (options.opacity_mask && !options.drawing_group_mask && !options.visual_mask)
        packet(nested, command::push_opacity_mask, 8.0F, 8.0F, 56.0F, 56.0F, 5U, 0U);
    if (options.shape == mil_brush_fixture_shape::path ||
        options.shape == mil_brush_fixture_shape::group ||
        options.shape == mil_brush_fixture_shape::combined) {
        packet(batch, command::channel_create_resource, 16U, 66U);
        packet(batch, command::matrix_transform, 16U,
            0.9, 0.0, 0.0, 0.8, 3.0, 5.0, 0U);
        if (options.shape == mil_brush_fixture_shape::path) {
            packet(batch, command::channel_create_resource, 15U, 73U);
            append(batch, static_cast<std::uint32_t>(24U + options.path_figures.size()));
            append(batch, static_cast<std::uint32_t>(command::path_geometry));
            append(batch, 15U);
            append(batch, 16U);
            append(batch, 0U);
            append(batch, static_cast<std::uint32_t>(options.path_figures.size()));
            batch.insert(batch.end(), options.path_figures.begin(), options.path_figures.end());
        } else {
            packet(batch, command::channel_create_resource, 17U, 69U);
            packet(batch, command::rectangle_geometry, 17U,
                0.0, 0.0, 8.0, 8.0,
                options.collapsed_group ? options.fixed_extent[0] : 48.0,
                options.collapsed_group ? options.fixed_extent[1] : 48.0, 0U, 0U, 0U, 0U);
            packet(batch, command::channel_create_resource, 18U, 70U);
            packet(batch, command::ellipse_geometry, 18U,
                options.collapsed_group ? options.fixed_extent[0] * 0.5 : 12.0,
                options.collapsed_group ? options.fixed_extent[1] * 0.5 : 18.0,
                32.0, 32.0, 0U, 0U, 0U, 0U);
            packet(batch, command::channel_create_resource, 15U,
                options.shape == mil_brush_fixture_shape::group ? 71U : 72U);
            if (options.shape == mil_brush_fixture_shape::group) {
                if (options.group_combined) {
                    packet(batch, command::channel_create_resource, 26U, 72U);
                    packet(batch, command::combined_geometry, 26U, 16U, options.combined_mode,
                        17U, options.identical_combined_operands ? 17U : 18U);
                }
                if (options.nested_group) {
                    packet(batch, command::channel_create_resource, 25U, 66U);
                    packet(batch, command::matrix_transform, 25U, 1.4, 0.2, -0.1, 0.7, 2.0, 3.0, 0U);
                    packet(batch, command::channel_create_resource, 23U, 68U);
                    packet(batch, command::line_geometry, 23U, 8.0, 16.0, 56.0, 48.0, 0U, 0U, 0U);
                    packet(batch, command::channel_create_resource, 24U, 73U);
                    append(batch, static_cast<std::uint32_t>(24U + options.path_figures.size()));
                    append(batch, static_cast<std::uint32_t>(command::path_geometry));
                    append(batch, 24U);
                    append(batch, 16U);
                    append(batch, 0U);
                    append(batch, static_cast<std::uint32_t>(options.path_figures.size()));
                    batch.insert(batch.end(), options.path_figures.begin(), options.path_figures.end());
                    packet(batch, command::channel_create_resource, 22U, 71U);
                    packet(batch, command::geometry_group, 22U, 25U, 0U, 12U,
                        options.group_combined ? 26U : 18U, 23U, 24U);
                }
                packet(batch, command::geometry_group, 15U, 16U, 0U, 8U,
                    options.group_combined ? 26U : 17U, options.nested_group ? 22U : 18U);
            } else {
                packet(batch, command::combined_geometry, 15U, 16U, options.combined_mode, 17U, 18U);
            }
        }
    }
    if (options.opacity_mask) {
        packet(batch, command::channel_create_resource, 30U, 75U);
        packet(batch, command::solid_color_brush, 30U, 1.0,
            progpu_native_color{0.0F, 1.0F, 0.0F, 1.0F}, 0U, 0U, 0U, 0U);
        if (options.drawing_group_mask) {
            packet(batch, command::channel_create_resource, 31U, 69U);
            packet(batch, command::rectangle_geometry, 31U,
                0.0, 0.0, 8.0, 8.0, 48.0, 48.0, 0U, 0U, 0U, 0U);
            packet(batch, command::channel_create_resource, 32U, 87U);
            packet(batch, command::geometry_drawing, 32U, 30U, 0U, 31U);
            packet(batch, command::channel_create_resource, 33U, 91U);
            packet(batch, command::drawing_group, 33U, 0.75, 4U,
                0U, 0U, 5U, 0U, 0U, 0U, 0U, 0U, 32U);
            packet(nested, command::draw_drawing, 33U, 0U);
        } else packet(nested, command::draw_rectangle, 8.0, 8.0, 48.0, 48.0, 30U, 0U);
    } else switch (options.shape) {
    case mil_brush_fixture_shape::glyphs:
    {
        batch.insert(batch.end(), options.glyph_commands.begin(), options.glyph_commands.end());
        batch.insert(batch.end(), options.glyph_brush_commands.begin(), options.glyph_brush_commands.end());
        const std::uint32_t foreground = options.glyph_brush_commands.empty() ? 5U : 19U;
        if (options.glyph_drawing) {
            packet(batch, command::channel_create_resource, 29U, 88U);
            packet(batch, command::glyph_run_drawing, 29U, 28U, foreground);
            packet(nested, command::draw_drawing, 29U, 0U);
        } else packet(nested, command::draw_glyph_run, foreground, 28U);
        break;
    }
    case mil_brush_fixture_shape::line_geometry:
        packet(batch, command::channel_create_resource, 15U, 68U);
        packet(batch, command::line_geometry, 15U, 8.0, 16.0,
            options.zero_length_line ? 8.0 : 56.0, options.zero_length_line ? 16.0 : 48.0, 0U, 0U, 0U);
        packet(nested, command::draw_geometry, fill_handle, pen_handle, 15U, 0U);
        break;
    case mil_brush_fixture_shape::line:
        packet(nested, command::draw_line, 8.0, 16.0,
            options.zero_length_line ? 8.0 : 56.0, options.zero_length_line ? 16.0 : 48.0, pen_handle, 0U);
        break;
    case mil_brush_fixture_shape::path:
    case mil_brush_fixture_shape::group:
    case mil_brush_fixture_shape::combined:
        packet(nested, command::draw_geometry, fill_handle, pen_handle, 15U, 0U);
        break;
    case mil_brush_fixture_shape::ellipse:
        packet(nested, command::draw_ellipse, 32.0, 32.0,
            options.fixed_extent[0] * 0.5, options.fixed_extent[1] * 0.5, fill_handle, pen_handle);
        break;
    case mil_brush_fixture_shape::rounded_rectangle:
        packet(nested, command::draw_rounded_rectangle,
            8.0, 8.0, options.fixed_extent[0], options.fixed_extent[1], 12.0, 6.0, fill_handle, pen_handle);
        break;
    default:
        packet(nested, command::draw_rectangle, 8.0, 8.0,
            options.fixed_extent[0], options.fixed_extent[1], fill_handle, pen_handle);
        break;
    }
    if (options.opacity_mask && !options.drawing_group_mask && !options.visual_mask) packet(nested, command::pop);
    if (options.paint_transform && !options.visual_mask) packet(nested, command::pop);
    if (options.inherited_clip && !options.visual_mask) packet(nested, command::pop);
    if (options.guidelines) packet(nested, command::pop);
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
    if (options.visual_mask && !options.missing_visual_bounds &&
        progpu_native_mil_channel_set_visual_cache_bounds(raw, 1U, 8.0, 8.0, 48.0, 48.0)
            != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    if (options.opacity_mask && options.drawing_group_mask && !options.missing_group_bounds &&
        progpu_native_mil_channel_set_drawing_group_bounds(raw, 33U, 8.0, 8.0, 48.0, 48.0)
            != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    if (!options.glyph_font.empty() && progpu_native_mil_channel_set_glyph_run_font_sfnt(raw,
            28U, 0U, options.glyph_style, options.glyph_font.data(), options.glyph_font.size())
        != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    if (visual_brush && progpu_native_mil_channel_set_visual_cache_bounds(raw,
        3U, 10.0, 20.0, 20.0, 10.0) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    constexpr std::array<std::uint8_t, 8U> pixels{255, 0, 0, 255, 0, 0, 255, 255};
    if (!vector_source && progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(raw,
        3U, 2U, 1U, 8U, pixels.data(), pixels.size(), options.dpi_x, options.dpi_y)
        != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    // Repeated pages and visual sources require the stateful frame contract.
    // Keep the request identical between sizing and copy, including its serial.
    const progpu_native_mil_scene_build_request request{
        sizeof(progpu_native_mil_scene_build_request), 0U, 4U, 0U,
        scene_id, 1U, options.target_dpi_scale_x, options.target_dpi_scale_y, 0U, 1U};
    progpu_native_mil_scene_build_result result{};
    result.struct_size = sizeof(result);
    std::size_t written = 0U;
    if (progpu_native_mil_channel_build_scene_with_request(raw, &request,
            nullptr, 0U, &written, nullptr, &result) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    scene.resize(written);
    return progpu_native_mil_channel_build_scene_with_request(raw, &request,
        scene.data(), scene.size(), &written, nullptr, &result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
        written == scene.size();
}

} // namespace progpu::native::tests

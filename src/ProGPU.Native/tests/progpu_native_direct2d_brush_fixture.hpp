#pragma once

#include "progpu_native_direct2d_clip_fixture.hpp"

namespace progpu::native::direct2d::tests {

// Independent source values for matched portable and Windows recording. Each
// case changes one public brush property between draws A, B, B, A.
struct brush_fixture_state {
    std::array<float, 4U> color{0.25F, 0.5F, 0.75F, 0.875F};
    float opacity = 0.75F;
    std::array<float, 6U> transform{1, 0, 0, 1, 0, 0};
    std::array<float, 2U> first{8, 10};
    std::array<float, 2U> second{1, 2};
    float radius_x = 20;
    float radius_y = 12;
};

inline brush_fixture_state changed_brush_state(std::uint32_t kind, unsigned mutation)
{
    brush_fixture_state state;
    switch (mutation) {
    case 0U: state.opacity = 0.375F; break;
    case 1U:
        if (kind == PROGPU_NATIVE_SCENE_BRUSH_SOLID) state.color = {0.75F, 0.25F, 0.5F, 0.625F};
        else state.transform = {2, 0, 0, 4, 6, 8};
        break;
    case 2U: state.first = {9, 11}; break;
    case 3U: state.second = {3, 4}; break;
    case 4U: state.radius_x = 22; break;
    case 5U: state.radius_y = 14; break;
    }
    return state;
}

inline bool brush_value_contract(const progpu_native_scene_brush& value,
    std::uint32_t kind, const brush_fixture_state& state)
{
    progpu_native_scene_brush expected{};
    expected.type = kind;
    expected.opacity = state.opacity;
    expected.coordinate_transform0[0] = 1;
    expected.coordinate_transform1[1] = 1;
    if (kind == PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
        expected.colors[0] = {state.color[0], state.color[1], state.color[2], state.color[3]};
    } else {
        expected.stop_count = 2U;
        expected.spread_method = PROGPU_NATIVE_SCENE_GRADIENT_PAD;
        expected.color_interpolation_mode = PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB;
        expected.colors[0] = {1, 0, 0, 1};
        expected.colors[1] = {0, 0, 1, 1};
        expected.offsets0[1] = 1;
        expected.coordinate_transform0[0] = 1 / state.transform[0];
        expected.coordinate_transform0[2] = -state.transform[4] / state.transform[0];
        expected.coordinate_transform1[1] = 1 / state.transform[3];
        expected.coordinate_transform1[2] = -state.transform[5] / state.transform[3];
        if (kind == PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT) {
            expected.start_point = {state.first[0], state.first[1]};
            expected.end_point = {state.second[0], state.second[1]};
        } else {
            expected.start_point = {state.first[0] + state.second[0], state.first[1] + state.second[1]};
            expected.center = {state.first[0], state.first[1]};
            expected.radius = state.radius_x;
            expected.radius_y = state.radius_y;
        }
    }
    if (value.type != expected.type || value.opacity != expected.opacity ||
        value.start_point.x != expected.start_point.x || value.start_point.y != expected.start_point.y ||
        value.end_point.x != expected.end_point.x || value.end_point.y != expected.end_point.y ||
        value.center.x != expected.center.x || value.center.y != expected.center.y ||
        value.radius != expected.radius || value.radius_y != expected.radius_y ||
        value.stop_count != expected.stop_count || value.spread_method != expected.spread_method ||
        value.color_interpolation_mode != expected.color_interpolation_mode ||
        value.reserved0 != 0U || value.reserved1 != 0U) return false;
    for (std::size_t index = 0U; index < std::size(value.colors); ++index) {
        const auto& actual_color = value.colors[index];
        const auto& expected_color = expected.colors[index];
        if (actual_color.r != expected_color.r || actual_color.g != expected_color.g ||
            actual_color.b != expected_color.b || actual_color.a != expected_color.a) return false;
    }
    return std::equal(std::begin(value.offsets0), std::end(value.offsets0), std::begin(expected.offsets0)) &&
        std::equal(std::begin(value.offsets1), std::end(value.offsets1), std::begin(expected.offsets1)) &&
        std::equal(std::begin(value.coordinate_transform0), std::end(value.coordinate_transform0),
            std::begin(expected.coordinate_transform0)) &&
        std::equal(std::begin(value.coordinate_transform1), std::end(value.coordinate_transform1),
            std::begin(expected.coordinate_transform1));
}

inline bool mutable_brush_contract(std::span<const std::byte> bytes,
    std::uint32_t kind, unsigned mutation, bool require_cache_reuse)
{
    progpu_native_scene_header header{};
    if (!read_scene_value(bytes, 0U, header) || header.command_count != 4U) return false;
    const auto changed = changed_brush_state(kind, mutation);
    std::array<std::uint32_t, 4U> indices{};
    for (std::size_t index = 0U; index < indices.size(); ++index) {
        progpu_native_scene_command command{};
        progpu_native_scene_draw_brushes draw{};
        progpu_native_scene_resource table{};
        progpu_native_scene_brush brush{};
        if (!read_scene_value(bytes, header.command_offset + index * header.command_stride, command) ||
            command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
            !read_scene_value(bytes, command.payload_offset, draw) || draw.brush_count != 1U ||
            draw.brush_resource_index >= header.resource_count ||
            !read_scene_value(bytes, command.payload_offset + sizeof(draw), indices[index]) ||
            !read_scene_value(bytes, header.resource_offset +
                std::uint64_t{draw.brush_resource_index} * header.resource_stride, table) ||
            table.kind != PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE ||
            (std::uint64_t{indices[index]} + 1U) * sizeof(brush) > table.payload_size ||
            !read_scene_value(bytes, table.payload_offset + std::uint64_t{indices[index]} * sizeof(brush), brush) ||
            !brush_value_contract(brush, kind, index == 1U || index == 2U ? changed : brush_fixture_state{})) return false;
        if (require_cache_reuse && table.payload_size != 2U * sizeof(brush)) return false;
        if (kind != PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
            if ((std::uint64_t{brush.stop_offset} + 2U) * sizeof(progpu_native_scene_gradient_stop) >
                table.auxiliary_size) return false;
            for (std::uint32_t stop_index = 0U; stop_index < 2U; ++stop_index) {
                progpu_native_scene_gradient_stop stop{};
                if (!read_scene_value(bytes, table.auxiliary_offset +
                        (std::uint64_t{brush.stop_offset} + stop_index) * sizeof(stop), stop) ||
                    stop.offset != static_cast<float>(stop_index) || stop.color.r != (stop_index == 0U ? 1 : 0) ||
                    stop.color.g != 0 || stop.color.b != (stop_index == 1U ? 1 : 0) || stop.color.a != 1 ||
                    stop.reserved0 != 0U || stop.reserved1 != 0U || stop.reserved2 != 0U) return false;
            }
        }
    }
    return !require_cache_reuse || (indices[0] == indices[3] && indices[1] == indices[2] && indices[0] != indices[1]);
}

} // namespace progpu::native::direct2d::tests

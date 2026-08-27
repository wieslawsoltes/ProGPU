#include "progpu_native_semantic_brush_tests.hpp"

#include "progpu_native.h"
#include "progpu_native_semantic_brush.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>

namespace progpu::native::tests {

bool semantic_perlin_brush_table_is_exact_and_bounded() {
    constexpr std::uint32_t resource_offset = 80U;
    constexpr std::uint32_t command_offset =
        resource_offset + sizeof(progpu_native_scene_resource);
    constexpr std::uint32_t brush_offset = 256U;
    constexpr std::uint32_t table_offset =
        brush_offset + sizeof(progpu_native_scene_brush);
    constexpr std::uint32_t draw_offset = table_offset +
        PROGPU_NATIVE_SCENE_PERLIN_TABLE_RECORDS *
            sizeof(progpu_native_scene_gradient_stop);
    std::vector<std::byte> bytes(
        draw_offset + sizeof(progpu_native_scene_draw_brushes) +
            sizeof(std::uint32_t));

    progpu_native_scene_header header{};
    header.resource_offset = resource_offset;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.resource_count = 1U;
    header.command_offset = command_offset;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.command_count = 1U;

    progpu_native_scene_resource resource{};
    resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE;
    resource.payload_offset = brush_offset;
    resource.payload_size = sizeof(progpu_native_scene_brush);
    resource.auxiliary_offset = table_offset;
    resource.auxiliary_size =
        PROGPU_NATIVE_SCENE_PERLIN_TABLE_RECORDS *
        sizeof(progpu_native_scene_gradient_stop);
    std::memcpy(
        bytes.data() + resource_offset,
        &resource,
        sizeof(resource));

    progpu_native_scene_brush brush{};
    brush.type = PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE;
    brush.opacity = 0.75F;
    brush.start_point = {0.08F, 0.12F};
    brush.end_point = {16.0F, 16.0F};
    brush.center = {128.0F, 128.0F};
    brush.radius = 17.0F;
    brush.stop_count = 3U;
    brush.spread_method = 1U;
    brush.color_interpolation_mode =
        PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SCRGB;
    brush.coordinate_transform0[0] = 0.5F;
    brush.coordinate_transform0[2] = 2.0F;
    brush.coordinate_transform1[1] = 0.75F;
    brush.coordinate_transform1[2] = -1.0F;
    std::memcpy(bytes.data() + brush_offset, &brush, sizeof(brush));

    for (std::uint32_t index = 0U;
         index < PROGPU_NATIVE_SCENE_PERLIN_TABLE_RECORDS;
         ++index) {
        const progpu_native_scene_gradient_stop record{
            {static_cast<float>(index & 1U),
                static_cast<float>((index >> 1U) & 1U),
                -0.5F,
                0.5F},
            static_cast<float>((index * 73U) & 255U),
            0U,
            0U,
            0U};
        std::memcpy(
            bytes.data() + table_offset +
                index * sizeof(record),
            &record,
            sizeof(record));
    }

    progpu_native_scene_command command{};
    command.kind = PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC;
    command.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    command.payload_offset = draw_offset;
    command.payload_size = sizeof(progpu_native_scene_draw_brushes) +
        sizeof(std::uint32_t);
    std::memcpy(
        bytes.data() + command_offset,
        &command,
        sizeof(command));
    const progpu_native_scene_draw_brushes draw{
        sizeof(progpu_native_scene_draw_brushes), 0U, 1U, 0U};
    std::memcpy(bytes.data() + draw_offset, &draw, sizeof(draw));
    constexpr std::uint32_t brush_index = 0U;
    std::memcpy(
        bytes.data() + draw_offset + sizeof(draw),
        &brush_index,
        sizeof(brush_index));

    std::uint32_t error_offset = 0U;
    if (!semantic::validate_brush_table(
            bytes.data(), resource, error_offset)) {
        return false;
    }
    semantic::semantic_brush_page page{};
    if (!semantic::compile_brush_page(
            bytes.data(), header, 0x1234U, page) ||
        page.brushes.size() != 2U ||
        page.gradient_stops.size() !=
            PROGPU_NATIVE_SCENE_PERLIN_TABLE_RECORDS + 1U ||
        page.brushes[1].type != PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE ||
        page.brushes[1].stop_count != 3U ||
        page.brushes[1].stop_offset != 1U) {
        return false;
    }

    auto hatch = brush;
    hatch.type = PROGPU_NATIVE_SCENE_BRUSH_CROSS_HATCH;
    hatch.center = {8.0F, 1.5F};
    hatch.stop_count = 0U;
    hatch.stop_offset = 0U;
    hatch.spread_method = 0U;
    hatch.color_interpolation_mode =
        PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB;
    std::memcpy(bytes.data() + brush_offset, &hatch, sizeof(hatch));
    if (!semantic::validate_brush_table(
            bytes.data(), resource, error_offset)) {
        return false;
    }
    hatch.center.x = 0.0F;
    std::memcpy(bytes.data() + brush_offset, &hatch, sizeof(hatch));
    if (semantic::validate_brush_table(
            bytes.data(), resource, error_offset)) {
        return false;
    }

    auto pad_outside = brush;
    pad_outside.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    pad_outside.stop_count = 2U;
    pad_outside.stop_offset = 0U;
    pad_outside.spread_method =
        static_cast<std::uint32_t>(PROGPU_NATIVE_SCENE_GRADIENT_PAD) |
        PROGPU_NATIVE_SCENE_GRADIENT_PAD_OUTSIDE_COLORS;
    pad_outside.color_interpolation_mode =
        PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB;
    pad_outside.colors[0] = {1.0F, 0.0F, 0.0F, 1.0F};
    pad_outside.colors[1] = {0.0F, 0.0F, 1.0F, 1.0F};
    std::memcpy(
        bytes.data() + brush_offset,
        &pad_outside,
        sizeof(pad_outside));
    if (!semantic::validate_brush_table(
            bytes.data(), resource, error_offset)) {
        return false;
    }
    pad_outside.spread_method =
        static_cast<std::uint32_t>(PROGPU_NATIVE_SCENE_GRADIENT_REFLECT) |
        PROGPU_NATIVE_SCENE_GRADIENT_PAD_OUTSIDE_COLORS;
    std::memcpy(
        bytes.data() + brush_offset,
        &pad_outside,
        sizeof(pad_outside));
    if (semantic::validate_brush_table(
            bytes.data(), resource, error_offset)) {
        return false;
    }
    std::memcpy(bytes.data() + brush_offset, &brush, sizeof(brush));

    auto truncated = resource;
    truncated.auxiliary_size -=
        sizeof(progpu_native_scene_gradient_stop);
    if (semantic::validate_brush_table(
            bytes.data(), truncated, error_offset)) {
        return false;
    }
    brush.stop_count = PROGPU_NATIVE_SCENE_MAX_PERLIN_OCTAVES + 1U;
    std::memcpy(bytes.data() + brush_offset, &brush, sizeof(brush));
    return !semantic::validate_brush_table(
        bytes.data(), resource, error_offset);
}

} // namespace progpu::native::tests

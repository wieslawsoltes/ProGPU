#pragma once

#include "progpu_native_direct2d_brush_fixture.hpp"

namespace progpu::native::direct2d::tests {

// One surviving aliased rectangle under the unchanged source transform. No
// pre-clear command, brush, clip or layer resource may survive replacement.
inline bool full_clear_suffix_contract(std::span<const std::byte> bytes)
{
    progpu_native_scene_header header{};
    progpu_native_scene_command command{};
    progpu_native_scene_resource geometry{}, table{};
    progpu_native_analytic_primitive primitive{};
    progpu_native_scene_draw_brushes draw{};
    progpu_native_scene_brush brush{};
    std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!read_scene_value(bytes, 0U, header) || header.command_count != 1U || header.resource_count != 2U ||
        !read_scene_value(bytes, header.command_offset, command) ||
        command.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
        command.state_index != PROGPU_NATIVE_SCENE_NO_INDEX || command.resource_index >= header.resource_count ||
        command.bounds_x != 7 || command.bounds_y != 13 || command.bounds_width != 16 || command.bounds_height != 24 ||
        !read_scene_value(bytes, header.resource_offset + std::uint64_t{command.resource_index} * header.resource_stride, geometry) ||
        geometry.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH || geometry.payload_size != sizeof(primitive) ||
        !read_scene_value(bytes, geometry.payload_offset, primitive) ||
        primitive.kind != PROGPU_NATIVE_PRIMITIVE_RECTANGLE || primitive.flags != PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED ||
        primitive.x != 1 || primitive.y != 2 || primitive.width != 8 || primitive.height != 8 || primitive.stroke_thickness != 0 ||
        primitive.transform.m11 != 2 || primitive.transform.m12 != 0 || primitive.transform.m21 != 0 ||
        primitive.transform.m22 != 3 || primitive.transform.m31 != 5 || primitive.transform.m32 != 7 ||
        !read_scene_value(bytes, command.payload_offset, draw) || draw.brush_count != 1U ||
        draw.brush_resource_index >= header.resource_count ||
        !read_scene_value(bytes, command.payload_offset + sizeof(draw), brush_index) || brush_index != 0U ||
        !read_scene_value(bytes, header.resource_offset + std::uint64_t{draw.brush_resource_index} * header.resource_stride, table) ||
        table.kind != PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE || table.payload_size != sizeof(brush) || table.auxiliary_size != 0U ||
        !read_scene_value(bytes, table.payload_offset, brush)) return false;
    return brush_value_contract(brush, PROGPU_NATIVE_SCENE_BRUSH_SOLID, brush_fixture_state{});
}

inline bool full_clear_empty_contract(std::span<const std::byte> bytes)
{
    progpu_native_scene_header header{};
    return read_scene_value(bytes, 0U, header) && header.command_count == 0U && header.resource_count == 0U;
}

} // namespace progpu::native::direct2d::tests

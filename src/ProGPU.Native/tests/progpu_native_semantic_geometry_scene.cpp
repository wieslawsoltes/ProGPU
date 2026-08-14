#include "progpu_native_semantic_geometry_scene.hpp"

#include "progpu_native_dawn.h"

#include <array>
#include <cstring>

namespace progpu::native::tests {
namespace {

template<typename T>
std::uint32_t append(
    std::vector<std::byte>& stream,
    const T* values,
    std::size_t count) {
    stream.resize((stream.size() + 7U) & ~7U);
    const auto offset = static_cast<std::uint32_t>(stream.size());
    const auto* first = reinterpret_cast<const std::byte*>(values);
    stream.insert(stream.end(), first, first + sizeof(T) * count);
    return offset;
}

} // namespace

std::vector<std::byte> create_semantic_geometry_scene_stream(
    std::uint32_t target_width,
    std::uint32_t target_height) {
    constexpr std::uint32_t command_count = 1U;
    constexpr std::uint32_t resource_count = 3U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);

    const progpu_native_affine_2d identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const std::array geometry{
        progpu_native_geometry_primitive{
            PROGPU_NATIVE_GEOMETRY_LINE,
            0U,
            {target_width * 0.125F, target_height * 0.5F},
            {target_width * 0.875F, target_height * 0.5F},
            {},
            {},
            8.0F,
            0.0F,
            {1.0F, 0.0F, 1.0F, 1.0F},
            identity},
        progpu_native_geometry_primitive{
            PROGPU_NATIVE_GEOMETRY_DOT_GRID,
            0U,
            {target_width * 0.125F, target_height * 0.625F},
            {target_width * 0.75F, target_height * 0.25F},
            {8.0F, 8.0F},
            {16.0F, 3.0F},
            0.0F,
            0.0F,
            {1.0F, 1.0F, 1.0F, 1.0F},
            identity}
    };
    const std::uint32_t geometry_offset = append(
        stream,
        geometry.data(),
        geometry.size());

    progpu_native_scene_brush brush{};
    brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
    brush.opacity = 0.8F;
    brush.colors[0] = {1.0F, 0.45F, 0.08F, 1.0F};
    brush.coordinate_transform0[0] = 1.0F;
    brush.coordinate_transform1[1] = 1.0F;
    const std::uint32_t brush_offset = append(stream, &brush, 1U);

    progpu_native_scene_state state{};
    state.struct_size = sizeof(state);
    state.transform = identity;
    state.opacity = 0.75F;
    const std::uint32_t state_offset = append(stream, &state, 1U);

    const progpu_native_scene_draw_brushes draw_brushes{
        sizeof(progpu_native_scene_draw_brushes),
        1U,
        static_cast<std::uint32_t>(geometry.size()),
        0U};
    const std::uint32_t draw_offset = append(stream, &draw_brushes, 1U);
    constexpr std::array<std::uint32_t, 2U> brush_indices{0U, 0U};
    append(stream, brush_indices.data(), brush_indices.size());

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 101U;
    header.generation = 1U;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = resource_count;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = header.total_size - arena_offset;
    std::memcpy(stream.data(), &header, sizeof(header));

    const std::array<progpu_native_scene_resource, resource_count> resources{{
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 1101U, 1U,
            geometry_offset,
            static_cast<std::uint32_t>(sizeof(geometry)), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 1102U, 1U,
            brush_offset, sizeof(brush), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_STATE,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 1103U, 1U,
            state_offset, sizeof(state), 0U, 0U}
    }};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

    const progpu_native_scene_command command{
        sizeof(progpu_native_scene_command),
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY,
        PROGPU_NATIVE_SCENE_RECORD_REQUIRED,
        0U,
        1111U,
        2U,
        0U,
        draw_offset,
        sizeof(draw_brushes) + sizeof(brush_indices),
        0.0F,
        0.0F,
        static_cast<float>(target_width),
        static_cast<float>(target_height),
        0U,
        0U};
    std::memcpy(stream.data() + command_offset, &command, sizeof(command));
    return stream;
}

} // namespace progpu::native::tests

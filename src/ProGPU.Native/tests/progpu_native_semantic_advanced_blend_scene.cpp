#include "progpu_native_semantic_advanced_blend_scene.hpp"

#include "progpu_native.h"

#include <array>
#include <cstring>

namespace progpu::native::tests {
namespace {

template<typename T>
std::uint32_t append_scene_payload(
    std::vector<std::byte>& stream,
    const T& value) {
    const std::size_t aligned_size = (stream.size() + 7U) & ~7U;
    stream.resize(aligned_size);
    const auto offset = static_cast<std::uint32_t>(stream.size());
    const auto* source = reinterpret_cast<const std::byte*>(&value);
    stream.insert(stream.end(), source, source + sizeof(value));
    return offset;
}

} // namespace

std::vector<std::byte> create_semantic_advanced_blend_scene_stream(
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t blend_mode) {
    constexpr std::uint32_t command_count = 4U;
    constexpr std::uint32_t resource_count = 2U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);

    const float target_width = static_cast<float>(width);
    const float target_height = static_cast<float>(height);
    const float destination_x = target_width / 16.0F;
    const float destination_y = target_height / 12.0F;
    const float destination_width = target_width * 5.0F / 8.0F;
    const float destination_height = target_height * 2.0F / 3.0F;
    const float source_x = target_width * 3.0F / 16.0F;
    const float source_y = target_height / 4.0F;
    const float source_width = target_width * 3.0F / 8.0F;
    const float source_height = target_height / 3.0F;
    constexpr progpu_native_affine_2d identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const progpu_native_analytic_primitive destination{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
        destination_x, destination_y,
        destination_width, destination_height,
        0.0F, 0.0F,
        {0.2F, 0.8F, 0.4F, 1.0F}, identity};
    const progpu_native_analytic_primitive source{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
        source_x, source_y, source_width, source_height,
        0.0F, 0.0F,
        {0.5F, 0.5F, 0.5F, 1.0F}, identity};
    const std::uint32_t destination_offset = append_scene_payload(
        stream,
        destination);
    const std::uint32_t source_offset = append_scene_payload(
        stream,
        source);
    const progpu_native_scene_layer layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {source_x, source_y, source_width, source_height},
        1.0F,
        blend_mode,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        101U,
        102U,
        0U,
        0U};
    const std::uint32_t layer_offset = append_scene_payload(stream, layer);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 97U;
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
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 971U, 1U,
            destination_offset, sizeof(destination), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 972U, 1U,
            source_offset, sizeof(source), 0U, 0U}
    }};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

    const std::array<progpu_native_scene_command, command_count> commands{{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 981U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, 0U,
            destination_x, destination_y,
            destination_width, destination_height, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 982U,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            layer_offset, sizeof(layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 983U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 1U, 0U, 0U,
            source_x, source_y, source_width, source_height, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 984U,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U}
    }};
    std::memcpy(
        stream.data() + command_offset,
        commands.data(),
        sizeof(commands));
    return stream;
}

} // namespace progpu::native::tests

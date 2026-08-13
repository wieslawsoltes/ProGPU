#include "progpu_native_semantic_backdrop_scene.hpp"

#include <array>
#include <cstring>

namespace progpu::native::tests {
namespace {

template<typename T>
std::uint32_t append_scene_payload(
    std::vector<std::byte>& stream,
    const T* values,
    std::size_t count) {
    const std::size_t aligned_size = (stream.size() + 7U) & ~7U;
    stream.resize(aligned_size);
    const auto offset = static_cast<std::uint32_t>(stream.size());
    const auto* source = reinterpret_cast<const std::byte*>(values);
    stream.insert(stream.end(), source, source + sizeof(T) * count);
    return offset;
}

} // namespace

std::vector<std::byte> create_semantic_backdrop_scene_stream(
    std::uint32_t width,
    std::uint32_t height) {
    constexpr std::uint32_t command_count = 6U;
    constexpr std::uint32_t resource_count = 3U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);

    const float target_width = static_cast<float>(width);
    const float target_height = static_cast<float>(height);
    const float split_x = target_width * 0.5F;
    const float layer_x = target_width * 0.25F;
    const float layer_y = target_height / 6.0F;
    const float layer_width = target_width * 0.5F;
    const float layer_height = target_height * 2.0F / 3.0F;
    const float marker_x = target_width * 11.0F / 32.0F;
    const float marker_y = target_height * 5.0F / 12.0F;
    const float marker_width = target_width / 8.0F;
    const float marker_height = target_height / 6.0F;
    constexpr progpu_native_affine_2d identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const std::array<progpu_native_analytic_primitive, 2U> backdrop{{
        {PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
            0.0F, 0.0F, split_x, target_height,
            0.0F, 0.0F, {1.0F, 0.0F, 0.0F, 1.0F}, identity},
        {PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
            split_x, 0.0F, target_width - split_x, target_height,
            0.0F, 0.0F, {0.0F, 0.0F, 1.0F, 1.0F}, identity}
    }};
    const progpu_native_analytic_primitive marker{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
        marker_x, marker_y, marker_width, marker_height,
        0.0F, 0.0F, {0.0F, 1.0F, 0.0F, 1.0F}, identity};
    const std::uint32_t backdrop_offset = append_scene_payload(
        stream,
        backdrop.data(),
        backdrop.size());
    const std::uint32_t marker_offset = append_scene_payload(
        stream,
        &marker,
        1U);

    const progpu_native_group_effect blur{
        sizeof(progpu_native_group_effect),
        PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR,
        0U,
        981U,
        2.0F,
        2.0F,
        0U,
        0U,
        0.0F,
        0.0F,
        0.0F,
        0.0F,
        0.0F,
        0.0F};
    const progpu_native_scene_effect_chain chain{
        sizeof(progpu_native_scene_effect_chain),
        1U,
        982U,
        0U};
    const std::uint32_t chain_offset = append_scene_payload(
        stream,
        &chain,
        1U);
    const std::uint32_t effect_offset = append_scene_payload(
        stream,
        &blur,
        1U);
    const progpu_native_scene_layer layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_BACKDROP |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {layer_x, layer_y, layer_width, layer_height},
        1.0F,
        PROGPU_NATIVE_BLEND_SRC_OVER,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        2U,
        983U,
        984U,
        0U,
        0U};
    const std::uint32_t layer_offset = append_scene_payload(
        stream,
        &layer,
        1U);
    const progpu_native_scene_layer initialize_previous_layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_BACKDROP |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        {target_width / 8.0F, target_height * 3.0F / 4.0F,
            target_width / 4.0F, target_height / 6.0F},
        1.0F,
        PROGPU_NATIVE_BLEND_SRC,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        992U,
        993U,
        0U,
        0U};
    const std::uint32_t initialize_previous_layer_offset =
        append_scene_payload(
            stream,
            &initialize_previous_layer,
            1U);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 98U;
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
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 985U, 1U,
            backdrop_offset, sizeof(backdrop), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 986U, 1U,
            marker_offset, sizeof(marker), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 987U, 1U,
            chain_offset, sizeof(chain), effect_offset, sizeof(blur)}
    }};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

    const std::array<progpu_native_scene_command, command_count> commands{{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 988U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, 0U,
            0.0F, 0.0F, target_width, target_height, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 989U,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            layer_offset, sizeof(layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 990U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 1U, 0U, 0U,
            marker_x, marker_y, marker_width, marker_height, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 991U,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 994U,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            initialize_previous_layer_offset,
            sizeof(initialize_previous_layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 995U,
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

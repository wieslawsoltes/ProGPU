#include "progpu_native_semantic_rounded_mask_scene.hpp"

#include "progpu_native_dawn.h"

#include <algorithm>
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

std::vector<std::byte> create_semantic_rounded_mask_scene_stream(
    std::uint32_t target_width,
    std::uint32_t target_height) {
    constexpr std::uint32_t command_count = 3U;
    constexpr std::uint32_t resource_count = 2U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    std::vector<std::byte> stream(arena_offset);

    const float scale_x = static_cast<float>(target_width) / 64.0F;
    const float scale_y = static_cast<float>(target_height) / 48.0F;
    const progpu_native_affine_2d identity{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    const progpu_native_analytic_primitive content{
        PROGPU_NATIVE_PRIMITIVE_RECTANGLE, 0U,
        8.0F * scale_x, 4.0F * scale_y,
        48.0F * scale_x, 40.0F * scale_y,
        0.0F, 0.0F, {0.0F, 0.85F, 1.0F, 1.0F}, identity};
    const std::uint32_t content_offset = append(stream, &content, 1U);

    progpu_native_scene_layer_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
    mask.bounds = {0.0F, 0.0F, 40.0F, 32.0F};
    mask.transform = {
        scale_x, 0.10F * scale_y,
        0.15F * scale_x, scale_y,
        8.0F * scale_x, 4.0F * scale_y};
    std::fill_n(mask.corner_radii_x, 4U, 8.0F);
    std::fill_n(mask.corner_radii_y, 4U, 8.0F);
    mask.opacity = 1.0F;
    const std::uint32_t mask_offset = append(stream, &mask, 1U);

    const progpu_native_scene_layer layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS,
        {8.0F * scale_x, 4.0F * scale_y,
            48.0F * scale_x, 40.0F * scale_y},
        1.0F,
        PROGPU_NATIVE_BLEND_SRC_OVER,
        1U,
        PROGPU_NATIVE_SCENE_NO_INDEX,
        201U,
        202U,
        0U,
        0U};
    const std::uint32_t layer_offset = append(stream, &layer, 1U);

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
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2001U, 1U,
            content_offset, sizeof(content), 0U, 0U},
        {sizeof(progpu_native_scene_resource),
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2002U, 1U,
            mask_offset, sizeof(mask), 0U, 0U}
    }};
    std::memcpy(stream.data() + resource_offset,
        resources.data(), sizeof(resources));

    const std::array<progpu_native_scene_command, command_count> commands{{
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2011U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            layer_offset, sizeof(layer),
            0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2012U,
            PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, 0U,
            8.0F * scale_x, 4.0F * scale_y,
            48.0F * scale_x, 40.0F * scale_y, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2013U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U}
    }};
    std::memcpy(stream.data() + command_offset,
        commands.data(), sizeof(commands));
    return stream;
}

} // namespace progpu::native::tests

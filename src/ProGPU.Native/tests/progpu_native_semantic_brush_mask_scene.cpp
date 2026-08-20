#include "progpu_native_semantic_brush_mask_scene.hpp"

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

std::vector<std::byte> create_semantic_brush_mask_scene_stream_impl(
    std::uint32_t target_width,
    std::uint32_t target_height,
    bool composite) {
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

    progpu_native_scene_layer_brush_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH;
    mask.gradient_stop_count = 2U;
    mask.bounds = {
        8.0F * scale_x, 4.0F * scale_y,
        48.0F * scale_x, 40.0F * scale_y};
    mask.transform = identity;
    mask.opacity = 1.0F;
    mask.brush.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    mask.brush.opacity = 1.0F;
    mask.brush.start_point = {
        mask.bounds.x,
        mask.bounds.y};
    mask.brush.end_point = {
        mask.bounds.x + mask.bounds.width,
        mask.bounds.y};
    mask.brush.stop_count = 2U;
    mask.brush.colors[0] = {1.0F, 1.0F, 1.0F, 0.0F};
    mask.brush.colors[1] = {1.0F, 1.0F, 1.0F, 1.0F};
    mask.brush.offsets0[1] = 1.0F;
    mask.brush.coordinate_transform0[0] = 1.0F;
    mask.brush.coordinate_transform1[1] = 1.0F;
    const std::array<progpu_native_scene_gradient_stop, 2U> stops{{
        {{1.0F, 1.0F, 1.0F, 0.0F}, 0.0F, 0U, 0U, 0U},
        {{1.0F, 1.0F, 1.0F, 1.0F}, 1.0F, 0U, 0U, 0U}
    }};
    std::uint32_t mask_offset = 0U;
    std::uint32_t mask_size = 0U;
    std::uint32_t auxiliary_offset = 0U;
    std::uint32_t auxiliary_size = 0U;
    if (composite) {
        progpu_native_scene_layer_brush_mask half = mask;
        half.gradient_stop_count = 0U;
        half.brush = {};
        half.brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
        half.brush.opacity = 1.0F;
        half.brush.colors[0] = {1.0F, 1.0F, 1.0F, 0.5F};
        half.brush.coordinate_transform0[0] = 1.0F;
        half.brush.coordinate_transform1[1] = 1.0F;
        const std::array<progpu_native_scene_layer_brush_mask, 2U> brushes{{
            mask,
            half
        }};
        progpu_native_scene_layer_composite_mask composite_mask{};
        composite_mask.struct_size = sizeof(composite_mask);
        composite_mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE;
        composite_mask.component_count = brushes.size();
        composite_mask.brush_mask_count = brushes.size();
        composite_mask.gradient_stop_count = stops.size();
        composite_mask.opacity = 1.0F;
        mask_offset = append(stream, &composite_mask, 1U);
        mask_size = sizeof(composite_mask);
        auxiliary_offset = append(stream, brushes.data(), brushes.size());
        const std::uint32_t stops_offset = append(
            stream,
            stops.data(),
            stops.size());
        auxiliary_size = stops_offset + sizeof(stops) - auxiliary_offset;
    } else {
        mask_offset = append(stream, &mask, 1U);
        mask_size = sizeof(mask);
        auxiliary_offset = append(stream, stops.data(), stops.size());
        auxiliary_size = sizeof(stops);
    }

    const progpu_native_scene_layer layer{
        sizeof(progpu_native_scene_layer),
        PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
            PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION,
        mask.bounds,
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
    header.scene_id = composite ? 107U : 106U;
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
            mask_offset, mask_size, auxiliary_offset, auxiliary_size}
    }};
    std::memcpy(
        stream.data() + resource_offset,
        resources.data(),
        sizeof(resources));

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
            mask.bounds.x, mask.bounds.y,
            mask.bounds.width, mask.bounds.height, 0U, 0U},
        {sizeof(progpu_native_scene_command),
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
            PROGPU_NATIVE_SCENE_RECORD_REQUIRED, 0U, 2013U,
            PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX,
            0U, 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U}
    }};
    std::memcpy(
        stream.data() + command_offset,
        commands.data(),
        sizeof(commands));
    return stream;
}

std::vector<std::byte> create_semantic_brush_mask_scene_stream(
    std::uint32_t target_width,
    std::uint32_t target_height) {
    return create_semantic_brush_mask_scene_stream_impl(
        target_width,
        target_height,
        false);
}

std::vector<std::byte> create_semantic_composite_brush_mask_scene_stream(
    std::uint32_t target_width,
    std::uint32_t target_height) {
    return create_semantic_brush_mask_scene_stream_impl(
        target_width,
        target_height,
        true);
}

} // namespace progpu::native::tests

#pragma once

#include "progpu_native.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <span>

namespace progpu::native::direct2d::tests {

template<typename T>
inline bool read_scene_value(std::span<const std::byte> bytes, std::uint64_t offset, T& value)
{
    if (offset > bytes.size() || sizeof(T) > bytes.size() - offset) return false;
    std::memcpy(&value, bytes.data() + static_cast<std::size_t>(offset), sizeof(T));
    return true;
}

// Shared portable-COM / Windows command-list oracle. The fixture records an
// aliased parent, a sheared fractional AA rectangle, an aliased child and two
// overlapping draws. Only the completed AA group may receive edge coverage.
inline bool grouped_axis_clip_contract(std::span<const std::byte> bytes)
{
    const auto read = [&]<typename T>(std::uint64_t offset, T& value) {
        return read_scene_value(bytes, offset, value);
    };
    const auto same_rect = [](const progpu_native_image_rect& value,
                              float x, float y, float width, float height) {
        return value.x == x && value.y == y && value.width == width && value.height == height;
    };
    progpu_native_scene_header header{};
    if (!read(0U, header) || header.command_count != 8U) return false;
    constexpr std::array kinds{
        PROGPU_NATIVE_SCENE_COMMAND_SAVE,
        PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
        PROGPU_NATIVE_SCENE_COMMAND_SAVE,
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
        PROGPU_NATIVE_SCENE_COMMAND_RESTORE,
        PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
        PROGPU_NATIVE_SCENE_COMMAND_RESTORE};
    std::array<progpu_native_scene_command, kinds.size()> commands{};
    for (std::size_t index = 0U; index < kinds.size(); ++index) {
        if (!read(header.command_offset + index * header.command_stride, commands[index]) ||
            commands[index].kind != static_cast<std::uint32_t>(kinds[index])) return false;
    }
    progpu_native_scene_layer layer{};
    if (!read(commands[1].payload_offset, layer) ||
        layer.flags != PROGPU_NATIVE_SCENE_LAYER_BOUNDS || layer.opacity != 1.0F ||
        layer.blend_mode != PROGPU_NATIVE_BLEND_SRC_OVER ||
        layer.effect_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX ||
        layer.mask_resource_index >= header.resource_count ||
        !same_rect(layer.bounds, 2.25F, 3.625F, 10.5F, 17.25F)) return false;
    progpu_native_scene_resource resource{};
    if (!read(header.resource_offset + std::uint64_t{layer.mask_resource_index} * header.resource_stride,
              resource) || resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK ||
        resource.payload_size != sizeof(progpu_native_scene_layer_mask)) return false;
    progpu_native_scene_layer_mask mask{};
    if (!read(resource.payload_offset, mask) ||
        mask.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE ||
        mask.opacity != 1.0F ||
        !same_rect(mask.bounds, 2.25F, 3.625F, 10.5F, 17.25F) ||
        mask.transform.m11 != 1.0F || mask.transform.m12 != 0.0F ||
        mask.transform.m21 != 0.0F || mask.transform.m22 != 1.0F ||
        mask.transform.m31 != 0.0F || mask.transform.m32 != 0.0F) return false;
    for (std::size_t index = 0U; index < 4U; ++index) {
        if (mask.corner_radii_x[index] != 0.0F || mask.corner_radii_y[index] != 0.0F) return false;
    }
    if (commands[2].state_index >= header.resource_count ||
        !read(header.resource_offset + std::uint64_t{commands[2].state_index} * header.resource_stride,
              resource) || resource.kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE) return false;
    progpu_native_scene_state state{};
    return read(resource.payload_offset, state) &&
        state.flags == PROGPU_NATIVE_SCENE_STATE_CLIP_RECT &&
        same_rect(state.clip_rect, 4.0F, 5.0F, 8.75F, 15.875F);
}

} // namespace progpu::native::direct2d::tests

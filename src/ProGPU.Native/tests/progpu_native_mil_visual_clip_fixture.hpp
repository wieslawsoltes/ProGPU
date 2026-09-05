#pragma once

#include "progpu_native_mil.h"
#include "progpu_native_mil_commands.generated.hpp"

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace progpu::native::tests {

enum class mil_clip_effect { none, zero_blur, blur, cached_blur, box_blur, shadow };

// An original raw-MIL fixture exercises the ABI and the exact same engine as
// the portable Direct2D integration gate, without a managed WPF adapter.
inline bool build_mil_visual_clip_fixture(std::vector<std::byte>& scene,
    mil_clip_effect effect = mil_clip_effect::none) {
    using mil::command;
    const auto append = [](std::vector<std::byte>& bytes, const auto& value) {
        const auto data = std::as_bytes(std::span(&value, 1U));
        bytes.insert(bytes.end(), data.begin(), data.end());
    };
    const auto packet = [&append](std::vector<std::byte>& bytes,
                                 command kind, const auto&... values) {
        const auto size = static_cast<std::uint32_t>(
            8U + (sizeof(values) + ... + 0U));
        append(bytes, size);
        append(bytes, static_cast<std::uint32_t>(kind));
        (append(bytes, values), ...);
    };
    std::vector<std::byte> batch;
    for (std::uint32_t visual = 1U; visual <= 3U; ++visual) {
        packet(batch, command::channel_create_resource, visual, 39U);
        packet(batch, command::visual_create, visual);
    }
    packet(batch, command::channel_create_resource, 4U, 47U);
    packet(batch, command::generic_target_create, 4U,
        std::uint64_t{0U}, std::uint64_t{0U}, 64U, 64U, 0U);
    packet(batch, command::target_set_root, 4U, 1U);
    packet(batch, command::channel_create_resource, 5U, 69U);
    packet(batch, command::rectangle_geometry, 5U,
        8.0, 8.0, 0.0, 0.0, 56.0, 64.0, 0U, 0U, 0U, 0U);
    packet(batch, command::visual_set_clip, 1U, 5U);
    packet(batch, command::channel_create_resource, 6U, 69U);
    packet(batch, command::rectangle_geometry, 6U,
        4.0, 4.0, 0.0, 16.0, 64.0, 32.0, 0U, 0U, 0U, 0U);
    if (effect == mil_clip_effect::shadow) {
        packet(batch, command::channel_create_resource, 40U, 37U);
        packet(batch, command::drop_shadow_effect, 40U, 4.0,
            progpu_native_color{0.0F, 1.0F, 0.0F, 1.0F}, 0.0, 1.0, 6.0,
            0U, 0U, 0U, 0U, 0U, 0U);
    } else if (effect != mil_clip_effect::none) {
        packet(batch, command::channel_create_resource, 40U, 36U);
        packet(batch, command::blur_effect, 40U,
            effect == mil_clip_effect::zero_blur ? 0.0 : 6.0,
            0U, effect == mil_clip_effect::box_blur ? 1U : 0U, 0U);
    }
    if (effect == mil_clip_effect::cached_blur) {
        packet(batch, command::channel_create_resource, 41U, 94U);
        packet(batch, command::bitmap_cache, 41U, 1.0, 0U, 0U, 0U);
    }
    for (std::uint32_t child = 2U; child <= 3U; ++child) {
        const std::uint32_t clip = 10U + child;
        const std::uint32_t content = 20U + child;
        const std::uint32_t brush = 30U + child;
        packet(batch, command::channel_create_resource, clip, 70U);
        packet(batch, command::ellipse_geometry, clip,
            12.0, 24.0, child == 2U ? 16.0 : 48.0, 32.0,
            0U, 0U, 0U, 0U);
        packet(batch, command::visual_set_clip, child, clip);
        if (effect != mil_clip_effect::none) {
            packet(batch, command::visual_set_effect, child, 40U);
        }
        if (effect == mil_clip_effect::cached_blur) {
            packet(batch, command::visual_set_cache_mode, child, 41U);
        }
        packet(batch, command::visual_insert_child_at, 1U, child, child - 2U);
        packet(batch, command::channel_create_resource, brush, 75U);
        const progpu_native_color color = child == 2U
            ? progpu_native_color{1.0F, 0.0F, 0.0F, 1.0F}
            : progpu_native_color{0.0F, 0.0F, 1.0F, 1.0F};
        packet(batch, command::solid_color_brush, brush, 1.0, color,
            0U, 0U, 0U, 0U);
        packet(batch, command::channel_create_resource, content, 43U);
        packet(batch, command::visual_set_content, child, content);
        std::vector<std::byte> commands;
        // An additional render-data mask must preserve both existing masks;
        // the second sibling must not inherit the first sibling's ellipse.
        packet(commands, command::push_clip, 6U, 0U);
        packet(commands, command::draw_rectangle,
            0.0, 0.0, 64.0, 64.0, brush, 0U);
        packet(commands, command::pop);
        append(batch, static_cast<std::uint32_t>(16U + commands.size()));
        append(batch, static_cast<std::uint32_t>(command::render_data));
        append(batch, content);
        append(batch, static_cast<std::uint32_t>(commands.size()));
        batch.insert(batch.end(), commands.begin(), commands.end());
    }
    progpu_native_mil_channel* channel = nullptr;
    if (progpu_native_mil_channel_create(&channel) !=
        PROGPU_NATIVE_MIL_STATUS_SUCCESS) {
        return false;
    }
    bool success = progpu_native_mil_channel_apply(
        channel, batch.data(), batch.size(), nullptr) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS;
    for (std::uint32_t child = 2U; success && child <= 3U; ++child) {
        success = progpu_native_mil_channel_set_visual_cache_bounds(
            channel, child, 0.0, 0.0, 64.0, 64.0) ==
            PROGPU_NATIVE_MIL_STATUS_SUCCESS;
    }
    std::size_t written = 0U;
    if (success) {
        success = progpu_native_mil_channel_build_scene(
            channel, 4U, 9011U, 1U, nullptr, 0U, &written, nullptr) ==
            PROGPU_NATIVE_MIL_STATUS_SUCCESS;
    }
    if (success) {
        scene.resize(written);
        success = progpu_native_mil_channel_build_scene(
            channel, 4U, 9011U, 1U, scene.data(), scene.size(),
            &written, nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS;
    }
    progpu_native_mil_channel_destroy(channel);
    return success && written == scene.size();
}

} // namespace progpu::native::tests

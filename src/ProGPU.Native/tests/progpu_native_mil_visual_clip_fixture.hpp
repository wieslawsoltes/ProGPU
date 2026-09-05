#pragma once

#include "progpu_native_mil.h"
#include "progpu_native_mil_commands.generated.hpp"

#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <vector>

namespace progpu::native::tests {

struct mil_clip_channel_deleter {
    void operator()(progpu_native_mil_channel* channel) const noexcept {
        progpu_native_mil_channel_destroy(channel);
    }
};
using mil_clip_channel = std::unique_ptr<
    progpu_native_mil_channel, mil_clip_channel_deleter>;

namespace mil_clip_fixture_detail {
template<class T>
void append(std::vector<std::byte>& bytes, const T& value) {
    const auto data = std::as_bytes(std::span(&value, 1U));
    bytes.insert(bytes.end(), data.begin(), data.end());
}
template<class... T>
void packet(std::vector<std::byte>& bytes, mil::command kind, const T&... values) {
    append(bytes, static_cast<std::uint32_t>(8U + (sizeof(values) + ... + 0U)));
    append(bytes, static_cast<std::uint32_t>(kind));
    (append(bytes, values), ...);
}
} // namespace mil_clip_fixture_detail

inline bool serialize_mil_visual_clip_fixture(progpu_native_mil_channel* channel,
    std::uint64_t scene_id, std::uint64_t generation, std::vector<std::byte>& scene) {
    std::size_t written = 0U;
    if (progpu_native_mil_channel_build_scene(channel, 4U, scene_id, generation,
            nullptr, 0U, &written, nullptr) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) {
        return false;
    }
    scene.resize(written);
    return progpu_native_mil_channel_build_scene(channel, 4U, scene_id, generation,
        scene.data(), scene.size(), &written, nullptr) ==
            PROGPU_NATIVE_MIL_STATUS_SUCCESS && written == scene.size();
}

enum class mil_clip_effect { none, zero_blur, blur, cached_blur, box_blur, shadow };

struct mil_clip_cache_options {
    bool enabled{};
    bool gradient{};
    double scale{1.0};
    double offset_x{};
    double offset_y{};
    bool snaps{};
    bool guidelines{};
    bool nested{};
    double root_scale{1.0};
};

// An original raw-MIL fixture exercises the ABI and the exact same engine as
// the portable Direct2D integration gate, without a managed WPF adapter.
inline bool build_mil_visual_clip_fixture(std::vector<std::byte>& scene,
    mil_clip_effect effect = mil_clip_effect::none,
    std::uint64_t scene_id = 9011U,
    const mil_clip_cache_options& cache_options = {},
    mil_clip_channel* retained_channel = nullptr) {
    using mil::command;
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
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
    const bool cached = cache_options.enabled ||
        effect == mil_clip_effect::cached_blur;
    if (cached) {
        packet(batch, command::channel_create_resource, 41U, 94U);
        packet(batch, command::bitmap_cache, 41U, cache_options.scale,
            0U, cache_options.snaps ? 1U : 0U, 0U);
    }
    if (cache_options.nested) {
        packet(batch, command::channel_create_resource, 44U, 94U);
        packet(batch, command::bitmap_cache, 44U,
            cache_options.root_scale, 0U, 0U, 0U);
        packet(batch, command::visual_set_cache_mode, 1U, 44U);
    }
    if (cache_options.gradient) {
        packet(batch, command::channel_create_resource, 42U, 77U);
        // Absolute byte size of two MIL gradient stops, each double + RGBA.
        packet(batch, command::linear_gradient_brush, 42U,
            1.0, 0.0, 0.0, 1.0, 0.0,
            0U, 0U, 0U, 1U, 1U, 0U, 48U, 0U, 0U,
            0.0, progpu_native_color{1.0F, 1.0F, 1.0F, 0.0F},
            1.0, progpu_native_color{1.0F, 1.0F, 1.0F, 1.0F});
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
        if (cached) {
            packet(batch, command::visual_set_cache_mode, child, 41U);
        }
        if (cache_options.gradient) {
            packet(batch, command::visual_set_alpha_mask, child, 42U);
        }
        if (cache_options.offset_x != 0.0 || cache_options.offset_y != 0.0) {
            packet(batch, command::visual_set_offset, child,
                cache_options.offset_x, cache_options.offset_y);
        }
        if (cache_options.guidelines) {
            packet(batch, command::visual_set_guideline_collection, child,
                std::uint16_t{2U}, std::uint16_t{0U},
                std::uint16_t{2U}, std::uint16_t{0U},
                0.25F, 63.75F, 0.25F, 63.75F);
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
    for (std::uint32_t child = 1U; success && child <= 3U; ++child) {
        success = progpu_native_mil_channel_set_visual_cache_bounds(
            channel, child, 0.0, 0.0, 64.0, 64.0) ==
            PROGPU_NATIVE_MIL_STATUS_SUCCESS;
    }
    if (success) {
        success = serialize_mil_visual_clip_fixture(channel, scene_id, 1U, scene);
    }
    if (success && retained_channel != nullptr) {
        retained_channel->reset(channel);
    } else {
        progpu_native_mil_channel_destroy(channel);
    }
    return success;
}

inline bool update_mil_visual_clip_fixture(progpu_native_mil_channel* channel,
    std::uint64_t scene_id, std::uint64_t generation,
    std::vector<std::byte>& scene, double radius_x) {
    std::vector<std::byte> batch;
    for (std::uint32_t child = 2U; child <= 3U; ++child) {
        mil_clip_fixture_detail::packet(batch, mil::command::ellipse_geometry,
            10U + child, radius_x, 24.0, child == 2U ? 16.0 : 48.0, 32.0,
            0U, 0U, 0U, 0U);
    }
    return progpu_native_mil_channel_apply(channel, batch.data(), batch.size(),
            nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
        serialize_mil_visual_clip_fixture(channel, scene_id, generation, scene);
}

} // namespace progpu::native::tests

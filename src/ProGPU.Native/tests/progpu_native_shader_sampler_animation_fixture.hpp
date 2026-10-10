#pragma once

#include "progpu_native_shader_sampler_pixel_fixture.hpp"

namespace progpu::native::tests {

struct shader_sampler_animation_case {
    double opacity;
    std::array<double, 4U> viewport;
    std::array<double, 4U> viewbox;
    bool animated;
    bool blue;
    bool reset;
    std::uint32_t left;
    std::uint32_t right;
    std::uint32_t split;
    std::uint8_t color;
};

// Independently specified nearest two-texel rectangles. Coordinates in the
// expected columns are physical capture pixels, not parsed native output.
inline constexpr std::array shader_sampler_animation_cases{
    shader_sampler_animation_case{1.0, {0, 0, 1, 1}, {0, 0, 1, 1}, true, false, false, 0, 32, 16, 255},
    shader_sampler_animation_case{0.5, {0, 0, 1, 1}, {0, 0, 1, 1}, true, false, false, 0, 32, 16, 128},
    shader_sampler_animation_case{0.5, {.25, 0, .5, 1}, {0, 0, 1, 1}, true, false, false, 8, 24, 16, 128},
    shader_sampler_animation_case{0.5, {.25, 0, .5, 1}, {.5, 0, .5, 1}, true, false, false, 8, 24, 8, 128},
    shader_sampler_animation_case{1.0, {0, 0, 1, 1}, {0, 0, 1, 1}, true, false, false, 0, 32, 16, 255},
    shader_sampler_animation_case{0.5, {.25, 0, .5, 1}, {.5, 0, .5, 1}, false, false, false, 0, 32, 16, 64},
    shader_sampler_animation_case{0.5, {0, 0, 1, 1}, {0, 0, 1, 1}, true, false, false, 0, 32, 16, 128},
    shader_sampler_animation_case{1.0, {0, 0, 1, 1}, {0, 0, 1, 1}, true, true, false, 0, 32, 16, 255},
    shader_sampler_animation_case{0.25, {.25, 0, .5, 1}, {0, 0, 1, 1}, true, true, true, 8, 24, 16, 64}};

inline void append_shader_sampler_animation_brush(std::vector<std::byte>& batch,
    bool absolute, bool animated, std::uint32_t opacity = 30U,
    std::uint32_t viewport = 31U, std::uint32_t viewbox = 32U,
    std::uint32_t transform = 0U) {
    using mil_clip_fixture_detail::packet;
    packet(batch, mil::command::image_brush, 5U, 0.25,
        absolute ? std::array{0.0, 0.0, 32.0, 24.0} : std::array{0.0, 0.0, 1.0, 1.0},
        absolute ? std::array{0.0, 0.0, 2.0, 1.0} : std::array{0.0, 0.0, 1.0, 1.0},
        0.707, 1.414, animated ? opacity : 0U, transform, 0U,
        absolute ? 0U : 1U, absolute ? 0U : 1U,
        animated ? viewport : 0U, animated ? viewbox : 0U,
        1U, 0U, 1U, 1U, 0U, 3U);
}

inline bool initialize_shader_sampler_animation(progpu_native_mil_channel* channel) {
    std::vector<std::byte> unused, batch;
    if (!build_original_shader_sampler_scene(channel, 0U, unused)) return false;
    using mil_clip_fixture_detail::packet;
    packet(batch, mil::command::visual_set_clip, 1U, 0U);
    packet(batch, mil::command::channel_create_resource, 30U, 49U);
    packet(batch, mil::command::channel_create_resource, 31U, 52U);
    packet(batch, mil::command::channel_create_resource, 32U, 52U);
    constexpr std::array<std::uint8_t, 8U> pixels{255, 0, 0, 255, 0, 255, 0, 255};
    return progpu_native_mil_channel_apply(channel, batch.data(), batch.size(), nullptr) ==
            PROGPU_NATIVE_MIL_STATUS_SUCCESS &&
        progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(channel,
            3U, 2U, 1U, 8U, pixels.data(), pixels.size(), 96.0, 96.0) ==
            PROGPU_NATIVE_MIL_STATUS_SUCCESS;
}

inline bool update_shader_sampler_animation(progpu_native_mil_channel* channel,
    bool absolute, std::uint32_t index) {
    if (index >= shader_sampler_animation_cases.size()) return false;
    const auto& test = shader_sampler_animation_cases[index];
    using mil::command;
    using mil_clip_fixture_detail::packet;
    std::vector<std::byte> batch;
    if (test.reset) {
        append_shader_sampler_animation_brush(batch, absolute, false);
        for (const auto id : {30U, 31U, 32U}) {
            const auto type = id == 30U ? 49U : 52U;
            packet(batch, command::channel_delete_resource, id, type);
            packet(batch, command::channel_create_resource, id, type);
        }
    }
    packet(batch, command::double_resource, 30U, test.opacity);
    auto viewport = test.viewport;
    auto viewbox = test.viewbox;
    if (absolute) {
        viewport[0] *= 32.0; viewport[2] *= 32.0;
        viewport[1] *= 24.0; viewport[3] *= 24.0;
        viewbox[0] *= 2.0; viewbox[2] *= 2.0;
    }
    packet(batch, command::rect_resource, 31U, viewport);
    packet(batch, command::rect_resource, 32U, viewbox);
    // Most frames update only the original animation resources. The shader,
    // brush and bitmap generation must not be manually bumped to fix a cache.
    if (index == 0U || index == 5U || index == 6U || test.reset)
        append_shader_sampler_animation_brush(batch, absolute, test.animated);
    if (progpu_native_mil_channel_apply(channel, batch.data(), batch.size(), nullptr) !=
        PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    if (index == 7U) {
        constexpr std::array<std::uint8_t, 8U> pixels{0, 0, 255, 255, 0, 255, 0, 255};
        if (progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(channel,
            3U, 2U, 1U, 8U, pixels.data(), pixels.size(), 96.0, 96.0) !=
            PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    }
    return true;
}

inline bool build_shader_sampler_animation(progpu_native_mil_channel* channel,
    bool absolute, std::uint32_t index, std::vector<std::byte>& scene) {
    const progpu_native_mil_scene_build_request request{sizeof(request), 0U, 4U,
        0U, absolute ? 0x94AEU : 0x94ADU, index + 1U, 1.0, 1.0, 0U, index + 1U};
    progpu_native_mil_scene_build_result result{}; result.struct_size = sizeof(result);
    std::size_t written{};
    if (progpu_native_mil_channel_build_scene_with_request(channel, &request,
        nullptr, 0U, &written, nullptr, &result) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    scene.resize(written);
    return progpu_native_mil_channel_build_scene_with_request(channel, &request,
        scene.data(), scene.size(), &written, nullptr, &result) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS && written == scene.size();
}

template<class Render, class Require>
void verify_shader_sampler_animation_pixels(Render render, Require require) {
    std::array<std::vector<std::uint8_t>, shader_sampler_animation_cases.size()> relative;
    for (const bool absolute : {false, true}) {
        std::array<std::vector<std::byte>, shader_sampler_animation_cases.size()> scenes;
        {
            progpu_native_mil_channel* raw{};
            require(progpu_native_mil_channel_create(&raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
                "animated sampler channel creation failed");
            mil_clip_channel owner(raw);
            require(initialize_shader_sampler_animation(raw), "animated sampler initialization failed");
            for (std::uint32_t index = 0U; index < scenes.size(); ++index) {
                require(update_shader_sampler_animation(raw, absolute, index), "animated sampler update failed");
                require(build_shader_sampler_animation(raw, absolute, index, scenes[index]),
                    "animated sampler scene publication failed");
            }
        }
        // Retained scene bytes survive source-resource reset and channel disposal.
        for (std::uint32_t index = 0U; index < scenes.size(); ++index) {
            const auto& test = shader_sampler_animation_cases[index];
            progpu_native_scene_header header{};
            require(scenes[index].size() >= sizeof(header), "animated sampler header missing");
            std::memcpy(&header, scenes[index].data(), sizeof(header));
            verify_shader_sampler_visual_commands(scenes[index], header, require);
            std::array<std::vector<std::uint8_t>, 3U> images;
            for (std::uint32_t replay = 0U; replay < images.size(); ++replay) {
                progpu_native_layer_metrics layers{}; layers.struct_size = sizeof(layers);
                progpu_native_scene_frame_metrics frame{}; frame.struct_size = sizeof(frame);
                const auto submissions = replay == 1U ? 1U : 2U;
                images[replay] = render(absolute, replay == 2U, scenes[index], header,
                    submissions, layers, frame);
                require(frame.submission_count == submissions && frame.command_count == shader_sampler_visual_commands.size(),
                    "animated sampler submissions/commands changed");
                require(layers.effect_kind == PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER && layers.effect_count == 1U &&
                    layers.effect_pass_count == (replay == 1U ? 0U : 1U) &&
                    layers.effect_cache_hit == (replay == 1U ? 1U : 0U),
                    "animated sampler effect retention changed");
            }
            require(images[0].size() == 64U * 64U * 4U && images[0] == images[1] && images[0] == images[2],
                "animated sampler cold/warm/independent full frames differ");
            if (absolute) require(images[0] == relative[index], "absolute/relative animation mappings differ");
            else relative[index] = images[0];
            for (std::uint32_t y = 0U; y < 64U; ++y) for (std::uint32_t x = 0U; x < 64U; ++x) {
                std::array<std::uint8_t, 4U> expected{0, 0, 0, 255};
                if (y >= 10U && y < 34U && x >= 8U + test.left && x < 8U + test.right)
                    expected[x < 8U + test.split ? test.blue ? 2U : 0U : 1U] = test.color;
                const auto* actual = images[0].data() + (y * 64U + x) * 4U;
                if (!std::equal(expected.begin(), expected.end(), actual))
                    std::fprintf(stderr, "Animated sampler absolute=%u case=%u pixel=%u,%u RGBA=%u,%u,%u,%u expected=%u,%u,%u,%u\n",
                        static_cast<unsigned>(absolute), index, x, y,
                        static_cast<unsigned>(actual[0]), static_cast<unsigned>(actual[1]),
                        static_cast<unsigned>(actual[2]), static_cast<unsigned>(actual[3]),
                        static_cast<unsigned>(expected[0]), static_cast<unsigned>(expected[1]),
                        static_cast<unsigned>(expected[2]), static_cast<unsigned>(expected[3]));
                require(std::equal(expected.begin(), expected.end(), actual),
                    "animated sampler mapping/opacity/ownership pixel differs");
            }
        }
    }
}

} // namespace progpu::native::tests

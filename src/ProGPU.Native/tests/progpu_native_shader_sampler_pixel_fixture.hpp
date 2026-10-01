#pragma once

#include "progpu_native_mil_visual_clip_fixture.hpp"

#include <algorithm>
#include <cstring>

namespace progpu::native::tests {

// Original MIL packets, owned upload and original shader bytes. The expected
// result below is a four-stripe source-color oracle, not another renderer.
inline bool build_original_shader_sampler_scene(std::uint32_t variant, std::vector<std::byte>& scene) {
    using mil::command;
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
    std::vector<std::byte> batch, content;
    const std::array types{39U, 43U, 95U, 47U, 80U, 33U, 38U, 75U, 66U, 69U};
    for (std::uint32_t i = 0U; i < types.size(); ++i)
        packet(batch, command::channel_create_resource, i + 1U, types[i]);
    packet(batch, command::visual_create, 1U);
    packet(batch, command::visual_set_render_options, 1U, 3U, 1U, 0U, 3U, 0U, 0U, 0U);
    packet(batch, command::solid_color_brush, 8U, 1.0, progpu_native_color{1, 1, 1, 1}, 0U, 0U, 0U, 0U);
    packet(batch, command::matrix_transform, 9U, 1.0, 0.0, 0.0, 1.0, 8.0, 0.0, 0U);
    packet(batch, command::image_brush, 5U, variant == 3U ? 1.0 : 0.5,
        std::array{0.0, 0.0, variant == 0U ? 1.0 : 0.5, 1.0}, std::array{0.0, 0.0, 1.0, 1.0},
        0.707, 1.414, 0U, variant == 2U ? 9U : 0U, 0U,
        1U, 1U, 0U, 0U, 1U, variant == 0U ? 0U : 4U, 1U, 1U, 0U, 3U);
    constexpr std::array<std::uint32_t, 15U> program{
        0xFFFF0200U, 0x0200001FU, 0x80000000U, 0xB0030000U,
        0x0200001FU, 0x90000000U, 0xA00F0800U,
        0x03000042U, 0x800F0000U, 0xB0E40000U, 0xA0E40800U,
        0x02000001U, 0x800F0800U, 0x80E40000U, 0xFFFFU};
    packet(batch, command::pixel_shader, 6U, 0U, static_cast<std::uint32_t>(sizeof(program)), 0U, program);
    packet(batch, command::shader_effect, 7U, 0.0, 0.0, 0.0, 0.0, 6U, 0xFFFFFFFFU,
        std::array<std::uint32_t, 8U>{0U, 0U, 0U, 0U, 0U, 0U, 8U, 4U}, 0U, 1U, 5U);
    packet(batch, command::visual_set_effect, 1U, 7U);
    packet(batch, command::rectangle_geometry, 10U, 0.0, 0.0, 16.0, 12.0, 16.0, 16.0, 0U, 0U, 0U, 0U);
    packet(batch, command::visual_set_clip, 1U, 10U);
    packet(content, command::draw_rectangle, 8.0, 10.0, 32.0, 24.0, 8U, 0U);
    append(batch, static_cast<std::uint32_t>(16U + content.size()));
    append(batch, static_cast<std::uint32_t>(command::render_data));
    append(batch, 2U); append(batch, static_cast<std::uint32_t>(content.size()));
    batch.insert(batch.end(), content.begin(), content.end());
    packet(batch, command::visual_set_content, 1U, 2U);
    packet(batch, command::generic_target_create, 4U, std::uint64_t{0U}, std::uint64_t{0U}, 64U, 64U, 0U);
    packet(batch, command::target_set_root, 4U, 1U);
    progpu_native_mil_channel* raw{};
    if (progpu_native_mil_channel_create(&raw) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    mil_clip_channel owner(raw);
    std::array<std::uint8_t, 8U> pixels{255, 0, 0, 255, 0, 255, 0, 255};
    if (variant == 3U) { pixels[0] = 0U; pixels[2] = 255U; }
    if (progpu_native_mil_channel_apply(raw, batch.data(), batch.size(), nullptr) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_set_visual_cache_bounds(raw, 1U, 8, 10, 32, 24) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(raw, 3U, 2U, 1U, 8U,
            pixels.data(), pixels.size(), 144.0, 192.0) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    const progpu_native_mil_scene_build_request request{
        sizeof(request), 0U, 4U, 0U, 0x9494U, variant + 1U, 1.0, 1.0, 0U, variant + 1U};
    progpu_native_mil_scene_build_result result{}; result.struct_size = sizeof(result);
    std::size_t written{};
    if (progpu_native_mil_channel_build_scene_with_request(raw, &request, nullptr, 0U, &written,
        nullptr, &result) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    scene.resize(written);
    return progpu_native_mil_channel_build_scene_with_request(raw, &request, scene.data(), scene.size(),
        &written, nullptr, &result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS && written == scene.size();
    // The source channel and bitmap bytes are disposed before GPU publication.
}

template<class Render, class Require>
void verify_original_shader_sampler_pixels(Render render, Require require) {
    for (std::uint32_t variant = 0U; variant < 4U; ++variant) {
        std::vector<std::byte> scene;
        require(build_original_shader_sampler_scene(variant, scene), "original ImageBrush shader scene rejected");
        progpu_native_scene_header header{};
        require(scene.size() >= sizeof(header), "ImageBrush shader scene header missing");
        std::memcpy(&header, scene.data(), sizeof(header));
        std::array<std::vector<std::uint8_t>, 3U> images;
        for (std::uint32_t replay = 0U; replay < images.size(); ++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size = sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size = sizeof(frame);
            const auto submissions = replay == 1U ? 1U : 2U;
            images[replay] = render(replay == 2U, scene, variant + 1U, submissions,
                header.command_count, layers, frame);
            require(frame.submission_count == submissions && frame.command_count == header.command_count,
                "owned sampler capture submission or source command count differs");
            require(layers.effect_kind == PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER && layers.effect_count == 1U &&
                layers.effect_pass_count == (replay == 1U ? 0U : 1U) &&
                layers.effect_cache_hit == (replay == 1U ? 1U : 0U), "owned sampler effect retention differs");
        }
        require(images[0] == images[1] && images[0] == images[2] && images[0].size() == 64U * 64U * 4U,
            "owned sampler cold/warm/independent pixels differ");
        for (unsigned y = 0U; y < 64U; ++y) for (unsigned x = 0U; x < 64U; ++x) {
            std::array<std::uint8_t, 4U> expected{0U, 0U, 0U, 255U};
            if (x >= 16U && x < 32U && y >= 12U && y < 28U) {
                const auto offset = variant == 2U ? 8U : 0U;
                const auto stripe = ((x - 8U + 32U - offset) / (variant == 0U ? 16U : 8U)) & 1U;
                expected[stripe == 1U ? 1U : variant == 3U ? 2U : 0U] = variant == 3U ? 255U : 128U;
            }
            const auto* actual = images[0].data() + (y * 64U + x) * 4U;
            require(std::equal(expected.begin(), expected.end(), actual),
                "original sampler color/opacity/physical normalization/tile transform/final clip differs");
        }
    }
}
} // namespace progpu::native::tests

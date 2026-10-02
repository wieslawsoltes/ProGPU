#pragma once

#include "progpu_native_mil_visual_clip_fixture.hpp"

#include <algorithm>
#include <cstdio>
#include <cstring>

namespace progpu::native::tests {

// Original MIL packets, owned upload and original shader bytes. The expected
// result below is an independent source-color oracle, not another renderer.
inline bool build_original_shader_sampler_scene(progpu_native_mil_channel* channel,
    std::uint32_t variant, std::vector<std::byte>& scene) {
    using mil::command;
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
    if (variant >= 13U) return false;
    const auto source_variant = variant < 4U ? variant
        : variant == 6U || variant == 9U || variant == 10U ? 1U : 0U;
    const bool full_source = variant >= 11U;
    const bool inherited = variant >= 5U && variant <= 10U;
    const auto extent = full_source ? 100.0 : 32.0;
    const auto input_height = full_source ? 100.0 : 24.0;
    std::vector<std::byte> batch, content;
    const std::array types{39U, 43U, 95U, 47U, 80U, 33U, 38U, 75U, 66U, 69U};
    if (variant == 0U) {
        for (std::uint32_t i = 0U; i < types.size(); ++i)
            packet(batch, command::channel_create_resource, i + 1U, types[i]);
        packet(batch, command::visual_create, 1U);
        packet(batch, command::generic_target_create, 4U, std::uint64_t{0U}, std::uint64_t{0U}, 64U, 64U, 0U);
        packet(batch, command::target_set_root, 4U, 1U);
    }
    if (variant == 5U) {
        packet(batch, command::channel_create_resource, 11U, 39U);
        packet(batch, command::visual_create, 11U);
        packet(batch, command::visual_insert_child_at, 11U, 1U, 0U);
        packet(batch, command::target_set_root, 4U, 11U);
    }
    if (variant >= 5U)
        packet(batch, command::visual_set_render_options, 11U, 2U, 0U, 0U,
            inherited ? 3U : 0U, 0U, 0U, 0U);
    if (variant == 11U) {
        packet(batch, command::channel_create_resource, 12U, 47U);
        packet(batch, command::generic_target_create, 12U, std::uint64_t{0U}, std::uint64_t{0U}, 128U, 128U, 0U);
        packet(batch, command::target_set_root, 12U, 11U);
    }
    // A flagged Unspecified update is real source state, including when an
    // already-published child resets its own Nearest mode. It inherits, not
    // forces Linear; the root's default remains Linear.
    packet(batch, command::visual_set_render_options, 1U, 3U, 1U, 0U,
        variant < 4U || variant == 7U || variant == 9U ? 3U : 0U, 0U, 0U, 0U);
    packet(batch, command::solid_color_brush, 8U, 1.0, progpu_native_color{1, 1, 1, 1}, 0U, 0U, 0U, 0U);
    packet(batch, command::matrix_transform, 9U, 1.0, 0.0, 0.0, 1.0, 8.0, 0.0, 0U);
    const std::array viewbox = !full_source ? std::array{0.0, 0.0, 1.0, 1.0}
        : variant == 11U ? std::array{50.0, 10.0, 100.0, 20.0} : std::array{0.25, 0.2, 0.5, 0.4};
    packet(batch, command::image_brush, 5U, full_source ? 0.25 : source_variant == 3U ? 1.0 : 0.5,
        std::array{0.0, 0.0, full_source || source_variant == 0U ? 1.0 : 0.5, 1.0}, viewbox,
        0.707, 1.414, 0U, !full_source && source_variant == 2U ? 9U : 0U, 0U,
        1U, variant == 11U ? 0U : 1U, 0U, 0U, full_source ? 0U : 1U,
        full_source || source_variant == 0U ? 0U : 4U, 1U, 1U, 0U, 3U);
    constexpr std::array<std::uint32_t, 15U> program{
        0xFFFF0200U, 0x0200001FU, 0x80000000U, 0xB0030000U,
        0x0200001FU, 0x90000000U, 0xA00F0800U,
        0x03000042U, 0x800F0000U, 0xB0E40000U, 0xA0E40800U,
        0x02000001U, 0x800F0800U, 0x80E40000U, 0xFFFFU};
    packet(batch, command::pixel_shader, 6U, 0U, static_cast<std::uint32_t>(sizeof(program)), 0U, program);
    packet(batch, command::shader_effect, 7U, 0.0, 0.0, 0.0, 0.0, 6U, 0xFFFFFFFFU,
        std::array<std::uint32_t, 8U>{0U, 0U, 0U, 0U, 0U, 0U, 8U, 4U}, 0U, (source_variant & 1U) == 0U ? 1U : 2U, 5U);
    packet(batch, command::visual_set_effect, 1U, 7U);
    packet(batch, command::rectangle_geometry, 10U, 0.0, 0.0,
        full_source ? 8.0 : 16.0, full_source ? 10.0 : 12.0,
        full_source ? 100.0 : 16.0, full_source ? 100.0 : 16.0, 0U, 0U, 0U, 0U);
    packet(batch, command::visual_set_clip, 1U, 10U);
    packet(content, command::draw_rectangle, 8.0, 10.0, extent, input_height, 8U, 0U);
    append(batch, static_cast<std::uint32_t>(16U + content.size()));
    append(batch, static_cast<std::uint32_t>(command::render_data));
    append(batch, 2U); append(batch, static_cast<std::uint32_t>(content.size()));
    batch.insert(batch.end(), content.begin(), content.end());
    packet(batch, command::visual_set_content, 1U, 2U);
    const auto old_bitmap_generation = progpu_native_mil_channel_get_resource_generation(channel, 3U);
    const auto old_brush_generation = progpu_native_mil_channel_get_resource_generation(channel, 5U);
    const auto bitmap_width = full_source ? 400U : 2U;
    const auto bitmap_height = full_source ? 200U : 1U;
    std::vector<std::uint8_t> pixels(bitmap_width * bitmap_height * 4U);
    for (std::uint32_t y = 0U; y < bitmap_height; ++y)
        for (std::uint32_t x = 0U; x < bitmap_width; ++x) {
            const auto at = (y * bitmap_width + x) * 4U;
            pixels[at + (x >= bitmap_width / 2U ? 1U : !full_source && source_variant == 3U ? 2U : 0U)] = 255U;
            pixels[at + 3U] = 255U;
        }
    if (progpu_native_mil_channel_apply(channel, batch.data(), batch.size(), nullptr) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_set_visual_cache_bounds(channel, 1U, 8, 10, extent, input_height) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(channel, 3U, bitmap_width, bitmap_height, bitmap_width * 4U,
            pixels.data(), pixels.size(), full_source ? 192.0 : 144.0, full_source ? 384.0 : 192.0) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    if (progpu_native_mil_channel_get_resource_generation(channel, 3U) <= old_bitmap_generation ||
        progpu_native_mil_channel_get_resource_generation(channel, 5U) <= old_brush_generation) return false;
    const progpu_native_mil_scene_build_request request{
        sizeof(request), 0U, full_source ? 12U : 4U, 0U, 0x9494U, variant + 1U, 1.0, 1.0, 0U, variant + 1U};
    progpu_native_mil_scene_build_result result{}; result.struct_size = sizeof(result);
    std::size_t written{};
    if (progpu_native_mil_channel_build_scene_with_request(channel, &request, nullptr, 0U, &written,
        nullptr, &result) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    scene.resize(written);
    return progpu_native_mil_channel_build_scene_with_request(channel, &request, scene.data(), scene.size(),
        &written, nullptr, &result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS && written == scene.size();
}

template<class Render, class Require>
void verify_original_shader_sampler_pixels(Render render, Require require) {
    std::array<std::vector<std::byte>, 13U> scenes;
    {
        progpu_native_mil_channel* raw{};
        require(progpu_native_mil_channel_create(&raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
            "original ImageBrush shader channel rejected");
        mil_clip_channel owner(raw);
        // Scene 0x9494 retains one source owner. Mutate its actual resources so
        // every bitmap/brush revision comes from that owner's live channel,
        // rather than recycling another channel's handle/generation namespace.
        for (std::uint32_t variant = 0U; variant < scenes.size(); ++variant)
            require(build_original_shader_sampler_scene(raw, variant, scenes[variant]),
                "original ImageBrush shader scene or resource revision rejected");
    }
    // All immutable captures outlive the channel and caller-owned bitmap bytes.
    std::vector<std::uint8_t> absolute_viewbox_pixels;
    std::vector<std::uint8_t> before_reset_pixels;
    for (std::uint32_t variant = 0U; variant < scenes.size(); ++variant) {
        const auto image_extent = variant >= 11U ? 128U : 64U;
        const auto& scene = scenes[variant];
        progpu_native_scene_header header{};
        require(scene.size() >= sizeof(header), "ImageBrush shader scene header missing");
        std::memcpy(&header, scene.data(), sizeof(header));
        std::array<std::vector<std::uint8_t>, 3U> images;
        for (std::uint32_t replay = 0U; replay < images.size(); ++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size = sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size = sizeof(frame);
            const auto submissions = replay == 1U ? 1U : 2U;
            images[replay] = render(replay == 2U, scene, variant + 1U, submissions,
                header.command_count, image_extent, layers, frame);
            require(frame.submission_count == submissions && frame.command_count == header.command_count,
                "owned sampler capture submission or source command count differs");
            require(layers.effect_kind == PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER && layers.effect_count == 1U &&
                layers.effect_pass_count == (replay == 1U ? 0U : 1U) &&
                layers.effect_cache_hit == (replay == 1U ? 1U : 0U), "owned sampler effect retention differs");
        }
        require(images[0] == images[1] && images[0] == images[2] && images[0].size() == image_extent * image_extent * 4U,
            "owned sampler cold/warm/independent pixels differ");
        if (variant == 7U || variant == 9U) before_reset_pixels = images[0];
        if (variant == 8U || variant == 10U) require(images[0] == before_reset_pixels,
            "owned sampler flagged Unspecified reset lost the parent's Nearest mode");
        if (variant == 11U) absolute_viewbox_pixels = images[0];
        if (variant == 12U) require(images[0] == absolute_viewbox_pixels,
            "owned sampler absolute/relative full-source viewbox pixels differ");
        for (unsigned y = 0U; y < image_extent; ++y) for (unsigned x = 0U; x < image_extent; ++x) {
            std::array<std::uint8_t, 4U> expected{0U, 0U, 0U, 255U};
            if (variant >= 11U) {
                // 400x200 pixels at 192/384 DPI are 200x50 DIPs. The
                // centered Stretch.None viewbox maps the FULL source to
                // (-50,30,200,50), then clips to the 100x100 viewport.
                // Source viewbox cropping would incorrectly leave 20 rows.
                if (x >= 8U && x < 108U && y >= 40U && y < 90U)
                    expected[x < 58U ? 0U : 1U] = 64U;
            } else if (x >= 16U && x < 32U && y >= 12U && y < 28U) {
                const auto source_variant = variant < 4U ? variant
                    : variant == 6U || variant == 9U || variant == 10U ? 1U : 0U;
                const auto offset = source_variant == 2U ? 8U : 0U;
                if (variant == 4U) {
                    // Two texel centers, clamped at the source image edge.
                    // Work in exact thirty-second-texel numerators; opacity is
                    // applied once when the capture is quantized to UNORM8.
                    const int green = std::clamp((static_cast<int>(x) - 8) * 2 - 15, 0, 32);
                    expected[1] = static_cast<std::uint8_t>((green * 255 + 32) / 64);
                    expected[0] = static_cast<std::uint8_t>(((32 - green) * 255 + 32) / 64);
                } else {
                    const auto stripe = ((x - 8U + 32U - offset) / (source_variant == 0U ? 16U : 8U)) & 1U;
                    expected[stripe == 1U ? 1U : source_variant == 3U ? 2U : 0U] = source_variant == 3U ? 255U : 128U;
                }
            }
            const auto* actual = images[0].data() + (y * image_extent + x) * 4U;
            const bool matches = std::equal(expected.begin(), expected.end(), actual);
            if (!matches) {
                std::fprintf(stderr,
                    "Original shader sampler variant=%u pixel=(%u,%u) actual=(%u,%u,%u,%u) expected=(%u,%u,%u,%u)\n",
                    variant, x, y,
                    static_cast<unsigned>(actual[0]), static_cast<unsigned>(actual[1]),
                    static_cast<unsigned>(actual[2]), static_cast<unsigned>(actual[3]),
                    static_cast<unsigned>(expected[0]), static_cast<unsigned>(expected[1]),
                    static_cast<unsigned>(expected[2]), static_cast<unsigned>(expected[3]));
            }
            require(matches,
                "original sampler color/opacity/physical normalization/tile transform/final clip differs");
        }
    }
}
} // namespace progpu::native::tests

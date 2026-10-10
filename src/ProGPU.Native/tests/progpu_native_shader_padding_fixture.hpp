#pragma once

#include "progpu_native_scene_builder.hpp"
#include "progpu_native_mil_visual_clip_fixture.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>

namespace progpu::native::tests {

enum class shader_padding_output { input, constant, uv, derivatives, image, uv_squared };
struct shader_padding_case {
    std::array<double, 4U> padding;
    shader_padding_output output;
    float dpi;
    progpu_native_status expected;
};
inline constexpr std::array shader_padding_cases{
    shader_padding_case{{0, 0, 0, 0}, shader_padding_output::input, 1, PROGPU_NATIVE_STATUS_SUCCESS},
    shader_padding_case{{2, 6, 4, 12}, shader_padding_output::input, 1, PROGPU_NATIVE_STATUS_SUCCESS},
    shader_padding_case{{2, 6, 4, 12}, shader_padding_output::constant, 1, PROGPU_NATIVE_STATUS_SUCCESS},
    shader_padding_case{{2, 6, 4, 12}, shader_padding_output::uv, 1, PROGPU_NATIVE_STATUS_SUCCESS},
    shader_padding_case{{2, 6, 4, 12}, shader_padding_output::derivatives, 1, PROGPU_NATIVE_STATUS_SUCCESS},
    shader_padding_case{{1, 3, 2, 6}, shader_padding_output::derivatives, 2, PROGPU_NATIVE_STATUS_SUCCESS},
    shader_padding_case{{2, 6, 4, 12}, shader_padding_output::image, 1, PROGPU_NATIVE_STATUS_SUCCESS},
    shader_padding_case{{4, 4, 2, 14}, shader_padding_output::image, 1, PROGPU_NATIVE_STATUS_SUCCESS},
    shader_padding_case{{0, 0, 0, 0}, shader_padding_output::input, 1, PROGPU_NATIVE_STATUS_SUCCESS},
    shader_padding_case{{0.25, 6, 4, 12}, shader_padding_output::input, 1, PROGPU_NATIVE_STATUS_SUCCESS},
    shader_padding_case{{2, 6, 32, 12}, shader_padding_output::input, 1, PROGPU_NATIVE_STATUS_UNSUPPORTED},
    shader_padding_case{{2.0 + 0x1p-25, 6, 4, 12}, shader_padding_output::input, 1, PROGPU_NATIVE_STATUS_SUCCESS}
};

inline std::vector<std::uint32_t> shader_padding_program(shader_padding_output output) {
    std::vector<std::uint32_t> program{
        0xFFFF0200U, 0x0200001FU, 0x80000000U, 0xB0030000U,
        0x0200001FU, 0x90000000U, 0xA00F0800U,
        0x05000051U, 0xA00F0002U, 0U, 0U, 0U, 0x3F800000U,
        0x03000042U, 0x800F0000U, 0xB0E40000U, 0xA0E40800U};
    if (output == shader_padding_output::uv_squared) {
        // ps_2_0 permits only one t# read per instruction. Retain the
        // original UVs in r1 before squaring, without relaxing that gate.
        program.insert(program.end(), {0x02000001U, 0x80030001U, 0xB0E40000U,
            0x03000005U, 0x80030800U, 0x80E40001U, 0x80E40001U,
            0x02000001U, 0x800C0800U, 0xA0E40002U});
    } else if (output == shader_padding_output::uv) {
        program.insert(program.end(), {0x02000001U, 0x80030800U, 0xB0E40000U,
            0x02000001U, 0x800C0800U, 0xA0E40002U});
    } else if (output == shader_padding_output::derivatives) {
        program.insert(program.end(), {0x02000001U, 0x80070800U, 0xA05C0000U,
            0x02000001U, 0x80080800U, 0xA0FF0002U});
    } else {
        program.insert(program.end(), {0x02000001U, 0x800F0800U,
            output == shader_padding_output::constant ? 0xA0E40000U : 0x80E40000U});
    }
    program.push_back(0xFFFFU);
    return program;
}

inline void append_shader_padding_effect(std::vector<std::byte>& batch,
    const std::array<double, 4U>& padding, shader_padding_output output) {
    using mil_clip_fixture_detail::packet;
    packet(batch, mil::command::shader_effect, 5U, padding, 6U,
        output == shader_padding_output::derivatives ? 0U : UINT32_MAX,
        std::array<std::uint32_t, 8U>{2U, 16U, 0U, 0U, 0U, 0U, 8U, 4U},
        std::uint16_t{0U}, std::array{0.25F, 0.5F, 0.75F, 1.0F}, 0U, 1U,
        output == shader_padding_output::image ? 9U : 7U);
    // Original Int16 register array has two bytes of DWORD framing at the END.
    batch.push_back(std::byte{}); batch.push_back(std::byte{});
    const std::uint32_t packet_size = 116U;
    std::memcpy(batch.data() + batch.size() - packet_size, &packet_size, sizeof(packet_size));
}

// One original channel owns all generations. No retained scene borrows its
// packets, bitmap upload or shader program after this builder returns.
inline bool build_shader_padding_scene(progpu_native_mil_channel* channel, std::uint32_t variant,
    std::vector<std::byte>& scene) {
    using mil::command;
    using mil_clip_fixture_detail::append;
    using mil_clip_fixture_detail::packet;
    if (variant >= shader_padding_cases.size()) return false;
    const auto& test = shader_padding_cases[variant];
    const double dpi = test.dpi;
    std::vector<std::byte> batch, content;
    if (variant == 0U) {
        constexpr std::array types{39U, 43U, 47U, 75U, 38U, 33U, 34U, 69U, 80U, 95U};
        for (std::uint32_t i = 0U; i < types.size(); ++i)
            packet(batch, command::channel_create_resource, i + 1U, types[i]);
        packet(batch, command::visual_create, 1U);
        packet(batch, command::generic_target_create, 3U, std::uint64_t{}, std::uint64_t{}, 64U, 64U, 0U);
        packet(batch, command::target_set_root, 3U, 1U);
        packet(batch, command::solid_color_brush, 4U, 1.0, progpu_native_color{1, 1, 1, 1}, 0U, 0U, 0U, 0U);
        packet(batch, command::implicit_input_brush, 7U, 1.0, 0U, 0U, 0U);
    }
    packet(batch, command::visual_set_render_options, 1U, 3U, 1U, 0U, 3U, 0U, 0U, 0U);
    packet(batch, command::rectangle_geometry, 8U, 0.0, 0.0, 14.0 / dpi, 12.0 / dpi,
        28.0 / dpi, 20.0 / dpi, 0U, 0U, 0U, 0U);
    packet(batch, command::visual_set_clip, 1U, 8U);
    packet(batch, command::image_brush, 9U, 1.0,
        std::array{0.0, 0.0, 1.0, 1.0}, std::array{0.0, 0.0, 1.0, 1.0},
        0.707, 1.414, 0U, 0U, 0U, 1U, 1U, 0U, 0U, 1U, 0U, 1U, 1U, 0U, 10U);
    const auto program = shader_padding_program(test.output);
    append(batch, static_cast<std::uint32_t>(24U + program.size() * sizeof(std::uint32_t)));
    append(batch, static_cast<std::uint32_t>(command::pixel_shader));
    append(batch, 6U); append(batch, 0U);
    append(batch, static_cast<std::uint32_t>(program.size() * sizeof(std::uint32_t))); append(batch, 0U);
    const auto code = std::as_bytes(std::span(program));
    batch.insert(batch.end(), code.begin(), code.end());
    append_shader_padding_effect(batch, test.padding, test.output);
    packet(batch, command::visual_set_effect, 1U, 5U);
    packet(content, command::draw_rectangle, 16.0 / dpi, 16.0 / dpi, 16.0 / dpi, 8.0 / dpi, 4U, 0U);
    append(batch, static_cast<std::uint32_t>(16U + content.size()));
    append(batch, static_cast<std::uint32_t>(command::render_data)); append(batch, 2U);
    append(batch, static_cast<std::uint32_t>(content.size()));
    batch.insert(batch.end(), content.begin(), content.end());
    packet(batch, command::visual_set_content, 1U, 2U);
    const auto old_generation = progpu_native_mil_channel_get_resource_generation(channel, 5U);
    constexpr std::array<std::uint8_t, 8U> pixels{255, 0, 0, 255, 0, 255, 0, 255};
    if (progpu_native_mil_channel_apply(channel, batch.data(), batch.size(), nullptr) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_get_resource_generation(channel, 5U) <= old_generation ||
        progpu_native_mil_channel_set_visual_cache_bounds(channel, 1U, 16.0 / dpi, 16.0 / dpi,
            16.0 / dpi, 8.0 / dpi) != PROGPU_NATIVE_MIL_STATUS_SUCCESS ||
        progpu_native_mil_channel_set_bitmap_source_rgba8_with_dpi(channel, 10U, 2U, 1U, 8U,
            pixels.data(), pixels.size(), 144.0, 192.0) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    const progpu_native_mil_scene_build_request request{
        sizeof(request), 0U, 3U, 0U, 0x9496U, variant + 1U, dpi, dpi, 0U, variant + 1U};
    std::size_t written{};
    progpu_native_mil_scene_build_result result{}; result.struct_size = sizeof(result);
    if (progpu_native_mil_channel_build_scene_with_request(channel, &request, nullptr, 0U, &written,
        nullptr, &result) != PROGPU_NATIVE_MIL_STATUS_SUCCESS) return false;
    scene.resize(written);
    return progpu_native_mil_channel_build_scene_with_request(channel, &request, scene.data(), scene.size(),
        &written, nullptr, &result) == PROGPU_NATIVE_MIL_STATUS_SUCCESS && written == scene.size();
}

template<class Render, class Require>
void verify_original_shader_padding_pixels(Render render, Require require) {
    std::array<std::vector<std::byte>, shader_padding_cases.size()> scenes;
    {
        progpu_native_mil_channel* raw{};
        require(progpu_native_mil_channel_create(&raw) == PROGPU_NATIVE_MIL_STATUS_SUCCESS,
            "padding source channel creation failed");
        mil_clip_channel owner(raw);
        for (std::uint32_t i = 0U; i < scenes.size(); ++i)
            require(build_shader_padding_scene(raw, i, scenes[i]), "original padding source scene rejected");
    }
    for (std::uint32_t variant = 0U; variant < scenes.size(); ++variant) {
        const auto& test = shader_padding_cases[variant];
        progpu_native_scene_header header{};
        require(scenes[variant].size() >= sizeof(header), "padding scene header missing");
        std::memcpy(&header, scenes[variant].data(), sizeof(header));
        std::array<std::vector<std::uint8_t>, 3U> images;
        for (std::uint32_t replay = 0U; replay < images.size(); ++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size = sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size = sizeof(frame);
            const std::uint32_t submissions = replay != 1U && test.output == shader_padding_output::image ? 2U : 1U;
            images[replay] = render(replay == 2U, scenes[variant], variant + 1U, test,
                header.command_count, submissions, layers, frame);
            if (test.expected != PROGPU_NATIVE_STATUS_SUCCESS) {
                require(images[replay].empty() && frame.submission_count == 0U,
                    "fractional or cropped padding capture published a submission");
                continue;
            }
            require(frame.command_count == header.command_count && frame.submission_count == submissions &&
                layers.effect_count == 1U && layers.effect_kind == PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER &&
                layers.effect_pass_count == (replay == 1U ? 0U : 1U) &&
                layers.effect_cache_hit == (replay == 1U ? 1U : 0U) &&
                layers.effect_uniform_upload_bytes == (replay == 1U ? 0U : 528U),
                "padding source revision/capture/pass/submission retention differs");
        }
        if (test.expected != PROGPU_NATIVE_STATUS_SUCCESS) continue;
        require(images[0] == images[1] && images[0] == images[2] && images[0].size() == 64U * 64U * 4U,
            "padding cold/warm/independent pixels differ");
        for (std::uint32_t y = 0U; y < 64U; ++y) for (std::uint32_t x = 0U; x < 64U; ++x) {
            std::array<std::uint8_t, 4U> expected{0, 0, 0, 255};
            // Authored independent PHYSICAL frames: padding variants 1–6/11
            // cover (12,14,32,16), variant7 covers (14,12,32,16).
            const std::uint32_t left = variant == 7U ? 14U : 12U;
            const std::uint32_t top = variant == 7U ? 12U : 14U;
            if (x >= 14U && x < 42U && y >= 12U && y < 32U) {
                if (test.output == shader_padding_output::input) {
                    if (x >= 16U && x < 32U && y >= 16U && y < 24U) expected = {255, 255, 255, 255};
                } else if (x >= left && x < left + 32U && y >= top && y < top + 16U) {
                    if (test.output == shader_padding_output::constant) expected = {64, 128, 191, 255};
                    if (test.output == shader_padding_output::derivatives) expected = {8, 16, 0, 255};
                    if (test.output == shader_padding_output::image) expected[x < left + 16U ? 0U : 1U] = 255U;
                    if (test.output == shader_padding_output::uv) {
                        expected[0] = static_cast<std::uint8_t>(((2U * (x - left) + 1U) * 255U + 32U) / 64U);
                        expected[1] = static_cast<std::uint8_t>(((2U * (y - top) + 1U) * 255U + 16U) / 32U);
                    }
                }
            }
            const auto* actual = images[0].data() + (y * 64U + x) * 4U;
            const bool same = std::equal(expected.begin(), expected.end(), actual);
            if (!same) std::fprintf(stderr,
                "Shader padding variant=%u pixel=(%u,%u) actual=(%u,%u,%u,%u) expected=(%u,%u,%u,%u)\n",
                variant, x, y, actual[0], actual[1], actual[2], actual[3],
                expected[0], expected[1], expected[2], expected[3]);
            require(same, "original padding input/UV/derivatives/sampler/final clip pixels differ");
        }
    }
}
} // namespace progpu::native::tests

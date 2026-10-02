#pragma once

#include "progpu_native_scene_builder.hpp"

#include <algorithm>
#include <array>
#include <vector>

namespace progpu::native::tests {
struct shader_derivative_frame_case {
    float width, height, dpi, left;
    std::uint32_t selected_register;
    bool mapped;
    std::uint32_t viewport_x;
    progpu_native_status expected;
};

template<class Render, class Require>
void verify_original_shader_derivative_pixels(Render render, Require require) {
    constexpr std::array cases{
        shader_derivative_frame_case{32, 16, 1, 8, 0, false, 0, PROGPU_NATIVE_STATUS_SUCCESS},
        shader_derivative_frame_case{16, 32, 1, 8, 0, false, 0, PROGPU_NATIVE_STATUS_SUCCESS},
        shader_derivative_frame_case{16, 16, 2, 4, 0, false, 0, PROGPU_NATIVE_STATUS_SUCCESS},
        shader_derivative_frame_case{32, 16, 1, 8, 1, false, 0, PROGPU_NATIVE_STATUS_SUCCESS},
        shader_derivative_frame_case{32, 16, 1, 8, 31, false, 0, PROGPU_NATIVE_STATUS_SUCCESS},
        shader_derivative_frame_case{32, 16, 1, 8, 0, true, 0, PROGPU_NATIVE_STATUS_SUCCESS},
        shader_derivative_frame_case{32, 16, 1, 8, 0, true, 1, PROGPU_NATIVE_STATUS_UNSUPPORTED},
        shader_derivative_frame_case{32, 16, 1, 8.25F, 0, false, 0, PROGPU_NATIVE_STATUS_UNSUPPORTED},
        shader_derivative_frame_case{32, 16, 1.1F, 8, 0, false, 0, PROGPU_NATIVE_STATUS_UNSUPPORTED},
        shader_derivative_frame_case{64, 16, 1, 8, 0, false, 0, PROGPU_NATIVE_STATUS_UNSUPPORTED}};
    for (std::size_t index = 0U; index < cases.size(); ++index) {
        const auto& test = cases[index];
        const auto input_constant = test.selected_register == 31U ? 31U : 0U;
        // Original shader outputs the selected constant's X/W/Y as RGB, and
        // DEF-owned one as alpha. Ddx and Ddy therefore have independent pixel
        // channels; source alpha cannot hide an incorrect derivative height.
        const std::array<std::uint32_t, 24U> program{
            0xFFFF0200U, 0x0200001FU, 0x80000000U, 0xB0030000U,
            0x0200001FU, 0x90000000U, 0xA00F0800U,
            0x05000051U, 0xA00F0002U, 0U, 0U, 0U, 0x3F800000U,
            0x03000042U, 0x800F0000U, 0xB0E40000U, 0xA0E40800U,
            0x02000001U, 0x80070800U, 0xA05C0000U | input_constant,
            0x02000001U, 0x80080800U, 0xA0FF0002U, 0xFFFFU};
        semantic_scene_builder builder(0x9495U, index + 1U);
        progpu_native_scene_shader_effect_derivatives effect{};
        effect.struct_size = sizeof(effect); effect.version = 3U;
        effect.sampler_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        effect.derivative_register = test.selected_register;
        effect.program.struct_size = sizeof(effect.program); effect.program.version = 1U;
        effect.program.bytecode_size = sizeof(program); effect.program.revision = static_cast<std::uint32_t>(index + 1U);
        for (const auto reg : {0U, 1U, 31U}) {
            const std::array original{0.25F, 0.5F, 0.75F, 1.0F};
            std::copy(original.begin(), original.end(), effect.program.constants + reg * 4U);
        }
        std::uint32_t effect_index{}, brush{};
        require(builder.add_shader_effect(effect, std::as_bytes(std::span(program)), effect_index),
            "original derivative bytecode resource rejected");
        progpu_native_scene_layer layer{};
        layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS;
        layer.bounds = {test.left, 8.0F / test.dpi, test.width, test.height};
        layer.opacity = 1.0F; layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
        layer.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX; layer.effect_resource_index = effect_index;
        layer.content_revision = layer.composite_revision = index + 1U;
        require(builder.push_layer(layer), "derivative input layer rejected");
        require(builder.add_solid_brush({1, 1, 1, 1}, 1, brush), "derivative white input rejected");
        progpu_native_analytic_primitive rectangle{};
        rectangle.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
        rectangle.x = layer.bounds.x; rectangle.y = layer.bounds.y;
        rectangle.width = test.width; rectangle.height = test.height;
        rectangle.color = {1, 1, 1, 1}; rectangle.transform = builder.identity_transform();
        require(builder.draw_analytic({&rectangle, 1U}, {&brush, 1U}, layer.bounds) && builder.pop_layer(),
            "derivative original source draw rejected");
        std::vector<std::byte> stream;
        require(builder.build(stream), "derivative scene serialization rejected");
        std::array<std::vector<std::uint8_t>, 3U> images;
        for (std::uint32_t replay = 0U; replay < images.size(); ++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size = sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size = sizeof(frame);
            images[replay] = render(replay == 2U, stream, index + 1U, test, layers, frame);
            if (test.expected != PROGPU_NATIVE_STATUS_SUCCESS) {
                require(images[replay].empty() && frame.submission_count == 0U,
                    "unsupported derivative capture published rendering");
                continue;
            }
            require(frame.submission_count == 1U && frame.command_count == 3U &&
                layers.effect_pass_count == (replay == 1U ? 0U : 1U) &&
                layers.effect_uniform_upload_bytes == (replay == 1U ? 0U : 528U),
                "derivative binding cold/warm ownership or constants upload differs");
        }
        if (test.expected != PROGPU_NATIVE_STATUS_SUCCESS) continue;
        require(images[0] == images[1] && images[0] == images[2] && images[0].size() == 64U * 64U * 4U,
            "derivative cold/warm/independent output differs");
        const auto physical_width = static_cast<unsigned>(test.width * test.dpi);
        const auto physical_height = static_cast<unsigned>(test.height * test.dpi);
        for (unsigned y = 0U; y < 64U; ++y) for (unsigned x = 0U; x < 64U; ++x) {
            std::array<std::uint8_t, 4U> expected{0, 0, 0, 255};
            if (x >= 8U && x < 8U + physical_width && y >= 8U && y < 8U + physical_height) {
                expected[0] = test.selected_register == 1U ? 64U : static_cast<std::uint8_t>(256U / physical_width);
                expected[1] = test.selected_register == 1U ? 255U : static_cast<std::uint8_t>(256U / physical_height);
                expected[2] = test.selected_register == 1U ? 128U : 0U;
            }
            require(std::equal(expected.begin(), expected.end(), images[0].data() + (y * 64U + x) * 4U),
                "original derivative register, precedence, DPI or device basis differs");
        }
    }
}
} // namespace progpu::native::tests

#pragma once

#include "progpu_native_scene_builder.hpp"

#include <array>
#include <cstdint>
#include <vector>

namespace progpu::native::tests {

// One original shader, one exact source capture and a separate final source
// clip. Independent expected pixels come from white * exact constant over
// opaque black, not another translator or a rendered reference texture.
template<class Render, class Require>
void verify_original_shader_effect_pixels(Render render, Require require) {
    constexpr std::array coefficients{1.0F, 0.5F, 0.5F, 0.25F};
    constexpr std::array<unsigned, 4U> expected_gray{255U, 128U, 255U, 64U};
    for (std::uint32_t variant = 0U; variant < 50U; ++variant) {
        const bool matrix_product = variant >= 10U;
        const auto matrix_group = matrix_product ? (variant - 10U) / 5U : 0U;
        const bool matrix_swapped = (matrix_group & 1U) != 0U;
        const bool matrix_negated = (matrix_group & 2U) != 0U;
        const auto matrix_opcode = 20U + (matrix_product ? (variant - 10U) % 5U : 0U);
        const auto matrix_columns = matrix_opcode <= 21U ? 4U : 3U;
        const auto matrix_rows = matrix_opcode == 20U || matrix_opcode == 22U ? 4U : matrix_opcode == 24U ? 2U : 3U;
        const bool cross_product = variant >= coefficients.size() && !matrix_product;
        const auto cross_axis = cross_product ? (variant - 4U) % 3U : 0U;
        const bool model_three = matrix_product ? matrix_group >= 4U : cross_product ? variant >= 7U : variant >= 2U;
        std::vector<std::uint32_t> program{
            model_three ? 0xFFFF0300U : 0xFFFF0200U,
            0x0200001FU, model_three ? 0x80000005U : 0x80000000U,
            model_three ? 0x90030000U : 0xB0030000U,
            0x0200001FU, 0x90000000U, 0xA00F0800U,
            0x03000042U, 0x800F0000U,
            model_three ? 0x90E40000U : 0xB0E40000U, 0xA0E40800U};
        if (matrix_product) {
            // Preserve unselected destination Z/W from the actual input. The
            // vector's W and unused matrix W distinguish DP3 from DP4, while
            // row ordering produces independent exact magenta output.
            program.insert(program.end(), {
                0x02000001U, 0x800F0001U, 0x80E40000U,
                0x03000005U, 0x800F0000U, 0x80E40000U, 0xA0E40000U,
                0x03000000U | matrix_opcode,
                0x80000001U | (((1U << matrix_rows) - 1U) << 16U) | (matrix_negated ? 0x00100000U : 0U),
                0x80000000U | ((matrix_swapped ? 0xE1U : 0xE4U) << 16U) | (matrix_negated ? 0x01000000U : 0U),
                0xA0E40001U,
                0x02000001U, 0x800F0800U, 0x80E40001U});
        } else if (cross_product) {
            // Actual source texels participate in the vector. CRS writes XYZ
            // into another initialized register, leaving original alpha intact.
            program.insert(program.end(), {
                0x02000001U, 0x800F0001U, 0x80E40000U,
                0x03000005U, 0x800F0000U, 0x80E40000U, 0xA0E40000U,
                0x03000021U, model_three ? 0x80170001U : 0x80070001U,
                0x80E40000U, model_three ? 0xA1E40001U : 0xA0E40001U,
                0x02000001U, 0x800F0800U, 0x80E40001U});
        } else if (variant == 2U) // Same uniforms, different original bytecode.
            program.insert(program.end(), {0x02000001U, 0x800F0800U, 0x80E40000U});
        else
            program.insert(program.end(), {0x03000005U, 0x800F0800U, 0x80E40000U, 0xA0E40000U});
        program.push_back(0xFFFFU);
        const std::uint64_t generation = variant + 1U;
        semantic_scene_builder builder(0x9493U, generation);
        progpu_native_scene_shader_effect shader{};
        shader.struct_size = sizeof(shader); shader.version = 1U;
        shader.bytecode_size = static_cast<std::uint32_t>(program.size() * sizeof(std::uint32_t));
        shader.revision = static_cast<std::uint32_t>(generation);
        shader.sampling_mode = variant & 1U;
        if (matrix_product) {
            shader.constants[0U] = shader.constants[3U] = matrix_negated ? -1.0F : 1.0F;
            const auto component = matrix_swapped ? 1U : 0U;
            for (std::uint32_t row = 0U; row < matrix_rows; ++row) {
                const bool zero = row == 1U;
                shader.constants[4U * (row + 1U) + component] = matrix_columns == 3U && zero ? 0.0F : 1.0F;
                shader.constants[4U * (row + 1U) + 3U] = matrix_columns == 3U ? (zero ? 1.0F : -1.0F) : (zero ? -1.0F : 0.0F);
            }
        } else if (cross_product) {
            shader.constants[(cross_axis + 1U) % 3U] = 1.0F;
            shader.constants[3U] = 1.0F;
            shader.constants[4U + (cross_axis + 2U) % 3U] = model_three ? -1.0F : 1.0F;
        } else {
            for (std::size_t i = 0U; i < 4U; ++i) shader.constants[i] = coefficients[variant];
        }
        std::uint32_t effect{}, brush{}, clip{};
        require(builder.add_shader_effect(shader, std::as_bytes(std::span(program)), effect),
            "original bytecode pixel resource rejected");
        auto state = builder.identity_state();
        state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
        state.clip_rect = {16, 12, 16, 16};
        require(builder.add_state(state, clip), "shader final source clip rejected");
        progpu_native_scene_layer layer{};
        layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS | PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE;
        layer.bounds = {8, 10, 32, 24};
        layer.opacity = 1.0F; layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
        layer.effect_resource_index = effect; layer.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        layer.reserved0 = clip;
        layer.content_revision = layer.composite_revision = generation;
        require(builder.push_layer(layer), "shader pixel layer rejected");
        require(builder.add_solid_brush({1, 1, 1, 1}, 1, brush), "shader white source brush rejected");
        progpu_native_analytic_primitive rectangle{};
        rectangle.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
        rectangle.x = 8; rectangle.y = 10; rectangle.width = 32; rectangle.height = 24;
        rectangle.color = {1, 1, 1, 1}; rectangle.transform = builder.identity_transform();
        require(builder.draw_analytic({&rectangle, 1U}, {&brush, 1U}, layer.bounds) &&
            builder.pop_layer(), "shader source capture rejected");
        std::vector<std::byte> stream;
        require(builder.build(stream), "shader source scene serialization failed");
        std::array<std::vector<std::uint8_t>, 3U> images;
        for (unsigned replay = 0U; replay < images.size(); ++replay) {
            progpu_native_layer_metrics layers{}; layers.struct_size = sizeof(layers);
            progpu_native_scene_frame_metrics frame{}; frame.struct_size = sizeof(frame);
            images[replay] = render(replay == 2U, stream, generation, layers, frame);
            require(layers.effect_kind == PROGPU_NATIVE_GROUP_EFFECT_WPF_SHADER &&
                layers.effect_count == 1U && layers.effect_pass_count == (replay == 1U ? 0U : 1U) &&
                layers.effect_cache_hit == (replay == 1U ? 1U : 0U),
                "shader cold/warm retained execution counts differ");
            require(layers.effect_uniform_upload_bytes == (replay == 1U ? 0U : 528U),
                "shader constants did not retain one original uniform generation");
            require(frame.submission_count == 1U && frame.command_count == 3U,
                "shader scene added a submission or changed the original source commands");
        }
        require(images[0] == images[1] && images[0] == images[2] && images[0].size() == 64U * 64U * 4U,
            "shader cold/warm/independent pixels differ");
        for (unsigned y = 0U; y < 64U; ++y) for (unsigned x = 0U; x < 64U; ++x) {
            std::array<unsigned, 3U> expected{};
            if (x >= 16U && x < 32U && y >= 12U && y < 28U) {
                if (matrix_product) expected = {255U, 0U, 255U};
                else if (cross_product) expected[cross_axis] = 255U;
                else expected.fill(expected_gray[variant]);
            }
            const auto* pixel = images[0].data() + (y * 64U + x) * 4U;
            require(pixel[0] == expected[0] && pixel[1] == expected[1] && pixel[2] == expected[2] && pixel[3] == 255U,
                "original bytecode result, alpha, capture frame or final clip differs");
        }
    }
}
} // namespace progpu::native::tests

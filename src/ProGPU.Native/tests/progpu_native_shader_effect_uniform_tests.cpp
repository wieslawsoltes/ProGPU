#include "progpu_native_scene_builder.hpp"
#include "../src/Scene/progpu_native_scene.hpp"
#include "../src/Scene/progpu_native_shader_effect_resource.hpp"
#include "../src/Scene/progpu_native_shader_effect_uniforms.hpp"
#include "../src/Scene/progpu_native_shader_sample_frame.hpp"

#include <bit>
#include <cstring>
#include <iostream>
#include <limits>

namespace {
bool check(bool value, int line) {
    if (!value) std::cerr << "shader derivative uniform control failed at " << line << '\n';
    return value;
}
#define UV_REQUIRE(value) do { if (!check((value), __LINE__)) return false; } while (false)

bool source_sample_frame_arithmetic() {
    using namespace progpu::native::shader_effect;
    const auto bits = [](float value) { return std::bit_cast<std::uint32_t>(value); };
    axis_matrix inverse{};
    UV_REQUIRE(inverse_axis_matrix({1.25F, 1.25F}, inverse));
    UV_REQUIRE(bits(inverse.x) == 0x3F4CCCCCU && bits(1.0F / 1.25F) == 0x3F4CCCCDU);
    UV_REQUIRE(bits(source_product(inverse.x, 1.25F)) == 0x3F7FFFFFU);
    UV_REQUIRE(inverse_axis_matrix({1.25F, 1.5F}, inverse));
    UV_REQUIRE(bits(source_product(inverse.x, 1.25F)) == 0x3F800001U);
    UV_REQUIRE(inverse_axis_matrix({std::nextafter(1.25F, 2.0F), 1.25F}, inverse));
    UV_REQUIRE(bits(inverse.w) == 0x3F7FFFFFU);
    sample_frame result{};
    sample_frame_request request{16.75F, 16.25F, 32.25F, 23.75F, {1, 1, 1, 1, 2, 3}};
    UV_REQUIRE(create_sample_frame(request, result));
    UV_REQUIRE((result.capture == sample_lattice{16, 16, 17, 8}));
    UV_REQUIRE((result.output == sample_lattice{18, 19, 17, 8}));
    UV_REQUIRE(result.unit_to_device.x == 17 && result.unit_to_device.y == 8 &&
        result.unit_to_device.tx == 18 && result.unit_to_device.ty == 19 && result.unit_to_device.w == 1);
    sample_projection projection{};
    UV_REQUIRE(project_sample_frame(result, {0, 0, 64U, 64U}, projection));
    UV_REQUIRE(projection.unit_to_clip.x == .53125F && projection.unit_to_clip.y == -.25F &&
        projection.unit_to_clip.tx == -.453125F && projection.unit_to_clip.ty == .421875F);
    UV_REQUIRE(project_sample_frame(result, {8, 10, 32U, 16U}, projection));
    UV_REQUIRE(projection.unit_to_clip.x == 1.0625F && projection.unit_to_clip.y == -1.0F &&
        projection.unit_to_clip.tx == -.40625F && projection.unit_to_clip.ty == -.0625F);
    const auto original_projection = projection;
    UV_REQUIRE(!project_sample_frame(result, {8, 10, 0U, 16U}, projection));
    UV_REQUIRE(std::memcmp(&original_projection, &projection, sizeof(projection)) == 0);
    request.source_to_device.tx = 2.25F; request.source_to_device.ty = 3.5F;
    UV_REQUIRE(create_sample_frame(request, result));
    UV_REQUIRE((result.capture == sample_lattice{16, 16, 17, 8}));
    UV_REQUIRE((result.output == sample_lattice{18, 19, 18, 9}));
    UV_REQUIRE(result.unit_to_device.tx == 18.25F && result.unit_to_device.ty == 19.5F);
    request.source_to_device = {1.25F, 1.25F, 1, 1, 2.5F, 3.75F};
    UV_REQUIRE(create_sample_frame(request, result));
    UV_REQUIRE((result.capture == sample_lattice{20, 20, 21, 10}));
    UV_REQUIRE(bits(result.residual.x) == 0x3F7FFFFFU && result.residual.w == 1.0F);
    // No out-of-band source or output mutation on a later invalid frame.
    const auto before = result;
    for (std::uint32_t variant = 0U; variant < 8U; ++variant) {
        auto invalid = request;
        if (variant == 0U) invalid.source_to_device.x = 0.0F;
        if (variant == 1U) invalid.source_to_device.y = -1.0F;
        if (variant == 2U) invalid.source_to_device.w = .5F;
        if (variant == 3U) invalid.local_left = std::numeric_limits<float>::quiet_NaN();
        if (variant == 4U) invalid.local_right = invalid.local_left;
        if (variant == 5U) invalid.local_right = 20'000.0F;
        if (variant == 6U) invalid.source_to_device.tx = std::numeric_limits<float>::infinity();
        if (variant == 7U) invalid.source_to_device.x = std::numeric_limits<float>::max();
        UV_REQUIRE(!create_sample_frame(invalid, result));
        UV_REQUIRE(std::memcmp(&result, &before, sizeof(result)) == 0);
    }
    return true;
}
}

bool run_shader_effect_uniform_tests() {
    using namespace progpu::native;
    UV_REQUIRE(source_sample_frame_arithmetic());
    static_assert(sizeof(progpu_native_scene_shader_effect) == 544U);
    static_assert(sizeof(progpu_native_scene_shader_effect_picture) == 560U);
    static_assert(sizeof(progpu_native_scene_shader_effect_derivatives) == 576U);
    static_assert(sizeof(progpu_native_scene_shader_sample_frame) == 128U);
    static_assert(sizeof(progpu_native_scene_shader_effect_samples) == 704U);
    static_assert(offsetof(progpu_native_scene_shader_effect_samples, frame) == 32U);
    static_assert(offsetof(progpu_native_scene_shader_effect_samples, program) == 160U);
    constexpr std::array<std::uint32_t, 15U> tokens{
        0xFFFF0200U, 0x0200001FU, 0x80000000U, 0xB0030000U,
        0x0200001FU, 0x90000000U, 0xA00F0800U,
        0x03000042U, 0x800F0000U, 0xB0E40000U, 0xA0E40800U,
        0x02000001U, 0x800F0800U, 0xA0E40000U, 0xFFFFU};
    const auto bytecode = std::as_bytes(std::span(tokens));
    progpu_native_scene_shader_effect program{};
    program.struct_size = sizeof(program); program.version = 1U;
    program.revision = 1U; program.bytecode_size = sizeof(tokens);
    for (std::size_t i = 0U; i < 128U; ++i) program.constants[i] = static_cast<float>(i + 1U);
    const auto original = program;
    // V5 keeps physical clip and final output independent of captured storage.
    // These are authored wire/atomicity controls, not provider execution evidence.
    progpu_native_scene_shader_effect_samples sampled{};
    sampled.struct_size = sizeof(sampled); sampled.version = 5U;
    sampled.input_resource_index = 0U;
    sampled.sampler_resource_index = sampled.derivative_register = PROGPU_NATIVE_SCENE_NO_INDEX;
    sampled.program = program;
    sampled.frame.local_left = 16.75F; sampled.frame.local_top = 16.25F;
    sampled.frame.local_right = 32.25F; sampled.frame.local_bottom = 23.75F;
    sampled.frame.source_scale_x = sampled.frame.source_scale_y = 1.25F;
    sampled.frame.source_offset_x = 2.25F; sampled.frame.source_offset_y = 3.5F;
    sampled.frame.source_dpi_x = sampled.frame.source_dpi_y = 1.25;
    sampled.frame.clip_left = 20; sampled.frame.clip_top = 21;
    sampled.frame.clip_right = 42; sampled.frame.clip_bottom = 33;
    UV_REQUIRE(shader_effect::complete_sample_frame(sampled.frame));
    UV_REQUIRE(shader_effect::validate_sample_frame(sampled.frame));
    for (std::uint32_t variant = 0U; variant < 20U; ++variant) {
        auto wire = sampled;
        if (variant == 1U) wire.struct_size -= 4U;
        if (variant == 2U) wire.version = 4U;
        if (variant == 3U) wire.input_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (variant == 4U) wire.flags = 1U;
        if (variant == 5U) wire.reserved[0] = 1U;
        if (variant == 6U) wire.reserved[1] = 1U;
        if (variant == 7U) wire.frame.capture_x++;
        if (variant == 8U) wire.frame.capture_width++;
        if (variant == 9U) wire.frame.output_x++;
        if (variant == 10U) wire.frame.output_height++;
        if (variant == 11U) wire.frame.quad_w = std::nextafter(wire.frame.quad_w, 2.0F);
        if (variant == 12U) wire.frame.quad_offset_x = std::nextafter(wire.frame.quad_offset_x, 100.0F);
        if (variant == 13U) wire.frame.clip_antialias = 2U;
        if (variant == 14U) wire.frame.clip_left += .25F;
        if (variant == 15U) wire.frame.clip_top = std::numeric_limits<float>::quiet_NaN();
        if (variant == 16U) wire.frame.clip_right = wire.frame.clip_left - 1.0F;
        if (variant == 17U) wire.frame.reserved = 1U;
        if (variant == 18U) wire.frame.source_dpi_y = 0.0;
        if (variant == 19U) wire.derivative_register = 32U;
        progpu_native_scene_shader_effect read_program = original;
        progpu_native_scene_shader_capture_frame old_frame{};
        progpu_native_scene_shader_sample_frame new_frame{};
        std::uint32_t sampler = 91U, derivative = 92U, input = 93U;
        const bool accepted = shader_effect::read_resource(std::as_bytes(std::span(&wire, 1U)), bytecode,
            read_program, sampler, derivative, old_frame, input, new_frame);
        UV_REQUIRE(accepted == (variant == 0U));
        if (!accepted) {
            UV_REQUIRE(sampler == 91U && derivative == 92U && input == 93U);
            UV_REQUIRE(std::memcmp(&read_program, &original, sizeof(original)) == 0);
        }
        UV_REQUIRE(!shader_effect::read_resource(std::as_bytes(std::span(&wire, 1U)), bytecode,
            read_program, sampler, derivative, old_frame));
    }
    progpu_native_scene_shader_effect_derivatives metadata{
        sizeof(metadata), 3U, PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, {0U, 0U, 0U}, program};
    semantic_scene_builder builder(0x9590U, 1U);
    std::uint32_t effect{};
    UV_REQUIRE(builder.add_shader_effect(metadata, bytecode, effect));
    std::vector<std::byte> stream;
    UV_REQUIRE(builder.build(stream));
    UV_REQUIRE(scene::validate(stream.data(), stream.size()).status == PROGPU_NATIVE_STATUS_SUCCESS);
    progpu_native_scene_header header{};
    std::memcpy(&header, stream.data(), sizeof(header));
    progpu_native_scene_resource resource{};
    std::memcpy(&resource, stream.data() + header.resource_offset + effect * header.resource_stride, sizeof(resource));
    for (std::uint32_t variant = 0U; variant < 43U; ++variant) {
        auto invalid = metadata;
        if (variant < 32U) invalid.flags = 1U << variant;
        else if (variant == 32U) invalid.version = 4U;
        else if (variant == 33U) invalid.struct_size -= 4U;
        else if (variant < 37U) invalid.reserved[variant - 34U] = 1U;
        else if (variant == 37U) invalid.derivative_register = 32U;
        else if (variant == 38U) invalid.derivative_register = PROGPU_NATIVE_SCENE_NO_INDEX;
        else if (variant == 39U) invalid.program.version = 3U;
        else if (variant == 40U) invalid.program.reserved = 1U;
        else if (variant == 41U) invalid.sampler_resource_index = effect; // self
        else invalid.sampler_resource_index = header.resource_count; // forward/foreign
        auto candidate = program; candidate.revision = 123U;
        std::uint32_t picture = 123U, reg = 456U;
        if (variant < 41U) {
            UV_REQUIRE(!shader_effect::read_resource(std::as_bytes(std::span(&invalid, 1U)), bytecode,
                candidate, picture, reg));
            UV_REQUIRE(candidate.revision == 123U && picture == 123U && reg == 456U);
        }
        std::uint32_t rejected = 42U;
        UV_REQUIRE(!builder.add_shader_effect(invalid, bytecode, rejected));
        UV_REQUIRE(rejected == PROGPU_NATIVE_SCENE_NO_INDEX);
        auto damaged = stream;
        std::memcpy(damaged.data() + resource.payload_offset, &invalid, sizeof(invalid));
        UV_REQUIRE(scene::validate(damaged.data(), damaged.size()).status != PROGPU_NATIVE_STATUS_SUCCESS);
    }
    progpu_native_scene_shader_effect parsed{};
    std::uint32_t picture = 42U, reg = 42U;
    UV_REQUIRE(shader_effect::read_resource(std::as_bytes(std::span(&metadata, 1U)), bytecode, parsed, picture, reg));
    UV_REQUIRE(reg == 0U && picture == PROGPU_NATIVE_SCENE_NO_INDEX &&
        std::memcmp(&parsed, &original, sizeof(parsed)) == 0);
    parsed.revision = 123U; picture = 456U;
    UV_REQUIRE(!shader_effect::read_resource(std::as_bytes(std::span(&metadata, 1U)), bytecode, parsed, picture));
    UV_REQUIRE(parsed.revision == 123U && picture == 456U); // old reader cannot discard v3 metadata
    for (const auto selected : {0U, 1U, 31U}) {
        for (const auto size : std::array{std::array{1U, 2U}, std::array{16U, 32U},
                std::array{32U, 16U}, std::array{1024U, 16384U}}) {
            std::array<float, 128U> output{};
            UV_REQUIRE(shader_effect::prepare_constants(program, selected, size[0], size[1], output));
            const auto offset = selected * 4U;
            for (std::size_t i = 0U; i < output.size(); ++i) {
                const auto expected = i == offset ? 1.0F / size[0] : i == offset + 3U ? 1.0F / size[1] :
                    i == offset + 1U || i == offset + 2U ? 0.0F : original.constants[i];
                UV_REQUIRE(output[i] == expected);
            }
        }
    }
    std::array<float, 128U> untouched{}; untouched.fill(42.0F);
    const auto before = untouched;
    UV_REQUIRE(!shader_effect::prepare_constants(program, 32U, 16U, 32U, untouched) && untouched == before);
    UV_REQUIRE(!shader_effect::prepare_constants(program, 0U, 0U, 32U, untouched) && untouched == before);
    UV_REQUIRE(!shader_effect::prepare_constants(program, 0U, 16U, 0U, untouched) && untouched == before);
    UV_REQUIRE(!shader_effect::prepare_constants(program, 0U, (1U << 24U) + 1U, 1U, untouched) && untouched == before);
    UV_REQUIRE(shader_effect::prepare_constants(program, PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, untouched));
    UV_REQUIRE(std::equal(untouched.begin(), untouched.end(), std::begin(original.constants)));
    UV_REQUIRE(std::memcmp(&program, &original, sizeof(program)) == 0); // immutable source snapshot
    return true;
}

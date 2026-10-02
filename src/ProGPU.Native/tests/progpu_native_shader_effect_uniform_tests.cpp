#include "progpu_native_scene_builder.hpp"
#include "../src/Scene/progpu_native_scene.hpp"
#include "../src/Scene/progpu_native_shader_effect_resource.hpp"
#include "../src/Scene/progpu_native_shader_effect_uniforms.hpp"

#include <cstring>
#include <iostream>

namespace {
bool check(bool value, int line) {
    if (!value) std::cerr << "shader derivative uniform control failed at " << line << '\n';
    return value;
}
#define UV_REQUIRE(value) do { if (!check((value), __LINE__)) return false; } while (false)
}

bool run_shader_effect_uniform_tests() {
    using namespace progpu::native;
    static_assert(sizeof(progpu_native_scene_shader_effect) == 544U);
    static_assert(sizeof(progpu_native_scene_shader_effect_picture) == 560U);
    static_assert(sizeof(progpu_native_scene_shader_effect_derivatives) == 576U);
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

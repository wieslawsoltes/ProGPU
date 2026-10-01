#include "progpu_native_shader_effect.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <limits>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

// Independently authored D3D9 token fixtures, not compiler output or another
// project's translator. These exercise translation/validation only: no device,
// shader compilation, native renderer, pixel evaluator or source UI is involved.
namespace {
using words = std::vector<std::uint32_t>;

constexpr std::uint32_t reg(std::uint32_t type, std::uint32_t number) {
    return 0x80000000U | ((type & 7U) << 28U) | ((type & 24U) << 8U) | number;
}
constexpr std::uint32_t dst(std::uint32_t type, std::uint32_t number,
                            std::uint32_t mask = 15U, std::uint32_t modifier = 0U) {
    return reg(type, number) | (mask << 16U) | (modifier << 20U);
}
constexpr std::uint32_t src(std::uint32_t type, std::uint32_t number,
                            std::uint32_t swizzle = 0xE4U, std::uint32_t modifier = 0U) {
    return reg(type, number) | (swizzle << 16U) | (modifier << 24U);
}

words sample(std::uint32_t input = 0U, std::uint32_t sampler = 0U) {
    // ps_3_0; dcl_texcoord0 v#.xy; dcl_2d s#;
    // texld r0, v#, s#; mov oC0, r0; end
    return {0xFFFF0300U, 0x0200001FU, 0x80000005U, dst(1U, input, 3U),
        0x0200001FU, 0x90000000U, dst(10U, sampler),
        0x03000042U, dst(0U, 0U), src(1U, input), src(10U, sampler),
        0x02000001U, dst(8U, 0U), src(0U, 0U), 0x0000FFFFU};
}

void require(bool value, std::string_view name) {
    if (!value) throw std::runtime_error(std::string(name));
}

struct controls {
    std::uint32_t count{};

    std::string accept(const words& program, std::uint32_t sampler = 0U) {
        const auto original = program;
        const auto bytes = std::as_bytes(std::span(program));
        std::string output = "previous validated program";
        require(progpu::native::shader_effect::translate(bytes, sampler), "validation rejected valid input");
        require(progpu::native::shader_effect::translate(bytes, sampler, &output), "translation rejected valid input");
        require(program == original, "translation mutated original bytecode");
        require(output != "previous validated program", "translation did not publish");
        ++count;
        return output;
    }

    void reject(const words& program, std::string_view name, std::uint32_t sampler = 0U) {
        const auto original = program;
        const auto bytes = std::as_bytes(std::span(program));
        std::string output = "previous validated program";
        require(!progpu::native::shader_effect::translate(bytes, sampler), name);
        require(!progpu::native::shader_effect::translate(bytes, sampler, &output), name);
        require(output == "previous validated program", "rejection published partial shader text");
        require(program == original, "rejection mutated original bytecode");
        ++count;
    }

    void replace(std::size_t index, std::uint32_t token, std::string_view name) {
        auto program = sample();
        program[index] = token;
        reject(program, name);
    }
};

void input_controls(controls& test) {
    const auto output = test.accept(sample());
    require(output ==
        "let input_v0 = t0;\n"
        "let value3 = wpf_sample_input((input_v0.xyzw).xy);\n"
        "r[0].x = value3.x;\nr[0].y = value3.y;\nr[0].z = value3.z;\nr[0].w = value3.w;\n"
        "let value4 = r[0].xyzw;\n"
        "o.x = value4.x;\no.y = value4.y;\no.z = value4.z;\no.w = value4.w;\n",
        "exact declared-input sample translation");
    for (std::uint32_t input = 1U; input < 10U; ++input) {
        const auto body = test.accept(sample(input, 15U), 15U);
        require(body.find("wpf_sample_input((input_v" + std::to_string(input) + ".xyzw).xy)") != std::string::npos,
            "declared input register identity was lost");
    }
    auto swizzled = sample(7U);
    swizzled[9U] = src(1U, 7U, 0x01U); // yxxx: only first two lanes are sampled.
    require(test.accept(swizzled).find("(input_v7.yxxx).xy") != std::string::npos, "UV swizzle lost");

    auto ps2 = sample();
    ps2[0U] = 0xFFFF0200U;
    ps2[2U] = 0x80000000U;
    ps2[3U] = dst(3U, 0U, 3U);
    ps2[9U] = src(3U, 0U);
    require(test.accept(ps2).find("wpf_sample_input((t0.xyzw).xy)") != std::string::npos,
        "original ps_2_0 input no longer translates");
    ps2[0U] = 0xFFFF0300U;
    test.reject(ps2, "ps_3_0 reused ps_2_0 t0 declaration");
    test.replace(0U, 0xFFFF0200U, "ps_2_0 accepted ps_3_0 input declaration");
    test.replace(0U, 0xFFFE0300U, "vertex shader admitted");
    test.replace(0U, 0xFFFF0301U, "different minor version admitted");
    test.replace(0U, 0xFFFF03FFU, "software shader admitted");
    test.replace(2U, 0x80010005U, "TEXCOORD1 admitted");
    test.replace(2U, 0x8000000AU, "COLOR0 admitted");
    test.replace(2U, 0x80000000U, "POSITION0 admitted");
    test.replace(2U, 0x80000025U, "reserved semantic bits admitted");
    test.replace(2U, 0x00000005U, "missing declaration marker admitted");
    test.replace(3U, dst(1U, 10U, 3U), "input register overflow admitted");
    for (const auto mask : {0U, 1U, 2U, 4U, 12U, 15U})
        test.replace(3U, dst(1U, 0U, mask), "unsupported input component mapping admitted");
    test.replace(3U, dst(1U, 0U, 3U, 4U), "centroid declaration admitted");
    test.replace(3U, dst(3U, 0U, 3U), "ps_3_0 declared t0 admitted");
    test.replace(9U, src(3U, 0U), "ps_3_0 read t0 admitted");
    test.replace(9U, src(1U, 1U), "undeclared input register admitted");
    test.replace(9U, src(1U, 0U, 0xEEU), "undeclared z/w source lanes admitted");
    auto missing = sample();
    missing.erase(missing.begin() + 1, missing.begin() + 4);
    test.reject(missing, "missing input declaration admitted");
    auto duplicate = sample();
    duplicate.insert(duplicate.begin() + 4, {0x0200001FU, 0x80000005U, dst(1U, 1U, 3U)});
    test.reject(duplicate, "duplicate TEXCOORD0 semantic admitted");
    auto late = sample();
    late.insert(late.end() - 1, {0x0200001FU, 0x80000005U, dst(1U, 1U, 3U)});
    test.reject(late, "late declaration admitted");
}

void operation_controls(controls& test) {
    struct operation { std::uint32_t opcode, sources; std::string_view expression; };
    constexpr std::array<operation, 13U> operations{{
        operation{1U, 1U, "r[0].xyzw"}, {2U, 2U, "r[0].xyzw + c[31].xyzw"},
        {3U, 2U, "r[0].xyzw - c[31].xyzw"}, {4U, 3U, "r[0].xyzw * c[31].xyzw + c[31].xyzw"},
        {5U, 2U, "r[0].xyzw * c[31].xyzw"},
        {8U, 2U, "vec4<f32>(dot((r[0].xyzw).xyz, (c[31].xyzw).xyz))"},
        {9U, 2U, "vec4<f32>(dot(r[0].xyzw, c[31].xyzw))"},
        {10U, 2U, "min(r[0].xyzw, c[31].xyzw)"}, {11U, 2U, "max(r[0].xyzw, c[31].xyzw)"},
        {18U, 3U, "r[0].xyzw * c[31].xyzw + (vec4<f32>(1.0) - r[0].xyzw) * c[31].xyzw"},
        {19U, 1U, "fract(r[0].xyzw)"}, {35U, 1U, "abs(r[0].xyzw)"},
        {88U, 3U, "select(c[31].xyzw, c[31].xyzw, r[0].xyzw >= vec4<f32>(0.0))"}}};
    for (const auto& op : operations) {
        auto program = sample();
        program.resize(11U);
        program.insert(program.end(), {((op.sources + 1U) << 24U) | op.opcode, dst(8U, 0U), src(0U, 0U)});
        if (op.sources >= 2U) program.push_back(src(2U, 31U));
        if (op.sources == 3U) program.push_back(src(2U, 31U));
        program.push_back(0xFFFFU);
        require(test.accept(program).find("let value4 = " + std::string(op.expression) + ";") != std::string::npos,
            "straight-line operation expression changed");
    }
    for (const auto modifier : {0U, 1U, 11U, 12U}) {
        auto program = sample();
        program[12U] = dst(8U, 0U, 15U, 1U);
        program[13U] = src(0U, 0U, 0x1BU, modifier); // wzyx, saturated output
        const auto body = test.accept(program);
        const std::string expression = modifier == 0U ? "r[0].wzyx" : modifier == 1U ?
            "(-r[0].wzyx)" : modifier == 11U ? "abs(r[0].wzyx)" : "(-abs(r[0].wzyx))";
        require(body.find("clamp(" + expression + ", vec4<f32>(0.0), vec4<f32>(1.0))") != std::string::npos,
            "source modifier, swizzle or saturation lost");
    }
    auto partial = sample();
    partial[12U] = dst(8U, 0U, 3U);
    partial.insert(partial.end() - 1, {0x02000001U, dst(8U, 0U, 12U), src(0U, 0U)});
    test.accept(partial);
    partial.erase(partial.end() - 4, partial.end() - 1);
    test.reject(partial, "partial output admitted");
    auto last_temp = sample();
    last_temp[8U] = dst(0U, 11U);
    last_temp[13U] = src(0U, 11U);
    test.accept(last_temp);
    test.replace(8U, dst(0U, 12U), "version-1 temporary budget enlarged");
    test.replace(13U, src(2U, 32U), "version-1 float constant budget enlarged");
    test.replace(13U, src(0U, 1U), "unwritten temporary admitted");
    test.replace(12U, dst(8U, 1U), "additional color output admitted");
    test.replace(12U, dst(9U, 0U), "depth output admitted");
    test.replace(12U, dst(0U, 0U), "missing color output admitted");
    test.replace(12U, dst(8U, 0U, 0U), "empty write mask admitted");
    test.replace(12U, dst(8U, 0U, 15U, 2U), "partial-precision destination admitted");
    auto read_ports = sample();
    read_ports.resize(11U);
    read_ports.insert(read_ports.end(), {0x03000002U, dst(8U, 0U), src(2U, 0U), src(2U, 1U), 0xFFFFU});
    test.reject(read_ports, "multiple float-constant read ports admitted");
    for (const auto type : {3U, 7U, 14U, 15U, 17U, 19U})
        test.replace(13U, src(type, 0U), "unimplemented register family admitted");
    test.replace(13U, src(2U, 0U) | 0x2000U, "relative addressing admitted");
    test.replace(13U, src(0U, 0U, 0xE4U, 2U), "unsupported source modifier admitted");
    test.replace(11U, 0x12000001U, "predicated instruction admitted");
    test.replace(11U, 0x42000001U, "coissued instruction admitted");
    for (const auto opcode : {27U, 40U, 48U, 47U, 65U, 91U, 92U, 93U, 95U})
        test.replace(11U, 0x02000000U | opcode, "flow/definition/texture/derivative opcode admitted");
}

void texture_and_boundary_controls(controls& test) {
    test.replace(5U, 0x98000000U, "cube sampler admitted");
    test.replace(6U, dst(10U, 1U), "unbound sampler declaration admitted");
    test.replace(10U, src(10U, 1U), "unbound sampler read admitted");
    test.replace(10U, src(10U, 0U, 0x1BU), "unsupported sampler swizzle admitted");
    test.replace(9U, src(1U, 0U, 0xE4U, 1U), "modified texture coordinates admitted");
    test.replace(9U, src(2U, 0U), "constant texture coordinate form admitted");
    test.replace(8U, dst(8U, 0U), "texture form writing color output admitted");
    test.replace(7U, 0x03010042U, "projected TEXLD admitted");
    test.replace(7U, 0x03020042U, "biased TEXLD admitted");
    test.reject(sample(), "out-of-range source sampler admitted", 16U);
    auto definition = sample();
    definition.insert(definition.begin() + 7, {0x05000051U, dst(2U, 31U), 0x80000000U,
        0x3F000000U, 0xBF800000U, 0x00000001U}); // -0, 0.5, -1, positive subnormal
    require(test.accept(definition).find("c[31] = vec4<f32>(bitcast<f32>(2147483648u), bitcast<f32>(1056964608u), "
        "bitcast<f32>(3212836864u), bitcast<f32>(1u));") != std::string::npos, "DEF bits changed");
    definition[9U] = 0x7F800000U;
    test.reject(definition, "infinite DEF admitted");
    auto comment = sample();
    comment.insert(comment.begin() + 1, {0x0002FFFEU, 0xFFFFU, 0xFFFF0200U});
    require(test.accept(comment) == test.accept(sample()), "comment contents interpreted as instructions");
    auto bounded = sample();
    bounded.insert(bounded.end() - 1, 508U, 0U); // 2 DCL + TEXLD + MOV + 508 NOP
    test.accept(bounded);
    bounded.insert(bounded.end() - 1, 0U);
    test.reject(bounded, "instruction budget enlarged");
    auto maximum = sample();
    constexpr std::size_t payload = 65536U / 4U - 16U;
    maximum.insert(maximum.begin() + 1, payload + 1U, 0U);
    maximum[1U] = (static_cast<std::uint32_t>(payload) << 16U) | 0xFFFEU;
    test.accept(maximum);
    maximum.insert(maximum.end() - 1, 0U);
    test.reject(maximum, "bytecode budget enlarged");
    test.replace(7U, 0x04000042U, "wrong instruction length admitted");
    test.replace(14U, 0x0100FFFFU, "malformed END admitted");
    auto trailing = sample();
    trailing.push_back(0U);
    test.reject(trailing, "trailing tokens admitted");
    auto truncated = sample();
    truncated.pop_back();
    test.reject(truncated, "missing END admitted");
    truncated = {0xFFFF0300U, 0x0003FFFEU, 0U};
    test.reject(truncated, "truncated comment admitted");
    const auto program = sample();
    const auto bytes = std::as_bytes(std::span(program));
    std::string untouched = "retained";
    require(!progpu::native::shader_effect::translate(bytes.first(bytes.size() - 1U), 0U, &untouched)
        && untouched == "retained", "unaligned byte count published output");
    ++test.count;
    progpu_native_scene_shader_effect effect{};
    effect.struct_size = sizeof(effect); effect.version = 1U; effect.revision = 1U;
    effect.bytecode_size = static_cast<std::uint32_t>(bytes.size());
    require(progpu::native::shader_effect::validate(effect, bytes), "version-1 resource rejected ps_3_0");
    effect.constants[127U] = std::numeric_limits<float>::infinity();
    require(!progpu::native::shader_effect::validate(effect, bytes), "nonfinite live constant admitted");
    ++test.count;
}
} // namespace

bool run_shader_effect_translation_tests() {
    try {
        controls test;
        input_controls(test);
        operation_controls(test);
        texture_and_boundary_controls(test);
        std::cout << "shader effect original-bytecode translation controls: " << test.count << " passed\n";
        return true;
    } catch (const std::exception& error) {
        std::cerr << "shader effect translation control failed: " << error.what() << '\n';
        return false;
    }
}

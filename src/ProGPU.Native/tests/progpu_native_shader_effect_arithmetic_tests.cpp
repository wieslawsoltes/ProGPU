#include "progpu_native_shader_effect.hpp"

#include <array>
#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <limits>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

// Original token fixtures and exact emission checks, not a CPU pixel evaluator.
// A separate callable entry point lets the owning native test registry wire this
// batch without replacing the existing shader-model-three controls.
namespace shader_arithmetic_controls {
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

words prefix(bool three) {
    return {three ? 0xFFFF0300U : 0xFFFF0200U,
        0x0200001FU, three ? 0x80000005U : 0x80000000U, dst(three ? 1U : 3U, 0U, 3U),
        0x0200001FU, 0x90000000U, dst(10U, 0U),
        0x03000042U, dst(0U, 0U), src(three ? 1U : 3U, 0U), src(10U, 0U)};
}

void definition(words& program, std::uint32_t number, std::array<float, 4U> values) {
    program.insert(program.begin() + 7, {0x05000051U, dst(2U, number),
        std::bit_cast<std::uint32_t>(values[0]), std::bit_cast<std::uint32_t>(values[1]),
        std::bit_cast<std::uint32_t>(values[2]), std::bit_cast<std::uint32_t>(values[3])});
}

void instruction(words& program, std::uint32_t opcode, std::uint32_t destination,
                 std::initializer_list<std::uint32_t> sources) {
    program.push_back((static_cast<std::uint32_t>(sources.size() + 1U) << 24U) | opcode);
    program.push_back(destination);
    program.insert(program.end(), sources);
}

void finish(words& program, std::uint32_t source = src(0U, 0U)) {
    instruction(program, 1U, dst(8U, 0U), {source});
    program.push_back(0xFFFFU);
}

void require(bool value, std::string_view name) {
    if (!value) throw std::runtime_error(std::string(name));
}

struct controls {
    std::uint32_t count{};
    std::string accept(const words& program) {
        const auto original = program;
        std::string body = "previous program";
        const auto bytes = std::as_bytes(std::span(program));
        require(progpu::native::shader_effect::translate(bytes, 0U), "valid arithmetic rejected by validator");
        require(progpu::native::shader_effect::translate(bytes, 0U, &body), "valid arithmetic rejected by emitter");
        require(program == original && body != "previous program", "accept changed original bytes or did not publish");
        ++count;
        return body;
    }
    void reject(const words& program, std::string_view name) {
        const auto original = program;
        std::string body = "previous program";
        const auto bytes = std::as_bytes(std::span(program));
        require(!progpu::native::shader_effect::translate(bytes, 0U), name);
        require(!progpu::native::shader_effect::translate(bytes, 0U, &body), name);
        require(program == original && body == "previous program", "rejection changed bytes or published partial text");
        ++count;
    }
};

void log_and_dot(controls& test, bool three) {
    for (std::uint32_t lane = 0U; lane < 4U; ++lane) {
        for (const auto modifier : {0U, 1U, 11U, 12U}) {
            auto program = prefix(three);
            instruction(program, 15U, dst(0U, 0U, 10U, 1U), {src(0U, 0U, lane * 0x55U, modifier)});
            finish(program);
            const auto body = test.accept(program);
            require(body.find("var log_result4 = bitcast<f32>(4286578687u);\nif (log_magnitude4 != 0u)") != std::string::npos,
                "LOG must guard exact -FLT_MAX zero result before log2");
            require(body.find("if (log_magnitude4 < 8388608u)") != std::string::npos &&
                body.find("log2(f32(log_magnitude4)) - 149.0") != std::string::npos,
                "LOG subnormal input was not normalized before log2");
            require(body.find("let value4 = clamp(vec4<f32>(log_result4), vec4<f32>(0.0), vec4<f32>(1.0));\n"
                "r[0].y = value4.y;\nr[0].w = value4.w;") != std::string::npos,
                "LOG scalar replication/SAT/write mask changed");
        }
    }
    for (const float value : {0.0F, -0.0F, 1.0F, -1.0F,
                              std::numeric_limits<float>::denorm_min(), -std::numeric_limits<float>::max()}) {
        auto program = prefix(three);
        definition(program, 31U, {value, value, value, value});
        instruction(program, 15U, dst(0U, 0U), {src(2U, 31U, 0U)});
        finish(program);
        test.accept(program); // This checks original bits/translation, not GPU numeric results.
    }
    for (const auto swizzle : {0xE4U, 0x54U, 0x01U}) {
        auto program = prefix(three);
        instruction(program, 15U, dst(0U, 0U), {src(0U, 0U, swizzle)});
        finish(program);
        test.reject(program, "LOG nonreplicate source accepted");
    }
    for (std::uint32_t mask = 1U; mask < 16U; ++mask) {
        auto program = prefix(three);
        // Only r1.xy and r2.w are defined. Destination W still needs src0/1.xy.
        instruction(program, 1U, dst(0U, 1U, 3U), {src(0U, 0U)});
        instruction(program, 1U, dst(0U, 2U, 8U), {src(0U, 0U)});
        instruction(program, 90U, dst(0U, 0U, mask), {src(0U, 1U), src(0U, 0U, 0xB1U, 1U), src(0U, 2U, 0xFFU)});
        finish(program);
        require(test.accept(program).find("vec4<f32>(dot((r[1].xyzw).xy, ((-r[0].yxwz)).xy) + (r[2].wwww).x)")
            != std::string::npos, "DP2ADD lane dependencies, modifiers or scalar replication changed");
        program[12U] = dst(0U, 1U, 1U);
        test.reject(program, "DP2ADD read unwritten Y despite narrow destination");
    }
    auto bad_scalar = prefix(three);
    instruction(bad_scalar, 90U, dst(0U, 0U), {src(0U, 0U), src(0U, 0U), src(0U, 0U)});
    finish(bad_scalar);
    test.reject(bad_scalar, "DP2ADD scalar addend did not require replicate swizzle");
    auto alias = prefix(three);
    instruction(alias, 90U, dst(0U, 0U), {src(0U, 0U, 0x1BU), src(0U, 0U), src(0U, 0U, 0xAAU)});
    finish(alias);
    const auto alias_body = test.accept(alias);
    require(alias_body.find("let value4 = vec4<f32>(dot((r[0].wzyx).xy, (r[0].xyzw).xy) + (r[0].zzzz).x);\n"
        "r[0].x = value4.x;\nr[0].y = value4.y;") != std::string::npos,
        "DP2ADD wrote an aliased destination before snapshotting all sources");
}

words nrm_program(bool three, std::array<float, 4U> values, std::uint32_t mask = 15U,
                  std::uint32_t swizzle = 0xE4U, std::uint32_t modifier = 0U) {
    auto program = prefix(three);
    definition(program, 31U, values);
    instruction(program, 36U, dst(0U, 0U, mask), {src(2U, 31U, swizzle, modifier)});
    finish(program);
    return program;
}

void bounded_normal(controls& test, bool three) {
    for (const auto modifier : {0U, 1U, 11U, 12U}) {
        const auto body = test.accept(nrm_program(three, {3.0F, -4.0F, 0.0F, 0.5F}, 15U, 0xE4U, modifier));
        require(body.find("dot(normal_source5.xyz, normal_source5.xyz)") != std::string::npos &&
            body.find("var normal_factor5 = bitcast<f32>(2139095039u);\nif (normal_squared5 != 0.0)") != std::string::npos &&
            body.find("normal_factor5 = inverseSqrt(normal_squared5)") != std::string::npos &&
            body.find("normal_source5.w * normal_factor5") != std::string::npos,
            "NRM lost XYZ norm, real zero guard or selected-W scaling");
    }
    for (std::uint32_t mask = 1U; mask < 16U; ++mask) {
        const auto body = test.accept(nrm_program(three, {0x1p62F, 0x1p-62F, -0.0F, -0x1p62F}, mask));
        require((body.find("normal_source5.w * normal_factor5") != std::string::npos) == ((mask & 8U) != 0U),
            "NRM evaluated unused W multiplication");
    }
    test.accept(nrm_program(three, {0x1p-62F, 0.0F, 0.0F, 0x1p62F}));
    test.accept(nrm_program(three, {0.0F, -0.0F, 0.0F, -1.0F}));
    test.accept(nrm_program(three, {-0.0F, 0.0F, -0.0F, 1.0F}));
    test.accept(nrm_program(three, {0.0F, 0.0F, 0.0F, -0.0F}));
    test.accept(nrm_program(three, {1.0F, 0.0F, 0.0F, std::numeric_limits<float>::max()}, 7U));
    test.accept(nrm_program(three, {0.0F, 0.0F, 0.0F, std::numeric_limits<float>::max()}, 7U));
    const float above = std::nextafter(0x1p62F, std::numeric_limits<float>::infinity());
    const float below = std::nextafter(0x1p-62F, 0.0F);
    test.reject(nrm_program(three, {above, 0.0F, 0.0F, 1.0F}), "NRM overflow-proof XYZ bound widened");
    test.reject(nrm_program(three, {-above, 0.0F, 0.0F, 1.0F}), "NRM negative XYZ bound widened");
    test.reject(nrm_program(three, {below, 0.0F, 0.0F, 1.0F}), "NRM normal-length lower bound widened");
    test.reject(nrm_program(three, {1.0F, 0.0F, 0.0F, above}), "NRM selected-W product bound widened");
    test.reject(nrm_program(three, {0.0F, 0.0F, 0.0F, std::nextafter(1.0F, 2.0F)}), "NRM zero-norm W overflow admitted");
    test.reject(nrm_program(three, {0.0F, 0.0F, 0.0F, -std::nextafter(1.0F, 2.0F)}), "NRM zero-norm negative W overflow admitted");
    test.reject(nrm_program(three, {1.0F, 0.0F, 0.0F, above}, 1U, 0xE7U), "NRM proof ignored original swizzle");
    for (const auto source : {src(2U, 30U), src(0U, 0U), src(0U, 1U), src(three ? 1U : 3U, 0U, 0U)}) {
        auto program = prefix(three);
        instruction(program, 36U, dst(0U, 0U), {source});
        finish(program);
        test.reject(program, "NRM mutable/unwritten/aliased/input source admitted without immutable proof");
    }
    auto output = nrm_program(three, {1.0F, 0.0F, 0.0F, 1.0F});
    output[18U] = dst(8U, 0U);
    test.reject(output, "NRM accepted non-temporary destination");
    output[18U] = dst(0U, 0U, 0U);
    test.reject(output, "NRM accepted empty write mask");
}

void coefficients(words& program) {
    definition(program, 30U, {-1.5500992e-006F, -2.1701389e-005F, 0.0026041667F, 0.00026041668F});
    definition(program, 31U, {-0.020833334F, -0.12500000F, 1.0F, 0.50000000F});
}

void sine_cosine(controls& test, bool three) {
    for (const auto mask : {1U, 2U, 3U}) {
        for (const auto modifier : {0U, 1U, 11U, 12U}) {
            auto program = prefix(three);
            if (!three) coefficients(program);
            if (three) instruction(program, 37U, dst(0U, 0U, mask), {src(0U, 0U, 0x55U, modifier)});
            else instruction(program, 37U, dst(0U, 0U, mask), {src(0U, 0U, 0x55U, modifier), src(2U, 30U), src(2U, 31U)});
            // Replicate only the defined result. The ps2 macro invalidates Z.
            finish(program, src(0U, 0U, mask == 2U ? 0x55U : 0U));
            const auto body = test.accept(program);
            require((body.find("cos(angle") != std::string::npos) == ((mask & 1U) != 0U) &&
                (body.find("sin(angle") != std::string::npos) == ((mask & 2U) != 0U), "SINCOS computed unselected components");
            auto undefined = program;
            undefined[undefined.size() - 2U] = src(0U, 0U, 0xAAU);
            if (three) test.accept(undefined);
            else test.reject(undefined, "ps2 SINCOS preserved stale Z");
            undefined[undefined.size() - 2U] = src(0U, 0U, 0xFFU);
            test.accept(undefined); // Both versions preserve the original W.
            if (mask != 3U) {
                undefined[undefined.size() - 2U] = src(0U, 0U, mask == 1U ? 0x55U : 0U);
                if (three) test.accept(undefined);
                else test.reject(undefined, "ps2 SINCOS preserved unselected XY");
            }
            auto wrong_length = program;
            wrong_length[three ? 11U : 23U] = three ? 0x04000025U : 0x02000025U;
            test.reject(wrong_length, "SINCOS accepted other shader model's operand count");
        }
    }
    for (const auto mask : {0U, 4U, 5U, 7U, 8U, 15U}) {
        auto program = prefix(three);
        if (!three) coefficients(program);
        if (three) instruction(program, 37U, dst(0U, 0U, mask), {src(0U, 0U, 0U)});
        else instruction(program, 37U, dst(0U, 0U, mask), {src(0U, 0U, 0U), src(2U, 30U), src(2U, 31U)});
        finish(program);
        test.reject(program, "SINCOS invalid destination mask admitted");
    }
    auto program = prefix(three);
    if (!three) coefficients(program);
    if (three) instruction(program, 37U, dst(0U, 0U, 3U), {src(0U, 0U)});
    else instruction(program, 37U, dst(0U, 0U, 3U), {src(0U, 0U), src(2U, 30U), src(2U, 31U)});
    finish(program, src(0U, 0U, 0U));
    test.reject(program, "SINCOS nonreplicate angle admitted");
    program[three ? 13U : 25U] = src(0U, 0U, 0U);
    program[three ? 12U : 24U] = dst(8U, 0U, 3U);
    test.reject(program, "SINCOS accepted non-temporary destination");
    if (!three) {
        program[24U] = dst(0U, 0U, 3U);
        test.accept(program);
        auto invalid = program;
        invalid[9U] ^= 1U;
        test.reject(invalid, "ps2 SINCOS coefficient bit change ignored");
        for (const auto coefficient : {src(2U, 31U), src(2U, 29U), src(2U, 30U, 0U),
                                       src(2U, 30U, 0xE4U, 1U), src(0U, 0U)}) {
            invalid = program;
            invalid[26U] = coefficient;
            test.reject(invalid, "ps2 SINCOS did not require exact distinct DEF-owned coefficients");
        }
        invalid = program;
        invalid.erase(invalid.begin() + 7, invalid.begin() + 19);
        test.reject(invalid, "ps2 SINCOS mutable coefficient uniforms admitted");
        // Z becomes readable only after a real later write, not a stale value.
        invalid = program;
        invalid.insert(invalid.end() - 4, {0x02000001U, dst(0U, 0U, 4U), src(2U, 0U)});
        invalid[invalid.size() - 2U] = src(0U, 0U, 0xAAU);
        test.accept(invalid);
    }
}

void structural_controls(controls& test, bool three) {
    auto make = [&](std::uint32_t a, std::uint32_t b, std::uint32_t c) {
        auto program = prefix(three);
        instruction(program, 90U, dst(0U, 0U), {a, b, c});
        finish(program);
        return program;
    };
    test.reject(make(src(2U, 0U), src(2U, 1U), src(0U, 0U, 0U)), "multiple float-constant read ports admitted");
    test.accept(make(src(2U, 0U), src(0U, 0U), src(2U, 0U, 0U)));
    const auto reads = make(src(2U, 0U), src(2U, 0U), src(2U, 0U, 0U));
    if (three) test.accept(reads);
    else test.reject(reads, "ps2 third float-constant read admitted");
    const auto input_type = three ? 1U : 3U;
    const auto input_reads = make(src(input_type, 0U), src(input_type, 0U), src(0U, 0U, 0U));
    if (three) test.accept(input_reads);
    else test.reject(input_reads, "ps2 second texture-coordinate read admitted");
    const auto valid = make(src(0U, 0U), src(2U, 31U), src(0U, 0U, 0U));
    for (const auto header : {0x0300005AU, 0x0500005AU, 0x1400005AU, 0x4400005AU, 0x0401005AU}) {
        auto program = valid;
        program[11U] = header;
        test.reject(program, "arithmetic instruction length or reserved bits ignored");
    }
    for (const auto source : {src(0U, 0U) | 0x2000U, src(2U, 32U), src(0U, 12U), src(7U, 0U),
                              src(14U, 0U), src(0U, 0U, 0xE4U, 2U)}) {
        auto program = valid;
        program[13U] = source;
        test.reject(program, "arithmetic addressing/register/modifier limit widened");
    }
    for (const auto opcode : {6U, 7U, 14U, 32U}) {
        auto program = prefix(three);
        if (opcode == 32U) instruction(program, opcode, dst(0U, 1U), {src(0U, 0U, 0U), src(2U, 0U, 0x55U)});
        else instruction(program, opcode, dst(0U, 1U), {src(0U, 0U, 0U)});
        finish(program);
        test.reject(program, "unresolved numeric family silently admitted");
    }
    auto late = valid;
    late.insert(late.end() - 1, {0x0200000FU, dst(0U, 0U), src(0U, 0U)});
    test.reject(late, "late scalar validation failure published previous prefix");
}
} // namespace shader_arithmetic_controls

bool run_shader_effect_arithmetic_tests() {
    try {
        shader_arithmetic_controls::controls test;
        for (const bool three : {false, true}) {
            shader_arithmetic_controls::log_and_dot(test, three);
            shader_arithmetic_controls::bounded_normal(test, three);
            shader_arithmetic_controls::sine_cosine(test, three);
            shader_arithmetic_controls::structural_controls(test, three);
        }
        std::cout << "shader effect original-bytecode arithmetic controls: " << test.count << " passed\n";
        return true;
    } catch (const std::exception& error) {
        std::cerr << "shader effect arithmetic control failed: " << error.what() << '\n';
        return false;
    }
}

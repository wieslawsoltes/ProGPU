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

void cross_products(controls& test, bool three) {
    for (std::uint32_t mask = 1U; mask < 8U; ++mask) {
        const auto required = ((mask & 1U) != 0U ? 6U : 0U) |
            ((mask & 2U) != 0U ? 5U : 0U) | ((mask & 4U) != 0U ? 3U : 0U);
        for (const auto modifier : {0U, 1U, 11U, 12U}) {
            auto program = prefix(three);
            instruction(program, 1U, dst(0U, 1U, required), {src(0U, 0U)});
            instruction(program, 1U, dst(0U, 2U, required), {src(0U, 0U)});
            instruction(program, 33U, dst(0U, 0U, mask, 1U),
                {src(0U, 1U, 0xE4U, modifier), src(0U, 2U, 0xE4U, modifier)});
            finish(program);
            const auto body = test.accept(program);
            require(body.find("let value6 = clamp(vec4<f32>(") != std::string::npos,
                "CRS lost original destination saturation");
            const auto begin = body.find("let value6 = ");
            const auto end = body.find("let value7 = ", begin);
            const auto operation = body.substr(begin, end - begin);
            require(operation.find("r[0].w =") == std::string::npos,
                "CRS overwrote the previous destination W");
            for (std::uint32_t lane = 0U; lane < 3U; ++lane)
                require((operation.find(std::string("r[0].") + "xyz"[lane] + " = value6.") != std::string::npos)
                    == ((mask & (1U << lane)) != 0U), "CRS destination mask changed");
            for (std::uint32_t lane = 0U; lane < 3U; ++lane) {
                if ((required & (1U << lane)) == 0U) continue;
                auto missing = program;
                const auto reduced = required & ~(1U << lane);
                // Both selected source components must be initialized, even
                // for one destination lane. A zero mask is independently invalid.
                missing[12U] = dst(0U, 1U, reduced);
                test.reject(missing, "CRS read a missing component from its first operand");
                missing = program;
                missing[15U] = dst(0U, 2U, reduced);
                test.reject(missing, "CRS read a missing component from its second operand");
            }
        }
    }
    const auto make = [&](std::uint32_t destination, std::uint32_t first, std::uint32_t second) {
        auto program = prefix(three);
        instruction(program, 33U, destination, {first, second});
        finish(program);
        return program;
    };
    const auto xyz = dst(0U, 1U, 7U);
    const auto source = src(0U, 0U);
    const auto constant = src(2U, 0U);
    auto program = make(xyz, source, constant);
    require(test.accept(program).find("vec4<f32>(r[0].y * c[0].z - r[0].z * c[0].y, "
        "r[0].z * c[0].x - r[0].x * c[0].z, "
        "r[0].x * c[0].y - r[0].y * c[0].x, 0.0)") != std::string::npos,
        "CRS right-handed lane/sign order changed");
    test.accept(make(xyz, constant, constant));
    test.reject(make(xyz, constant, src(2U, 1U)), "CRS admitted two constant-register read ports");
    test.reject(make(dst(8U, 0U, 7U), source, constant), "CRS wrote the output register");
    for (std::uint32_t mask = 8U; mask < 16U; ++mask)
        test.reject(make(dst(0U, 1U, mask), source, constant), "CRS defined W");
    test.reject(make(dst(0U, 0U, 7U), source, constant), "CRS destination aliased its first source");
    test.reject(make(dst(0U, 0U, 7U), constant, source), "CRS destination aliased its second source");
    for (const auto swizzle : {0U, 0x1BU, 0x55U, 0xAAU, 0xFFU}) {
        test.reject(make(xyz, src(0U, 0U, swizzle), constant), "CRS first non-default swizzle accepted");
        test.reject(make(xyz, source, src(2U, 0U, swizzle)), "CRS second non-default swizzle accepted");
    }
    for (const auto header : {0x02000021U, 0x04000021U, 0x13000021U, 0x43000021U, 0x03010021U}) {
        auto invalid = program;
        invalid[11U] = header;
        test.reject(invalid, "CRS framing, predication or reserved controls accepted");
    }
    for (const auto opcode : {12U, 13U}) {
        auto invalid = program;
        invalid[11U] = 0x03000000U | opcode;
        test.reject(invalid, "vertex-only comparison was admitted as a pixel instruction");
    }
}
// Exercise the precise scalar load form independently of the existing CRS
// admission controls. Only the required source lanes exist in these programs.
void cross_scalar_sources(controls& test, bool three) {
    for (std::uint32_t mask = 1U; mask < 8U; ++mask)
        for (const auto first_modifier : {0U, 1U, 11U, 12U})
            for (const auto second_modifier : {0U, 1U, 11U, 12U}) {
                const auto required = ((mask & 1U) != 0U ? 6U : 0U) |
                    ((mask & 2U) != 0U ? 5U : 0U) | ((mask & 4U) != 0U ? 3U : 0U);
                auto program = prefix(three);
                instruction(program, 1U, dst(0U, 1U, required), {src(0U, 0U)});
                instruction(program, 1U, dst(0U, 2U, required), {src(0U, 0U)});
                instruction(program, 33U, dst(0U, 0U, mask),
                    {src(0U, 1U, 0xE4U, first_modifier), src(0U, 2U, 0xE4U, second_modifier)});
                finish(program);
                const auto body = test.accept(program);
                const auto begin = body.find("let value6 = ");
                const auto operation = body.substr(begin, body.find("let value7 = ", begin) - begin);
                require(operation.find(".xyzw") == std::string::npos &&
                    operation.find("r[1].w") == std::string::npos && operation.find("r[2].w") == std::string::npos,
                    "CRS introduced a nested swizzle view or source W read");
                for (std::uint32_t source = 1U; source <= 2U; ++source)
                    for (std::uint32_t lane = 0U; lane < 3U; ++lane) {
                        const auto scalar = "r[" + std::to_string(source) + "]." + "xyz"[lane];
                        const bool needed = (required & (1U << lane)) != 0U;
                        require((operation.find(scalar) != std::string::npos) == needed,
                            "CRS scalar source dependency changed");
                        if (!needed) continue;
                        const auto modifier = source == 1U ? first_modifier : second_modifier;
                        const auto expected = modifier == 0U ? scalar : modifier == 1U ? "(-" + scalar + ")" :
                            modifier == 11U ? "abs(" + scalar + ")" : "(-abs(" + scalar + "))";
                        require(operation.find(expected) != std::string::npos,
                            "CRS scalar modifier or ABSNEG order changed");
                    }
            }
    for (const auto modifier : {0U, 1U, 11U, 12U}) {
        auto program = prefix(three);
        definition(program, 31U, {-0.0F, 0.0F, -0.0F, 0.0F});
        instruction(program, 1U, dst(0U, 1U), {src(0U, 0U)});
        instruction(program, 33U, dst(0U, 1U, 1U),
            {src(0U, 0U), src(2U, 31U, 0xE4U, modifier)});
        finish(program, src(0U, 1U));
        const auto body = test.accept(program);
        require(body.find("c[31] = vec4<f32>(bitcast<f32>(2147483648u), bitcast<f32>(0u), "
            "bitcast<f32>(2147483648u), bitcast<f32>(0u));") != std::string::npos,
            "CRS scalar emission canonicalized original signed-zero DEF bits");
        const std::array expected{"c[31].z", "(-c[31].z)", "abs(c[31].z)", "(-abs(c[31].z))"};
        const auto index = modifier == 0U ? 0U : modifier == 1U ? 1U : modifier == 11U ? 2U : 3U;
        require(body.find(std::string("r[0].y * ") + expected[index]) != std::string::npos,
            "CRS changed the modifier operation on a signed-zero source");
    }
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
        shader_arithmetic_controls::controls cross;
        for (const bool three : {false, true}) shader_arithmetic_controls::cross_products(cross, three);
        std::cout << "shader effect original-bytecode cross-product controls: " << cross.count << " passed\n";
        shader_arithmetic_controls::controls scalar_sources;
        for (const bool three : {false, true}) shader_arithmetic_controls::cross_scalar_sources(scalar_sources, three);
        std::cout << "shader effect cross-product scalar-source controls: " << scalar_sources.count << " passed\n";
        return true;
    } catch (const std::exception& error) {
        std::cerr << "shader effect arithmetic control failed: " << error.what() << '\n';
        return false;
    }
}

#include "progpu_native_shader_effect.hpp"

#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <string>

namespace progpu::native::shader_effect {
namespace {
constexpr std::uint32_t maximum_bytes = 65536U;
constexpr std::uint32_t maximum_instructions = 512U;
constexpr char lanes[] = "xyzw";
// D3D9 SDK public SINCOS coefficient contract, not an approximation selected by
// this translator. The ps_2_0 macro requires these exact, distinct DEF vectors.
constexpr std::array<std::array<float, 4U>, 2U> sincos_coefficients{{
    {-1.5500992e-006F, -2.1701389e-005F, 0.0026041667F, 0.00026041668F},
    {-0.020833334F, -0.12500000F, 1.0F, 0.50000000F}}};

std::uint32_t register_type(std::uint32_t token) noexcept {
    return ((token >> 28U) & 7U) | ((token >> 8U) & 24U);
}

bool parameter(std::uint32_t token) noexcept {
    return (token & 0x80000000U) != 0U && (token & 0x0000E000U) == 0U;
}
} // namespace

bool translate(std::span<const std::byte> bytecode,
               std::uint32_t source_sampler, std::string* wgsl_body) noexcept {
    if (bytecode.size() < 8U || bytecode.size() > maximum_bytes ||
        bytecode.size() % 4U != 0U || source_sampler >= 16U) return false;
    const auto word = [&](std::size_t index) noexcept {
        std::uint32_t value = 0U;
        std::memcpy(&value, bytecode.data() + index * 4U, 4U);
        return value;
    };
    const bool shader_model_three = word(0U) == 0xFFFF0300U;
    if (!shader_model_three && word(0U) != 0xFFFF0200U) return false;
    try {
        std::string body;
        std::array<std::uint32_t, 12U> written{};
        std::array<bool, 32U> definitions{};
        std::array<std::array<std::uint32_t, 4U>, 32U> definition_bits{};
        std::uint32_t output_written = 0U, instructions = 0U;
        std::uint32_t input_mask = 0U;
        std::uint32_t input_register = 0U;
        bool sampler_declared = false, executable = false, sampled = false;
        const auto append = [&](const std::string& text) {
            if (wgsl_body != nullptr) body += text;
        };
        for (std::size_t cursor = 1U; cursor < bytecode.size() / 4U;) {
            const auto instruction = word(cursor++);
            const auto opcode = instruction & 0xFFFFU;
            if (opcode == 0xFFFFU) {
                if (instruction != 0xFFFFU || cursor != bytecode.size() / 4U ||
                    output_written != 15U || !sampled) return false;
                if (wgsl_body != nullptr) *wgsl_body = std::move(body);
                return true;
            }
            if (opcode == 0xFFFEU) {
                if ((instruction & 0x80000000U) != 0U) return false;
                const auto count = (instruction >> 16U) & 0x7FFFU;
                if (count > bytecode.size() / 4U - cursor) return false;
                cursor += count;
                continue;
            }
            if (++instructions > maximum_instructions ||
                (instruction & 0xF0FF0000U) != 0U) return false;
            const auto count = (instruction >> 24U) & 15U;
            if (count > bytecode.size() / 4U - cursor) return false;
            const auto first = cursor;
            cursor += count;
            if (opcode == 0U) { // nop
                if (count != 0U) return false;
                continue;
            }
            if (opcode == 31U) { // dcl: version-specific UV input or one 2D sampler
                if (count != 2U || executable) return false;
                const auto declaration = word(first);
                const auto destination = word(first + 1U);
                if (!parameter(destination) || (destination & 0x0FF00000U) != 0U)
                    return false;
                const auto type = register_type(destination);
                const auto index = destination & 0x7FFU;
                const auto mask = (destination >> 16U) & 15U;
                if (!shader_model_three && type == 3U && index == 0U && declaration == 0x80000000U &&
                    mask != 0U && input_mask == 0U) input_mask = mask;
                else if (shader_model_three && type == 1U && index < 10U &&
                         declaration == 0x80000005U && mask == 3U && input_mask == 0U) {
                    // ps_3_0 consolidates t# into declared v# inputs. The
                    // retained source supplies only TEXCOORD0.xy; packed,
                    // centroid, other semantics and extra components require
                    // their own source contract. t0 here is the canonical
                    // wrapper's UV value, never a ps_3_0 texture register.
                    input_register = index;
                    input_mask = mask;
                    append("let input_v" + std::to_string(index) + " = t0;\n");
                }
                else if (type == 10U && index == source_sampler &&
                         declaration == 0x90000000U && mask == 15U && !sampler_declared)
                    sampler_declared = true;
                else return false;
                continue;
            }
            if (opcode == 81U) { // def: exact finite immediate bits override cN
                if (count != 5U || executable) return false;
                const auto destination = word(first);
                const auto index = destination & 0x7FFU;
                if (!parameter(destination) || register_type(destination) != 2U ||
                    (destination & 0x0FFF0000U) != 0x000F0000U || index >= 32U || definitions[index])
                    return false;
                definitions[index] = true;
                std::string expression = "c[" + std::to_string(index) + "] = vec4<f32>(";
                for (std::uint32_t lane = 0U; lane < 4U; ++lane) {
                    const auto bits = word(first + lane + 1U);
                    if (!std::isfinite(std::bit_cast<float>(bits))) return false;
                    definition_bits[index][lane] = bits;
                    if (lane != 0U) expression += ", ";
                    expression += "bitcast<f32>(" + std::to_string(bits) + "u)";
                }
                append(expression + ");\n");
                continue;
            }
            executable = true;
            std::uint32_t sources = 0U;
            switch (opcode) {
            case 1U: case 15U: case 19U: case 35U: case 36U:
                sources = 1U; break; // mov/log/frc/abs/bounded nrm
            case 37U: sources = shader_model_three ? 1U : 3U; break; // sincos
            case 2U: case 3U: case 5U: case 8U: case 9U: case 10U: case 11U:
            case 66U: sources = 2U; break; // add/sub/mul/dp3/dp4/min/max/texld
            case 4U: case 18U: case 88U: case 90U: sources = 3U; break; // mad/lrp/cmp/dp2add
            // RCP/RSQ require infinity at zero. EXP/POW also need an overflow
            // and numeric-domain contract beyond WGSL's finite-math allowance.
            case 6U: case 7U: case 14U: case 32U: return false;
            default: return false;
            }
            if (count != sources + 1U) return false;
            const auto destination = word(first);
            const auto destination_type = register_type(destination);
            const auto destination_index = destination & 0x7FFU;
            const auto mask = (destination >> 16U) & 15U;
            const auto modifier = (destination >> 20U) & 15U;
            if (!parameter(destination) || (destination & 0x0F000000U) != 0U ||
                mask == 0U || modifier > 1U ||
                !((destination_type == 0U && destination_index < 12U) ||
                  (destination_type == 8U && destination_index == 0U))) return false;
            if (shader_model_three && opcode == 66U && destination_type != 0U)
                return false; // The admitted TEXLD form writes a temporary.
            if ((opcode == 36U || opcode == 37U) && destination_type != 0U)
                return false;
            if (opcode == 37U && mask > 3U) return false;
            std::array<std::string, 3U> operands;
            std::uint32_t constant_register = 32U;
            std::uint32_t constant_reads = 0U, texture_reads = 0U;
            for (std::uint32_t index = 0U; index < sources; ++index) {
                const auto token = word(first + index + 1U);
                const auto type = register_type(token);
                const auto number = token & 0x7FFU;
                const auto source_modifier = (token >> 24U) & 15U;
                if (!parameter(token)) return false;
                if (opcode == 37U && !shader_model_three && index != 0U) {
                    // Live uniform values are not part of the immutable
                    // program cache key. Do not ignore unverified coefficients.
                    if (type != 2U || number >= 32U || !definitions[number] ||
                        source_modifier != 0U || ((token >> 16U) & 255U) != 0xE4U)
                        return false;
                    for (std::uint32_t lane = 0U; lane < 4U; ++lane)
                        if (definition_bits[number][lane] !=
                            std::bit_cast<std::uint32_t>(sincos_coefficients[index - 1U][lane]))
                            return false;
                    // The macro's two required coefficient registers are the
                    // documented exception to ordinary constant read ports.
                    continue;
                }
                if (opcode == 66U && index == 1U) {
                    if (type != 10U || number != source_sampler || !sampler_declared ||
                        source_modifier != 0U || ((token >> 16U) & 255U) != 0xE4U)
                        return false;
                    continue;
                }
                if (source_modifier != 0U && source_modifier != 1U &&
                    source_modifier != 11U && source_modifier != 12U) return false;
                const bool scalar = opcode == 15U || opcode == 37U ||
                    (opcode == 90U && index == 2U);
                if (scalar && ((token >> 16U) & 255U) !=
                    ((token >> 16U) & 3U) * 0x55U) return false;
                if (opcode == 36U) {
                    // Compiler-proven finite subset, not a CPU shader evaluator.
                    // Runtime-valued NRM remains unsupported. Magnitudes are
                    // unchanged by all admitted source modifiers, so inspect
                    // the actual swizzled DEF lanes after their sign operation.
                    if (type != 2U || number >= 32U || !definitions[number]) return false;
                    bool nonzero = false, normal_length = false;
                    for (std::uint32_t lane = 0U; lane < 3U; ++lane) {
                        const auto selected = (token >> (16U + 2U * lane)) & 3U;
                        const float magnitude = std::abs(std::bit_cast<float>(definition_bits[number][selected]));
                        if (magnitude > 0x1p62F) return false;
                        nonzero |= magnitude != 0.0F;
                        normal_length |= magnitude >= 0x1p-62F;
                    }
                    if (nonzero && !normal_length) return false;
                    if ((mask & 8U) != 0U) {
                        const auto selected = (token >> 22U) & 3U;
                        const float magnitude = std::abs(std::bit_cast<float>(definition_bits[number][selected]));
                        if (magnitude > (nonzero ? 0x1p62F : 1.0F)) return false;
                    }
                }
                if (shader_model_three && opcode == 66U &&
                    (source_modifier != 0U || (type != 0U && type != 1U)))
                    return false; // No modified/constant-coordinate texture form.
                std::uint32_t available = 0U;
                std::string expression;
                if (type == 0U && number < 12U) {
                    available = written[number]; expression = "r[" + std::to_string(number) + "]";
                } else if (type == 2U && number < 32U) {
                    // Both versions have one float-constant read port; ps_2_0
                    // additionally allows at most two reads of that register.
                    if (constant_register != 32U && constant_register != number)
                        return false;
                    if (!shader_model_three && ++constant_reads > 2U) return false;
                    constant_register = number;
                    available = 15U; expression = "c[" + std::to_string(number) + "]";
                } else if (!shader_model_three && type == 3U && number == 0U) {
                    if (++texture_reads > 1U) return false;
                    available = input_mask; expression = "t0";
                } else if (shader_model_three && type == 1U && input_mask != 0U &&
                           number == input_register) {
                    available = input_mask; expression = "input_v" + std::to_string(number);
                } else return false;
                const auto required = scalar ? 1U : opcode == 36U ? (7U | (mask & 8U)) :
                    opcode == 8U ? 7U : opcode == 9U ? 15U :
                    opcode == 66U || opcode == 90U ? 3U : mask;
                expression += ".";
                for (std::uint32_t lane = 0U; lane < 4U; ++lane) {
                    const auto selected = (token >> (16U + 2U * lane)) & 3U;
                    if ((required & (1U << lane)) != 0U && (available & (1U << selected)) == 0U)
                        return false;
                    expression += lanes[selected];
                }
                if (source_modifier == 11U || source_modifier == 12U) expression = "abs(" + expression + ")";
                if (source_modifier == 1U || source_modifier == 12U) expression = "(-" + expression + ")";
                operands[index] = std::move(expression);
            }
            const auto& a = operands[0]; const auto& b = operands[1]; const auto& c = operands[2];
            const auto suffix = std::to_string(instructions);
            std::string result;
            switch (opcode) {
            case 1U: result = a; break;
            case 2U: result = a + " + " + b; break;
            case 3U: result = a + " - " + b; break;
            case 4U: result = a + " * " + b + " + " + c; break;
            case 5U: result = a + " * " + b; break;
            case 8U: result = "vec4<f32>(dot((" + a + ").xyz, (" + b + ").xyz))"; break;
            case 9U: result = "vec4<f32>(dot(" + a + ", " + b + "))"; break;
            case 10U: result = "min(" + a + ", " + b + ")"; break;
            case 11U: result = "max(" + a + ", " + b + ")"; break;
            case 15U: {
                // Branch before log2, rather than selecting away log2(0).
                // Scale subnormal magnitude bits into a normal integer-valued
                // float before taking a logarithm; no FTZ-dependent log2(0).
                const auto magnitude = "log_magnitude" + suffix;
                const auto scalar_result = "log_result" + suffix;
                append("let " + magnitude + " = bitcast<u32>((" + a + ").x) & 2147483647u;\n");
                append("var " + scalar_result + " = bitcast<f32>(4286578687u);\n");
                append("if (" + magnitude + " != 0u) {\n");
                append("  if (" + magnitude + " < 8388608u) {\n");
                append("    " + scalar_result + " = log2(f32(" + magnitude + ")) - 149.0;\n");
                append("  } else {\n    " + scalar_result + " = log2(bitcast<f32>(" + magnitude + "));\n  }\n}\n");
                result = "vec4<f32>(" + scalar_result + ")";
                break;
            }
            case 18U: result = a + " * " + b + " + (vec4<f32>(1.0) - " + a + ") * " + c; break;
            case 19U: result = "fract(" + a + ")"; break;
            case 35U: result = "abs(" + a + ")"; break;
            case 36U: {
                const auto normal = "normal_source" + suffix;
                const auto squared = "normal_squared" + suffix;
                const auto factor = "normal_factor" + suffix;
                append("let " + normal + " = " + a + ";\n");
                append("let " + squared + " = dot(" + normal + ".xyz, " + normal + ".xyz);\n");
                append("var " + factor + " = bitcast<f32>(2139095039u);\n");
                append("if (" + squared + " != 0.0) {\n  " + factor + " = inverseSqrt(" + squared + ");\n}\n");
                // Do not evaluate W multiplication when W is not written: its
                // value deliberately does not participate in the finite proof.
                result = "vec4<f32>(" + normal + ".xyz * " + factor + ", " +
                    ((mask & 8U) != 0U ? normal + ".w * " + factor : "0.0") + ")";
                break;
            }
            case 37U: {
                const auto angle = "angle" + suffix;
                append("let " + angle + " = (" + a + ").x;\n");
                result = "vec4<f32>(" + ((mask & 1U) != 0U ? "cos(" + angle + ")" : "0.0") +
                    ", " + ((mask & 2U) != 0U ? "sin(" + angle + ")" : "0.0") + ", 0.0, 0.0)";
                break;
            }
            case 66U:
                result = "wpf_sample_input((" + a + ").xy)";
                sampled = true; break;
            case 88U: result = "select(" + c + ", " + b + ", " + a + " >= vec4<f32>(0.0))"; break;
            case 90U: result = "vec4<f32>(dot((" + a + ").xy, (" + b + ").xy) + (" + c + ").x)"; break;
            default: return false;
            }
            if (modifier == 1U) result = "clamp(" + result + ", vec4<f32>(0.0), vec4<f32>(1.0))";
            const auto temporary = "value" + std::to_string(instructions);
            append("let " + temporary + " = " + result + ";\n");
            const auto target = destination_type == 8U ? "o" : "r[" + std::to_string(destination_index) + "]";
            for (std::uint32_t lane = 0U; lane < 4U; ++lane) {
                if ((mask & (1U << lane)) != 0U)
                    append(target + "." + lanes[lane] + " = " + temporary + "." + lanes[lane] + ";\n");
            }
            if (destination_type == 8U) output_written |= mask;
            else {
                // ps_2_0 SINCOS leaves W intact but makes unselected XYZ
                // undefined. Stale previous values may not be read afterwards.
                if (opcode == 37U && !shader_model_three) written[destination_index] &= 8U;
                written[destination_index] |= mask;
            }
        }
    } catch (...) { return false; }
    return false;
}

bool validate(const progpu_native_scene_shader_effect& effect,
              std::span<const std::byte> bytecode) noexcept {
    if (effect.struct_size != sizeof(effect) || effect.version != 1U ||
        effect.bytecode_size != bytecode.size() || effect.revision == 0U ||
        effect.sampling_mode > 1U || effect.flags != 0U || effect.reserved != 0U)
        return false;
    for (float value : effect.constants) if (!std::isfinite(value)) return false;
    return translate(bytecode, effect.source_sampler);
}
} // namespace progpu::native::shader_effect

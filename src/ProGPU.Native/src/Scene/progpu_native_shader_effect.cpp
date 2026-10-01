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
    if (word(0U) != 0xFFFF0200U) return false;
    try {
        std::string body;
        std::array<std::uint32_t, 12U> written{};
        std::array<bool, 32U> definitions{};
        std::uint32_t output_written = 0U, instructions = 0U;
        std::uint32_t input_mask = 0U;
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
            if (opcode == 31U) { // dcl: t0 or the one 2D input sampler
                if (count != 2U || executable) return false;
                const auto declaration = word(first);
                const auto destination = word(first + 1U);
                if (!parameter(destination) || (destination & 0x0FF00000U) != 0U)
                    return false;
                const auto type = register_type(destination);
                const auto index = destination & 0x7FFU;
                const auto mask = (destination >> 16U) & 15U;
                if (type == 3U && index == 0U && declaration == 0x80000000U &&
                    mask != 0U && input_mask == 0U) input_mask = mask;
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
                    if (lane != 0U) expression += ", ";
                    expression += "bitcast<f32>(" + std::to_string(bits) + "u)";
                }
                append(expression + ");\n");
                continue;
            }
            executable = true;
            std::uint32_t sources = 0U;
            switch (opcode) {
            case 1U: case 19U: case 35U: sources = 1U; break; // mov/frc/abs
            case 2U: case 3U: case 5U: case 8U: case 9U: case 10U: case 11U:
            case 66U: sources = 2U; break; // add/sub/mul/dp3/dp4/min/max/texld
            case 4U: case 18U: case 88U: sources = 3U; break; // mad/lrp/cmp
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
            std::array<std::string, 3U> operands;
            for (std::uint32_t index = 0U; index < sources; ++index) {
                const auto token = word(first + index + 1U);
                const auto type = register_type(token);
                const auto number = token & 0x7FFU;
                const auto source_modifier = (token >> 24U) & 15U;
                if (!parameter(token)) return false;
                if (opcode == 66U && index == 1U) {
                    if (type != 10U || number != source_sampler || !sampler_declared ||
                        source_modifier != 0U || ((token >> 16U) & 255U) != 0xE4U)
                        return false;
                    continue;
                }
                if (source_modifier != 0U && source_modifier != 1U &&
                    source_modifier != 11U && source_modifier != 12U) return false;
                std::uint32_t available = 0U;
                std::string expression;
                if (type == 0U && number < 12U) {
                    available = written[number]; expression = "r[" + std::to_string(number) + "]";
                } else if (type == 2U && number < 32U) {
                    available = 15U; expression = "c[" + std::to_string(number) + "]";
                } else if (type == 3U && number == 0U) {
                    available = input_mask; expression = "t0";
                } else return false;
                const auto required = opcode == 8U ? 7U : opcode == 9U ? 15U :
                    opcode == 66U ? 3U : mask;
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
            case 18U: result = a + " * " + b + " + (vec4<f32>(1.0) - " + a + ") * " + c; break;
            case 19U: result = "fract(" + a + ")"; break;
            case 35U: result = "abs(" + a + ")"; break;
            case 66U:
                result = "wpf_sample_input((" + a + ").xy)";
                sampled = true; break;
            case 88U: result = "select(" + c + ", " + b + ", " + a + " >= vec4<f32>(0.0))"; break;
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
            else written[destination_index] |= mask;
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

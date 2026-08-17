#pragma once

#include "progpu_native.h"

#include <cmath>
#include <cstddef>
#include <cstdint>

namespace progpu::native::path_boolean {

inline constexpr std::uint32_t maximum_instruction_count = 63U;
inline constexpr std::uint32_t maximum_stack_depth = 16U;

template<typename Path, typename Node>
bool validate(
    const Path& path,
    const Node* nodes,
    std::size_t node_count) noexcept {
    const std::uint64_t program_offset = path.boolean_node_offset;
    const std::uint64_t program_count = path.boolean_node_count;
    if (program_count == 0U) {
        return program_offset == 0U;
    }
    if (program_count > maximum_instruction_count || nodes == nullptr ||
        program_offset > node_count ||
        program_count > node_count - program_offset) {
        return false;
    }

    const std::uint64_t segment_offset = path.segment_offset;
    const std::uint64_t segment_count = path.segment_count;
    const std::uint64_t segment_end = segment_offset + segment_count;
    std::uint32_t stack_depth = 0U;
    for (std::uint64_t index = program_offset;
         index < program_offset + program_count;
         ++index) {
        const auto& node = nodes[index];
        if (node.kind > PROGPU_NATIVE_PATH_BOOLEAN_REVERSE_DIFFERENCE ||
            node.reserved0 != 0U || node.reserved1 != 0U) {
            return false;
        }
        if (node.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF) {
            if (stack_depth >= maximum_stack_depth ||
                node.segment_count == 0U ||
                node.segment_offset < segment_offset ||
                node.segment_offset > segment_end ||
                node.segment_count > segment_end - node.segment_offset ||
                !std::isfinite(node.min_x) ||
                !std::isfinite(node.min_y) ||
                !std::isfinite(node.max_x) ||
                !std::isfinite(node.max_y) ||
                node.max_x <= node.min_x || node.max_y <= node.min_y ||
                node.fill_rule > PROGPU_NATIVE_FILL_RULE_EVEN_ODD) {
                return false;
            }
            ++stack_depth;
        } else if (node.kind == PROGPU_NATIVE_PATH_BOOLEAN_EMPTY) {
            if (stack_depth >= maximum_stack_depth ||
                node.segment_offset != 0U || node.segment_count != 0U ||
                node.min_x != 0.0F || node.min_y != 0.0F ||
                node.max_x != 0.0F || node.max_y != 0.0F ||
                node.fill_rule != 0U) {
                return false;
            }
            ++stack_depth;
        } else {
            if (stack_depth < 2U || node.segment_offset != 0U ||
                node.segment_count != 0U || node.min_x != 0.0F ||
                node.min_y != 0.0F || node.max_x != 0.0F ||
                node.max_y != 0.0F || node.fill_rule != 0U) {
                return false;
            }
            --stack_depth;
        }
    }
    return stack_depth == 1U;
}

} // namespace progpu::native::path_boolean

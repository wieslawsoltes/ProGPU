#pragma once

#include "progpu_native_gpu_records.hpp"
#include "progpu_native_path_boolean_validation.hpp"

#include <array>
#include <cstdint>
#include <vector>

namespace progpu::native::path_boolean {

inline constexpr std::uint32_t gpu_program_flag = 0x80000000U;
inline constexpr std::uint32_t gpu_empty_token = 0x40000000U;

struct gpu_program_reference final {
    std::uint32_t path_record_index;
    std::uint32_t program_index;
    std::uint32_t operation_kind;
    bool split_binary_xor;
};

template<typename Path, typename Node>
gpu_program_reference append_gpu_records(
    const Path& path,
    const Node* nodes,
    std::vector<gpu_path_record>& records) {
    const auto path_record_index =
        static_cast<std::uint32_t>(records.size());
    if (path.boolean_node_count == 0U) {
        records.push_back({
            static_cast<std::uint32_t>(path.segment_offset),
            static_cast<std::uint32_t>(path.segment_count),
            path.min_x,
            path.min_y,
            path.max_x,
            path.max_y,
            path.fill_rule,
            0U});
        return {path_record_index, 0U, 0U, false};
    }

    if (path.boolean_node_count == 3U) {
        const auto& first = nodes[path.boolean_node_offset];
        const auto& second = nodes[path.boolean_node_offset + 1U];
        const auto& operation = nodes[path.boolean_node_offset + 2U];
        if (first.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF &&
            second.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF &&
            operation.kind == PROGPU_NATIVE_PATH_BOOLEAN_XOR) {
            const auto append_leaf = [&records](const Node& leaf) {
                records.push_back({
                    static_cast<std::uint32_t>(leaf.segment_offset),
                    static_cast<std::uint32_t>(leaf.segment_count),
                    leaf.min_x,
                    leaf.min_y,
                    leaf.max_x,
                    leaf.max_y,
                    leaf.fill_rule,
                    0U});
            };
            append_leaf(first);
            append_leaf(second);
            return {
                path_record_index,
                path_record_index + 1U,
                0U,
                true};
        }
    }

    std::array<std::uint32_t, maximum_instruction_count> tokens{};
    std::uint32_t leaf_count = 0U;
    for (std::size_t index = 0U;
         index < path.boolean_node_count;
         ++index) {
        const auto& node = nodes[path.boolean_node_offset + index];
        if (node.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF) {
            tokens[index] = leaf_count++;
            records.push_back({
                static_cast<std::uint32_t>(node.segment_offset),
                static_cast<std::uint32_t>(node.segment_count),
                node.min_x,
                node.min_y,
                node.max_x,
                node.max_y,
                node.fill_rule,
                0U});
        } else if (node.kind == PROGPU_NATIVE_PATH_BOOLEAN_EMPTY) {
            tokens[index] = gpu_empty_token;
        } else {
            tokens[index] = gpu_program_flag | (node.kind - 1U);
        }
    }

    const auto program_index = static_cast<std::uint32_t>(records.size());
    for (std::size_t index = 0U;
         index < path.boolean_node_count;
         ++index) {
        records.push_back({
            tokens[index], 0U, 0.0F, 0.0F, 0.0F, 0.0F, 0U, 0U});
    }
    return {
        path_record_index,
        program_index,
        gpu_program_flag |
            static_cast<std::uint32_t>(path.boolean_node_count),
        false};
}

} // namespace progpu::native::path_boolean

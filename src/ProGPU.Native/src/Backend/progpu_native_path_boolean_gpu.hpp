#pragma once

#include "progpu_native_gpu_records.hpp"
#include "progpu_native_path_boolean_validation.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <span>
#include <vector>

namespace progpu::native::path_boolean {

inline constexpr std::uint32_t gpu_program_flag = 0x80000000U;
inline constexpr std::uint32_t gpu_signed_winding_program_flag = 0x40000000U;
inline constexpr std::uint32_t gpu_empty_token = 0x40000000U;
inline constexpr std::uint32_t gpu_winding_leaf_token_flag = 0x20000000U;
inline constexpr std::size_t maximum_equivalence_segment_comparisons =
    1U << 20U;

struct gpu_program_reference final {
    std::uint32_t path_record_index;
    std::uint32_t program_index;
    std::uint32_t operation_kind;
    std::uint32_t split_leaf_count;
};

template<typename Node>
bool has_overlapping_translated_equivalent_leaves(
    std::span<const progpu_native_path_segment> segments,
    const Node* nodes,
    std::size_t node_offset,
    std::size_t node_count) noexcept {
    if (segments.empty() || nodes == nullptr || node_count < 2U) {
        return false;
    }
    const auto nearly_equal = [](float first, float second) noexcept {
        const float scale = std::max(
            1.0F,
            std::max(std::abs(first), std::abs(second)));
        return std::abs(first - second) <= 0.00001F * scale;
    };
    const auto translated_point_equal = [&nearly_equal](
        progpu_native_point first,
        progpu_native_point second,
        float translation_x,
        float translation_y) noexcept {
        return nearly_equal(first.x + translation_x, second.x) &&
            nearly_equal(first.y + translation_y, second.y);
    };
    const auto invariant_point_equal = [&nearly_equal](
        progpu_native_point first,
        progpu_native_point second) noexcept {
        return nearly_equal(first.x, second.x) &&
            nearly_equal(first.y, second.y);
    };
    const auto translated_segment_equal = [
        &translated_point_equal,
        &invariant_point_equal](
        const progpu_native_path_segment& first,
        const progpu_native_path_segment& second,
        float translation_x,
        float translation_y) noexcept {
        if (first.kind != second.kind || first.pad0 != second.pad0 ||
            first.pad1 != second.pad1 || first.pad2 != second.pad2 ||
            !translated_point_equal(
                first.p0,
                second.p0,
                translation_x,
                translation_y) ||
            !translated_point_equal(
                first.p1,
                second.p1,
                translation_x,
                translation_y)) {
            return false;
        }
        switch (first.kind) {
        case PROGPU_NATIVE_PATH_SEGMENT_LINE:
            return invariant_point_equal(first.p2, second.p2) &&
                invariant_point_equal(first.p3, second.p3);
        case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
            return translated_point_equal(
                       first.p2,
                       second.p2,
                       translation_x,
                       translation_y) &&
                invariant_point_equal(first.p3, second.p3);
        case PROGPU_NATIVE_PATH_SEGMENT_CUBIC:
            return translated_point_equal(
                       first.p2,
                       second.p2,
                       translation_x,
                       translation_y) &&
                translated_point_equal(
                    first.p3,
                    second.p3,
                    translation_x,
                    translation_y);
        case PROGPU_NATIVE_PATH_SEGMENT_ARC:
            return translated_point_equal(
                       first.p2,
                       second.p2,
                       translation_x,
                       translation_y) &&
                invariant_point_equal(first.p3, second.p3);
        default:
            return false;
        }
    };

    // This retained-compilation classifier is intentionally scalar: segment
    // kinds select different invariant/transformed fields, and the first
    // mismatch terminates a candidate. A fixed comparison budget prevents an
    // adversarial near-match matrix from becoming a compute-heavy CPU path;
    // exhausting it conservatively selects the exact split-GPU evaluator.
    std::size_t comparison_count = 0U;
    const auto node_end = node_offset + node_count;
    for (std::size_t first_index = node_offset;
         first_index < node_end;
         ++first_index) {
        const auto& first = nodes[first_index];
        if (first.kind != PROGPU_NATIVE_PATH_BOOLEAN_LEAF ||
            first.segment_count == 0U ||
            first.segment_offset > segments.size() ||
            first.segment_count > segments.size() - first.segment_offset) {
            continue;
        }
        for (std::size_t second_index = first_index + 1U;
             second_index < node_end;
             ++second_index) {
            const auto& second = nodes[second_index];
            if (second.kind != PROGPU_NATIVE_PATH_BOOLEAN_LEAF ||
                first.segment_count != second.segment_count ||
                second.segment_offset > segments.size() ||
                second.segment_count >
                    segments.size() - second.segment_offset ||
                std::max(first.min_x, second.min_x) >=
                    std::min(first.max_x, second.max_x) ||
                std::max(first.min_y, second.min_y) >=
                    std::min(first.max_y, second.max_y) ||
                !nearly_equal(
                    first.max_x - first.min_x,
                    second.max_x - second.min_x) ||
                !nearly_equal(
                    first.max_y - first.min_y,
                    second.max_y - second.min_y)) {
                continue;
            }
            const auto& first_segment = segments[first.segment_offset];
            const auto& second_segment = segments[second.segment_offset];
            const float translation_x =
                second_segment.p0.x - first_segment.p0.x;
            const float translation_y =
                second_segment.p0.y - first_segment.p0.y;
            if (nearly_equal(translation_x, 0.0F) &&
                nearly_equal(translation_y, 0.0F)) {
                continue;
            }
            bool equivalent = true;
            for (std::size_t segment_index = 0U;
                 segment_index < first.segment_count;
                 ++segment_index) {
                if (comparison_count ==
                    maximum_equivalence_segment_comparisons) {
                    return true;
                }
                ++comparison_count;
                if (!translated_segment_equal(
                        segments[first.segment_offset + segment_index],
                        segments[second.segment_offset + segment_index],
                        translation_x,
                        translation_y)) {
                    equivalent = false;
                    break;
                }
            }
            if (equivalent) {
                return true;
            }
        }
    }
    return false;
}

template<typename Path, typename Node>
std::uint32_t pure_left_fold_xor_leaf_count(
    const Path& path,
    const Node* nodes) noexcept {
    if (path.boolean_node_count < 3U ||
        (path.boolean_node_count & 1U) == 0U) {
        return 0U;
    }
    if (nodes[path.boolean_node_offset].kind !=
            PROGPU_NATIVE_PATH_BOOLEAN_LEAF ||
        nodes[path.boolean_node_offset + 1U].kind !=
            PROGPU_NATIVE_PATH_BOOLEAN_LEAF ||
        nodes[path.boolean_node_offset + 2U].kind !=
            PROGPU_NATIVE_PATH_BOOLEAN_XOR) {
        return 0U;
    }
    std::uint32_t leaf_count = 2U;
    for (std::size_t index = 3U;
         index < path.boolean_node_count;
         index += 2U) {
        if (nodes[path.boolean_node_offset + index].kind !=
                PROGPU_NATIVE_PATH_BOOLEAN_LEAF ||
            nodes[path.boolean_node_offset + index + 1U].kind !=
                PROGPU_NATIVE_PATH_BOOLEAN_XOR) {
            return 0U;
        }
        ++leaf_count;
    }
    return leaf_count;
}

template<typename Path, typename Node>
gpu_program_reference append_gpu_records(
    const Path& path,
    const Node* nodes,
    std::vector<gpu_path_record>& records,
    std::span<const progpu_native_path_segment> segments = {}) {
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
        return {path_record_index, 0U, 0U, 0U};
    }

    std::array<std::uint32_t, maximum_instruction_count> tokens{};
    std::uint32_t leaf_count = 0U;
    bool signed_winding_program = false;
    for (std::size_t index = 0U;
         index < path.boolean_node_count;
         ++index) {
        const auto& node = nodes[path.boolean_node_offset + index];
        if (node.kind == PROGPU_NATIVE_PATH_BOOLEAN_LEAF ||
            node.kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_LEAF) {
            tokens[index] = leaf_count++;
            if (node.kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_LEAF) {
                tokens[index] |= gpu_winding_leaf_token_flag;
                signed_winding_program = true;
            }
            records.push_back({
                static_cast<std::uint32_t>(node.segment_offset),
                static_cast<std::uint32_t>(node.segment_count),
                node.min_x,
                node.min_y,
                node.max_x,
                node.max_y,
                node.fill_rule,
                node.kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_LEAF
                    ? 1U
                    : 0U});
        } else if (node.kind == PROGPU_NATIVE_PATH_BOOLEAN_EMPTY) {
            tokens[index] = gpu_empty_token;
        } else {
            tokens[index] = gpu_program_flag | (node.kind - 1U);
            signed_winding_program = signed_winding_program ||
                node.kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_ADD ||
                node.kind == PROGPU_NATIVE_PATH_BOOLEAN_WINDING_NEGATE;
        }
    }
    const bool split_program = signed_winding_program ||
        (pure_left_fold_xor_leaf_count(path, nodes) != 0U ||
            has_overlapping_translated_equivalent_leaves(
                segments,
                nodes,
                static_cast<std::size_t>(path.boolean_node_offset),
                static_cast<std::size_t>(path.boolean_node_count)));

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
            (signed_winding_program
                ? gpu_signed_winding_program_flag
                : 0U) |
            static_cast<std::uint32_t>(path.boolean_node_count),
        split_program ? leaf_count : 0U};
}

} // namespace progpu::native::path_boolean

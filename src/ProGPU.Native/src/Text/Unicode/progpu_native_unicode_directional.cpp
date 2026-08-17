#include "progpu_native_text.hpp"

#include "progpu_native_unicode_data.generated.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

// Exact allocation-free port of the ProGPU-owned
// ProGPU.Text/UnicodeDirectionalData.Generated.cs at repository checkpoint
// 2d4dba69. The packed pairs are generated from that source and searched in
// O(log P) time with O(1) storage for P directional mappings.

namespace progpu::native::text {
namespace {

std::uint32_t find_directional_mapping(
    std::span<const std::uint32_t> pairs,
    std::uint32_t code_point) noexcept {
    std::size_t low = 0U;
    std::size_t high = pairs.size() / 2U;
    while (low < high) {
        const std::size_t middle = low + (high - low) / 2U;
        const std::size_t offset = middle * 2U;
        if (code_point < pairs[offset]) {
            high = middle;
        } else if (code_point > pairs[offset]) {
            low = middle + 1U;
        } else {
            return pairs[offset + 1U];
        }
    }
    return code_point;
}

} // namespace

std::uint32_t get_unicode_mirrored_code_point(
    std::uint32_t code_point) noexcept {
    return find_directional_mapping(detail::unicode_mirror_pairs, code_point);
}

std::uint32_t get_unicode_vertical_code_point(
    std::uint32_t code_point) noexcept {
    return find_directional_mapping(detail::unicode_vertical_pairs, code_point);
}

} // namespace progpu::native::text

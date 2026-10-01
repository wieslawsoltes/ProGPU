#pragma once

#include "progpu_native_edit_item_data.generated.hpp"

#include <cstddef>

namespace progpu::native::text::detail {

inline constexpr std::uint32_t edit_item_soft_entry = 1U;
inline constexpr std::uint32_t edit_item_suppressed_entry = 2U;
inline constexpr std::uint32_t edit_item_white_exit = 4U;

struct edit_item_properties final {
    // Zero is the existing paragraph-bridge policy, NOT a fabricated Windows
    // eScript identity. Nonzero values come from exact observed native items.
    std::uint32_t policy_identity = 0U;
    std::uint32_t flags = 0U;
};

inline bool try_get_observed_edit_item_properties(
    std::uint32_t code_point,
    edit_item_properties& result) noexcept
{
    std::size_t low = 0U;
    std::size_t high = edit_item_property_ranges.size() / 4U;
    while (low < high) {
        const auto middle = low + (high - low) / 2U;
        const auto offset = middle * 4U;
        if (code_point < edit_item_property_ranges[offset]) high = middle;
        else if (code_point > edit_item_property_ranges[offset + 1U]) low = middle + 1U;
        else {
            result = {edit_item_property_ranges[offset + 2U], edit_item_property_ranges[offset + 3U]};
            return true;
        }
    }
    return false;
}

inline bool requires_observed_edit_item_properties(std::uint32_t script) noexcept
{
    for (const auto selected : edit_item_observed_script_domains)
        if (selected == script) return true;
    return false;
}

} // namespace progpu::native::text::detail

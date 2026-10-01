#pragma once

#include "progpu_native_text.hpp"
#include "progpu_native_edit_symbol_data.generated.hpp"

namespace progpu::native::text::detail {

inline unicode_line_break_class get_edit_symbol_line_break_class(
    std::uint32_t code_point) noexcept
{
    static_assert(static_cast<std::uint32_t>(unicode_line_break_class::alphabetic) == 3U);
    static_assert(static_cast<std::uint32_t>(unicode_line_break_class::ideographic) == 25U);
    std::size_t low = 0U;
    std::size_t high = edit_symbol_line_class_ranges.size() / 3U;
    while (low < high) {
        const auto middle = low + (high - low) / 2U;
        const auto offset = middle * 3U;
        if (code_point < edit_symbol_line_class_ranges[offset]) high = middle;
        else if (code_point > edit_symbol_line_class_ranges[offset + 1U]) low = middle + 1U;
        else return static_cast<unicode_line_break_class>(edit_symbol_line_class_ranges[offset + 2U]);
    }
    return unicode_line_break_class::unknown;
}

// Original-source EDIT selection attaches ZWJ to its left-hand class but does
// not impose modern UAX14 LB8a after it. A separate pinned, observed BMP symbol
// domain supplies its resolved AL/ID property. This private profile changes neither
// the original scalar/source records nor the default Unicode17 worker.
bool try_resolve_edit_selection_line_breaks(
    std::span<const unicode_scalar> input,
    std::span<unicode_line_break_class> class_scratch,
    std::span<text_line_break_kind> breaks_after,
    unicode_error* error = nullptr) noexcept;

} // namespace progpu::native::text::detail

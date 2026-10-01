#pragma once

#include "progpu_native_text.hpp"

namespace progpu::native::text::detail {

// Original-source EDIT selection attaches ZWJ to its left-hand class but does
// not impose modern UAX14 LB8a after it. This private profile changes neither
// the original scalar/source records nor the default Unicode17 worker.
bool try_resolve_edit_joiner_line_breaks(
    std::span<const unicode_scalar> input,
    std::span<unicode_line_break_class> class_scratch,
    std::span<text_line_break_kind> breaks_after,
    unicode_error* error = nullptr) noexcept;

} // namespace progpu::native::text::detail

#pragma once

#include <cstdint>
#include <span>
#include <vector>

namespace progpu::native::text {

enum class edit_word_boundary_error : std::uint32_t {
    none = 0U,
    invalid_encoding,
    input_too_large,
    allocation_failure,
    dependency_unavailable,
    dependency_failure,
    unqualified_bmp_symbol_policy,
    unqualified_joiner_policy,
    unqualified_complex_script_policy,
    unqualified_script_item_transition_policy,
    invalid_paragraph_level
};

// One owned original-source UTF-16 generation. This internal explicit profile
// is NOT an ordinary EDIT provider capability: unresolved compatibility classes
// fail closed, and no partial candidate is published. In particular, modern
// grapheme segmentation cannot silently remove a legacy EDIT word boundary.
struct edit_word_boundary_snapshot final {
    std::vector<std::uint32_t> positions{};
    std::uint32_t leading_content_start = 0U;
};

bool try_create_edit_word_boundary_snapshot(
    std::span<const std::uint16_t> source,
    edit_word_boundary_snapshot& result,
    edit_word_boundary_error& error,
    std::int8_t paragraph_level = 0) noexcept;

} // namespace progpu::native::text

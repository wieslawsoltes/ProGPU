#pragma once

#include "progpu_native_text.hpp"

#include <cstdint>
#include <span>

namespace progpu::native::text::detail {

enum class gpos_apply_result : std::uint8_t {
    no_match,
    applied,
    invalid_argument,
    malformed
};

gpos_apply_result apply_gpos_lookup_at(
    const open_type_layout_table_view&,
    std::uint16_t,
    std::span<shaping_glyph>,
    std::size_t,
    const open_type_gpos_apply_options&,
    std::uint32_t) noexcept;

gpos_apply_result apply_gpos_context_subtable(
    const open_type_layout_table_view& gpos,
    std::span<const std::byte> table,
    std::uint16_t type,
    std::size_t subtable,
    std::span<shaping_glyph> glyphs,
    std::size_t position,
    std::uint16_t lookup_flags,
    std::uint16_t mark_filtering_set,
    const open_type_gpos_apply_options& options,
    std::uint32_t depth) noexcept;

} // namespace progpu::native::text::detail

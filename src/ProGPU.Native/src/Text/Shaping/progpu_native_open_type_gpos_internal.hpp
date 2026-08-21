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

inline void mark_gpos_dependency(
    std::span<shaping_glyph> glyphs,
    std::size_t first,
    std::size_t last) noexcept {
    if (first > last || last >= glyphs.size() || first == last) {
        return;
    }
    std::int32_t minimum_cluster = glyphs[first].cluster;
    for (std::size_t index = first + 1U; index <= last; ++index) {
        if (glyphs[index].cluster < minimum_cluster) {
            minimum_cluster = glyphs[index].cluster;
        }
    }
    constexpr auto dependency_flags =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    for (std::size_t index = first; index <= last; ++index) {
        if (glyphs[index].cluster == minimum_cluster) {
            continue;
        }
        glyphs[index].flags = static_cast<shaping_glyph_flags>(
            static_cast<std::uint32_t>(glyphs[index].flags) |
            dependency_flags);
    }
}

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

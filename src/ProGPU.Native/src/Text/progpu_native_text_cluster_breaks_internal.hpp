#pragma once

#include "progpu_native_text.hpp"

#include <algorithm>
#include <limits>

namespace progpu::native::text::detail {

// Original paragraph producer from Text/Interop/progpu_native_text_shaping_interop.cpp.
// Both wire scalars and unicode_scalar retain the same input_index/length
// fields; this helper changes no source, bidi or cluster-end policy.
// Outputs are private scratch: a later invalid glyph group may leave earlier
// groups written. This is not a public atomic-generation publication promise.
template<class Scalar>
bool try_map_logical_cluster_breaks(
    std::span<const Scalar> input,
    std::span<const text_line_break_kind> scalar_breaks,
    std::span<const shaping_glyph> glyphs,
    std::span<text_line_break_kind> glyph_breaks) noexcept {
    if (scalar_breaks.size() != input.size() ||
        glyph_breaks.size() < glyphs.size()) {
        return false;
    }
    if (glyphs.empty()) return true;
    if (input.empty()) return false;
    for (std::size_t index = 0U; index < input.size(); ++index) {
        const std::uint64_t end =
            static_cast<std::uint64_t>(input[index].input_index) +
            input[index].input_length;
        if (end > std::numeric_limits<std::uint32_t>::max() ||
            (index != 0U && input[index].input_index <
                    static_cast<std::uint64_t>(input[index - 1U].input_index) +
                        input[index - 1U].input_length)) {
            return false;
        }
    }
    std::size_t scalar_cursor = 0U;
    std::size_t glyph_start = 0U;
    std::int32_t previous_cluster = -1;
    while (glyph_start < glyphs.size()) {
        const std::int32_t cluster = glyphs[glyph_start].cluster;
        if (cluster < 0 || cluster <= previous_cluster) return false;
        std::size_t glyph_end = glyph_start + 1U;
        while (glyph_end < glyphs.size() &&
            glyphs[glyph_end].cluster == cluster) {
            ++glyph_end;
        }
        if (glyph_end < glyphs.size() &&
            glyphs[glyph_end].cluster <= cluster) {
            return false;
        }
        const std::uint32_t next_cluster = glyph_end < glyphs.size()
            ? static_cast<std::uint32_t>(glyphs[glyph_end].cluster)
            : std::numeric_limits<std::uint32_t>::max();
        while (scalar_cursor < input.size() &&
            input[scalar_cursor].input_index < next_cluster) {
            ++scalar_cursor;
        }
        if (scalar_cursor == 0U) return false;
        std::fill(
            glyph_breaks.begin() + static_cast<std::ptrdiff_t>(glyph_start),
            glyph_breaks.begin() + static_cast<std::ptrdiff_t>(glyph_end),
            text_line_break_kind::prohibited);
        glyph_breaks[glyph_end - 1U] = scalar_breaks[scalar_cursor - 1U];
        previous_cluster = cluster;
        glyph_start = glyph_end;
    }
    return true;
}

} // namespace progpu::native::text::detail

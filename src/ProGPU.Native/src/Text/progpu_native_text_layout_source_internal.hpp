#pragma once

#include "progpu_native_text_layout_retained_internal.hpp"

namespace progpu::native::text {

// Original producer-owned logical metrics, not promoted float positions. These
// are independent of the raw shaping unit system (design units or device 26.6).
struct text_source_glyph_metrics final {
    double advance_x = 0.0, advance_y = 0.0, offset_x = 0.0, offset_y = 0.0;
};

struct text_source_item_metrics final {
    double ascent = 0.0, descent = 0.0;
};

struct text_source_glyph_position final {
    double x = 0.0, y = 0.0, advance_x = 0.0, advance_y = 0.0;
};

struct text_source_line_metrics final {
    double width = 0.0, top = 0.0, height = 0.0;
    double baseline_offset = 0.0, baseline_y = 0.0, origin_x = 0.0;
};

// Optional double lane of the same logical scanner/measured writer. IDs, source
// ranges, flags and actual L1/L2 order are published through the original arrays;
// these spans have identical occurrence/line indices. All arrays are caller-owned
// disjoint synchronous scratch, like text_layout_retained_metadata. Glyph/line
// float outputs are the raster projection, not the source geometry authority.
struct text_source_layout final {
    double maximum_width = 0.0, line_height = 0.0;
    std::span<const text_source_glyph_metrics> logical_metrics{};
    std::span<const text_source_item_metrics> item_metrics{};
    std::span<text_source_glyph_position> positioned_metrics{};
    std::span<text_source_line_metrics> line_metrics{};
};

// First admitted lane is ordinary horizontal, non-justified and untrimmed, with
// no tabs/objects. The caller must retain all input and output from this writer
// as one generation; this is not a Display provider or boundary-recomposition
// capability. Original unsafe flags and shaped-cluster boundaries still apply.
bool try_layout_source_measured_logical_shaped_text_retained(
    std::span<const shaping_glyph> logical_glyphs, std::span<const text_line_break_kind> breaks_after,
    std::span<const std::int8_t> bidi_levels, std::span<const float> glyph_scales,
    std::int8_t paragraph_level, const text_layout_options& options, text_tab_options tabs,
    text_logical_layout_scratch scratch,
    std::span<positioned_text_glyph> positioned_glyphs, std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count, std::uint32_t& line_count,
    text_layout_retained_metadata retained, text_source_layout source,
    font_error* error = nullptr) noexcept;

} // namespace progpu::native::text

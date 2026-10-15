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

enum class text_source_width_mode : std::uint8_t { double_advances, ideal_units };

struct text_source_width_policy final {
    text_source_width_mode mode = text_source_width_mode::double_advances;
    double pixels_per_dip = 0.0;
    bool operator==(const text_source_width_policy&) const = default;
};

// A sequential native line measure, separate from per-glyph draw advances.
// The explicit ideal lane retains integral 1/300-DIP width before projection.
// Each append is O(1), allocation-free and atomic on numeric failure. Prefix
// dependence precludes SIMD; the original independent glyph projections stay
// vectorized. A double lane keeps the original addition order exactly.
struct text_source_width_accumulator final {
    double width = 0.0;
    std::int64_t ideal_width = 0;
    bool append(double advance, text_source_width_policy policy) noexcept;
    bool fits(double maximum_width, text_source_width_policy policy) const noexcept;
};

bool valid_text_source_width_policy(text_source_width_policy policy) noexcept;

struct text_source_glyph_position final {
    double x = 0.0, y = 0.0, advance_x = 0.0, advance_y = 0.0;
    std::int32_t cluster = 0;
};

struct text_source_line_metrics final {
    double width = 0.0, top = 0.0, height = 0.0;
    double baseline_offset = 0.0, baseline_y = 0.0, origin_x = 0.0;
    std::uint32_t glyph_start = 0U, glyph_count = 0U;
};

// Native fitting owns these exact logical partitions after boundary placement
// recomposition. No source caller may synthesize a partition or clear raw flags.
struct text_source_fitted_line final {
    std::uint32_t glyph_start = 0U, glyph_count = 0U;
    double width = 0.0;
    bool clipped = false;
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
    std::span<const text_source_fitted_line> fitted_lines{};
    text_source_width_policy width_policy{};
    bool preserve_soft_wrap_space_geometry = false;
};

bool is_text_layout_trailing_space(std::uint32_t code_point) noexcept;

// Complete original U+0020 cluster suffix only. Other space families keep
// their independent contracts; this never trims or rewrites the source.
std::size_t source_soft_wrap_space_start(std::span<const shaping_glyph> glyphs) noexcept;

// Same original cluster ordering and L2 writer as the generic route. Only the
// explicit source soft-wrap caller may retain its resolved trailing run.
bool try_get_source_text_line_visual_indices(
    std::span<const shaping_glyph> logical_glyphs, std::span<const std::int8_t> bidi_levels,
    std::int8_t paragraph_level, std::span<text_visual_cluster_group> group_scratch,
    std::span<std::uint32_t> visual_indices, std::uint32_t& written,
    bool preserve_trailing_spaces, font_error* error = nullptr) noexcept;

// First admitted lane is ordinary horizontal, non-justified and untrimmed, with
// no tabs/objects. The caller must retain all input and output from this writer
// as one generation; this is not a Display provider. Without an owned fitted
// partition the original unsafe flags remain authoritative; native fitted
// partitions require separately retained original-boundary placement proof.
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

#pragma once

#include "progpu_native_hinted_shaper.hpp"

namespace progpu::native::text {

// Private horizontal fitting snapshot. The exact shaped generation remains
// owned, including descriptors absent from maximum-lines positioned output.
struct hinted_text_layout final {
    std::shared_ptr<const hinted_shaped_run> run{};
    float logical_units_per_physical_pixel = 0.0F;
    std::int8_t paragraph_level = 0;
    text_layout_options options{}; // scale is logical units per device 26.6 unit.
    std::vector<shaping_glyph> logical_glyphs{}; // Original metrics, Y-down only.
    std::vector<std::uint32_t> logical_run_indices{};
    std::vector<text_line_break_kind> breaks_after{};
    std::vector<std::int8_t> logical_bidi_levels{};
    std::vector<std::int32_t> logical_cluster_ends{};
    std::vector<text_justification_class> justification_classes{};
    std::vector<text_item_metrics> item_metrics{};
    std::vector<positioned_text_glyph> glyphs{}; // glyph_index is original run index.
    std::vector<std::uint32_t> descriptor_indices{}; // One exact slot per positioned glyph.
    std::vector<std::int8_t> bidi_levels{};
    std::vector<std::int32_t> cluster_ends{};
    std::vector<positioned_text_line> lines{};
    std::vector<float> line_origins{}; // Writer pen origins, independent of ink offsets.
    text_layout_metrics metrics{};
};

// Metadata follows the retained run's glyph order; the first run.glyphs.size()
// entries are copied, and every unused caller tail remains untouched. Metrics
// are mandatory caller-resolved nonnegative logical ascent/descent, never the
// native driver's rounded global metrics. Widths/heights are already logical.
// options.scale must be 1; the sole conversion is units_per_pixel / 64, applied
// by the original writer's SIMD metric lanes. No em/UPM scaling or reshaping.
// Horizontal LTR/RTL only; synthetic trimming signs and source Display remain
// unadmitted. Result publication is atomic and owns every used metadata field.
bool try_layout_hinted_shaped_run(std::shared_ptr<const hinted_shaped_run> run,
    std::span<const text_line_break_kind> breaks_after,
    std::span<const std::int8_t> bidi_levels,
    std::span<const std::int32_t> cluster_ends,
    std::span<const text_item_metrics> item_metrics,
    std::span<const text_justification_class> justification_classes,
    std::int8_t paragraph_level, float logical_units_per_physical_pixel,
    const text_layout_options& options,
    std::shared_ptr<const hinted_text_layout>& result,
    font_error* error = nullptr) noexcept;

} // namespace progpu::native::text

#pragma once

#include "progpu_native_text.hpp"

namespace progpu::native::text {

struct text_layout_line_frame final {
    double top = 0.0;
    float baseline_offset = 0.0F;
    bool measured = false;
    bool operator==(const text_layout_line_frame&) const = default;
};

// Private opt-in writer outputs, borrowed for this synchronous call. The caller
// supplies disjoint owned spans covering the original requirements' complete
// glyph/line capacities, including maximum-lines fitting. Only emitted entries
// are written; unused tails retain their original values. These are the actual
// per-line L1/L2 group levels and initial pen plus the applied alignment shift,
// never a second bidi/order pass or glyph-ink-derived origin.
struct text_layout_retained_metadata final {
    std::span<std::int8_t> positioned_bidi_levels{};
    std::span<float> line_origins{};
    // Optional additive capture of the actual writer inputs. An empty span
    // preserves existing callers; when requested it covers full line capacity.
    std::span<text_layout_line_frame> line_frames{};
};

// Same original measured writer and failure/count convention, with mandatory
// metadata capacities checked before scratch, glyph or line writes. Trimming
// rejects: synthetic sign metadata requires its own contract. Earlier scratch
// prefixes on a later writer failure remain private/unpublished, as in the
// original function. No public ABI/scratch change or source Display admission.
bool try_layout_measured_logical_shaped_text_retained(
    std::span<const shaping_glyph> logical_glyphs, std::span<const text_line_break_kind> breaks_after,
    std::span<const std::int8_t> bidi_levels, std::span<const float> glyph_scales,
    std::int8_t paragraph_level, const text_layout_options& options, text_tab_options tabs,
    std::span<float> advance_scratch, text_logical_layout_scratch scratch,
    std::span<positioned_text_glyph> positioned_glyphs, std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count, std::uint32_t& line_count,
    std::span<const text_justification_class> classes,
    std::span<const text_item_metrics> item_metrics,
    text_layout_retained_metadata retained, font_error* error = nullptr) noexcept;

} // namespace progpu::native::text

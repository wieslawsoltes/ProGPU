#include "progpu_native_hinted_paragraph_transport_internal.hpp"

#include <bit>
#include <cmath>
#include <algorithm>
#include <new>

namespace progpu::native::text {
namespace {
bool exact(double left, double right) noexcept {
    return std::bit_cast<std::uint64_t>(left) == std::bit_cast<std::uint64_t>(right);
}
progpu_native_hinted_source_glyph_offset source_offset(const progpu_native_positioned_text_glyph& glyph,
    const progpu_native_hinted_glyph_nominal_metrics& metric, std::uint32_t units_per_em,
    float em, std::int8_t level, float baseline, double prefix) noexcept {
    const double nominal_advance = static_cast<double>(metric.advance_width_design_units) /
        static_cast<double>(units_per_em) * static_cast<double>(em);
    return {(level & 1) == 0 ? static_cast<double>(glyph.x) - prefix :
        -prefix - nominal_advance - static_cast<double>(glyph.x),
        -(static_cast<double>(glyph.y) - static_cast<double>(baseline))};
}
}

// The view belongs to the original validated resource. This helper never accepts
// a public imported view or computes a new line. Source nominal offsets use the
// existing double prefix/order, while retained writer positions remain floats.
progpu_native_status validate_hinted_source_frame(const progpu_native_hinted_glyph_resource_view& view,
    std::span<const progpu_native_hinted_glyph_nominal_metrics> nominal,
    std::span<const std::uint32_t> indices, float source_em_size, progpu_native_point source_baseline,
    std::span<const double> advances, std::span<const progpu_native_hinted_source_glyph_offset> offsets,
    progpu_native_hinted_source_glyph_frame& result) noexcept {
    if (indices.empty() || indices.size() != advances.size() || indices.size() != offsets.size() ||
        nominal.size() != view.counts.positioned_glyph_count || !std::isfinite(source_em_size) || source_em_size <= 0.0F ||
        !std::isfinite(source_baseline.x) || !std::isfinite(source_baseline.y)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (view.layout.direction != PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT &&
        view.layout.direction != PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    if (indices.front() >= view.counts.positioned_glyph_count) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    std::uint32_t line_index = 0U;
    for (; line_index < view.counts.line_count; ++line_index) {
        const auto& line = view.lines[line_index];
        if (indices.front() >= line.glyph_start && indices.front() - line.glyph_start < line.glyph_count) break;
    }
    if (line_index == view.counts.line_count) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    const auto& line = view.lines[line_index];
    const auto font_index = view.positioned_glyphs[indices.front()].font_index;
    if (font_index >= view.font_source_count) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto units_per_em = view.font_sources[font_index].units_per_em;
    const auto level = view.positioned_bidi_levels[indices.front()];
    if (units_per_em == 0U || level < 0 || !std::isfinite(line.baseline_y)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const double translation_y = static_cast<double>(source_baseline.y) - line.baseline_y;
    const float draw_y = static_cast<float>(translation_y);
    // A source frame must be representable by the actual renderer translation,
    // not repaired with a per-glyph offset or accepted with an epsilon.
    if (!std::isfinite(draw_y) || static_cast<double>(draw_y) != translation_y) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    double prefix = 0.0;
    for (std::size_t i = 0U; i < indices.size(); ++i) {
        const auto index = indices[i];
        if (index >= view.counts.positioned_glyph_count) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        if (index < line.glyph_start || index - line.glyph_start >= line.glyph_count) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        const auto& glyph = view.positioned_glyphs[index];
        const auto& metric = nominal[index];
        const auto run_index = view.positioned_owners[index].run_index;
        if (run_index >= view.counts.run_count || metric.positioned_index != index || metric.font_index != glyph.font_index ||
            metric.glyph_id != glyph.glyph_id) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        if (glyph.font_index != font_index || view.positioned_bidi_levels[index] != level ||
            source_em_size / static_cast<float>(units_per_em) != view.runs[run_index].source_scale ||
            glyph.advance_y != 0.0F) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        if (!std::isfinite(glyph.x) || !std::isfinite(glyph.y) || !std::isfinite(glyph.advance_x) ||
            !std::isfinite(advances[i]) || !exact(advances[i], static_cast<double>(glyph.advance_x)))
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        if (!std::isfinite(glyph.x + source_baseline.x) || !std::isfinite(glyph.y + draw_y))
            return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        // Preserve source GlyphTypeface's design-width / UPM * em arithmetic;
        // no hinted 26.6 advance, GPOS subtraction or half-em substitute.
        const auto expected = source_offset(glyph, metric, units_per_em, source_em_size, level, line.baseline_y, prefix);
        if (!std::isfinite(offsets[i].x) || !std::isfinite(offsets[i].y) ||
            offsets[i].x != expected.x || offsets[i].y != expected.y) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        prefix += advances[i];
        if (!std::isfinite(prefix)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    const progpu_native_hinted_source_glyph_frame candidate{line_index, font_index, level, line.baseline_y,
        source_baseline, {source_baseline.x, draw_y}, {0.0F, -line.baseline_y}};
    result = candidate;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status copy_hinted_source_offsets(const progpu_native_hinted_glyph_resource_view& view,
    std::span<const progpu_native_hinted_glyph_nominal_metrics> nominal, std::span<const std::uint32_t> indices,
    float source_em_size, std::span<progpu_native_hinted_source_glyph_offset> output) noexcept {
    if (indices.empty() || output.size() < indices.size() || nominal.size() != view.counts.positioned_glyph_count ||
        indices.front() >= view.counts.positioned_glyph_count) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    std::uint32_t line_index = 0U;
    for (; line_index < view.counts.line_count; ++line_index) {
        const auto& line = view.lines[line_index];
        if (indices.front() >= line.glyph_start && indices.front() - line.glyph_start < line.glyph_count) break;
    }
    if (line_index == view.counts.line_count) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    const auto font_index = view.positioned_glyphs[indices.front()].font_index;
    if (font_index >= view.font_source_count || view.font_sources[font_index].units_per_em == 0U)
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        // Private staging preserves caller outputs on any later invalid owner,
        // frame or arithmetic. Advances are copied original values, never inferred.
        std::vector<double> advances; advances.reserve(indices.size());
        std::vector<progpu_native_hinted_source_glyph_offset> offsets; offsets.reserve(indices.size());
        double prefix = 0.0;
        for (const auto index : indices) {
            if (index >= view.counts.positioned_glyph_count) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            const auto& glyph = view.positioned_glyphs[index];
            offsets.push_back(source_offset(glyph, nominal[index], view.font_sources[font_index].units_per_em,
                source_em_size, view.positioned_bidi_levels[indices.front()], view.lines[line_index].baseline_y, prefix));
            advances.push_back(static_cast<double>(glyph.advance_x));
            prefix += glyph.advance_x;
        }
        progpu_native_hinted_source_glyph_frame frame{};
        const auto status = validate_hinted_source_frame(view, nominal, indices, source_em_size, {}, advances, offsets, frame);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
        std::copy(offsets.begin(), offsets.end(), output.begin());
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}
} // namespace progpu::native::text

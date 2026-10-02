#include "progpu_native_hinted_source_run_internal.hpp"

#include <algorithm>
#include <bit>
#include <cmath>
#include <new>

namespace progpu::native::text {
namespace {
bool same_double(double a, double b) noexcept {
    return std::bit_cast<std::uint64_t>(a) == std::bit_cast<std::uint64_t>(b);
}
struct source_run_values final {
    std::vector<double> advances{};
    std::vector<progpu_native_hinted_source_glyph_offset> offsets{};
    progpu_native_hinted_source_run_frame frame{};
};

progpu_native_status stage_source_run(const hinted_paragraph_generation& p,
    const progpu_native_hinted_glyph_resource_view& view,
    std::span<const progpu_native_hinted_glyph_nominal_metrics> nominal,
    std::span<const std::uint32_t> indices, double em, double dpi,
    const progpu_native_hinted_source_glyph_offset* baseline, source_run_values& result) {
    if (!p.has_source_geometry) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    if (indices.empty() || !std::isfinite(em) || em <= 0.0 || !std::isfinite(dpi) || dpi <= 0.0 ||
        p.source_glyphs.size() != p.glyphs.size() || p.source_lines.size() != p.lines.size() ||
        p.positioned_owners.size() != p.glyphs.size() || p.bidi_levels.size() != p.glyphs.size() ||
        nominal.size() != p.glyphs.size() || view.counts.positioned_glyph_count != p.glyphs.size() ||
        indices.front() >= p.glyphs.size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (p.layout.direction != PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT &&
        p.layout.direction != PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    std::size_t line_index = 0U;
    for (; line_index < p.source_lines.size(); ++line_index) {
        const auto& l = p.source_lines[line_index];
        if (indices.front() >= l.glyph_start && indices.front() - l.glyph_start < l.glyph_count) break;
    }
    if (line_index >= p.source_lines.size()) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    const auto& line = p.source_lines[line_index];
    const auto run_index = p.positioned_owners[indices.front()].run_index;
    if (run_index >= p.runs.size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto& run = p.runs[run_index];
    if (run.style_index >= p.source_styles.size() || run.font_index >= view.font_source_count || view.font_sources == nullptr)
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const auto& style = p.source_styles[run.style_index];
    const auto units_per_em = view.font_sources[run.font_index].units_per_em;
    const auto level = p.bidi_levels[indices.front()];
    if (units_per_em == 0U || level < 0 || !std::isfinite(line.baseline_y)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (!same_double(em, style.em_size) || !same_double(dpi, style.pixels_per_dip) ||
        static_cast<double>(view.dpi_scale) != dpi) return PROGPU_NATIVE_STATUS_UNSUPPORTED;

    const auto origin = baseline == nullptr ? progpu_native_hinted_source_glyph_offset{0.0, line.baseline_y} : *baseline;
    if (!std::isfinite(origin.x) || !std::isfinite(origin.y)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    const double translate_y = origin.y - line.baseline_y;
    const progpu_native_point raster{static_cast<float>(origin.x), static_cast<float>(translate_y)};
    if (!std::isfinite(raster.x) || !std::isfinite(raster.y) || static_cast<double>(raster.x) != origin.x ||
        static_cast<double>(raster.y) != translate_y) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    result.frame = {static_cast<std::uint32_t>(line_index), run.font_index, level, 0U, line.baseline_y,
        origin, {origin.x, translate_y}, raster};
    result.advances.reserve(indices.size()); result.offsets.reserve(indices.size());
    double prefix = 0.0;
    for (const auto index : indices) {
        if (index >= p.glyphs.size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        if (index < line.glyph_start || index - line.glyph_start >= line.glyph_count ||
            p.positioned_owners[index].run_index != run_index || p.bidi_levels[index] != level)
            return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        const auto& raw = p.glyphs[index];
        const auto& glyph = p.source_glyphs[index];
        const auto& metric = nominal[index];
        if (raw.glyph_index >= p.logical_font_indices.size() || raw.glyph_index >= p.logical_owners.size() ||
            p.logical_font_indices[raw.glyph_index] != run.font_index ||
            p.logical_owners[raw.glyph_index] != p.positioned_owners[index] || metric.positioned_index != index ||
            metric.font_index != run.font_index || metric.glyph_id != raw.glyph_id || glyph.cluster != raw.cluster)
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        if (glyph.advance_y != 0.0) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        if (!std::isfinite(glyph.x) || !std::isfinite(glyph.y) || !std::isfinite(glyph.advance_x) ||
            !std::isfinite(raw.x + raster.x) || !std::isfinite(raw.y + raster.y)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        // This is the original GlyphTypeface nominal-offset convention, NOT a
        // new shaping offset or rounded replacement for the retained advance.
        const double nominal_advance = static_cast<double>(metric.advance_width_design_units) /
            static_cast<double>(units_per_em) * em;
        const progpu_native_hinted_source_glyph_offset offset{
            (level & 1) == 0 ? glyph.x - prefix : -prefix - nominal_advance - glyph.x,
            -(glyph.y - line.baseline_y)};
        if (!std::isfinite(offset.x) || !std::isfinite(offset.y)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        result.advances.push_back(glyph.advance_x); result.offsets.push_back(offset);
        prefix += glyph.advance_x;
        if (!std::isfinite(prefix)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}
} // namespace

progpu_native_status copy_hinted_source_metrics(const hinted_paragraph_generation& paragraph,
    const progpu_native_hinted_glyph_resource_view& resource,
    std::span<const progpu_native_hinted_glyph_nominal_metrics> nominal,
    std::span<const std::uint32_t> indices, double em, double dpi,
    std::span<double> advances, std::span<progpu_native_hinted_source_glyph_offset> offsets) noexcept {
    if (advances.size() < indices.size() || offsets.size() < indices.size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        source_run_values candidate;
        const auto status = stage_source_run(paragraph, resource, nominal, indices, em, dpi, nullptr, candidate);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
        std::copy(candidate.advances.begin(), candidate.advances.end(), advances.begin());
        std::copy(candidate.offsets.begin(), candidate.offsets.end(), offsets.begin());
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}

progpu_native_status validate_hinted_source_run(const hinted_paragraph_generation& paragraph,
    const progpu_native_hinted_glyph_resource_view& resource,
    std::span<const progpu_native_hinted_glyph_nominal_metrics> nominal,
    std::span<const std::uint32_t> indices, double em, double dpi,
    progpu_native_hinted_source_glyph_offset baseline, std::span<const double> advances,
    std::span<const progpu_native_hinted_source_glyph_offset> offsets, progpu_native_hinted_source_run_frame& frame) noexcept {
    if (advances.size() != indices.size() || offsets.size() != indices.size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        source_run_values candidate;
        const auto status = stage_source_run(paragraph, resource, nominal, indices, em, dpi, &baseline, candidate);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
        for (std::size_t i = 0U; i < indices.size(); ++i)
            if (!same_double(advances[i], candidate.advances[i]) || !same_double(offsets[i].x, candidate.offsets[i].x) ||
                !same_double(offsets[i].y, candidate.offsets[i].y)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        frame = candidate.frame;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) { return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY; }
    catch (...) { return PROGPU_NATIVE_STATUS_INTERNAL_ERROR; }
}
} // namespace progpu::native::text

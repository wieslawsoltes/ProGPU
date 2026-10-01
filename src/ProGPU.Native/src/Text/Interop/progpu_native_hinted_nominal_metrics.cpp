#include "progpu_native_hinted_paragraph_transport_internal.hpp"

#include <limits>
#include <new>

namespace progpu::native::text {

progpu_native_status capture_hinted_nominal_metrics(const hinted_paragraph_generation& paragraph,
    std::vector<progpu_native_hinted_glyph_nominal_metrics>& result) noexcept {
    if (!paragraph.normalized_coordinates.empty()) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
    if (paragraph.glyphs.size() > UINT32_MAX || paragraph.positioned_owners.size() != paragraph.glyphs.size())
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        // One parser view per original face, all borrowed from retained immutable
        // owners. Direct hmtx lookup deliberately avoids design_advance_width's
        // legacy half-em fallback. No glyph load, HVAR inference or hint execution.
        std::vector<sfnt_font_view> fonts(paragraph.font_sources.size());
        std::vector<std::uint16_t> glyph_counts(fonts.size());
        for (std::size_t i = 0U; i < fonts.size(); ++i) {
            const auto& source = paragraph.font_sources[i];
            if (source == nullptr || !sfnt_font_view::try_create(source->bytes, source->face_index, fonts[i]) ||
                !fonts[i].try_get_glyph_count(glyph_counts[i])) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        }
        std::vector<progpu_native_hinted_glyph_nominal_metrics> candidate;
        candidate.reserve(paragraph.glyphs.size());
        // Dependent original owner/font gathers and fallible table reads, not a
        // compute fallback: preserve occurrence order and atomic error semantics.
        for (std::size_t i = 0U; i < paragraph.glyphs.size(); ++i) {
            const auto& glyph = paragraph.glyphs[i];
            const auto& owner = paragraph.positioned_owners[i];
            if (glyph.glyph_index >= paragraph.logical_font_indices.size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            const auto font_index = paragraph.logical_font_indices[glyph.glyph_index];
            if (font_index >= fonts.size() || glyph.glyph_id >= glyph_counts[font_index] ||
                owner.run_index >= paragraph.runs.size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            const auto& run = paragraph.runs[owner.run_index];
            if (run.font_index != font_index || run.style_index >= paragraph.device_styles.size())
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            if (!paragraph.device_styles[run.style_index].variation_coordinates_16_16.empty())
                return PROGPU_NATIVE_STATUS_UNSUPPORTED;
            sfnt_horizontal_glyph_metrics metric{};
            if (!fonts[font_index].try_get_horizontal_glyph_metrics(
                    static_cast<std::uint16_t>(glyph.glyph_id), metric)) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
            candidate.push_back({static_cast<std::uint32_t>(i), font_index, glyph.glyph_id, metric.advance_width});
        }
        result.swap(candidate);
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

} // namespace progpu::native::text

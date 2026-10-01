#include "progpu_native_hinted_paragraph_interaction.hpp"
#include "progpu_native_owned_allocation_internal.hpp"
#include "../progpu_native_text_interaction_impl.hpp"

#include <limits>
#include <new>

namespace progpu::native::text {
namespace {
bool exact_source_maps(const hinted_paragraph_generation& paragraph) noexcept {
    const auto logical = paragraph.logical_glyphs.size();
    if (paragraph.layout.direction > PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT ||
        paragraph.layout.trimming != PROGPU_NATIVE_TEXT_TRIMMING_NONE ||
        paragraph.logical_owners.size() != logical || paragraph.logical_bidi_levels.size() != logical ||
        paragraph.logical_cluster_ends.size() != logical || paragraph.logical_font_indices.size() != logical ||
        paragraph.positioned_owners.size() != paragraph.glyphs.size() ||
        paragraph.bidi_levels.size() != paragraph.glyphs.size() || paragraph.cluster_ends.size() != paragraph.glyphs.size() ||
        paragraph.line_origins.size() != paragraph.lines.size() ||
        paragraph.glyphs.size() > std::numeric_limits<std::uint32_t>::max() / 2U ||
        paragraph.lines.size() > std::numeric_limits<std::uint32_t>::max()) return false;
    for (std::size_t index = 0U; index < paragraph.glyphs.size(); ++index) {
        const auto& positioned = paragraph.glyphs[index];
        if (positioned.glyph_index >= logical) return false;
        const auto source = positioned.glyph_index;
        const auto owner = paragraph.logical_owners[source];
        if (owner != paragraph.positioned_owners[index] || owner.run_index >= paragraph.runs.size() ||
            positioned.glyph_id != paragraph.logical_glyphs[source].glyph_id ||
            positioned.cluster != paragraph.logical_glyphs[source].cluster ||
            paragraph.cluster_ends[index] != paragraph.logical_cluster_ends[source]) return false;
        // The original writer retains its actual L1-adjusted visual group
        // level. Logical shaping levels remain separate source metadata;
        // interaction must not independently reset/re-resolve either policy.
        const auto& run = paragraph.runs[owner.run_index];
        if (run.generation == nullptr || run.generation->batch == nullptr ||
            paragraph.logical_font_indices[source] != run.font_index ||
            owner.run_glyph_index >= run.generation->glyphs.size() ||
            owner.run_glyph_index >= run.generation->descriptor_indices.size() ||
            owner.descriptor_index != run.generation->descriptor_indices[owner.run_glyph_index] ||
            owner.descriptor_index >= run.generation->source_descriptor_count ||
            owner.descriptor_index >= run.generation->batch->glyphs.size() ||
            positioned.glyph_id != run.generation->glyphs[owner.run_glyph_index].glyph_id ||
            positioned.cluster != run.generation->glyphs[owner.run_glyph_index].cluster ||
            positioned.glyph_id != run.generation->batch->glyphs[owner.descriptor_index].glyph_index) return false;
    }
    return true;
}
} // namespace

bool hinted_paragraph_interaction::allocation_aliases(const void* output, std::size_t bytes) const noexcept {
    const owned_output_range range{output, bytes};
    return range.overlaps(this, sizeof(*this)) || range.overlaps(boxes_) || range.overlaps(carets_);
}

hinted_paragraph_interaction_result create_hinted_paragraph_interaction(
    std::shared_ptr<const hinted_paragraph_generation> paragraph) noexcept {
    hinted_paragraph_interaction_result result{};
    try {
        if (paragraph == nullptr || !exact_source_maps(*paragraph)) return result;
        const std::span<const positioned_text_glyph> glyphs{paragraph->glyphs};
        const std::span<const positioned_text_line> lines{paragraph->lines};
        const std::span<const std::int32_t> ends{paragraph->cluster_ends};
        const std::span<const std::int8_t> levels{paragraph->bidi_levels};
        const std::span<const float> origins{paragraph->line_origins};
        text_interaction_requirements required{};
        if (!interaction_detail::get_requirements(glyphs, lines, ends, levels, required, &result.error,
                true, std::span<const text_fragment_placement>{}, origins)) return result;
        auto candidate = std::shared_ptr<hinted_paragraph_interaction>(new hinted_paragraph_interaction{});
        candidate->paragraph_ = std::move(paragraph);
        candidate->boxes_.resize(required.cluster_box_capacity);
        candidate->carets_.resize(required.caret_stop_capacity);
        std::uint32_t boxes = 0U, carets = 0U;
        if (!interaction_detail::build(glyphs, lines, ends, levels,
                std::span<text_cluster_box>{candidate->boxes_}, std::span<text_caret_stop>{candidate->carets_},
                boxes, carets, &result.error, true, std::span<const text_fragment_placement>{}, origins)) return result;
        candidate->boxes_.resize(boxes);
        candidate->carets_.resize(carets);
        result.generation = std::move(candidate);
        result.error = font_error::none;
        result.status = PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        result.status = PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
        result.error = font_error::insufficient_buffer;
    } catch (...) {
        result.status = PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        result.error = font_error::invalid_argument;
    }
    return result;
}
} // namespace progpu::native::text

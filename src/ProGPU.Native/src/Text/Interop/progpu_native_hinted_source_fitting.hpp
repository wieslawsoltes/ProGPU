#pragma once

#include "progpu_native_hinted_paragraph_internal.hpp"

namespace progpu::native::text {
struct hinted_source_fitted_slice final {
    std::uint32_t run_index = 0U;
    std::shared_ptr<const hinted_positioned_slice> placement{};
};

// One width-specific native placement generation. Raw full-paragraph runs are
// independently retained by every replayed slice; the original logical array
// is not replaced. The common writer consumes these exact partitions/metrics.
struct hinted_source_fitting final {
    std::uint32_t first_logical_glyph = 0U;
    std::vector<shaping_glyph> fitting_glyphs{};
    std::vector<text_source_glyph_metrics> metrics{};
    std::vector<text_source_fitted_line> lines{}; // Relative to first_logical_glyph.
    std::vector<hinted_source_fitted_slice> slices{};
    std::vector<std::uint32_t> slice_indices{}, slice_glyph_indices{};
    bool allocation_aliases(const void* output, std::size_t bytes) const noexcept;
};

struct hinted_source_fitting_result final {
    progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    std::shared_ptr<const hinted_source_fitting> generation{};
};

// Native complete-paragraph fitting. Candidate placement uses original captured
// GPOS recipes only at proven original pre-positioning cluster boundaries. Work
// is bounded by 2^24 candidate glyph visits, including discarded candidates;
// exhaustion/unsupported recipes never become overflow success or fallback.
hinted_source_fitting_result fit_hinted_source_paragraph(
    const hinted_paragraph_generation& paragraph, std::uint32_t first_logical_glyph,
    double maximum_width) noexcept;

// Exact provenance checks for the frame reader; no replay, font work or repair.
bool validate_hinted_source_fitting(const hinted_paragraph_generation& paragraph,
    const hinted_source_fitting& fitting) noexcept;
} // namespace progpu::native::text

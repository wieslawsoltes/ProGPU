#pragma once

#include "progpu_native_hinted_gpos.hpp"

#include <cstddef>

namespace progpu::native::text {
// One original post-substitution descriptor per glyph. Initialization precedes
// mark zeroing, space fallback and positioning; never replace final advances.
// Whole-span failure atomicity and untouched tails; no shaping/font/GPU work.
hinted_gpos_frame_result initialize_hinted_run_metrics(const hinted_glyph_batch& batch,
    const sfnt_font_view& font, shaping_direction direction, std::span<shaping_glyph> glyphs,
    hinted_projection_policy policy = hinted_projection_policy::automatic,
    std::span<const std::int16_t> normalized_coordinates = {}) noexcept;

// The first source_descriptor_count descriptors belong to the ordered source
// run; trailing descriptors are auxiliary captures from the same generation.
// Initialize only that prefix while validating the entire output capacity for
// aliases. Failure and output tails remain untouched, including an empty prefix.
hinted_gpos_frame_result initialize_hinted_run_metrics(const hinted_glyph_batch& batch,
    const sfnt_font_view& font, shaping_direction direction, std::span<shaping_glyph> glyphs,
    std::size_t source_descriptor_count,
    hinted_projection_policy policy = hinted_projection_policy::automatic,
    std::span<const std::int16_t> normalized_coordinates = {}) noexcept;
} // namespace progpu::native::text

#pragma once

#include "progpu_native_hinted_gpos.hpp"

namespace progpu::native::text {
// One original post-substitution descriptor per glyph. Initialization precedes
// mark zeroing, space fallback and positioning; never replace final advances.
// Whole-span failure atomicity and untouched tails; no shaping/font/GPU work.
hinted_gpos_frame_result initialize_hinted_run_metrics(const hinted_glyph_batch& batch,
    const sfnt_font_view& font, shaping_direction direction, std::span<shaping_glyph> glyphs,
    hinted_projection_policy policy = hinted_projection_policy::automatic,
    std::span<const std::int16_t> normalized_coordinates = {}) noexcept;
} // namespace progpu::native::text

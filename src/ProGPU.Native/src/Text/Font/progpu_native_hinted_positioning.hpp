#pragma once

#include "progpu_native_hinted_font.hpp"
#include "progpu_native_text.hpp"

namespace progpu::native::text {
struct hinted_shaped_run;

// One actual post-GSUB/default-ignorable/space preparation boundary from the
// original run. No borrowed shaping options, plan, context or table survives.
// This first recipe admits horizontal single/pair placement, not contextual
// substitution, marks/cursive attachment or script stretching across a break.
struct hinted_positioning_recipe final {
    std::vector<shaping_glyph> prepared_glyphs{};
    std::vector<open_type_tag> requested_features{}, explicit_features{};
    std::vector<shaping_feature> feature_settings{};
    std::vector<std::int16_t> normalized_coordinates{};
    std::vector<std::uint16_t> lookups{};
    open_type_shape_run_options configuration{}; // All pointer/span fields empty.
    hinted_projection_policy projection = hinted_projection_policy::automatic;
    bool legacy_kerning = false;

    open_type_shape_run_options options() const noexcept;
    bool allocation_aliases(const void* output, std::size_t bytes) const noexcept;
};

struct hinted_positioned_slice final {
    std::shared_ptr<const hinted_shaped_run> original{};
    // Original prepared descriptor order, not a renamed or isolated source run.
    std::uint32_t prepared_start = 0U, prepared_count = 0U;
    // Same final visual order as the original shaped run; each occurrence keeps
    // its exact original glyph index and therefore font/descriptor/source owner.
    std::vector<shaping_glyph> glyphs{};
    std::vector<std::uint32_t> original_glyph_indices{};
};

// No font execution, GSUB, source slicing or context lookup. Replays the retained
// positioning program against original prepared glyphs/captured device metrics.
// Both edges must be distinct clusters and safe in PRE-positioning dependency
// metadata. Final raw unsafe flags are never cleared or substituted for proof.
// Failure leaves result unchanged. This is not yet fitting/provider admission.
bool try_recompose_hinted_positioning(std::shared_ptr<const hinted_shaped_run> original,
    std::uint32_t prepared_start, std::uint32_t prepared_count,
    std::shared_ptr<const hinted_positioned_slice>& result, font_error* error = nullptr) noexcept;

// Capture callback helper for the actual shaper boundary. Unsupported placement
// families return success with an empty recipe; malformed data still fails.
bool try_capture_hinted_positioning(const sfnt_font_view& font,
    const open_type_shape_run_options& options, std::span<const shaping_glyph> prepared,
    hinted_projection_policy projection, std::shared_ptr<const hinted_positioning_recipe>& result,
    font_error* error = nullptr) noexcept;
} // namespace progpu::native::text

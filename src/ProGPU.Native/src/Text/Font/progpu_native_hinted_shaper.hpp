#pragma once

#include "progpu_native_hinted_font.hpp"
#include "progpu_native_text.hpp"
#include "progpu_native.h"
#include "progpu_native_text_hinting.h"

namespace progpu::native::text {
struct hinted_shaped_run final {
    std::shared_ptr<const hinted_glyph_batch> batch{};
    // Exact admitted scalar records before this run's normalization/GSUB.
    // Source adapters may already have substituted scalars; this is not an
    // original pre-substitution source or inferred bidi/formatting metadata.
    std::vector<unicode_scalar> shaping_input{};
    std::vector<shaping_glyph> glyphs{};
    std::vector<std::uint32_t> descriptor_indices{};
    std::vector<std::int16_t> normalized_coordinates{};
    std::size_t source_descriptor_count = 0U;
    std::size_t figure_descriptor_start = 0U;
    std::size_t figure_descriptor_count = 0U;
    std::size_t punctuation_descriptor_start = 0U;
    std::size_t punctuation_descriptor_count = 0U;
    shaping_direction direction = shaping_direction::unspecified;
};

struct hinted_shape_error final {
    font_error shaping = font_error::none;
    hinted_font_error capture = hinted_font_error::none;
    hinted_projection_error projection = hinted_projection_error::none;
    // Actual bad_alloc in this owned adapter, never a glyph/scratch budget cap.
    bool resource_exhausted = false;
};

// Explicit opt-in, under the caller's existing context use lease. Capture
// follows substitution and precedes positioning. Only a complete owned run is
// published; no context, callback, scratch or plan pointer survives this call.
// The additive C/managed adapter retains this exact generation; it does not
// admit source Display or change the original shaping APIs and defaults.
bool try_shape_context_hinted(progpu_native_text_context* context,
    std::uint32_t font_index, const hinted_font_configuration& configuration,
    std::span<const unicode_scalar> input, const open_type_shape_run_options& options,
    std::shared_ptr<const hinted_shaped_run>& result, hinted_shape_error& error,
    hinted_projection_policy policy = hinted_projection_policy::automatic,
    const open_type_shape_plan* plan = nullptr) noexcept;

#if defined(PROGPU_NATIVE_FONT_HINTING)
// Private consumers retain this exact generation under their existing handle
// lease. No reshape, descriptor-ID search or source capability admission.
std::shared_ptr<const hinted_shaped_run> select_hinted_run_generation(
    const progpu_native_hinted_run* run) noexcept;
#endif
} // namespace progpu::native::text

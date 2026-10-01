#pragma once

#include "../Font/progpu_native_hinted_shaper.hpp"
#include "progpu_native_text_styles.h"
#include "progpu_native_text_flow.h"
#include "../progpu_native_text_layout_retained_internal.hpp"

namespace progpu::native::text {

// Explicit device size and conversion, not inferred from the source style scale.
// The borrowed axes are used synchronously and copied into the owned generation.
struct hinted_paragraph_style_configuration final {
    std::uint32_t font_index = 0U;
    float source_scale = 0.0F;
    hinted_font_configuration hinting{};
    float logical_units_per_physical_pixel = 0.0F;
};

struct hinted_paragraph_owned_style_configuration final {
    std::uint32_t font_index = 0U;
    float source_scale = 0.0F;
    std::uint32_t x_pixels_per_em_26_6 = 0U, y_pixels_per_em_26_6 = 0U;
    font_hint_policy policy = font_hint_policy::truetype_40;
    std::uint32_t x_phase_26_6 = 0U, y_phase_26_6 = 0U;
    float logical_units_per_physical_pixel = 0.0F;
    std::vector<std::int32_t> variation_coordinates_16_16{};
};

struct hinted_paragraph_glyph_owner final {
    std::uint32_t run_index = 0U;
    std::uint32_t run_glyph_index = 0U;
    std::uint32_t descriptor_index = 0U;
    bool operator==(const hinted_paragraph_glyph_owner&) const = default;
};

struct hinted_paragraph_run final {
    std::shared_ptr<const hinted_shaped_run> generation{};
    std::uint32_t scalar_start = 0U, scalar_count = 0U;
    std::uint32_t logical_start = 0U, logical_count = 0U;
    std::uint32_t font_index = 0U, style_index = 0U;
    std::int8_t bidi_level = 0;
    float source_scale = 0.0F;
    float logical_units_per_physical_pixel = 0.0F;
};

// Original paragraph producer metadata, retained at its actual production sites.
// No borrowed context, style, request, scratch or callback survives publication.
struct hinted_paragraph_generation final {
    hinted_paragraph_generation() = default;
    hinted_paragraph_generation(const hinted_paragraph_generation&) = delete;
    hinted_paragraph_generation& operator=(const hinted_paragraph_generation&) = delete;
    hinted_paragraph_generation(hinted_paragraph_generation&&) = delete;
    hinted_paragraph_generation& operator=(hinted_paragraph_generation&&) = delete;
    std::vector<progpu_native_text_scalar> source_input{}, pre_context{}, post_context{};
    std::vector<progpu_native_text_feature> features{};
    std::vector<std::int16_t> normalized_coordinates{};
    // shaping's pointers refer only to the owned vectors above.
    progpu_native_text_shape_request shaping{};
    progpu_native_text_layout_options layout{};
    // Admitted scalars after the original styled digit substitution, not source.
    std::vector<unicode_scalar> shaping_input{};
    bool source_digit_bidi = false;
    std::int8_t paragraph_level = 0;
    std::vector<unicode_bidi_level> scalar_levels{};
    std::vector<unicode_script_run> script_runs{};
    std::vector<unicode_grapheme_cluster> graphemes{};
    std::vector<font_fallback_run> fallback_runs{};
    std::vector<std::shared_ptr<const owned_font_source>> font_sources{};
    std::vector<progpu_native_text_style_run> styles{};
    std::vector<progpu_native_text_style_metrics> source_metrics{};
    std::vector<hinted_paragraph_owned_style_configuration> device_styles{};
    std::vector<hinted_paragraph_run> runs{};
    std::vector<shaping_glyph> logical_glyphs{}; // Physical 26.6, wire Y-down.
    std::vector<std::int8_t> logical_bidi_levels{};
    std::vector<std::uint32_t> logical_font_indices{};
    std::vector<float> logical_source_scales{}; // Original style identity only.
    std::vector<float> glyph_scales{}; // Sole writer conversion: units/pixel / 64.
    std::vector<hinted_paragraph_glyph_owner> logical_owners{};
    std::vector<unicode_line_break_class> line_break_classes{};
    std::vector<text_line_break_kind> scalar_breaks{}, breaks_after{};
    // Empty unless the original producer needed justification classification.
    std::vector<text_justification_class> justification_classes{};
    std::vector<text_item_metrics> item_metrics{};
    // Explicit original scalar-range coverage; actual producer BK/NL/CR/LF
    // boundaries and admitted CRLF pairing never relabel the source text.
    // This is not empty-hard-row caret metadata.
    std::vector<std::int32_t> logical_cluster_ends{};
    std::vector<positioned_text_glyph> glyphs{}; // Original logical glyph_index.
    std::vector<positioned_text_line> lines{}; // Original writer fields unchanged.
    std::vector<hinted_paragraph_glyph_owner> positioned_owners{};
    std::vector<std::int8_t> bidi_levels{}; // Actual writer L1/L2-used levels.
    std::vector<std::int32_t> cluster_ends{};
    std::vector<float> line_origins{}; // Literal writer pen + alignment, never ink X.
    std::vector<text_layout_line_frame> line_frames{}; // Actual writer double top and source ascent.
    text_layout_metrics metrics{};
    progpu_native_text_paragraph_result paragraph_result{};
};

// Pointer-level guard shared by publication and adversarial alias controls.
// Never bind/dereference an oversized diagnostic reference merely to reject an
// overlap; both complete output ranges must be valid and disjoint first.
bool hinted_paragraph_publication_disjoint(
    const std::shared_ptr<const hinted_paragraph_generation>* result,
    const progpu_native_text_paragraph_result* diagnostic) noexcept;

// Private horizontal opt-in over the original paragraph producer and measured
// writer. Nonempty input requires one explicit source metric/device config per
// style. Tabs, objects and synthetic trimming are rejected; exclusions, floats,
// collapse and continuation are not represented by this entrypoint. Alignment,
// justification, maximum lines and the original digit/bidi policies are retained.
// Result publication is atomic. Diagnostic may change only after safe alias
// preflight; all input and unused caller storage remains untouched. This does
// not admit source Display, empty hard-row carets, or a new public C ABI.
progpu_native_status try_layout_context_hinted_paragraph(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request& shaping,
    const progpu_native_text_layout_options& layout,
    std::span<const progpu_native_text_style_run> styles,
    std::span<const progpu_native_text_style_metrics> source_metrics,
    std::span<const hinted_paragraph_style_configuration> device_styles,
    std::shared_ptr<const hinted_paragraph_generation>& result,
    progpu_native_text_paragraph_result& diagnostic) noexcept;

struct hinted_paragraph_reflow_result final {
    progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    std::shared_ptr<const hinted_paragraph_generation> generation{};
};

// Reuses the ORIGINAL complete logical generation and shared run/font owners.
// Only the existing measured writer places a suffix at an exact shaped cluster
// boundary; no shaping, hinting, bidi resolution or context lookup is repeated.
// The new generation owns its positioning/interaction inputs independently.
// Nested continuation cannot move before the current view's first input.
hinted_paragraph_reflow_result reflow_hinted_paragraph(
    const hinted_paragraph_generation& paragraph, std::int32_t input_start,
    float maximum_width) noexcept;

} // namespace progpu::native::text

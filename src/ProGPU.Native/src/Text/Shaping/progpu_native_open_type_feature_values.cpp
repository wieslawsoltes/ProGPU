#include "progpu_native_open_type_feature_values_internal.hpp"

#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>
#include <span>
#include <utility>

// Direct C++20 execution of ProGPU-owned ShapingFeature values and half-open
// input ranges. Lookup ownership is resolved once per ranged lookup; the
// ordinary no-range shaping path remains on the bulk executor.

namespace progpu::native::text::feature_detail {

namespace {

bool tracks_fallback_mark_metadata(
    const open_type_shape_run_options& options) noexcept {
    const auto script = options.unicode_script.value == 0U
        ? options.script
        : options.unicode_script;
    return options.complex_script == open_type_complex_script::none &&
        script != open_type_tag::from_chars('t', 'h', 'a', 'i') &&
        script != open_type_tag::from_chars('l', 'a', 'o', ' ') &&
        script != open_type_tag::from_chars('m', 'y', 'm', 'r') &&
        script != open_type_tag::from_chars('q', 'a', 'a', 'g');
}

} // namespace

bool contains_feature(
    std::span<const open_type_tag> features,
    open_type_tag feature) noexcept {
    return std::find(features.begin(), features.end(), feature) !=
        features.end();
}

bool has_feature_settings(
    const open_type_shape_run_options& options,
    open_type_tag feature) noexcept {
    return std::any_of(
        options.feature_settings.begin(),
        options.feature_settings.end(),
        [feature](const shaping_feature& setting) {
            return setting.tag == feature;
        });
}

std::uint32_t get_feature_value(
    const open_type_shape_run_options& options,
    open_type_tag feature,
    std::int32_t cluster) noexcept {
    const bool has_settings = has_feature_settings(options, feature);
    std::uint32_t value = has_settings
        ? 0U
        : (contains_feature(options.requested_features, feature) ? 1U : 0U);
    const std::uint32_t input_index = cluster < 0
        ? 0U
        : static_cast<std::uint32_t>(cluster);
    for (const auto& setting : options.feature_settings) {
        if (setting.tag == feature && setting.applies_to(input_index)) {
            value = setting.value;
        }
    }
    return value;
}

bool is_feature_explicit_at(
    const open_type_shape_run_options& options,
    open_type_tag feature,
    std::int32_t cluster) noexcept {
    const bool explicit_tag = contains_feature(options.explicit_features, feature);
    bool has_settings = false;
    const std::uint32_t input_index = cluster < 0
        ? 0U
        : static_cast<std::uint32_t>(cluster);
    for (const auto& setting : options.feature_settings) {
        if (setting.tag != feature) {
            continue;
        }
        has_settings = true;
        if (setting.applies_to(input_index)) {
            return true;
        }
    }
    return explicit_tag && !has_settings;
}

bool is_global_feature(
    open_type_tag script,
    open_type_tag feature) noexcept {
    constexpr auto rand = open_type_tag::from_chars('r', 'a', 'n', 'd');
    constexpr std::array directional{
        open_type_tag::from_chars('l', 't', 'r', 'a'),
        open_type_tag::from_chars('l', 't', 'r', 'm'),
        open_type_tag::from_chars('r', 't', 'l', 'a'),
        open_type_tag::from_chars('r', 't', 'l', 'm'),
        open_type_tag::from_chars('v', 'e', 'r', 't'),
        open_type_tag::from_chars('v', 'r', 't', '2')};
    constexpr std::array hangul{
        open_type_tag::from_chars('l', 'j', 'm', 'o'),
        open_type_tag::from_chars('v', 'j', 'm', 'o'),
        open_type_tag::from_chars('t', 'j', 'm', 'o')};
    return feature == rand || contains_feature(directional, feature) ||
        (script == open_type_tag::from_chars('h', 'a', 'n', 'g') &&
            contains_feature(hangul, feature));
}

bool try_resolve_lookup_feature(
    const open_type_layout_table_view& layout,
    const open_type_shape_run_options& options,
    std::uint16_t lookup,
    lookup_feature_resolution& result,
    font_error* error) noexcept {
    result = {};
    if (!layout.try_required_feature_for_lookup(
            options.script,
            options.language,
            lookup,
            options.normalized_coordinates,
            result.feature,
            result.required,
            error)) {
        return false;
    }
    if (result.required) {
        return true;
    }
    for (const auto feature : options.requested_features) {
        bool contains = false;
        if (!layout.try_feature_contains_lookup(
                options.script,
                options.language,
                feature,
                lookup,
                options.normalized_coordinates,
                contains,
                error)) {
            return false;
        }
        if (!contains) {
            continue;
        }
        if (!result.found || !is_global_feature(options.script, result.feature) ||
            is_global_feature(options.script, feature)) {
            result.feature = feature;
            result.found = true;
        }
    }
    return true;
}

bool is_decimal_digit(std::uint32_t code_point) noexcept {
    return get_unicode_line_break_class(code_point) ==
        unicode_line_break_class::numeric;
}

bool has_fraction_actions(std::span<const unicode_scalar> input) noexcept {
    for (std::size_t slash = 1U; slash + 1U < input.size(); ++slash) {
        if (input[slash].code_point != 0x2044U ||
            !is_decimal_digit(input[slash - 1U].code_point) ||
            !is_decimal_digit(input[slash + 1U].code_point)) {
            continue;
        }
        return true;
    }
    return false;
}

std::span<const open_type_tag> inactive_fraction_features(
    const open_type_shape_run_options& options,
    std::array<open_type_tag, 3U>& storage) noexcept {
    constexpr std::array conditional{
        open_type_tag::from_chars('f', 'r', 'a', 'c'),
        open_type_tag::from_chars('n', 'u', 'm', 'r'),
        open_type_tag::from_chars('d', 'n', 'o', 'm')};
    std::size_t count = 0U;
    for (const auto feature : conditional) {
        if (!contains_feature(options.explicit_features, feature)) {
            storage[count++] = feature;
        }
    }
    return std::span<const open_type_tag>{storage}.first(count);
}

bool apply_fraction_lookup(
    const open_type_layout_table_view& gsub,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::uint16_t lookup,
    fraction_feature_kind kind,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    font_error* error) noexcept;

bool try_get_fraction_feature_kind(
    const open_type_layout_table_view& gsub,
    const open_type_shape_run_options& options,
    std::uint16_t lookup,
    fraction_feature_kind& result,
    font_error* error) noexcept {
    result = fraction_feature_kind::none;
    constexpr std::array features{
        std::pair{open_type_tag::from_chars('f', 'r', 'a', 'c'),
            fraction_feature_kind::fraction},
        std::pair{open_type_tag::from_chars('n', 'u', 'm', 'r'),
            fraction_feature_kind::numerator},
        std::pair{open_type_tag::from_chars('d', 'n', 'o', 'm'),
            fraction_feature_kind::denominator}};
    for (const auto& [feature, kind] : features) {
        if (!contains_feature(options.requested_features, feature)) {
            continue;
        }
        bool contains = false;
        if (!gsub.try_feature_contains_lookup(
                options.script,
                options.language,
                feature,
                lookup,
                options.normalized_coordinates,
                contains,
                error)) {
            return false;
        }
        if (contains) {
            result = kind;
        }
    }
    return true;
}

bool is_fraction_feature_enabled(
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::int32_t cluster,
    fraction_feature_kind kind) noexcept {
    if (cluster < 0 || kind == fraction_feature_kind::none) {
        return false;
    }
    const open_type_tag feature = kind == fraction_feature_kind::fraction
        ? open_type_tag::from_chars('f', 'r', 'a', 'c')
        : (kind == fraction_feature_kind::numerator
            ? open_type_tag::from_chars('n', 'u', 'm', 'r')
            : open_type_tag::from_chars('d', 'n', 'o', 'm'));
    if (get_feature_value(options, feature, cluster) == 0U) {
        return false;
    }
    if (is_feature_explicit_at(options, feature, cluster)) {
        return true;
    }
    for (std::size_t slash = 1U; slash + 1U < input.size(); ++slash) {
        if (input[slash].code_point != 0x2044U ||
            !is_decimal_digit(input[slash - 1U].code_point) ||
            !is_decimal_digit(input[slash + 1U].code_point)) {
            continue;
        }
        std::size_t numerator = slash;
        while (numerator > 0U &&
            is_decimal_digit(input[numerator - 1U].code_point)) {
            --numerator;
        }
        std::size_t denominator = slash + 1U;
        while (denominator < input.size() &&
            is_decimal_digit(input[denominator].code_point)) {
            ++denominator;
        }
        for (std::size_t index = numerator; index < denominator; ++index) {
            if (input[index].input_index != static_cast<std::uint32_t>(cluster)) {
                continue;
            }
            return kind == fraction_feature_kind::fraction ||
                (kind == fraction_feature_kind::numerator && index < slash) ||
                (kind == fraction_feature_kind::denominator && index > slash);
        }
    }
    return false;
}

bool apply_fraction_features(
    const open_type_layout_table_view& gsub,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    font_error* error) noexcept {
    constexpr std::array conditional{
        open_type_tag::from_chars('f', 'r', 'a', 'c'),
        open_type_tag::from_chars('n', 'u', 'm', 'r'),
        open_type_tag::from_chars('d', 'n', 'o', 'm')};
    const bool has_explicit_fraction = std::any_of(
        conditional.begin(),
        conditional.end(),
        [&options](open_type_tag feature) {
            return contains_feature(options.explicit_features, feature) ||
                has_feature_settings(options, feature);
        });
    if (!has_fraction_actions(input) && !has_explicit_fraction) {
        return true;
    }
    for (std::uint16_t lookup = 0U; lookup < gsub.lookup_count(); ++lookup) {
        fraction_feature_kind kind{};
        if (!try_get_fraction_feature_kind(
                gsub, options, lookup, kind, error)) {
            return false;
        }
        if (kind == fraction_feature_kind::none) {
            continue;
        }
        if (!apply_fraction_lookup(
                gsub,
                input,
                options,
                lookup,
                kind,
                glyph_storage,
                glyph_count,
                gdef,
                error)) {
            return false;
        }
    }
    return true;
}

bool apply_fraction_lookup(
    const open_type_layout_table_view& gsub,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::uint16_t lookup,
    fraction_feature_kind kind,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    font_error* error) noexcept {
    std::uint32_t position = 0U;
    while (position < glyph_count) {
        if (!is_fraction_feature_enabled(
                input, options, glyph_storage[position].cluster, kind)) {
            ++position;
            continue;
        }
        const std::uint32_t count_before = glyph_count;
        std::uint32_t context_match_end = 0U;
        bool applied = false;
        const open_type_tag feature = kind == fraction_feature_kind::fraction
            ? open_type_tag::from_chars('f', 'r', 'a', 'c')
            : (kind == fraction_feature_kind::numerator
                ? open_type_tag::from_chars('n', 'u', 'm', 'r')
                : open_type_tag::from_chars('d', 'n', 'o', 'm'));
        if (!try_apply_open_type_gsub_lookup_at(
                gsub,
                lookup,
                glyph_storage,
                glyph_count,
                position,
                open_type_gsub_apply_options{
                    gdef,
                    get_feature_value(
                        options, feature, glyph_storage[position].cluster),
                    0U,
                    false,
                    &context_match_end,
                    tracks_fallback_mark_metadata(options)},
                applied,
                error)) {
            return false;
        }
        if (glyph_count > count_before) {
            position += glyph_count - count_before;
        }
        position = std::max(position + 1U, context_match_end);
    }
    return true;
}

bool apply_gsub_lookup_with_feature_values(
    const open_type_layout_table_view& gsub,
    const open_type_shape_run_options& options,
    std::uint16_t lookup,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    font_error* error,
    std::uint32_t* random_state,
    const open_type_glyph_set_digest* lookup_digest,
    const open_type_coverage_view* lookup_coverage,
    std::span<const open_type_context_subtable_requirement>
        lookup_context_subtables,
    std::span<const open_type_context_coverage_requirement>
        lookup_context_coverages,
    const lookup_feature_resolution* cached_resolution,
    bool restrict_to_syllable) noexcept {
    if (options.feature_settings.empty()) {
        bool applied = false;
        open_type_gsub_apply_options apply_options{
            gdef,
            options.alternate_value,
            0U,
            false,
            nullptr,
            tracks_fallback_mark_metadata(options)};
        apply_options.lookup_digest = lookup_digest;
        apply_options.lookup_coverage = lookup_coverage;
        apply_options.lookup_context_subtables = lookup_context_subtables;
        apply_options.lookup_context_coverages = lookup_context_coverages;
        apply_options.restrict_to_syllable = restrict_to_syllable;
        return try_apply_open_type_gsub_lookup(
            gsub,
            lookup,
            glyph_storage,
            glyph_count,
            apply_options,
            applied,
            error);
    }
    lookup_feature_resolution resolution{};
    if (cached_resolution != nullptr) {
        resolution = *cached_resolution;
    } else if (!try_resolve_lookup_feature(
                   gsub, options, lookup, resolution, error)) {
        return false;
    }
    if (resolution.required || !resolution.found ||
        !has_feature_settings(options, resolution.feature)) {
        bool applied = false;
        open_type_gsub_apply_options apply_options{
            gdef,
            options.alternate_value,
            0U,
            false,
            nullptr,
            tracks_fallback_mark_metadata(options)};
        apply_options.lookup_digest = lookup_digest;
        apply_options.lookup_coverage = lookup_coverage;
        apply_options.lookup_context_subtables = lookup_context_subtables;
        apply_options.lookup_context_coverages = lookup_context_coverages;
        apply_options.restrict_to_syllable = restrict_to_syllable;
        return try_apply_open_type_gsub_lookup(
            gsub,
            lookup,
            glyph_storage,
            glyph_count,
            apply_options,
            applied,
            error);
    }

    open_type_lookup_view lookup_view{};
    if (!gsub.try_get_lookup(lookup, lookup_view, error)) {
        return false;
    }
    const bool reverse = lookup_view.type == 8U;
    std::uint32_t iteration = reverse ? glyph_count : 0U;
    while (reverse ? iteration != 0U : iteration < glyph_count) {
        const std::uint32_t position = reverse ? --iteration : iteration;
        if (lookup_digest != nullptr &&
            glyph_storage[position].glyph_id <= 0xFFFFU &&
            !lookup_digest->may_have(static_cast<std::uint16_t>(
                glyph_storage[position].glyph_id))) {
            if (!reverse) {
                ++iteration;
            }
            continue;
        }
        const std::uint32_t feature_value = get_feature_value(
            options, resolution.feature, glyph_storage[position].cluster);
        if (feature_value == 0U) {
            if (!reverse) {
                ++iteration;
            }
            continue;
        }
        const std::uint32_t count_before = glyph_count;
        std::uint32_t context_match_end = 0U;
        bool applied = false;
        open_type_gsub_apply_options apply_options{
            gdef,
            feature_value,
            0U,
            false,
            &context_match_end,
            tracks_fallback_mark_metadata(options)};
        apply_options.lookup_coverage = lookup_coverage;
        apply_options.lookup_context_subtables = lookup_context_subtables;
        apply_options.lookup_context_coverages = lookup_context_coverages;
        apply_options.random_state = random_state;
        apply_options.random_alternate =
            resolution.feature ==
                open_type_tag::from_chars('r', 'a', 'n', 'd') &&
            feature_value == std::numeric_limits<std::uint16_t>::max();
        apply_options.restrict_to_syllable = restrict_to_syllable;
        if (!try_apply_open_type_gsub_lookup_at(
                gsub,
                lookup,
                glyph_storage,
                glyph_count,
                position,
                apply_options,
                applied,
                error)) {
            return false;
        }
        if (!reverse) {
            if (glyph_count > count_before) {
                iteration += glyph_count - count_before;
            }
            iteration = std::max(iteration + 1U, context_match_end);
        }
    }
    return true;
}

bool apply_gpos_lookup_with_feature_values(
    const open_type_layout_table_view& gpos,
    const open_type_shape_run_options& options,
    std::uint16_t lookup,
    std::span<shaping_glyph> glyphs,
    const open_type_gpos_apply_options& apply_options,
    font_error* error,
    const lookup_feature_resolution* cached_resolution) noexcept {
    if (options.feature_settings.empty()) {
        bool applied = false;
        return try_apply_open_type_gpos_lookup(
            gpos, lookup, glyphs, apply_options, applied, error);
    }
    lookup_feature_resolution resolution{};
    if (cached_resolution != nullptr) {
        resolution = *cached_resolution;
    } else if (!try_resolve_lookup_feature(
                   gpos, options, lookup, resolution, error)) {
        return false;
    }
    if (resolution.required || !resolution.found ||
        !has_feature_settings(options, resolution.feature)) {
        bool applied = false;
        return try_apply_open_type_gpos_lookup(
            gpos, lookup, glyphs, apply_options, applied, error);
    }
    for (std::uint32_t position = 0U; position < glyphs.size(); ++position) {
        if (apply_options.lookup_digest != nullptr &&
            glyphs[position].glyph_id <= 0xFFFFU &&
            !apply_options.lookup_digest->may_have(
                static_cast<std::uint16_t>(glyphs[position].glyph_id))) {
            continue;
        }
        if (get_feature_value(
                options, resolution.feature, glyphs[position].cluster) == 0U) {
            continue;
        }
        bool applied = false;
        if (!try_apply_open_type_gpos_lookup_at(
                gpos,
                lookup,
                glyphs,
                position,
                apply_options,
                applied,
                error)) {
            return false;
        }
    }
    return true;
}

} // namespace progpu::native::text::feature_detail

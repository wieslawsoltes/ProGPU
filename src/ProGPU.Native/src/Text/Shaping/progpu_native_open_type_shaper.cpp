#include "progpu_native_text.hpp"

#include "progpu_native_open_type_complex_internal.hpp"
#include "progpu_native_open_type_feature_values_internal.hpp"
#include "progpu_native_initial_mapping_internal.hpp"
#include "progpu_native_use_diacritics_internal.hpp"
#include "progpu_native_open_type_gsub_internal.hpp"
#include "progpu_native_legacy_kern_internal.hpp"
#include "progpu_native_fallback_marks_internal.hpp"
#include "progpu_native_arabic_stretch_internal.hpp"
#include "progpu_native_arabic_actions_internal.hpp"
#include "progpu_native_arabic_fallback_internal.hpp"
#include "progpu_native_space_fallback_internal.hpp"
#include "progpu_native_vowel_constraints_internal.hpp"
#include "progpu_native_shaping_options_internal.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>
#include <utility>

// Native uniform-run orchestration ported from the stage boundaries in
// ProGPU-owned CpuOpenTypeShaper.cs/OpenTypeTextShaper.cs at checkpoint
// 3b9ade5f. Script-specific state machines remain separate shaping slices.

namespace progpu::native::text {
namespace {

using feature_detail::apply_fraction_features;
using feature_detail::apply_fraction_lookup;
using feature_detail::apply_gpos_lookup_with_feature_values;
using feature_detail::apply_gsub_lookup_with_feature_values;
using feature_detail::fraction_feature_kind;
using feature_detail::get_feature_value;
using feature_detail::has_feature_settings;
using feature_detail::inactive_fraction_features;
using feature_detail::lookup_feature_resolution;
using feature_detail::try_resolve_lookup_feature;
using detail::clear_arabic_actions;
using detail::get_arabic_action;
using detail::set_arabic_action;

constexpr open_type_tag gdef_tag =
    open_type_tag::from_chars('G', 'D', 'E', 'F');
constexpr open_type_tag gsub_tag =
    open_type_tag::from_chars('G', 'S', 'U', 'B');
constexpr open_type_tag gpos_tag =
    open_type_tag::from_chars('G', 'P', 'O', 'S');
constexpr std::uint32_t hangul_feature_mask = 0x30000000U;
constexpr std::uint32_t hangul_feature_shift = 28U;
constexpr auto kern_feature =
    open_type_tag::from_chars('k', 'e', 'r', 'n');
constexpr auto distance_feature =
    open_type_tag::from_chars('d', 'i', 's', 't');
constexpr auto stretch_feature =
    open_type_tag::from_chars('s', 't', 'c', 'h');
constexpr auto arabic_script =
    open_type_tag::from_chars('a', 'r', 'a', 'b');
constexpr std::array arabic_form_features{
    std::pair{open_type_tag::from_chars('i', 's', 'o', 'l'),
        open_type_arabic_action::isolated},
    std::pair{open_type_tag::from_chars('f', 'i', 'n', 'a'),
        open_type_arabic_action::final},
    std::pair{open_type_tag::from_chars('f', 'i', 'n', '2'),
        open_type_arabic_action::final2},
    std::pair{open_type_tag::from_chars('f', 'i', 'n', '3'),
        open_type_arabic_action::final3},
    std::pair{open_type_tag::from_chars('m', 'e', 'd', 'i'),
        open_type_arabic_action::medial},
    std::pair{open_type_tag::from_chars('m', 'e', 'd', '2'),
        open_type_arabic_action::medial2},
    std::pair{open_type_tag::from_chars('i', 'n', 'i', 't'),
        open_type_arabic_action::initial}};

bool is_default_ignorable(std::uint32_t value) noexcept {
    return value == 0x00ADU || value == 0x034FU || value == 0x061CU ||
        value == 0x115FU || value == 0x1160U || value == 0x17B4U ||
        value == 0x17B5U || (value >= 0x180BU && value <= 0x180FU) ||
        (value >= 0x200BU && value <= 0x200FU) ||
        (value >= 0x202AU && value <= 0x202EU) ||
        (value >= 0x2060U && value <= 0x206FU) || value == 0x3164U ||
        value == 0xFEFFU || value == 0xFFA0U ||
        (value >= 0xFFF0U && value <= 0xFFF8U) ||
        (value >= 0xFE00U && value <= 0xFE0FU) ||
        (value >= 0x1BCA0U && value <= 0x1BCAFU) ||
        (value >= 0x1D173U && value <= 0x1D17AU) ||
        (value >= 0xE0000U && value <= 0xE0FFFU);
}

bool is_unicode_mark(std::uint32_t code_point) noexcept {
    const auto grapheme = get_unicode_grapheme_break_class(code_point);
    return get_unicode_bidi_class(code_point) ==
            unicode_bidi_class::nonspacing_mark ||
        grapheme == unicode_grapheme_break_class::spacing_mark ||
        get_unicode_canonical_combining_class(code_point) != 0U;
}

bool is_positioning_mark(
    const shaping_glyph& glyph,
    const open_type_gdef_view* gdef) noexcept {
    if (gdef != nullptr && glyph.glyph_id <= 0xFFFFU) {
        const auto glyph_class = gdef->glyph_class(
            static_cast<std::uint16_t>(glyph.glyph_id));
        if (glyph_class != open_type_glyph_class::unclassified) {
            return glyph_class == open_type_glyph_class::mark;
        }
    }
    return !is_default_ignorable(glyph.code_point) &&
        is_unicode_mark(glyph.code_point);
}

open_type_tag effective_unicode_script(
    const open_type_shape_run_options& options) noexcept {
    return options.unicode_script.value == 0U
        ? options.script
        : options.unicode_script;
}

bool uses_fallback_mark_positioning(
    const open_type_shape_run_options& options) noexcept {
    if (options.complex_script != open_type_complex_script::none) return false;
    const auto script = effective_unicode_script(options);
    return script != open_type_tag::from_chars('t', 'h', 'a', 'i') &&
        script != open_type_tag::from_chars('l', 'a', 'o', ' ') &&
        script != open_type_tag::from_chars('m', 'y', 'm', 'r') &&
        script != open_type_tag::from_chars('q', 'a', 'a', 'g');
}

std::int32_t clamp_i16(std::int64_t value) noexcept {
    return static_cast<std::int32_t>(std::clamp<std::int64_t>(
        value,
        std::numeric_limits<std::int16_t>::min(),
        std::numeric_limits<std::int16_t>::max()));
}

std::int64_t round_to_even(float value) noexcept {
    const auto lower = std::floor(value);
    const auto fraction = value - lower;
    if (fraction < 0.5F) return static_cast<std::int64_t>(lower);
    if (fraction > 0.5F) return static_cast<std::int64_t>(lower + 1.0F);
    return static_cast<std::int64_t>(
        std::fmod(lower, 2.0F) == 0.0F ? lower : lower + 1.0F);
}

bool is_run_feature_enabled(
    const open_type_shape_run_options& options,
    open_type_tag tag) noexcept {
    bool enabled = std::find(
        options.requested_features.begin(),
        options.requested_features.end(),
        tag) != options.requested_features.end();
    for (const auto& setting : options.feature_settings) {
        if (setting.tag == tag && setting.start == 0U &&
            setting.end == 0xFFFFFFFFU) {
            enabled = setting.value != 0U;
        }
    }
    return enabled;
}

enum class hangul_feature : std::uint32_t {
    none = 0U,
    leading = 1U,
    vowel = 2U,
    trailing = 3U
};

bool uses_arabic_joining(open_type_tag script) noexcept {
    constexpr std::array scripts{
        open_type_tag::from_chars('a', 'd', 'l', 'm'),
        open_type_tag::from_chars('a', 'r', 'a', 'b'),
        open_type_tag::from_chars('c', 'h', 'r', 's'),
        open_type_tag::from_chars('r', 'o', 'h', 'g'),
        open_type_tag::from_chars('m', 'a', 'n', 'd'),
        open_type_tag::from_chars('m', 'a', 'n', 'i'),
        open_type_tag::from_chars('m', 'o', 'n', 'g'),
        open_type_tag::from_chars('n', 'k', 'o', 'o'),
        open_type_tag::from_chars('o', 'u', 'g', 'r'),
        open_type_tag::from_chars('p', 'h', 'a', 'g'),
        open_type_tag::from_chars('p', 'h', 'l', 'p'),
        open_type_tag::from_chars('s', 'o', 'g', 'd'),
        open_type_tag::from_chars('s', 'y', 'r', 'c')};
    return std::find(scripts.begin(), scripts.end(), script) != scripts.end();
}

bool uses_hangul(open_type_tag script) noexcept {
    return script == open_type_tag::from_chars('h', 'a', 'n', 'g');
}

bool may_expand_preprocessing(
    open_type_tag script,
    shaping_buffer_flags flags) noexcept {
    const bool beginning =
        (static_cast<std::uint8_t>(flags) &
            static_cast<std::uint8_t>(
                shaping_buffer_flags::beginning_of_text)) != 0U;
    return beginning || uses_hangul(script) ||
        script == open_type_tag::from_chars('t', 'h', 'a', 'i') ||
        script == open_type_tag::from_chars('l', 'a', 'o', ' ') ||
        detail::has_vowel_constraints(script);
}

bool is_variation_selector(std::uint32_t code_point) noexcept {
    return (code_point >= 0xFE00U && code_point <= 0xFE0FU) ||
        (code_point >= 0xE0100U && code_point <= 0xE01EFU);
}

bool is_printable_ascii(std::span<const unicode_scalar> input) noexcept {
    return std::all_of(input.begin(), input.end(), [](const auto& scalar) {
        return scalar.code_point >= 0x20U && scalar.code_point <= 0x7EU;
    });
}

void write_ascii_graphemes(
    std::span<const unicode_scalar> input,
    std::span<unicode_grapheme_cluster> output) noexcept {
    for (std::size_t index = 0U; index < input.size(); ++index) {
        output[index] = unicode_grapheme_cluster{
            input[index].input_index,
            input[index].input_length,
            static_cast<std::uint32_t>(index),
            1U};
    }
}

hangul_feature get_hangul_feature(const shaping_glyph& glyph) noexcept {
    return static_cast<hangul_feature>(
        (static_cast<std::uint32_t>(glyph.flags) & hangul_feature_mask) >>
        hangul_feature_shift);
}

void clear_hangul_features(std::span<shaping_glyph> glyphs) noexcept {
    for (auto& glyph : glyphs) {
        glyph.flags = static_cast<shaping_glyph_flags>(
            static_cast<std::uint32_t>(glyph.flags) & ~hangul_feature_mask);
    }
}

struct complex_metadata_guard final {
    std::span<shaping_glyph> storage{};
    const std::uint32_t* count = nullptr;
    bool enabled = false;

    ~complex_metadata_guard() {
        if (enabled && count != nullptr) {
            complex_detail::clear_metadata(storage.first(
                std::min<std::size_t>(*count, storage.size())));
        }
    }
};

bool apply_arabic_form_feature(
    const open_type_layout_table_view& gsub,
    open_type_tag script,
    open_type_tag language,
    open_type_tag feature,
    open_type_arabic_action action,
    std::span<std::uint16_t> lookup_scratch,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_shape_run_options& run_options,
    const open_type_gsub_apply_options& apply_options,
    font_error* error) noexcept {
    std::uint32_t lookup_count = 0U;
    if (!gsub.try_select_feature_lookups(
            script,
            language,
            feature,
            lookup_scratch,
            lookup_count,
            error)) {
        return false;
    }
    for (std::uint32_t lookup = 0U; lookup < lookup_count; ++lookup) {
        std::uint32_t position = 0U;
        while (position < glyph_count) {
            const std::uint32_t value = get_feature_value(
                run_options, feature, glyph_storage[position].cluster);
            if (value == 0U ||
                get_arabic_action(glyph_storage[position]) != action) {
                ++position;
                continue;
            }
            const std::uint32_t count_before = glyph_count;
            bool applied = false;
            auto targeted_options = apply_options;
            targeted_options.alternate_value = value;
            if (!try_apply_open_type_gsub_lookup_at(
                    gsub,
                    lookup_scratch[lookup],
                    glyph_storage,
                    glyph_count,
                    position,
                    targeted_options,
                    applied,
                    error)) {
                return false;
            }
            position += 1U + (glyph_count > count_before
                ? glyph_count - count_before
                : 0U);
        }
    }
    return true;
}

bool apply_arabic_stretch_feature(
    const open_type_layout_table_view& gsub,
    open_type_tag script,
    open_type_tag language,
    std::span<std::uint16_t> lookup_scratch,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_shape_run_options& run_options,
    const open_type_gdef_view* gdef,
    bool track_fallback_marks,
    font_error* error) noexcept {
    if (!is_run_feature_enabled(run_options, stretch_feature)) return true;
    std::uint32_t lookup_count = 0U;
    if (!gsub.try_select_feature_lookups(
            script,
            language,
            stretch_feature,
            lookup_scratch,
            lookup_count,
            error)) {
        return false;
    }
    for (std::uint32_t lookup = 0U; lookup < lookup_count; ++lookup) {
        std::uint32_t position = 0U;
        while (position < glyph_count) {
            const std::uint32_t value = get_feature_value(
                run_options,
                stretch_feature,
                glyph_storage[position].cluster);
            if (value == 0U) {
                ++position;
                continue;
            }
            const std::uint32_t count_before = glyph_count;
            std::uint32_t context_match_end = 0U;
            bool applied = false;
            if (!try_apply_open_type_gsub_lookup_at(
                    gsub,
                    lookup_scratch[lookup],
                    glyph_storage,
                    glyph_count,
                    position,
                    open_type_gsub_apply_options{
                        gdef,
                        value,
                        0U,
                        false,
                        &context_match_end,
                        track_fallback_marks,
                        true},
                    applied,
                    error)) {
                return false;
            }
            if (glyph_count > count_before) {
                position += glyph_count - count_before;
            }
            position = std::max(position + 1U, context_match_end);
        }
    }
    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        if (detail::is_arabic_stretch_multiplied(glyph_storage[index])) {
            set_arabic_action(
                glyph_storage[index],
                (detail::arabic_stretch_component(glyph_storage[index]) & 1U)
                    != 0U
                    ? open_type_arabic_action::stretch_repeating
                    : open_type_arabic_action::stretch_fixed);
        }
        detail::clear_arabic_stretch_metadata(glyph_storage[index]);
    }
    return true;
}

bool apply_hangul_feature(
    const open_type_layout_table_view& gsub,
    open_type_tag script,
    open_type_tag language,
    open_type_tag feature,
    hangul_feature required_feature,
    std::span<std::uint16_t> lookup_scratch,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_shape_run_options& run_options,
    const open_type_gsub_apply_options& apply_options,
    font_error* error) noexcept {
    std::uint32_t lookup_count = 0U;
    if (!gsub.try_select_feature_lookups(
            script,
            language,
            feature,
            lookup_scratch,
            lookup_count,
            error)) {
        return false;
    }
    for (std::uint32_t lookup = 0U; lookup < lookup_count; ++lookup) {
        std::uint32_t position = 0U;
        while (position < glyph_count) {
            const std::uint32_t value = get_feature_value(
                run_options, feature, glyph_storage[position].cluster);
            if (value == 0U ||
                get_hangul_feature(glyph_storage[position]) !=
                required_feature) {
                ++position;
                continue;
            }
            const std::uint32_t count_before = glyph_count;
            bool applied = false;
            auto targeted_options = apply_options;
            targeted_options.alternate_value = value;
            if (!try_apply_open_type_gsub_lookup_at(
                    gsub,
                    lookup_scratch[lookup],
                    glyph_storage,
                    glyph_count,
                    position,
                    targeted_options,
                    applied,
                    error)) {
                return false;
            }
            position += 1U + (glyph_count > count_before
                ? glyph_count - count_before
                : 0U);
        }
    }
    return true;
}

bool contains_feature(
    std::span<const open_type_tag> features,
    open_type_tag feature) noexcept {
    return std::find(features.begin(), features.end(), feature) !=
        features.end();
}


bool apply_complex_feature(
    const open_type_layout_table_view& gsub,
    const open_type_shape_run_options& run_options,
    open_type_tag feature,
    std::uint32_t required_private_mask,
    std::span<std::uint16_t> lookup_scratch,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    font_error* error) noexcept {
    if (!contains_feature(run_options.requested_features, feature)) {
        return true;
    }
    std::uint32_t lookup_count = 0U;
    if (!gsub.try_select_feature_lookups(
            run_options.script,
            run_options.language,
            feature,
            lookup_scratch,
            lookup_count,
            error)) {
        return false;
    }
    const open_type_gsub_apply_options apply_options{
        gdef,
        run_options.alternate_value,
        required_private_mask == 0U
            ? 0U
            : required_private_mask << complex_detail::feature_shift,
        true};
    for (std::uint32_t index = 0U; index < lookup_count; ++index) {
        if (!has_feature_settings(run_options, feature)) {
            bool applied = false;
            if (!try_apply_open_type_gsub_lookup(
                    gsub,
                    lookup_scratch[index],
                    glyph_storage,
                    glyph_count,
                    apply_options,
                    applied,
                    error)) {
                return false;
            }
            continue;
        }
        open_type_lookup_view lookup{};
        if (!gsub.try_get_lookup(lookup_scratch[index], lookup, error)) {
            return false;
        }
        const bool reverse = lookup.type == 8U;
        std::uint32_t iteration = reverse ? glyph_count : 0U;
        while (reverse ? iteration != 0U : iteration < glyph_count) {
            const std::uint32_t position = reverse ? --iteration : iteration;
            const std::uint32_t value = get_feature_value(
                run_options, feature, glyph_storage[position].cluster);
            if (value == 0U) {
                if (!reverse) {
                    ++iteration;
                }
                continue;
            }
            const std::uint32_t count_before = glyph_count;
            std::uint32_t context_match_end = 0U;
            bool applied = false;
            auto targeted_options = apply_options;
            targeted_options.alternate_value = value;
            targeted_options.context_match_end = &context_match_end;
            if (!try_apply_open_type_gsub_lookup_at(
                    gsub,
                    lookup_scratch[index],
                    glyph_storage,
                    glyph_count,
                    position,
                    targeted_options,
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
    }
    return true;
}

template<std::size_t N>
bool apply_complex_feature_group(
    const open_type_layout_table_view& gsub,
    const open_type_shape_run_options& options,
    const std::array<open_type_tag, N>& features,
    std::span<std::uint16_t> lookup_scratch,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    font_error* error) noexcept {
    for (const auto feature : features) {
        std::uint32_t mask = 0U;
        if (options.complex_script == open_type_complex_script::khmer) {
            if (feature == open_type_tag::from_chars('p', 'r', 'e', 'f')) {
                mask = 1U;
            } else if (feature == open_type_tag::from_chars('b', 'l', 'w', 'f') ||
                feature == open_type_tag::from_chars('a', 'b', 'v', 'f') ||
                feature == open_type_tag::from_chars('p', 's', 't', 'f')) {
                mask = 2U;
            } else if (feature == open_type_tag::from_chars('c', 'f', 'a', 'r')) {
                mask = 4U;
            }
        } else if (options.complex_script == open_type_complex_script::indic) {
            if (feature == open_type_tag::from_chars('r', 'p', 'h', 'f')) mask = 1U;
            else if (feature == open_type_tag::from_chars('p', 'r', 'e', 'f')) mask = 2U;
            else if (feature == open_type_tag::from_chars('b', 'l', 'w', 'f')) mask = 4U;
            else if (feature == open_type_tag::from_chars('a', 'b', 'v', 'f')) mask = 8U;
            else if (feature == open_type_tag::from_chars('h', 'a', 'l', 'f')) mask = 16U;
            else if (feature == open_type_tag::from_chars('p', 's', 't', 'f')) mask = 32U;
            else if (feature == open_type_tag::from_chars('i', 'n', 'i', 't')) mask = 64U;
        } else if (options.complex_script == open_type_complex_script::use &&
            feature == open_type_tag::from_chars('r', 'p', 'h', 'f')) {
            mask = 1U;
        }
        if (!apply_complex_feature(
                gsub,
                options,
                feature,
                mask,
                lookup_scratch,
                glyph_storage,
                glyph_count,
                gdef,
                error)) {
            return false;
        }
    }
    return true;
}

bool apply_complex_script_features(
    const sfnt_font_view& font,
    const open_type_layout_table_view& gsub,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::span<std::uint16_t> lookup_scratch,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gdef_view* gdef,
    font_error* error) noexcept {
    std::uint32_t required_count = 0U;
    if (!gsub.try_select_lookups(
            options.script,
            options.language,
            {},
            lookup_scratch,
            required_count,
            error)) {
        return false;
    }
    for (std::uint32_t index = 0U; index < required_count; ++index) {
        bool applied = false;
        if (!try_apply_open_type_gsub_lookup(
                gsub,
                lookup_scratch[index],
                glyph_storage,
                glyph_count,
                open_type_gsub_apply_options{
                    gdef, options.alternate_value},
                applied,
                error)) {
            return false;
        }
    }

    constexpr std::array directional{
        open_type_tag::from_chars('l', 't', 'r', 'a'),
        open_type_tag::from_chars('l', 't', 'r', 'm'),
        open_type_tag::from_chars('r', 't', 'l', 'a'),
        open_type_tag::from_chars('r', 't', 'l', 'm')};
    constexpr std::array preprocessing{
        open_type_tag::from_chars('r', 'v', 'r', 'n'),
        open_type_tag::from_chars('f', 'r', 'a', 'c'),
        open_type_tag::from_chars('n', 'u', 'm', 'r'),
        open_type_tag::from_chars('d', 'n', 'o', 'm'),
        open_type_tag::from_chars('l', 'o', 'c', 'l'),
        open_type_tag::from_chars('c', 'c', 'm', 'p')};
    if (!apply_complex_feature_group(
            gsub, options, directional, lookup_scratch, glyph_storage,
            glyph_count, gdef, error)) {
        return false;
    }
    for (std::uint16_t lookup = 0U; lookup < gsub.lookup_count(); ++lookup) {
        lookup_feature_resolution resolution{};
        if (!try_resolve_lookup_feature(
                gsub, options, lookup, resolution, error)) {
            return false;
        }
        if (resolution.required || !resolution.found ||
            !contains_feature(preprocessing, resolution.feature)) {
            continue;
        }
        fraction_feature_kind fraction_kind = fraction_feature_kind::none;
        if (resolution.feature ==
            open_type_tag::from_chars('f', 'r', 'a', 'c')) {
            fraction_kind = fraction_feature_kind::fraction;
        } else if (resolution.feature ==
            open_type_tag::from_chars('n', 'u', 'm', 'r')) {
            fraction_kind = fraction_feature_kind::numerator;
        } else if (resolution.feature ==
            open_type_tag::from_chars('d', 'n', 'o', 'm')) {
            fraction_kind = fraction_feature_kind::denominator;
        }
        if (fraction_kind != fraction_feature_kind::none) {
            if (!apply_fraction_lookup(
                    gsub,
                    input,
                    options,
                    lookup,
                    fraction_kind,
                    glyph_storage,
                    glyph_count,
                    gdef,
                    error)) {
                return false;
            }
        } else if (!apply_gsub_lookup_with_feature_values(
                gsub,
                options,
                lookup,
                glyph_storage,
                glyph_count,
                gdef,
                error)) {
            return false;
        }
    }

    constexpr std::array khmer_basic{
        open_type_tag::from_chars('p', 'r', 'e', 'f'),
        open_type_tag::from_chars('b', 'l', 'w', 'f'),
        open_type_tag::from_chars('a', 'b', 'v', 'f'),
        open_type_tag::from_chars('p', 's', 't', 'f'),
        open_type_tag::from_chars('c', 'f', 'a', 'r')};
    constexpr std::array myanmar_basic{
        open_type_tag::from_chars('r', 'p', 'h', 'f'),
        open_type_tag::from_chars('p', 'r', 'e', 'f'),
        open_type_tag::from_chars('b', 'l', 'w', 'f'),
        open_type_tag::from_chars('p', 's', 't', 'f')};
    constexpr std::array indic_basic{
        open_type_tag::from_chars('n', 'u', 'k', 't'),
        open_type_tag::from_chars('a', 'k', 'h', 'n'),
        open_type_tag::from_chars('r', 'p', 'h', 'f'),
        open_type_tag::from_chars('r', 'k', 'r', 'f'),
        open_type_tag::from_chars('p', 'r', 'e', 'f'),
        open_type_tag::from_chars('b', 'l', 'w', 'f'),
        open_type_tag::from_chars('a', 'b', 'v', 'f'),
        open_type_tag::from_chars('h', 'a', 'l', 'f'),
        open_type_tag::from_chars('p', 's', 't', 'f'),
        open_type_tag::from_chars('v', 'a', 't', 'u'),
        open_type_tag::from_chars('c', 'j', 'c', 't')};
    constexpr std::array use_repha{
        open_type_tag::from_chars('r', 'p', 'h', 'f')};
    constexpr std::array use_prebase{
        open_type_tag::from_chars('p', 'r', 'e', 'f')};
    constexpr std::array use_basic{
        open_type_tag::from_chars('n', 'u', 'k', 't'),
        open_type_tag::from_chars('a', 'k', 'h', 'n'),
        open_type_tag::from_chars('r', 'k', 'r', 'f'),
        open_type_tag::from_chars('a', 'b', 'v', 'f'),
        open_type_tag::from_chars('b', 'l', 'w', 'f'),
        open_type_tag::from_chars('h', 'a', 'l', 'f'),
        open_type_tag::from_chars('p', 's', 't', 'f'),
        open_type_tag::from_chars('v', 'a', 't', 'u'),
        open_type_tag::from_chars('c', 'j', 'c', 't'),
        open_type_tag::from_chars('i', 's', 'o', 'l'),
        open_type_tag::from_chars('i', 'n', 'i', 't'),
        open_type_tag::from_chars('m', 'e', 'd', 'i'),
        open_type_tag::from_chars('f', 'i', 'n', 'a')};
    bool known_applied = true;
    if (options.complex_script == open_type_complex_script::khmer) {
        known_applied = apply_complex_feature_group(
            gsub, options, khmer_basic, lookup_scratch, glyph_storage,
            glyph_count, gdef, error);
    } else if (options.complex_script == open_type_complex_script::myanmar) {
        known_applied = complex_detail::try_reorder_myanmar(
            font,
            options.buffer_flags,
            glyph_storage,
            glyph_count,
            error);
        if (known_applied) {
            known_applied = apply_complex_feature_group(
                gsub, options, myanmar_basic, lookup_scratch, glyph_storage,
                glyph_count, gdef, error);
        }
    } else if (options.complex_script == open_type_complex_script::indic) {
        known_applied = complex_detail::try_initial_reorder_indic(
            font,
            effective_unicode_script(options),
            options.buffer_flags,
            glyph_storage,
            glyph_count,
            error);
        if (known_applied) {
            known_applied = apply_complex_feature_group(
                gsub, options, indic_basic, lookup_scratch, glyph_storage,
                glyph_count, gdef, error);
        }
        if (known_applied) {
            complex_detail::final_reorder_indic(
                effective_unicode_script(options),
                glyph_storage.first(glyph_count));
        }
    } else {
        complex_detail::clear_substituted(
            glyph_storage.first(glyph_count));
        known_applied = apply_complex_feature_group(
            gsub, options, use_repha, lookup_scratch, glyph_storage,
            glyph_count, gdef, error);
        if (known_applied) {
            complex_detail::record_use_repha(
                glyph_storage.first(glyph_count));
            complex_detail::clear_substituted(
                glyph_storage.first(glyph_count));
            known_applied = apply_complex_feature_group(
                gsub, options, use_prebase, lookup_scratch, glyph_storage,
                glyph_count, gdef, error);
        }
        if (known_applied) {
            complex_detail::record_use_prebase(
                glyph_storage.first(glyph_count));
            known_applied = apply_complex_feature_group(
                gsub, options, use_basic, lookup_scratch, glyph_storage,
                glyph_count, gdef, error);
        }
        if (known_applied) {
            known_applied = complex_detail::try_reorder_use(
                font,
                options.buffer_flags,
                glyph_storage,
                glyph_count,
                error);
        }
    }
    if (!known_applied) {
        return false;
    }

    for (const auto feature : options.requested_features) {
            if (contains_feature(directional, feature) ||
            contains_feature(preprocessing, feature) ||
            contains_feature(khmer_basic, feature) ||
            contains_feature(myanmar_basic, feature) ||
            contains_feature(indic_basic, feature) ||
            contains_feature(use_repha, feature) ||
            contains_feature(use_prebase, feature) ||
            contains_feature(use_basic, feature)) {
            continue;
        }
        if (!apply_complex_feature(
                gsub, options, feature, 0U, lookup_scratch, glyph_storage,
                glyph_count, gdef, error)) {
            return false;
        }
    }
    return true;
}

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool try_get_layout(
    const sfnt_font_view& font,
    open_type_tag tag,
    open_type_layout_table_view& result,
    std::size_t& length,
    font_error* error) noexcept {
    result = {};
    length = 0U;
    sfnt_table_view table{};
    if (!font.try_get_table(tag, table)) {
        return true;
    }
    length = table.bytes.size();
    return open_type_layout_table_view::try_create(table.bytes, result, error);
}

bool try_get_gdef(
    const sfnt_font_view& font,
    std::size_t gsub_length,
    std::size_t gpos_length,
    open_type_gdef_view& result,
    bool& has_gdef,
    font_error* error) noexcept {
    result = {};
    has_gdef = false;
    sfnt_table_view table{};
    if (!font.try_get_table(gdef_tag, table) ||
        is_open_type_gdef_blocklisted(
            table.bytes.size(), gsub_length, gpos_length)) {
        return true;
    }
    if (!open_type_gdef_view::try_create(table.bytes, result, error)) {
        return false;
    }
    has_gdef = true;
    return true;
}

bool has_glyph_flag(
    const shaping_glyph& glyph,
    shaping_glyph_flags flag) noexcept {
    return (static_cast<std::uint32_t>(glyph.flags) &
        static_cast<std::uint32_t>(flag)) != 0U;
}

std::span<const unicode_scalar> write_pre_context(
    std::span<const unicode_scalar> outer,
    std::span<const unicode_scalar> prefix,
    std::array<unicode_scalar, 5U>& storage) noexcept {
    const auto prefix_count =
        std::min<std::size_t>(storage.size(), prefix.size());
    const auto outer_count = std::min<std::size_t>(
        storage.size() - prefix_count, outer.size());
    const auto count = outer_count + prefix_count;
    std::copy(
        outer.end() - static_cast<std::ptrdiff_t>(outer_count),
        outer.end(),
        storage.begin());
    std::copy(
        prefix.end() - static_cast<std::ptrdiff_t>(prefix_count),
        prefix.end(),
        storage.begin() + static_cast<std::ptrdiff_t>(outer_count));
    return std::span<const unicode_scalar>{storage}.first(count);
}

std::span<const unicode_scalar> write_post_context(
    std::span<const unicode_scalar> suffix,
    std::span<const unicode_scalar> outer,
    std::array<unicode_scalar, 5U>& storage) noexcept {
    const auto suffix_count =
        std::min<std::size_t>(storage.size(), suffix.size());
    const auto outer_count = std::min<std::size_t>(
        storage.size() - suffix_count, outer.size());
    const auto count = suffix_count + outer_count;
    std::copy_n(suffix.begin(), suffix_count, storage.begin());
    std::copy_n(
        outer.begin(),
        outer_count,
        storage.begin() + static_cast<std::ptrdiff_t>(suffix_count));
    return std::span<const unicode_scalar>{storage}.first(count);
}

bool verify_open_type_shape_result(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::span<const shaping_glyph> expected,
    std::span<shaping_glyph> fragment_glyphs,
    open_type_shape_run_scratch scratch,
    const open_type_shape_plan* plan,
    font_error* error) noexcept {
    if (expected.empty()) {
        return true;
    }
    if (input.empty()) {
        set_error(error, font_error::verification_failed);
        return false;
    }
    const bool monotone =
        options.cluster_level == shaping_cluster_level::monotone_graphemes ||
        options.cluster_level == shaping_cluster_level::monotone_characters;
    if (!monotone) {
        return true;
    }
    const bool forward =
        options.direction == shaping_direction::left_to_right ||
        options.direction == shaping_direction::top_to_bottom;
    for (std::size_t index = 1U; index < expected.size(); ++index) {
        const auto previous = expected[index - 1U].cluster;
        const auto current = expected[index].cluster;
        if (previous != current && ((previous < current) != forward)) {
            set_error(error, font_error::verification_failed);
            return false;
        }
    }

    const auto run_start = input.front().input_index;
    const auto run_end_value =
        static_cast<std::uint64_t>(input.back().input_index) +
        input.back().input_length;
    if (run_end_value > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::verification_failed);
        return false;
    }
    const auto run_end = static_cast<std::uint32_t>(run_end_value);
    std::size_t output_start = 0U;
    std::uint32_t logical_edge = forward ? run_start : run_end;
    for (std::size_t output_end = 1U;
         output_end <= expected.size();
         ++output_end) {
        const bool at_end = output_end == expected.size();
        if (!at_end) {
            const auto& left = expected[output_end - 1U];
            const auto& right = expected[output_end];
            if (left.cluster == right.cluster) continue;
            const auto& boundary = forward ? right : left;
            if (has_glyph_flag(
                    boundary, shaping_glyph_flags::unsafe_to_break)) {
                continue;
            }
        }

        const auto fragment_cluster = [&](std::size_t index,
                                          std::uint32_t& value) {
            const auto cluster = expected[index].cluster;
            if (cluster < 0) return false;
            value = static_cast<std::uint32_t>(cluster);
            return true;
        };
        std::uint32_t fragment_start = 0U;
        std::uint32_t fragment_end = 0U;
        if (forward) {
            fragment_start = logical_edge;
            if (at_end) {
                fragment_end = run_end;
            } else if (!fragment_cluster(output_end, fragment_end)) {
                set_error(error, font_error::verification_failed);
                return false;
            }
            logical_edge = fragment_end;
        } else {
            if (at_end) {
                fragment_start = run_start;
            } else if (!fragment_cluster(
                           output_end - 1U, fragment_start)) {
                set_error(error, font_error::verification_failed);
                return false;
            }
            fragment_end = logical_edge;
            logical_edge = fragment_start;
        }
        if (fragment_start >= fragment_end) {
            set_error(error, font_error::verification_failed);
            return false;
        }

        const auto scalar_at_or_after = [&](std::uint32_t source_index) {
            return std::lower_bound(
                input.begin(),
                input.end(),
                source_index,
                [](const unicode_scalar& scalar, std::uint32_t value) {
                    return scalar.input_index < value;
                });
        };
        const auto fragment_first = scalar_at_or_after(fragment_start);
        const auto fragment_last = scalar_at_or_after(fragment_end);
        if (fragment_first == fragment_last ||
            fragment_first == input.end() ||
            fragment_first->input_index != fragment_start ||
            (fragment_last != input.end() &&
                fragment_last->input_index != fragment_end)) {
            set_error(error, font_error::verification_failed);
            return false;
        }
        const auto first_index = static_cast<std::size_t>(
            fragment_first - input.begin());
        const auto last_index = static_cast<std::size_t>(
            fragment_last - input.begin());
        const auto fragment_input = input.subspan(
            first_index, last_index - first_index);

        auto fragment_options = options;
        auto flags = static_cast<std::uint8_t>(options.buffer_flags);
        flags &= static_cast<std::uint8_t>(
            ~static_cast<std::uint8_t>(shaping_buffer_flags::verify));
        if (fragment_start != run_start) {
            flags &= static_cast<std::uint8_t>(
                ~static_cast<std::uint8_t>(
                    shaping_buffer_flags::beginning_of_text));
        }
        if (fragment_end != run_end) {
            flags &= static_cast<std::uint8_t>(
                ~static_cast<std::uint8_t>(
                    shaping_buffer_flags::end_of_text));
        }
        fragment_options.buffer_flags =
            static_cast<shaping_buffer_flags>(flags);
        std::array<unicode_scalar, 5U> pre_storage{};
        std::array<unicode_scalar, 5U> post_storage{};
        fragment_options.pre_context = write_pre_context(
            options.pre_context,
            input.first(first_index),
            pre_storage);
        fragment_options.post_context = write_post_context(
            input.subspan(last_index),
            options.post_context,
            post_storage);
        scratch.verification = nullptr;
        std::uint32_t actual_count = 0U;
        if (!try_shape_open_type_run(
                font,
                fragment_input,
                fragment_options,
                fragment_glyphs,
                scratch,
                actual_count,
                error,
                plan)) {
            if (error == nullptr || *error != font_error::insufficient_buffer) {
                set_error(error, font_error::verification_failed);
            }
            return false;
        }
        const auto expected_count = output_end - output_start;
        if (actual_count != expected_count) {
            set_error(error, font_error::verification_failed);
            return false;
        }
        for (std::size_t index = 0U; index < expected_count; ++index) {
            const auto& left = expected[output_start + index];
            const auto& right = fragment_glyphs[index];
            if (left.glyph_id != right.glyph_id ||
                left.cluster != right.cluster ||
                left.code_point != right.code_point ||
                left.advance_x != right.advance_x ||
                left.advance_y != right.advance_y ||
                left.offset_x != right.offset_x ||
                left.offset_y != right.offset_y) {
                set_error(error, font_error::verification_failed);
                return false;
            }
        }
        output_start = output_end;
    }
    return true;
}

} // namespace

bool try_verify_open_type_shape_result(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::span<const shaping_glyph> expected,
    std::span<shaping_glyph> fragment_glyph_storage,
    open_type_shape_run_scratch scratch,
    font_error* error,
    const open_type_shape_plan* plan) noexcept {
    auto fragment_options = options;
    fragment_options.buffer_flags = static_cast<shaping_buffer_flags>(
        static_cast<std::uint8_t>(options.buffer_flags) &
        static_cast<std::uint8_t>(
            ~static_cast<std::uint8_t>(shaping_buffer_flags::verify)));
    open_type_shape_run_requirements requirements{};
    if (!try_get_open_type_shape_run_requirements(
            font, input, fragment_options, requirements, error)) {
        return false;
    }
    if (fragment_glyph_storage.size() < requirements.glyph_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    scratch.verification = nullptr;
    const bool verified = verify_open_type_shape_result(
        font,
        input,
        options,
        expected,
        fragment_glyph_storage.first(requirements.glyph_capacity),
        scratch,
        plan,
        error);
    if (verified) {
        set_error(error, font_error::none);
    }
    return verified;
}

bool try_apply_directional_code_point_fallback(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    bool has_vertical_substitution,
    font_error* error) noexcept {
    const bool backward = direction == shaping_direction::right_to_left ||
        direction == shaping_direction::bottom_to_top;
    const bool vertical_fallback = !has_vertical_substitution &&
        (direction == shaping_direction::top_to_bottom ||
            direction == shaping_direction::bottom_to_top);
    if (!backward && !vertical_fallback) {
        set_error(error, font_error::none);
        return true;
    }

    for (auto& glyph : glyphs) {
        std::uint32_t code_point = glyph.code_point;
        std::uint16_t mapped_glyph = 0U;
        if (backward) {
            const auto mirrored = get_unicode_mirrored_code_point(code_point);
            if (mirrored != code_point) {
                if (!font.try_get_glyph_index(mirrored, mapped_glyph)) {
                    set_error(error, font_error::invalid_face);
                    return false;
                }
                if (mapped_glyph != 0U) code_point = mirrored;
            }
        }
        if (vertical_fallback) {
            const auto vertical = get_unicode_vertical_code_point(code_point);
            if (vertical != code_point) {
                if (!font.try_get_glyph_index(vertical, mapped_glyph)) {
                    set_error(error, font_error::invalid_face);
                    return false;
                }
                if (mapped_glyph != 0U) code_point = vertical;
            }
        }
        if (code_point == glyph.code_point) continue;
        if (!font.try_get_glyph_index(code_point, mapped_glyph)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        glyph.code_point = code_point;
        glyph.glyph_id = mapped_glyph;
    }
    set_error(error, font_error::none);
    return true;
}

bool try_get_open_type_shape_run_requirements(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    open_type_shape_run_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (input.size() > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (input.size() > std::numeric_limits<std::uint32_t>::max() / 3U) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    open_type_layout_table_view gsub{};
    open_type_layout_table_view gpos{};
    std::size_t gsub_length = 0U;
    std::size_t gpos_length = 0U;
    if (!try_get_layout(font, gsub_tag, gsub, gsub_length, error) ||
        !try_get_layout(font, gpos_tag, gpos, gpos_length, error)) {
        return false;
    }
    const std::uint32_t input_count = static_cast<std::uint32_t>(input.size());
    std::uint32_t grapheme_count = input_count;
    if (!is_printable_ascii(input)) {
        unicode_error unicode_result = unicode_error::none;
        if (!try_get_unicode_grapheme_cluster_count(
                input, grapheme_count, &unicode_result)) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
    }
    result = open_type_shape_run_requirements{
        input_count,
        input_count * 3U,
        grapheme_count,
        gsub.lookup_count(),
        gpos.lookup_count(),
        input_count,
        input_count,
        input_count + 1U};
    set_error(error, font_error::none);
    return true;
}

bool try_get_open_type_shape_run_requirements(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    open_type_shape_run_requirements& result,
    font_error* error) noexcept {
    if (!detail::valid_shaping_options(
            options.direction,
            options.cluster_level,
            options.buffer_flags,
            false)) {
        result = {};
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (!try_get_open_type_shape_run_requirements(
            font, input, result, error)) {
        return false;
    }
    if (static_cast<std::uint8_t>(options.complex_script) >
        static_cast<std::uint8_t>(open_type_complex_script::khmer)) {
        result = {};
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if ((options.complex_script == open_type_complex_script::indic ||
            options.complex_script == open_type_complex_script::use) &&
        options.normalization_data == nullptr) {
        result = {};
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::uint32_t mapped_count = 0U;
    if (!detail::try_get_initial_mapping_count(
            font,
            input,
            options.complex_script,
            options.normalization_data,
            mapped_count,
            error)) {
        result = {};
        return false;
    }
    result.initial_glyph_count = mapped_count;
    const std::uint64_t expanded_capacity =
        static_cast<std::uint64_t>(mapped_count) + input.size();
    if (expanded_capacity >= std::numeric_limits<std::uint32_t>::max()) {
        result = {};
        set_error(error, font_error::invalid_argument);
        return false;
    }
    result.glyph_capacity = std::max(
        result.glyph_capacity,
        static_cast<std::uint32_t>(expanded_capacity));
    if (options.complex_script == open_type_complex_script::use) {
        std::uint32_t normalized_count = 0U;
        if (!detail::try_get_use_diacritic_glyph_count(
                input,
                *options.normalization_data,
                normalized_count,
                error)) {
            result = {};
            return false;
        }
        const std::uint64_t use_capacity =
            static_cast<std::uint64_t>(normalized_count) + input.size();
        if (use_capacity >= std::numeric_limits<std::uint32_t>::max()) {
            result = {};
            set_error(error, font_error::invalid_argument);
            return false;
        }
        result.glyph_capacity = std::max(
            result.glyph_capacity,
            static_cast<std::uint32_t>(use_capacity));
    }
    if (options.complex_script != open_type_complex_script::none) {
        result.complex_script_capacity = result.glyph_capacity;
        result.complex_script_index_capacity = result.glyph_capacity + 1U;
    }
    const bool monotone_clusters =
        options.cluster_level == shaping_cluster_level::monotone_graphemes ||
        options.cluster_level == shaping_cluster_level::monotone_characters;
    if (monotone_clusters &&
        (static_cast<std::uint8_t>(options.buffer_flags) &
            static_cast<std::uint8_t>(shaping_buffer_flags::verify)) != 0U) {
        result.verification_glyph_capacity = result.glyph_capacity;
    }
    set_error(error, font_error::none);
    return true;
}

bool try_shape_open_type_run(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::span<shaping_glyph> glyph_storage,
    open_type_shape_run_scratch scratch,
    std::uint32_t& glyph_count,
    font_error* error,
    const open_type_shape_plan* plan) noexcept {
    glyph_count = 0U;
    complex_metadata_guard complex_guard{glyph_storage, &glyph_count, false};
    for (const auto& feature : options.feature_settings) {
        if (feature.start > feature.end ||
            !contains_feature(options.requested_features, feature.tag)) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
    }
    if (plan != nullptr && !plan->matches(font, options)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    open_type_shape_run_requirements requirements{};
    if (!try_get_open_type_shape_run_requirements(
            font, input, options, requirements, error)) {
        return false;
    }
    const auto unicode_script = effective_unicode_script(options);
    const bool simple_latin =
        options.direction == shaping_direction::left_to_right &&
        unicode_script == open_type_tag::from_chars('l', 'a', 't', 'n') &&
        options.complex_script == open_type_complex_script::none &&
        is_printable_ascii(input);
    const bool arabic_joining = uses_arabic_joining(unicode_script);
    const bool hangul = uses_hangul(unicode_script);
    const bool complex_script =
        options.complex_script != open_type_complex_script::none;
    const bool verify =
        requirements.verification_glyph_capacity != 0U;
    const bool fallback_mark_positioning =
        options.zero_mark_advances && uses_fallback_mark_positioning(options);
    const bool zero_mark_advances_early = options.zero_mark_advances &&
        (options.complex_script == open_type_complex_script::use ||
            options.complex_script == open_type_complex_script::myanmar);
    if (static_cast<std::uint8_t>(options.complex_script) >
        static_cast<std::uint8_t>(open_type_complex_script::khmer)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const std::size_t glyph_capacity = may_expand_preprocessing(
        unicode_script, options.buffer_flags) || complex_script
        ? requirements.glyph_capacity
        : requirements.initial_glyph_count;
    if (glyph_storage.size() < glyph_capacity ||
        scratch.grapheme_clusters.size() < requirements.grapheme_capacity ||
        scratch.gsub_lookups.size() < requirements.gsub_lookup_capacity ||
        scratch.gpos_lookups.size() < requirements.gpos_lookup_capacity ||
        (arabic_joining && scratch.arabic_actions.size() <
            requirements.script_action_capacity) ||
        (arabic_joining && scratch.arabic_flags.size() <
            requirements.script_action_capacity) ||
        (complex_script &&
            (scratch.script_categories.size() <
                    requirements.complex_script_capacity ||
                scratch.script_syllables.size() <
                    requirements.complex_script_capacity ||
                (options.complex_script == open_type_complex_script::use &&
                    scratch.script_indices.size() <
                        requirements.complex_script_index_capacity))) ||
        (verify &&
            (scratch.verification == nullptr ||
                scratch.verification->glyphs.size() <
                    requirements.verification_glyph_capacity)) ||
        scratch.attachments.size() < glyph_storage.size() ||
        scratch.attachment_states.size() < glyph_storage.size()) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    for (const auto& scalar : input) {
        if (scalar.input_index >
            static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max())) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        detail::initial_mapping mapping{};
        if (!detail::try_resolve_initial_mapping(
                font,
                scalar.code_point,
                options.complex_script,
                options.normalization_data,
                mapping,
                error)) {
            return false;
        }
        for (std::size_t index = 0U; index < mapping.size(); ++index) {
            const auto code_point = mapping.code_point_at(index);
            std::uint16_t glyph = 0U;
            float advance_width = 0.0F;
            if (!font.try_get_glyph_index(code_point, glyph)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            if (!detail::try_map_space_fallback(
                    font, code_point, glyph, error)) {
                return false;
            }
            const bool has_advance = scratch.fallback_marks == nullptr
                ? font.try_get_design_advance_width(
                    glyph,
                    options.normalized_coordinates,
                    advance_width,
                    error)
                : font.try_get_design_advance_width(
                    glyph,
                    options.normalized_coordinates,
                    advance_width,
                    scratch.fallback_marks->advance_width,
                    error);
            if (!has_advance) return false;
        }
    }

    std::uint32_t grapheme_count = requirements.grapheme_capacity;
    if (is_printable_ascii(input)) {
        write_ascii_graphemes(
            input,
            scratch.grapheme_clusters.first(requirements.grapheme_capacity));
    } else {
        unicode_error unicode_result = unicode_error::none;
        if (!try_segment_unicode_graphemes(
                input,
                scratch.grapheme_clusters.first(
                    requirements.grapheme_capacity),
                grapheme_count,
                &unicode_result)) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
    }
    if (arabic_joining) {
        std::uint32_t action_count = 0U;
        unicode_error action_error = unicode_error::none;
        if (!try_assign_open_type_arabic_actions_and_flags(
                input,
                scratch.grapheme_clusters.first(grapheme_count),
                options.pre_context,
                options.post_context,
                options.buffer_flags,
                scratch.arabic_actions.first(
                    requirements.script_action_capacity),
                scratch.arabic_flags.first(
                    requirements.script_action_capacity),
                action_count,
                &action_error) || action_count != input.size()) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
    }
    std::uint32_t mapped_count = 0U;
    for (std::uint32_t cluster_index = 0U;
         cluster_index < grapheme_count;
         ++cluster_index) {
        const auto cluster = scratch.grapheme_clusters[cluster_index];
        for (std::uint32_t offset = 0U; offset < cluster.scalar_count; ++offset) {
            const std::size_t scalar_index = cluster.scalar_index + offset;
            if (offset != 0U && mapped_count != 0U &&
                is_variation_selector(input[scalar_index].code_point)) {
                std::uint16_t variation_glyph = 0U;
                if (font.try_get_variation_glyph(
                        glyph_storage[mapped_count - 1U].code_point,
                        input[scalar_index].code_point,
                        variation_glyph)) {
                    glyph_storage[mapped_count - 1U].glyph_id = variation_glyph;
                    continue;
                }
            }
            detail::initial_mapping mapping{};
            if (!detail::try_resolve_initial_mapping(
                    font,
                    input[scalar_index].code_point,
                    options.complex_script,
                    options.normalization_data,
                    mapping,
                    error)) {
                return false;
            }
            for (std::size_t index = 0U; index < mapping.size(); ++index) {
                if (mapped_count >= glyph_storage.size()) {
                    set_error(error, font_error::insufficient_buffer);
                    return false;
                }
                const auto code_point = mapping.code_point_at(index);
                std::uint16_t glyph = 0U;
                if (!font.try_get_glyph_index(code_point, glyph)) {
                    set_error(error, font_error::invalid_face);
                    return false;
                }
                if (!detail::try_map_space_fallback(
                        font, code_point, glyph, error)) {
                    return false;
                }
                glyph_storage[mapped_count] = shaping_glyph{
                    glyph,
                    code_point,
                    static_cast<std::int32_t>(cluster.input_index)};
                if (arabic_joining) {
                    set_arabic_action(
                        glyph_storage[mapped_count],
                        scratch.arabic_actions[scalar_index]);
                    glyph_storage[mapped_count].flags =
                        static_cast<shaping_glyph_flags>(
                            static_cast<std::uint32_t>(
                                glyph_storage[mapped_count].flags) |
                            static_cast<std::uint32_t>(
                                scratch.arabic_flags[scalar_index]));
                }
                ++mapped_count;
            }
        }
    }
    glyph_count = mapped_count;

    open_type_layout_table_view gsub{};
    open_type_layout_table_view gpos{};
    std::size_t gsub_length = 0U;
    std::size_t gpos_length = 0U;
    open_type_gdef_view gdef{};
    bool has_gdef = false;
    std::uint32_t random_alternate_state = 1U;
    if (plan != nullptr) {
        gsub = plan->gsub;
        gpos = plan->gpos;
        gdef = plan->gdef;
        has_gdef = plan->has_gdef;
    } else {
        if (!try_get_layout(font, gsub_tag, gsub, gsub_length, error) ||
            !try_get_layout(font, gpos_tag, gpos, gpos_length, error) ||
            !try_get_gdef(
                font, gsub_length, gpos_length, gdef, has_gdef, error)) {
            return false;
        }
    }

    const bool is_vertical_run =
        options.direction == shaping_direction::top_to_bottom ||
        options.direction == shaping_direction::bottom_to_top;
    bool has_vertical_substitution = false;
    constexpr std::array vertical_features{
        open_type_tag::from_chars('v', 'e', 'r', 't'),
        open_type_tag::from_chars('v', 'r', 't', '2')};
    if (is_vertical_run) {
        for (const auto feature : vertical_features) {
            if (!is_run_feature_enabled(options, feature)) continue;
            std::uint32_t lookup_count = 0U;
            if (!gsub.try_select_feature_lookups(
                    options.script,
                    options.language,
                    feature,
                    scratch.gsub_lookups.first(gsub.lookup_count()),
                    lookup_count,
                    error)) {
                glyph_count = 0U;
                return false;
            }
            if (lookup_count != 0U) {
                has_vertical_substitution = true;
                break;
            }
        }
    }
    if (!simple_latin && !try_apply_directional_code_point_fallback(
            font,
            glyph_storage.first(glyph_count),
            options.direction,
            has_vertical_substitution,
            error)) {
        glyph_count = 0U;
        return false;
    }

    if (!simple_latin && !try_preprocess_open_type_glyphs(
            font,
            unicode_script,
            options.cluster_level,
            options.buffer_flags,
            options.compose_hebrew_presentation_forms,
            glyph_storage,
            glyph_count,
            error,
            options.complex_script == open_type_complex_script::use
                ? options.normalization_data
                : nullptr,
            !options.pre_context.empty())) {
        glyph_count = 0U;
        return false;
    }
    if (options.complex_script == open_type_complex_script::khmer) {
        if (!complex_detail::try_prepare_khmer(
                font,
                options.buffer_flags,
                glyph_storage,
                glyph_count,
                scratch.script_categories,
                scratch.script_syllables,
                error)) {
            glyph_count = 0U;
            return false;
        }
        complex_guard.enabled = true;
    } else if (options.complex_script == open_type_complex_script::indic) {
        if (!complex_detail::try_prepare_indic(
                glyph_storage,
                glyph_count,
                scratch.script_categories,
                scratch.script_syllables,
                error)) {
            glyph_count = 0U;
            return false;
        }
        complex_guard.enabled = true;
    } else if (options.complex_script == open_type_complex_script::myanmar) {
        if (!complex_detail::try_prepare_myanmar(
                font,
                options.buffer_flags,
                glyph_storage,
                glyph_count,
                scratch.script_categories,
                scratch.script_syllables,
                error)) {
            glyph_count = 0U;
            return false;
        }
        complex_guard.enabled = true;
    } else if (options.complex_script == open_type_complex_script::use) {
        if (!complex_detail::try_prepare_use(
                font,
                options.buffer_flags,
                glyph_storage,
                glyph_count,
                scratch.script_categories,
                scratch.script_syllables,
                scratch.script_indices,
                error)) {
            glyph_count = 0U;
            return false;
        }
        complex_guard.enabled = true;
    } else if (complex_script) {
        set_error(error, font_error::invalid_argument);
        glyph_count = 0U;
        return false;
    }

    const open_type_gdef_view* gdef_pointer = has_gdef ? &gdef : nullptr;
    bool has_arabic_form_substitution = false;
    if (unicode_script == arabic_script && gsub.lookup_count() != 0U) {
        for (const auto& [feature, action] : arabic_form_features) {
            static_cast<void>(action);
            std::uint32_t lookup_count = 0U;
            if (!gsub.try_select_feature_lookups(
                    options.script,
                    options.language,
                    feature,
                    scratch.gsub_lookups.first(gsub.lookup_count()),
                    lookup_count,
                    error)) {
                clear_arabic_actions(glyph_storage.first(glyph_count));
                return false;
            }
            if (lookup_count != 0U) {
                has_arabic_form_substitution = true;
                break;
            }
        }
    }
    std::array<open_type_tag, 3U> excluded_fraction_storage{};
    const auto excluded_fraction = inactive_fraction_features(
        options,
        excluded_fraction_storage);
    std::array<open_type_tag, 11U> excluded_gsub_storage{};
    std::copy(excluded_fraction.begin(), excluded_fraction.end(),
        excluded_gsub_storage.begin());
    std::size_t excluded_gsub_count = excluded_fraction.size();
    if (arabic_joining) {
        excluded_gsub_storage[excluded_gsub_count++] = stretch_feature;
        for (const auto& [feature, action] : arabic_form_features) {
            static_cast<void>(action);
            excluded_gsub_storage[excluded_gsub_count++] = feature;
        }
    }
    const auto excluded_gsub = std::span<const open_type_tag>{
        excluded_gsub_storage}.first(excluded_gsub_count);

    if (gsub.lookup_count() != 0U) {
        if (arabic_joining &&
            !apply_arabic_stretch_feature(
                gsub,
                options.script,
                options.language,
                scratch.gsub_lookups.first(gsub.lookup_count()),
                glyph_storage,
                glyph_count,
                options,
                gdef_pointer,
                fallback_mark_positioning,
                error)) {
            clear_arabic_actions(glyph_storage.first(glyph_count));
            return false;
        }
        if (complex_script) {
            if (!apply_complex_script_features(
                    font,
                    gsub,
                    input,
                    options,
                    scratch.gsub_lookups.first(gsub.lookup_count()),
                    glyph_storage,
                    glyph_count,
                    gdef_pointer,
                    error)) {
                return false;
            }
        } else {
            std::uint32_t lookup_count = 0U;
            std::span<const std::uint16_t> selected_lookups{};
            if (plan != nullptr && excluded_gsub.empty()) {
                selected_lookups = plan->gsub_lookups;
                lookup_count = static_cast<std::uint32_t>(
                    selected_lookups.size());
            } else {
                const bool selected = excluded_gsub.empty()
                    ? gsub.try_select_lookups(
                        options.script,
                        options.language,
                        options.requested_features,
                        scratch.gsub_lookups.first(gsub.lookup_count()),
                        lookup_count,
                        error)
                    : gsub.try_select_lookups_excluding(
                        options.script,
                        options.language,
                        options.requested_features,
                        excluded_gsub,
                        scratch.gsub_lookups.first(gsub.lookup_count()),
                        lookup_count,
                        error);
                if (!selected) {
                    return false;
                }
                selected_lookups = scratch.gsub_lookups.first(lookup_count);
            }
            for (std::uint32_t index = 0U; index < lookup_count; ++index) {
                if (!apply_gsub_lookup_with_feature_values(
                        gsub,
                        options,
                        selected_lookups[index],
                        glyph_storage,
                        glyph_count,
                        gdef_pointer,
                        error,
                        &random_alternate_state)) {
                    return false;
                }
            }
            if (!apply_fraction_features(
                    gsub,
                    input,
                    options,
                    glyph_storage,
                    glyph_count,
                    gdef_pointer,
                    error)) {
                return false;
            }
        }
        if (arabic_joining) {
            const open_type_gsub_apply_options apply_options{
                gdef_pointer,
                options.alternate_value,
                0U,
                false,
                nullptr,
                fallback_mark_positioning};
            for (const auto& [feature, action] : arabic_form_features) {
                if (!apply_arabic_form_feature(
                        gsub,
                        options.script,
                        options.language,
                        feature,
                        action,
                        scratch.gsub_lookups.first(gsub.lookup_count()),
                        glyph_storage,
                        glyph_count,
                        options,
                        apply_options,
                        error)) {
                    clear_arabic_actions(glyph_storage.first(glyph_count));
                    return false;
                }
            }
        }
        if (hangul) {
            const open_type_gsub_apply_options apply_options{
                gdef_pointer,
                options.alternate_value,
                0U,
                false,
                nullptr,
                fallback_mark_positioning};
            constexpr std::array jamo_features{
                std::pair{open_type_tag::from_chars('l', 'j', 'm', 'o'),
                    hangul_feature::leading},
                std::pair{open_type_tag::from_chars('v', 'j', 'm', 'o'),
                    hangul_feature::vowel},
                std::pair{open_type_tag::from_chars('t', 'j', 'm', 'o'),
                    hangul_feature::trailing}};
            for (const auto& [feature, kind] : jamo_features) {
                if (!apply_hangul_feature(
                        gsub,
                        options.script,
                        options.language,
                        feature,
                        kind,
                        scratch.gsub_lookups.first(gsub.lookup_count()),
                        glyph_storage,
                        glyph_count,
                        options,
                        apply_options,
                        error)) {
                    clear_hangul_features(glyph_storage.first(glyph_count));
                    return false;
                }
            }
        }
    }
    if (unicode_script == arabic_script &&
        !has_arabic_form_substitution &&
        !detail::try_apply_arabic_fallback(
            font,
            glyph_storage,
            glyph_count,
            gdef_pointer,
            detail::arabic_fallback_options{
                is_run_feature_enabled(
                    options,
                    open_type_tag::from_chars('i', 'n', 'i', 't')),
                is_run_feature_enabled(
                    options,
                    open_type_tag::from_chars('m', 'e', 'd', 'i')),
                is_run_feature_enabled(
                    options,
                    open_type_tag::from_chars('f', 'i', 'n', 'a')),
                is_run_feature_enabled(
                    options,
                    open_type_tag::from_chars('i', 's', 'o', 'l')),
                is_run_feature_enabled(
                    options,
                    open_type_tag::from_chars('r', 'l', 'i', 'g')),
                fallback_mark_positioning},
            error)) {
        clear_arabic_actions(glyph_storage.first(glyph_count));
        return false;
    }
    if (gsub.lookup_count() == 0U &&
        complex_script) {
        bool reordered = true;
        if (options.complex_script == open_type_complex_script::use) {
            reordered = complex_detail::try_reorder_use(
                font,
                options.buffer_flags,
                glyph_storage,
                glyph_count,
                error);
        } else if (options.complex_script ==
            open_type_complex_script::myanmar) {
            reordered = complex_detail::try_reorder_myanmar(
                font,
                options.buffer_flags,
                glyph_storage,
                glyph_count,
                error);
        } else if (options.complex_script ==
            open_type_complex_script::indic) {
            reordered = complex_detail::try_initial_reorder_indic(
                font,
                unicode_script,
                options.buffer_flags,
                glyph_storage,
                glyph_count,
                error);
            if (reordered) {
                complex_detail::final_reorder_indic(
                    unicode_script, glyph_storage.first(glyph_count));
            }
        }
        if (!reordered) {
            return false;
        }
    }

    if (hangul) {
        clear_hangul_features(glyph_storage.first(glyph_count));
    }

    const bool vertical =
        options.direction == shaping_direction::top_to_bottom ||
        options.direction == shaping_direction::bottom_to_top;
    sfnt_table_view gpos_table{};
    const bool has_gpos_table = font.try_get_table(gpos_tag, gpos_table);
    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        scratch.attachments[index] = {};
        if (fallback_mark_positioning) {
            scratch.attachments[index].reserved0 =
                detail::fallback_ligature_count(glyph_storage[index]);
            scratch.attachments[index].reserved1 =
                detail::fallback_ligature_component(glyph_storage[index]);
            detail::clear_fallback_ligature_metadata(glyph_storage[index]);
        }
        if (glyph_storage[index].glyph_id > 0xFFFFU) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        float advance_width = 0.0F;
        const auto glyph =
            static_cast<std::uint16_t>(glyph_storage[index].glyph_id);
        const bool has_advance = scratch.fallback_marks == nullptr
            ? font.try_get_design_advance_width(
                glyph,
                options.normalized_coordinates,
                advance_width,
                error)
            : font.try_get_design_advance_width(
                glyph,
                options.normalized_coordinates,
                advance_width,
                scratch.fallback_marks->advance_width,
                error);
        if (!has_advance) {
            return false;
        }
        if (vertical) {
            std::int32_t advance_height = 0;
            std::int32_t origin_y = 0;
            if (!font.try_get_design_advance_height(
                    static_cast<std::uint16_t>(glyph_storage[index].glyph_id),
                    advance_height) ||
                !font.try_get_design_vertical_origin_y(
                    static_cast<std::uint16_t>(glyph_storage[index].glyph_id),
                    origin_y)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            glyph_storage[index].advance_x = 0;
            glyph_storage[index].advance_y = clamp_i16(-advance_height);
            glyph_storage[index].offset_x = clamp_i16(
                -(round_to_even(advance_width) / 2));
            glyph_storage[index].offset_y = clamp_i16(-origin_y);
        } else {
            glyph_storage[index].advance_x = clamp_i16(
                static_cast<std::int64_t>(std::lround(advance_width)));
            glyph_storage[index].advance_y = 0;
            glyph_storage[index].offset_x = 0;
            glyph_storage[index].offset_y = 0;
        }
        if (!detail::try_apply_space_fallback(
                font,
                options.direction,
                options.normalized_coordinates,
                scratch.fallback_marks,
                glyph_storage[index],
                error)) {
            return false;
        }
        if (zero_mark_advances_early &&
            is_positioning_mark(glyph_storage[index], gdef_pointer)) {
            if (!has_gpos_table &&
                (options.direction == shaping_direction::left_to_right ||
                 options.direction == shaping_direction::top_to_bottom)) {
                glyph_storage[index].offset_x = clamp_i16(
                    static_cast<std::int64_t>(glyph_storage[index].offset_x) -
                    glyph_storage[index].advance_x);
                glyph_storage[index].offset_y = clamp_i16(
                    static_cast<std::int64_t>(glyph_storage[index].offset_y) -
                    glyph_storage[index].advance_y);
            }
            glyph_storage[index].advance_x = 0;
            glyph_storage[index].advance_y = 0;
        }
    }

    bool has_gpos_kerning = false;
    if (gpos.lookup_count() != 0U) {
        std::uint32_t lookup_count = 0U;
        std::span<const std::uint16_t> selected_lookups{};
        if (plan != nullptr && excluded_fraction.empty()) {
            selected_lookups = plan->gpos_lookups;
            lookup_count = static_cast<std::uint32_t>(
                selected_lookups.size());
        } else {
            const bool selected = excluded_fraction.empty()
                ? gpos.try_select_lookups(
                    options.script,
                    options.language,
                    options.requested_features,
                    scratch.gpos_lookups.first(gpos.lookup_count()),
                    lookup_count,
                    error)
                : gpos.try_select_lookups_excluding(
                    options.script,
                    options.language,
                    options.requested_features,
                    excluded_fraction,
                    scratch.gpos_lookups.first(gpos.lookup_count()),
                    lookup_count,
                    error);
            if (!selected) {
                return false;
            }
            selected_lookups = scratch.gpos_lookups.first(lookup_count);
        }
        const auto glyphs = glyph_storage.first(glyph_count);
        const auto attachments = scratch.attachments.first(glyph_count);
        const open_type_gpos_apply_options apply_options{
            gdef_pointer,
            options.direction,
            attachments,
            &font,
            options.normalized_coordinates};
        for (std::uint32_t index = 0U; index < lookup_count; ++index) {
            lookup_feature_resolution resolution{};
            if (!try_resolve_lookup_feature(
                    gpos,
                    options,
                    selected_lookups[index],
                    resolution,
                    error)) {
                return false;
            }
            has_gpos_kerning |= (resolution.required || resolution.found) &&
                (resolution.feature == kern_feature ||
                    resolution.feature == distance_feature);
            if (!apply_gpos_lookup_with_feature_values(
                    gpos,
                    options,
                    selected_lookups[index],
                    glyphs,
                    apply_options,
                    error)) {
                return false;
            }
        }
    }
    if (!has_gpos_kerning &&
        is_run_feature_enabled(options, kern_feature) &&
        options.complex_script != open_type_complex_script::indic &&
        (options.direction == shaping_direction::left_to_right ||
            options.direction == shaping_direction::right_to_left)) {
        detail::apply_legacy_kern(
            font, glyph_storage.first(glyph_count), gdef_pointer);
    }
    const bool zero_mark_advances_late = options.zero_mark_advances &&
        (fallback_mark_positioning ||
            unicode_script == open_type_tag::from_chars('t', 'h', 'a', 'i') ||
            unicode_script == open_type_tag::from_chars('l', 'a', 'o', ' '));
    if (zero_mark_advances_late) {
        const bool adjust_offsets = !has_gpos_table &&
            (options.direction == shaping_direction::left_to_right ||
                options.direction == shaping_direction::top_to_bottom);
        for (std::uint32_t index = 0U; index < glyph_count; ++index) {
            if (!is_positioning_mark(glyph_storage[index], gdef_pointer)) {
                continue;
            }
            if (adjust_offsets) {
                glyph_storage[index].offset_x = clamp_i16(
                    static_cast<std::int64_t>(glyph_storage[index].offset_x) -
                    glyph_storage[index].advance_x);
                glyph_storage[index].offset_y = clamp_i16(
                    static_cast<std::int64_t>(glyph_storage[index].offset_y) -
                    glyph_storage[index].advance_y);
            }
            glyph_storage[index].advance_x = 0;
            glyph_storage[index].advance_y = 0;
        }
    }
    if (gpos.lookup_count() != 0U) {
        const auto glyphs = glyph_storage.first(glyph_count);
        const auto attachments = scratch.attachments.first(glyph_count);
        if (!try_resolve_open_type_attachments(
                glyphs,
                attachments,
                options.direction,
                scratch.attachment_states.first(glyph_count),
                error)) {
            return false;
        }
    }
    if (fallback_mark_positioning &&
        !detail::try_apply_fallback_mark_positioning_from_attachments(
            font,
            glyph_storage.first(glyph_count),
            options.direction,
            scratch.attachments.first(glyph_count),
            options.normalized_coordinates,
            scratch.fallback_marks,
            error)) {
        return false;
    }
    if (options.direction == shaping_direction::right_to_left ||
        options.direction == shaping_direction::bottom_to_top) {
        auto glyphs = glyph_storage.first(glyph_count);
        std::reverse(glyphs.begin(), glyphs.end());
        if (options.cluster_level ==
            shaping_cluster_level::monotone_characters) {
            for (std::size_t start = 0U; start < glyphs.size();) {
                while (start < glyphs.size() &&
                    complex_detail::modified_combining_class(
                        glyphs[start].code_point) == 0) {
                    ++start;
                }
                auto end = start;
                while (end < glyphs.size() &&
                    complex_detail::modified_combining_class(
                        glyphs[end].code_point) != 0) {
                    ++end;
                }
                if (end > start) {
                    auto left = start;
                    auto right = end - 1U;
                    while (left < right) {
                        std::swap(
                            glyphs[left].cluster,
                            glyphs[right].cluster);
                        ++left;
                        --right;
                    }
                }
                start = end < glyphs.size() ? end + 1U : end;
            }
        }
    }
    if (arabic_joining &&
        !detail::try_apply_arabic_stretch_from_glyph_actions(
            font,
            glyph_storage,
            glyph_count,
            options.direction == shaping_direction::right_to_left,
            options.normalized_coordinates,
            scratch.arabic_stretch_runs,
            error)) {
        clear_arabic_actions(glyph_storage.first(glyph_count));
        return false;
    }
    if (arabic_joining) {
        clear_arabic_actions(glyph_storage.first(glyph_count));
    }
    if (verify && !try_verify_open_type_shape_result(
            font,
            input,
            options,
            glyph_storage.first(glyph_count),
            scratch.verification->glyphs.first(
                requirements.verification_glyph_capacity),
            scratch,
            error,
            plan)) {
        glyph_count = 0U;
        return false;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text

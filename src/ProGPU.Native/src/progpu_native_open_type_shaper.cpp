#include "progpu_native_text.hpp"

#include "progpu_native_open_type_complex_internal.hpp"

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

constexpr open_type_tag gdef_tag =
    open_type_tag::from_chars('G', 'D', 'E', 'F');
constexpr open_type_tag gsub_tag =
    open_type_tag::from_chars('G', 'S', 'U', 'B');
constexpr open_type_tag gpos_tag =
    open_type_tag::from_chars('G', 'P', 'O', 'S');
constexpr std::uint32_t arabic_action_mask = 0x70000000U;
constexpr std::uint32_t arabic_action_shift = 28U;
constexpr std::uint32_t hangul_feature_mask = 0x30000000U;
constexpr std::uint32_t hangul_feature_shift = 28U;

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
        script == open_type_tag::from_chars('l', 'a', 'o', ' ');
}

bool is_variation_selector(std::uint32_t code_point) noexcept {
    return (code_point >= 0xFE00U && code_point <= 0xFE0FU) ||
        (code_point >= 0xE0100U && code_point <= 0xE01EFU);
}

void set_arabic_action(
    shaping_glyph& glyph,
    open_type_arabic_action action) noexcept {
    const std::uint32_t flags = static_cast<std::uint32_t>(glyph.flags);
    glyph.flags = static_cast<shaping_glyph_flags>(
        (flags & ~arabic_action_mask) |
        (static_cast<std::uint32_t>(action) << arabic_action_shift));
}

open_type_arabic_action get_arabic_action(
    const shaping_glyph& glyph) noexcept {
    return static_cast<open_type_arabic_action>(
        (static_cast<std::uint32_t>(glyph.flags) & arabic_action_mask) >>
        arabic_action_shift);
}

void clear_arabic_actions(
    std::span<shaping_glyph> glyphs) noexcept {
    for (auto& glyph : glyphs) {
        glyph.flags = static_cast<shaping_glyph_flags>(
            static_cast<std::uint32_t>(glyph.flags) & ~arabic_action_mask);
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
    const open_type_gsub_apply_options& options,
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
            if (get_arabic_action(glyph_storage[position]) != action) {
                ++position;
                continue;
            }
            const std::uint32_t count_before = glyph_count;
            bool applied = false;
            if (!try_apply_open_type_gsub_lookup_at(
                    gsub,
                    lookup_scratch[lookup],
                    glyph_storage,
                    glyph_count,
                    position,
                    options,
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

bool apply_hangul_feature(
    const open_type_layout_table_view& gsub,
    open_type_tag script,
    open_type_tag language,
    open_type_tag feature,
    hangul_feature required_feature,
    std::span<std::uint16_t> lookup_scratch,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gsub_apply_options& options,
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
            if (get_hangul_feature(glyph_storage[position]) !=
                required_feature) {
                ++position;
                continue;
            }
            const std::uint32_t count_before = glyph_count;
            bool applied = false;
            if (!try_apply_open_type_gsub_lookup_at(
                    gsub,
                    lookup_scratch[lookup],
                    glyph_storage,
                    glyph_count,
                    position,
                    options,
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
    bool has_fraction,
    std::array<open_type_tag, 3U>& storage) noexcept {
    if (has_fraction) {
        return {};
    }
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
            glyph_count, gdef, error) ||
        !apply_complex_feature_group(
            gsub, options, preprocessing, lookup_scratch, glyph_storage,
            glyph_count, gdef, error)) {
        return false;
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
            options.script,
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
                options.script, glyph_storage.first(glyph_count));
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

} // namespace

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
    std::uint32_t grapheme_count = 0U;
    unicode_error unicode_result = unicode_error::none;
    if (!try_get_unicode_grapheme_cluster_count(
            input, grapheme_count, &unicode_result)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const std::uint32_t input_count = static_cast<std::uint32_t>(input.size());
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
    if (plan != nullptr && !plan->matches(font, options)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    open_type_shape_run_requirements requirements{};
    if (!try_get_open_type_shape_run_requirements(
            font, input, requirements, error)) {
        return false;
    }
    const bool arabic_joining = uses_arabic_joining(options.script);
    const bool hangul = uses_hangul(options.script);
    const bool complex_script =
        options.complex_script != open_type_complex_script::none;
    if (static_cast<std::uint8_t>(options.complex_script) >
        static_cast<std::uint8_t>(open_type_complex_script::khmer)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const std::size_t glyph_capacity = may_expand_preprocessing(
        options.script, options.buffer_flags) || complex_script
        ? requirements.glyph_capacity
        : requirements.initial_glyph_count;
    if (glyph_storage.size() < glyph_capacity ||
        scratch.grapheme_clusters.size() < requirements.grapheme_capacity ||
        scratch.gsub_lookups.size() < requirements.gsub_lookup_capacity ||
        scratch.gpos_lookups.size() < requirements.gpos_lookup_capacity ||
        (arabic_joining && scratch.arabic_actions.size() <
            requirements.script_action_capacity) ||
        (complex_script &&
            (scratch.script_categories.size() <
                    requirements.complex_script_capacity ||
                scratch.script_syllables.size() <
                    requirements.complex_script_capacity ||
                (options.complex_script == open_type_complex_script::use &&
                    scratch.script_indices.size() <
                        requirements.complex_script_index_capacity))) ||
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
        std::uint16_t glyph = 0U;
        sfnt_horizontal_glyph_metrics metrics{};
        if (!font.try_get_glyph_index(scalar.code_point, glyph) ||
            !font.try_get_horizontal_glyph_metrics(glyph, metrics)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
    }

    std::uint32_t grapheme_count = 0U;
    unicode_error unicode_result = unicode_error::none;
    if (!try_segment_unicode_graphemes(
            input,
            scratch.grapheme_clusters.first(requirements.grapheme_capacity),
            grapheme_count,
            &unicode_result)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    if (arabic_joining) {
        std::uint32_t action_count = 0U;
        unicode_error action_error = unicode_error::none;
        if (!try_assign_open_type_arabic_actions(
                input,
                scratch.arabic_actions.first(
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
            std::uint16_t glyph = 0U;
            if (!font.try_get_glyph_index(
                    input[scalar_index].code_point, glyph)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            glyph_storage[mapped_count] = shaping_glyph{
                glyph,
                input[scalar_index].code_point,
                static_cast<std::int32_t>(cluster.input_index)};
            if (arabic_joining) {
                set_arabic_action(
                    glyph_storage[mapped_count],
                    scratch.arabic_actions[scalar_index]);
            }
            ++mapped_count;
        }
    }
    glyph_count = mapped_count;

    if (!try_preprocess_open_type_glyphs(
            font,
            options.script,
            options.cluster_level,
            options.buffer_flags,
            options.compose_hebrew_presentation_forms,
            glyph_storage,
            glyph_count,
            error)) {
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

    open_type_layout_table_view gsub{};
    open_type_layout_table_view gpos{};
    std::size_t gsub_length = 0U;
    std::size_t gpos_length = 0U;
    open_type_gdef_view gdef{};
    bool has_gdef = false;
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
    const open_type_gdef_view* gdef_pointer = has_gdef ? &gdef : nullptr;
    std::array<open_type_tag, 3U> excluded_fraction_storage{};
    const auto excluded_fraction = inactive_fraction_features(
        options,
        has_fraction_actions(input),
        excluded_fraction_storage);

    if (gsub.lookup_count() != 0U) {
        if (complex_script) {
            if (!apply_complex_script_features(
                    font,
                    gsub,
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
            if (plan != nullptr && excluded_fraction.empty()) {
                selected_lookups = plan->gsub_lookups;
                lookup_count = static_cast<std::uint32_t>(
                    selected_lookups.size());
            } else {
                const bool selected = excluded_fraction.empty()
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
                        excluded_fraction,
                        scratch.gsub_lookups.first(gsub.lookup_count()),
                        lookup_count,
                        error);
                if (!selected) {
                    return false;
                }
                selected_lookups = scratch.gsub_lookups.first(lookup_count);
            }
            for (std::uint32_t index = 0U; index < lookup_count; ++index) {
                bool applied = false;
                if (!try_apply_open_type_gsub_lookup(
                        gsub,
                        selected_lookups[index],
                        glyph_storage,
                        glyph_count,
                        open_type_gsub_apply_options{
                            gdef_pointer, options.alternate_value},
                        applied,
                        error)) {
                    return false;
                }
            }
        }
        if (arabic_joining) {
            const open_type_gsub_apply_options apply_options{
                gdef_pointer, options.alternate_value};
            constexpr std::array form_features{
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
            for (const auto& [feature, action] : form_features) {
                if (!apply_arabic_form_feature(
                        gsub,
                        options.script,
                        options.language,
                        feature,
                        action,
                        scratch.gsub_lookups.first(gsub.lookup_count()),
                        glyph_storage,
                        glyph_count,
                        apply_options,
                        error)) {
                    clear_arabic_actions(glyph_storage.first(glyph_count));
                    return false;
                }
            }
        }
        if (hangul) {
            const open_type_gsub_apply_options apply_options{
                gdef_pointer, options.alternate_value};
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
                        apply_options,
                        error)) {
                    clear_hangul_features(glyph_storage.first(glyph_count));
                    return false;
                }
            }
        }
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
                options.script,
                options.buffer_flags,
                glyph_storage,
                glyph_count,
                error);
            if (reordered) {
                complex_detail::final_reorder_indic(
                    options.script, glyph_storage.first(glyph_count));
            }
        }
        if (!reordered) {
            return false;
        }
    }

    if (arabic_joining) {
        clear_arabic_actions(glyph_storage.first(glyph_count));
    }
    if (hangul) {
        clear_hangul_features(glyph_storage.first(glyph_count));
    }

    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        if (glyph_storage[index].glyph_id > 0xFFFFU) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        sfnt_horizontal_glyph_metrics metrics{};
        if (!font.try_get_horizontal_glyph_metrics(
                static_cast<std::uint16_t>(glyph_storage[index].glyph_id),
                metrics)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        std::int64_t advance = metrics.advance_width;
        if (!options.normalized_coordinates.empty()) {
            float delta = 0.0F;
            bool uses_hvar = false;
            if (!font.try_get_horizontal_advance_variation(
                    static_cast<std::uint16_t>(glyph_storage[index].glyph_id),
                    options.normalized_coordinates,
                    delta,
                    uses_hvar,
                    error)) {
                return false;
            }
            advance += static_cast<std::int64_t>(std::lround(delta));
        }
        glyph_storage[index].advance_x = static_cast<std::int32_t>(
            std::clamp<std::int64_t>(
                advance,
                std::numeric_limits<std::int32_t>::min(),
                std::numeric_limits<std::int32_t>::max()));
        glyph_storage[index].advance_y = 0;
        glyph_storage[index].offset_x = 0;
        glyph_storage[index].offset_y = 0;
        if (options.zero_mark_advances && gdef_pointer != nullptr &&
            gdef_pointer->glyph_class(
                static_cast<std::uint16_t>(glyph_storage[index].glyph_id)) ==
                open_type_glyph_class::mark) {
            glyph_storage[index].advance_x = 0;
        }
        scratch.attachments[index] = {};
    }

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
        for (std::uint32_t index = 0U; index < lookup_count; ++index) {
            bool applied = false;
            if (!try_apply_open_type_gpos_lookup(
                    gpos,
                    selected_lookups[index],
                    glyphs,
                    open_type_gpos_apply_options{
                        gdef_pointer,
                        options.direction,
                        attachments,
                        &font,
                        options.normalized_coordinates},
                    applied,
                    error)) {
                return false;
            }
        }
        if (!try_resolve_open_type_attachments(
                glyphs,
                attachments,
                options.direction,
                scratch.attachment_states.first(glyph_count),
                error)) {
            return false;
        }
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text

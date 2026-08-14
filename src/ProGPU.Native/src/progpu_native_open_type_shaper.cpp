#include "progpu_native_text.hpp"

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
constexpr std::uint32_t arabic_action_mask = 0x00000700U;
constexpr std::uint32_t arabic_action_shift = 8U;
constexpr std::uint32_t hangul_feature_mask = 0x00001800U;
constexpr std::uint32_t hangul_feature_shift = 11U;

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
        static_cast<std::uint32_t>(input.size())};
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
    font_error* error) noexcept {
    glyph_count = 0U;
    open_type_shape_run_requirements requirements{};
    if (!try_get_open_type_shape_run_requirements(
            font, input, requirements, error)) {
        return false;
    }
    const bool arabic_joining = uses_arabic_joining(options.script);
    const bool hangul = uses_hangul(options.script);
    const std::size_t glyph_capacity = hangul
        ? requirements.glyph_capacity
        : requirements.initial_glyph_count;
    if (glyph_storage.size() < glyph_capacity ||
        scratch.grapheme_clusters.size() < requirements.grapheme_capacity ||
        scratch.gsub_lookups.size() < requirements.gsub_lookup_capacity ||
        scratch.gpos_lookups.size() < requirements.gpos_lookup_capacity ||
        (arabic_joining && scratch.arabic_actions.size() <
            requirements.script_action_capacity) ||
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
    for (std::uint32_t cluster_index = 0U;
         cluster_index < grapheme_count;
         ++cluster_index) {
        const auto cluster = scratch.grapheme_clusters[cluster_index];
        for (std::uint32_t offset = 0U; offset < cluster.scalar_count; ++offset) {
            const std::size_t scalar_index = cluster.scalar_index + offset;
            std::uint16_t glyph = 0U;
            if (!font.try_get_glyph_index(
                    input[scalar_index].code_point, glyph)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            glyph_storage[scalar_index] = shaping_glyph{
                glyph,
                input[scalar_index].code_point,
                static_cast<std::int32_t>(cluster.input_index)};
        }
    }
    glyph_count = requirements.initial_glyph_count;

    if (hangul && !try_prepare_open_type_hangul(
            font, glyph_storage, glyph_count, error)) {
        glyph_count = 0U;
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
                &action_error)) {
            set_error(error, font_error::invalid_argument);
            glyph_count = 0U;
            return false;
        }
        for (std::uint32_t index = 0U; index < action_count; ++index) {
            set_arabic_action(glyph_storage[index], scratch.arabic_actions[index]);
        }
    }

    open_type_layout_table_view gsub{};
    open_type_layout_table_view gpos{};
    std::size_t gsub_length = 0U;
    std::size_t gpos_length = 0U;
    if (!try_get_layout(font, gsub_tag, gsub, gsub_length, error) ||
        !try_get_layout(font, gpos_tag, gpos, gpos_length, error)) {
        return false;
    }
    open_type_gdef_view gdef{};
    bool has_gdef = false;
    if (!try_get_gdef(
            font, gsub_length, gpos_length, gdef, has_gdef, error)) {
        return false;
    }
    const open_type_gdef_view* gdef_pointer = has_gdef ? &gdef : nullptr;

    if (gsub.lookup_count() != 0U) {
        std::uint32_t lookup_count = 0U;
        if (!gsub.try_select_lookups(
                options.script,
                options.language,
                options.requested_features,
                scratch.gsub_lookups.first(gsub.lookup_count()),
                lookup_count,
                error)) {
            return false;
        }
        for (std::uint32_t index = 0U; index < lookup_count; ++index) {
            bool applied = false;
            if (!try_apply_open_type_gsub_lookup(
                    gsub,
                    scratch.gsub_lookups[index],
                    glyph_storage,
                    glyph_count,
                    open_type_gsub_apply_options{
                        gdef_pointer, options.alternate_value},
                    applied,
                    error)) {
                return false;
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
        if (!gpos.try_select_lookups(
                options.script,
                options.language,
                options.requested_features,
                scratch.gpos_lookups.first(gpos.lookup_count()),
                lookup_count,
                error)) {
            return false;
        }
        const auto glyphs = glyph_storage.first(glyph_count);
        const auto attachments = scratch.attachments.first(glyph_count);
        for (std::uint32_t index = 0U; index < lookup_count; ++index) {
            bool applied = false;
            if (!try_apply_open_type_gpos_lookup(
                    gpos,
                    scratch.gpos_lookups[index],
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

#include "progpu_native_text.hpp"
#include "progpu_native_open_type_complex_internal.hpp"
#include "progpu_native_vowel_constraints_internal.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Native port of ProGPU-owned GlyphSubstitutionBuffer's common preprocessing
// stages. Work is linear except stable combining-mark insertion ordering,
// whose adversarial reverse-class case is O(M^2); all storage is caller-owned.

namespace progpu::native::text {
namespace {

constexpr open_type_tag hebrew =
    open_type_tag::from_chars('h', 'e', 'b', 'r');
constexpr open_type_tag hangul =
    open_type_tag::from_chars('h', 'a', 'n', 'g');
constexpr open_type_tag thai =
    open_type_tag::from_chars('t', 'h', 'a', 'i');
constexpr open_type_tag lao =
    open_type_tag::from_chars('l', 'a', 'o', ' ');

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

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool has_flag(
    shaping_buffer_flags value,
    shaping_buffer_flags flag) noexcept {
    return (static_cast<std::uint8_t>(value) &
        static_cast<std::uint8_t>(flag)) != 0U;
}

bool is_mark(std::uint32_t code_point) noexcept {
    const auto grapheme = get_unicode_grapheme_break_class(code_point);
    return grapheme == unicode_grapheme_break_class::extend ||
        grapheme == unicode_grapheme_break_class::spacing_mark ||
        get_unicode_canonical_combining_class(code_point) != 0U;
}

int modified_combining_class_core(std::uint32_t code_point) noexcept {
    if (code_point == 0x1A60U || code_point == 0x0FC6U) {
        return 254;
    }
    if (code_point == 0x0F39U) {
        return 127;
    }
    switch (get_unicode_canonical_combining_class(code_point)) {
        case 10U: return 22;
        case 11U: return 15;
        case 12U: return 16;
        case 13U: return 17;
        case 14U: return 23;
        case 15U: return 18;
        case 16U: return 19;
        case 17U: return 20;
        case 18U: return 21;
        case 19U: return 14;
        case 20U: return 24;
        case 21U: return 12;
        case 22U: return 25;
        case 23U: return 13;
        case 24U: return 10;
        case 25U: return 11;
        case 27U: return 28;
        case 28U: return 29;
        case 29U: return 30;
        case 30U: return 31;
        case 31U: return 32;
        case 32U: return 33;
        case 33U: return 27;
        case 84U: return 4;
        case 91U: return 5;
        case 103U: return 3;
        case 130U: return 132;
        case 132U: return 131;
        default:
            return get_unicode_canonical_combining_class(code_point);
    }
}

bool is_arabic_modifier(std::uint32_t code_point) noexcept {
    switch (code_point) {
        case 0x0654U: case 0x0655U: case 0x0658U: case 0x06DCU:
        case 0x06E3U: case 0x06E7U: case 0x06E8U: case 0x08CAU:
        case 0x08CBU: case 0x08CDU: case 0x08CEU: case 0x08CFU:
        case 0x08D3U: case 0x08F3U:
            return true;
        default:
            return false;
    }
}

void reorder_arabic_modifiers(
    std::span<shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end) noexcept {
    for (int canonical = 220; canonical <= 230; canonical += 10) {
        std::size_t first = start;
        while (first < end &&
            modified_combining_class_core(glyphs[first].code_point) < canonical) {
            ++first;
        }
        if (first == end ||
            modified_combining_class_core(glyphs[first].code_point) != canonical) {
            continue;
        }
        std::size_t last = first;
        while (last < end &&
            modified_combining_class_core(glyphs[last].code_point) == canonical &&
            is_arabic_modifier(glyphs[last].code_point)) {
            ++last;
        }
        if (last == first) {
            continue;
        }
        std::rotate(
            glyphs.begin() + static_cast<std::ptrdiff_t>(start),
            glyphs.begin() + static_cast<std::ptrdiff_t>(first),
            glyphs.begin() + static_cast<std::ptrdiff_t>(last));
        start += last - first;
    }
}

void reorder_modified_combining_marks(
    std::span<shaping_glyph> glyphs,
    open_type_tag script,
    shaping_cluster_level cluster_level) noexcept {
    std::size_t start = 0U;
    while (start < glyphs.size()) {
        while (start < glyphs.size() &&
            modified_combining_class_core(glyphs[start].code_point) == 0) {
            ++start;
        }
        std::size_t end = start;
        while (end < glyphs.size() &&
            modified_combining_class_core(glyphs[end].code_point) != 0) {
            ++end;
        }
        for (std::size_t index = start + 1U; index < end; ++index) {
            const shaping_glyph value = glyphs[index];
            const int value_class = modified_combining_class_core(value.code_point);
            std::size_t destination = index;
            std::int32_t crossed_cluster =
                std::numeric_limits<std::int32_t>::max();
            while (destination > start &&
                modified_combining_class_core(
                    glyphs[destination - 1U].code_point) > value_class) {
                crossed_cluster = std::min(
                    crossed_cluster, glyphs[destination - 1U].cluster);
                glyphs[destination] = glyphs[destination - 1U];
                --destination;
            }
            glyphs[destination] = value;
            if (cluster_level == shaping_cluster_level::monotone_characters &&
                destination < index) {
                for (std::size_t crossed = destination + 1U;
                     crossed <= index;
                     ++crossed) {
                    glyphs[crossed].cluster = crossed_cluster;
                }
            }
        }
        if (uses_arabic_joining(script)) {
            reorder_arabic_modifiers(glyphs, start, end);
        }
        start = end == start ? end + 1U : end;
    }
}

bool is_hebrew_starter(std::uint32_t code_point) noexcept {
    return (code_point >= 0x05D0U && code_point <= 0x05EAU) ||
        (code_point >= 0xFB1DU && code_point <= 0xFB4EU) ||
        code_point == 0x05F2U;
}

bool try_compose_hebrew(
    std::uint32_t first,
    std::uint32_t second,
    std::uint32_t& composed) noexcept {
    composed = 0U;
    if (second == 0x05BCU && first >= 0x05D0U && first <= 0x05EAU) {
        constexpr std::array<std::uint16_t, 27U> forms{
            0xFB30U, 0xFB31U, 0xFB32U, 0xFB33U, 0xFB34U, 0xFB35U,
            0xFB36U, 0U, 0xFB38U, 0xFB39U, 0xFB3AU, 0xFB3BU, 0xFB3CU,
            0U, 0xFB3EU, 0U, 0xFB40U, 0xFB41U, 0U, 0xFB43U, 0xFB44U,
            0U, 0xFB46U, 0xFB47U, 0xFB48U, 0xFB49U, 0xFB4AU};
        composed = forms[first - 0x05D0U];
        return composed != 0U;
    }
    if (first == 0x05D9U && second == 0x05B4U) composed = 0xFB1DU;
    else if (first == 0x05F2U && second == 0x05B7U) composed = 0xFB1FU;
    else if (first == 0x05D0U && second == 0x05B7U) composed = 0xFB2EU;
    else if (first == 0x05D0U && second == 0x05B8U) composed = 0xFB2FU;
    else if (first == 0x05D5U && second == 0x05B9U) composed = 0xFB4BU;
    else if (first == 0x05D1U && second == 0x05BFU) composed = 0xFB4CU;
    else if (first == 0x05DBU && second == 0x05BFU) composed = 0xFB4DU;
    else if (first == 0x05E4U && second == 0x05BFU) composed = 0xFB4EU;
    else if (first == 0x05E9U && second == 0x05C1U) composed = 0xFB2AU;
    else if (first == 0x05E9U && second == 0x05C2U) composed = 0xFB2BU;
    else if ((first == 0xFB49U && second == 0x05C1U) ||
        (first == 0xFB2AU && second == 0x05BCU)) composed = 0xFB2CU;
    else if ((first == 0xFB49U && second == 0x05C2U) ||
        (first == 0xFB2BU && second == 0x05BCU)) composed = 0xFB2DU;
    return composed != 0U;
}

void erase_at(
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::size_t index) noexcept {
    std::move(
        storage.begin() + static_cast<std::ptrdiff_t>(index + 1U),
        storage.begin() + static_cast<std::ptrdiff_t>(count),
        storage.begin() + static_cast<std::ptrdiff_t>(index));
    --count;
}

bool compose_hebrew_forms(
    const sfnt_font_view& font,
    std::span<shaping_glyph> storage,
    std::uint32_t& count) noexcept {
    for (std::size_t start = 0U; start < count;) {
        const std::int32_t cluster = storage[start].cluster;
        std::size_t end = start + 1U;
        while (end < count && storage[end].cluster == cluster) {
            ++end;
        }
        std::size_t starter = start;
        while (starter < end &&
            !is_hebrew_starter(storage[starter].code_point)) {
            ++starter;
        }
        if (starter < end) {
            for (std::size_t index = starter + 1U; index < end;) {
                std::uint32_t composed = 0U;
                if (!try_compose_hebrew(
                        storage[starter].code_point,
                        storage[index].code_point,
                        composed)) {
                    ++index;
                    continue;
                }
                std::uint16_t glyph = 0U;
                if (!font.try_get_glyph_index(composed, glyph)) {
                    return false;
                }
                if (glyph == 0U) {
                    ++index;
                    continue;
                }
                storage[starter].code_point = composed;
                storage[starter].glyph_id = glyph;
                erase_at(storage, count, index);
                --end;
            }
        }
        start = end;
    }
    return true;
}

bool is_thai_lao_above_mark(std::uint32_t code_point) noexcept {
    const std::uint32_t thai_code_point = code_point & ~0x80U;
    return (thai_code_point >= 0x0E34U && thai_code_point <= 0x0E37U) ||
        (thai_code_point >= 0x0E47U && thai_code_point <= 0x0E4EU) ||
        thai_code_point == 0x0E31U || thai_code_point == 0x0E3BU;
}

std::int32_t minimum_cluster(
    std::span<const shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end) noexcept {
    std::int32_t result = glyphs[start].cluster;
    for (std::size_t index = start + 1U; index < end; ++index) {
        result = std::min(result, glyphs[index].cluster);
    }
    return result;
}

bool prepare_thai_lao(
    const sfnt_font_view& font,
    open_type_tag script,
    shaping_cluster_level cluster_level,
    std::span<shaping_glyph> storage,
    std::uint32_t& count) noexcept {
    if (script != thai && script != lao) {
        return true;
    }
    const std::uint32_t sara_am = script == thai ? 0x0E33U : 0x0EB3U;
    const std::uint32_t nikhahit = script == thai ? 0x0E4DU : 0x0ECDU;
    const std::uint32_t sara_aa = sara_am - 1U;
    std::uint16_t nikhahit_glyph = 0U;
    std::uint16_t sara_aa_glyph = 0U;
    if (!font.try_get_glyph_index(nikhahit, nikhahit_glyph) ||
        !font.try_get_glyph_index(sara_aa, sara_aa_glyph)) {
        return false;
    }
    for (std::size_t index = 0U; index < count; ++index) {
        if (storage[index].code_point != sara_am) {
            continue;
        }
        const shaping_glyph source = storage[index];
        std::move_backward(
            storage.begin() + static_cast<std::ptrdiff_t>(index + 1U),
            storage.begin() + static_cast<std::ptrdiff_t>(count),
            storage.begin() + static_cast<std::ptrdiff_t>(count + 1U));
        ++count;
        storage[index] = shaping_glyph{
            nikhahit_glyph, nikhahit, source.cluster};
        storage[index + 1U] = shaping_glyph{
            sara_aa_glyph, sara_aa, source.cluster};
        std::size_t start = index;
        while (start > 0U &&
            is_thai_lao_above_mark(storage[start - 1U].code_point)) {
            --start;
        }
        if (start < index) {
            const shaping_glyph moved = storage[index];
            std::move_backward(
                storage.begin() + static_cast<std::ptrdiff_t>(start),
                storage.begin() + static_cast<std::ptrdiff_t>(index),
                storage.begin() + static_cast<std::ptrdiff_t>(index + 1U));
            storage[start] = moved;
        }
        const std::size_t merge_start =
            cluster_level == shaping_cluster_level::monotone_characters
            ? start
            : start == 0U ? 0U : start - 1U;
        const std::size_t merge_end = index + 2U;
        const std::int32_t cluster = minimum_cluster(
            storage, merge_start, merge_end);
        for (std::size_t merge = merge_start; merge < merge_end; ++merge) {
            storage[merge].cluster = cluster;
        }
        ++index;
    }
    return true;
}

} // namespace

int complex_detail::modified_combining_class(
    std::uint32_t code_point) noexcept {
    return modified_combining_class_core(code_point);
}

bool try_preprocess_open_type_glyphs(
    const sfnt_font_view& font,
    open_type_tag script,
    shaping_cluster_level cluster_level,
    shaping_buffer_flags buffer_flags,
    bool compose_hebrew_presentation_forms,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    font_error* error) noexcept {
    if (glyph_count > glyph_storage.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::size_t additions = 0U;
    for (std::size_t index = 0U; index < glyph_count; ++index) {
        if (script == hangul &&
            glyph_storage[index].code_point >= 0xAC00U &&
            glyph_storage[index].code_point <= 0xD7A3U) {
            additions += 2U;
        }
        if ((script == thai && glyph_storage[index].code_point == 0x0E33U) ||
            (script == lao && glyph_storage[index].code_point == 0x0EB3U)) {
            ++additions;
        }
    }
    additions += detail::count_vowel_constraint_insertions(
        script, glyph_storage.first(glyph_count));
    std::uint16_t dotted_circle = 0U;
    const bool insert_dotted_circle = glyph_count != 0U &&
        has_flag(buffer_flags, shaping_buffer_flags::beginning_of_text) &&
        !has_flag(
            buffer_flags,
            shaping_buffer_flags::do_not_insert_dotted_circle) &&
        is_mark(glyph_storage[0U].code_point);
    if (insert_dotted_circle) {
        if (!font.try_get_glyph_index(0x25CCU, dotted_circle)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        additions += dotted_circle == 0U ? 0U : 1U;
    }
    if (additions > glyph_storage.size() - glyph_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    if (insert_dotted_circle && dotted_circle != 0U) {
        std::move_backward(
            glyph_storage.begin(),
            glyph_storage.begin() + static_cast<std::ptrdiff_t>(glyph_count),
            glyph_storage.begin() + static_cast<std::ptrdiff_t>(
                glyph_count + 1U));
        glyph_storage[0U] = shaping_glyph{
            dotted_circle, 0x25CCU, glyph_storage[1U].cluster};
        ++glyph_count;
    }
    if (script == hangul && !try_prepare_open_type_hangul(
            font, glyph_storage, glyph_count, error)) {
        return false;
    }
    reorder_modified_combining_marks(
        glyph_storage.first(glyph_count), script, cluster_level);
    if (script == hebrew && compose_hebrew_presentation_forms &&
        !compose_hebrew_forms(font, glyph_storage, glyph_count)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    if (!detail::try_apply_vowel_constraints(
            font, script, glyph_storage, glyph_count, error)) {
        return false;
    }
    if (!prepare_thai_lao(
            font,
            script,
            cluster_level,
            glyph_storage,
            glyph_count)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text

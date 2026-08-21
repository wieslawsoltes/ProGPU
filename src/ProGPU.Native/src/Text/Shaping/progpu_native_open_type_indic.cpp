#include "progpu_native_open_type_complex_internal.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Native port of ProGPU-owned Indic preparation and reorder contracts. The
// caller owns all storage; private state is packed into shaping_glyph::flags
// and scrubbed by the uniform-run guard before the public result is returned.

namespace progpu::native::text::complex_detail {
namespace {

constexpr std::uint8_t consonant_syllable = 0U;
constexpr std::uint8_t vowel_syllable = 1U;
constexpr std::uint8_t standalone_cluster = 2U;
constexpr std::uint8_t broken_cluster = 4U;

constexpr std::uint8_t consonant = 1U;
constexpr std::uint8_t vowel = 2U;
constexpr std::uint8_t nukta = 3U;
constexpr std::uint8_t halant = 4U;
constexpr std::uint8_t zwnj = 5U;
constexpr std::uint8_t zwj = 6U;
constexpr std::uint8_t matra = 7U;
constexpr std::uint8_t syllable_modifier = 8U;
constexpr std::uint8_t placeholder = 10U;
constexpr std::uint8_t dotted_circle = 11U;
constexpr std::uint8_t register_shifter = 12U;
constexpr std::uint8_t matra_post = 13U;
constexpr std::uint8_t repha = 14U;
constexpr std::uint8_t ra = 15U;
constexpr std::uint8_t consonant_medial = 16U;
constexpr std::uint8_t consonant_with_stacker = 18U;

constexpr std::uint8_t position_ra_reph = 1U;
constexpr std::uint8_t position_pre_matra = 2U;
constexpr std::uint8_t position_pre_consonant = 3U;
constexpr std::uint8_t position_base = 4U;
constexpr std::uint8_t position_after_main = 5U;
constexpr std::uint8_t position_before_sub = 7U;
constexpr std::uint8_t position_below_consonant = 8U;
constexpr std::uint8_t position_after_sub = 9U;
constexpr std::uint8_t position_before_post = 10U;
constexpr std::uint8_t position_post_consonant = 11U;
constexpr std::uint8_t position_after_post = 12U;
constexpr std::uint8_t position_syllable_modifier = 13U;
constexpr std::uint8_t position_end = 14U;

constexpr std::uint8_t rphf_mask = 1U;
constexpr std::uint8_t pref_mask = 2U;
constexpr std::uint8_t blwf_mask = 4U;
constexpr std::uint8_t abvf_mask = 8U;
constexpr std::uint8_t half_mask = 16U;
constexpr std::uint8_t pstf_mask = 32U;
constexpr std::uint8_t init_mask = 64U;

constexpr auto rphf_tag = open_type_tag::from_chars('r', 'p', 'h', 'f');
constexpr auto pref_tag = open_type_tag::from_chars('p', 'r', 'e', 'f');
constexpr auto blwf_tag = open_type_tag::from_chars('b', 'l', 'w', 'f');
constexpr auto pstf_tag = open_type_tag::from_chars('p', 's', 't', 'f');
constexpr auto vatu_tag = open_type_tag::from_chars('v', 'a', 't', 'u');

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool has_flag(shaping_buffer_flags value, shaping_buffer_flags flag) noexcept {
    return (static_cast<std::uint8_t>(value) &
        static_cast<std::uint8_t>(flag)) != 0U;
}

bool is_joiner(const shaping_glyph& glyph) noexcept {
    const auto value = category(glyph);
    return !ligated(glyph) && (value == zwj || value == zwnj);
}

bool is_consonant(const shaping_glyph& glyph) noexcept {
    const auto value = category(glyph);
    return !ligated(glyph) &&
        (value == consonant || value == consonant_with_stacker ||
            value == ra || value == consonant_medial || value == vowel ||
            value == placeholder || value == dotted_circle);
}

bool is_halant(const shaping_glyph& glyph) noexcept {
    return !ligated(glyph) && category(glyph) == halant;
}

bool has_feature(const shaping_glyph& glyph, std::uint8_t mask) noexcept {
    return (get_field(glyph, feature_mask, feature_shift) & mask) != 0U;
}

bool would_substitute(
    indic_substitution_probe probe,
    open_type_tag feature,
    std::span<const std::uint16_t> glyphs) noexcept {
    return probe.would_substitute != nullptr &&
        probe.would_substitute(probe.context, feature, glyphs);
}

void merge_cluster(
    std::span<shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end) noexcept {
    if (start >= end) {
        return;
    }
    const auto first = glyphs[start].cluster;
    const auto last = glyphs[end - 1U].cluster;
    while (start > 0U && glyphs[start - 1U].cluster == first) {
        --start;
    }
    while (end < glyphs.size() && glyphs[end].cluster == last) {
        ++end;
    }
    auto minimum = std::numeric_limits<std::int32_t>::max();
    for (std::size_t index = start; index < end; ++index) {
        minimum = std::min(minimum, glyphs[index].cluster);
    }
    for (std::size_t index = start; index < end; ++index) {
        glyphs[index].cluster = minimum;
    }
}

void mark_syllables_unsafe(std::span<shaping_glyph> glyphs) noexcept {
    for (std::size_t start = 0U; start < glyphs.size();) {
        const auto current = syllable(glyphs[start]);
        std::size_t end = start + 1U;
        while (end < glyphs.size() && syllable(glyphs[end]) == current) {
            ++end;
        }
        if (end - start >= 2U) {
            auto minimum = std::numeric_limits<std::int32_t>::max();
            for (std::size_t index = start; index < end; ++index) {
                minimum = std::min(minimum, glyphs[index].cluster);
            }
            for (std::size_t index = start; index < end; ++index) {
                if (glyphs[index].cluster != minimum) {
                    glyphs[index].flags = static_cast<shaping_glyph_flags>(
                        raw_flags(glyphs[index]) |
                        static_cast<std::uint32_t>(
                            shaping_glyph_flags::unsafe_to_break) |
                        static_cast<std::uint32_t>(
                            shaping_glyph_flags::unsafe_to_concat));
                }
            }
        }
        start = end;
    }
}

void attach_mark_positions(
    std::span<shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end,
    std::size_t base) noexcept {
    std::uint8_t last_position = 0U;
    for (std::size_t index = start; index < end; ++index) {
        const auto value = category(glyphs[index]);
        if (is_joiner(glyphs[index]) || value == nukta ||
            value == register_shifter || value == consonant_medial ||
            value == halant) {
            set_position(glyphs[index], last_position);
            if (value == halant &&
                position(glyphs[index]) == position_pre_matra) {
                for (std::size_t prior = index; prior > start; --prior) {
                    if (position(glyphs[prior - 1U]) != position_pre_matra) {
                        set_position(glyphs[index],
                            position(glyphs[prior - 1U]));
                        break;
                    }
                }
            }
        } else if (position(glyphs[index]) != position_syllable_modifier) {
            if (value == matra_post && index > start &&
                category(glyphs[index - 1U]) == syllable_modifier) {
                set_position(glyphs[index - 1U], position(glyphs[index]));
            }
            last_position = position(glyphs[index]);
        }
    }
    std::size_t last = std::min(base, end - 1U);
    for (std::size_t index = last + 1U; index < end; ++index) {
        if (is_consonant(glyphs[index])) {
            for (std::size_t mark = last + 1U; mark < index; ++mark) {
                if (position(glyphs[mark]) < position_syllable_modifier) {
                    set_position(glyphs[mark], position(glyphs[index]));
                }
            }
            last = index;
        } else if (category(glyphs[index]) == matra ||
            category(glyphs[index]) == matra_post) {
            last = index;
        }
    }
}

void stable_sort_positions(
    std::span<shaping_glyph> glyphs,
    std::span<std::uint32_t> original_order,
    std::size_t start,
    std::size_t end) noexcept {
    for (std::size_t index = start + 1U; index < end; ++index) {
        auto value = glyphs[index];
        const auto value_order = original_order[index];
        std::size_t insertion = index;
        while (insertion > start &&
            position(glyphs[insertion - 1U]) > position(value)) {
            glyphs[insertion] = glyphs[insertion - 1U];
            original_order[insertion] = original_order[insertion - 1U];
            --insertion;
        }
        glyphs[insertion] = value;
        original_order[insertion] = value_order;
    }
}

void reverse_left_matras(
    std::span<shaping_glyph> glyphs,
    std::span<std::uint32_t> original_order,
    std::size_t start,
    std::size_t end) noexcept {
    std::size_t first = end;
    std::size_t last = end;
    for (std::size_t index = start; index < end; ++index) {
        if (position(glyphs[index]) == position_base) {
            break;
        }
        if (position(glyphs[index]) == position_pre_matra) {
            if (first == end) {
                first = index;
            }
            last = index;
        }
    }
    if (first >= last) {
        return;
    }
    std::reverse(glyphs.begin() + static_cast<std::ptrdiff_t>(first),
        glyphs.begin() + static_cast<std::ptrdiff_t>(last + 1U));
    std::reverse(original_order.begin() + static_cast<std::ptrdiff_t>(first),
        original_order.begin() + static_cast<std::ptrdiff_t>(last + 1U));
    std::size_t group_start = first;
    for (std::size_t index = first; index <= last; ++index) {
        const auto value = category(glyphs[index]);
        if (value != matra && value != matra_post) {
            continue;
        }
        std::reverse(
            glyphs.begin() + static_cast<std::ptrdiff_t>(group_start),
            glyphs.begin() + static_cast<std::ptrdiff_t>(index + 1U));
        std::reverse(
            original_order.begin() + static_cast<std::ptrdiff_t>(group_start),
            original_order.begin() + static_cast<std::ptrdiff_t>(index + 1U));
        group_start = index + 1U;
    }
}

void merge_sort_clusters(
    std::span<shaping_glyph> glyphs,
    std::span<std::uint32_t> original_order,
    std::size_t start,
    std::size_t end,
    std::size_t base,
    bool old_spec) noexcept {
    if (base >= end) {
        return;
    }
    if (old_spec || end - start > 127U) {
        merge_cluster(glyphs, base, end);
        return;
    }

    constexpr auto visited = std::numeric_limits<std::uint32_t>::max();
    for (std::size_t index = base; index < end; ++index) {
        if (original_order[index] == visited) {
            continue;
        }
        auto minimum = index;
        auto maximum = index;
        auto cursor = start + original_order[index];
        while (cursor != index && cursor >= start && cursor < end) {
            minimum = std::min(minimum, cursor);
            maximum = std::max(maximum, cursor);
            const auto next = start + original_order[cursor];
            original_order[cursor] = visited;
            cursor = next;
        }
        merge_cluster(glyphs, std::max(base, minimum), maximum + 1U);
    }
}

open_type_tag legacy_script(open_type_tag script) noexcept {
    if ((script.value & 0xFFU) == static_cast<std::uint32_t>('2')) {
        const auto a = static_cast<char>(script.value >> 24U);
        const auto b = static_cast<char>(script.value >> 16U);
        const auto c = static_cast<char>(script.value >> 8U);
        if (a == 'd' && b == 'e' && c == 'v') return open_type_tag::from_chars('d','e','v','a');
        if (a == 'b' && b == 'n' && c == 'g') return open_type_tag::from_chars('b','e','n','g');
        if (a == 'g' && b == 'u' && c == 'r') return open_type_tag::from_chars('g','u','r','u');
        if (a == 'g' && b == 'j' && c == 'r') return open_type_tag::from_chars('g','u','j','r');
        if (a == 'o' && b == 'r' && c == 'y') return open_type_tag::from_chars('o','r','y','a');
        if (a == 't' && b == 'm' && c == 'l') return open_type_tag::from_chars('t','a','m','l');
        if (a == 't' && b == 'e' && c == 'l') return open_type_tag::from_chars('t','e','l','u');
        if (a == 'k' && b == 'n' && c == 'd') return open_type_tag::from_chars('k','n','d','a');
        if (a == 'm' && b == 'l' && c == 'm') return open_type_tag::from_chars('m','l','y','m');
    }
    return script;
}

std::uint8_t reph_position(open_type_tag script) noexcept {
    script = legacy_script(script);
    if (script == open_type_tag::from_chars('b','e','n','g')) return position_after_sub;
    if (script == open_type_tag::from_chars('g','u','r','u')) return position_before_sub;
    if (script == open_type_tag::from_chars('o','r','y','a') ||
        script == open_type_tag::from_chars('m','l','y','m')) return position_after_main;
    if (script == open_type_tag::from_chars('t','a','m','l') ||
        script == open_type_tag::from_chars('t','e','l','u') ||
        script == open_type_tag::from_chars('k','n','d','a')) return position_after_post;
    return position_before_post;
}

std::uint32_t virama_code_point(open_type_tag script) noexcept {
    script = legacy_script(script);
    if (script == open_type_tag::from_chars('d','e','v','a')) return 0x094DU;
    if (script == open_type_tag::from_chars('b','e','n','g')) return 0x09CDU;
    if (script == open_type_tag::from_chars('g','u','r','u')) return 0x0A4DU;
    if (script == open_type_tag::from_chars('g','u','j','r')) return 0x0ACDU;
    if (script == open_type_tag::from_chars('o','r','y','a')) return 0x0B4DU;
    if (script == open_type_tag::from_chars('t','a','m','l')) return 0x0BCDU;
    if (script == open_type_tag::from_chars('t','e','l','u')) return 0x0C4DU;
    if (script == open_type_tag::from_chars('k','n','d','a')) return 0x0CCDU;
    if (script == open_type_tag::from_chars('m','l','y','m')) return 0x0D4DU;
    return 0U;
}

enum class reph_mode : std::uint8_t { implicit, explicit_mode, logical };

reph_mode get_reph_mode(open_type_tag script) noexcept {
    script = legacy_script(script);
    if (script == open_type_tag::from_chars('t','e','l','u')) {
        return reph_mode::explicit_mode;
    }
    if (script == open_type_tag::from_chars('m','l','y','m')) {
        return reph_mode::logical;
    }
    return reph_mode::implicit;
}

bool below_mode_is_pre_and_post(open_type_tag script) noexcept {
    script = legacy_script(script);
    return script != open_type_tag::from_chars('t','e','l','u') &&
        script != open_type_tag::from_chars('k','n','d','a');
}

bool is_init_continuation(std::uint32_t code_point) noexcept {
    switch (get_unicode_general_category(code_point)) {
        case unicode_general_category::uppercase_letter:
        case unicode_general_category::lowercase_letter:
        case unicode_general_category::titlecase_letter:
        case unicode_general_category::modifier_letter:
        case unicode_general_category::other_letter:
        case unicode_general_category::nonspacing_mark:
        case unicode_general_category::spacing_combining_mark:
        case unicode_general_category::enclosing_mark:
        case unicode_general_category::format:
        case unicode_general_category::surrogate:
        case unicode_general_category::private_use:
        case unicode_general_category::other_not_assigned:
            return true;
        default:
            return false;
    }
}

void mark_dependency(
    std::span<shaping_glyph> glyphs,
    std::size_t first,
    std::size_t last) noexcept {
    if (first >= last || last >= glyphs.size()) {
        return;
    }
    auto minimum = glyphs[first].cluster;
    for (std::size_t index = first + 1U; index <= last; ++index) {
        minimum = std::min(minimum, glyphs[index].cluster);
    }
    constexpr auto flags =
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    for (std::size_t index = first; index <= last; ++index) {
        if (glyphs[index].cluster != minimum) {
            glyphs[index].flags = static_cast<shaping_glyph_flags>(
                raw_flags(glyphs[index]) | flags);
        }
    }
}

void move_glyph(
    std::span<shaping_glyph> glyphs,
    std::size_t source,
    std::size_t destination) noexcept {
    if (source == destination || source >= glyphs.size() ||
        destination >= glyphs.size()) {
        return;
    }
    if (source < destination) {
        std::rotate(
            glyphs.begin() + static_cast<std::ptrdiff_t>(source),
            glyphs.begin() + static_cast<std::ptrdiff_t>(source + 1U),
            glyphs.begin() + static_cast<std::ptrdiff_t>(destination + 1U));
    } else {
        std::rotate(
            glyphs.begin() + static_cast<std::ptrdiff_t>(destination),
            glyphs.begin() + static_cast<std::ptrdiff_t>(source),
            glyphs.begin() + static_cast<std::ptrdiff_t>(source + 1U));
    }
}

std::size_t find_reph_destination(
    std::span<const shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end,
    std::size_t base,
    open_type_tag script) noexcept {
    const auto desired = reph_position(script);
    if (desired != position_after_post) {
        auto explicit_halant = start + 1U;
        while (explicit_halant < base &&
            !is_halant(glyphs[explicit_halant])) {
            ++explicit_halant;
        }
        if (explicit_halant < base) {
            if (explicit_halant + 1U < base &&
                is_joiner(glyphs[explicit_halant + 1U])) {
                ++explicit_halant;
            }
            return explicit_halant;
        }
    }
    if (desired == position_after_main) {
        auto destination = base;
        while (destination + 1U < end &&
            position(glyphs[destination + 1U]) <= position_after_main) {
            ++destination;
        }
        if (destination < end) {
            return destination;
        }
    }
    if (desired == position_after_sub) {
        auto destination = base;
        while (destination + 1U < end &&
            position(glyphs[destination + 1U]) != position_post_consonant &&
            position(glyphs[destination + 1U]) != position_after_post &&
            position(glyphs[destination + 1U]) !=
                position_syllable_modifier) {
            ++destination;
        }
        if (destination < end) {
            return destination;
        }
    }

    auto destination = end - 1U;
    while (destination > start &&
        position(glyphs[destination]) == position_syllable_modifier) {
        --destination;
    }
    if (is_halant(glyphs[destination])) {
        for (auto index = base + 1U; index < destination; ++index) {
            const auto value = category(glyphs[index]);
            if (value == matra || value == matra_post) {
                --destination;
                break;
            }
        }
    }
    return destination;
}

void update_consonant_positions(
    const sfnt_font_view& font,
    open_type_tag unicode_script,
    std::span<shaping_glyph> glyphs,
    indic_substitution_probe probe) noexcept {
    const auto virama = virama_code_point(unicode_script);
    std::uint16_t virama_glyph = 0U;
    if (virama == 0U ||
        !font.try_get_glyph_index(virama, virama_glyph) ||
        virama_glyph == 0U) {
        return;
    }
    for (auto& glyph : glyphs) {
        if (position(glyph) != position_base || glyph.glyph_id > 0xFFFFU) {
            continue;
        }
        const auto glyph_id = static_cast<std::uint16_t>(glyph.glyph_id);
        const std::array<std::uint16_t, 2U> first{virama_glyph, glyph_id};
        const std::array<std::uint16_t, 2U> second{glyph_id, virama_glyph};
        const auto first_span = std::span<const std::uint16_t>{
            first.data(), first.size()};
        const auto second_span = std::span<const std::uint16_t>{
            second.data(), second.size()};
        const bool below = would_substitute(probe, blwf_tag, first_span) ||
            would_substitute(probe, blwf_tag, second_span) ||
            would_substitute(probe, vatu_tag, first_span) ||
            would_substitute(probe, vatu_tag, second_span);
        if (below) {
            set_position(glyph, position_below_consonant);
        } else if (would_substitute(probe, pstf_tag, first_span) ||
            would_substitute(probe, pstf_tag, second_span) ||
            would_substitute(probe, pref_tag, first_span) ||
            would_substitute(probe, pref_tag, second_span)) {
            set_position(glyph, position_post_consonant);
        }
    }
}

void initial_reorder_syllable(
    std::span<shaping_glyph> glyphs,
    std::span<std::uint32_t> original_order,
    std::size_t start,
    std::size_t end,
    open_type_tag unicode_script,
    bool old_spec,
    indic_substitution_probe probe) noexcept {
    const auto normalized = legacy_script(unicode_script);
    if (normalized == open_type_tag::from_chars('k','n','d','a') &&
        end - start >= 3U && category(glyphs[start]) == ra &&
        category(glyphs[start + 1U]) == halant &&
        category(glyphs[start + 2U]) == zwj) {
        merge_cluster(glyphs, start + 1U, start + 3U);
        std::swap(glyphs[start + 1U], glyphs[start + 2U]);
    }

    std::size_t limit = start;
    std::size_t base = end;
    bool has_reph = false;
    const auto mode = get_reph_mode(normalized);
    if (end - start >= 3U && category(glyphs[start]) == ra &&
        category(glyphs[start + 1U]) == halant &&
        ((mode == reph_mode::implicit &&
            !is_joiner(glyphs[start + 2U])) ||
         (mode == reph_mode::explicit_mode &&
            category(glyphs[start + 2U]) == zwj))) {
        const std::size_t length = mode == reph_mode::explicit_mode ? 3U : 2U;
        const std::array<std::uint16_t, 3U> ids{
            static_cast<std::uint16_t>(glyphs[start].glyph_id),
            static_cast<std::uint16_t>(glyphs[start + 1U].glyph_id),
            static_cast<std::uint16_t>(glyphs[start + 2U].glyph_id)};
        const auto ids_span = std::span<const std::uint16_t>{
            ids.data(), ids.size()};
        if (would_substitute(probe, rphf_tag,
                ids_span.first(2U)) ||
            (length == 3U &&
                would_substitute(probe, rphf_tag, ids_span))) {
            limit += 2U;
            while (limit < end && is_joiner(glyphs[limit])) {
                ++limit;
            }
            base = start;
            has_reph = true;
        }
    } else if (mode == reph_mode::logical &&
        category(glyphs[start]) == repha) {
        ++limit;
        while (limit < end && is_joiner(glyphs[limit])) {
            ++limit;
        }
        base = start;
        has_reph = true;
    }

    bool seen_below = false;
    for (std::size_t cursor = end; cursor > limit; --cursor) {
        const auto index = cursor - 1U;
        if (is_consonant(glyphs[index])) {
            if (position(glyphs[index]) != position_below_consonant &&
                (position(glyphs[index]) != position_post_consonant ||
                    seen_below)) {
                base = index;
                break;
            }
            if (position(glyphs[index]) == position_below_consonant) {
                seen_below = true;
            }
            base = index;
        } else if (index > start && category(glyphs[index]) == zwj &&
            category(glyphs[index - 1U]) == halant) {
            break;
        }
    }
    if (has_reph && base == start && limit - base <= 2U) {
        has_reph = false;
    }
    for (std::size_t index = start; index < base; ++index) {
        set_position(glyphs[index],
            std::min<std::uint8_t>(position_pre_consonant,
                position(glyphs[index])));
    }
    if (base < end) {
        set_position(glyphs[base], position_base);
    }
    if (has_reph) {
        set_position(glyphs[start], position_ra_reph);

    }

    if (old_spec && base < end) {
        const bool disallow_double_halants =
            normalized == open_type_tag::from_chars('k','n','d','a');
        for (std::size_t index = base + 1U; index < end; ++index) {
            if (category(glyphs[index]) != halant) {
                continue;
            }
            auto destination = end - 1U;
            while (destination > index &&
                !is_consonant(glyphs[destination]) &&
                !(disallow_double_halants &&
                    category(glyphs[destination]) == halant)) {
                --destination;
            }
            if (category(glyphs[destination]) != halant &&
                destination > index) {
                std::rotate(
                    glyphs.begin() + static_cast<std::ptrdiff_t>(index),
                    glyphs.begin() + static_cast<std::ptrdiff_t>(index + 1U),
                    glyphs.begin() + static_cast<std::ptrdiff_t>(destination + 1U));
            }
            break;
        }
    }
    attach_mark_positions(glyphs, start, end, base);
    for (std::size_t index = start; index < end; ++index) {
        original_order[index] = static_cast<std::uint32_t>(index - start);
    }
    stable_sort_positions(glyphs, original_order, start, end);
    reverse_left_matras(glyphs, original_order, start, end);
    base = end;
    for (std::size_t index = start; index < end; ++index) {
        if (position(glyphs[index]) == position_base) {
            base = index;
            break;
        }
    }
    const bool modern = !old_spec;
    merge_sort_clusters(
        glyphs, original_order, start, end, base, old_spec);
    for (std::size_t index = start;
         index < end && position(glyphs[index]) == position_ra_reph;
         ++index) {
        add_feature(glyphs[index], rphf_mask);
    }
    const std::uint8_t pre_mask = static_cast<std::uint8_t>(
        half_mask | (modern && below_mode_is_pre_and_post(normalized)
            ? blwf_mask
            : 0U));
    for (std::size_t index = start; index < base; ++index) {
        add_feature(glyphs[index], pre_mask);
    }
    if (base < end) {
        for (std::size_t index = base + 1U; index < end; ++index) {
            add_feature(glyphs[index],
                static_cast<std::uint8_t>(blwf_mask | abvf_mask | pstf_mask));
        }
    }
    if (base + 2U < end) {
        for (std::size_t index = base + 1U; index + 1U < end; ++index) {
            if (glyphs[index].glyph_id > 0xFFFFU ||
                glyphs[index + 1U].glyph_id > 0xFFFFU) {
                continue;
            }
            const std::array<std::uint16_t, 2U> ids{
                static_cast<std::uint16_t>(glyphs[index].glyph_id),
                static_cast<std::uint16_t>(glyphs[index + 1U].glyph_id)};
            const auto ids_span = std::span<const std::uint16_t>{
                ids.data(), ids.size()};
            if (!would_substitute(probe, pref_tag, ids_span)) {
                continue;
            }
            add_feature(glyphs[index], pref_mask);
            add_feature(glyphs[index + 1U], pref_mask);
            break;
        }
    }
    for (std::size_t index = start + 1U; index < end; ++index) {
        if (category(glyphs[index]) == zwnj) {
            for (std::size_t prior = index; prior-- > start;) {
                const auto features = get_field(
                    glyphs[prior], feature_mask, feature_shift);
                set_field(glyphs[prior], feature_mask, feature_shift,
                    static_cast<std::uint8_t>(features & ~half_mask));
                if (is_consonant(glyphs[prior])) {
                    break;
                }
            }
        }
    }
}

} // namespace

bool try_prepare_indic(
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t glyph_count,
    std::span<std::uint8_t> category_scratch,
    std::span<std::uint8_t> syllable_scratch,
    font_error* error) noexcept {
    if (glyph_count > glyph_storage.size() ||
        category_scratch.size() < glyph_count ||
        syllable_scratch.size() < glyph_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        const auto properties = get_unicode_indic_shaping_properties(
            glyph_storage[index].code_point);
        category_scratch[index] = properties.category;
        set_category(glyph_storage[index], properties.category);
        set_position(glyph_storage[index], properties.position);
    }
    if (!try_assign_unicode_syllables(
            unicode_syllable_machine::indic,
            category_scratch.first(glyph_count),
            {},
            syllable_scratch.first(glyph_count))) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        set_syllable(glyph_storage[index], syllable_scratch[index]);
    }
    mark_syllables_unsafe(glyph_storage.first(glyph_count));
    set_error(error, font_error::none);
    return true;
}

bool try_initial_reorder_indic(
    const sfnt_font_view& font,
    open_type_tag unicode_script,
    open_type_tag layout_script,
    shaping_buffer_flags buffer_flags,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    std::span<std::uint32_t> original_order_scratch,
    indic_substitution_probe substitution_probe,
    font_error* error) noexcept {
    if (glyph_count > glyph_storage.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    update_consonant_positions(
        font,
        unicode_script,
        glyph_storage.first(glyph_count),
        substitution_probe);

    std::uint16_t dotted_glyph = 0U;
    const bool insert_dotted = !has_flag(
        buffer_flags, shaping_buffer_flags::do_not_insert_dotted_circle) &&
        font.try_get_glyph_index(0x25CCU, dotted_glyph) &&
        dotted_glyph != 0U;
    std::uint32_t insertions = 0U;
    if (insert_dotted) {
        std::uint8_t previous = 0U;
        for (std::uint32_t index = 0U; index < glyph_count; ++index) {
            const auto current = syllable(glyph_storage[index]);
            if (current != previous && (current & 0x0FU) == broken_cluster) {
                ++insertions;
            }
            previous = current;
        }
        if (insertions > glyph_storage.size() - glyph_count) {
            set_error(error, font_error::insufficient_buffer);
            return false;
        }
    }
    if (original_order_scratch.size() < glyph_count + insertions) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    if (insertions != 0U) {
        std::uint8_t previous = 0U;
        for (std::uint32_t index = 0U; index < glyph_count; ++index) {
            const auto current = syllable(glyph_storage[index]);
            if (current == previous || (current & 0x0FU) != broken_cluster) {
                previous = current;
                continue;
            }
            previous = current;
            while (index < glyph_count && syllable(glyph_storage[index]) == previous &&
                category(glyph_storage[index]) == repha) {
                ++index;
            }
            std::move_backward(glyph_storage.begin() + index,
                glyph_storage.begin() + glyph_count,
                glyph_storage.begin() + glyph_count + 1U);
            const auto cluster = index < glyph_count
                ? glyph_storage[index + 1U].cluster
                : glyph_storage[index - 1U].cluster;
            glyph_storage[index] = shaping_glyph{
                dotted_glyph, 0x25CCU, cluster};
            set_category(glyph_storage[index], dotted_circle);
            set_position(glyph_storage[index], position_end);
            set_syllable(glyph_storage[index], previous);
            ++glyph_count;
        }
    }
    auto glyphs = glyph_storage.first(glyph_count);
    for (std::size_t start = 0U; start < glyphs.size();) {
        const auto current = syllable(glyphs[start]);
        std::size_t end = start + 1U;
        while (end < glyphs.size() && syllable(glyphs[end]) == current) {
            ++end;
        }
        const auto type = static_cast<std::uint8_t>(current & 0x0FU);
        if (type == consonant_syllable || type == vowel_syllable ||
            type == standalone_cluster || type == broken_cluster) {
            initial_reorder_syllable(
                glyphs,
                original_order_scratch,
                start,
                end,
                unicode_script,
                (layout_script.value & 0xFFU) !=
                    static_cast<std::uint32_t>('2'),
                substitution_probe);
        }
        start = end;
    }
    set_error(error, font_error::none);
    return true;
}

void final_reorder_indic(
    const sfnt_font_view& font,
    open_type_tag script,
    std::span<shaping_glyph> glyphs) noexcept {
    script = legacy_script(script);
    std::uint16_t virama_glyph = 0U;
    const auto virama = virama_code_point(script);
    const bool has_virama = virama != 0U &&
        font.try_get_glyph_index(virama, virama_glyph) &&
        virama_glyph != 0U;
    constexpr auto malayalam =
        open_type_tag::from_chars('m', 'l', 'y', 'm');
    constexpr auto tamil =
        open_type_tag::from_chars('t', 'a', 'm', 'l');

    for (std::size_t start = 0U; start < glyphs.size();) {
        const auto current = syllable(glyphs[start]);
        std::size_t end = start + 1U;
        while (end < glyphs.size() && syllable(glyphs[end]) == current) {
            ++end;
        }
        bool reordered = false;

        if (has_virama) {
            for (std::size_t index = start; index < end; ++index) {
                if (glyphs[index].glyph_id != virama_glyph ||
                    !ligated(glyphs[index]) ||
                    !multiplied(glyphs[index])) {
                    continue;
                }
                set_category(glyphs[index], halant);
                clear_ligated(glyphs[index]);
                clear_multiplied(glyphs[index]);
            }
        }

        bool try_prebase = false;
        for (std::size_t index = start; index < end; ++index) {
            if (has_feature(glyphs[index], pref_mask)) {
                try_prebase = true;
                break;
            }
        }

        std::size_t base = start;
        while (base < end && position(glyphs[base]) < position_base) {
            ++base;
        }

        if (try_prebase && base + 1U < end) {
            for (std::size_t index = base + 1U; index < end; ++index) {
                if (!has_feature(glyphs[index], pref_mask)) {
                    continue;
                }
                if (!(substituted(glyphs[index]) &&
                        ligated(glyphs[index]) &&
                        !multiplied(glyphs[index]))) {
                    base = index;
                    while (base < end && is_halant(glyphs[base])) {
                        ++base;
                    }
                    if (base < end) {
                        set_position(glyphs[base], position_base);
                    }
                    try_prebase = false;
                }
                break;
            }
        }

        if (script == malayalam && base < end) {
            auto index = base + 1U;
            while (index < end) {
                while (index < end && is_joiner(glyphs[index])) {
                    ++index;
                }
                if (index == end || !is_halant(glyphs[index])) {
                    break;
                }
                ++index;
                while (index < end && is_joiner(glyphs[index])) {
                    ++index;
                }
                if (index < end && is_consonant(glyphs[index]) &&
                    position(glyphs[index]) == position_below_consonant) {
                    base = index;
                    set_position(glyphs[base], position_base);
                }
                ++index;
            }
        }

        if (base < end && base > start &&
            position(glyphs[base]) > position_base) {
            --base;
        }
        if (base == end && end > start && category(glyphs[end - 1U]) == zwj) {
            --base;
        }
        while (base > start && base < end &&
            ((!ligated(glyphs[base]) && category(glyphs[base]) == nukta) ||
                is_halant(glyphs[base]))) {
            --base;
        }

        if (start + 1U < end && start < base) {
            auto destination = base == end ? base - 2U : base - 1U;
            if (script != malayalam && script != tamil) {
                while (true) {
                    while (destination > start &&
                        category(glyphs[destination]) != matra &&
                        category(glyphs[destination]) != matra_post &&
                        category(glyphs[destination]) != halant) {
                        --destination;
                    }
                    if (!is_halant(glyphs[destination]) ||
                        position(glyphs[destination]) == position_pre_matra) {
                        destination = start;
                        break;
                    }
                    if (destination + 1U < end &&
                        category(glyphs[destination + 1U]) == zwj &&
                        destination > start) {
                        --destination;
                        continue;
                    }
                    break;
                }
            }
            if (destination > start &&
                position(glyphs[destination]) != position_pre_matra) {
                for (auto index = destination; index > start; --index) {
                    if (position(glyphs[index - 1U]) != position_pre_matra) {
                        continue;
                    }
                    move_glyph(glyphs, index - 1U, destination);
                    merge_cluster(
                        glyphs,
                        destination,
                        std::min(end, base + 1U));
                    reordered = true;
                    --destination;
                }
            } else {
                for (std::size_t index = start; index < base; ++index) {
                    if (position(glyphs[index]) == position_pre_matra) {
                        merge_cluster(
                            glyphs,
                            index,
                            std::min(end, base + 1U));
                        break;
                    }
                }
            }
        }

        if (start + 1U < end &&
            position(glyphs[start]) == position_ra_reph &&
            ((category(glyphs[start]) == repha) !=
                (ligated(glyphs[start]) && !multiplied(glyphs[start])))) {
            const auto destination = find_reph_destination(
                glyphs, start, end, base, script);
            merge_cluster(glyphs, start, destination + 1U);
            move_glyph(glyphs, start, destination);
            reordered = true;
            if (start < base && base <= destination) {
                --base;
            }
        }

        if (try_prebase && base + 1U < end) {
            for (std::size_t index = base + 1U; index < end; ++index) {
                if (!has_feature(glyphs[index], pref_mask)) {
                    continue;
                }
                if (ligated(glyphs[index]) && !multiplied(glyphs[index])) {
                    auto destination = base;
                    if (script != malayalam && script != tamil) {
                        while (destination > start &&
                            category(glyphs[destination - 1U]) != matra &&
                            category(glyphs[destination - 1U]) != matra_post &&
                            category(glyphs[destination - 1U]) != halant) {
                            --destination;
                        }
                    }
                    if (destination > start &&
                        is_halant(glyphs[destination - 1U]) &&
                        destination < end && is_joiner(glyphs[destination])) {
                        ++destination;
                    }
                    merge_cluster(glyphs, destination, index + 1U);
                    move_glyph(glyphs, index, destination);
                    reordered = true;
                    if (destination <= base && base < index) {
                        ++base;
                    }
                }
                break;
            }
        }

        if (reordered || position(glyphs[start]) == position_pre_matra) {
            merge_cluster(glyphs, start, end);
        }
        if (position(glyphs[start]) == position_pre_matra) {
            if (start == 0U ||
                !is_init_continuation(glyphs[start - 1U].code_point)) {
                add_feature(glyphs[start], init_mask);
            } else {
                mark_dependency(glyphs, start - 1U, start);
            }
        }
        start = end;
    }
}

} // namespace progpu::native::text::complex_detail

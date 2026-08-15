#include "progpu_native_open_type_complex_internal.hpp"

#include <algorithm>
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
constexpr std::uint8_t position_after_sub = 9U;
constexpr std::uint8_t position_before_post = 10U;
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
    return value == zwj || value == zwnj;
}

bool is_consonant(const shaping_glyph& glyph) noexcept {
    const auto value = category(glyph);
    return value == consonant || value == consonant_with_stacker ||
        value == ra || value == consonant_medial || value == vowel ||
        value == placeholder || value == dotted_circle;
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
    std::size_t start,
    std::size_t end) noexcept {
    for (std::size_t index = start + 1U; index < end; ++index) {
        auto value = glyphs[index];
        std::size_t insertion = index;
        while (insertion > start &&
            position(glyphs[insertion - 1U]) > position(value)) {
            glyphs[insertion] = glyphs[insertion - 1U];
            --insertion;
        }
        glyphs[insertion] = value;
    }
}

void reverse_left_matras(
    std::span<shaping_glyph> glyphs,
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
    std::size_t group_start = first;
    for (std::size_t index = first; index <= last; ++index) {
        const auto value = category(glyphs[index]);
        if (value != matra && value != matra_post) {
            continue;
        }
        std::reverse(
            glyphs.begin() + static_cast<std::ptrdiff_t>(group_start),
            glyphs.begin() + static_cast<std::ptrdiff_t>(index + 1U));
        group_start = index + 1U;
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

void initial_reorder_syllable(
    std::span<shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end,
    open_type_tag script) noexcept {
    std::size_t limit = start;
    std::size_t base = end;
    const auto normalized = legacy_script(script);
    bool has_reph = false;
    if (normalized == open_type_tag::from_chars('m','l','y','m') &&
        category(glyphs[start]) == repha) {
        ++limit;
        has_reph = true;
    } else if (end - start >= 2U && category(glyphs[start]) == ra &&
        category(glyphs[start + 1U]) == halant &&
        (end - start == 2U || !is_joiner(glyphs[start + 2U]))) {
        limit += 2U;
        while (limit < end && is_joiner(glyphs[limit])) {
            ++limit;
        }
        has_reph = true;
    }
    for (std::size_t cursor = end; cursor > limit; --cursor) {
        if (is_consonant(glyphs[cursor - 1U])) {
            base = cursor - 1U;
            break;
        }
    }
    if (base == end) {
        base = limit < end ? limit : start;
    }
    if (has_reph && base == start && limit - start <= 2U) {
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
        add_feature(glyphs[start], rphf_mask);
        if (start + 1U < end) {
            add_feature(glyphs[start + 1U], rphf_mask);
        }
    }
    attach_mark_positions(glyphs, start, end, base);
    stable_sort_positions(glyphs, start, end);
    reverse_left_matras(glyphs, start, end);
    base = end;
    for (std::size_t index = start; index < end; ++index) {
        if (position(glyphs[index]) == position_base) {
            base = index;
            break;
        }
    }
    const bool modern = (script.value & 0xFFU) ==
        static_cast<std::uint32_t>('2');
    const std::uint8_t pre_mask = static_cast<std::uint8_t>(
        half_mask | (modern ? blwf_mask : 0U));
    for (std::size_t index = start; index < base; ++index) {
        add_feature(glyphs[index], pre_mask);
    }
    if (base < end) {
        for (std::size_t index = base + 1U; index < end; ++index) {
            add_feature(glyphs[index],
                static_cast<std::uint8_t>(pref_mask | blwf_mask |
                    abvf_mask | pstf_mask));
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
    merge_cluster(glyphs, start, end);
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
    open_type_tag script,
    shaping_buffer_flags buffer_flags,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    font_error* error) noexcept {
    if (glyph_count > glyph_storage.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
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
            initial_reorder_syllable(glyphs, start, end, script);
        }
        start = end;
    }
    set_error(error, font_error::none);
    return true;
}

void final_reorder_indic(
    open_type_tag script,
    std::span<shaping_glyph> glyphs) noexcept {
    for (std::size_t start = 0U; start < glyphs.size();) {
        const auto current = syllable(glyphs[start]);
        std::size_t end = start + 1U;
        while (end < glyphs.size() && syllable(glyphs[end]) == current) {
            ++end;
        }
        std::size_t base = start;
        while (base < end && position(glyphs[base]) < position_base) {
            ++base;
        }
        if (start + 1U < end && start < base &&
            position(glyphs[start]) == position_ra_reph &&
            (category(glyphs[start]) == repha || substituted(glyphs[start]))) {
            const auto desired = reph_position(script);
            std::size_t destination = base < end ? base : end - 1U;
            while (destination + 1U < end &&
                position(glyphs[destination + 1U]) <= desired) {
                ++destination;
            }
            merge_cluster(glyphs, start, destination + 1U);
            std::rotate(glyphs.begin() + static_cast<std::ptrdiff_t>(start),
                glyphs.begin() + static_cast<std::ptrdiff_t>(start + 1U),
                glyphs.begin() + static_cast<std::ptrdiff_t>(destination + 1U));
        }
        if (position(glyphs[start]) == position_pre_matra) {
            add_feature(glyphs[start], init_mask);
        }
        start = end;
    }
}

} // namespace progpu::native::text::complex_detail

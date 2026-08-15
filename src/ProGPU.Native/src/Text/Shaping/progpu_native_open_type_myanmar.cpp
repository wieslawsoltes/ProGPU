#include "progpu_native_open_type_complex_internal.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Exact native port of the ProGPU-owned Myanmar preparation and reorder
// stages. Metadata is packed transiently into the caller-owned glyph span;
// stable positional ordering is performed in place with no heap allocation.

namespace progpu::native::text::complex_detail {
namespace {

constexpr std::uint8_t myanmar_consonant_syllable = 0U;
constexpr std::uint8_t myanmar_broken_cluster = 1U;
constexpr std::uint8_t consonant = 1U;
constexpr std::uint8_t vowel = 2U;
constexpr std::uint8_t halant = 4U;
constexpr std::uint8_t placeholder = 10U;
constexpr std::uint8_t dotted_circle = 11U;
constexpr std::uint8_t ra = 15U;
constexpr std::uint8_t consonant_with_stacker = 18U;
constexpr std::uint8_t vedic_sign = 9U;
constexpr std::uint8_t vowel_below = 21U;
constexpr std::uint8_t vowel_pre = 22U;
constexpr std::uint8_t asat = 32U;
constexpr std::uint8_t medial_ra = 36U;
constexpr std::uint8_t variation_selector = 40U;

constexpr std::uint8_t position_pre_matra = 2U;
constexpr std::uint8_t position_pre_consonant = 3U;
constexpr std::uint8_t position_base_consonant = 4U;
constexpr std::uint8_t position_after_main = 5U;
constexpr std::uint8_t position_before_sub = 7U;
constexpr std::uint8_t position_below_consonant = 8U;
constexpr std::uint8_t position_after_sub = 9U;

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool has_flag(shaping_buffer_flags value, shaping_buffer_flags flag) noexcept {
    return (static_cast<std::uint8_t>(value) &
        static_cast<std::uint8_t>(flag)) != 0U;
}

void merge_cluster(
    std::span<shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end) noexcept {
    if (start >= end) {
        return;
    }
    const auto first_cluster = glyphs[start].cluster;
    const auto last_cluster = glyphs[end - 1U].cluster;
    while (start > 0U && glyphs[start - 1U].cluster == first_cluster) {
        --start;
    }
    while (end < glyphs.size() && glyphs[end].cluster == last_cluster) {
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
                if (glyphs[index].cluster == minimum) {
                    continue;
                }
                glyphs[index].flags = static_cast<shaping_glyph_flags>(
                    raw_flags(glyphs[index]) |
                    static_cast<std::uint32_t>(
                        shaping_glyph_flags::unsafe_to_break) |
                    static_cast<std::uint32_t>(
                        shaping_glyph_flags::unsafe_to_concat));
            }
        }
        start = end;
    }
}

bool is_consonant(const shaping_glyph& glyph) noexcept {
    const auto value = category(glyph);
    return value == consonant || value == consonant_with_stacker ||
        value == ra || value == vowel || value == placeholder ||
        value == dotted_circle;
}

void stable_sort_positions(
    std::span<shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end) noexcept {
    for (std::size_t index = start + 1U; index < end; ++index) {
        auto glyph = glyphs[index];
        std::size_t target = index;
        while (target > start &&
            position(glyphs[target - 1U]) > position(glyph)) {
            glyphs[target] = glyphs[target - 1U];
            --target;
        }
        glyphs[target] = glyph;
    }
}

void reorder_syllable(
    std::span<shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end) noexcept {
    std::size_t base = end;
    const bool has_reph = end - start >= 3U &&
        category(glyphs[start]) == ra &&
        category(glyphs[start + 1U]) == asat &&
        category(glyphs[start + 2U]) == halant;
    const std::size_t limit = has_reph ? start + 3U : start;
    base = has_reph ? start : limit;
    if (!has_reph) {
        for (std::size_t index = limit; index < end; ++index) {
            if (is_consonant(glyphs[index])) {
                base = index;
                break;
            }
        }
    }

    std::size_t cursor = start;
    for (; cursor < start + (has_reph ? 3U : 0U); ++cursor) {
        set_position(glyphs[cursor], position_after_main);
    }
    for (; cursor < base; ++cursor) {
        set_position(glyphs[cursor], position_pre_consonant);
    }
    if (cursor < end) {
        set_position(glyphs[cursor++], position_base_consonant);
    }

    std::uint8_t current_position = position_after_main;
    for (; cursor < end; ++cursor) {
        const auto value = category(glyphs[cursor]);
        if (value == medial_ra) {
            set_position(glyphs[cursor], position_pre_consonant);
            continue;
        }
        if (value == vowel_pre) {
            set_position(glyphs[cursor], position_pre_matra);
            continue;
        }
        if (value == variation_selector) {
            set_position(glyphs[cursor], position(glyphs[cursor - 1U]));
            continue;
        }
        if (current_position == position_after_main && value == vowel_below) {
            current_position = position_below_consonant;
            set_position(glyphs[cursor], current_position);
            continue;
        }
        if (current_position == position_below_consonant &&
            value == vedic_sign) {
            set_position(glyphs[cursor], position_before_sub);
            continue;
        }
        if (current_position == position_below_consonant &&
            value == vowel_below) {
            set_position(glyphs[cursor], current_position);
            continue;
        }
        if (current_position == position_below_consonant &&
            value != vedic_sign) {
            current_position = position_after_sub;
        }
        set_position(glyphs[cursor], current_position);
    }

    merge_cluster(glyphs, start, end);
    stable_sort_positions(glyphs, start, end);

    std::size_t first_left = end;
    std::size_t last_left = end;
    for (std::size_t index = start; index < end; ++index) {
        if (position(glyphs[index]) != position_pre_matra) {
            continue;
        }
        if (first_left == end) {
            first_left = index;
        }
        last_left = index;
    }
    if (first_left < last_left) {
        std::reverse(glyphs.begin() + static_cast<std::ptrdiff_t>(first_left),
            glyphs.begin() + static_cast<std::ptrdiff_t>(last_left + 1U));
        std::size_t segment_start = first_left;
        for (std::size_t index = segment_start; index <= last_left; ++index) {
            if (category(glyphs[index]) != vowel_pre) {
                continue;
            }
            std::reverse(
                glyphs.begin() + static_cast<std::ptrdiff_t>(segment_start),
                glyphs.begin() + static_cast<std::ptrdiff_t>(index + 1U));
            segment_start = index + 1U;
        }
    }
}

} // namespace

bool try_prepare_myanmar(
    const sfnt_font_view& font,
    shaping_buffer_flags buffer_flags,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    std::span<std::uint8_t> category_scratch,
    std::span<std::uint8_t> syllable_scratch,
    font_error* error) noexcept {
    static_cast<void>(font);
    static_cast<void>(buffer_flags);
    if (glyph_count > glyph_storage.size() ||
        category_scratch.size() < glyph_count ||
        syllable_scratch.size() < glyph_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    if (glyph_count == 0U) {
        set_error(error, font_error::none);
        return true;
    }
    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        const auto properties = get_unicode_indic_shaping_properties(
            glyph_storage[index].code_point);
        category_scratch[index] = properties.category;
    }
    if (!try_assign_unicode_syllables(
            unicode_syllable_machine::myanmar,
            category_scratch.first(glyph_count),
            {},
            syllable_scratch.first(glyph_count))) {
        set_error(error, font_error::invalid_argument);
        return false;
    }

    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        set_category(glyph_storage[index], category_scratch[index]);
        set_syllable(glyph_storage[index], syllable_scratch[index]);
    }
    mark_syllables_unsafe(glyph_storage.first(glyph_count));

    set_error(error, font_error::none);
    return true;
}

bool try_reorder_myanmar(
    const sfnt_font_view& font,
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
    std::uint32_t insertion_count = 0U;
    if (insert_dotted) {
        std::uint8_t previous = 0U;
        for (std::uint32_t index = 0U; index < glyph_count; ++index) {
            const auto current = syllable(glyph_storage[index]);
            if (current != previous &&
                (current & 0x0FU) == myanmar_broken_cluster) {
                ++insertion_count;
            }
            previous = current;
        }
        if (insertion_count > glyph_storage.size() - glyph_count) {
            set_error(error, font_error::insufficient_buffer);
            return false;
        }
    }

    if (insert_dotted && insertion_count != 0U) {
        std::uint8_t previous = 0U;
        for (std::uint32_t index = 0U; index < glyph_count; ++index) {
            const auto current = syllable(glyph_storage[index]);
            if (current == previous ||
                (current & 0x0FU) != myanmar_broken_cluster) {
                previous = current;
                continue;
            }
            previous = current;
            std::move_backward(
                glyph_storage.begin() + index,
                glyph_storage.begin() + glyph_count,
                glyph_storage.begin() + glyph_count + 1U);
            glyph_storage[index] = shaping_glyph{
                dotted_glyph,
                0x25CCU,
                glyph_storage[index + 1U].cluster};
            set_category(glyph_storage[index], dotted_circle);
            set_syllable(glyph_storage[index], previous);
            ++glyph_count;
            ++index;
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
        if (type == myanmar_consonant_syllable ||
            type == myanmar_broken_cluster) {
            reorder_syllable(glyphs, start, end);
        }
        start = end;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text::complex_detail

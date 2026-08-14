#include "progpu_native_open_type_complex_internal.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Exact native structural port of ProGPU-owned Universal Shaping Engine
// categorization, filtered syllable assignment, broken-cluster recovery, and
// post-basic reordering. Stage-specific GSUB execution remains in the common
// OpenType stage runner so glyph storage stays caller-owned throughout.

namespace progpu::native::text::complex_detail {
namespace {

constexpr std::uint8_t use_virama_terminated = 0U;
constexpr std::uint8_t use_sakot_terminated = 1U;
constexpr std::uint8_t use_standard = 2U;
constexpr std::uint8_t use_symbol = 5U;
constexpr std::uint8_t use_broken = 7U;

constexpr std::uint8_t use_base = 1U;
constexpr std::uint8_t use_cgj = 6U;
constexpr std::uint8_t use_halant = 12U;
constexpr std::uint8_t use_zwnj = 14U;
constexpr std::uint8_t use_repha = 18U;
constexpr std::uint8_t use_vowel_pre = 22U;
constexpr std::uint8_t use_vowel_modifier_pre = 23U;
constexpr std::uint8_t use_final_above = 24U;
constexpr std::uint8_t use_final_below = 25U;
constexpr std::uint8_t use_final_post = 26U;
constexpr std::uint8_t use_medial_above = 27U;
constexpr std::uint8_t use_medial_below = 28U;
constexpr std::uint8_t use_medial_post = 29U;
constexpr std::uint8_t use_medial_pre = 30U;
constexpr std::uint8_t use_vowel_above = 33U;
constexpr std::uint8_t use_vowel_below = 34U;
constexpr std::uint8_t use_vowel_post = 35U;
constexpr std::uint8_t use_vowel_modifier_above = 37U;
constexpr std::uint8_t use_vowel_modifier_below = 38U;
constexpr std::uint8_t use_vowel_modifier_post = 39U;
constexpr std::uint8_t use_final_modifier_above = 45U;
constexpr std::uint8_t use_final_modifier_below = 46U;
constexpr std::uint8_t use_final_modifier_post = 47U;
constexpr std::uint8_t use_invisible_stacker = 44U;
constexpr std::uint8_t use_halant_or_vowel_modifier = 53U;

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool has_flag(shaping_buffer_flags value, shaping_buffer_flags flag) noexcept {
    return (static_cast<std::uint8_t>(value) &
        static_cast<std::uint8_t>(flag)) != 0U;
}

bool is_mark(std::uint32_t code_point) noexcept {
    const auto value = get_unicode_grapheme_break_class(code_point);
    return value == unicode_grapheme_break_class::extend ||
        value == unicode_grapheme_break_class::spacing_mark;
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

bool is_post_base(std::uint8_t value) noexcept {
    switch (value) {
        case use_final_above:
        case use_final_below:
        case use_final_post:
        case use_final_modifier_above:
        case use_final_modifier_below:
        case use_final_modifier_post:
        case use_medial_above:
        case use_medial_below:
        case use_medial_post:
        case use_medial_pre:
        case use_vowel_above:
        case use_vowel_below:
        case use_vowel_post:
        case use_vowel_pre:
        case use_vowel_modifier_above:
        case use_vowel_modifier_below:
        case use_vowel_modifier_post:
        case use_vowel_modifier_pre:
            return true;
        default:
            return false;
    }
}

bool is_halant(std::uint8_t value) noexcept {
    return value == use_halant ||
        value == use_halant_or_vowel_modifier ||
        value == use_invisible_stacker;
}

void reorder_syllable(
    std::span<shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end,
    std::uint8_t type) noexcept {
    if (type != use_virama_terminated && type != use_sakot_terminated &&
        type != use_standard && type != use_symbol && type != use_broken) {
        return;
    }
    if (category(glyphs[start]) == use_repha && end - start > 1U) {
        for (std::size_t index = start + 1U; index < end; ++index) {
            const bool post_base = is_post_base(category(glyphs[index])) ||
                is_halant(category(glyphs[index]));
            if (!post_base && index != end - 1U) {
                continue;
            }
            const std::size_t destination = post_base ? index - 1U : index;
            merge_cluster(glyphs, start, destination + 1U);
            std::rotate(
                glyphs.begin() + static_cast<std::ptrdiff_t>(start),
                glyphs.begin() + static_cast<std::ptrdiff_t>(start + 1U),
                glyphs.begin() + static_cast<std::ptrdiff_t>(destination + 1U));
            break;
        }
    }

    std::size_t target = start;
    for (std::size_t index = start; index < end; ++index) {
        const auto value = category(glyphs[index]);
        if (is_halant(value)) {
            target = index + 1U;
        } else if ((value == use_vowel_pre ||
                    value == use_vowel_modifier_pre) &&
            target < index) {
            merge_cluster(glyphs, target, index + 1U);
            std::rotate(
                glyphs.begin() + static_cast<std::ptrdiff_t>(target),
                glyphs.begin() + static_cast<std::ptrdiff_t>(index),
                glyphs.begin() + static_cast<std::ptrdiff_t>(index + 1U));
        }
    }
}

} // namespace

bool try_prepare_use(
    const sfnt_font_view& font,
    shaping_buffer_flags buffer_flags,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    std::span<std::uint8_t> category_scratch,
    std::span<std::uint8_t> syllable_scratch,
    std::span<std::uint32_t> index_scratch,
    font_error* error) noexcept {
    if (glyph_count > glyph_storage.size() ||
        category_scratch.size() < glyph_count ||
        syllable_scratch.size() < glyph_count ||
        index_scratch.size() < static_cast<std::size_t>(glyph_count) + 1U) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    if (glyph_count == 0U) {
        set_error(error, font_error::none);
        return true;
    }
    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        category_scratch[index] = get_unicode_use_shaping_category(
            glyph_storage[index].code_point);
    }
    std::uint32_t machine_count = 0U;
    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        const auto value = category_scratch[index];
        if (value == use_cgj) {
            continue;
        }
        if (value == use_zwnj) {
            auto following = index + 1U;
            while (following < glyph_count &&
                category_scratch[following] == use_cgj) {
                ++following;
            }
            if (following < glyph_count &&
                is_mark(glyph_storage[following].code_point)) {
                continue;
            }
        }
        index_scratch[machine_count++] = index;
    }
    if (machine_count == 0U) {
        std::fill_n(syllable_scratch.begin(), glyph_count, std::uint8_t{0U});
    } else {
        index_scratch[machine_count] = glyph_count;
        if (!try_assign_unicode_syllables(
                unicode_syllable_machine::use,
                category_scratch.first(glyph_count),
                index_scratch.first(machine_count + 1U),
                syllable_scratch.first(glyph_count))) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
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
            const auto current = syllable_scratch[index];
            if (current != previous && (current & 0x0FU) == use_broken) {
                ++insertion_count;
            }
            previous = current;
        }
        if (insertion_count > glyph_storage.size() - glyph_count) {
            set_error(error, font_error::insufficient_buffer);
            return false;
        }
    }

    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        set_category(glyph_storage[index], category_scratch[index]);
        set_syllable(glyph_storage[index], syllable_scratch[index]);
    }
    mark_syllables_unsafe(glyph_storage.first(glyph_count));
    for (std::uint32_t start = 0U; start < glyph_count;) {
        const auto current = syllable(glyph_storage[start]);
        std::uint32_t end = start + 1U;
        while (end < glyph_count && syllable(glyph_storage[end]) == current) {
            ++end;
        }
        const std::uint32_t limit = category(glyph_storage[start]) == use_repha
            ? 1U
            : std::min<std::uint32_t>(3U, end - start);
        for (std::uint32_t index = start; index < start + limit; ++index) {
            add_feature(glyph_storage[index], 1U);
        }
        start = end;
    }

    if (insert_dotted && insertion_count != 0U) {
        std::uint8_t previous = 0U;
        for (std::uint32_t index = 0U; index < glyph_count; ++index) {
            const auto current = syllable(glyph_storage[index]);
            if (current == previous || (current & 0x0FU) != use_broken) {
                previous = current;
                continue;
            }
            previous = current;
            while (index < glyph_count &&
                syllable(glyph_storage[index]) == previous &&
                category(glyph_storage[index]) == use_repha) {
                ++index;
            }
            std::move_backward(
                glyph_storage.begin() + index,
                glyph_storage.begin() + glyph_count,
                glyph_storage.begin() + glyph_count + 1U);
            const auto cluster = index < glyph_count
                ? glyph_storage[index + 1U].cluster
                : glyph_storage[index - 1U].cluster;
            glyph_storage[index] = shaping_glyph{
                dotted_glyph, 0x25CCU, cluster};
            set_category(glyph_storage[index], use_base);
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
        reorder_syllable(
            glyphs, start, end, static_cast<std::uint8_t>(current & 0x0FU));
        start = end;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text::complex_detail

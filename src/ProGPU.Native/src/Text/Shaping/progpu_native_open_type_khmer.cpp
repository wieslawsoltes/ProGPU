#include "progpu_native_open_type_complex_internal.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Exact native port of ProGPU-owned Khmer syllable preparation: generated
// categorization/machine execution, broken-cluster dotted circles, coeng-ro
// and pre-vowel movement, cluster merging, and feature-mask assignment.

namespace progpu::native::text::complex_detail {
namespace {

constexpr std::uint8_t khmer_broken_cluster = 1U;
constexpr std::uint8_t indic_halant = 4U;
constexpr std::uint8_t indic_dotted_circle = 11U;
constexpr std::uint8_t indic_ra = 15U;
constexpr std::uint8_t indic_vowel_pre = 22U;
constexpr std::uint8_t khmer_pref_mask = 1U;
constexpr std::uint8_t khmer_post_base_mask = 2U;
constexpr std::uint8_t khmer_cfar_mask = 4U;

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
    std::int32_t cluster = std::numeric_limits<std::int32_t>::max();
    for (std::size_t index = start; index < end; ++index) {
        cluster = std::min(cluster, glyphs[index].cluster);
    }
    for (std::size_t index = start; index < end; ++index) {
        glyphs[index].cluster = cluster;
    }
}

void mark_syllables_unsafe(std::span<shaping_glyph> glyphs) noexcept {
    for (std::size_t start = 0U; start < glyphs.size();) {
        const auto current_syllable = syllable(glyphs[start]);
        std::size_t end = start + 1U;
        while (end < glyphs.size() &&
            syllable(glyphs[end]) == current_syllable) {
            ++end;
        }
        if (end - start >= 2U) {
            std::int32_t minimum_cluster =
                std::numeric_limits<std::int32_t>::max();
            for (std::size_t index = start; index < end; ++index) {
                minimum_cluster = std::min(
                    minimum_cluster, glyphs[index].cluster);
            }
            for (std::size_t index = start; index < end; ++index) {
                if (glyphs[index].cluster == minimum_cluster) {
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

void prepare_syllable(
    std::span<shaping_glyph> glyphs,
    std::size_t start,
    std::size_t end) noexcept {
    if (end - start <= 1U) {
        return;
    }
    for (std::size_t index = start + 1U; index < end; ++index) {
        add_feature(glyphs[index], khmer_post_base_mask);
    }

    std::uint32_t coeng_count = 0U;
    for (std::size_t index = start + 1U; index < end; ++index) {
        if (category(glyphs[index]) == indic_halant &&
            coeng_count <= 2U && index + 1U < end) {
            ++coeng_count;
            if (category(glyphs[index + 1U]) == indic_ra) {
                add_feature(glyphs[index], khmer_pref_mask);
                add_feature(glyphs[index + 1U], khmer_pref_mask);
                merge_cluster(glyphs, start, index + 2U);
                std::rotate(
                    glyphs.begin() + static_cast<std::ptrdiff_t>(start),
                    glyphs.begin() + static_cast<std::ptrdiff_t>(index),
                    glyphs.begin() + static_cast<std::ptrdiff_t>(index + 2U));
                for (std::size_t following = index + 2U;
                     following < end;
                     ++following) {
                    add_feature(glyphs[following], khmer_cfar_mask);
                }
                coeng_count = 2U;
            }
        } else if (category(glyphs[index]) == indic_vowel_pre) {
            merge_cluster(glyphs, start, index + 1U);
            std::rotate(
                glyphs.begin() + static_cast<std::ptrdiff_t>(start),
                glyphs.begin() + static_cast<std::ptrdiff_t>(index),
                glyphs.begin() + static_cast<std::ptrdiff_t>(index + 1U));
        }
    }
}

} // namespace

bool try_prepare_khmer(
    const sfnt_font_view& font,
    shaping_buffer_flags buffer_flags,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    std::span<std::uint8_t> category_scratch,
    std::span<std::uint8_t> syllable_scratch,
    font_error* error) noexcept {
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
            unicode_syllable_machine::khmer,
            category_scratch.first(glyph_count),
            {},
            syllable_scratch.first(glyph_count))) {
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
            const auto current = syllable_scratch[index];
            if (current != previous &&
                (current & 0x0FU) == khmer_broken_cluster) {
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

    if (insert_dotted && insertion_count != 0U) {
        std::uint8_t previous = 0U;
        for (std::uint32_t index = 0U; index < glyph_count; ++index) {
            const auto current = syllable(glyph_storage[index]);
            if (current == previous ||
                (current & 0x0FU) != khmer_broken_cluster) {
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
            set_category(glyph_storage[index], indic_dotted_circle);
            set_syllable(glyph_storage[index], previous);
            ++glyph_count;
            ++index;
        }
    }

    auto glyphs = glyph_storage.first(glyph_count);
    for (std::size_t start = 0U; start < glyphs.size();) {
        const auto current_syllable = syllable(glyphs[start]);
        std::size_t end = start + 1U;
        while (end < glyphs.size() &&
            syllable(glyphs[end]) == current_syllable) {
            ++end;
        }
        const auto type = static_cast<std::uint8_t>(current_syllable & 0x0FU);
        if (type == 0U || type == khmer_broken_cluster) {
            prepare_syllable(glyphs, start, end);
        }
        start = end;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text::complex_detail

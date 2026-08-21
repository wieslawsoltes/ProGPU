#include "progpu_native_use_diacritics_internal.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Exact allocation-free port of ProGPU-owned
// GlyphSubstitutionBuffer.NormalizeUseDiacritics at repository checkpoint
// e68517cc. The shared FormD plan is searched in O(G log R + D), where G is
// the glyph count, R the decomposition records, and D the written components.
// Capacity and cmap mappings are validated before the backward in-place write.

namespace progpu::native::text::detail {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

std::uint32_t read_u32(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return std::to_integer<std::uint32_t>(bytes[offset]) |
        (std::to_integer<std::uint32_t>(bytes[offset + 1U]) << 8U) |
        (std::to_integer<std::uint32_t>(bytes[offset + 2U]) << 16U) |
        (std::to_integer<std::uint32_t>(bytes[offset + 3U]) << 24U);
}

bool is_mark(std::uint32_t code_point) noexcept {
    const auto category = get_unicode_general_category(code_point);
    return category == unicode_general_category::nonspacing_mark ||
        category == unicode_general_category::spacing_combining_mark ||
        category == unicode_general_category::enclosing_mark;
}

bool try_get_eligible_decomposition(
    const unicode_normalization_data& normalization,
    std::uint32_t code_point,
    std::span<const std::byte>& decomposition) noexcept {
    decomposition = {};
    return normalization.try_get_decomposition(code_point, decomposition) &&
        !decomposition.empty() && is_mark(read_u32(decomposition, 0U));
}

} // namespace

bool try_get_use_diacritic_glyph_count(
    std::span<const unicode_scalar> input,
    const unicode_normalization_data& normalization,
    std::uint32_t& result,
    font_error* error) noexcept {
    std::uint64_t count = 0U;
    for (const auto& scalar : input) {
        std::span<const std::byte> decomposition{};
        count += try_get_eligible_decomposition(
                normalization, scalar.code_point, decomposition)
            ? decomposition.size() / 4U
            : 1U;
        if (count > std::numeric_limits<std::uint32_t>::max()) {
            result = 0U;
            set_error(error, font_error::invalid_argument);
            return false;
        }
    }
    result = static_cast<std::uint32_t>(count);
    set_error(error, font_error::none);
    return true;
}

bool try_get_use_diacritic_additions(
    const sfnt_font_view& font,
    const unicode_normalization_data& normalization,
    std::span<const shaping_glyph> glyphs,
    std::size_t& additions,
    font_error* error) noexcept {
    additions = 0U;
    for (const auto& glyph : glyphs) {
        std::span<const std::byte> decomposition{};
        if (!try_get_eligible_decomposition(
                normalization, glyph.code_point, decomposition)) {
            continue;
        }
        const std::size_t component_count = decomposition.size() / 4U;
        if (component_count - 1U >
            std::numeric_limits<std::size_t>::max() - additions) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        for (std::size_t offset = 0U;
             offset < decomposition.size();
             offset += 4U) {
            std::uint16_t mapped = 0U;
            if (!font.try_get_glyph_index(
                    read_u32(decomposition, offset), mapped)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
        }
        additions += component_count - 1U;
    }
    set_error(error, font_error::none);
    return true;
}

bool try_normalize_use_diacritics(
    const sfnt_font_view& font,
    const unicode_normalization_data& normalization,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    font_error* error) noexcept {
    if (glyph_count > glyph_storage.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::size_t additions = 0U;
    if (!try_get_use_diacritic_additions(
            font,
            normalization,
            glyph_storage.first(glyph_count),
            additions,
            error)) {
        return false;
    }
    if (additions > glyph_storage.size() - glyph_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    const auto final_count = static_cast<std::size_t>(glyph_count) + additions;
    std::size_t source_index = glyph_count;
    std::size_t destination_index = final_count;
    while (source_index != 0U) {
        const shaping_glyph source = glyph_storage[--source_index];
        std::span<const std::byte> decomposition{};
        if (!try_get_eligible_decomposition(
                normalization, source.code_point, decomposition)) {
            glyph_storage[--destination_index] = source;
            continue;
        }
        for (std::size_t offset = decomposition.size(); offset != 0U;) {
            offset -= 4U;
            const auto code_point = read_u32(decomposition, offset);
            std::uint16_t mapped = 0U;
            if (!font.try_get_glyph_index(code_point, mapped)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            glyph_storage[--destination_index] = shaping_glyph{
                mapped, code_point, source.cluster};
        }
    }
    glyph_count = static_cast<std::uint32_t>(final_count);
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text::detail

#include "progpu_native_vowel_constraints_internal.hpp"

#include "progpu_native_unicode_data.generated.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <span>

// Exact allocation-free port of ProGPU-owned VowelConstraintData and
// GlyphSubstitutionBuffer.ApplyVowelConstraints at repository checkpoint
// e06c9055. The fixed generated record scan is O(G * R) for G glyphs and R
// constraints, with O(1) internal storage and caller-owned output capacity.

namespace progpu::native::text::detail {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

std::uint32_t match_length(
    open_type_tag script,
    std::uint32_t first,
    std::uint32_t second,
    std::uint32_t third) noexcept {
    constexpr std::size_t stride = 4U;
    for (std::size_t index = 0U;
         index + stride <= unicode_vowel_constraints.size();
         index += stride) {
        if (unicode_vowel_constraints[index] != script.value ||
            unicode_vowel_constraints[index + 1U] != first ||
            unicode_vowel_constraints[index + 2U] != second) {
            continue;
        }
        const auto expected_third = unicode_vowel_constraints[index + 3U];
        if (expected_third == 0U) return 2U;
        if (expected_third == third) return 3U;
    }
    return 0U;
}

} // namespace

bool has_vowel_constraints(open_type_tag script) noexcept {
    for (std::size_t index = 0U;
         index < unicode_vowel_constraints.size();
         index += 4U) {
        if (unicode_vowel_constraints[index] == script.value) return true;
    }
    return false;
}

std::size_t count_vowel_constraint_insertions(
    open_type_tag script,
    std::span<const shaping_glyph> glyphs) noexcept {
    std::size_t additions = 0U;
    for (std::size_t index = 0U; index + 1U < glyphs.size();) {
        const auto third = index + 2U < glyphs.size()
            ? glyphs[index + 2U].code_point
            : 0U;
        const auto length = match_length(
            script,
            glyphs[index].code_point,
            glyphs[index + 1U].code_point,
            third);
        if (length == 0U) {
            ++index;
        } else {
            ++additions;
            index += length;
        }
    }
    return additions;
}

bool try_apply_vowel_constraints(
    const sfnt_font_view& font,
    open_type_tag script,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    font_error* error) noexcept {
    if (glyph_count > glyph_storage.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const auto additions = count_vowel_constraint_insertions(
        script, glyph_storage.first(glyph_count));
    if (additions > glyph_storage.size() - glyph_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    if (additions == 0U) {
        set_error(error, font_error::none);
        return true;
    }
    std::uint16_t dotted_circle = 0U;
    if (!font.try_get_glyph_index(0x25CCU, dotted_circle)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    for (std::uint32_t index = 0U; index + 1U < glyph_count;) {
        const auto third = index + 2U < glyph_count
            ? glyph_storage[index + 2U].code_point
            : 0U;
        const auto length = match_length(
            script,
            glyph_storage[index].code_point,
            glyph_storage[index + 1U].code_point,
            third);
        if (length == 0U) {
            ++index;
            continue;
        }
        const auto final_index = index + length - 1U;
        const auto cluster = glyph_storage[final_index].cluster;
        std::move_backward(
            glyph_storage.begin() + static_cast<std::ptrdiff_t>(final_index),
            glyph_storage.begin() + static_cast<std::ptrdiff_t>(glyph_count),
            glyph_storage.begin() + static_cast<std::ptrdiff_t>(
                glyph_count + 1U));
        glyph_storage[final_index] = shaping_glyph{
            dotted_circle, 0x25CCU, cluster};
        ++glyph_count;
        index += length + 1U;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text::detail

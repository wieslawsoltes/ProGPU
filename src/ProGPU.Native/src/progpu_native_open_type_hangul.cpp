#include "progpu_native_text.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Native port of ProGPU-owned GlyphSubstitutionBuffer.PrepareHangulShaping.
// The public shaping flags reserve two transient bits for the Jamo feature;
// the orchestrator consumes and clears them before returning public records.

namespace progpu::native::text {
namespace {

constexpr std::uint32_t hangul_feature_mask = 0x00001800U;
constexpr std::uint32_t hangul_feature_shift = 11U;

enum class hangul_feature : std::uint32_t {
    none = 0U,
    leading = 1U,
    vowel = 2U,
    trailing = 3U
};

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool is_leading(std::uint32_t code_point) noexcept {
    return (code_point >= 0x1100U && code_point <= 0x115FU) ||
        (code_point >= 0xA960U && code_point <= 0xA97CU);
}

bool is_vowel(std::uint32_t code_point) noexcept {
    return (code_point >= 0x1160U && code_point <= 0x11A7U) ||
        (code_point >= 0xD7B0U && code_point <= 0xD7C6U);
}

bool is_trailing(std::uint32_t code_point) noexcept {
    return (code_point >= 0x11A8U && code_point <= 0x11FFU) ||
        (code_point >= 0xD7CBU && code_point <= 0xD7FBU);
}

void set_feature(shaping_glyph& glyph, hangul_feature feature) noexcept {
    glyph.flags = static_cast<shaping_glyph_flags>(
        (static_cast<std::uint32_t>(glyph.flags) & ~hangul_feature_mask) |
        (static_cast<std::uint32_t>(feature) << hangul_feature_shift));
}

std::int32_t merged_cluster(
    std::span<const shaping_glyph> glyphs,
    std::size_t start,
    std::size_t count) noexcept {
    std::int32_t result = glyphs[start].cluster;
    for (std::size_t index = 1U; index < count; ++index) {
        result = std::min(result, glyphs[start + index].cluster);
    }
    return result;
}

bool try_map(
    const sfnt_font_view& font,
    std::uint32_t code_point,
    std::uint16_t& glyph) noexcept {
    glyph = 0U;
    return font.try_get_glyph_index(code_point, glyph);
}

void erase_after(
    std::span<shaping_glyph> storage,
    std::uint32_t& count,
    std::size_t index,
    std::size_t erase_count) noexcept {
    std::move(
        storage.begin() + static_cast<std::ptrdiff_t>(index + erase_count),
        storage.begin() + static_cast<std::ptrdiff_t>(count),
        storage.begin() + static_cast<std::ptrdiff_t>(index));
    count -= static_cast<std::uint32_t>(erase_count);
}

} // namespace

bool try_prepare_open_type_hangul(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    font_error* error) noexcept {
    if (glyph_count > glyph_storage.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::size_t maximum_count = glyph_count;
    for (std::size_t index = 0U; index < glyph_count; ++index) {
        const std::uint32_t code_point = glyph_storage[index].code_point;
        if (code_point >= 0xAC00U && code_point <= 0xD7A3U) {
            if (maximum_count > glyph_storage.size() ||
                glyph_storage.size() - maximum_count < 2U) {
                set_error(error, font_error::insufficient_buffer);
                return false;
            }
            maximum_count += 2U;
        }
    }
    if (maximum_count > glyph_storage.size()) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        set_feature(glyph_storage[index], hangul_feature::none);
    }

    for (std::uint32_t index = 0U; index < glyph_count; ++index) {
        const std::uint32_t code_point = glyph_storage[index].code_point;
        if (is_leading(code_point) && index + 1U < glyph_count &&
            is_vowel(glyph_storage[index + 1U].code_point)) {
            const std::uint32_t vowel = glyph_storage[index + 1U].code_point;
            const std::uint32_t trailing = index + 2U < glyph_count &&
                is_trailing(glyph_storage[index + 2U].code_point)
                ? glyph_storage[index + 2U].code_point
                : 0U;
            const std::uint32_t input_count = trailing == 0U ? 2U : 3U;
            if (code_point >= 0x1100U && code_point <= 0x1112U &&
                vowel >= 0x1161U && vowel <= 0x1175U &&
                (trailing == 0U ||
                    (trailing >= 0x11A8U && trailing <= 0x11C2U))) {
                const std::uint32_t syllable = 0xAC00U +
                    (code_point - 0x1100U) * 588U +
                    (vowel - 0x1161U) * 28U +
                    (trailing == 0U ? 0U : trailing - 0x11A7U);
                std::uint16_t composed = 0U;
                if (!try_map(font, syllable, composed)) {
                    set_error(error, font_error::invalid_face);
                    return false;
                }
                if (composed != 0U) {
                    shaping_glyph& first = glyph_storage[index];
                    first.code_point = syllable;
                    first.glyph_id = composed;
                    first.cluster = merged_cluster(
                        glyph_storage, index, input_count);
                    erase_after(
                        glyph_storage,
                        glyph_count,
                        index + 1U,
                        input_count - 1U);
                    continue;
                }
            }
            const std::int32_t cluster = merged_cluster(
                glyph_storage, index, input_count);
            for (std::uint32_t offset = 0U; offset < input_count; ++offset) {
                glyph_storage[index + offset].cluster = cluster;
            }
            set_feature(glyph_storage[index], hangul_feature::leading);
            set_feature(glyph_storage[index + 1U], hangul_feature::vowel);
            if (trailing != 0U) {
                set_feature(
                    glyph_storage[index + 2U], hangul_feature::trailing);
            }
            index += input_count - 1U;
            continue;
        }

        if (code_point < 0xAC00U || code_point > 0xD7A3U) {
            continue;
        }
        const std::uint32_t syllable_index = code_point - 0xAC00U;
        const std::uint32_t trailing_index = syllable_index % 28U;
        if (trailing_index == 0U && index + 1U < glyph_count &&
            glyph_storage[index + 1U].code_point >= 0x11A8U &&
            glyph_storage[index + 1U].code_point <= 0x11C2U) {
            const std::uint32_t combined = code_point +
                glyph_storage[index + 1U].code_point - 0x11A7U;
            std::uint16_t combined_glyph = 0U;
            if (!try_map(font, combined, combined_glyph)) {
                set_error(error, font_error::invalid_face);
                return false;
            }
            if (combined_glyph != 0U) {
                glyph_storage[index].code_point = combined;
                glyph_storage[index].glyph_id = combined_glyph;
                glyph_storage[index].cluster = merged_cluster(
                    glyph_storage, index, 2U);
                erase_after(glyph_storage, glyph_count, index + 1U, 1U);
                continue;
            }
        }

        const bool followed_by_noncombining_trailing =
            trailing_index == 0U && index + 1U < glyph_count &&
            is_trailing(glyph_storage[index + 1U].code_point) &&
            !(glyph_storage[index + 1U].code_point >= 0x11A8U &&
                glyph_storage[index + 1U].code_point <= 0x11C2U);
        if (glyph_storage[index].glyph_id != 0U &&
            !followed_by_noncombining_trailing) {
            continue;
        }

        const std::uint32_t leading = 0x1100U + syllable_index / 588U;
        const std::uint32_t vowel =
            0x1161U + (syllable_index % 588U) / 28U;
        const std::uint32_t trailing = 0x11A7U + trailing_index;
        std::uint16_t leading_glyph = 0U;
        std::uint16_t vowel_glyph = 0U;
        std::uint16_t trailing_glyph = 0U;
        if (!try_map(font, leading, leading_glyph) ||
            !try_map(font, vowel, vowel_glyph) ||
            (trailing_index != 0U &&
                !try_map(font, trailing, trailing_glyph))) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        if (leading_glyph == 0U || vowel_glyph == 0U ||
            (trailing_index != 0U && trailing_glyph == 0U)) {
            continue;
        }

        const std::uint32_t replacement_count =
            trailing_index == 0U ? 2U : 3U;
        std::move_backward(
            glyph_storage.begin() + static_cast<std::ptrdiff_t>(index + 1U),
            glyph_storage.begin() + static_cast<std::ptrdiff_t>(glyph_count),
            glyph_storage.begin() + static_cast<std::ptrdiff_t>(
                glyph_count + replacement_count - 1U));
        glyph_count += replacement_count - 1U;
        const shaping_glyph source = glyph_storage[index];
        glyph_storage[index] = source;
        glyph_storage[index].code_point = leading;
        glyph_storage[index].glyph_id = leading_glyph;
        set_feature(glyph_storage[index], hangul_feature::leading);
        glyph_storage[index + 1U] = source;
        glyph_storage[index + 1U].code_point = vowel;
        glyph_storage[index + 1U].glyph_id = vowel_glyph;
        set_feature(glyph_storage[index + 1U], hangul_feature::vowel);
        if (trailing_index != 0U) {
            glyph_storage[index + 2U] = source;
            glyph_storage[index + 2U].code_point = trailing;
            glyph_storage[index + 2U].glyph_id = trailing_glyph;
            set_feature(
                glyph_storage[index + 2U], hangul_feature::trailing);
        }
        if (followed_by_noncombining_trailing) {
            set_feature(
                glyph_storage[index + replacement_count],
                hangul_feature::trailing);
        }
        index += replacement_count - 1U;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text

#include "progpu_native_text.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port provenance: ProGPU-owned
// GlyphPositionBuffer.ApplyArabicStretch at repository checkpoint c483e175.
// The native form keeps run discovery and expansion in caller-owned storage.

namespace progpu::native::text {
namespace {

constexpr std::uint32_t maximum_shaped_glyph_count = 1'048'576U;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) *destination = value;
}

bool is_stretch(open_type_arabic_action action) noexcept {
    return action == open_type_arabic_action::stretch_fixed ||
        action == open_type_arabic_action::stretch_repeating;
}

bool is_stretch_word_character(std::uint32_t code_point) noexcept {
    using enum unicode_general_category;
    switch (get_unicode_general_category(code_point)) {
        case other_not_assigned:
        case private_use:
        case modifier_letter:
        case other_letter:
        case spacing_combining_mark:
        case enclosing_mark:
        case nonspacing_mark:
        case decimal_digit_number:
        case letter_number:
        case other_number:
        case currency_symbol:
        case modifier_symbol:
        case math_symbol:
        case other_symbol:
            return true;
        default:
            return false;
    }
}

std::uint32_t source_index(
    std::uint32_t index,
    std::uint32_t count,
    bool right_to_left) noexcept {
    return right_to_left ? index : count - index - 1U;
}

bool try_width(
    const sfnt_font_view& font,
    const shaping_glyph& glyph,
    std::span<const std::int16_t> coordinates,
    std::int32_t& result,
    font_error* error) noexcept {
    if (glyph.glyph_id > 0xFFFFU) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    float width = 0.0F;
    if (!font.try_get_design_advance_width(
            static_cast<std::uint16_t>(glyph.glyph_id),
            coordinates,
            width,
            error)) {
        return false;
    }
    const auto rounded = std::lround(width);
    if (rounded < std::numeric_limits<std::int32_t>::min() ||
        rounded > std::numeric_limits<std::int32_t>::max()) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result = static_cast<std::int32_t>(rounded);
    return true;
}

bool try_build_runs(
    const sfnt_font_view& font,
    std::span<const shaping_glyph> glyphs,
    std::span<const open_type_arabic_action> actions,
    bool right_to_left,
    std::span<const std::int16_t> coordinates,
    std::span<arabic_stretch_run> runs,
    bool write_runs,
    arabic_stretch_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (actions.size() < glyphs.size() ||
        glyphs.size() > maximum_shaped_glyph_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const auto count = static_cast<std::uint32_t>(glyphs.size());
    std::uint64_t extra_glyphs = 0U;
    std::uint32_t run_count = 0U;
    auto glyph_at = [&](std::uint32_t index) -> const shaping_glyph& {
        return glyphs[source_index(index, count, right_to_left)];
    };
    auto action_at = [&](std::uint32_t index) {
        return actions[source_index(index, count, right_to_left)];
    };
    for (std::uint32_t index = count; index > 0U;) {
        if (!is_stretch(action_at(index - 1U))) {
            --index;
            continue;
        }
        const std::uint32_t end = index;
        std::int64_t fixed_width = 0;
        std::int64_t repeating_width = 0;
        std::uint32_t fixed_count = 0U;
        std::uint32_t repeating_count = 0U;
        while (index > 0U && is_stretch(action_at(index - 1U))) {
            --index;
            std::int32_t width = 0;
            if (!try_width(font, glyph_at(index), coordinates, width, error)) {
                return false;
            }
            if (action_at(index) == open_type_arabic_action::stretch_fixed) {
                fixed_width += width;
                ++fixed_count;
            } else {
                repeating_width += width;
                ++repeating_count;
            }
        }
        const std::uint32_t start = index;
        std::uint32_t context = start;
        std::int64_t total_width = 0;
        while (context > 0U && !is_stretch(action_at(context - 1U)) &&
            (glyph_at(context - 1U).code_point == 0x00ADU ||
             glyph_at(context - 1U).code_point == 0x034FU ||
             glyph_at(context - 1U).code_point == 0x061CU ||
             is_stretch_word_character(glyph_at(context - 1U).code_point))) {
            --context;
            total_width += glyph_at(context).advance_x;
        }
        std::int64_t remaining = total_width - fixed_width;
        std::int64_t copies = remaining > repeating_width &&
            repeating_width > 0
            ? remaining / repeating_width - 1
            : 0;
        std::int64_t overlap = 0;
        const std::int64_t shortfall =
            remaining - repeating_width * (copies + 1);
        if (shortfall > 0 && repeating_count > 0U) {
            ++copies;
            const std::int64_t excess =
                (copies + 1) * repeating_width - remaining;
            if (excess > 0) {
                overlap = excess /
                    (copies * static_cast<std::int64_t>(repeating_count));
                remaining = 0;
            }
        }
        const std::uint32_t base_glyphs = fixed_count + repeating_count;
        const std::uint32_t max_copies = repeating_count > 0U &&
            base_glyphs < 256U
            ? (256U - base_glyphs) / repeating_count
            : 0U;
        copies = std::clamp<std::int64_t>(copies, 0, max_copies);
        extra_glyphs += static_cast<std::uint64_t>(copies) * repeating_count;
        if (static_cast<std::uint64_t>(count) + extra_glyphs >
            maximum_shaped_glyph_count) {
            set_error(error, font_error::insufficient_buffer);
            return false;
        }
        if (write_runs) {
            if (run_count >= runs.size() ||
                remaining < std::numeric_limits<std::int32_t>::min() ||
                remaining > std::numeric_limits<std::int32_t>::max() ||
                overlap < std::numeric_limits<std::int32_t>::min() ||
                overlap > std::numeric_limits<std::int32_t>::max()) {
                set_error(error, font_error::insufficient_buffer);
                return false;
            }
            runs[run_count] = arabic_stretch_run{
                start,
                end,
                static_cast<std::uint32_t>(copies),
                static_cast<std::int32_t>(remaining),
                static_cast<std::int32_t>(overlap)};
        }
        ++run_count;
    }
    result.glyph_capacity = static_cast<std::uint32_t>(
        static_cast<std::uint64_t>(count) + extra_glyphs);
    result.run_capacity = run_count;
    set_error(error, font_error::none);
    return true;
}

std::int32_t clamp_i16(std::int64_t value) noexcept {
    return static_cast<std::int32_t>(std::clamp<std::int64_t>(
        value,
        std::numeric_limits<std::int16_t>::min(),
        std::numeric_limits<std::int16_t>::max()));
}

} // namespace

bool try_get_arabic_stretch_requirements(
    const sfnt_font_view& font,
    std::span<const shaping_glyph> glyphs,
    std::span<const open_type_arabic_action> actions,
    bool right_to_left,
    std::span<const std::int16_t> normalized_coordinates,
    arabic_stretch_requirements& result,
    font_error* error) noexcept {
    return try_build_runs(
        font,
        glyphs,
        actions,
        right_to_left,
        normalized_coordinates,
        {},
        false,
        result,
        error);
}

bool try_apply_arabic_stretch(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    std::span<const open_type_arabic_action> actions,
    bool right_to_left,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<arabic_stretch_run> run_scratch,
    font_error* error) noexcept {
    if (glyph_count > glyph_storage.size()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    arabic_stretch_requirements requirements{};
    if (!try_build_runs(
            font,
            glyph_storage.first(glyph_count),
            actions,
            right_to_left,
            normalized_coordinates,
            run_scratch,
            true,
            requirements,
            error)) {
        return false;
    }
    if (glyph_storage.size() < requirements.glyph_capacity ||
        run_scratch.size() < requirements.run_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    if (requirements.run_capacity == 0U) {
        set_error(error, font_error::none);
        return true;
    }
    const std::uint32_t source_count = glyph_count;
    if (!right_to_left) {
        std::reverse(glyph_storage.begin(),
            glyph_storage.begin() + source_count);
    }
    for (std::uint32_t run_index = 0U;
         run_index < requirements.run_capacity;
         ++run_index) {
        const auto& run = run_scratch[run_index];
        std::uint32_t context = run.start;
        while (context > 0U &&
            !is_stretch(actions[source_index(
                context - 1U, source_count, right_to_left)]) &&
            (glyph_storage[context - 1U].code_point == 0x00ADU ||
             glyph_storage[context - 1U].code_point == 0x034FU ||
             glyph_storage[context - 1U].code_point == 0x061CU ||
             is_stretch_word_character(
                glyph_storage[context - 1U].code_point))) {
            --context;
        }
        for (std::uint32_t index = context; index < run.end; ++index) {
            glyph_storage[index].flags = static_cast<shaping_glyph_flags>(
                static_cast<std::uint32_t>(glyph_storage[index].flags) |
                static_cast<std::uint32_t>(
                    shaping_glyph_flags::unsafe_to_break) |
                static_cast<std::uint32_t>(
                    shaping_glyph_flags::unsafe_to_concat));
        }
    }
    std::uint32_t write = requirements.glyph_capacity;
    std::uint32_t source = source_count;
    std::uint32_t run_index = 0U;
    while (source > 0U) {
        const auto action = actions[source_index(
            source - 1U, source_count, right_to_left)];
        if (!is_stretch(action)) {
            glyph_storage[--write] = glyph_storage[--source];
            continue;
        }
        const auto run = run_scratch[run_index++];
        source = run.start;
        std::int64_t x_offset = run.remaining_width / 2;
        for (std::uint32_t glyph_index = run.end;
             glyph_index > run.start;
             --glyph_index) {
            shaping_glyph glyph = glyph_storage[glyph_index - 1U];
            std::int32_t width = 0;
            if (!try_width(font, glyph, normalized_coordinates, width, error)) {
                return false;
            }
            const auto glyph_action = actions[source_index(
                glyph_index - 1U, source_count, right_to_left)];
            const std::uint32_t repeat =
                glyph_action == open_type_arabic_action::stretch_repeating
                ? run.copy_count + 1U
                : 1U;
            glyph.advance_x = 0;
            for (std::uint32_t copy = 0U; copy < repeat; ++copy) {
                if (right_to_left) {
                    x_offset -= width;
                    if (copy > 0U) x_offset += run.extra_repeat_overlap;
                }
                glyph.offset_x = clamp_i16(x_offset);
                glyph_storage[--write] = glyph;
                if (!right_to_left) {
                    x_offset += width;
                    if (copy > 0U) x_offset -= run.extra_repeat_overlap;
                }
            }
        }
    }
    glyph_count = requirements.glyph_capacity;
    if (!right_to_left) {
        std::reverse(glyph_storage.begin(), glyph_storage.begin() + glyph_count);
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text

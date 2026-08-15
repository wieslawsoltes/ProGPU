#include "progpu_native_text.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Native port of the grapheme-preserving fallback ownership used by
// ProGPU-owned FontManager/OpenTypeTextShaper. Platform discovery supplies a
// stable borrowed face span once; selection never calls back per glyph.

namespace progpu::native::text {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool validate_inputs(
    std::span<const unicode_scalar> input,
    std::span<const unicode_grapheme_cluster> graphemes,
    std::span<const font_fallback_candidate> candidates,
    std::uint32_t preferred_font_index) noexcept {
    if (candidates.empty() || preferred_font_index >= candidates.size() ||
        input.size() > std::numeric_limits<std::uint32_t>::max()) {
        return false;
    }
    std::size_t expected = 0U;
    for (const auto& grapheme : graphemes) {
        if (grapheme.scalar_count == 0U || grapheme.scalar_index != expected ||
            grapheme.scalar_index > input.size() ||
            grapheme.scalar_count > input.size() - grapheme.scalar_index) {
            return false;
        }
        expected += grapheme.scalar_count;
    }
    return expected == input.size();
}

bool supports_grapheme(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const unicode_grapheme_cluster& grapheme) noexcept {
    for (std::uint32_t offset = 0U; offset < grapheme.scalar_count; ++offset) {
        const std::uint32_t code_point =
            input[grapheme.scalar_index + offset].code_point;
        // Variation selectors and join controls modify the preceding scalar;
        // cmap format-14 support is handled by the shaping stage rather than
        // causing a false fallback split here.
        if ((code_point >= 0xFE00U && code_point <= 0xFE0FU) ||
            (code_point >= 0xE0100U && code_point <= 0xE01EFU) ||
            code_point == 0x200CU || code_point == 0x200DU) {
            continue;
        }
        std::uint16_t glyph = 0U;
        if (!font.try_get_glyph_index(code_point, glyph) || glyph == 0U) {
            return false;
        }
    }
    return true;
}

std::uint32_t choose_font(
    std::span<const unicode_scalar> input,
    const unicode_grapheme_cluster& grapheme,
    std::span<const font_fallback_candidate> candidates,
    std::uint32_t preferred,
    bool& missing) noexcept {
    missing = false;
    if (candidates[preferred].font != nullptr &&
        supports_grapheme(*candidates[preferred].font, input, grapheme)) {
        return preferred;
    }
    for (std::uint32_t index = 0U; index < candidates.size(); ++index) {
        if (index == preferred || candidates[index].font == nullptr) {
            continue;
        }
        if (supports_grapheme(*candidates[index].font, input, grapheme)) {
            return index;
        }
    }
    missing = true;
    if (candidates[preferred].font != nullptr) {
        return preferred;
    }
    for (std::uint32_t index = 0U; index < candidates.size(); ++index) {
        if (candidates[index].font != nullptr) {
            return index;
        }
    }
    return preferred;
}

bool itemize(
    std::span<const unicode_scalar> input,
    std::span<const unicode_grapheme_cluster> graphemes,
    std::span<const font_fallback_candidate> candidates,
    std::uint32_t preferred_font_index,
    std::span<font_fallback_run> output,
    bool write,
    std::uint32_t& result) noexcept {
    result = 0U;
    bool has_previous = false;
    bool previous_missing = false;
    std::uint32_t previous_font = 0U;
    for (const auto& grapheme : graphemes) {
        bool missing = false;
        const std::uint32_t font = choose_font(
            input, grapheme, candidates, preferred_font_index, missing);
        const unicode_scalar& first = input[grapheme.scalar_index];
        const unicode_scalar& last =
            input[grapheme.scalar_index + grapheme.scalar_count - 1U];
        const std::uint64_t input_end =
            static_cast<std::uint64_t>(last.input_index) + last.input_length;
        if (input_end > std::numeric_limits<std::uint32_t>::max() ||
            input_end < first.input_index) {
            return false;
        }
        if (has_previous && previous_font == font &&
            previous_missing == missing) {
            if (write) {
                auto& run = output[result - 1U];
                run.scalar_count += grapheme.scalar_count;
                run.input_length = static_cast<std::uint32_t>(input_end) -
                    run.input_index;
            }
            continue;
        }
        if (write) {
            output[result] = font_fallback_run{
                grapheme.scalar_index,
                grapheme.scalar_count,
                first.input_index,
                static_cast<std::uint32_t>(input_end) - first.input_index,
                font,
                missing};
        }
        ++result;
        has_previous = true;
        previous_font = font;
        previous_missing = missing;
    }
    return true;
}

} // namespace

bool try_get_font_fallback_run_count(
    std::span<const unicode_scalar> input,
    std::span<const unicode_grapheme_cluster> graphemes,
    std::span<const font_fallback_candidate> candidates,
    std::uint32_t preferred_font_index,
    std::uint32_t& result,
    font_error* error) noexcept {
    result = 0U;
    if (!validate_inputs(input, graphemes, candidates, preferred_font_index) ||
        !itemize(
            input,
            graphemes,
            candidates,
            preferred_font_index,
            {},
            false,
            result)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    set_error(error, font_error::none);
    return true;
}

bool try_itemize_font_fallback(
    std::span<const unicode_scalar> input,
    std::span<const unicode_grapheme_cluster> graphemes,
    std::span<const font_fallback_candidate> candidates,
    std::uint32_t preferred_font_index,
    std::span<font_fallback_run> output,
    std::uint32_t& written,
    font_error* error) noexcept {
    written = 0U;
    std::uint32_t required = 0U;
    if (!try_get_font_fallback_run_count(
            input,
            graphemes,
            candidates,
            preferred_font_index,
            required,
            error)) {
        return false;
    }
    if (output.size() < required) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    if (!itemize(
            input,
            graphemes,
            candidates,
            preferred_font_index,
            output,
            true,
            written)) {
        written = 0U;
        set_error(error, font_error::invalid_argument);
        return false;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text

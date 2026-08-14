#include "progpu_native_text.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Native port of the allocation-free positioned-line ownership in ProGPU-owned
// TextLayout. Unicode line-break classification remains an independent input so
// shaped runs, fallback runs, and paragraph reflow can be cached separately.

namespace progpu::native::text {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool valid_options(const text_layout_options& options) noexcept {
    return std::isfinite(options.scale) && options.scale > 0.0F &&
        std::isfinite(options.maximum_width) &&
        options.maximum_width >= 0.0F &&
        std::isfinite(options.line_height) && options.line_height >= 0.0F &&
        options.direction != shaping_direction::unspecified;
}

float horizontal_advance(
    const shaping_glyph& glyph,
    float scale) noexcept {
    return static_cast<float>(glyph.advance_x) * scale;
}

bool can_break_after(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    std::size_t index) noexcept {
    return breaks_after[index] != text_line_break_kind::prohibited &&
        (index + 1U == glyphs.size() ||
            glyphs[index].cluster != glyphs[index + 1U].cluster);
}

struct line_scan final {
    std::size_t end = 0U;
    float width = 0.0F;
    bool clipped = false;
};

line_scan scan_line(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    std::size_t start,
    bool final_allowed_line) noexcept {
    float width = 0.0F;
    float break_width = 0.0F;
    std::size_t last_break = start;
    for (std::size_t index = start; index < glyphs.size(); ++index) {
        const float next_width = width + horizontal_advance(
            glyphs[index], options.scale);
        const bool break_here = can_break_after(glyphs, breaks_after, index);
        const bool mandatory = break_here &&
            breaks_after[index] == text_line_break_kind::mandatory;
        if (mandatory) {
            return line_scan{index + 1U, next_width, false};
        }
        if (break_here) {
            last_break = index + 1U;
            break_width = next_width;
        }
        if (options.maximum_width > 0.0F &&
            next_width > options.maximum_width && index > start) {
            if (final_allowed_line) {
                return line_scan{
                    last_break > start ? last_break : index,
                    last_break > start ? break_width : width,
                    true};
            }
            return line_scan{
                last_break > start ? last_break : index,
                last_break > start ? break_width : width,
                false};
        }
        width = next_width;
    }
    return line_scan{glyphs.size(), width, false};
}

bool count_lines(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    std::uint32_t& result) noexcept {
    result = 0U;
    std::size_t start = 0U;
    while (start < glyphs.size()) {
        const bool final_allowed = options.maximum_lines != 0U &&
            result + 1U >= options.maximum_lines;
        const line_scan line = scan_line(
            glyphs, breaks_after, options, start, final_allowed);
        if (line.end <= start || line.end > glyphs.size()) {
            return false;
        }
        ++result;
        start = line.end;
        if (final_allowed) {
            break;
        }
    }
    return true;
}

} // namespace

bool try_get_text_layout_requirements(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    text_layout_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (glyphs.size() > std::numeric_limits<std::uint32_t>::max() ||
        breaks_after.size() != glyphs.size() || !valid_options(options)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::uint32_t line_count = 0U;
    if (!count_lines(glyphs, breaks_after, options, line_count)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    result = text_layout_requirements{
        static_cast<std::uint32_t>(glyphs.size()), line_count};
    set_error(error, font_error::none);
    return true;
}

bool try_layout_shaped_text(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    std::span<positioned_text_glyph> positioned_glyphs,
    std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count,
    std::uint32_t& line_count,
    font_error* error) noexcept {
    glyph_count = 0U;
    line_count = 0U;
    text_layout_requirements requirements{};
    if (!try_get_text_layout_requirements(
            glyphs, breaks_after, options, requirements, error)) {
        return false;
    }
    if (positioned_glyphs.size() < requirements.glyph_capacity ||
        lines.size() < requirements.line_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    std::size_t start = 0U;
    while (start < glyphs.size() && line_count < requirements.line_capacity) {
        const bool final_allowed = options.maximum_lines != 0U &&
            line_count + 1U >= options.maximum_lines;
        const line_scan line = scan_line(
            glyphs, breaks_after, options, start, final_allowed);
        const float baseline = static_cast<float>(line_count) *
            options.line_height;
        float cursor_x = 0.0F;
        float cursor_y = baseline;
        for (std::size_t index = start; index < line.end; ++index) {
            const shaping_glyph& glyph = glyphs[index];
            positioned_glyphs[index] = positioned_text_glyph{
                static_cast<std::uint32_t>(index),
                glyph.cluster,
                cursor_x + static_cast<float>(glyph.offset_x) * options.scale,
                cursor_y + static_cast<float>(glyph.offset_y) * options.scale,
                static_cast<float>(glyph.advance_x) * options.scale,
                static_cast<float>(glyph.advance_y) * options.scale};
            cursor_x += static_cast<float>(glyph.advance_x) * options.scale;
            cursor_y += static_cast<float>(glyph.advance_y) * options.scale;
        }
        const std::int32_t input_start = glyphs[start].cluster;
        const std::int32_t input_end = line.end < glyphs.size()
            ? glyphs[line.end].cluster
            : glyphs[line.end - 1U].cluster + 1;
        lines[line_count] = positioned_text_line{
            static_cast<std::uint32_t>(start),
            static_cast<std::uint32_t>(line.end - start),
            input_start,
            input_end,
            line.width,
            baseline,
            options.line_height,
            line.clipped || (final_allowed && line.end < glyphs.size())};
        ++line_count;
        start = line.end;
        if (final_allowed) {
            break;
        }
    }
    glyph_count = static_cast<std::uint32_t>(start);
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text

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
        options.direction != shaping_direction::unspecified &&
        static_cast<std::uint8_t>(options.trimming) <=
            static_cast<std::uint8_t>(text_trimming::word_ellipsis) &&
        static_cast<std::uint8_t>(options.alignment) <=
            static_cast<std::uint8_t>(text_alignment::justify) &&
        std::isfinite(options.ellipsis_advance) &&
        options.ellipsis_advance >= 0.0F;
}

float line_alignment_shift(
    const text_layout_options& options,
    float line_width) noexcept {
    if (options.maximum_width <= line_width) return 0.0F;
    switch (options.alignment) {
        case text_alignment::center:
            return (options.maximum_width - line_width) * 0.5F;
        case text_alignment::right:
            return options.maximum_width - line_width;
        case text_alignment::left:
        case text_alignment::justify:
            return 0.0F;
    }
    return 0.0F;
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

struct trimmed_line final {
    std::size_t end = 0U;
    float content_width = 0.0F;
};

trimmed_line trim_line(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    std::size_t start,
    std::size_t end,
    float width) noexcept {
    const float ellipsis_width = options.ellipsis_advance * options.scale;
    if (options.maximum_width <= 0.0F ||
        width + ellipsis_width <= options.maximum_width) {
        return trimmed_line{end, width};
    }

    std::size_t candidate = end;
    float candidate_width = width;
    while (candidate > start &&
        candidate_width + ellipsis_width > options.maximum_width) {
        const std::int32_t cluster = glyphs[candidate - 1U].cluster;
        do {
            --candidate;
            candidate_width -= horizontal_advance(
                glyphs[candidate], options.scale);
        } while (candidate > start &&
            glyphs[candidate - 1U].cluster == cluster);
    }
    if (options.trimming != text_trimming::word_ellipsis ||
        candidate == start) {
        return trimmed_line{candidate, candidate_width};
    }

    std::size_t word_end = start;
    float word_width = 0.0F;
    float scan_width = 0.0F;
    for (std::size_t index = start; index < candidate; ++index) {
        scan_width += horizontal_advance(glyphs[index], options.scale);
        if (can_break_after(glyphs, breaks_after, index)) {
            word_end = index + 1U;
            word_width = scan_width;
        }
    }
    return word_end > start
        ? trimmed_line{word_end, word_width}
        : trimmed_line{candidate, candidate_width};
}

line_scan scan_line(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    std::size_t start,
    bool final_allowed_line) noexcept {
    float width = 0.0F;
    float break_width = 0.0F;
    std::size_t last_break = start;
    float cluster_width = 0.0F;
    std::size_t last_cluster_break = start;
    for (std::size_t index = start; index < glyphs.size(); ++index) {
        if (index > start &&
            glyphs[index - 1U].cluster != glyphs[index].cluster) {
            last_cluster_break = index;
            cluster_width = width;
        }
        const float next_width = width + horizontal_advance(
            glyphs[index], options.scale);
        const bool break_here = can_break_after(glyphs, breaks_after, index);
        const bool mandatory = break_here &&
            breaks_after[index] == text_line_break_kind::mandatory;
        if (mandatory) {
            return line_scan{index + 1U, next_width, false};
        }
        if (options.maximum_width > 0.0F &&
            next_width > options.maximum_width && index > start) {
            if (last_break > start) {
                return line_scan{
                    last_break, break_width, final_allowed_line};
            }
            if (last_cluster_break > start) {
                return line_scan{
                    last_cluster_break, cluster_width, final_allowed_line};
            }
            std::size_t hard_end = index + 1U;
            float hard_width = next_width;
            while (hard_end < glyphs.size() &&
                glyphs[hard_end - 1U].cluster ==
                    glyphs[hard_end].cluster) {
                hard_width += horizontal_advance(
                    glyphs[hard_end], options.scale);
                ++hard_end;
            }
            return line_scan{
                hard_end, hard_width, final_allowed_line};
        }
        if (break_here) {
            last_break = index + 1U;
            break_width = next_width;
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
    const std::uint64_t glyph_capacity = glyphs.size();
    if (glyph_capacity > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    result = text_layout_requirements{
        static_cast<std::uint32_t>(glyph_capacity), line_count};
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
        const bool should_trim = options.trimming != text_trimming::none &&
            (line.clipped || (final_allowed && line.end < glyphs.size()));
        const trimmed_line visible = should_trim
            ? trim_line(
                glyphs,
                breaks_after,
                options,
                start,
                line.end,
                line.width)
            : trimmed_line{line.end, line.width};
        const float baseline = static_cast<float>(line_count) *
            options.line_height;
        float cursor_x = 0.0F;
        float cursor_y = baseline;
        for (std::size_t index = start; index < visible.end; ++index) {
            const shaping_glyph& glyph = glyphs[index];
            positioned_glyphs[index] = positioned_text_glyph{
                static_cast<std::uint32_t>(index),
                glyph.glyph_id,
                glyph.cluster,
                cursor_x + static_cast<float>(glyph.offset_x) * options.scale,
                cursor_y + static_cast<float>(glyph.offset_y) * options.scale,
                static_cast<float>(glyph.advance_x) * options.scale,
                static_cast<float>(glyph.advance_y) * options.scale};
            cursor_x += static_cast<float>(glyph.advance_x) * options.scale;
            cursor_y += static_cast<float>(glyph.advance_y) * options.scale;
        }
        std::size_t output_end = visible.end;
        if (should_trim) {
            const std::int32_t cluster = visible.end > start
                ? glyphs[visible.end - 1U].cluster
                : glyphs[start].cluster;
            positioned_glyphs[output_end] = positioned_text_glyph{
                std::numeric_limits<std::uint32_t>::max(),
                options.ellipsis_glyph_id,
                cluster,
                cursor_x,
                cursor_y,
                options.ellipsis_advance * options.scale,
                0.0F};
            ++output_end;
        }
        const float output_width = visible.content_width + (should_trim
            ? options.ellipsis_advance * options.scale
            : 0.0F);
        const float alignment_shift = line_alignment_shift(
            options, output_width);
        for (std::size_t index = start;
            alignment_shift > 0.0F && index < output_end;
            ++index) {
            positioned_glyphs[index].x += alignment_shift;
        }
        const std::int32_t input_start = glyphs[start].cluster;
        const std::int32_t input_end = visible.end < glyphs.size()
            ? glyphs[visible.end].cluster
            : glyphs[visible.end - 1U].cluster + 1;
        lines[line_count] = positioned_text_line{
            static_cast<std::uint32_t>(start),
            static_cast<std::uint32_t>(output_end - start),
            input_start,
            input_end,
            output_width,
            baseline,
            options.line_height,
            line.clipped || (final_allowed && line.end < glyphs.size())};
        ++line_count;
        start = line.end;
        if (final_allowed) {
            if (should_trim) {
                start = output_end;
            }
            break;
        }
    }
    glyph_count = static_cast<std::uint32_t>(start);
    set_error(error, font_error::none);
    return true;
}

bool try_layout_logical_shaped_text(
    std::span<const shaping_glyph> logical_glyphs,
    std::span<const text_line_break_kind> breaks_after,
    std::span<const std::int8_t> bidi_levels,
    std::int8_t paragraph_level,
    const text_layout_options& options,
    text_logical_layout_scratch scratch,
    std::span<positioned_text_glyph> positioned_glyphs,
    std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count,
    std::uint32_t& line_count,
    font_error* error) noexcept {
    glyph_count = 0U;
    line_count = 0U;
    text_layout_requirements requirements{};
    if (!try_get_text_layout_requirements(
            logical_glyphs,
            breaks_after,
            options,
            requirements,
            error) ||
        bidi_levels.size() != logical_glyphs.size() ||
        (paragraph_level != 0 && paragraph_level != 1) ||
        !std::all_of(
            bidi_levels.begin(),
            bidi_levels.end(),
            [](std::int8_t level) { return level >= 0 && level <= 125; })) {
        if (bidi_levels.size() != logical_glyphs.size() ||
            (paragraph_level != 0 && paragraph_level != 1) ||
            !std::all_of(
                bidi_levels.begin(),
                bidi_levels.end(),
                [](std::int8_t level) {
                    return level >= 0 && level <= 125;
                })) {
            set_error(error, font_error::invalid_argument);
        }
        return false;
    }
    if (scratch.visual_groups.size() < requirements.glyph_capacity ||
        scratch.visual_indices.size() < requirements.glyph_capacity ||
        positioned_glyphs.size() < requirements.glyph_capacity ||
        lines.size() < requirements.line_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    std::size_t input_start_index = 0U;
    std::size_t output_cursor = 0U;
    while (input_start_index < logical_glyphs.size() &&
        line_count < requirements.line_capacity) {
        const bool final_allowed = options.maximum_lines != 0U &&
            line_count + 1U >= options.maximum_lines;
        const line_scan line = scan_line(
            logical_glyphs,
            breaks_after,
            options,
            input_start_index,
            final_allowed);
        const bool should_trim = options.trimming != text_trimming::none &&
            (line.clipped ||
                (final_allowed && line.end < logical_glyphs.size()));
        const trimmed_line visible = should_trim
            ? trim_line(
                logical_glyphs,
                breaks_after,
                options,
                input_start_index,
                line.end,
                line.width)
            : trimmed_line{line.end, line.width};

        const auto line_logical = logical_glyphs.subspan(
            input_start_index, visible.end - input_start_index);
        const auto line_levels = bidi_levels.subspan(
            input_start_index, visible.end - input_start_index);
        std::uint32_t visual_count = 0U;
        if (!try_get_text_line_visual_indices(
                line_logical,
                line_levels,
                paragraph_level,
                scratch.visual_groups,
                scratch.visual_indices,
                visual_count,
                error)) {
            glyph_count = 0U;
            line_count = 0U;
            return false;
        }

        const std::size_t output_start = output_cursor;
        const float baseline = static_cast<float>(line_count) *
            options.line_height;
        float cursor_x = 0.0F;
        float cursor_y = baseline;
        for (std::uint32_t visual_index = 0U;
            visual_index < visual_count;
            ++visual_index) {
            const std::size_t source_index = input_start_index +
                scratch.visual_indices[visual_index];
            const shaping_glyph& glyph = logical_glyphs[source_index];
            positioned_glyphs[output_cursor++] = positioned_text_glyph{
                static_cast<std::uint32_t>(source_index),
                glyph.glyph_id,
                glyph.cluster,
                cursor_x + static_cast<float>(glyph.offset_x) * options.scale,
                cursor_y + static_cast<float>(glyph.offset_y) * options.scale,
                static_cast<float>(glyph.advance_x) * options.scale,
                static_cast<float>(glyph.advance_y) * options.scale};
            cursor_x += static_cast<float>(glyph.advance_x) * options.scale;
            cursor_y += static_cast<float>(glyph.advance_y) * options.scale;
        }
        if (should_trim) {
            const std::int32_t cluster = visible.end > input_start_index
                ? logical_glyphs[visible.end - 1U].cluster
                : logical_glyphs[input_start_index].cluster;
            positioned_glyphs[output_cursor++] = positioned_text_glyph{
                std::numeric_limits<std::uint32_t>::max(),
                options.ellipsis_glyph_id,
                cluster,
                cursor_x,
                cursor_y,
                options.ellipsis_advance * options.scale,
                0.0F};
        }

        const float output_width = visible.content_width + (should_trim
            ? options.ellipsis_advance * options.scale
            : 0.0F);
        const float alignment_shift = line_alignment_shift(
            options, output_width);
        for (std::size_t index = output_start;
            alignment_shift > 0.0F && index < output_cursor;
            ++index) {
            positioned_glyphs[index].x += alignment_shift;
        }

        const std::int32_t input_start =
            logical_glyphs[input_start_index].cluster;
        const std::int32_t input_end = visible.end < logical_glyphs.size()
            ? logical_glyphs[visible.end].cluster
            : logical_glyphs[visible.end - 1U].cluster + 1;
        lines[line_count] = positioned_text_line{
            static_cast<std::uint32_t>(output_start),
            static_cast<std::uint32_t>(output_cursor - output_start),
            input_start,
            input_end,
            output_width,
            baseline,
            options.line_height,
            line.clipped ||
                (final_allowed && line.end < logical_glyphs.size())};
        ++line_count;
        input_start_index = line.end;
        if (final_allowed) break;
    }
    glyph_count = static_cast<std::uint32_t>(output_cursor);
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text

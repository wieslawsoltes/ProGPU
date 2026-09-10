#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

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
        (options.direction == shaping_direction::left_to_right ||
            options.direction == shaping_direction::right_to_left) &&
        static_cast<std::uint8_t>(options.trimming) <=
            static_cast<std::uint8_t>(text_trimming::word_ellipsis) &&
        static_cast<std::uint8_t>(options.alignment) <=
            static_cast<std::uint8_t>(text_alignment::justify) &&
        std::isfinite(options.ellipsis_advance) &&
        options.ellipsis_advance >= 0.0F && std::isfinite(options.ellipsis_advance * options.scale) &&
        std::isfinite(options.collapse_width) && (options.collapse_width == -1.0F || options.collapse_width >= 0.0F) &&
        (options.collapse_width < 0.0F ||
            (options.maximum_lines > 0U && options.trimming != text_trimming::none));
}

bool translate_glyph_position(positioned_text_glyph& glyph, float x, float y) noexcept {
    // Independent coordinate lanes; source-index restoration remains separate.
#if defined(__aarch64__) || defined(_M_ARM64)
    float coordinates[2]{glyph.x, glyph.y};
    const float offsets[2]{x, y};
    const auto moved = vadd_f32(vld1_f32(coordinates), vld1_f32(offsets));
    const auto valid = vcle_f32(vabs_f32(moved), vdup_n_f32(std::numeric_limits<float>::max()));
    if (vget_lane_u32(valid, 0) == 0U || vget_lane_u32(valid, 1) == 0U) return false;
    vst1_f32(coordinates, moved); glyph.x = coordinates[0]; glyph.y = coordinates[1];
#elif defined(__SSE2__) || defined(_M_X64)
    const auto moved = _mm_add_ps(_mm_set_ps(0, 0, glyph.y, glyph.x), _mm_set_ps(0, 0, y, x));
    const auto absolute = _mm_andnot_ps(_mm_set1_ps(-0.0F), moved);
    if ((_mm_movemask_ps(_mm_cmple_ps(absolute, _mm_set1_ps(std::numeric_limits<float>::max()))) & 3) != 3) return false;
    float coordinates[4]; _mm_storeu_ps(coordinates, moved);
    glyph.x = coordinates[0]; glyph.y = coordinates[1];
#else
    const float moved_x = glyph.x + x, moved_y = glyph.y + y;
    if (!std::isfinite(moved_x) || !std::isfinite(moved_y)) return false;
    glyph.x = moved_x; glyph.y = moved_y;
#endif
    return true;
}

// Paired independent ascent/descent reductions. The ordered line-height
// prefix is separate; no GPU dispatch or per-item allocation is justified.
bool metric_envelope(std::span<const text_item_metrics> metrics,
    text_item_metrics& maximum) noexcept {
    maximum = {};
#if defined(__aarch64__) || defined(_M_ARM64)
    auto peak = vdup_n_f32(0.0F);
    const auto limit = vdup_n_f32(std::numeric_limits<float>::max());
    for (const auto& metric : metrics) {
        const float pair[2]{metric.ascent, metric.descent};
        const auto value = vld1_f32(pair);
        const auto valid = vand_u32(vcge_f32(value, vdup_n_f32(0.0F)), vcle_f32(value, limit));
        if (vget_lane_u32(valid, 0) == 0U || vget_lane_u32(valid, 1) == 0U) return false;
        peak = vmax_f32(peak, value);
    }
    maximum = {vget_lane_f32(peak, 0), vget_lane_f32(peak, 1)};
#elif defined(__SSE2__) || defined(_M_X64)
    auto peak = _mm_setzero_ps();
    const auto limit = _mm_set1_ps(std::numeric_limits<float>::max());
    for (const auto& metric : metrics) {
        const auto value = _mm_setr_ps(metric.ascent, metric.descent, 0.0F, 0.0F);
        if (_mm_movemask_ps(_mm_and_ps(_mm_cmpge_ps(value, _mm_setzero_ps()),
                _mm_cmple_ps(value, limit))) != 15) return false;
        peak = _mm_max_ps(peak, value);
    }
    float values[4]{};
    _mm_storeu_ps(values, peak);
    maximum = {values[0], values[1]};
#else
    // Scalar reference only where the desktop SIMD baseline is unavailable.
    for (const auto& metric : metrics) {
        if (!std::isfinite(metric.ascent) || metric.ascent < 0.0F ||
            !std::isfinite(metric.descent) || metric.descent < 0.0F) return false;
        maximum.ascent = std::max(maximum.ascent, metric.ascent);
        maximum.descent = std::max(maximum.descent, metric.descent);
    }
#endif
    return true;
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

float scale_at(std::span<const float> scales, std::size_t index,
    const text_layout_options& options) noexcept {
    return scales.empty() ? options.scale : scales[index];
}

float layout_advance(const shaping_glyph& glyph, float scale, float width, text_tab_options tabs) noexcept {
    if (tabs.interval <= 0.0F || glyph.glyph_id != text_tab_glyph_id) return horizontal_advance(glyph, scale);
    double remainder = std::fmod(static_cast<double>(width) + tabs.origin, static_cast<double>(tabs.interval));
    if (remainder < 0.0) remainder += tabs.interval;
    const float advance = static_cast<float>(static_cast<double>(tabs.interval) - remainder);
    return std::isfinite(width + advance) && width + advance > width ? advance : std::numeric_limits<float>::infinity();
}

std::array<float, 4> scale_metrics(const shaping_glyph& glyph, float scale) noexcept {
    std::array<float, 4> output{};
#if defined(__aarch64__) || defined(_M_ARM64)
    const std::int32_t input[4]{glyph.advance_x, glyph.advance_y, glyph.offset_x, glyph.offset_y};
    vst1q_f32(output.data(), vmulq_n_f32(vcvtq_f32_s32(vld1q_s32(input)), scale));
#elif defined(__SSE2__) || defined(_M_X64)
    const auto input = _mm_set_epi32(glyph.offset_y, glyph.offset_x, glyph.advance_y, glyph.advance_x);
    _mm_storeu_ps(output.data(), _mm_mul_ps(_mm_cvtepi32_ps(input), _mm_set1_ps(scale)));
#else
    // Fixed four-lane reference on architectures without the desktop SIMD baseline.
    output = {static_cast<float>(glyph.advance_x) * scale, static_cast<float>(glyph.advance_y) * scale,
        static_cast<float>(glyph.offset_x) * scale, static_cast<float>(glyph.offset_y) * scale};
#endif
    return output;
}

bool valid_scales(std::span<const shaping_glyph> glyphs,
    std::span<const float> scales) noexcept {
    if (scales.empty()) return true;
    if (scales.size() != glyphs.size()) return false;
    // Four independent metric lanes per glyph; no integer rescaling or rounding.
    // Non-SIMD architectures retain the scalar reference; supported desktop
    // baselines use NEON or SSE2, without an alignment precondition.
    for (std::size_t i = 0; i < scales.size(); ++i) {
        const float scale = scales[i];
        if (!std::isfinite(scale) || scale <= 0.0F) return false;
        const auto metrics = scale_metrics(glyphs[i], scale);
#if defined(__aarch64__) || defined(_M_ARM64)
        const auto product = vld1q_f32(metrics.data());
        if (vminvq_u32(vcleq_f32(vabsq_f32(product), vdupq_n_f32(std::numeric_limits<float>::max()))) == 0U)
            return false;
#elif defined(__SSE2__) || defined(_M_X64)
        const auto product = _mm_loadu_ps(metrics.data());
        const auto absolute = _mm_andnot_ps(_mm_set1_ps(-0.0F), product);
        if (_mm_movemask_ps(_mm_cmple_ps(absolute, _mm_set1_ps(std::numeric_limits<float>::max()))) != 15)
            return false;
#else
        for (const auto metric : metrics)
            if (!std::isfinite(metric)) return false;
#endif
    }
    return true;
}

bool has_flag(
    shaping_glyph_flags value,
    shaping_glyph_flags flag) noexcept {
    return (static_cast<std::uint32_t>(value) &
        static_cast<std::uint32_t>(flag)) != 0U;
}

bool trailing_space(std::uint32_t cp) noexcept {
    return (cp >= 0x09U && cp <= 0x0DU) || cp == 0x20U || cp == 0x85U || cp == 0xA0U ||
        cp == 0x1680U || (cp >= 0x2000U && cp <= 0x200BU) || cp == 0x2028U ||
        cp == 0x2029U || cp == 0x202FU || cp == 0x205FU || cp == 0x3000U;
}

bool is_safe_break_before(
    std::span<const shaping_glyph> glyphs,
    std::size_t index) noexcept {
    return index == 0U || index >= glyphs.size() ||
        (glyphs[index - 1U].cluster != glyphs[index].cluster &&
            !has_flag(
                glyphs[index].flags,
                shaping_glyph_flags::unsafe_to_break));
}

bool can_break_after(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    std::size_t index) noexcept {
    return breaks_after[index] != text_line_break_kind::prohibited &&
        is_safe_break_before(glyphs, index + 1U);
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
    float width,
    std::span<const float> scales = {}, text_tab_options tabs = {}) noexcept {
    const float ellipsis_width = options.ellipsis_advance * options.scale;
    const float limit = options.collapse_width >= 0.0F ? options.collapse_width : options.maximum_width;
    if ((limit <= 0.0F && options.collapse_width < 0.0F) || width + ellipsis_width <= limit) {
        return trimmed_line{end, width};
    }

    // Reuse the forward line metric policy: tab advances depend on the
    // preceding width and cannot be recovered by subtracting font advances.
    // Only retain safe shaping boundaries; a changed cluster id alone does
    // not authorize cutting context-dependent shaping without reshaping.
    // This prefix/state scan is dependency-bound, O(N), allocation-free.
    trimmed_line character{start, 0.0F};
    trimmed_line word{start, 0.0F};
    float scan_width = 0.0F;
    for (std::size_t index = start; index < end; ++index) {
        scan_width += layout_advance(glyphs[index], scale_at(scales, index, options), scan_width, tabs);
        if (scan_width + ellipsis_width <= limit &&
            is_safe_break_before(glyphs, index + 1U)) {
            character = {index + 1U, scan_width};
            if (can_break_after(glyphs, breaks_after, index)) word = character;
        }
    }
    return options.trimming == text_trimming::word_ellipsis && word.end > start
        ? word : character;
}

line_scan scan_line(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    std::size_t start,
    bool final_allowed_line,
    std::span<const float> scales = {}, text_tab_options tabs = {}) noexcept {
    float width = 0.0F;
    float break_width = 0.0F;
    std::size_t last_break = start;
    float cluster_width = 0.0F;
    std::size_t last_cluster_break = start;
    for (std::size_t index = start; index < glyphs.size(); ++index) {
        if (index > start && is_safe_break_before(glyphs, index)) {
            last_cluster_break = index;
            cluster_width = width;
        }
        const float next_width = width + layout_advance(
            glyphs[index], scale_at(scales, index, options), width, tabs);
        if (!std::isfinite(next_width)) return line_scan{index + 1U, next_width, false};
        const bool break_here = can_break_after(glyphs, breaks_after, index);
        const bool mandatory = break_here &&
            breaks_after[index] == text_line_break_kind::mandatory;
        if (options.maximum_width > 0.0F &&
            next_width > options.maximum_width && index > start) {
            if (last_break > start) {
                return line_scan{
                    last_break, break_width, final_allowed_line};
            }
            if (!tabs.allow_emergency_break) {
                if (break_here) return line_scan{index + 1U, next_width, final_allowed_line};
                width = next_width;
                continue;
            }
            if (last_cluster_break > start) {
                return line_scan{
                    last_cluster_break, cluster_width, final_allowed_line};
            }
            std::size_t hard_end = index + 1U;
            float hard_width = next_width;
            while (hard_end < glyphs.size() &&
                !is_safe_break_before(glyphs, hard_end)) {
                hard_width += layout_advance(
                    glyphs[hard_end], scale_at(scales, hard_end, options), hard_width, tabs);
                ++hard_end;
            }
            return line_scan{
                hard_end, hard_width, final_allowed_line};
        }
        // A paragraph-end mandatory break does not exempt its final glyph from
        // wrapping. Resolve an earlier safe boundary before consuming it.
        if (mandatory) {
            return line_scan{index + 1U, next_width, false};
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
    std::uint32_t& result,
    std::span<const float> scales = {}, text_tab_options tabs = {}) noexcept {
    result = 0U;
    std::size_t start = 0U;
    while (start < glyphs.size()) {
        const bool final_allowed = options.maximum_lines != 0U &&
            result + 1U >= options.maximum_lines;
        const line_scan line = scan_line(
            glyphs, breaks_after, options, start, final_allowed, scales, tabs);
        if (line.end <= start || line.end > glyphs.size() || !std::isfinite(line.width)) {
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

bool try_measure_text_intrinsic_widths(std::span<const unicode_scalar> input,
    std::span<const shaping_glyph> glyphs, std::span<const text_line_break_kind> breaks_after,
    std::span<const float> scales, const text_layout_options& options,
    text_tab_options tabs, text_intrinsic_widths& result, font_error* error) noexcept {
    result = {};
    const auto invalid = [&]() noexcept { set_error(error, font_error::invalid_argument); return false; };
    if (breaks_after.size() != glyphs.size() || !valid_options(options) ||
        !valid_scales(glyphs, scales) || !std::isfinite(tabs.interval) || tabs.interval < 0.0F ||
        !std::isfinite(tabs.origin) || (input.empty() && !glyphs.empty())) return invalid();
    for (std::size_t i = 0; i < input.size(); ++i) {
        if (input[i].input_length == 0U || (i != 0U &&
            input[i].input_index < static_cast<std::uint64_t>(input[i - 1U].input_index) + input[i - 1U].input_length))
            return invalid();
    }
    // Unicode White_Space plus zero-width break controls. Classification is
    // based on the whole source cluster, not a ligature's first code point.
    text_intrinsic_widths measured{};
    float word = 0.0F, paragraph = 0.0F, word_visible = 0.0F, paragraph_visible = 0.0F;
    std::size_t scalar = 0U, start = 0U;
    while (start < glyphs.size()) {
        const auto cluster = glyphs[start].cluster;
        if (cluster < 0 || scalar >= input.size() || input[scalar].input_index != static_cast<std::uint32_t>(cluster))
            return invalid();
        std::size_t end = start + 1U;
        while (end < glyphs.size() && glyphs[end].cluster == cluster) ++end;
        if (end < glyphs.size() && glyphs[end].cluster <= cluster) return invalid();
        const auto next = end < glyphs.size() ? static_cast<std::uint64_t>(glyphs[end].cluster) :
            static_cast<std::uint64_t>(input.back().input_index) + input.back().input_length;
        bool whitespace = true;
        while (scalar < input.size() && input[scalar].input_index < next)
            whitespace &= trailing_space(input[scalar++].code_point);
        for (std::size_t i = start; i < end; ++i) {
            if (static_cast<std::uint8_t>(breaks_after[i]) > static_cast<std::uint8_t>(text_line_break_kind::mandatory))
                return invalid();
            const auto scale = scale_at(scales, i, options);
            word += layout_advance(glyphs[i], scale, word, tabs);
            paragraph += layout_advance(glyphs[i], scale, paragraph, tabs);
            if (!std::isfinite(word) || !std::isfinite(paragraph) || word < 0.0F || paragraph < 0.0F)
                return invalid();
        }
        if (!whitespace) { word_visible = word; paragraph_visible = paragraph; }
        if (can_break_after(glyphs, breaks_after, end - 1U)) {
            measured.minimum = std::max(measured.minimum, word_visible);
            word = word_visible = 0.0F;
            if (breaks_after[end - 1U] == text_line_break_kind::mandatory) {
                measured.maximum = std::max(measured.maximum, paragraph_visible);
                paragraph = paragraph_visible = 0.0F;
            }
        }
        start = end;
    }
    measured.minimum = std::max(measured.minimum, word_visible);
    measured.maximum = std::max(measured.maximum, paragraph_visible);
    result = measured;
    set_error(error, font_error::none);
    return true;
}

bool try_get_text_layout_requirements(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    text_layout_requirements& result,
    font_error* error) noexcept {
    return try_get_scaled_text_layout_requirements(glyphs, breaks_after, {}, options, result, error);
}

bool try_get_scaled_text_layout_requirements(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    std::span<const float> glyph_scales,
    const text_layout_options& options,
    text_layout_requirements& result,
    font_error* error) noexcept {
    return try_get_tabbed_text_layout_requirements(glyphs, breaks_after, glyph_scales, options, {}, result, error);
}

bool try_get_tabbed_text_layout_requirements(
    std::span<const shaping_glyph> glyphs, std::span<const text_line_break_kind> breaks_after,
    std::span<const float> glyph_scales, const text_layout_options& options,
    text_tab_options tabs, text_layout_requirements& result, font_error* error) noexcept {
    result = {};
    if (glyphs.size() > std::numeric_limits<std::uint32_t>::max() ||
        breaks_after.size() != glyphs.size() || !valid_options(options) || !valid_scales(glyphs, glyph_scales) ||
        !std::isfinite(tabs.interval) || tabs.interval < 0.0F || !std::isfinite(tabs.origin)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::uint32_t line_count = 0U;
    if (!count_lines(glyphs, breaks_after, options, line_count, glyph_scales, tabs)) {
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
    if (options.collapse_width >= 0.0F) {
        set_error(error, font_error::invalid_argument);
        return false; // Source-preserving collapse requires logical/bidi layout.
    }
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
    return try_layout_scaled_logical_shaped_text(logical_glyphs, breaks_after, bidi_levels, {}, paragraph_level,
        options, scratch, positioned_glyphs, lines, glyph_count, line_count, error);
}

bool try_layout_scaled_logical_shaped_text(
    std::span<const shaping_glyph> logical_glyphs,
    std::span<const text_line_break_kind> breaks_after,
    std::span<const std::int8_t> bidi_levels,
    std::span<const float> glyph_scales,
    std::int8_t paragraph_level,
    const text_layout_options& options,
    text_logical_layout_scratch scratch,
    std::span<positioned_text_glyph> positioned_glyphs,
    std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count,
    std::uint32_t& line_count,
    font_error* error) noexcept {
    return try_layout_tabbed_logical_shaped_text(logical_glyphs, breaks_after, bidi_levels, glyph_scales,
        paragraph_level, options, {}, {}, scratch, positioned_glyphs, lines, glyph_count, line_count, error);
}

bool try_classify_text_justification(std::span<const unicode_scalar> input,
    std::span<const shaping_glyph> glyphs, std::span<text_justification_class> classes,
    font_error* error) noexcept {
    const auto invalid = [&]() noexcept { set_error(error, font_error::invalid_argument); return false; };
    if (classes.size() < glyphs.size() || (input.empty() && !glyphs.empty())) return invalid();
    for (std::size_t i = 0; i < input.size(); ++i)
        if (input[i].input_length == 0U || (i != 0U && input[i].input_index <
                static_cast<std::uint64_t>(input[i - 1U].input_index) + input[i - 1U].input_length))
            return invalid();
    // O(S + G), allocation-free. Cluster topology requires an ordered scan;
    // glyph metric arithmetic remains on the existing four-lane SIMD path.
    std::size_t scalar = 0U, start = 0U;
    while (start < glyphs.size()) {
        const auto cluster = glyphs[start].cluster;
        if (cluster < 0 || scalar >= input.size() || input[scalar].input_index != static_cast<std::uint32_t>(cluster))
            return invalid();
        std::size_t end = start + 1U;
        while (end < glyphs.size() && glyphs[end].cluster == cluster) ++end;
        if (end < glyphs.size() && glyphs[end].cluster <= cluster) return invalid();
        const auto next = end < glyphs.size() ? static_cast<std::uint64_t>(glyphs[end].cluster) :
            static_cast<std::uint64_t>(input.back().input_index) + input.back().input_length;
        bool whitespace = true, word_space = true;
        while (scalar < input.size() && input[scalar].input_index < next) {
            const auto cp = input[scalar++].code_point;
            whitespace &= trailing_space(cp);
            word_space &= cp == 0x20U;
        }
        const auto value = word_space ? text_justification_class::word_space :
            whitespace ? text_justification_class::whitespace : text_justification_class::content;
        std::fill(classes.begin() + start, classes.begin() + end, value);
        start = end;
    }
    set_error(error, font_error::none);
    return true;
}

bool try_resolve_text_line_intervals(text_exclusion_rectangle band,
    std::span<const text_exclusion_rectangle> exclusions,
    std::span<text_line_interval> scratch, std::span<text_line_interval> output,
    std::uint32_t& count, float& next_y, font_error* error) noexcept {
    count = 0U;
    const auto invalid = [&]() noexcept { set_error(error, font_error::invalid_argument); return false; };
    const auto valid = [](text_exclusion_rectangle r) noexcept {
        // Independent coordinate lanes; ordering remains a scalar dependency.
        const float values[]{r.left, r.top, r.right, r.bottom};
#if defined(__aarch64__) || defined(_M_ARM64)
        const auto v = vabsq_f32(vld1q_f32(values));
        const auto mask = vcleq_f32(v, vdupq_n_f32(std::numeric_limits<float>::max()));
        const bool finite = vminvq_u32(mask) != 0U;
#elif defined(__SSE2__) || defined(_M_X64)
        const auto v = _mm_loadu_ps(values);
        const auto limit = _mm_set1_ps(std::numeric_limits<float>::max());
        const bool finite = _mm_movemask_ps(_mm_and_ps(_mm_cmple_ps(v, limit),
            _mm_cmpge_ps(v, _mm_sub_ps(_mm_setzero_ps(), limit)))) == 15;
#else
        // Fixed four-value reference for targets without a desktop SIMD baseline.
        const bool finite = std::all_of(std::begin(values), std::end(values),
            [](float value) { return std::isfinite(value); });
#endif
        return finite && r.left <= r.right && r.top <= r.bottom;
    };
    if (exclusions.size() > (1U << 20U) || !valid(band) || band.top == band.bottom) return invalid();
    if (scratch.size() < exclusions.size() || output.size() <= exclusions.size()) {
        set_error(error, font_error::insufficient_buffer); return false;
    }
    struct memory_region { std::uintptr_t begin; std::size_t size; };
    const memory_region regions[]{
        {reinterpret_cast<std::uintptr_t>(exclusions.data()), exclusions.size_bytes()},
        {reinterpret_cast<std::uintptr_t>(scratch.data()), exclusions.size() * sizeof(text_line_interval)},
        {reinterpret_cast<std::uintptr_t>(output.data()), (exclusions.size() + 1U) * sizeof(text_line_interval)}};
    for (std::size_t i = 0; i < 3U; ++i) {
        const auto a = regions[i];
        if (a.size == 0U) continue;
        if (a.begin == 0U || a.begin > UINTPTR_MAX - a.size) return invalid();
        for (std::size_t j = 0; j < i; ++j)
            if (regions[j].size != 0U && a.begin < regions[j].begin + regions[j].size &&
                regions[j].begin < a.begin + a.size) return invalid();
    }
    for (const auto r : exclusions) if (!valid(r)) return invalid();
    std::size_t used = 0U;
    float next = std::numeric_limits<float>::max();
    for (const auto r : exclusions) {
        if (r.left == r.right || r.top == r.bottom || r.top >= band.bottom || r.bottom <= band.top) continue;
        const float left = std::max(band.left, r.left), right = std::min(band.right, r.right);
        if (left >= right) continue;
        scratch[used++] = {left, right};
        next = std::min(next, r.bottom);
    }
    if (used > 1U)
        std::sort(scratch.begin(), scratch.begin() + used, [](auto a, auto b) {
            return a.left < b.left || (a.left == b.left && a.right < b.right);
        });
    float cursor = band.left;
    for (std::size_t i = 0; i < used; ++i) {
        const auto interval = scratch[i];
        if (interval.left > cursor) output[count++] = {cursor, interval.left};
        cursor = std::max(cursor, interval.right);
    }
    if (cursor < band.right) output[count++] = {cursor, band.right};
    next_y = used == 0U ? band.top : next;
    set_error(error, font_error::none);
    return true;
}

bool try_layout_tabbed_logical_shaped_text(
    std::span<const shaping_glyph> logical_glyphs, std::span<const text_line_break_kind> breaks_after,
    std::span<const std::int8_t> bidi_levels, std::span<const float> glyph_scales,
    std::int8_t paragraph_level, const text_layout_options& options, text_tab_options tabs,
    std::span<float> advance_scratch, text_logical_layout_scratch scratch,
    std::span<positioned_text_glyph> positioned_glyphs, std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count, std::uint32_t& line_count, font_error* error) noexcept {
    return try_layout_justified_logical_shaped_text(logical_glyphs, breaks_after, bidi_levels,
        glyph_scales, paragraph_level, options, tabs, advance_scratch, scratch,
        positioned_glyphs, lines, glyph_count, line_count, {}, error);
}

static bool fit_exclusion_band_core(std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after, std::span<const float> scales,
    std::uint32_t start, const text_layout_options& options, text_tab_options tabs,
    text_exclusion_rectangle band, std::span<const text_exclusion_rectangle> exclusions,
    std::span<text_line_interval> scratch, std::span<text_line_interval> intervals,
    std::span<text_line_fragment> fragments, std::uint32_t& fragment_count,
    std::uint32_t& next_glyph, float& next_y, bool validated, font_error* error) noexcept {
    fragment_count = 0U;
    next_glyph = start;
    const auto invalid = [&]() noexcept { set_error(error, font_error::invalid_argument); return false; };
    if (glyphs.size() > UINT32_MAX || start > glyphs.size() ||
        breaks_after.size() != glyphs.size() || !valid_options(options) ||
        options.trimming != text_trimming::none || (!validated && !valid_scales(glyphs, scales)) ||
        !std::isfinite(tabs.interval) || tabs.interval < 0 || !std::isfinite(tabs.origin) ||
        !is_safe_break_before(glyphs, start)) return invalid();
    if (exclusions.size() >= fragments.size()) {
        set_error(error, font_error::insufficient_buffer); return false;
    }
    std::uint32_t interval_count = 0U;
    float retry_y{};
    if (!try_resolve_text_line_intervals(band, exclusions, scratch, intervals, interval_count, retry_y, error)) return false;
    const bool rtl = options.direction == shaping_direction::right_to_left;
    for (std::uint32_t i = 0; i < interval_count && next_glyph < glyphs.size(); ++i) {
        const auto interval = intervals[rtl ? interval_count - 1U - i : i];
        auto local = options;
        local.maximum_width = interval.right - interval.left;
        auto local_tabs = tabs;
        local_tabs.origin += rtl ? band.right - interval.right : interval.left - band.left;
        if (!std::isfinite(local.maximum_width) || !std::isfinite(local_tabs.origin)) {
            fragment_count = 0U; next_glyph = start; return invalid();
        }
        const auto fitted = scan_line(glyphs, breaks_after, local, next_glyph, false, scales, local_tabs);
        if (!std::isfinite(fitted.width)) {
            fragment_count = 0U; next_glyph = start; return invalid();
        }
        const bool full_width = interval.left == band.left && interval.right == band.right;
        if (fitted.width > local.maximum_width && !full_width) continue;
        fragments[fragment_count++] = {next_glyph, static_cast<std::uint32_t>(fitted.end - next_glyph),
            interval.left, local.maximum_width, fitted.width};
        next_glyph = static_cast<std::uint32_t>(fitted.end);
        if (next_glyph != 0U && breaks_after[next_glyph - 1U] == text_line_break_kind::mandatory) break;
    }
    next_y = retry_y;
    set_error(error, font_error::none);
    return true;
}

bool try_fit_text_exclusion_band(std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks, std::span<const float> scales,
    std::uint32_t start, const text_layout_options& options, text_tab_options tabs,
    text_exclusion_rectangle band, std::span<const text_exclusion_rectangle> exclusions,
    std::span<text_line_interval> scratch, std::span<text_line_interval> intervals,
    std::span<text_line_fragment> fragments, std::uint32_t& fragment_count,
    std::uint32_t& next_glyph, float& next_y, font_error* error) noexcept {
    return fit_exclusion_band_core(glyphs, breaks, scales, start, options, tabs, band,
        exclusions, scratch, intervals, fragments, fragment_count, next_glyph, next_y, false, error);
}

bool try_layout_justified_logical_shaped_text(
    std::span<const shaping_glyph> logical_glyphs, std::span<const text_line_break_kind> breaks_after,
    std::span<const std::int8_t> bidi_levels, std::span<const float> glyph_scales,
    std::int8_t paragraph_level, const text_layout_options& options, text_tab_options tabs,
    std::span<float> advance_scratch, text_logical_layout_scratch scratch,
    std::span<positioned_text_glyph> positioned_glyphs, std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count, std::uint32_t& line_count,
    std::span<const text_justification_class> classes, font_error* error) noexcept {
    return try_layout_measured_logical_shaped_text(logical_glyphs, breaks_after, bidi_levels,
        glyph_scales, paragraph_level, options, tabs, advance_scratch, scratch,
        positioned_glyphs, lines, glyph_count, line_count, classes, {}, error);
}

static bool layout_measured_core(
    std::span<const shaping_glyph> logical_glyphs, std::span<const text_line_break_kind> breaks_after,
    std::span<const std::int8_t> bidi_levels, std::span<const float> glyph_scales,
    std::int8_t paragraph_level, const text_layout_options& options, text_tab_options tabs,
    std::span<float> advance_scratch, text_logical_layout_scratch scratch,
    std::span<positioned_text_glyph> positioned_glyphs, std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count, std::uint32_t& line_count,
    std::span<const text_justification_class> classes,
    std::span<const text_item_metrics> item_metrics, bool continues, font_error* error) noexcept {
    glyph_count = 0U;
    line_count = 0U;
    if (!item_metrics.empty()) {
        text_item_metrics maximum{};
        if (item_metrics.size() != logical_glyphs.size() ||
            options.trimming != text_trimming::none || !metric_envelope(item_metrics, maximum) ||
            std::max(static_cast<double>(options.line_height),
                static_cast<double>(maximum.ascent) + maximum.descent) *
                static_cast<double>(logical_glyphs.size()) > std::numeric_limits<float>::max()) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
    }
    if (!classes.empty() && (classes.size() != logical_glyphs.size() ||
        !std::all_of(classes.begin(), classes.end(), [](auto value) {
            return value <= text_justification_class::word_space;
        }))) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    text_layout_requirements requirements{};
    if (!try_get_tabbed_text_layout_requirements(
            logical_glyphs,
            breaks_after,
            glyph_scales,
            options,
            tabs,
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
    if ((tabs.interval > 0.0F && advance_scratch.size() < requirements.glyph_capacity) ||
        scratch.visual_groups.size() < requirements.glyph_capacity ||
        scratch.visual_indices.size() < requirements.glyph_capacity ||
        positioned_glyphs.size() < requirements.glyph_capacity ||
        lines.size() < requirements.line_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    std::size_t input_start_index = 0U;
    std::size_t output_cursor = 0U;
    double measured_top = 0.0;
    while (input_start_index < logical_glyphs.size() &&
        line_count < requirements.line_capacity) {
        const bool final_allowed = options.maximum_lines != 0U &&
            line_count + 1U >= options.maximum_lines;
        const line_scan line = scan_line(
            logical_glyphs,
            breaks_after,
            options,
            input_start_index,
            final_allowed,
            glyph_scales, tabs);
        const bool should_trim = options.trimming != text_trimming::none &&
            (line.clipped ||
                (final_allowed && options.collapse_width >= 0.0F && line.width > options.collapse_width) ||
                (final_allowed && line.end < logical_glyphs.size()));
        const trimmed_line visible = should_trim
            ? trim_line(
                logical_glyphs,
                breaks_after,
                options,
                input_start_index,
                line.end,
                line.width,
                glyph_scales, tabs)
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
        if (tabs.interval > 0.0F) {
            float logical_width = 0.0F;
            for (std::size_t i = input_start_index; i < visible.end; ++i) {
                advance_scratch[i] = layout_advance(logical_glyphs[i], scale_at(glyph_scales, i, options), logical_width, tabs);
                logical_width += advance_scratch[i];
            }
        }
        std::size_t justify_start = input_start_index, justify_end = visible.end;
        std::size_t opportunities = 0U;
        float expansion = 0.0F, trailing_width = 0.0F;
        const auto opportunity = [&](std::size_t i) noexcept {
            return i >= justify_start && i < justify_end &&
                classes[i] == text_justification_class::word_space &&
                (i + 1U == logical_glyphs.size() || logical_glyphs[i].cluster != logical_glyphs[i + 1U].cluster) &&
                is_safe_break_before(logical_glyphs, i + 1U);
        };
        if (options.alignment == text_alignment::justify && !classes.empty() &&
            !should_trim && !line.clipped && (line.end < logical_glyphs.size() || continues) &&
            breaks_after[line.end - 1U] != text_line_break_kind::mandatory && options.maximum_width > 0.0F) {
            while (justify_start < justify_end && classes[justify_start] != text_justification_class::content) ++justify_start;
            while (justify_end > justify_start && classes[justify_end - 1U] != text_justification_class::content) --justify_end;
            for (std::size_t i = input_start_index; i < visible.end; ++i) {
                // Expanding a prefix before a tab would invalidate its retained grid advance.
                if (logical_glyphs[i].glyph_id == text_tab_glyph_id) justify_start = i + 1U;
                if (i >= justify_end) trailing_width += tabs.interval > 0.0F ? advance_scratch[i] :
                    horizontal_advance(logical_glyphs[i], scale_at(glyph_scales, i, options));
            }
            while (justify_start < justify_end && classes[justify_start] != text_justification_class::content) ++justify_start;
            for (std::size_t i = justify_start; i < justify_end; ++i) opportunities += opportunity(i) ? 1U : 0U;
            const float available = options.maximum_width - (visible.content_width - trailing_width);
            if (opportunities != 0U && available > 0.0F && std::isfinite(visible.content_width + available))
                expansion = available;
        }
        float line_height = options.line_height;
        float baseline = static_cast<float>(line_count) * options.line_height;
        if (!item_metrics.empty()) {
            text_item_metrics envelope{};
            // Entire input was validated before publishing any output.
            (void)metric_envelope(item_metrics.subspan(input_start_index,
                visible.end - input_start_index), envelope);
            line_height = std::max(line_height, envelope.ascent + envelope.descent);
            baseline = static_cast<float>(measured_top + envelope.ascent);
        }
        const float sign_width = should_trim ? options.ellipsis_advance * options.scale : 0.0F;
        const bool leading_sign = should_trim && options.collapse_width >= 0.0F && (paragraph_level & 1) != 0;
        float cursor_x = leading_sign ? sign_width :
            expansion > 0.0F && (paragraph_level & 1) != 0 ? -trailing_width : 0.0F;
        float cursor_y = baseline;
        float distributed = 0.0F;
        std::size_t remaining_opportunities = opportunities;
        const std::int32_t sign_cluster = options.collapse_width >= 0.0F && visible.end < logical_glyphs.size()
            ? logical_glyphs[visible.end].cluster
            : visible.end > input_start_index ? logical_glyphs[visible.end - 1U].cluster
            : logical_glyphs[input_start_index].cluster;
        if (leading_sign) {
            positioned_glyphs[output_cursor++] = positioned_text_glyph{
                std::numeric_limits<std::uint32_t>::max(), options.ellipsis_glyph_id,
                sign_cluster, 0.0F, baseline, sign_width, 0.0F};
        }
        for (std::uint32_t visual_index = 0U;
            visual_index < visual_count;
            ++visual_index) {
            const std::size_t source_index = input_start_index +
                scratch.visual_indices[visual_index];
            const shaping_glyph& glyph = logical_glyphs[source_index];
            const float scale = scale_at(glyph_scales, source_index, options);
            auto metrics = scale_metrics(glyph, scale);
            if (tabs.interval > 0.0F) metrics[0] = advance_scratch[source_index];
            if (expansion > 0.0F && opportunity(source_index)) {
                const float extra = --remaining_opportunities == 0U ? expansion - distributed :
                    expansion / static_cast<float>(opportunities);
                metrics[0] += extra;
                distributed += extra;
            }
            positioned_glyphs[output_cursor++] = positioned_text_glyph{
                static_cast<std::uint32_t>(source_index),
                glyph.glyph_id,
                glyph.cluster,
                cursor_x + metrics[2], cursor_y + metrics[3], metrics[0], metrics[1]};
            cursor_x += metrics[0];
            cursor_y += metrics[1];
        }
        if (should_trim && !leading_sign) {
            positioned_glyphs[output_cursor++] = positioned_text_glyph{
                std::numeric_limits<std::uint32_t>::max(),
                options.ellipsis_glyph_id,
                sign_cluster,
                cursor_x,
                cursor_y,
                options.ellipsis_advance * options.scale,
                0.0F};
        }

        const float output_width = visible.content_width + expansion + (should_trim
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
            line_height,
            should_trim || line.clipped ||
                (final_allowed && line.end < logical_glyphs.size())};
        ++line_count;
        measured_top += line_height;
        input_start_index = line.end;
        if (final_allowed) break;
    }
    glyph_count = static_cast<std::uint32_t>(output_cursor);
    set_error(error, font_error::none);
    return true;
}

bool try_layout_measured_logical_shaped_text(
    std::span<const shaping_glyph> glyphs, std::span<const text_line_break_kind> breaks,
    std::span<const std::int8_t> levels, std::span<const float> scales,
    std::int8_t paragraph_level, const text_layout_options& options, text_tab_options tabs,
    std::span<float> advances, text_logical_layout_scratch scratch,
    std::span<positioned_text_glyph> positioned, std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count, std::uint32_t& line_count,
    std::span<const text_justification_class> classes,
    std::span<const text_item_metrics> metrics, font_error* error) noexcept {
    return layout_measured_core(glyphs, breaks, levels, scales, paragraph_level, options,
        tabs, advances, scratch, positioned, lines, glyph_count, line_count, classes, metrics, false, error);
}

static bool layout_exclusion_band_core(std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks, std::span<const std::int8_t> levels,
    std::span<const float> scales, std::span<const text_justification_class> classes,
    std::span<const text_item_metrics> metrics, std::uint32_t start,
    std::int8_t paragraph_level, const text_layout_options& options, text_tab_options tabs,
    text_exclusion_rectangle band, std::span<const text_exclusion_rectangle> exclusions,
    std::span<text_line_interval> exclusion_scratch, std::span<text_line_interval> intervals,
    std::span<text_line_fragment> fragments, std::span<float> advance_scratch,
    text_logical_layout_scratch scratch, std::span<positioned_text_glyph> positioned,
    std::span<positioned_text_line> lines, text_exclusion_band_result& result,
    bool validated, font_error* error) noexcept {
    result = {};
    const auto fail = [&](font_error value) noexcept { result = {}; set_error(error, value); return false; };
    text_item_metrics maximum{};
    if (start > glyphs.size() || levels.size() != glyphs.size() || metrics.size() != glyphs.size() ||
        (!classes.empty() && classes.size() != glyphs.size()) ||
        (paragraph_level != 0 && paragraph_level != 1) ||
        (!validated && (!std::all_of(levels.begin(), levels.end(), [](auto level) { return level >= 0 && level <= 125; }) ||
        !std::all_of(classes.begin(), classes.end(), [](auto value) { return value <= text_justification_class::word_space; }) ||
        !metric_envelope(metrics, maximum)))) return fail(font_error::invalid_argument);
    const auto remaining = glyphs.size() - start;
    if (positioned.size() < remaining || lines.size() < std::min(remaining, exclusions.size() + 1U) ||
        scratch.visual_groups.size() < remaining || scratch.visual_indices.size() < remaining ||
        (tabs.interval > 0 && advance_scratch.size() < remaining)) return fail(font_error::insufficient_buffer);
    std::uint32_t count{}, next{};
    float retry_y{};
    auto fitting_options = options;
    fitting_options.direction = paragraph_level == 0 ? shaping_direction::left_to_right : shaping_direction::right_to_left;
    if (!fit_exclusion_band_core(glyphs, breaks, scales, start, fitting_options, tabs,
        band, exclusions, exclusion_scratch, intervals, fragments, count, next, retry_y, validated, error)) return false;
    result.next_glyph = start; result.top = band.top; result.next_y = retry_y;
    if (start == glyphs.size()) { result.status = text_exclusion_band_status::complete; return true; }
    if (count == 0U) return true;
    text_item_metrics envelope{};
    // Fitted fragments consume one contiguous logical range despite spatial gaps.
    (void)metric_envelope(metrics.subspan(start, next - start), envelope);
    result.height = std::max(options.line_height, envelope.ascent + envelope.descent);
    result.baseline = band.top + envelope.ascent;
    const float bottom = band.top + result.height;
    if (!std::isfinite(bottom) || !std::isfinite(result.baseline) || bottom <= band.top)
        return fail(font_error::invalid_argument);
    if (bottom != band.bottom) { result.status = text_exclusion_band_status::refit_height; return true; }
    std::uint32_t written = 0U;
    for (std::uint32_t i = 0; i < count; ++i) {
        const auto fragment = fragments[i];
        const auto begin = fragment.glyph_start, length = fragment.glyph_count;
        auto local = fitting_options; local.maximum_width = fragment.width; local.maximum_lines = 0U;
        auto local_tabs = tabs;
        local_tabs.origin += paragraph_level == 0 ? fragment.left - band.left :
            band.right - (fragment.left + fragment.width);
        const bool continues = begin + length < glyphs.size() &&
            breaks[begin + length - 1U] != text_line_break_kind::mandatory;
        std::uint32_t emitted{}, emitted_lines{};
        if (!layout_measured_core(glyphs.subspan(begin, length), breaks.subspan(begin, length),
            levels.subspan(begin, length), scales.empty() ? scales : scales.subspan(begin, length),
            paragraph_level, local, local_tabs, advance_scratch, scratch, positioned.subspan(written),
            lines.subspan(i, 1), emitted, emitted_lines, classes.empty() ? classes : classes.subspan(begin, length),
            metrics.subspan(begin, length), continues, error)) { result = {}; return false; }
        if (emitted != length || emitted_lines != 1U) return fail(font_error::verification_failed);
        const float shift_y = result.baseline - lines[i].baseline_y;
        for (std::uint32_t j = 0; j < emitted; ++j) {
            auto& glyph = positioned[written + j];
            glyph.glyph_index += begin;
            if (!translate_glyph_position(glyph, fragment.left, shift_y)) return fail(font_error::invalid_argument);
        }
        lines[i].glyph_start = written;
        lines[i].baseline_y = result.baseline; lines[i].height = result.height;
        if (begin + length < glyphs.size()) lines[i].input_end = glyphs[begin + length].cluster;
        written += emitted;
    }
    result.status = text_exclusion_band_status::placed;
    result.next_glyph = next; result.glyph_count = written; result.fragment_count = count;
    return true;
}

bool try_layout_text_exclusion_band(std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks, std::span<const std::int8_t> levels,
    std::span<const float> scales, std::span<const text_justification_class> classes,
    std::span<const text_item_metrics> metrics, std::uint32_t start,
    std::int8_t paragraph_level, const text_layout_options& options, text_tab_options tabs,
    text_exclusion_rectangle band, std::span<const text_exclusion_rectangle> exclusions,
    std::span<text_line_interval> exclusion_scratch, std::span<text_line_interval> intervals,
    std::span<text_line_fragment> fragments, std::span<float> advance_scratch,
    text_logical_layout_scratch scratch, std::span<positioned_text_glyph> positioned,
    std::span<positioned_text_line> lines, text_exclusion_band_result& result,
    font_error* error) noexcept {
    return layout_exclusion_band_core(glyphs, breaks, levels, scales, classes, metrics, start,
        paragraph_level, options, tabs, band, exclusions, exclusion_scratch, intervals,
        fragments, advance_scratch, scratch, positioned, lines, result, false, error);
}

bool try_layout_excluded_logical_shaped_text(std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks, std::span<const std::int8_t> levels,
    std::span<const float> scales, std::span<const text_justification_class> classes,
    std::span<const text_item_metrics> metrics, std::int8_t paragraph_level,
    const text_layout_options& options, text_tab_options tabs,
    std::span<const text_exclusion_rectangle> exclusions,
    std::span<text_line_interval> exclusion_scratch, std::span<text_line_interval> intervals,
    std::span<text_line_fragment> fragments, std::span<float> advance_scratch,
    text_logical_layout_scratch scratch, std::span<positioned_text_glyph> positioned,
    std::span<positioned_text_line> lines, std::span<text_fragment_placement> placements,
    text_exclusion_flow_result& result, std::uint32_t maximum_attempts, font_error* error) noexcept {
    result = {};
    const auto fail = [&](font_error value) noexcept { result = {}; set_error(error, value); return false; };
    text_item_metrics maximum{};
    if (glyphs.size() > (1U << 20U) || metrics.size() != glyphs.size() ||
        maximum_attempts == 0U || maximum_attempts > (1U << 20U) ||
        !metric_envelope(metrics, maximum) || !valid_options(options) || options.maximum_width <= 0)
        return fail(font_error::invalid_argument);
    if (positioned.size() < glyphs.size() || lines.size() < glyphs.size() || placements.size() < glyphs.size())
        return fail(font_error::insufficient_buffer);
    const float initial_height = std::max(options.line_height, maximum.ascent + maximum.descent);
    if (!std::isfinite(initial_height) || (!glyphs.empty() && initial_height <= 0))
        return fail(font_error::invalid_argument);
    // Empty paragraphs still traverse validation, without manufacturing a line.
    if (glyphs.empty()) {
        text_exclusion_band_result empty{};
        if (!layout_exclusion_band_core(glyphs, breaks, levels, scales, classes, metrics, 0,
            paragraph_level, options, tabs, {0, 0, options.maximum_width, 1}, exclusions,
            exclusion_scratch, intervals, fragments, advance_scratch, scratch, positioned, lines, empty, false, error))
            return false;
        return true;
    }
    const auto seed_height = [&](std::uint32_t start) noexcept {
        return std::max(options.line_height, metrics[start].ascent + metrics[start].descent);
    };
    double top = 0;
    float height = seed_height(0);
    bool validated = false;
    while (result.next_glyph < glyphs.size()) {
        if (result.attempts++ >= maximum_attempts) return fail(font_error::verification_failed);
        const float band_top = static_cast<float>(top), bottom = band_top + height;
        if (!std::isfinite(bottom) || bottom <= band_top) return fail(font_error::invalid_argument);
        text_exclusion_band_result band{};
        if (!layout_exclusion_band_core(glyphs, breaks, levels, scales, classes, metrics, result.next_glyph,
            paragraph_level, options, tabs, {0, band_top, options.maximum_width, bottom}, exclusions,
            exclusion_scratch, intervals, fragments, advance_scratch, scratch,
            positioned.subspan(result.glyph_count), lines.subspan(result.fragment_count), band, validated, error)) {
            result = {}; return false;
        }
        validated = true;
        if (band.status == text_exclusion_band_status::refit_height) { height = band.height; continue; }
        if (band.status == text_exclusion_band_status::blocked) {
            if (band.next_y <= band_top) return fail(font_error::verification_failed);
            top = band.next_y; height = seed_height(result.next_glyph); continue;
        }
        if (band.status != text_exclusion_band_status::placed || band.next_glyph <= result.next_glyph)
            return fail(font_error::verification_failed);
        for (std::uint32_t i = 0; i < band.fragment_count; ++i) {
            const auto index = result.fragment_count + i;
            lines[index].glyph_start += result.glyph_count;
            placements[index] = {result.row_count, fragments[i].left, band.top, fragments[i].width};
        }
        result.glyph_count += band.glyph_count; result.fragment_count += band.fragment_count;
        result.next_glyph = band.next_glyph; ++result.row_count;
        top += band.height; result.height = top;
        if (options.maximum_lines != 0U && result.row_count >= options.maximum_lines) {
            if (result.next_glyph < glyphs.size()) lines[result.fragment_count - 1U].clipped = true;
            break;
        }
        if (result.next_glyph < glyphs.size()) height = seed_height(result.next_glyph);
    }
    set_error(error, font_error::none);
    return true;
}

bool try_layout_open_type_text(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    std::span<shaping_glyph> public_metric_scratch,
    std::span<positioned_text_glyph> positioned_glyphs,
    std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count,
    std::uint32_t& line_count,
    font_error* error) noexcept {
    glyph_count = 0U;
    line_count = 0U;
    if (public_metric_scratch.size() < glyphs.size()) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    if (options.direction != shaping_direction::left_to_right &&
        options.direction != shaping_direction::right_to_left) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    for (const auto& glyph : glyphs) {
        if (glyph.advance_y == std::numeric_limits<std::int32_t>::min() ||
            glyph.offset_y == std::numeric_limits<std::int32_t>::min()) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
    }
    for (std::size_t index = 0U; index < glyphs.size(); ++index) {
        public_metric_scratch[index] = glyphs[index];
        public_metric_scratch[index].advance_y = -glyphs[index].advance_y;
        public_metric_scratch[index].offset_y = -glyphs[index].offset_y;
    }
    return try_layout_shaped_text(
        public_metric_scratch.first(glyphs.size()),
        breaks_after,
        options,
        positioned_glyphs,
        lines,
        glyph_count,
        line_count,
        error);
}

} // namespace progpu::native::text

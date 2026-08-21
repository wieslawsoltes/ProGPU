#include "progpu_native_text.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Native port of the cluster-box/caret/hit/selection ownership in ProGPU-owned
// TextLayout. Logical cluster ends and bidi levels remain caller-owned inputs.

namespace progpu::native::text {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool finite_glyph(const positioned_text_glyph& glyph) noexcept {
    return std::isfinite(glyph.x) && std::isfinite(glyph.y) &&
        std::isfinite(glyph.advance_x) && std::isfinite(glyph.advance_y);
}

bool finite_line(const positioned_text_line& line) noexcept {
    return std::isfinite(line.baseline_y) && std::isfinite(line.height) &&
        line.height >= 0.0F;
}

bool validate_inputs(
    std::span<const positioned_text_glyph> glyphs,
    std::span<const positioned_text_line> lines,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels) noexcept {
    if (cluster_ends.size() != glyphs.size() ||
        bidi_levels.size() != glyphs.size()) {
        return false;
    }
    std::size_t expected = 0U;
    for (const auto& line : lines) {
        if (!finite_line(line) || line.glyph_start != expected ||
            line.glyph_count > glyphs.size() - expected) {
            return false;
        }
        const std::size_t end = expected + line.glyph_count;
        for (std::size_t index = expected; index < end; ++index) {
            if (!finite_glyph(glyphs[index]) ||
                cluster_ends[index] <= glyphs[index].cluster) {
                return false;
            }
        }
        expected = end;
    }
    return expected == glyphs.size();
}

std::uint32_t count_clusters(
    std::span<const positioned_text_glyph> glyphs,
    std::span<const positioned_text_line> lines) noexcept {
    std::uint32_t result = 0U;
    for (const auto& line : lines) {
        const std::size_t start = line.glyph_start;
        const std::size_t end = start + line.glyph_count;
        for (std::size_t index = start; index < end;) {
            ++result;
            const std::int32_t cluster = glyphs[index].cluster;
            do {
                ++index;
            } while (index < end && glyphs[index].cluster == cluster);
        }
    }
    return result;
}

} // namespace

bool try_get_text_interaction_requirements(
    std::span<const positioned_text_glyph> glyphs,
    std::span<const positioned_text_line> lines,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels,
    text_interaction_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (!validate_inputs(glyphs, lines, cluster_ends, bidi_levels)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const std::uint32_t clusters = count_clusters(glyphs, lines);
    if (clusters > std::numeric_limits<std::uint32_t>::max() / 2U) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    result = text_interaction_requirements{clusters, clusters * 2U};
    set_error(error, font_error::none);
    return true;
}

bool try_build_text_interaction(
    std::span<const positioned_text_glyph> glyphs,
    std::span<const positioned_text_line> lines,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels,
    std::span<text_cluster_box> cluster_boxes,
    std::span<text_caret_stop> caret_stops,
    std::uint32_t& cluster_box_count,
    std::uint32_t& caret_stop_count,
    font_error* error) noexcept {
    cluster_box_count = 0U;
    caret_stop_count = 0U;
    text_interaction_requirements requirements{};
    if (!try_get_text_interaction_requirements(
            glyphs,
            lines,
            cluster_ends,
            bidi_levels,
            requirements,
            error)) {
        return false;
    }
    if (cluster_boxes.size() < requirements.cluster_box_capacity ||
        caret_stops.size() < requirements.caret_stop_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    for (std::uint32_t line_index = 0U;
         line_index < lines.size();
         ++line_index) {
        const positioned_text_line& line = lines[line_index];
        const std::size_t end = static_cast<std::size_t>(line.glyph_start) +
            line.glyph_count;
        for (std::size_t index = line.glyph_start; index < end;) {
            const std::size_t start = index;
            const std::int32_t cluster = glyphs[index].cluster;
            float left = std::min(
                glyphs[index].x,
                glyphs[index].x + glyphs[index].advance_x);
            float right = std::max(
                glyphs[index].x,
                glyphs[index].x + glyphs[index].advance_x);
            std::int32_t cluster_end = cluster_ends[index];
            do {
                left = std::min(left, std::min(
                    glyphs[index].x,
                    glyphs[index].x + glyphs[index].advance_x));
                right = std::max(right, std::max(
                    glyphs[index].x,
                    glyphs[index].x + glyphs[index].advance_x));
                cluster_end = std::max(cluster_end, cluster_ends[index]);
                ++index;
            } while (index < end && glyphs[index].cluster == cluster);
            const text_cluster_box box{
                cluster,
                cluster_end,
                line_index,
                bidi_levels[start],
                0U,
                0U,
                0U,
                left,
                line.baseline_y,
                std::max(0.0F, right - left),
                std::max(1.0F, line.height)};
            cluster_boxes[cluster_box_count++] = box;
            const bool rtl = (box.bidi_level & 1) != 0;
            const text_caret_stop leading{
                rtl ? box.input_end : box.input_start,
                line_index,
                box.x,
                box.y,
                box.height,
                box.bidi_level,
                rtl};
            const text_caret_stop trailing{
                rtl ? box.input_start : box.input_end,
                line_index,
                box.x + box.width,
                box.y,
                box.height,
                box.bidi_level,
                !rtl};
            const auto append_caret = [&](const text_caret_stop& value) {
                if (caret_stop_count != 0U) {
                    const auto& previous = caret_stops[caret_stop_count - 1U];
                    if (previous.input_position == value.input_position &&
                        previous.trailing == value.trailing &&
                        std::abs(previous.x - value.x) < 0.0001F &&
                        std::abs(previous.y - value.y) < 0.0001F) {
                        return;
                    }
                }
                caret_stops[caret_stop_count++] = value;
            };
            append_caret(leading);
            append_caret(trailing);
        }
    }
    set_error(error, font_error::none);
    return true;
}

bool try_hit_test_text(
    std::span<const text_cluster_box> cluster_boxes,
    float x,
    float y,
    text_hit_test_result& result,
    font_error* error) noexcept {
    result = {};
    if (cluster_boxes.empty() || !std::isfinite(x) || !std::isfinite(y)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    float best_distance = std::numeric_limits<float>::infinity();
    const text_cluster_box* best = nullptr;
    bool inside = false;
    for (const auto& box : cluster_boxes) {
        const float right = box.x + box.width;
        const float bottom = box.y + box.height;
        const float dx = x < box.x ? box.x - x : x > right ? x - right : 0.0F;
        const float dy = y < box.y ? box.y - y : y > bottom ? y - bottom : 0.0F;
        const float distance = dx * dx + dy * dy;
        if (distance >= best_distance) {
            continue;
        }
        best_distance = distance;
        best = &box;
        inside = dx == 0.0F && dy == 0.0F;
    }
    if (best == nullptr) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const bool visual_right = x >= best->x + best->width * 0.5F;
    const bool rtl = (best->bidi_level & 1) != 0;
    const bool trailing = rtl ? !visual_right : visual_right;
    result = text_hit_test_result{
        trailing ? best->input_end : best->input_start,
        best->line_index,
        text_rectangle{best->x, best->y, best->width, best->height},
        best->bidi_level,
        trailing,
        inside};
    set_error(error, font_error::none);
    return true;
}

bool try_get_text_caret_stop(
    std::span<const text_caret_stop> caret_stops,
    std::int32_t input_position,
    bool trailing_affinity,
    text_caret_stop& result,
    font_error* error) noexcept {
    result = {};
    if (caret_stops.empty()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::size_t best = 0U;
    auto best_distance = std::numeric_limits<std::int64_t>::max();
    for (std::size_t index = 0U; index < caret_stops.size(); ++index) {
        const auto& candidate = caret_stops[index];
        if (!std::isfinite(candidate.x) || !std::isfinite(candidate.y) ||
            !std::isfinite(candidate.height) || candidate.height < 0.0F) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        const auto delta = static_cast<std::int64_t>(
            candidate.input_position) - input_position;
        const auto distance = delta < 0 ? -delta : delta;
        if (distance < best_distance ||
            (distance == best_distance &&
                candidate.trailing == trailing_affinity &&
                caret_stops[best].trailing != trailing_affinity)) {
            best = index;
            best_distance = distance;
        }
    }
    result = caret_stops[best];
    set_error(error, font_error::none);
    return true;
}

bool try_move_text_caret_visually(
    std::span<const text_caret_stop> caret_stops,
    std::int32_t input_position,
    bool trailing_affinity,
    std::int32_t direction,
    text_caret_stop& result,
    font_error* error) noexcept {
    result = {};
    if (caret_stops.empty()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::size_t current = 0U;
    auto best_distance = std::numeric_limits<std::int64_t>::max();
    for (std::size_t index = 0U; index < caret_stops.size(); ++index) {
        const auto& candidate = caret_stops[index];
        if (!std::isfinite(candidate.x) || !std::isfinite(candidate.y) ||
            !std::isfinite(candidate.height) || candidate.height < 0.0F) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        const auto delta = static_cast<std::int64_t>(
            candidate.input_position) - input_position;
        const auto logical_distance = delta < 0 ? -delta : delta;
        const auto distance = logical_distance * 4 +
            (candidate.trailing == trailing_affinity ? 0 : 1);
        if (distance < best_distance) {
            best_distance = distance;
            current = index;
        }
    }
    if (direction < 0 && current != 0U) {
        --current;
    } else if (direction > 0 && current + 1U < caret_stops.size()) {
        ++current;
    }
    result = caret_stops[current];
    set_error(error, font_error::none);
    return true;
}

bool try_get_text_selection_rectangles(
    std::span<const text_cluster_box> cluster_boxes,
    std::int32_t input_start,
    std::int32_t input_end,
    std::span<text_rectangle> rectangles,
    std::uint32_t& written,
    font_error* error) noexcept {
    written = 0U;
    if (input_end < input_start ||
        cluster_boxes.size() > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::uint32_t required = 0U;
    std::uint32_t previous_line = std::numeric_limits<std::uint32_t>::max();
    float previous_right = 0.0F;
    for (const auto& box : cluster_boxes) {
        if (box.input_end <= input_start || box.input_start >= input_end) {
            continue;
        }
        const bool starts_rectangle = required == 0U ||
            box.line_index != previous_line ||
            box.x > previous_right + 0.5F;
        if (starts_rectangle) {
            ++required;
            previous_right = box.x + box.width;
        } else {
            previous_right = std::max(previous_right, box.x + box.width);
        }
        previous_line = box.line_index;
    }
    if (rectangles.size() < required) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    previous_line = std::numeric_limits<std::uint32_t>::max();
    for (const auto& box : cluster_boxes) {
        if (box.input_end <= input_start || box.input_start >= input_end) {
            continue;
        }
        if (written != 0U && box.line_index == previous_line &&
            box.x <= rectangles[written - 1U].x +
                rectangles[written - 1U].width + 0.5F) {
            auto& rectangle = rectangles[written - 1U];
            const float right = std::max(
                rectangle.x + rectangle.width, box.x + box.width);
            rectangle.x = std::min(rectangle.x, box.x);
            rectangle.width = right - rectangle.x;
        } else {
            rectangles[written++] = text_rectangle{
                box.x, box.y, box.width, box.height};
        }
        previous_line = box.line_index;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text

#include "progpu_native_text.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Vertical-axis adaptation of the ProGPU-owned physical cluster/caret model.
// All output remains caller-owned; no glyph, box, or caret allocation occurs.

namespace progpu::native::text {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

bool valid_direction(shaping_direction direction) noexcept {
    return direction == shaping_direction::top_to_bottom ||
        direction == shaping_direction::bottom_to_top;
}

bool finite_glyph(const positioned_text_glyph& glyph) noexcept {
    return std::isfinite(glyph.x) && std::isfinite(glyph.y) &&
        std::isfinite(glyph.advance_x) && std::isfinite(glyph.advance_y);
}

bool finite_box(const text_vertical_cluster_box& box) noexcept {
    return box.input_end > box.input_start &&
        box.bidi_level >= 0 && box.bidi_level <= 125 &&
        std::isfinite(box.x) && std::isfinite(box.y) &&
        std::isfinite(box.width) && box.width >= 0.0F &&
        std::isfinite(box.height) && box.height >= 0.0F;
}

bool validate_inputs(
    std::span<const positioned_text_glyph> glyphs,
    std::span<const positioned_text_column> columns,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels,
    shaping_direction direction) noexcept {
    if (!valid_direction(direction) || cluster_ends.size() != glyphs.size() ||
        bidi_levels.size() != glyphs.size() ||
        glyphs.size() > std::numeric_limits<std::uint32_t>::max() ||
        columns.size() > std::numeric_limits<std::uint32_t>::max()) {
        return false;
    }
    std::size_t expected = 0U;
    for (const auto& column : columns) {
        if (column.glyph_start != expected ||
            column.glyph_count > glyphs.size() - expected ||
            !std::isfinite(column.x) || !std::isfinite(column.width) ||
            column.width < 0.0F || !std::isfinite(column.height) ||
            column.height < 0.0F) {
            return false;
        }
        const std::size_t end = expected + column.glyph_count;
        for (std::size_t index = expected; index < end; ++index) {
            if (!finite_glyph(glyphs[index]) ||
                cluster_ends[index] <= glyphs[index].cluster ||
                bidi_levels[index] < 0 || bidi_levels[index] > 125) {
                return false;
            }
        }
        expected = end;
    }
    return expected == glyphs.size();
}

std::uint32_t count_clusters(
    std::span<const positioned_text_glyph> glyphs,
    std::span<const positioned_text_column> columns) noexcept {
    std::uint32_t result = 0U;
    for (const auto& column : columns) {
        const std::size_t end = static_cast<std::size_t>(
            column.glyph_start) + column.glyph_count;
        for (std::size_t index = column.glyph_start; index < end;) {
            ++result;
            const auto cluster = glyphs[index].cluster;
            do {
                ++index;
            } while (index < end && glyphs[index].cluster == cluster);
        }
    }
    return result;
}

bool finite_caret(const text_vertical_caret_stop& caret) noexcept {
    return std::isfinite(caret.x) && std::isfinite(caret.y) &&
        std::isfinite(caret.width) && caret.width >= 0.0F &&
        caret.bidi_level >= 0 && caret.bidi_level <= 125;
}

bool boxes_touch_vertically(
    const text_rectangle& previous,
    const text_vertical_cluster_box& box) noexcept {
    if (std::abs(previous.x - box.x) >= 0.01F ||
        std::abs(previous.width - box.width) >= 0.01F) {
        return false;
    }
    const float previous_bottom = previous.y + previous.height;
    const float box_bottom = box.y + box.height;
    return box.y <= previous_bottom + 0.5F &&
        previous.y <= box_bottom + 0.5F;
}

void merge_vertical_rectangle(
    text_rectangle& rectangle,
    const text_vertical_cluster_box& box) noexcept {
    const float bottom = std::max(
        rectangle.y + rectangle.height, box.y + box.height);
    rectangle.y = std::min(rectangle.y, box.y);
    rectangle.height = bottom - rectangle.y;
}

} // namespace

bool try_get_vertical_text_interaction_requirements(
    std::span<const positioned_text_glyph> glyphs,
    std::span<const positioned_text_column> columns,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels,
    shaping_direction direction,
    text_interaction_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (!validate_inputs(
            glyphs, columns, cluster_ends, bidi_levels, direction)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const std::uint32_t clusters = count_clusters(glyphs, columns);
    if (clusters > std::numeric_limits<std::uint32_t>::max() / 2U) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    result = text_interaction_requirements{clusters, clusters * 2U};
    set_error(error, font_error::none);
    return true;
}

bool try_build_vertical_text_interaction(
    std::span<const positioned_text_glyph> glyphs,
    std::span<const positioned_text_column> columns,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels,
    shaping_direction direction,
    std::span<text_vertical_cluster_box> cluster_boxes,
    std::span<text_vertical_caret_stop> caret_stops,
    std::uint32_t& cluster_box_count,
    std::uint32_t& caret_stop_count,
    font_error* error) noexcept {
    cluster_box_count = 0U;
    caret_stop_count = 0U;
    text_interaction_requirements requirements{};
    if (!try_get_vertical_text_interaction_requirements(
            glyphs,
            columns,
            cluster_ends,
            bidi_levels,
            direction,
            requirements,
            error)) {
        return false;
    }
    if (cluster_boxes.size() < requirements.cluster_box_capacity ||
        caret_stops.size() < requirements.caret_stop_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    const bool bottom_to_top =
        direction == shaping_direction::bottom_to_top;
    for (std::uint32_t column_index = 0U;
        column_index < columns.size();
        ++column_index) {
        const auto& column = columns[column_index];
        const std::size_t end = static_cast<std::size_t>(
            column.glyph_start) + column.glyph_count;
        for (std::size_t index = column.glyph_start; index < end;) {
            const std::size_t start = index;
            const std::int32_t cluster = glyphs[index].cluster;
            float top = std::numeric_limits<float>::infinity();
            float bottom = -std::numeric_limits<float>::infinity();
            std::int32_t cluster_end = cluster_ends[index];
            do {
                const float glyph_end =
                    glyphs[index].y + glyphs[index].advance_y;
                top = std::min(top, std::min(glyphs[index].y, glyph_end));
                bottom = std::max(
                    bottom, std::max(glyphs[index].y, glyph_end));
                cluster_end = std::max(cluster_end, cluster_ends[index]);
                ++index;
            } while (index < end && glyphs[index].cluster == cluster);
            if (bottom - top < 1.0F) {
                if (bottom_to_top) {
                    top = bottom - 1.0F;
                } else {
                    bottom = top + 1.0F;
                }
            }

            const text_vertical_cluster_box box{
                cluster,
                cluster_end,
                column_index,
                bidi_levels[start],
                bottom_to_top,
                0U,
                0U,
                column.x,
                top,
                std::max(1.0F, column.width),
                bottom - top};
            cluster_boxes[cluster_box_count++] = box;
            const float leading_y = bottom_to_top ? bottom : top;
            const float trailing_y = bottom_to_top ? top : bottom;
            const text_vertical_caret_stop leading{
                box.input_start,
                column_index,
                box.x,
                leading_y,
                box.width,
                box.bidi_level,
                false};
            const text_vertical_caret_stop trailing{
                box.input_end,
                column_index,
                box.x,
                trailing_y,
                box.width,
                box.bidi_level,
                true};
            const auto append_caret = [&](const auto& value) {
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

bool try_hit_test_vertical_text(
    std::span<const text_vertical_cluster_box> cluster_boxes,
    float x,
    float y,
    text_vertical_hit_test_result& result,
    font_error* error) noexcept {
    result = {};
    if (cluster_boxes.empty() || !std::isfinite(x) || !std::isfinite(y)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    float best_distance = std::numeric_limits<float>::infinity();
    const text_vertical_cluster_box* best = nullptr;
    bool inside = false;
    for (const auto& box : cluster_boxes) {
        if (!finite_box(box)) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        const float right = box.x + box.width;
        const float bottom = box.y + box.height;
        const float dx = x < box.x ? box.x - x : x > right ? x - right : 0.0F;
        const float dy = y < box.y ? box.y - y : y > bottom ? y - bottom : 0.0F;
        const float distance = dx * dx + dy * dy;
        if (distance >= best_distance) continue;
        best_distance = distance;
        best = &box;
        inside = dx == 0.0F && dy == 0.0F;
    }
    if (best == nullptr) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const bool lower_half = y >= best->y + best->height * 0.5F;
    const bool trailing = best->bottom_to_top ? !lower_half : lower_half;
    result = text_vertical_hit_test_result{
        trailing ? best->input_end : best->input_start,
        best->column_index,
        text_rectangle{best->x, best->y, best->width, best->height},
        best->bidi_level,
        trailing,
        inside};
    set_error(error, font_error::none);
    return true;
}

bool try_get_vertical_text_caret_stop(
    std::span<const text_vertical_caret_stop> caret_stops,
    std::int32_t input_position,
    bool trailing_affinity,
    text_vertical_caret_stop& result,
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
        if (!finite_caret(candidate)) {
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

bool try_move_vertical_text_caret_visually(
    std::span<const text_vertical_caret_stop> caret_stops,
    std::int32_t input_position,
    bool trailing_affinity,
    std::int32_t direction,
    text_vertical_caret_stop& result,
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
        if (!finite_caret(candidate)) {
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

bool try_get_vertical_text_selection_rectangles(
    std::span<const text_vertical_cluster_box> cluster_boxes,
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
    std::uint32_t previous_column = std::numeric_limits<std::uint32_t>::max();
    text_rectangle previous{};
    for (const auto& box : cluster_boxes) {
        if (!finite_box(box)) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        if (box.input_end <= input_start || box.input_start >= input_end) {
            continue;
        }
        if (required != 0U && box.column_index == previous_column &&
            boxes_touch_vertically(previous, box)) {
            merge_vertical_rectangle(previous, box);
        } else {
            ++required;
            previous = text_rectangle{box.x, box.y, box.width, box.height};
        }
        previous_column = box.column_index;
    }
    if (rectangles.size() < required) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    previous_column = std::numeric_limits<std::uint32_t>::max();
    for (const auto& box : cluster_boxes) {
        if (box.input_end <= input_start || box.input_start >= input_end) {
            continue;
        }
        if (written != 0U && box.column_index == previous_column &&
            boxes_touch_vertically(rectangles[written - 1U], box)) {
            merge_vertical_rectangle(rectangles[written - 1U], box);
        } else {
            rectangles[written++] = text_rectangle{
                box.x, box.y, box.width, box.height};
        }
        previous_column = box.column_index;
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text

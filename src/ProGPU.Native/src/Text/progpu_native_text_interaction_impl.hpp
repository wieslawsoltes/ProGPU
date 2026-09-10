#pragma once

#include "progpu_native_text.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Native port of the cluster-box/caret/hit/selection ownership in ProGPU-owned
// TextLayout. Logical cluster ends and bidi levels remain caller-owned inputs.

namespace progpu::native::text::interaction_detail {
namespace {

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

template<class Glyph>
bool finite_glyph(const Glyph& glyph) noexcept {
    return std::isfinite(glyph.x) && std::isfinite(glyph.y) &&
        std::isfinite(glyph.advance_x) && std::isfinite(glyph.advance_y) &&
        std::isfinite(glyph.x + glyph.advance_x);
}

template<class Line>
bool finite_line(const Line& line) noexcept {
    return std::isfinite(line.baseline_y) && std::isfinite(line.height) &&
        line.height >= 0.0F && std::isfinite(line.baseline_y + line.height);
}

template<class Glyph, class Line, class Fragment = text_fragment_placement>
bool validate_inputs(
    std::span<const Glyph> glyphs,
    std::span<const Line> lines,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels,
    bool measured_lines = false,
    std::span<const Fragment> fragments = {}) noexcept {
    if (cluster_ends.size() != glyphs.size() ||
        bidi_levels.size() != glyphs.size() || (!fragments.empty() && fragments.size() != lines.size())) {
        return false;
    }
    int row_direction = 0;
    for (std::size_t i = 0; i < fragments.size(); ++i) {
        const auto f = fragments[i];
        if (!std::isfinite(f.left) || !std::isfinite(f.top) || !std::isfinite(f.width) ||
            f.width <= 0 || !std::isfinite(f.left + f.width)) return false;
        if (i == 0) { if (f.row_index != 0U) return false; continue; }
        const auto p = fragments[i - 1U];
        if (f.row_index == p.row_index) {
            if (f.top != p.top || lines[i].height != lines[i - 1U].height ||
                lines[i].baseline_y != lines[i - 1U].baseline_y) return false;
            const int direction = f.left >= p.left + p.width ? 1 :
                f.left + f.width <= p.left ? -1 : 0;
            if (direction == 0 || (row_direction != 0 && direction != row_direction)) return false;
            row_direction = direction;
        } else {
            if (f.row_index != p.row_index + 1U || f.top < p.top + lines[i - 1U].height) return false;
            row_direction = 0;
        }
    }
    std::size_t expected = 0U;
    double line_top = 0.0;
    std::size_t fragment_index = 0U;
    for (const auto& line : lines) {
        if (!fragments.empty()) line_top = fragments[fragment_index++].top;
        const double line_bottom = line_top + static_cast<double>(line.height);
        const bool valid_metrics = measured_lines || !fragments.empty()
            ? std::isfinite(line.baseline_y) && std::isfinite(line.height) &&
                line.height >= 0.0F && std::isfinite(line_bottom) &&
                line_bottom <= std::numeric_limits<float>::max() &&
                line.baseline_y >= static_cast<float>(line_top) &&
                line.baseline_y <= static_cast<float>(line_bottom)
            : finite_line(line);
        if (!valid_metrics || line.glyph_start != expected ||
            line.glyph_count > glyphs.size() - expected) {
            return false;
        }
        const std::size_t end = expected + line.glyph_count;
        for (std::size_t index = expected; index < end; ++index) {
            if (!finite_glyph(glyphs[index]) ||
                cluster_ends[index] <= glyphs[index].cluster ||
                bidi_levels[index] < 0 || bidi_levels[index] > 125) {
                return false;
            }
        }
        expected = end;
        line_top = line_bottom;
    }
    return expected == glyphs.size();
}

template<class Glyph, class Line>
std::uint32_t count_clusters(
    std::span<const Glyph> glyphs,
    std::span<const Line> lines) noexcept {
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

template<class Glyph, class Line, class Fragment = text_fragment_placement>
bool get_requirements(
    std::span<const Glyph> glyphs,
    std::span<const Line> lines,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels,
    text_interaction_requirements& result,
    font_error* error,
    bool measured_lines = false,
    std::span<const Fragment> fragments = {}) noexcept {
    result = {};
    if (!validate_inputs(glyphs, lines, cluster_ends, bidi_levels, measured_lines, fragments)) {
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

template<class Glyph, class Line, class Box, class Caret, class Fragment = text_fragment_placement>
bool build(
    std::span<const Glyph> glyphs,
    std::span<const Line> lines,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels,
    std::span<Box> cluster_boxes,
    std::span<Caret> caret_stops,
    std::uint32_t& cluster_box_count,
    std::uint32_t& caret_stop_count,
    font_error* error,
    bool measured_lines = false,
    std::span<const Fragment> fragments = {}) noexcept {
    cluster_box_count = 0U;
    caret_stop_count = 0U;
    text_interaction_requirements requirements{};
    if (!get_requirements(
            glyphs,
            lines,
            cluster_ends,
            bidi_levels,
            requirements,
            error,
            measured_lines, fragments)) {
        return false;
    }
    if (cluster_boxes.size() < requirements.cluster_box_capacity ||
        caret_stops.size() < requirements.caret_stop_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    // Same dependency-bound double prefix as the measured paragraph writer.
    double line_top = 0.0;
    for (std::uint32_t line_index = 0U;
         line_index < lines.size();
         ++line_index) {
        const Line& line = lines[line_index];
        if (!fragments.empty()) line_top = fragments[line_index].top;
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
            const Box box{
                cluster,
                cluster_end,
                line_index,
                bidi_levels[start],
                0U,
                0U,
                0U,
                left,
                measured_lines || !fragments.empty() ? static_cast<float>(line_top) : line.baseline_y,
                std::max(0.0F, right - left),
                measured_lines || !fragments.empty() ? line.height : std::max(1.0F, line.height)};
            cluster_boxes[cluster_box_count++] = box;
            const bool rtl = (box.bidi_level & 1) != 0;
            const Caret leading{
                rtl ? box.input_end : box.input_start,
                line_index,
                box.x,
                box.y,
                box.height,
                box.bidi_level,
                rtl, 0U, 0U};
            const Caret trailing{
                rtl ? box.input_start : box.input_end,
                line_index,
                box.x + box.width,
                box.y,
                box.height,
                box.bidi_level,
                !rtl, 0U, 0U};
            const auto append_caret = [&](const Caret& value) {
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
        line_top += static_cast<double>(line.height);
    }
    set_error(error, font_error::none);
    return true;
}

template<class Box, class Hit>
bool hit_test(
    std::span<const Box> cluster_boxes,
    float x,
    float y,
    Hit& result,
    font_error* error) noexcept {
    result = {};
    if (cluster_boxes.empty() || !std::isfinite(x) || !std::isfinite(y)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    float best_distance = std::numeric_limits<float>::infinity();
    const Box* best = nullptr;
    bool inside = false;
    for (const auto& box : cluster_boxes) {
        if (!std::isfinite(box.x) || !std::isfinite(box.y) ||
            !std::isfinite(box.width) || !std::isfinite(box.height) ||
            !std::isfinite(box.x + box.width) || !std::isfinite(box.y + box.height) ||
            box.width < 0.0F || box.height < 0.0F || box.input_end <= box.input_start ||
            box.bidi_level < 0 || box.bidi_level > 125) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
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
    result = Hit{
        trailing ? best->input_end : best->input_start,
        best->line_index,
        decltype(result.bounds){best->x, best->y, best->width, best->height},
        best->bidi_level,
        trailing,
        inside, 0U};
    set_error(error, font_error::none);
    return true;
}

template<class Caret>
bool get_caret(
    std::span<const Caret> caret_stops,
    std::int32_t input_position,
    bool trailing_affinity,
    Caret& result,
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
            !std::isfinite(candidate.height) || candidate.height < 0.0F ||
            candidate.bidi_level < 0 || candidate.bidi_level > 125 ||
            static_cast<std::uint8_t>(candidate.trailing) > 1U) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        const auto delta = static_cast<std::int64_t>(
            candidate.input_position) - input_position;
        const auto distance = delta < 0 ? -delta : delta;
        if (distance < best_distance ||
            (distance == best_distance &&
                static_cast<bool>(candidate.trailing) == trailing_affinity &&
                static_cast<bool>(caret_stops[best].trailing) != trailing_affinity)) {
            best = index;
            best_distance = distance;
        }
    }
    result = caret_stops[best];
    set_error(error, font_error::none);
    return true;
}

template<class Caret>
bool move_caret(
    std::span<const Caret> caret_stops,
    std::int32_t input_position,
    bool trailing_affinity,
    std::int32_t direction,
    Caret& result,
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
            !std::isfinite(candidate.height) || candidate.height < 0.0F ||
            candidate.bidi_level < 0 || candidate.bidi_level > 125 ||
            static_cast<std::uint8_t>(candidate.trailing) > 1U) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        const auto delta = static_cast<std::int64_t>(
            candidate.input_position) - input_position;
        const auto logical_distance = delta < 0 ? -delta : delta;
        const auto distance = logical_distance * 4 +
            (static_cast<bool>(candidate.trailing) == trailing_affinity ? 0 : 1);
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

template<class Box, class Rectangle>
bool selection(
    std::span<const Box> cluster_boxes,
    std::int32_t input_start,
    std::int32_t input_end,
    std::span<Rectangle> rectangles,
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
        if (!std::isfinite(box.x) || !std::isfinite(box.y) ||
            !std::isfinite(box.width) || !std::isfinite(box.height) ||
            !std::isfinite(box.x + box.width) || !std::isfinite(box.y + box.height) ||
            box.width < 0.0F || box.height < 0.0F || box.input_end <= box.input_start ||
            box.bidi_level < 0 || box.bidi_level > 125) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
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
            rectangles[written++] = Rectangle{
                box.x, box.y, box.width, box.height};
        }
        previous_line = box.line_index;
    }
    set_error(error, font_error::none);
    return true;
}

template<class Caret, class Fragment>
bool move_fragment_caret(std::span<const Caret> carets,
    std::span<const Fragment> placements, std::uint32_t current_index,
    text_caret_direction direction, std::int8_t paragraph_level, float preferred_x,
    std::uint32_t& next_index, font_error* error) noexcept {
    next_index = current_index;
    const auto invalid = [&] { if (error != nullptr) *error = font_error::invalid_argument; return false; };
    if (carets.size() > UINT32_MAX || current_index >= carets.size() ||
        (paragraph_level != 0 && paragraph_level != 1) || !std::isfinite(preferred_x) ||
        static_cast<std::uint8_t>(direction) > static_cast<std::uint8_t>(text_caret_direction::down)) return invalid();
    int row_direction = 0;
    for (std::size_t i = 0; i < placements.size(); ++i) {
        const auto f = placements[i];
        if (!std::isfinite(f.left) || !std::isfinite(f.top) || !std::isfinite(f.width) ||
            f.width <= 0 || !std::isfinite(f.left + f.width)) return invalid();
        if (i == 0U) { if (f.row_index != 0U) return invalid(); }
        else {
            const auto p = placements[i - 1U];
            if (f.row_index == p.row_index) {
                if (f.top != p.top) return invalid();
                const int order = f.left >= p.left + p.width ? 1 : f.left + f.width <= p.left ? -1 : 0;
                if (order == 0 || (row_direction != 0 && order != row_direction)) return invalid();
                row_direction = order;
            } else {
                if (f.row_index != p.row_index + 1U || f.top < p.top) return invalid();
                row_direction = 0;
            }
        }
    }
    for (std::size_t i = 0; i < carets.size(); ++i) {
        const auto c = carets[i];
        if (c.line_index >= placements.size() || !std::isfinite(c.x) ||
            !std::isfinite(c.y) || !std::isfinite(c.height) || c.height < 0 ||
            c.y != static_cast<float>(placements[c.line_index].top) || c.bidi_level < 0 || c.bidi_level > 125 ||
            c.reserved0 != 0U || c.reserved1 != 0U) return invalid();
        if (i != 0U && c.line_index < carets[i - 1U].line_index) return invalid();
    }
    const auto row = placements[carets[current_index].line_index].row_index;
    const bool horizontal = direction == text_caret_direction::left || direction == text_caret_direction::right;
    const bool right = direction == text_caret_direction::right;
    const auto less = [&](std::uint32_t a, std::uint32_t b) noexcept {
        if (carets[a].x != carets[b].x) return carets[a].x < carets[b].x;
        const float ax = placements[carets[a].line_index].left, bx = placements[carets[b].line_index].left;
        return ax != bx ? ax < bx : a < b;
    };
    std::uint32_t found = UINT32_MAX;
    if (horizontal) {
        for (std::uint32_t i = 0; i < carets.size(); ++i) {
            if (placements[carets[i].line_index].row_index != row ||
                (right ? !less(current_index, i) : !less(i, current_index))) continue;
            if (found == UINT32_MAX || (right ? less(i, found) : less(found, i))) found = i;
        }
    }
    if (found == UINT32_MAX) {
        const bool forward = horizontal ? (right != (paragraph_level != 0)) : direction == text_caret_direction::down;
        std::uint32_t target_row = UINT32_MAX;
        double distance = std::numeric_limits<double>::infinity();
        for (std::uint32_t i = 0; i < carets.size(); ++i) {
            const auto candidate_row = placements[carets[i].line_index].row_index;
            if (forward ? candidate_row <= row : candidate_row >= row) continue;
            const bool nearer_row = target_row == UINT32_MAX || (forward ? candidate_row < target_row : candidate_row > target_row);
            if (nearer_row) { target_row = candidate_row; found = UINT32_MAX; distance = std::numeric_limits<double>::infinity(); }
            if (candidate_row != target_row) continue;
            if (horizontal) {
                if (found == UINT32_MAX || (right ? less(i, found) : less(found, i))) found = i;
            } else {
                const double delta = std::abs(static_cast<double>(carets[i].x) - preferred_x);
                if (delta < distance || (delta == distance && found != UINT32_MAX &&
                    carets[i].trailing == carets[current_index].trailing && carets[found].trailing != carets[current_index].trailing)) {
                    distance = delta; found = i;
                }
            }
        }
    }
    if (found != UINT32_MAX) next_index = found;
    if (error != nullptr) *error = font_error::none;
    return true;
}

} // namespace progpu::native::text

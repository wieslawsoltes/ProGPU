#include "progpu_native_text_interaction_impl.hpp"

namespace progpu::native::text {

bool try_get_text_interaction_requirements(
    std::span<const positioned_text_glyph> glyphs,
    std::span<const positioned_text_line> lines,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels,
    text_interaction_requirements& result,
    font_error* error) noexcept {
    return interaction_detail::get_requirements(glyphs, lines, cluster_ends, bidi_levels, result, error);
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
    return interaction_detail::build(glyphs, lines, cluster_ends, bidi_levels, cluster_boxes, caret_stops, cluster_box_count, caret_stop_count, error);
}

bool try_hit_test_text(
    std::span<const text_cluster_box> cluster_boxes,
    float x,
    float y,
    text_hit_test_result& result,
    font_error* error) noexcept {
    return interaction_detail::hit_test(cluster_boxes, x, y, result, error);
}

bool try_build_fragment_text_interaction(
    std::span<const positioned_text_glyph> glyphs, std::span<const positioned_text_line> lines,
    std::span<const text_fragment_placement> placements,
    std::span<const std::int32_t> cluster_ends, std::span<const std::int8_t> bidi_levels,
    std::span<text_cluster_box> cluster_boxes, std::span<text_caret_stop> caret_stops,
    std::uint32_t& cluster_box_count, std::uint32_t& caret_stop_count, font_error* error) noexcept {
    if (placements.size() != lines.size()) {
        cluster_box_count = caret_stop_count = 0U;
        if (error != nullptr) *error = font_error::invalid_argument;
        return false;
    }
    return interaction_detail::build(glyphs, lines, cluster_ends, bidi_levels,
        cluster_boxes, caret_stops, cluster_box_count, caret_stop_count, error, true, placements);
}

bool try_get_text_caret_stop(
    std::span<const text_caret_stop> caret_stops,
    std::int32_t input_position,
    bool trailing_affinity,
    text_caret_stop& result,
    font_error* error) noexcept {
    return interaction_detail::get_caret(caret_stops, input_position, trailing_affinity, result, error);
}

bool try_move_text_caret_visually(
    std::span<const text_caret_stop> caret_stops,
    std::int32_t input_position,
    bool trailing_affinity,
    std::int32_t direction,
    text_caret_stop& result,
    font_error* error) noexcept {
    return interaction_detail::move_caret(caret_stops, input_position, trailing_affinity, direction, result, error);
}

bool try_get_text_selection_rectangles(
    std::span<const text_cluster_box> cluster_boxes,
    std::int32_t input_start,
    std::int32_t input_end,
    std::span<text_rectangle> rectangles,
    std::uint32_t& written,
    font_error* error) noexcept {
    return interaction_detail::selection(cluster_boxes, input_start, input_end, rectangles, written, error);
}

bool try_move_fragment_text_caret(std::span<const text_caret_stop> carets,
    std::span<const text_fragment_placement> placements, std::uint32_t current_index,
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
            c.y != placements[c.line_index].top || c.bidi_level < 0 || c.bidi_level > 125 ||
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

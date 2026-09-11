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
    return interaction_detail::move_fragment_caret(carets, placements, current_index, direction,
        paragraph_level, preferred_x, next_index, error);
}

} // namespace progpu::native::text

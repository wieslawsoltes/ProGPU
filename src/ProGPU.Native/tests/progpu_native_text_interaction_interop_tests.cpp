#include "progpu_native_text_interaction.h"
#include "progpu_native_text.hpp"
#include <array>
#include <cstdlib>
#include <limits>
#include <iostream>

namespace {
void require(bool condition) { if (!condition) std::abort(); }
constexpr auto success = PROGPU_NATIVE_STATUS_SUCCESS;
constexpr auto invalid = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;

void measured_interaction() {
    std::array<progpu_native_positioned_text_glyph, 3> glyphs{{
        {0, 10, 0, 0, 0, 12, 8, 0},
        {1, UINT32_MAX - 1U, UINT32_MAX, 1, 0, 62, 30.25F, 0},
        {2, 11, 0, 2, 0, 81, 9, 0}}};
    std::array<progpu_native_positioned_text_line, 4> lines{{
        {0, 1, 0, 1, 8, 12, 20, 0, 0, 0, 0},
        {1, 0, 1, 1, 0, 22, 7, 0, 0, 0, 0},
        {1, 1, 1, 2, 30.25F, 62, 42, 0, 0, 0, 0},
        {2, 1, 2, 3, 9, 81, 20, 0, 0, 0, 0}}};
    std::array<std::int32_t, 3> ends{1, 2, 3};
    std::array<std::int8_t, 3> levels{0, 1, 0};
    progpu_native_text_interaction_request request{
        sizeof(request), PROGPU_NATIVE_ABI_VERSION,
        glyphs.data(), 3, lines.data(), 4, ends.data(), 3, levels.data(), 3};
    progpu_native_text_interaction_requirements needed{};
    needed.struct_size = sizeof(needed);
    require(progpu_native_text_interaction_get_measured_requirements(&request, &needed) == success);
    require(needed.cluster_box_capacity == 3 && needed.caret_stop_capacity == 6);
    std::array<progpu_native_text_cluster_box, 3> boxes{};
    std::array<progpu_native_text_caret_stop, 6> carets{};
    progpu_native_text_interaction_result result{};
    result.struct_size = sizeof(result);
    const auto build = [&]() {
        return progpu_native_text_interaction_build_measured(&request,
            boxes.data(), 3, carets.data(), 6, &result);
    };
    require(build() == success && result.cluster_box_count == 3 && result.caret_stop_count == 6);
    require(boxes[0].y == 0 && boxes[1].y == 27 && boxes[2].y == 69);
    require(boxes[1].height == 42 && boxes[1].width == 30.25F && boxes[1].line_index == 2);
    require(carets[2].y == 27 && carets[2].input_position == 2 && carets[2].trailing == 1);
    progpu_native_text_hit_test_result hit{};
    require(progpu_native_text_interaction_hit_test(boxes.data(), 3, 2, 28, &hit) == success);
    require(hit.inside == 1 && hit.line_index == 2 && hit.input_position == 2 && hit.trailing == 1);
    progpu_native_text_rectangle selection{};
    std::uint32_t written = 0;
    require(progpu_native_text_interaction_get_selection(boxes.data(), 3, 1, 2,
        &selection, 1, &written) == success);
    require(written == 1 && selection.y == 27 && selection.height == 42 && selection.width == 30.25F);
    require(progpu_native_text_interaction_build(&request, boxes.data(), 3, carets.data(), 6, &result) == success);
    require(boxes[1].y == 62); // Old ABI still treats baseline_y as the top.
    boxes[0].x = 123;
    require(progpu_native_text_interaction_build_measured(&request, boxes.data(), 2, carets.data(), 6, &result) == invalid);
    require(result.cluster_box_count == 0 && boxes[0].x == 123);
    for (const float baseline : {26.0F, 70.0F, std::numeric_limits<float>::quiet_NaN(),
            std::numeric_limits<float>::infinity()}) {
        lines[2].baseline_y = baseline;
        require(build() == invalid && result.cluster_box_count == 0 && boxes[0].x == 123);
        require(progpu_native_text_interaction_get_measured_requirements(&request, &needed) == invalid);
        require(needed.cluster_box_capacity == 0 && needed.caret_stop_capacity == 0);
    }
    lines[2].baseline_y = 27;
    lines[2].height = 0;
    lines[3].baseline_y = 39;
    require(build() == success && boxes[1].y == 27 && boxes[1].height == 0 && boxes[2].y == 27);
    boxes[0].x = 123;
    for (const float height : {-1.0F, std::numeric_limits<float>::quiet_NaN(),
            std::numeric_limits<float>::infinity()}) {
        lines[2].height = height;
        require(build() == invalid && result.cluster_box_count == 0 && boxes[0].x == 123);
    }
    lines[2].height = 0;
    lines[0].height = std::numeric_limits<float>::max();
    lines[1].height = std::numeric_limits<float>::max();
    lines[1].baseline_y = std::numeric_limits<float>::max();
    require(build() == invalid && result.cluster_box_count == 0 && boxes[0].x == 123);
}
}

int main() {
    measured_interaction();
    using namespace progpu::native::text;
    static_assert(sizeof(progpu_native_text_cluster_box) == 32U);
    static_assert(sizeof(progpu_native_text_caret_stop) == 24U);
    static_assert(sizeof(progpu_native_text_hit_test_result) == 28U);
    std::array<progpu_native_positioned_text_glyph, 4> glyphs{};
    glyphs[0] = {0U, 10U, 0U, 0, 0.0F, 0.0F, 8.0F, 0.0F};
    glyphs[1] = {1U, 11U, 0U, 0, 5.0F, 0.0F, 0.0F, 0.0F};
    glyphs[2] = {2U, 12U, 3U, 2, 8.0F, 0.0F, 10.0F, 0.0F};
    glyphs[3] = {3U, 13U, 0U, 4, 0.0F, 20.0F, 6.0F, 0.0F};
    std::array<progpu_native_positioned_text_line, 2> lines{};
    lines[0] = {0U, 3U, 0, 4, 18.0F, 0.0F, 20.0F, 0U, 0U, 0U, 0U};
    lines[1] = {3U, 1U, 4, 5, 6.0F, 20.0F, 20.0F, 0U, 0U, 0U, 0U};
    std::array<std::int32_t, 4> ends{2, 2, 4, 5};
    std::array<std::int8_t, 4> levels{0, 0, 1, 0};
    progpu_native_text_interaction_request request{};
    request.struct_size = sizeof(request);
    request.abi_version = PROGPU_NATIVE_ABI_VERSION;
    request.glyphs = glyphs.data(); request.glyph_count = 4U;
    request.lines = lines.data(); request.line_count = 2U;
    request.cluster_ends = ends.data(); request.cluster_end_count = 4U;
    request.bidi_levels = levels.data(); request.bidi_level_count = 4U;
    progpu_native_text_interaction_requirements requirements{};
    requirements.struct_size = sizeof(requirements);
    require(progpu_native_text_interaction_get_requirements(&request, &requirements) == success);
    require(requirements.cluster_box_capacity == 3U && requirements.caret_stop_capacity == 6U);
    std::array<progpu_native_text_cluster_box, 3> boxes{};
    std::array<progpu_native_text_caret_stop, 6> carets{};
    boxes[0].input_start = 123;
    progpu_native_text_interaction_result result{};
    result.struct_size = sizeof(result);
    require(progpu_native_text_interaction_build(&request, boxes.data(), 3U, carets.data(), 1U, &result) == invalid);
    require(result.cluster_box_count == 0U && result.caret_stop_count == 0U);
    require(result.error_code == static_cast<std::uint32_t>(font_error::insufficient_buffer));
    require(boxes[0].input_start == 123);
    require(progpu_native_text_interaction_build(&request, boxes.data(), 3U, carets.data(), 6U, &result) == success);
    require(result.cluster_box_count == 3U && result.caret_stop_count == 6U);

    // Existing public C++ surface and C ABI instantiate one original algorithm.
    std::array<positioned_text_glyph, 4> native_glyphs{};
    std::array<positioned_text_line, 2> native_lines{};
    for (std::size_t i = 0U; i < glyphs.size(); ++i) {
        const auto& g = glyphs[i];
        native_glyphs[i] = {g.glyph_index, g.glyph_id, g.cluster, g.x, g.y, g.advance_x, g.advance_y};
    }
    for (std::size_t i = 0U; i < lines.size(); ++i) {
        const auto& l = lines[i];
        native_lines[i] = {l.glyph_start, l.glyph_count, l.input_start, l.input_end,
            l.width, l.baseline_y, l.height, l.clipped != 0U};
    }
    std::array<text_cluster_box, 3> native_boxes{};
    std::array<text_caret_stop, 6> native_carets{};
    std::uint32_t box_count = 0U, caret_count = 0U;
    require(try_build_text_interaction(native_glyphs, native_lines, ends, levels,
        native_boxes, native_carets, box_count, caret_count));
    require(box_count == result.cluster_box_count && caret_count == result.caret_stop_count);
    for (std::size_t i = 0U; i < boxes.size(); ++i) {
        require(boxes[i].input_start == native_boxes[i].input_start && boxes[i].input_end == native_boxes[i].input_end);
        require(boxes[i].x == native_boxes[i].x && boxes[i].width == native_boxes[i].width);
        require(boxes[i].y == native_boxes[i].y && boxes[i].height == native_boxes[i].height);
        require(boxes[i].bidi_level == native_boxes[i].bidi_level && boxes[i].line_index == native_boxes[i].line_index);
    }
    progpu_native_text_hit_test_result hit{};
    for (std::size_t i = 0U; i < carets.size(); ++i) {
        require(carets[i].input_position == native_carets[i].input_position);
        require(carets[i].line_index == native_carets[i].line_index);
        require(carets[i].x == native_carets[i].x && carets[i].y == native_carets[i].y);
        require(carets[i].height == native_carets[i].height && carets[i].bidi_level == native_carets[i].bidi_level);
        require((carets[i].trailing != 0U) == native_carets[i].trailing);
    }
    require(progpu_native_text_interaction_hit_test(boxes.data(), 3U, 16.0F, 5.0F, &hit) == success);
    require(hit.inside == 1U && hit.trailing == 0U && hit.input_position == 2 && hit.bidi_level == 1);
    require(progpu_native_text_interaction_hit_test(boxes.data(), 3U, 9.0F, 5.0F, &hit) == success);
    require(hit.trailing == 1U && hit.input_position == 4);
    progpu_native_text_caret_stop caret{};
    require(progpu_native_text_interaction_get_caret(carets.data(), 6U, 2, 1U, &caret) == success);
    require(caret.input_position == 2 && caret.x == 8.0F);
    require(progpu_native_text_interaction_move_caret(carets.data(), 6U, 2, 1U, 1, &caret) == success);
    require(caret.input_position == 4 && caret.x == 8.0F);
    // Byte-valued ABI affinities and bool-valued C++ affinities must select
    // the same stop at shared logical positions, including visual movement.
    for (const bool trailing : {false, true}) {
        text_caret_stop native_caret{};
        require(try_get_text_caret_stop(native_carets, 2, trailing, native_caret));
        require(progpu_native_text_interaction_get_caret(carets.data(), 6U, 2,
            static_cast<std::uint8_t>(trailing), &caret) == success);
        require(caret.input_position == native_caret.input_position && caret.x == native_caret.x);
        require(static_cast<bool>(caret.trailing) == native_caret.trailing);
        for (const std::int32_t direction : {-1, 0, 1}) {
            require(try_move_text_caret_visually(native_carets, 2, trailing, direction, native_caret));
            require(progpu_native_text_interaction_move_caret(carets.data(), 6U, 2,
                static_cast<std::uint8_t>(trailing), direction, &caret) == success);
            require(caret.input_position == native_caret.input_position && caret.x == native_caret.x);
            require(caret.line_index == native_caret.line_index && caret.y == native_caret.y);
            require(static_cast<bool>(caret.trailing) == native_caret.trailing);
        }
    }
    std::array<progpu_native_text_rectangle, 3> rectangles{};
    std::uint32_t written = 99U;
    require(progpu_native_text_interaction_get_selection(boxes.data(), 3U, 0, 5, rectangles.data(), 1U, &written) == invalid);
    require(written == 0U);
    require(progpu_native_text_interaction_get_selection(boxes.data(), 3U, 0, 5, rectangles.data(), 3U, &written) == success);
    require(written == 2U && rectangles[0].width == 18.0F && rectangles[1].y == 20.0F);
    request.cluster_end_count = 3U;
    require(progpu_native_text_interaction_get_requirements(&request, &requirements) == invalid);
    require(requirements.cluster_box_capacity == 0U);
    boxes[2].x = std::numeric_limits<float>::quiet_NaN();
    require(progpu_native_text_interaction_hit_test(boxes.data(), 3U, 0.0F, 0.0F, &hit) == invalid);
    require(hit.inside == 0U && hit.input_position == 0);
    carets[0].trailing = 2U;
    require(progpu_native_text_interaction_get_caret(carets.data(), 6U, 0, 0U, &caret) == invalid);
    require(progpu_native_text_interaction_move_caret(carets.data(), 6U, 0, 0U, 1, &caret) == invalid);
    std::cout << "text interaction C ABI differential: PASS\n";
}

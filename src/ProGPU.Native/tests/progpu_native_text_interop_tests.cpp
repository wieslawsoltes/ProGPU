#include "progpu_native.h"

#include <cstddef>
#include <cstdint>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <string_view>
#include <vector>

namespace {

void require(bool condition) {
    if (!condition) throw std::runtime_error("native text interop assertion failed");
}

std::vector<std::uint8_t> read_font() {
    std::ifstream stream(PROGPU_NATIVE_TEST_INTER_FONT, std::ios::binary);
    require(stream.good());
    return std::vector<std::uint8_t>(
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>());
}

std::vector<progpu_native_text_scalar> ascii_scalars(std::string_view text) {
    std::vector<progpu_native_text_scalar> result;
    result.reserve(text.size());
    for (std::uint32_t index = 0U; index < text.size(); ++index) {
        result.push_back(progpu_native_text_scalar{
            static_cast<std::uint8_t>(text[index]), index, 1U, 0U, 0U, 0U});
    }
    return result;
}

void bulk_shape_is_deterministic_and_caller_owned() {
    const auto font = read_font();
    const auto input = ascii_scalars("AVATAR office 1/2");
    const progpu_native_text_shape_request request{
        sizeof(progpu_native_text_shape_request),
        PROGPU_NATIVE_ABI_VERSION,
        font.data(),
        font.size(),
        0U,
        PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES,
        input.data(),
        static_cast<std::uint32_t>(input.size()),
        nullptr,
        0U,
        nullptr,
        0U,
        nullptr,
        0U,
        nullptr,
        0U,
        nullptr,
        0U,
        0U,
        0U,
        PROGPU_NATIVE_TEXT_DIRECTION_UNSPECIFIED,
        PROGPU_NATIVE_TEXT_CLUSTER_MONOTONE_GRAPHEMES,
        0U,
        1U,
        0U,
        0U};
    progpu_native_text_line_break_requirements break_requirements{};
    break_requirements.struct_size = sizeof(break_requirements);
    require(progpu_native_text_get_line_break_requirements(
                input.data(),
                static_cast<std::uint32_t>(input.size()),
                &break_requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(break_requirements.break_capacity == input.size() &&
        break_requirements.scratch_alignment == 1U &&
        break_requirements.scratch_bytes != 0U);
    std::vector<std::uint8_t> resolved_breaks(
        break_requirements.break_capacity);
    std::vector<std::uint8_t> break_scratch(
        static_cast<std::size_t>(break_requirements.scratch_bytes));
    progpu_native_text_line_break_result break_result{};
    break_result.struct_size = sizeof(break_result);
    require(progpu_native_text_resolve_line_breaks(
                input.data(),
                static_cast<std::uint32_t>(input.size()),
                resolved_breaks.data(),
                static_cast<std::uint32_t>(resolved_breaks.size()),
                break_scratch.data(),
                break_scratch.size(),
                &break_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(break_result.break_count == input.size() &&
        break_result.error_code == 0U &&
        resolved_breaks[0U] == PROGPU_NATIVE_TEXT_LINE_BREAK_PROHIBITED &&
        resolved_breaks[6U] == PROGPU_NATIVE_TEXT_LINE_BREAK_OPPORTUNITY &&
        resolved_breaks[13U] == PROGPU_NATIVE_TEXT_LINE_BREAK_OPPORTUNITY &&
        resolved_breaks.back() == PROGPU_NATIVE_TEXT_LINE_BREAK_MANDATORY);

    progpu_native_text_shape_requirements requirements{};
    requirements.struct_size = sizeof(requirements);
    require(progpu_native_text_get_shape_requirements(
                &request, &requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(requirements.error_code == 0U &&
        requirements.glyph_capacity >= input.size() &&
        requirements.scratch_alignment == 1U &&
        requirements.scratch_bytes != 0U);

    std::vector<progpu_native_text_shaping_glyph> first(
        requirements.glyph_capacity);
    std::vector<progpu_native_text_shaping_glyph> second(
        requirements.glyph_capacity);
    std::vector<std::uint8_t> scratch(
        static_cast<std::size_t>(requirements.scratch_bytes) + 1U);
    progpu_native_text_shape_result first_result{};
    first_result.struct_size = sizeof(first_result);
    require(progpu_native_text_shape(
                &request,
                first.data(),
                static_cast<std::uint32_t>(first.size()),
                scratch.data() + 1U,
                scratch.size() - 1U,
                &first_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(first_result.error_code == 0U &&
        first_result.glyph_count != 0U &&
        first_result.glyph_count <= requirements.glyph_capacity &&
        first_result.scratch_bytes_used <= requirements.scratch_bytes);

    progpu_native_text_shape_result second_result{};
    second_result.struct_size = sizeof(second_result);
    require(progpu_native_text_shape(
                &request,
                second.data(),
                static_cast<std::uint32_t>(second.size()),
                scratch.data(),
                static_cast<std::size_t>(requirements.scratch_bytes),
                &second_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(second_result.glyph_count == first_result.glyph_count);
    for (std::uint32_t index = 0U; index < first_result.glyph_count; ++index) {
        const auto& left = first[index];
        const auto& right = second[index];
        require(left.glyph_id == right.glyph_id &&
            left.code_point == right.code_point &&
            left.cluster == right.cluster &&
            left.flags == right.flags &&
            left.advance_x == right.advance_x &&
            left.advance_y == right.advance_y &&
            left.offset_x == right.offset_x &&
            left.offset_y == right.offset_y);
    }

    progpu_native_text_context* context = nullptr;
    require(progpu_native_text_context_create(
                PROGPU_NATIVE_ABI_VERSION,
                font.data(),
                font.size(),
                0U,
                nullptr,
                0U,
                &context) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(context != nullptr);
    auto retained_request = request;
    retained_request.font_data = nullptr;
    retained_request.font_size = 0U;
    progpu_native_text_shape_requirements retained_requirements{};
    retained_requirements.struct_size = sizeof(retained_requirements);
    require(progpu_native_text_context_get_shape_requirements(
                context,
                &retained_request,
                &retained_requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(retained_requirements.glyph_capacity ==
            requirements.glyph_capacity &&
        retained_requirements.scratch_bytes == requirements.scratch_bytes);
    progpu_native_text_shape_result retained_result{};
    retained_result.struct_size = sizeof(retained_result);
    require(progpu_native_text_context_shape(
                context,
                &retained_request,
                second.data(),
                static_cast<std::uint32_t>(second.size()),
                scratch.data(),
                static_cast<std::size_t>(retained_requirements.scratch_bytes),
                &retained_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(retained_result.glyph_count == first_result.glyph_count);
    for (std::uint32_t index = 0U; index < first_result.glyph_count; ++index) {
        const auto& left = first[index];
        const auto& right = second[index];
        require(left.glyph_id == right.glyph_id &&
            left.code_point == right.code_point &&
            left.cluster == right.cluster &&
            left.flags == right.flags &&
            left.advance_x == right.advance_x &&
            left.advance_y == right.advance_y &&
            left.offset_x == right.offset_x &&
            left.offset_y == right.offset_y);
    }
    progpu_native_text_context_destroy(context);

    std::vector<std::uint8_t> breaks(first_result.glyph_count, 0U);
    breaks.back() = PROGPU_NATIVE_TEXT_LINE_BREAK_MANDATORY;
    const progpu_native_text_layout_request layout_request{
        sizeof(progpu_native_text_layout_request),
        PROGPU_NATIVE_ABI_VERSION,
        first.data(),
        first_result.glyph_count,
        breaks.data(),
        static_cast<std::uint32_t>(breaks.size()),
        16.0F / 2048.0F,
        1000.0F,
        20.0F,
        0U,
        PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT,
        PROGPU_NATIVE_TEXT_TRIMMING_NONE,
        PROGPU_NATIVE_TEXT_ALIGNMENT_CENTER,
        0U,
        0.0F,
        0U};
    progpu_native_text_layout_requirements layout_requirements{};
    layout_requirements.struct_size = sizeof(layout_requirements);
    require(progpu_native_text_layout_get_requirements(
                &layout_request,
                &layout_requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(layout_requirements.glyph_capacity == first_result.glyph_count &&
        layout_requirements.line_capacity == first_result.glyph_count &&
        layout_requirements.scratch_alignment == 1U &&
        layout_requirements.scratch_bytes != 0U);
    std::vector<progpu_native_positioned_text_glyph> positioned(
        layout_requirements.glyph_capacity);
    std::vector<progpu_native_positioned_text_line> lines(
        layout_requirements.line_capacity);
    std::vector<std::uint8_t> layout_scratch(
        static_cast<std::size_t>(layout_requirements.scratch_bytes));
    progpu_native_text_layout_result layout_result{};
    layout_result.struct_size = sizeof(layout_result);
    require(progpu_native_text_layout(
                &layout_request,
                positioned.data(),
                static_cast<std::uint32_t>(positioned.size()),
                lines.data(),
                static_cast<std::uint32_t>(lines.size()),
                layout_scratch.data(),
                layout_scratch.size(),
                &layout_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(layout_result.error_code == 0U &&
        layout_result.glyph_count == first_result.glyph_count &&
        layout_result.line_count == 1U &&
        layout_result.content_width > 0.0F &&
        layout_result.content_height == 20.0F &&
        positioned[0].x > 0.0F &&
        lines[0].glyph_count == first_result.glyph_count &&
        lines[0].clipped == 0U);

    progpu_native_text_layout_result short_layout_result{};
    short_layout_result.struct_size = sizeof(short_layout_result);
    require(progpu_native_text_layout(
                &layout_request,
                positioned.data(),
                static_cast<std::uint32_t>(positioned.size()),
                lines.data(),
                static_cast<std::uint32_t>(lines.size()),
                layout_scratch.data(),
                layout_scratch.size() - 1U,
                &short_layout_result) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(short_layout_result.error_code == 7U);

    progpu_native_text_shape_result short_result{};
    short_result.struct_size = sizeof(short_result);
    require(progpu_native_text_shape(
                &request,
                second.data(),
                static_cast<std::uint32_t>(second.size()),
                scratch.data(),
                static_cast<std::size_t>(requirements.scratch_bytes - 1U),
                &short_result) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(short_result.glyph_count == 0U && short_result.error_code == 7U);
}

} // namespace

int main() {
    bulk_shape_is_deterministic_and_caller_owned();
    return 0;
}

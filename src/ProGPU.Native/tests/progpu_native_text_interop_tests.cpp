#include "progpu_native.h"

#include <cstddef>
#include <cstdint>
#include <fstream>
#include <iterator>
#include <limits>
#include <stdexcept>
#include <string_view>
#include <vector>

namespace {

void require(bool condition) {
    if (!condition) throw std::runtime_error("native text interop assertion failed");
}

std::vector<std::uint8_t> read_file(const char* path) {
    std::ifstream stream(path, std::ios::binary);
    require(stream.good());
    return std::vector<std::uint8_t>(
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>());
}

std::vector<std::uint8_t> read_font() {
    return read_file(PROGPU_NATIVE_TEST_INTER_FONT);
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

    progpu_native_text_bidi_requirements bidi_requirements{};
    bidi_requirements.struct_size = sizeof(bidi_requirements);
    require(progpu_native_text_get_bidi_requirements(
                input.data(),
                static_cast<std::uint32_t>(input.size()),
                &bidi_requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(bidi_requirements.level_capacity == input.size() &&
        bidi_requirements.scratch_alignment == 1U &&
        bidi_requirements.scratch_bytes != 0U);
    std::vector<progpu_native_text_bidi_level> bidi_levels(
        bidi_requirements.level_capacity);
    std::vector<std::uint8_t> bidi_scratch(
        static_cast<std::size_t>(bidi_requirements.scratch_bytes));
    progpu_native_text_bidi_result bidi_result{};
    bidi_result.struct_size = sizeof(bidi_result);
    require(progpu_native_text_resolve_bidi(
                input.data(),
                static_cast<std::uint32_t>(input.size()),
                -1,
                bidi_levels.data(),
                static_cast<std::uint32_t>(bidi_levels.size()),
                bidi_scratch.data(),
                bidi_scratch.size(),
                &bidi_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(bidi_result.level_count == input.size() &&
        bidi_result.paragraph_level == 0 &&
        bidi_result.error_code == 0U &&
        bidi_levels.front().level == 0 &&
        bidi_levels.back().level == 0);
    bidi_result.struct_size = sizeof(bidi_result);
    require(progpu_native_text_resolve_bidi(
                input.data(),
                static_cast<std::uint32_t>(input.size()),
                1,
                bidi_levels.data(),
                static_cast<std::uint32_t>(bidi_levels.size()),
                bidi_scratch.data(),
                bidi_scratch.size(),
                &bidi_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(bidi_result.paragraph_level == 1 &&
        bidi_levels.front().level == 2);

    progpu_native_text_shape_requirements requirements{};
    requirements.struct_size = sizeof(requirements);
    require(progpu_native_text_get_shape_requirements(
                &request, &requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(requirements.error_code == 0U &&
        requirements.glyph_capacity >= input.size() &&
        requirements.scratch_alignment == 1U &&
        requirements.scratch_bytes != 0U);

    const std::uint8_t malformed_normalization[]{0U};
    auto malformed_request = request;
    malformed_request.normalization_data = malformed_normalization;
    malformed_request.normalization_data_size =
        sizeof(malformed_normalization);
    progpu_native_text_shape_requirements malformed_requirements{};
    malformed_requirements.struct_size = sizeof(malformed_requirements);
    require(progpu_native_text_get_shape_requirements(
                &malformed_request,
                &malformed_requirements) ==
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(malformed_requirements.error_code == 1U);

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

    const std::vector<progpu_native_text_scalar> mark_input{
        progpu_native_text_scalar{0x61U, 0U, 1U, 0U, 0U, 0U},
        progpu_native_text_scalar{0x0315U, 1U, 1U, 0U, 0U, 0U}};
    auto mark_request = request;
    mark_request.input = mark_input.data();
    mark_request.input_count =
        static_cast<std::uint32_t>(mark_input.size());
    progpu_native_text_shape_requirements mark_requirements{};
    mark_requirements.struct_size = sizeof(mark_requirements);
    require(progpu_native_text_get_shape_requirements(
                &mark_request,
                &mark_requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    std::vector<progpu_native_text_shaping_glyph> mark_glyphs(
        mark_requirements.glyph_capacity);
    std::vector<std::uint8_t> mark_scratch(
        static_cast<std::size_t>(mark_requirements.scratch_bytes));
    progpu_native_text_shape_result mark_result{};
    mark_result.struct_size = sizeof(mark_result);
    require(progpu_native_text_shape(
                &mark_request,
                mark_glyphs.data(),
                static_cast<std::uint32_t>(mark_glyphs.size()),
                mark_scratch.data(),
                mark_scratch.size(),
                &mark_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(mark_result.glyph_count == 2U &&
        mark_glyphs[1U].code_point == 0x0315U &&
        mark_glyphs[1U].advance_y == 0 &&
        mark_glyphs[1U].offset_y < 0);

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

    const std::vector<progpu_native_text_shaping_glyph> vertical_input{
        {1U, 0x41U, 0, 0U, 0, 100, 0, 0},
        {2U, 0x42U, 1, 0U, 0, 100, 0, 0}};
    const std::vector<std::uint8_t> vertical_breaks{
        PROGPU_NATIVE_TEXT_LINE_BREAK_PROHIBITED,
        PROGPU_NATIVE_TEXT_LINE_BREAK_MANDATORY};
    const progpu_native_text_layout_request vertical_request{
        sizeof(progpu_native_text_layout_request),
        PROGPU_NATIVE_ABI_VERSION,
        vertical_input.data(),
        static_cast<std::uint32_t>(vertical_input.size()),
        vertical_breaks.data(),
        static_cast<std::uint32_t>(vertical_breaks.size()),
        0.01F,
        100.0F,
        20.0F,
        0U,
        PROGPU_NATIVE_TEXT_DIRECTION_TOP_TO_BOTTOM,
        PROGPU_NATIVE_TEXT_TRIMMING_NONE,
        PROGPU_NATIVE_TEXT_ALIGNMENT_LEFT,
        0U,
        0.0F,
        0U};
    progpu_native_text_vertical_layout_requirements vertical_requirements{};
    vertical_requirements.struct_size = sizeof(vertical_requirements);
    require(progpu_native_text_vertical_layout_get_requirements(
                &vertical_request,
                &vertical_requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(vertical_requirements.glyph_capacity == vertical_input.size() &&
        vertical_requirements.column_capacity == vertical_input.size() &&
        vertical_requirements.scratch_bytes != 0U);
    std::vector<progpu_native_positioned_text_glyph> vertical_positioned(
        vertical_requirements.glyph_capacity);
    std::vector<progpu_native_positioned_text_column> columns(
        vertical_requirements.column_capacity);
    std::vector<std::uint8_t> vertical_scratch(
        static_cast<std::size_t>(vertical_requirements.scratch_bytes));
    progpu_native_text_vertical_layout_result vertical_result{};
    vertical_result.struct_size = sizeof(vertical_result);
    require(progpu_native_text_vertical_layout(
                &vertical_request,
                vertical_positioned.data(),
                static_cast<std::uint32_t>(vertical_positioned.size()),
                columns.data(),
                static_cast<std::uint32_t>(columns.size()),
                vertical_scratch.data(),
                vertical_scratch.size(),
                &vertical_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(vertical_result.glyph_count == vertical_input.size() &&
        vertical_result.column_count == 1U &&
        vertical_positioned[0U].font_index == 0U &&
        vertical_positioned[0U].y == 0.0F &&
        vertical_positioned[1U].y == 1.0F &&
        columns[0U].glyph_count == vertical_input.size() &&
        columns[0U].height == 2.0F);

    const progpu_native_text_layout_options paragraph_options{
        sizeof(progpu_native_text_layout_options),
        16.0F / 2048.0F,
        1000.0F,
        20.0F,
        0U,
        PROGPU_NATIVE_TEXT_DIRECTION_UNSPECIFIED,
        PROGPU_NATIVE_TEXT_TRIMMING_NONE,
        PROGPU_NATIVE_TEXT_ALIGNMENT_CENTER,
        0U,
        0.0F,
        0U,
        0U};
    progpu_native_text_paragraph_requirements paragraph_requirements{};
    paragraph_requirements.struct_size = sizeof(paragraph_requirements);
    require(progpu_native_text_context_get_paragraph_requirements(
                context,
                &retained_request,
                &paragraph_options,
                &paragraph_requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(paragraph_requirements.glyph_capacity >= first_result.glyph_count &&
        paragraph_requirements.line_capacity ==
            paragraph_requirements.glyph_capacity &&
        paragraph_requirements.scratch_alignment == 1U &&
        paragraph_requirements.scratch_bytes != 0U);
    std::vector<progpu_native_positioned_text_glyph> paragraph_glyphs(
        paragraph_requirements.glyph_capacity);
    std::vector<progpu_native_positioned_text_line> paragraph_lines(
        paragraph_requirements.line_capacity);
    std::vector<std::uint8_t> paragraph_scratch(
        static_cast<std::size_t>(paragraph_requirements.scratch_bytes));
    progpu_native_text_paragraph_result paragraph_result{};
    paragraph_result.struct_size = sizeof(paragraph_result);
    require(progpu_native_text_context_layout_paragraph(
                context,
                &retained_request,
                &paragraph_options,
                paragraph_glyphs.data(),
                static_cast<std::uint32_t>(paragraph_glyphs.size()),
                paragraph_lines.data(),
                static_cast<std::uint32_t>(paragraph_lines.size()),
                paragraph_scratch.data(),
                paragraph_scratch.size(),
                &paragraph_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(paragraph_result.error_code == 0U &&
        paragraph_result.error_stage ==
            PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_NONE &&
        paragraph_result.paragraph_level == 0 &&
        paragraph_result.shaped_glyph_count == first_result.glyph_count &&
        paragraph_result.glyph_count == layout_result.glyph_count &&
        paragraph_result.line_count == layout_result.line_count &&
        paragraph_result.content_width == layout_result.content_width &&
        paragraph_result.content_height == layout_result.content_height);
    for (std::uint32_t index = 0U; index < paragraph_result.glyph_count;
        ++index) {
        require(paragraph_glyphs[index].glyph_id == positioned[index].glyph_id &&
            paragraph_glyphs[index].cluster == positioned[index].cluster &&
            paragraph_glyphs[index].x == positioned[index].x &&
            paragraph_glyphs[index].y == positioned[index].y &&
            paragraph_glyphs[index].advance_x == positioned[index].advance_x &&
            paragraph_glyphs[index].advance_y == positioned[index].advance_y);
    }

    auto trimmed_options = paragraph_options;
    trimmed_options.maximum_width = 1.0F;
    trimmed_options.maximum_lines = 1U;
    trimmed_options.trimming = PROGPU_NATIVE_TEXT_TRIMMING_CHARACTER_ELLIPSIS;
    trimmed_options.alignment = PROGPU_NATIVE_TEXT_ALIGNMENT_LEFT;
    trimmed_options.ellipsis_glyph_id = first[0U].glyph_id;
    trimmed_options.ellipsis_advance =
        static_cast<float>(first[0U].advance_x);
    paragraph_result.struct_size = sizeof(paragraph_result);
    require(progpu_native_text_context_layout_paragraph(
                context,
                &retained_request,
                &trimmed_options,
                paragraph_glyphs.data(),
                static_cast<std::uint32_t>(paragraph_glyphs.size()),
                paragraph_lines.data(),
                static_cast<std::uint32_t>(paragraph_lines.size()),
                paragraph_scratch.data(),
                paragraph_scratch.size(),
                &paragraph_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(paragraph_result.glyph_count == 1U &&
        paragraph_result.line_count == 1U &&
        paragraph_glyphs[0U].glyph_index ==
            std::numeric_limits<std::uint32_t>::max() &&
        paragraph_glyphs[0U].glyph_id == trimmed_options.ellipsis_glyph_id &&
        paragraph_glyphs[0U].font_index == 0U &&
        paragraph_lines[0U].clipped != 0U);

    progpu_native_text_paragraph_result short_paragraph_result{};
    short_paragraph_result.struct_size = sizeof(short_paragraph_result);
    require(progpu_native_text_context_layout_paragraph(
                context,
                &retained_request,
                &paragraph_options,
                paragraph_glyphs.data(),
                static_cast<std::uint32_t>(paragraph_glyphs.size()),
                paragraph_lines.data(),
                static_cast<std::uint32_t>(paragraph_lines.size()),
                paragraph_scratch.data(),
                paragraph_scratch.size() - 1U,
                &short_paragraph_result) ==
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(short_paragraph_result.error_code == 7U &&
        short_paragraph_result.error_stage ==
            PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT);

    const std::vector<progpu_native_text_scalar> mixed_input{
        {0x61U, 0U, 1U, 0U, 0U, 0U},
        {0x62U, 1U, 1U, 0U, 0U, 0U},
        {0x63U, 2U, 1U, 0U, 0U, 0U},
        {0x20U, 3U, 1U, 0U, 0U, 0U},
        {0x05D0U, 4U, 1U, 0U, 0U, 0U},
        {0x05D1U, 5U, 1U, 0U, 0U, 0U},
        {0x05D2U, 6U, 1U, 0U, 0U, 0U}};
    auto mixed_request = retained_request;
    mixed_request.input = mixed_input.data();
    mixed_request.input_count = static_cast<std::uint32_t>(mixed_input.size());
    mixed_request.direction = PROGPU_NATIVE_TEXT_DIRECTION_UNSPECIFIED;
    const progpu_native_text_layout_options mixed_options{
        sizeof(progpu_native_text_layout_options),
        16.0F / 2048.0F,
        0.0F,
        20.0F,
        0U,
        PROGPU_NATIVE_TEXT_DIRECTION_UNSPECIFIED,
        PROGPU_NATIVE_TEXT_TRIMMING_NONE,
        PROGPU_NATIVE_TEXT_ALIGNMENT_LEFT,
        0U,
        0.0F,
        0U,
        0U};
    paragraph_requirements.struct_size = sizeof(paragraph_requirements);
    require(progpu_native_text_context_get_paragraph_requirements(
                context,
                &mixed_request,
                &mixed_options,
                &paragraph_requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    paragraph_glyphs.resize(paragraph_requirements.glyph_capacity);
    paragraph_lines.resize(paragraph_requirements.line_capacity);
    paragraph_scratch.resize(
        static_cast<std::size_t>(paragraph_requirements.scratch_bytes));
    paragraph_result.struct_size = sizeof(paragraph_result);
    require(progpu_native_text_context_layout_paragraph(
                context,
                &mixed_request,
                &mixed_options,
                paragraph_glyphs.data(),
                static_cast<std::uint32_t>(paragraph_glyphs.size()),
                paragraph_lines.data(),
                static_cast<std::uint32_t>(paragraph_lines.size()),
                paragraph_scratch.data(),
                paragraph_scratch.size(),
                &paragraph_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(paragraph_result.paragraph_level == 0 &&
        paragraph_result.glyph_count == mixed_input.size() &&
        paragraph_result.line_count == 1U &&
        paragraph_result.shaping_run_count == 2U);
    constexpr std::uint32_t expected_clusters[]{0U, 1U, 2U, 3U, 6U, 5U, 4U};
    for (std::size_t index = 0U; index < mixed_input.size(); ++index) {
        require(paragraph_glyphs[index].cluster ==
            static_cast<std::int32_t>(expected_clusters[index]));
    }

    const std::vector<progpu_native_text_scalar> mixed_script_input{
        {0x61U, 0U, 1U, 0U, 0U, 0U},
        {0x62U, 1U, 1U, 0U, 0U, 0U},
        {0x03B1U, 2U, 1U, 0U, 0U, 0U},
        {0x03B2U, 3U, 1U, 0U, 0U, 0U}};
    mixed_request.input = mixed_script_input.data();
    mixed_request.input_count =
        static_cast<std::uint32_t>(mixed_script_input.size());
    paragraph_requirements.struct_size = sizeof(paragraph_requirements);
    require(progpu_native_text_context_get_paragraph_requirements(
                context,
                &mixed_request,
                &mixed_options,
                &paragraph_requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    paragraph_glyphs.resize(paragraph_requirements.glyph_capacity);
    paragraph_lines.resize(paragraph_requirements.line_capacity);
    paragraph_scratch.resize(
        static_cast<std::size_t>(paragraph_requirements.scratch_bytes));
    paragraph_result.struct_size = sizeof(paragraph_result);
    require(progpu_native_text_context_layout_paragraph(
                context,
                &mixed_request,
                &mixed_options,
                paragraph_glyphs.data(),
                static_cast<std::uint32_t>(paragraph_glyphs.size()),
                paragraph_lines.data(),
                static_cast<std::uint32_t>(paragraph_lines.size()),
                paragraph_scratch.data(),
                paragraph_scratch.size(),
                &paragraph_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(paragraph_result.paragraph_level == 0 &&
        paragraph_result.glyph_count == mixed_script_input.size() &&
        paragraph_result.line_count == 1U &&
        paragraph_result.shaping_run_count == 2U);
    for (std::size_t index = 0U; index < mixed_script_input.size(); ++index) {
        require(paragraph_glyphs[index].cluster ==
            static_cast<std::int32_t>(index));
    }

    const auto fallback_font = read_file(PROGPU_NATIVE_TEST_NOTO_CFF_FONT);
    std::uint32_t fallback_font_index = 0U;
    require(progpu_native_text_context_add_fallback_font(
                context,
                fallback_font.data(),
                fallback_font.size(),
                0U,
                0x4E6F746F434A4BULL,
                &fallback_font_index) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(fallback_font_index == 1U);
    const std::vector<progpu_native_text_scalar> fallback_input{
        {0x61U, 0U, 1U, 0U, 0U, 0U},
        {0x65E5U, 1U, 1U, 0U, 0U, 0U},
        {0x62U, 2U, 1U, 0U, 0U, 0U}};
    mixed_request.input = fallback_input.data();
    mixed_request.input_count =
        static_cast<std::uint32_t>(fallback_input.size());
    paragraph_requirements.struct_size = sizeof(paragraph_requirements);
    require(progpu_native_text_context_get_paragraph_requirements(
                context,
                &mixed_request,
                &mixed_options,
                &paragraph_requirements) == PROGPU_NATIVE_STATUS_SUCCESS);
    paragraph_glyphs.resize(paragraph_requirements.glyph_capacity);
    paragraph_lines.resize(paragraph_requirements.line_capacity);
    paragraph_scratch.resize(
        static_cast<std::size_t>(paragraph_requirements.scratch_bytes));
    paragraph_result.struct_size = sizeof(paragraph_result);
    require(progpu_native_text_context_layout_paragraph(
                context,
                &mixed_request,
                &mixed_options,
                paragraph_glyphs.data(),
                static_cast<std::uint32_t>(paragraph_glyphs.size()),
                paragraph_lines.data(),
                static_cast<std::uint32_t>(paragraph_lines.size()),
                paragraph_scratch.data(),
                paragraph_scratch.size(),
                &paragraph_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(paragraph_result.glyph_count == fallback_input.size() &&
        paragraph_result.shaping_run_count == 3U &&
        paragraph_result.cached_plan_count >= 3U &&
        paragraph_result.plan_build_count >=
            paragraph_result.cached_plan_count &&
        paragraph_glyphs[0U].font_index == 0U &&
        paragraph_glyphs[1U].font_index == fallback_font_index &&
        paragraph_glyphs[1U].glyph_id != 0U &&
        paragraph_glyphs[2U].font_index == 0U);
    const std::uint32_t retained_plan_count =
        paragraph_result.cached_plan_count;
    const std::uint32_t retained_plan_builds =
        paragraph_result.plan_build_count;
    paragraph_result.struct_size = sizeof(paragraph_result);
    require(progpu_native_text_context_layout_paragraph(
                context,
                &mixed_request,
                &mixed_options,
                paragraph_glyphs.data(),
                static_cast<std::uint32_t>(paragraph_glyphs.size()),
                paragraph_lines.data(),
                static_cast<std::uint32_t>(paragraph_lines.size()),
                paragraph_scratch.data(),
                paragraph_scratch.size(),
                &paragraph_result) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(paragraph_result.cached_plan_count == retained_plan_count &&
        paragraph_result.plan_build_count == retained_plan_builds);

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
    progpu_native_text_context_destroy(context);
}

} // namespace

int main() {
    bulk_shape_is_deterministic_and_caller_owned();
    return 0;
}

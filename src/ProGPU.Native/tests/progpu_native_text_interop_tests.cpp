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

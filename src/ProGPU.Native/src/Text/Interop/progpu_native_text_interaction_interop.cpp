#include "progpu_native_text_interaction.h"
#include "../progpu_native_text_interaction_impl.hpp"

namespace {
using namespace progpu::native::text;
namespace algorithms = progpu::native::text::interaction_detail;

template<class T>
bool buffer(const T* data, std::uint32_t count) noexcept {
    return count == 0U || (data != nullptr &&
        reinterpret_cast<std::uintptr_t>(data) % alignof(T) == 0U);
}

bool request_valid(const progpu_native_text_interaction_request* value) noexcept {
    return value != nullptr && value->struct_size == sizeof(*value) &&
        value->abi_version == PROGPU_NATIVE_ABI_VERSION &&
        value->glyph_count <= std::numeric_limits<std::uint32_t>::max() / 2U &&
        value->cluster_end_count == value->glyph_count &&
        value->bidi_level_count == value->glyph_count &&
        buffer(value->glyphs, value->glyph_count) &&
        buffer(value->lines, value->line_count) &&
        buffer(value->cluster_ends, value->cluster_end_count) &&
        buffer(value->bidi_levels, value->bidi_level_count);
}

progpu_native_status status(bool success) noexcept {
    return success ? PROGPU_NATIVE_STATUS_SUCCESS : PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
}
}

progpu_native_status progpu_native_text_interaction_get_requirements(
    const progpu_native_text_interaction_request* request,
    progpu_native_text_interaction_requirements* result) {
    if (result == nullptr || result->struct_size != sizeof(*result))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *result = {};
    result->struct_size = sizeof(*result);
    result->error_code = static_cast<std::uint32_t>(font_error::invalid_argument);
    if (!request_valid(request)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    text_interaction_requirements required{};
    font_error error{};
    const bool success = algorithms::get_requirements(
        std::span(request->glyphs, request->glyph_count),
        std::span(request->lines, request->line_count),
        std::span(request->cluster_ends, request->cluster_end_count),
        std::span(request->bidi_levels, request->bidi_level_count), required, &error);
    result->cluster_box_capacity = required.cluster_box_capacity;
    result->caret_stop_capacity = required.caret_stop_capacity;
    result->error_code = static_cast<std::uint32_t>(error);
    return status(success);
}

progpu_native_status progpu_native_text_interaction_build(
    const progpu_native_text_interaction_request* request,
    progpu_native_text_cluster_box* boxes, std::uint32_t box_capacity,
    progpu_native_text_caret_stop* carets, std::uint32_t caret_capacity,
    progpu_native_text_interaction_result* result) {
    if (result == nullptr || result->struct_size != sizeof(*result))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *result = {};
    result->struct_size = sizeof(*result);
    result->error_code = static_cast<std::uint32_t>(font_error::invalid_argument);
    if (!request_valid(request) || !buffer(boxes, box_capacity) || !buffer(carets, caret_capacity))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    font_error error{};
    const bool success = algorithms::build(
        std::span(request->glyphs, request->glyph_count),
        std::span(request->lines, request->line_count),
        std::span(request->cluster_ends, request->cluster_end_count),
        std::span(request->bidi_levels, request->bidi_level_count),
        std::span(boxes, box_capacity), std::span(carets, caret_capacity),
        result->cluster_box_count, result->caret_stop_count, &error);
    result->error_code = static_cast<std::uint32_t>(error);
    return status(success);
}

progpu_native_status progpu_native_text_interaction_hit_test(
    const progpu_native_text_cluster_box* boxes, std::uint32_t count, float x, float y,
    progpu_native_text_hit_test_result* result) {
    if (result == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *result = {};
    if (!buffer(boxes, count)) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    return status(algorithms::hit_test(std::span(boxes, count), x, y, *result, nullptr));
}

progpu_native_status progpu_native_text_interaction_get_caret(
    const progpu_native_text_caret_stop* carets, std::uint32_t count,
    std::int32_t position, std::uint8_t trailing, progpu_native_text_caret_stop* result) {
    if (result == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *result = {};
    if (!buffer(carets, count) || trailing > 1U) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    return status(algorithms::get_caret(std::span(carets, count), position, trailing != 0U, *result, nullptr));
}

progpu_native_status progpu_native_text_interaction_move_caret(
    const progpu_native_text_caret_stop* carets, std::uint32_t count,
    std::int32_t position, std::uint8_t trailing, std::int32_t direction,
    progpu_native_text_caret_stop* result) {
    if (result == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *result = {};
    if (!buffer(carets, count) || trailing > 1U || direction < -1 || direction > 1)
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    return status(algorithms::move_caret(std::span(carets, count), position, trailing != 0U, direction, *result, nullptr));
}

progpu_native_status progpu_native_text_interaction_get_selection(
    const progpu_native_text_cluster_box* boxes, std::uint32_t count,
    std::int32_t start, std::int32_t end,
    progpu_native_text_rectangle* rectangles, std::uint32_t capacity, std::uint32_t* written) {
    if (written == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *written = 0U;
    if (!buffer(boxes, count) || !buffer(rectangles, capacity))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    return status(algorithms::selection(std::span(boxes, count), start, end,
        std::span(rectangles, capacity), *written, nullptr));
}

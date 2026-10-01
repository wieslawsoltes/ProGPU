#include "progpu_native.h"
#include "../progpu_native_edit_word_boundaries.hpp"

#include <algorithm>
#include <limits>

namespace {
using progpu::native::text::edit_word_boundary_error;

struct address_range { std::uintptr_t begin{}, end{}; };

template<class T>
bool range(const T* pointer, std::uint32_t count, address_range& result) noexcept {
    const auto begin = reinterpret_cast<std::uintptr_t>(pointer);
    if (count != 0U && (pointer == nullptr || begin % alignof(T) != 0U)) return false;
    const auto bytes = static_cast<std::uint64_t>(count) * sizeof(T);
    if (bytes > std::numeric_limits<std::uintptr_t>::max() - begin) return false;
    result = {begin, begin + static_cast<std::uintptr_t>(bytes)};
    return true;
}

std::uint32_t wire_error(edit_word_boundary_error error) noexcept {
    switch (error) {
    case edit_word_boundary_error::none: return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_NONE;
    case edit_word_boundary_error::invalid_encoding: return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_ENCODING;
    case edit_word_boundary_error::input_too_large: return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INPUT_TOO_LARGE;
    case edit_word_boundary_error::allocation_failure: return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_ALLOCATION_FAILURE;
    case edit_word_boundary_error::dependency_unavailable: return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_DEPENDENCY_UNAVAILABLE;
    case edit_word_boundary_error::dependency_failure: return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_DEPENDENCY_FAILURE;
    case edit_word_boundary_error::unqualified_bmp_symbol_policy: return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_UNQUALIFIED_BMP_SYMBOL_POLICY;
    case edit_word_boundary_error::unqualified_joiner_policy: return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_UNQUALIFIED_JOINER_POLICY;
    case edit_word_boundary_error::unqualified_complex_script_policy: return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_UNQUALIFIED_COMPLEX_SCRIPT_POLICY;
    case edit_word_boundary_error::unqualified_script_item_transition_policy: return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_UNQUALIFIED_SCRIPT_ITEM_TRANSITION_POLICY;
    case edit_word_boundary_error::invalid_paragraph_level: return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_PARAGRAPH_LEVEL;
    }
    return PROGPU_NATIVE_EDIT_WORD_BOUNDARY_DEPENDENCY_FAILURE;
}

progpu_native_edit_word_boundary_result failure(std::uint32_t error,
    progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT) noexcept {
    return {static_cast<std::uint32_t>(status), error, 0U, 0U};
}
}

progpu_native_edit_word_boundary_result progpu_native_text_resolve_edit_word_boundaries_utf16(
    const std::uint16_t* source, std::uint32_t source_length, std::int32_t paragraph_level,
    std::uint32_t* positions, std::uint32_t position_capacity) {
    if (paragraph_level != 0 && paragraph_level != 1)
        return failure(PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_PARAGRAPH_LEVEL);
    if (source_length > static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max()))
        return failure(PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INPUT_TOO_LARGE);
    address_range input{}, output{};
    if (!range(source, source_length, input) || !range(positions, position_capacity, output) ||
        (source_length != 0U && position_capacity != 0U && input.begin < output.end && output.begin < input.end))
        return failure(PROGPU_NATIVE_EDIT_WORD_BOUNDARY_INVALID_BUFFER);
    progpu::native::text::edit_word_boundary_snapshot candidate;
    edit_word_boundary_error error{};
    // The original classifier owns atomic allocation/encoding/policy checks and
    // a full-source bidi/line/dictionary pass. Transport never rewrites its input.
    if (!progpu::native::text::try_create_edit_word_boundary_snapshot(
            std::span(source, source_length), candidate, error, static_cast<std::int8_t>(paragraph_level))) {
        auto status = PROGPU_NATIVE_STATUS_UNSUPPORTED;
        if (error == edit_word_boundary_error::allocation_failure) status = PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
        else if (error == edit_word_boundary_error::dependency_failure) status = PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        else if (error == edit_word_boundary_error::invalid_encoding || error == edit_word_boundary_error::input_too_large ||
                 error == edit_word_boundary_error::invalid_paragraph_level) status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        return failure(wire_error(error), status);
    }
    if (candidate.positions.size() > position_capacity)
        return failure(PROGPU_NATIVE_EDIT_WORD_BOUNDARY_OUTPUT_TOO_SMALL);
    std::copy(candidate.positions.begin(), candidate.positions.end(), positions);
    return {PROGPU_NATIVE_STATUS_SUCCESS, PROGPU_NATIVE_EDIT_WORD_BOUNDARY_NONE,
        static_cast<std::uint32_t>(candidate.positions.size()), candidate.leading_content_start};
}

#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>

namespace progpu::native::scene {

struct validation_result {
    progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    progpu_native_scene_validation_error error =
        PROGPU_NATIVE_SCENE_VALIDATION_HEADER;
    std::uint32_t error_offset = 0U;
    std::uint32_t draw_count = 0U;
    std::uint32_t maximum_stack_depth = 0U;
    std::uint64_t payload_bytes = 0U;
    progpu_native_scene_header header{};
};

validation_result validate(
    const void* stream,
    std::size_t stream_size) noexcept;

bool generations_do_not_regress(
    const void* previous_stream,
    const progpu_native_scene_header& previous_header,
    const void* next_stream,
    const progpu_native_scene_header& next_header,
    std::uint32_t& error_offset) noexcept;

void write_metrics(
    const validation_result& result,
    progpu_native_scene_metrics* metrics) noexcept;

} // namespace progpu::native::scene

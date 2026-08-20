#pragma once

#include "progpu_native.h"

struct progpu_native_engine;

namespace progpu::native::execution {

progpu_native_status begin_hit_test(
    progpu_native_engine* engine,
    const progpu_native_hit_test_query* query,
    std::uint64_t* request_token);

progpu_native_status poll_hit_test(
    progpu_native_engine* engine,
    std::uint64_t request_token,
    progpu_native_hit_test_result* results,
    std::uint32_t result_capacity,
    std::uint32_t* result_count,
    progpu_native_hit_test_result* summary,
    std::uint8_t* complete);

} // namespace progpu::native::execution

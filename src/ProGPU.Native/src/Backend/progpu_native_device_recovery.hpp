#pragma once

#include "progpu_native.h"

struct progpu_native_engine;

namespace progpu::native::recovery {

progpu_native_status mark_device_lost(
    progpu_native_engine* engine) noexcept;

progpu_native_status clone_retained_cpu_state(
    const progpu_native_engine* source,
    progpu_native_engine* replacement) noexcept;

} // namespace progpu::native::recovery

#pragma once

#include <span>

namespace progpu::native::direct2d::core {

// Internal recorder arithmetic, not an installed C++ or C ABI capability.
// Borrowed validated dash storage; DPI must be finite and positive.
void scale_hairline_dashes(
    std::span<double> intervals, double& offset, float dpi) noexcept;

} // namespace progpu::native::direct2d::core

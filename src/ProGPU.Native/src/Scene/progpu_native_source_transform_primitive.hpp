#pragma once

#include "progpu_native_shader_affine_frame.hpp"
#include <numbers>

namespace progpu::native::shader_effect {

struct source_angle final {
    float degrees{};
    float radians{};
};

// Original resource order: reduce the original double before narrowing, then
// one float product by the pre-divided radians-per-degree constant. This does
// not establish any sine/cosine/tangent policy or source rotation admission.
inline bool reduce_source_angle(double degrees, source_angle& output) noexcept {
    if (!std::isfinite(degrees)) return false;
    source_angle candidate{};
    candidate.degrees = static_cast<float>(std::fmod(degrees, 360.0));
    constexpr float radians_per_degree = std::numbers::pi_v<float> / 180.0F;
    candidate.radians = source_product(candidate.degrees, radians_per_degree);
    if (!std::isfinite(candidate.radians)) return false;
    output = candidate;
    return true;
}

// Two ordered products, not simplified compensation. Reuse ProGPU-owned
// binary32 matrix arithmetic, preserving rounding and atomic failure.
inline bool center_source_primitive(const axis_matrix& core, float center_x,
    float center_y, axis_matrix& output) noexcept {
    if (!finite_affine_matrix(core) || !std::isfinite(center_x) || !std::isfinite(center_y)) return false;
    const axis_matrix before{1, 1, 1, 1, -center_x, -center_y};
    const axis_matrix after{1, 1, 1, 1, center_x, center_y};
    const auto candidate = multiply_affine_matrix(multiply_affine_matrix(before, core), after);
    if (!finite_affine_matrix(candidate)) return false;
    output = candidate;
    return true;
}

} // namespace progpu::native::shader_effect

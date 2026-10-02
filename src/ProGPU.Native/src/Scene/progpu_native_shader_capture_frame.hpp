#pragma once

#include "progpu_native.h"

#include <cmath>
#include <cstdint>

namespace progpu::native::shader_effect {

// Each operation publishes its original float result before the next one.
// These bounded source-metadata operations must not become a fused multiply/add.
inline float source_product(float first, float second) noexcept {
    volatile float result = first * second;
    return result;
}
inline float source_sum(float first, float second) noexcept {
    volatile float result = first + second;
    return result;
}

// First-family proof boundary, not a replacement for the remaining general
// source inverse contract. In this range all diagonal 4x4 cofactors,
// determinant/reciprocal products and the identity residual are exact powers
// of two. Merely testing (1 / scale) * scale would not prove the original full
// matrix inverse used by WPF for arbitrary finite positive scales.
inline bool exact_capture_scale(float scale) noexcept {
    if (!std::isfinite(scale) || scale < 0x1p-8F || scale > 0x1p8F) return false;
    int exponent{};
    return std::frexp(scale, &exponent) == 0.5F;
}

// Caller supplies actual original local float edges and a proven source matrix.
// This pure helper never manufactures transform provenance from scene bounds.
// O(1), no allocation, target clipping, geometry changes or GPU work.
inline bool complete_capture_frame(progpu_native_scene_shader_capture_frame& frame) noexcept {
    const auto& f = frame;
    if (!std::isfinite(f.local_left) || !std::isfinite(f.local_top) ||
        !std::isfinite(f.local_right) || !std::isfinite(f.local_bottom) ||
        f.local_right <= f.local_left || f.local_bottom <= f.local_top ||
        !std::isfinite(f.source_scale_x) || !std::isfinite(f.source_scale_y) ||
        !exact_capture_scale(f.source_scale_x) || !exact_capture_scale(f.source_scale_y) ||
        !std::isfinite(f.source_offset_x) || !std::isfinite(f.source_offset_y) ||
        !std::isfinite(f.source_dpi_x) || !std::isfinite(f.source_dpi_y) ||
        f.source_dpi_x <= 0.0 || f.source_dpi_y <= 0.0 ||
        f.source_dpi_x != static_cast<float>(f.source_dpi_x) ||
        f.source_dpi_y != static_cast<float>(f.source_dpi_y)) return false;
    const float sx = std::sqrt(source_product(f.source_scale_x, f.source_scale_x));
    const float sy = std::sqrt(source_product(f.source_scale_y, f.source_scale_y));
    if (!std::isfinite(sx) || !std::isfinite(sy) || sx <= 0.0F || sy <= 0.0F ||
        sx != f.source_scale_x || sy != f.source_scale_y ||
        source_product(1.0F / sx, f.source_scale_x) != 1.0F ||
        source_product(1.0F / sy, f.source_scale_y) != 1.0F) return false;
    const double left = std::floor(source_product(f.local_left, sx));
    const double top = std::floor(source_product(f.local_top, sy));
    const double right = std::ceil(source_product(f.local_right, sx));
    const double bottom = std::ceil(source_product(f.local_bottom, sy));
    constexpr double exact_integer_limit = 1U << 24U;
    if (!std::isfinite(left) || !std::isfinite(top) || !std::isfinite(right) || !std::isfinite(bottom) ||
        left < -exact_integer_limit || top < -exact_integer_limit ||
        right > exact_integer_limit || bottom > exact_integer_limit ||
        right <= left || bottom <= top || right - left > 16'384.0 || bottom - top > 16'384.0) return false;
    const float final_x = source_sum(static_cast<float>(left), f.source_offset_x);
    const float final_y = source_sum(static_cast<float>(top), f.source_offset_y);
    if (!std::isfinite(final_x) || !std::isfinite(final_y) ||
        final_x != left + static_cast<double>(f.source_offset_x) ||
        final_y != top + static_cast<double>(f.source_offset_y) ||
        final_x != std::floor(final_x) || final_y != std::floor(final_y) ||
        std::abs(final_x) > exact_integer_limit || std::abs(final_y) > exact_integer_limit) return false;
    auto result = frame;
    result.capture_x = static_cast<std::int32_t>(left);
    result.capture_y = static_cast<std::int32_t>(top);
    result.capture_width = static_cast<std::uint32_t>(right - left);
    result.capture_height = static_cast<std::uint32_t>(bottom - top);
    result.final_x = static_cast<std::int32_t>(final_x);
    result.final_y = static_cast<std::int32_t>(final_y);
    frame = result;
    return true;
}

inline bool validate_capture_frame(const progpu_native_scene_shader_capture_frame& frame) noexcept {
    auto expected = frame;
    return complete_capture_frame(expected) && expected.capture_x == frame.capture_x &&
        expected.capture_y == frame.capture_y && expected.capture_width == frame.capture_width &&
        expected.capture_height == frame.capture_height && expected.final_x == frame.final_x &&
        expected.final_y == frame.final_y;
}

struct capture_output_coverage {
    std::int32_t left{}, top{}, right{}, bottom{};
};

// Original aliased output clipping has its own 28.4 conversion; allocation
// rounding and generic pixel-center ceil are observably different contracts.
// Independently derived from the fixed-coordinate and tie rules, not a copy of
// a rasterizer implementation. Inputs have already passed the bounded frame.
inline std::int32_t aliased_capture_edge(float edge) noexcept {
    const double fixed = std::floor(static_cast<double>(source_product(edge, 16.0F)) + 0.5);
    return static_cast<std::int32_t>(std::floor((fixed + 7.0) / 16.0));
}

inline capture_output_coverage output_coverage(const progpu_native_scene_shader_capture_frame& f) noexcept {
    const auto edge = [](float local, float scale, float offset) {
        return aliased_capture_edge(source_sum(source_product(local, scale), offset));
    };
    return {edge(f.local_left, f.source_scale_x, f.source_offset_x),
        edge(f.local_top, f.source_scale_y, f.source_offset_y),
        edge(f.local_right, f.source_scale_x, f.source_offset_x),
        edge(f.local_bottom, f.source_scale_y, f.source_offset_y)};
}
} // namespace progpu::native::shader_effect

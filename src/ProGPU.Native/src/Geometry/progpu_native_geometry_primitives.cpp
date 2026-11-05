#include "progpu_native_geometry_primitives.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64) || (defined(_M_IX86_FP) && _M_IX86_FP >= 2)
#include <emmintrin.h>
#elif defined(__wasm_simd128__)
#include <wasm_simd128.h>
#endif

// Device-independent primitives moved from the Direct2D behavior core
// (src/Direct2D/progpu_native_direct2d_core.cpp), which keeps forwarders.

namespace progpu::native::geometry {
namespace {

[[nodiscard]] bool finite_point(const point_2f& point) noexcept
{
    return std::isfinite(point.x) && std::isfinite(point.y);
}

} // namespace

bool valid_transform(
    const matrix_3x2_f* transform) noexcept
{
    return transform == nullptr ||
        (std::isfinite(transform->m11) &&
            std::isfinite(transform->m12) &&
            std::isfinite(transform->m21) &&
            std::isfinite(transform->m22) &&
            std::isfinite(transform->m31) &&
            std::isfinite(transform->m32));
}

bool valid_arc_segment(const arc_segment_f& arc) noexcept
{
    return finite_point(arc.point) && std::isfinite(arc.size.width) &&
        std::isfinite(arc.size.height) && arc.size.width >= 0.0F &&
        arc.size.height >= 0.0F && std::isfinite(arc.rotation_angle) &&
        (arc.sweep == arc_sweep_direction::counter_clockwise ||
            arc.sweep == arc_sweep_direction::clockwise) &&
        (arc.size_kind == arc_size_kind::small_value ||
            arc.size_kind == arc_size_kind::large_value);
}

status arc_to_cubics(
    point_2f start,
    const arc_segment_f& arc,
    std::array<cubic_bezier_segment_f, 4U>* cubics,
    std::uint32_t* cubic_count) noexcept
{
    if (cubics == nullptr || cubic_count == nullptr) {
        return status_code::pointer_error;
    }
    *cubics = {};
    *cubic_count = 0U;
    if (!finite_point(start) || !valid_arc_segment(arc)) {
        return status_code::invalid_argument;
    }
    if ((start.x == arc.point.x && start.y == arc.point.y) ||
        arc.size.width == 0.0F || arc.size.height == 0.0F) {
        return status_code::ok;
    }

    constexpr double pi = 3.141592653589793238462643383279502884;
    const double phi = std::remainder(
        static_cast<double>(arc.rotation_angle), 360.0) * pi / 180.0;
    const double cosine = std::cos(phi);
    const double sine = std::sin(phi);
    const double half_dx =
        (static_cast<double>(start.x) - arc.point.x) * 0.5;
    const double half_dy =
        (static_cast<double>(start.y) - arc.point.y) * 0.5;
    const double x1_prime = cosine * half_dx + sine * half_dy;
    const double y1_prime = -sine * half_dx + cosine * half_dy;
    double radius_x = std::abs(static_cast<double>(arc.size.width));
    double radius_y = std::abs(static_cast<double>(arc.size.height));
    double radius_x_squared = radius_x * radius_x;
    double radius_y_squared = radius_y * radius_y;
    const double scale =
        x1_prime * x1_prime / radius_x_squared +
        y1_prime * y1_prime / radius_y_squared;
    if (!std::isfinite(scale)) {
        return status_code::invalid_argument;
    }
    if (scale > 1.0) {
        const double factor = std::sqrt(scale);
        radius_x *= factor;
        radius_y *= factor;
        radius_x_squared = radius_x * radius_x;
        radius_y_squared = radius_y * radius_y;
    }

    const bool large = arc.size_kind == arc_size_kind::large_value;
    const bool clockwise =
        arc.sweep == arc_sweep_direction::clockwise;
    const double numerator = std::max(
        0.0,
        radius_x_squared * radius_y_squared -
            radius_x_squared * y1_prime * y1_prime -
            radius_y_squared * x1_prime * x1_prime);
    const double denominator =
        radius_x_squared * y1_prime * y1_prime +
        radius_y_squared * x1_prime * x1_prime;
    const double sign = large == clockwise ? -1.0 : 1.0;
    const double coefficient = denominator == 0.0
        ? 0.0
        : sign * std::sqrt(numerator / denominator);
    const double center_x_prime =
        coefficient * radius_x * y1_prime / radius_y;
    const double center_y_prime =
        -coefficient * radius_y * x1_prime / radius_x;
    const double center_x = cosine * center_x_prime -
        sine * center_y_prime +
        (static_cast<double>(start.x) + arc.point.x) * 0.5;
    const double center_y = sine * center_x_prime +
        cosine * center_y_prime +
        (static_cast<double>(start.y) + arc.point.y) * 0.5;

    const auto vector_angle = [](double ux, double uy, double vx, double vy) {
        return std::atan2(ux * vy - uy * vx, ux * vx + uy * vy);
    };
    const double ux = (x1_prime - center_x_prime) / radius_x;
    const double uy = (y1_prime - center_y_prime) / radius_y;
    const double vx = (-x1_prime - center_x_prime) / radius_x;
    const double vy = (-y1_prime - center_y_prime) / radius_y;
    const double start_angle = std::atan2(uy, ux);
    double delta = vector_angle(ux, uy, vx, vy);
    if (!clockwise && delta > 0.0) {
        delta -= 2.0 * pi;
    } else if (clockwise && delta < 0.0) {
        delta += 2.0 * pi;
    }
    *cubic_count = static_cast<std::uint32_t>(std::clamp(
        std::ceil(std::abs(delta) / (pi * 0.5)),
        1.0,
        4.0));
    const double step = delta / *cubic_count;
    point_2f current = start;
    constexpr double maximum =
        static_cast<double>((std::numeric_limits<float>::max)());
    for (std::uint32_t index = 0U; index < *cubic_count; ++index) {
        const double angle0 = start_angle + step * index;
        const double angle1 = angle0 + step;
        const double alpha = 4.0 / 3.0 * std::tan(step * 0.25);
        const auto evaluate = [&](double angle) {
            const double local_x = radius_x * std::cos(angle);
            const double local_y = radius_y * std::sin(angle);
            return std::array<double, 2U>{
                center_x + cosine * local_x - sine * local_y,
                center_y + sine * local_x + cosine * local_y};
        };
        const auto derivative = [&](double angle) {
            const double local_x = -radius_x * std::sin(angle);
            const double local_y = radius_y * std::cos(angle);
            return std::array<double, 2U>{
                cosine * local_x - sine * local_y,
                sine * local_x + cosine * local_y};
        };
        const auto evaluated_end = evaluate(angle1);
        const std::array<double, 2U> end = index + 1U == *cubic_count
            ? std::array<double, 2U>{arc.point.x, arc.point.y}
            : evaluated_end;
        const auto tangent0 = derivative(angle0);
        const auto tangent1 = derivative(angle1);
        const std::array<std::array<double, 2U>, 3U> values{{
            {static_cast<double>(current.x) + alpha * tangent0[0U],
                static_cast<double>(current.y) + alpha * tangent0[1U]},
            {end[0U] - alpha * tangent1[0U],
                end[1U] - alpha * tangent1[1U]},
            end}};
        if (!std::all_of(values.begin(), values.end(), [&](const auto& point) {
                return std::isfinite(point[0U]) &&
                    std::isfinite(point[1U]) &&
                    std::abs(point[0U]) <= maximum &&
                    std::abs(point[1U]) <= maximum;
            })) {
            *cubics = {};
            *cubic_count = 0U;
            return status_code::invalid_argument;
        }
        (*cubics)[index] = {
            {static_cast<float>(values[0U][0U]),
                static_cast<float>(values[0U][1U])},
            {static_cast<float>(values[1U][0U]),
                static_cast<float>(values[1U][1U])},
            {static_cast<float>(values[2U][0U]),
                static_cast<float>(values[2U][1U])}};
        current = (*cubics)[index].point3;
    }
    return status_code::ok;
}

bool valid_stroke_style(
    const stroke_style_properties_f& properties,
    const float* dashes,
    std::uint32_t dash_count) noexcept
{
    if (properties.start_cap > cap_style::triangle ||
        properties.end_cap > cap_style::triangle ||
        properties.dash_cap > cap_style::triangle ||
        properties.join > line_join::miter_or_bevel ||
        !std::isfinite(properties.miter_limit) ||
        properties.miter_limit <= 0.0F ||
        properties.dash > dash_style::custom ||
        !std::isfinite(properties.dash_offset) ||
        ((properties.dash == dash_style::custom) !=
            (dashes != nullptr && dash_count != 0U)) ||
        (properties.dash != dash_style::custom &&
            (dashes != nullptr || dash_count != 0U))) {
        return false;
    }
    bool has_positive_dash = false;
    std::uint32_t index = 0U;
    // Independent interval validation, shared by query and render style creation.
    // Four intrinsic lanes with at most three scalar tail values; no repacking.
#if defined(__aarch64__) || defined(_M_ARM64)
    for (; dash_count - index >= 4U; index += 4U) {
        const auto values = vld1q_f32(dashes + index);
        const auto valid = vandq_u32(vcgeq_f32(values, vdupq_n_f32(0.0F)),
            vcleq_f32(values, vdupq_n_f32((std::numeric_limits<float>::max)())));
        if (vminvq_u32(valid) == 0U) return false;
        has_positive_dash |= vmaxvq_u32(vcgtq_f32(values, vdupq_n_f32(0.0F))) != 0U;
    }
#elif defined(__SSE2__) || defined(_M_X64) || (defined(_M_IX86_FP) && _M_IX86_FP >= 2)
    for (; dash_count - index >= 4U; index += 4U) {
        const auto values = _mm_loadu_ps(dashes + index);
        const auto valid = _mm_and_ps(_mm_cmpge_ps(values, _mm_setzero_ps()),
            _mm_cmple_ps(values, _mm_set1_ps((std::numeric_limits<float>::max)())));
        if (_mm_movemask_ps(valid) != 15) return false;
        has_positive_dash |= _mm_movemask_ps(_mm_cmpgt_ps(values, _mm_setzero_ps())) != 0;
    }
#elif defined(__wasm_simd128__)
    for (; dash_count - index >= 4U; index += 4U) {
        const auto values = wasm_v128_load(dashes + index);
        const auto valid = wasm_v128_and(wasm_f32x4_ge(values, wasm_f32x4_splat(0.0F)),
            wasm_f32x4_le(values, wasm_f32x4_splat((std::numeric_limits<float>::max)())));
        if (!wasm_i32x4_all_true(valid)) return false;
        has_positive_dash |= wasm_v128_any_true(wasm_f32x4_gt(values, wasm_f32x4_splat(0.0F)));
    }
#endif
    for (; index < dash_count; ++index) {
        if (!std::isfinite(dashes[index]) || dashes[index] < 0.0F) {
            return false;
        }
        has_positive_dash = has_positive_dash || dashes[index] > 0.0F;
    }
    return dash_count == 0U || has_positive_dash;
}

} // namespace progpu::native::geometry

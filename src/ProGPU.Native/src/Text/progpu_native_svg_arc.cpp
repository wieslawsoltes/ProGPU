#include "progpu_native_svg_path_internal.hpp"

#include <algorithm>
#include <cmath>
#include <numbers>

// Direct native port provenance: ProGPU-owned
// ArcSegmentGeometry.TryGetArcCenter/bounds at checkpoint 8bf3cd44. This fixed
// work unit resolves SVG endpoint arcs once for the retained path ABI.
namespace progpu::native::text::svg_path_detail {
namespace {

constexpr float epsilon = 0.00001F;
constexpr float two_pi = 2.0F * std::numbers::pi_v<float>;

float normalize_radians(float angle) noexcept {
    float normalized = std::fmod(angle, two_pi);
    if (normalized < 0.0F) {
        normalized += two_pi;
    }
    return normalized;
}

} // namespace

bool angle_within_sweep(
    float theta,
    float theta1,
    float delta_theta) noexcept {
    if (!std::isfinite(theta) || !std::isfinite(theta1) ||
        !std::isfinite(delta_theta) || std::abs(delta_theta) <= epsilon) {
        return false;
    }
    if (std::abs(delta_theta) >= two_pi - epsilon) {
        return true;
    }
    const float distance = delta_theta > 0.0F
        ? normalize_radians(theta - theta1)
        : normalize_radians(theta1 - theta);
    return distance <= std::abs(delta_theta) + epsilon;
}

point evaluate_arc(
    point center,
    float radius_x,
    float radius_y,
    float rotation_degrees,
    float theta) noexcept {
    const float phi = rotation_degrees *
        std::numbers::pi_v<float> / 180.0F;
    const float cosine_phi = std::cos(phi);
    const float sine_phi = std::sin(phi);
    const float cosine_theta = std::cos(theta);
    const float sine_theta = std::sin(theta);
    return {
        radius_x * cosine_theta * cosine_phi -
            radius_y * sine_theta * sine_phi + center.x,
        radius_x * cosine_theta * sine_phi +
            radius_y * sine_theta * cosine_phi + center.y};
}

bool resolve_arc(
    point start,
    point end,
    point radii,
    float rotation_degrees,
    bool large_arc,
    bool clockwise,
    point& center,
    float& theta1,
    float& delta_theta,
    float& radius_x,
    float& radius_y) noexcept {
    center = {};
    theta1 = 0.0F;
    delta_theta = 0.0F;
    radius_x = std::abs(radii.x);
    radius_y = std::abs(radii.y);
    const point chord = start - end;
    if (!finite(start) || !finite(end) || !finite(radii) ||
        !std::isfinite(rotation_degrees) ||
        chord.x * chord.x + chord.y * chord.y <= epsilon * epsilon ||
        radius_x <= epsilon || radius_y <= epsilon) {
        return false;
    }

    const float phi = rotation_degrees *
        std::numbers::pi_v<float> / 180.0F;
    const float cosine_phi = std::cos(phi);
    const float sine_phi = std::sin(phi);
    const float dx = chord.x * 0.5F;
    const float dy = chord.y * 0.5F;
    const float x1p = cosine_phi * dx + sine_phi * dy;
    const float y1p = -sine_phi * dx + cosine_phi * dy;
    float radius_x_squared = radius_x * radius_x;
    float radius_y_squared = radius_y * radius_y;
    const float x1p_squared = x1p * x1p;
    const float y1p_squared = y1p * y1p;
    const float radii_check = x1p_squared / radius_x_squared +
        y1p_squared / radius_y_squared;
    if (!std::isfinite(radii_check)) {
        return false;
    }
    if (radii_check > 1.0F) {
        const float scale = std::sqrt(radii_check);
        radius_x *= scale;
        radius_y *= scale;
        radius_x_squared = radius_x * radius_x;
        radius_y_squared = radius_y * radius_y;
    }

    const float denominator = radius_x_squared * y1p_squared +
        radius_y_squared * x1p_squared;
    if (denominator <= 0.0F || !std::isfinite(denominator)) {
        return false;
    }
    const float sign = large_arc == clockwise ? -1.0F : 1.0F;
    float square_term =
        (radius_x_squared * radius_y_squared -
            radius_x_squared * y1p_squared -
            radius_y_squared * x1p_squared) / denominator;
    if (!std::isfinite(square_term)) {
        return false;
    }
    square_term = std::max(square_term, 0.0F);
    const float coefficient = sign * std::sqrt(square_term);
    const float cxp = coefficient * (radius_x * y1p / radius_y);
    const float cyp = coefficient * -(radius_y * x1p / radius_x);
    center = {
        cosine_phi * cxp - sine_phi * cyp +
            (start.x + end.x) * 0.5F,
        sine_phi * cxp + cosine_phi * cyp +
            (start.y + end.y) * 0.5F};

    const float ux = (x1p - cxp) / radius_x;
    const float uy = (y1p - cyp) / radius_y;
    const float vx = (-x1p - cxp) / radius_x;
    const float vy = (-y1p - cyp) / radius_y;
    theta1 = std::atan2(uy, ux);
    const float theta2 = std::atan2(vy, vx);
    delta_theta = theta2 - theta1;
    if (clockwise) {
        if (delta_theta < 0.0F) {
            delta_theta += two_pi;
        }
    } else if (delta_theta > 0.0F) {
        delta_theta -= two_pi;
    }
    return finite(center) && std::isfinite(theta1) &&
        std::isfinite(delta_theta) && std::isfinite(radius_x) &&
        std::isfinite(radius_y);
}

} // namespace progpu::native::text::svg_path_detail

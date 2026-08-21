#pragma once

#include <cmath>

namespace progpu::native::text::svg_path_detail {

struct point final {
    float x = 0.0F;
    float y = 0.0F;
};

inline point operator+(point left, point right) noexcept {
    return {left.x + right.x, left.y + right.y};
}

inline point operator-(point left, point right) noexcept {
    return {left.x - right.x, left.y - right.y};
}

inline point operator*(point value, float scale) noexcept {
    return {value.x * scale, value.y * scale};
}

inline bool equal(point left, point right) noexcept {
    return left.x == right.x && left.y == right.y;
}

inline bool finite(point value) noexcept {
    return std::isfinite(value.x) && std::isfinite(value.y);
}

bool angle_within_sweep(
    float theta,
    float theta1,
    float delta_theta) noexcept;

point evaluate_arc(
    point center,
    float radius_x,
    float radius_y,
    float rotation_degrees,
    float theta) noexcept;

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
    float& radius_y) noexcept;

} // namespace progpu::native::text::svg_path_detail

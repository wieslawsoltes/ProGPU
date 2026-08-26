#pragma once

#include <algorithm>
#include <array>
#include <cmath>
#include <numbers>

namespace progpu::native::geometry {

// Shared native port of ProGPU ArcSegmentGeometry.TryGetArcCenter/bounds at
// checkpoint 8bf3cd44. SVG glyphs and retained MIL paths intentionally consume
// the same endpoint-arc resolution and sweep rules.
struct arc_point final {
    float x = 0.0F;
    float y = 0.0F;
};

struct wpf_cubic_arc_piece final {
    arc_point control1{};
    arc_point control2{};
    arc_point end{};
};

inline arc_point operator+(arc_point left, arc_point right) noexcept {
    return {left.x + right.x, left.y + right.y};
}

inline arc_point operator-(arc_point left, arc_point right) noexcept {
    return {left.x - right.x, left.y - right.y};
}

inline arc_point operator*(arc_point value, float scale) noexcept {
    return {value.x * scale, value.y * scale};
}

inline bool equal(arc_point left, arc_point right) noexcept {
    return left.x == right.x && left.y == right.y;
}

inline bool finite(arc_point value) noexcept {
    return std::isfinite(value.x) && std::isfinite(value.y);
}

inline float normalize_radians(float angle) noexcept {
    constexpr float two_pi = 2.0F * std::numbers::pi_v<float>;
    float normalized = std::fmod(angle, two_pi);
    if (normalized < 0.0F) {
        normalized += two_pi;
    }
    return normalized;
}

inline bool angle_within_sweep(
    float theta,
    float theta1,
    float delta_theta) noexcept {
    constexpr float epsilon = 0.00001F;
    constexpr float two_pi = 2.0F * std::numbers::pi_v<float>;
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

inline arc_point evaluate_arc(
    arc_point center,
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

inline bool resolve_arc(
    arc_point start,
    arc_point end,
    arc_point radii,
    float rotation_degrees,
    bool large_arc,
    bool clockwise,
    arc_point& center,
    float& theta1,
    float& delta_theta,
    float& radius_x,
    float& radius_y) noexcept {
    constexpr float epsilon = 0.00001F;
    constexpr float two_pi = 2.0F * std::numbers::pi_v<float>;
    center = {};
    theta1 = 0.0F;
    delta_theta = 0.0F;
    radius_x = std::abs(radii.x);
    radius_y = std::abs(radii.y);
    const arc_point chord = start - end;
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

// Reflection-free native port of WPF's ArcToBezier helper. WPF converts each
// endpoint ArcSegment into one through four equal-angle cubic Beziers before
// CSnappingTask visits the start, control, and end points. Returning -1 means
// a coincident endpoint (ignored by CFigureData), 0 means a line, and 1..4 is
// the number of cubic pieces. Inputs intentionally use the float values read
// by PathGeometryWrapper so its FUZZ, angle partition, and control-distance
// decisions remain reproducible.
inline bool lower_wpf_arc_to_cubics(
    arc_point start,
    arc_point end,
    arc_point radii,
    float rotation_degrees,
    bool large_arc,
    bool sweep_up,
    std::array<wpf_cubic_arc_piece, 4U>& pieces,
    int& piece_count) noexcept {
    constexpr float fuzz = 1.0e-6F;
    constexpr float fuzz_squared = 1.0e-12F;
    constexpr float pi_over_180 =
        static_cast<float>(0.0174532925199432957692);
    constexpr float two_pi =
        static_cast<float>(6.2831853071795865);
    constexpr double four_thirds = 1.33333333333333333;
    piece_count = -1;
    pieces = {};
    if (!finite(start) || !finite(end) || !finite(radii) ||
        !std::isfinite(rotation_degrees)) {
        return false;
    }

    float x = 0.5F * (end.x - start.x);
    float y = 0.5F * (end.y - start.y);
    float half_chord_squared = x * x + y * y;
    if (half_chord_squared < fuzz_squared) {
        return true;
    }
    const auto accept_radius = [half_chord_squared](float& radius) noexcept {
        const bool accepted = !(radius * radius <=
            half_chord_squared * fuzz_squared);
        if (accepted && radius < 0.0F) {
            radius = -radius;
        }
        return accepted;
    };
    float radius_x = radii.x;
    float radius_y = radii.y;
    if (!accept_radius(radius_x) || !accept_radius(radius_y)) {
        piece_count = 0;
        return true;
    }

    float cosine_rotation = 1.0F;
    float sine_rotation = 0.0F;
    if (std::abs(rotation_degrees) >= fuzz) {
        const float inverse_rotation = -rotation_degrees * pi_over_180;
        cosine_rotation = std::cos(inverse_rotation);
        sine_rotation = std::sin(inverse_rotation);
        const float rotated_x = x * cosine_rotation - y * sine_rotation;
        y = x * sine_rotation + y * cosine_rotation;
        x = rotated_x;
    }

    x /= radius_x;
    y /= radius_y;
    half_chord_squared = x * x + y * y;
    float center_x = 0.0F;
    float center_y = 0.0F;
    bool zero_center = false;
    if (half_chord_squared > 1.0F) {
        const float scale = std::sqrt(half_chord_squared);
        radius_x *= scale;
        radius_y *= scale;
        zero_center = true;
        x /= scale;
        y /= scale;
    } else {
        if (!(half_chord_squared > 0.0F)) {
            return false;
        }
        const float scale = std::sqrt(
            (1.0F - half_chord_squared) / half_chord_squared);
        if (large_arc != sweep_up) {
            center_x = -scale * y;
            center_y = scale * x;
        } else {
            center_x = scale * y;
            center_y = -scale * x;
        }
    }

    arc_point unit_start{-x - center_x, -y - center_y};
    const arc_point unit_end{x - center_x, y - center_y};
    const float matrix00 = cosine_rotation * radius_x;
    const float matrix01 = -sine_rotation * radius_x;
    const float matrix10 = sine_rotation * radius_y;
    const float matrix11 = cosine_rotation * radius_y;
    float matrix20 = 0.5F * (end.x + start.x);
    float matrix21 = 0.5F * (end.y + start.y);
    if (!zero_center) {
        matrix20 += matrix00 * center_x + matrix10 * center_y;
        matrix21 += matrix01 * center_x + matrix11 * center_y;
    }
    const auto map_to_ellipse = [=](arc_point point) noexcept {
        return arc_point{
            matrix00 * point.x + matrix10 * point.y + matrix20,
            matrix01 * point.x + matrix11 * point.y + matrix21};
    };

    float cosine_piece =
        unit_start.x * unit_end.x + unit_start.y * unit_end.y;
    float sine_piece =
        unit_start.x * unit_end.y - unit_start.y * unit_end.x;
    if (cosine_piece >= 0.0F) {
        piece_count = large_arc ? 4 : 1;
    } else {
        piece_count = large_arc ? 3 : 2;
    }
    if (piece_count != 1) {
        float angle = std::atan2(sine_piece, cosine_piece);
        if (sweep_up) {
            if (angle < 0.0F) {
                angle += two_pi;
            }
        } else if (angle > 0.0F) {
            angle -= two_pi;
        }
        angle /= static_cast<float>(piece_count);
        cosine_piece = std::cos(angle);
        sine_piece = std::sin(angle);
    }

    const double a = 0.5 * (1.0 + static_cast<double>(cosine_piece));
    const double denominator_squared = 1.0 - a;
    double bezier_distance = 0.0;
    if (a >= 0.0 && denominator_squared > 0.0) {
        const double denominator = std::sqrt(denominator_squared);
        const double numerator = four_thirds * (1.0 - std::sqrt(a));
        if (numerator > denominator * static_cast<double>(fuzz)) {
            bezier_distance = numerator / denominator;
        }
    }
    float signed_distance = static_cast<float>(bezier_distance);
    if (!sweep_up) {
        signed_distance = -signed_distance;
    }
    arc_point first_tangent{
        -signed_distance * unit_start.y,
        signed_distance * unit_start.x};

    for (int index = 0; index < piece_count; ++index) {
        const bool last = index + 1 == piece_count;
        const arc_point piece_end = last
            ? unit_end
            : arc_point{
                unit_start.x * cosine_piece -
                    unit_start.y * sine_piece,
                unit_start.x * sine_piece +
                    unit_start.y * cosine_piece};
        const arc_point second_tangent{
            -signed_distance * piece_end.y,
            signed_distance * piece_end.x};
        auto& piece = pieces[static_cast<std::size_t>(index)];
        piece.control1 = map_to_ellipse(unit_start + first_tangent);
        piece.control2 = map_to_ellipse(piece_end - second_tangent);
        piece.end = last ? end : map_to_ellipse(piece_end);
        if (!finite(piece.control1) || !finite(piece.control2) ||
            !finite(piece.end)) {
            piece_count = -1;
            pieces = {};
            return false;
        }
        unit_start = piece_end;
        first_tangent = second_tangent;
    }
    return true;
}

} // namespace progpu::native::geometry

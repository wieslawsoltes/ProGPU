#include "progpu_native.h"
#include "../src/Direct2D/progpu_native_direct2d_path.hpp"

#include <array>
#include <bit>
#include <cmath>
#include <cstdio>
#include <limits>
#include <numbers>
#include <span>
#include <vector>

namespace {
using point = progpu_native_point;
using segment = progpu_native_path_segment;

std::vector<segment> polygon(std::initializer_list<point> points)
{
    std::vector<segment> result;
    const std::span<const point> view{points.begin(), points.size()};
    for (std::size_t index = 0; index < view.size(); ++index) {
        segment edge{};
        edge.p0 = view[index];
        edge.p1 = view[(index + 1U) % view.size()];
        result.push_back(edge);
    }
    return result;
}

struct outline final {
    progpu_native_geometry_outline* value = nullptr;
    const point* points = nullptr;
    const std::uint32_t* offsets = nullptr;
    std::uint32_t point_count = 0, contour_count = 0;
    ~outline() { progpu_native_geometry_outline_destroy(value); }
    progpu_native_status combine(std::span<const segment> a, std::span<const segment> b,
        std::uint32_t mode, std::uint32_t first_fill = PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        float tolerance = 0.01F) {
        progpu_native_geometry_outline_destroy(value);
        value = nullptr;
        return progpu_native_geometry_combine(a.data(), static_cast<std::uint32_t>(a.size()), first_fill,
            b.data(), static_cast<std::uint32_t>(b.size()), PROGPU_NATIVE_FILL_RULE_NON_ZERO,
            mode, tolerance, &value, &points, &point_count, &offsets, &contour_count);
    }
    // Independent scalar test oracle for even-odd point membership, not product code.
    bool contains(double x, double y) const {
        bool inside = false;
        for (std::uint32_t contour = 0; contour < contour_count; ++contour) {
            const auto begin = offsets[contour], end = offsets[contour + 1U];
            for (auto i = begin, j = end - 1U; i < end; j = i++) {
                const point a = points[i], b = points[j];
                if ((a.y > y) != (b.y > y) &&
                    x < (double{b.x} - a.x) * (y - a.y) / (double{b.y} - a.y) + a.x) inside = !inside;
            }
        }
        return inside;
    }
};

bool modes_and_actual_boundaries()
{
    const auto a = polygon({{0, 0}, {10, 0}, {10, 10}, {0, 10}});
    const auto b = polygon({{5, 0}, {15, 0}, {15, 10}, {5, 10}});
    constexpr bool expected[4][3]{{true, true, true}, {false, true, false},
        {true, false, true}, {true, false, false}};
    outline output;
    for (std::uint32_t mode = 0; mode < 4; ++mode) {
        if (output.combine(a, b, mode) != PROGPU_NATIVE_STATUS_SUCCESS || output.value == nullptr) return false;
        for (std::uint32_t probe = 0; probe < 3; ++probe)
            if (output.contains(2.0 + 5.0 * probe, 5.0) != expected[mode][probe]) return false;
        if (output.contains(20, 5)) return false;
    }
    const auto triangle = polygon({{0, 0}, {10, 0}, {0, 10}});
    if (output.combine(triangle, a, 1) != PROGPU_NATIVE_STATUS_SUCCESS ||
        !output.contains(2, 2) || output.contains(8, 8)) return false;
    const auto inner = polygon({{2, 2}, {8, 2}, {8, 8}, {2, 8}});
    if (output.combine(a, inner, 3) != PROGPU_NATIVE_STATUS_SUCCESS ||
        output.contour_count != 2 || !output.contains(1, 1) || output.contains(5, 5)) return false;

    auto doubled = a;
    doubled.insert(doubled.end(), a.begin(), a.end());
    if (output.combine(doubled, {}, 0, PROGPU_NATIVE_FILL_RULE_EVEN_ODD) != PROGPU_NATIVE_STATUS_SUCCESS ||
        output.contour_count != 0 || output.point_count != 0 || output.offsets[0] != 0) return false;
    return output.combine(doubled, {}, 0) == PROGPU_NATIVE_STATUS_SUCCESS && output.contains(5, 5);
}

bool curved_result_matches_shared_core()
{
    namespace d2d = progpu::native::direct2d::compat;
    namespace com = progpu::native::com;
    segment arc{};
    arc.kind = PROGPU_NATIVE_PATH_SEGMENT_ARC;
    arc.p0 = arc.p1 = {10, 0};
    arc.p2 = {0, 0};
    arc.p3 = {10, 10};
    arc.pad1 = std::bit_cast<std::uint32_t>(2.0F * std::numbers::pi_v<float>);
    const std::array first{arc};
    const point center{}, outside{12, 0};
    std::uint32_t contains = 0;
    if (progpu_native_geometry_fill_contains(first.data(), 1, 0, &center, 0.01F, &contains) !=
        PROGPU_NATIVE_STATUS_SUCCESS || contains != 1) return false;
    if (progpu_native_geometry_fill_contains(first.data(), 1, 0, &outside, 0.01F, &contains) !=
        PROGPU_NATIVE_STATUS_SUCCESS || contains != 0) return false;
    const auto second = polygon({{0, -20}, {20, -20}, {20, 20}, {0, 20}});
    std::vector<std::vector<d2d::point_2f>> expected;
    if (com::failed(d2d::detail::combine_native_fill_contours(first, d2d::fill_mode::winding,
        second, d2d::fill_mode::winding, d2d::combine_mode::intersect, 0.01F, expected))) return false;
    outline output;
    if (output.combine(first, second, 1) != PROGPU_NATIVE_STATUS_SUCCESS ||
        output.contour_count != expected.size() || !output.contains(5, 0) || output.contains(-5, 0)) return false;
    for (std::size_t contour = 0; contour < expected.size(); ++contour) {
        if (output.offsets[contour + 1U] - output.offsets[contour] != expected[contour].size()) return false;
        for (std::size_t index = 0; index < expected[contour].size(); ++index) {
            const point actual = output.points[output.offsets[contour] + index];
            if (actual.x != expected[contour][index].x || actual.y != expected[contour][index].y) return false;
        }
    }
    return true;
}

bool failures_and_empty_ownership()
{
    outline output;
    if (output.combine({}, {}, 0) != PROGPU_NATIVE_STATUS_SUCCESS || output.value == nullptr ||
        output.point_count != 0 || output.contour_count != 0 || output.offsets[0] != 0) return false;
    if (output.combine({}, {}, 4) != PROGPU_NATIVE_STATUS_INVALID_ARGUMENT || output.value != nullptr ||
        output.points != nullptr || output.offsets != nullptr || output.point_count != 0 || output.contour_count != 0) return false;
    if (output.combine({}, {}, 0, 2) != PROGPU_NATIVE_STATUS_INVALID_ARGUMENT) return false;
    if (output.combine({}, {}, 0, 0, std::numeric_limits<float>::quiet_NaN()) != PROGPU_NATIVE_STATUS_INVALID_ARGUMENT) return false;
    auto bad = polygon({{0, 0}, {1, 0}, {0, 1}});
    bad[0].kind = 42;
    if (output.combine(bad, {}, 0) != PROGPU_NATIVE_STATUS_INVALID_ARGUMENT) return false;
    return progpu_native_geometry_combine(nullptr, 1, 0, nullptr, 0, 0, 0, 0.1F,
        &output.value, &output.points, &output.point_count, &output.offsets, &output.contour_count) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
}

// Independent scalar oracle, including projected-edge tolerance and half-open
// crossings. Exercises the intrinsic product path without using its helpers.
bool scalar_fill(std::span<const segment> edges, point p, bool even_odd, double tolerance)
{
    int winding = 0;
    for (const auto& edge : edges) {
        const double x = double{edge.p1.x} - edge.p0.x, y = double{edge.p1.y} - edge.p0.y;
        const double u = double{p.x} - edge.p0.x, v = double{p.y} - edge.p0.y;
        const double length = x * x + y * y, projection = u * x + v * y;
        const double cross = x * v - y * u;
        const double distance = length == 0 ? u * u + v * v : cross * cross / length;
        if (projection >= 0 && projection <= length && distance <= tolerance * tolerance) return true;
        if (edge.p0.y <= p.y && edge.p1.y > p.y && cross > 0) ++winding;
        if (edge.p0.y > p.y && edge.p1.y <= p.y && cross < 0) --winding;
    }
    return even_odd ? winding % 2 != 0 : winding != 0;
}

bool fill_queries_match_scalar_and_reject_bad_inputs()
{
    auto path = polygon({{0, 0}, {16, 0}, {16, 12}, {8, 5}, {0, 12}});
    const auto inner = polygon({{3, 2}, {6, 2}, {6, 4}, {3, 4}});
    path.insert(path.end(), inner.begin(), inner.end());
    constexpr float tolerance = 0.03125F;
    std::uint32_t contains = 99;
    for (std::uint32_t fill = 0; fill < 2; ++fill) {
        for (int y = -4; y <= 28; ++y) for (int x = -4; x <= 36; ++x) {
            const point probe{static_cast<float>(x) * 0.5F, static_cast<float>(y) * 0.5F};
            if (progpu_native_geometry_fill_contains(path.data(), static_cast<std::uint32_t>(path.size()),
                fill, &probe, tolerance, &contains) != PROGPU_NATIVE_STATUS_SUCCESS ||
                (contains != 0) != scalar_fill(path, probe, fill == 1, tolerance)) return false;
        }
    }
    const point origin{};
    if (progpu_native_geometry_fill_contains(nullptr, 0, 0, &origin, tolerance, &contains) !=
        PROGPU_NATIVE_STATUS_SUCCESS || contains != 0) return false;
    if (progpu_native_geometry_fill_contains(nullptr, 1, 0, &origin, tolerance, &contains) !=
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT || contains != 0) return false;
    if (progpu_native_geometry_fill_contains(nullptr, 0, 2, &origin, tolerance, &contains) !=
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT || contains != 0) return false;
    if (progpu_native_geometry_fill_contains(nullptr, 0, 0, &origin, 0, &contains) !=
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT || contains != 0) return false;
    const point bad{std::numeric_limits<float>::quiet_NaN(), 0};
    return progpu_native_geometry_fill_contains(nullptr, 0, 0, &bad, tolerance, &contains) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && contains == 0;
}
} // namespace

int main()
{
    if (!modes_and_actual_boundaries() || !curved_result_matches_shared_core() || !failures_and_empty_ownership() ||
        !fill_queries_match_scalar_and_reject_bad_inputs()) {
        std::fputs("Native geometry utility conformance failed.\n", stderr);
        return 1;
    }
    return 0;
}

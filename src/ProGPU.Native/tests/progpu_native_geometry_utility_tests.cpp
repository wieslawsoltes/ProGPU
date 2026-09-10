#include "progpu_native.h"
#include "../src/Direct2D/progpu_native_direct2d_path.hpp"

#include <array>
#include <algorithm>
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

segment line_segment(point start, point end)
{
    segment value{};
    value.p0 = start; value.p1 = end;
    return value;
}

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

bool filled_relations_preserve_topology_and_shared_com_results()
{
    const auto outer = polygon({{0, 0}, {10, 0}, {10, 10}, {0, 10}});
    const auto inner = polygon({{2, 2}, {8, 2}, {8, 8}, {2, 8}});
    const auto overlap = polygon({{5, 5}, {15, 5}, {15, 15}, {5, 15}});
    const auto touching = polygon({{10, 0}, {20, 0}, {20, 10}, {10, 10}});
    const auto distant = polygon({{20, 20}, {30, 20}, {30, 30}, {20, 30}});
    const auto triangle = polygon({{0, 0}, {10, 0}, {0, 10}});
    const auto opposite_corner = polygon({{8, 8}, {9, 8}, {9, 9}, {8, 9}});
    const auto check = [](std::span<const segment> a, std::span<const segment> b,
        std::uint32_t expected, std::uint32_t fill = PROGPU_NATIVE_FILL_RULE_NON_ZERO) {
        std::uint32_t relation = 99;
        return progpu_native_geometry_compare_fill(a.data(), static_cast<std::uint32_t>(a.size()), fill,
            b.data(), static_cast<std::uint32_t>(b.size()), PROGPU_NATIVE_FILL_RULE_NON_ZERO,
            0.01F, &relation) == PROGPU_NATIVE_STATUS_SUCCESS && relation == expected;
    };
    if (!check(outer, inner, PROGPU_NATIVE_GEOMETRY_RELATION_CONTAINS) ||
        !check(inner, outer, PROGPU_NATIVE_GEOMETRY_RELATION_IS_CONTAINED) ||
        !check(outer, outer, PROGPU_NATIVE_GEOMETRY_RELATION_IS_CONTAINED) ||
        !check(outer, overlap, PROGPU_NATIVE_GEOMETRY_RELATION_OVERLAP) ||
        !check(outer, touching, PROGPU_NATIVE_GEOMETRY_RELATION_OVERLAP) ||
        !check(outer, distant, PROGPU_NATIVE_GEOMETRY_RELATION_DISJOINT) ||
        !check(triangle, opposite_corner, PROGPU_NATIVE_GEOMETRY_RELATION_DISJOINT) ||
        !check({}, outer, PROGPU_NATIVE_GEOMETRY_RELATION_DISJOINT) ||
        !check(outer, {}, PROGPU_NATIVE_GEOMETRY_RELATION_DISJOINT) ||
        !check({}, {}, PROGPU_NATIVE_GEOMETRY_RELATION_DISJOINT)) return false;
    auto ring = outer;
    ring.insert(ring.end(), inner.begin(), inner.end());
    if (!check(ring, opposite_corner, PROGPU_NATIVE_GEOMETRY_RELATION_CONTAINS,
            PROGPU_NATIVE_FILL_RULE_EVEN_ODD)) return false;
    const auto center = polygon({{3, 3}, {7, 3}, {7, 7}, {3, 7}});
    if (!check(ring, center, PROGPU_NATIVE_GEOMETRY_RELATION_DISJOINT,
            PROGPU_NATIVE_FILL_RULE_EVEN_ODD)) return false;
    auto doubled = outer;
    doubled.insert(doubled.end(), outer.begin(), outer.end());
    if (!check(doubled, inner, PROGPU_NATIVE_GEOMETRY_RELATION_DISJOINT, PROGPU_NATIVE_FILL_RULE_EVEN_ODD) ||
        !check(doubled, inner, PROGPU_NATIVE_GEOMETRY_RELATION_CONTAINS)) return false;

    // Public original-ProGPU COM entry and the new C entry share the same
    // algorithm. Exercise an analytic cubic rather than only polygon transport.
    namespace d2d = progpu::native::direct2d::compat;
    namespace com = progpu::native::com;
    segment curve{};
    curve.kind = 2; curve.p0 = {0, 0}; curve.p1 = {0, 10}; curve.p2 = {10, 10}; curve.p3 = {10, 0};
    const std::array<segment, 2> curved{curve, line_segment({10, 0}, {0, 0})};
    com::pointer<d2d::factory> factory;
    com::pointer<d2d::path_geometry> a, b;
    if (com::failed(d2d::create_factory(factory.put())) ||
        com::failed(d2d::detail::create_native_fill_geometry(factory.get(), curved, d2d::fill_mode::winding, a.put())) ||
        com::failed(d2d::detail::create_native_fill_geometry(factory.get(), inner, d2d::fill_mode::winding, b.put()))) return false;
    d2d::geometry_relation expected{};
    if (com::failed(a->CompareWithGeometry(b.get(), nullptr, 0.01F, &expected)) ||
        !check(curved, inner, static_cast<std::uint32_t>(expected))) return false;
    std::uint32_t relation = 99;
    if (progpu_native_geometry_compare_fill(nullptr, 1, 0, nullptr, 0, 0, 0.01F, &relation) !=
            PROGPU_NATIVE_STATUS_INVALID_ARGUMENT || relation != PROGPU_NATIVE_GEOMETRY_RELATION_UNKNOWN) return false;
    relation = 99;
    return progpu_native_geometry_compare_fill(nullptr, 0, 2, nullptr, 0, 0, 0.01F, &relation) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && relation == PROGPU_NATIVE_GEOMETRY_RELATION_UNKNOWN &&
        progpu_native_geometry_compare_fill(nullptr, 0, 0, nullptr, 0, 0, 0.01F, nullptr) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
}

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
        PROGPU_NATIVE_STATUS_SUCCESS || contains != 1) { std::fprintf(stderr, "arc center contains=%u\n", contains); return false; }
    if (progpu_native_geometry_fill_contains(first.data(), 1, 0, &outside, 0.01F, &contains) !=
        PROGPU_NATIVE_STATUS_SUCCESS || contains != 0) return false;
    const auto second = polygon({{0, -20}, {20, -20}, {20, 20}, {0, 20}});
    std::vector<std::vector<d2d::point_2f>> expected;
    if (com::failed(d2d::detail::combine_native_fill_contours(first, d2d::fill_mode::winding,
        second, d2d::fill_mode::winding, d2d::combine_mode::intersect, 0.01F, expected))) { std::fprintf(stderr, "arc shared combine failed\n"); return false; }
    outline output;
    if (output.combine(first, second, 1) != PROGPU_NATIVE_STATUS_SUCCESS ||
        output.contour_count != expected.size() || !output.contains(5, 0) || output.contains(-5, 0)) {
        std::fprintf(stderr, "arc intersection contours=%u expected=%zu right=%d left=%d\n",
            output.contour_count, expected.size(), output.contains(5, 0), output.contains(-5, 0)); return false; }
    for (std::size_t contour = 0; contour < expected.size(); ++contour) {
        if (output.offsets[contour + 1U] - output.offsets[contour] != expected[contour].size()) {
            std::fprintf(stderr, "arc point count actual=%u expected=%zu\n", output.offsets[contour + 1U] - output.offsets[contour], expected[contour].size()); return false; }
        for (std::size_t index = 0; index < expected[contour].size(); ++index) {
            const point actual = output.points[output.offsets[contour] + index];
            if (actual.x != expected[contour][index].x || actual.y != expected[contour][index].y) {
                std::fprintf(stderr, "arc point[%zu] actual=(%g,%g) expected=(%g,%g)\n", index, actual.x, actual.y, expected[contour][index].x, expected[contour][index].y); return false; }
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

bool stroke_queries_preserve_caps_gaps_dashes_and_world_order()
{
    const std::array edges{line_segment({0, 0}, {2, 0}), line_segment({2, 0}, {8, 0}), line_segment({8, 0}, {10, 0})};
    const std::array<progpu_native_geometry_query_figure, 1> figures{{{{0, 0}, 0, 3, 0}}};
    const std::array<std::uint8_t, 3> flags{1, 0, 1};
    progpu_native_geometry_query_pen pen{2, 10, 0, 0, 0, 0, 0};
    progpu_native_image_rect bounds{};
    std::uint32_t has = 0, contains = 0;
    const auto query = [&](const point* p, const progpu_native_affine_2d* world = nullptr,
        const float* dashes = nullptr, std::uint32_t dash_count = 0U) {
        return progpu_native_geometry_stroke_query(figures.data(), 1, edges.data(), flags.data(), 3,
            &pen, dashes, dash_count, world, p, 0.01F, &bounds, &has, &contains);
    };
    if (query(nullptr) != PROGPU_NATIVE_STATUS_SUCCESS || has != 1 ||
        bounds.x != 0 || bounds.y != -1 || bounds.width != 10 || bounds.height != 2) return false;
    const point gap{5, 0}, first{1, 0}, last{9, 0};
    if (query(&gap) != PROGPU_NATIVE_STATUS_SUCCESS || contains != 0) return false;
    if (query(&first) != PROGPU_NATIVE_STATUS_SUCCESS || contains != 1) return false;
    if (query(&last) != PROGPU_NATIVE_STATUS_SUCCESS || contains != 1) return false;
    pen.start_cap = pen.end_cap = 1; // square, applied before world scaling
    const progpu_native_affine_2d world{2, 0, 0, 3, 7, 8};
    if (query(nullptr, &world) != PROGPU_NATIVE_STATUS_SUCCESS || has != 1 ||
        bounds.x != 5 || bounds.y != 5 || bounds.width != 24 || bounds.height != 6) return false;

    const std::array line{line_segment({0, 0}, {10, 0})};
    const std::array<progpu_native_geometry_query_figure, 1> one{{{{0, 0}, 0, 1, 0}}};
    const std::array<std::uint8_t, 1> stroked{1};
    const std::array<float, 2> dashes{1, 1};
    pen.start_cap = pen.end_cap = 0;
    const point dash_on{1, 0}, dash_off{3, 0};
    for (const auto p : {dash_on, dash_off}) {
        if (progpu_native_geometry_stroke_query(one.data(), 1, line.data(), stroked.data(), 1,
            &pen, dashes.data(), 2, nullptr, &p, 0.01F, &bounds, &has, &contains) != PROGPU_NATIVE_STATUS_SUCCESS ||
            contains != (p.x == 1 ? 1U : 0U)) return false;
    }
    return true;
}

bool stroke_queries_reject_incomplete_transport()
{
    const std::array line{line_segment({0, 0}, {10, 0})};
    std::array<progpu_native_geometry_query_figure, 1> figures{{{{0, 0}, 0, 1, 1}}};
    std::array<std::uint8_t, 1> flags{1};
    const progpu_native_geometry_query_pen pen{2, 10, 0, 0, 0, 0, 0};
    progpu_native_image_rect bounds{1, 2, 3, 4};
    std::uint32_t has = 99, contains = 99;
    const auto query = [&](std::span<const segment> segments) {
        return progpu_native_geometry_stroke_query(figures.data(), 1, segments.data(), flags.data(),
            static_cast<std::uint32_t>(segments.size()), &pen, nullptr, 0, nullptr, nullptr, 0.01F,
            &bounds, &has, &contains);
    };
    if (query(line) != PROGPU_NATIVE_STATUS_INVALID_ARGUMENT || has != 0 || contains != 0 || bounds.width != 0) return false;
    figures[0].flags = 0;
    flags[0] = 4;
    if (query(line) != PROGPU_NATIVE_STATUS_INVALID_ARGUMENT) return false;
    flags[0] = 1;
    figures[0].first_segment = 1;
    if (query(line) != PROGPU_NATIVE_STATUS_INVALID_ARGUMENT) return false;
    figures[0].first_segment = 0;
    const std::array<segment, 1> constant{};
    return query(constant) == PROGPU_NATIVE_STATUS_SUCCESS && has == 0 && contains == 0;
}

bool point_strokes_match_independent_cap_oracle()
{
    // Explicit original ProGPU point policy; independent scalar shape oracle.
    const auto cap_hit = [](std::uint32_t cap, float x, float y) {
        if (cap == 1U) return std::abs(x) <= 1 && std::abs(y) <= 1;
        if (cap == 2U) return x * x + y * y <= 1;
        if (cap == 3U) return std::abs(x) + std::abs(y) <= 1;
        return false;
    };
    const point anchor{8, 4};
    const progpu_native_affine_2d world{0, 2, -3, 0, 7, -5};
    for (std::uint32_t kind = 0; kind < 3; ++kind) {
        segment edge{};
        edge.kind = kind;
        edge.p0 = edge.p1 = edge.p2 = edge.p3 = anchor;
        progpu_native_geometry_query_figure figure{anchor, 0, 1, 0};
        const std::uint8_t flag = 1;
        for (std::uint32_t start = 0; start < 4; ++start) {
            for (std::uint32_t end = 0; end < 4; ++end) {
                progpu_native_geometry_query_pen pen{2, 10, 0, start, end, 0, 0};
                progpu_native_image_rect bounds{};
                std::uint32_t has = 0, hit = 0;
                if (progpu_native_geometry_stroke_query(&figure, 1, &edge, &flag, 1, &pen,
                        nullptr, 0, nullptr, nullptr, 0.01F, &bounds, &has, &hit) != PROGPU_NATIVE_STATUS_SUCCESS)
                    return false;
                const bool ink = start != 0 || end != 0;
                if (has != (ink ? 1U : 0U)) return false;
                if (ink && (bounds.x != anchor.x - (start != 0 ? 1 : 0) || bounds.y != anchor.y - 1 ||
                    bounds.width != (start != 0 ? 1 : 0) + (end != 0 ? 1 : 0) || bounds.height != 2)) return false;
                for (int y = -5; y <= 5; ++y) for (int x = -5; x <= 5; ++x) {
                    const float dx = x * 0.25F, dy = y * 0.25F;
                    const bool expected = (dx <= 0 && cap_hit(start, dx, dy)) || (dx >= 0 && cap_hit(end, dx, dy));
                    const point probe{7 - 3 * (anchor.y + dy), -5 + 2 * (anchor.x + dx)};
                    if (progpu_native_geometry_stroke_query(&figure, 1, &edge, &flag, 1, &pen,
                            nullptr, 0, &world, &probe, 0.01F, &bounds, &has, &hit) != PROGPU_NATIVE_STATUS_SUCCESS ||
                        hit != (expected ? 1U : 0U)) return false;
                }
            }
        }
    }
    // A closed collapsed contour retains the existing MIL round-pair policy.
    auto edge = line_segment(anchor, anchor);
    progpu_native_geometry_query_figure figure{anchor, 0, 1, 1};
    const std::uint8_t flag = 1;
    progpu_native_geometry_query_pen pen{2, 10, 0, 0, 0, 0, 0};
    progpu_native_image_rect bounds{};
    std::uint32_t has = 0, hit = 0;
    if (progpu_native_geometry_stroke_query(&figure, 1, &edge, &flag, 1, &pen,
        nullptr, 0, &world, nullptr, 0.01F, &bounds, &has, &hit) != PROGPU_NATIVE_STATUS_SUCCESS ||
        has != 1 || bounds.x != -8 || bounds.y != 9 || bounds.width != 6 || bounds.height != 4) return false;
    // Include the public portable COM queries, not just the C ABI Widen route.
    namespace d2d = progpu::native::direct2d::compat;
    namespace com = progpu::native::com;
    com::pointer<d2d::factory> owner;
    com::pointer<d2d::path_geometry> path;
    if (com::failed(d2d::create_factory(owner.put())) ||
        com::failed(d2d::detail::create_native_query_geometry(owner.get(), std::span(&figure, 1U),
            std::span(&edge, 1U), std::span(&flag, 1U), path.put()))) return false;
    const d2d::matrix_3x2_f matrix{0, 2, -3, 0, 7, -5};
    d2d::rectangle_f measured{};
    std::int32_t direct_hit = 0;
    if (com::failed(path->GetWidenedBounds(2, nullptr, &matrix, 0.01F, &measured)) ||
        measured.left != bounds.x || measured.top != bounds.y ||
        measured.right != bounds.x + bounds.width || measured.bottom != bounds.y + bounds.height ||
        com::failed(path->StrokeContainsPoint({-5, 11}, 2, nullptr, &matrix, 0.01F, &direct_hit)) || direct_hit != 1)
        return false;
    const std::array<float, 3> pattern{1, 2, 1}; // doubled odd pattern: on/off/on/off/on/off
    for (const float phase : {0.0F, 1.0F, 2.0F, 3.5F, 4.5F, 5.5F, 7.5F, -0.5F}) {
        pen.dash_offset = phase;
        const bool visible = phase == 0 || phase == 1 || phase == 3.5F || phase == 5.5F;
        if (progpu_native_geometry_stroke_query(&figure, 1, &edge, &flag, 1, &pen,
            pattern.data(), 3, nullptr, &anchor, 0.01F, &bounds, &has, &hit) != PROGPU_NATIVE_STATUS_SUCCESS ||
            hit != (visible ? 1U : 0U)) return false;
    }
    return true;
}

bool constant_edges_preserve_endpoint_and_join_eligibility()
{
    const std::array baseline{line_segment({0, 0}, {2, 0}), line_segment({2, 0}, {2, 2})};
    const std::array padded{line_segment({0, 0}, {0, 0}), baseline[0],
        line_segment({2, 0}, {2, 0}), baseline[1], line_segment({2, 2}, {2, 2})};
    const std::array<std::uint8_t, 2> baseline_flags{1, 3};
    std::array<std::uint8_t, 5> flags{1, 1, 3, 1, 1};
    const progpu_native_geometry_query_pen pen{2, 10, 0, 1, 3, 0, 1};
    const auto query = [&](std::span<const segment> edges, std::span<const std::uint8_t> states,
        const point* probe, progpu_native_image_rect& bounds, std::uint32_t& hit) {
        const progpu_native_geometry_query_figure figure{{0, 0}, 0, static_cast<std::uint32_t>(edges.size()), 0};
        std::uint32_t has = 0;
        return progpu_native_geometry_stroke_query(&figure, 1, edges.data(), states.data(), figure.segment_count,
            &pen, nullptr, 0, nullptr, probe, 0.01F, &bounds, &has, &hit);
    };
    progpu_native_image_rect expected{}, actual{};
    std::uint32_t first = 0, second = 0;
    if (query(baseline, baseline_flags, nullptr, expected, first) != PROGPU_NATIVE_STATUS_SUCCESS ||
        query(padded, flags, nullptr, actual, second) != PROGPU_NATIVE_STATUS_SUCCESS ||
        expected.x != actual.x || expected.y != actual.y || expected.width != actual.width || expected.height != actual.height)
        return false;
    for (int y = -5; y <= 13; ++y) for (int x = -5; x <= 13; ++x) {
        const point probe{x * 0.25F, y * 0.25F};
        if (query(baseline, baseline_flags, &probe, expected, first) != PROGPU_NATIVE_STATUS_SUCCESS ||
            query(padded, flags, &probe, actual, second) != PROGPU_NATIVE_STATUS_SUCCESS || first != second) return false;
    }
    // Zero-distance gaps must suppress source caps, not heal endpoint identity.
    flags.front() = flags.back() = 0;
    const point before{-0.5F, 0}, after{2, 2.5F};
    return query(padded, flags, &before, actual, second) == PROGPU_NATIVE_STATUS_SUCCESS && second == 0 &&
        query(padded, flags, &after, actual, second) == PROGPU_NATIVE_STATUS_SUCCESS && second == 0;
}

bool dash_validation_matches_scalar_oracle()
{
    namespace core = progpu::native::direct2d::core;
    core::stroke_style_properties_f style{};
    style.dash = core::dash_style::custom;
    style.miter_limit = 10;
    std::array<float, 18> storage{};
    float* pattern = storage.data() + 1; // deliberately not 16-byte aligned
    for (std::uint32_t count = 1; count <= 17; ++count) {
        for (std::uint32_t changed = 0; changed < count; ++changed) {
            for (float value : {0.0F, 2.0F, -1.0F, std::numeric_limits<float>::infinity(),
                std::numeric_limits<float>::quiet_NaN()}) {
                std::fill(pattern, pattern + count, 0.0F);
                pattern[changed] = value;
                bool valid = true, positive = false;
                for (std::uint32_t i = 0; i < count; ++i) {
                    valid &= std::isfinite(pattern[i]) && pattern[i] >= 0;
                    positive |= pattern[i] > 0;
                }
                if (core::valid_stroke_style(style, pattern, count) != (valid && positive)) return false;
            }
        }
    }
    return true;
}
} // namespace

int main()
{
    const std::array<std::pair<const char*, bool (*)()>, 10> tests{{
        {"filled_relations", filled_relations_preserve_topology_and_shared_com_results},
        {"modes_and_boundaries", modes_and_actual_boundaries},
        {"curved_result", curved_result_matches_shared_core},
        {"empty_ownership", failures_and_empty_ownership},
        {"fill_queries", fill_queries_match_scalar_and_reject_bad_inputs},
        {"stroke_queries", stroke_queries_preserve_caps_gaps_dashes_and_world_order},
        {"stroke_transport", stroke_queries_reject_incomplete_transport},
        {"point_strokes", point_strokes_match_independent_cap_oracle},
        {"constant_edges", constant_edges_preserve_endpoint_and_join_eligibility},
        {"dash_validation", dash_validation_matches_scalar_oracle}}};
    bool passed = true;
    for (const auto& [name, test] : tests) {
        if (!test()) {
            std::fprintf(stderr, "Native geometry utility conformance failed: %s\n", name);
            passed = false;
        }
    }
    if (!passed) return 1;
    return 0;
}

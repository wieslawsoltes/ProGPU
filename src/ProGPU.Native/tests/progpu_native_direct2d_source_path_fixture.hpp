#pragma once

#include "../src/Direct2D/progpu_native_direct2d_prepared_glyphs.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <vector>

namespace progpu::native::direct2d::tests {

// Independent scalar oracle: recursively partition the original cubic's
// dyadic parameter tree using its analytic second derivative. Production uses
// an ordered adaptive walk and SIMD axes. Neither side computes pixel coverage.
inline std::vector<progpu_native_path_segment> scalar_source_path(
    std::span<const progpu_native_path_segment> source, const compat::matrix_3x2_f& m,
    const progpu_native_scene_source_coverage_frame& frame)
{
    const auto multiply = [](float a, float b) { volatile float v = a * b; return v; };
    const auto add = [](float a, float b) { volatile float v = a + b; return v; };
    const auto project = [&](progpu_native_point p) {
        const float x = multiply(add(add(multiply(p.x, m.m11), multiply(p.y, m.m21)), m.m31), frame.dpi_scale_x);
        const float y = multiply(add(add(multiply(p.x, m.m12), multiply(p.y, m.m22)), m.m32), frame.dpi_scale_y);
        return progpu_native_point{std::ceil(x * 16.F) / 16.F, std::ceil(y * 16.F) / 16.F};
    };
    std::vector<progpu_native_path_segment> result;
    const auto line = [&](progpu_native_point a, progpu_native_point b) {
        result.push_back({a, b, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U});
    };
    for (const auto& segment : source) {
        if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE) {
            line(project(segment.p0), project(segment.p1));
            continue;
        }
        const std::array<progpu_native_point, 4U> points{
            project(segment.p0), project(segment.p1), project(segment.p2), project(segment.p3)};
        const auto curvature = [&](double t) {
            double maximum = 0;
            for (unsigned axis = 0; axis < 2; ++axis) {
                const auto coordinate = [&](unsigned i) { return double(axis == 0 ? points[i].x : points[i].y); };
                const double first = 6 * coordinate(0) - 12 * coordinate(1) + 6 * coordinate(2);
                const double last = 6 * coordinate(1) - 12 * coordinate(2) + 6 * coordinate(3);
                maximum = std::max(maximum, std::abs(first * (1 - t) + last * t));
            }
            return maximum;
        };
        const auto evaluate = [&](double t) {
            const double u = 1 - t;
            std::array<float, 2U> position{};
            for (unsigned axis = 0; axis < 2; ++axis) {
                const auto coordinate = [&](unsigned i) { return double(axis == 0 ? points[i].x : points[i].y); };
                const double value = coordinate(0) * (u*u*u) + coordinate(1) * (3*u*u*t) +
                    coordinate(2) * (3*u*t*t) + coordinate(3) * (t*t*t);
                position[axis] = float(std::floor(value * 16 + .5) / 16);
            }
            return progpu_native_point{position[0], position[1]};
        };
        auto previous = points[0];
        const auto visit = [&](auto&& self, double begin, double end) -> void {
            const double width = end - begin;
            if (std::max(curvature(begin), curvature(end)) * width * width > 1.5) {
                const double middle = (begin + end) / 2;
                self(self, begin, middle); self(self, middle, end);
            } else {
                const auto next = evaluate(end); line(previous, next); previous = next;
            }
        };
        visit(visit, 0, 1);
    }
    return result;
}

template<class Check>
bool source_path_preparation_contracts(Check check)
{
    const progpu_native_scene_source_coverage_frame original{sizeof(original), 1U, 1, 1, 64U, 64U, 0U, 0U};
    const compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    const std::array transforms{identity, compat::matrix_3x2_f{1, .25F, -.125F, 1, 7, -9},
        compat::matrix_3x2_f{-1, 0, 0, 1.5F, -3.59375F, 17.90625F},
        compat::matrix_3x2_f{.5F, -.25F, .125F, 2, .03125F, -.09375F}};
    unsigned compared = 0;
    for (unsigned seed = 0; seed < 192; ++seed) {
        const float shift = float(int(seed % 17) - 8) / 32;
        const progpu_native_point a{-13.03125F + shift, -7.09375F - shift}, b{19.15625F, 11.03125F};
        const std::array<progpu_native_path_segment, 2U> source{{
            {a, {float(int(seed % 23) - 11) * 3.125F, 27.21875F + shift},
                {-23.84375F, float(int(seed % 29) - 14) * 2.0625F}, b, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0U, 0U, 0U},
            {b, a, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}}};
        const auto retained = source;
        for (const auto& transform : transforms) {
            auto frame = original;
            frame.dpi_scale_x = seed % 2 ? 1.25F : 2.F;
            frame.dpi_scale_y = seed % 3 ? 1.5F : 1.F;
            prepared_original_path_coverage candidate;
            if (!check(prepare_original_path_coverage(source, transform, frame, seed % 2, candidate) == com::ok,
                    "source path SIMD preparation")) return false;
            const auto scalar = scalar_source_path(source, transform, frame);
            if (!check(candidate.segments.size() == scalar.size() &&
                    std::memcmp(candidate.segments.data(), scalar.data(), scalar.size() * sizeof(scalar[0])) == 0 &&
                    std::memcmp(source.data(), retained.data(), sizeof(source)) == 0 &&
                    candidate.path.sample_grid == 8U && candidate.path.fill_rule == seed % 2 &&
                    candidate.path.segment_count == scalar.size(),
                    "source path SIMD/scalar exact coordinates, subdivision and original ownership")) return false;
            ++compared;
        }
    }
    // Exact curvature threshold and its adjacent original 1/16 control.
    std::array<progpu_native_path_segment, 2U> threshold{{
        {{0, 0}, {.0625F, .0625F}, {.375F, .375F}, {.6875F, .6875F}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0U, 0U, 0U},
        {{.6875F, .6875F}, {0, 0}, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}}};
    prepared_original_path_coverage retained;
    if (!check(prepare_original_path_coverage(threshold, identity, original, 0U, retained) == com::ok &&
            retained.segments.size() == 2U, "source path admits exact derivative threshold")) return false;
    threshold[0].p2.x += .0625F;
    if (!check(prepare_original_path_coverage(threshold, identity, original, 0U, retained) == com::ok &&
            retained.segments.size() == 3U, "source path subdivides immediately above exact threshold")) return false;
    const auto retained_segments = retained.segments;
    const auto retained_path = retained.path;
    for (unsigned rejected = 0; rejected < 12; ++rejected) {
        auto source = threshold; auto frame = original; auto transform = identity;
        switch (rejected) {
        case 0: source[0].kind = PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC; break;
        case 1: source[1].p0.x += .001F; break; // Same physical cell, still disconnected.
        case 2: source[1].p1.x += .001F; break;
        case 3: source[0].p1.y = std::numeric_limits<float>::quiet_NaN(); break;
        case 4: source[0].pad0 = 1U; break;
        case 5: frame.flags = 1U; break;
        case 6: frame.dpi_scale_y = 0; break;
        case 7: frame.pixel_height = 0U; break;
        case 8: transform.m22 = 0; break;
        case 9: transform.m31 = 16385; break;
        case 10: frame.version = 2U; break;
        case 11: transform.m11 = std::numeric_limits<float>::infinity(); break;
        }
        if (!check(prepare_original_path_coverage(source, transform, frame, 0U, retained) == com::false_result &&
                retained.segments.size() == retained_segments.size() &&
                std::memcmp(retained.segments.data(), retained_segments.data(), retained_segments.size() * sizeof(source[0])) == 0 &&
                std::memcmp(&retained.path, &retained_path, sizeof(retained_path)) == 0,
                "rejected source paths preserve the entire earlier generation")) return false;
    }
    return check(compared == 768U, "complete source path SIMD/scalar inventory");
}
} // namespace progpu::native::direct2d::tests

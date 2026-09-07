#pragma once

#include "../Mil/progpu_native_mil_curve_dash.hpp"

#include <array>
#include <bit>
#include <cmath>
#include <cstdint>
#include <new>
#include <span>
#include <vector>

#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#define PROGPU_NATIVE_STROKE_INTRINSICS_NEON 1
#elif defined(__SSE2__) || defined(_M_X64) || (defined(_M_IX86_FP) && _M_IX86_FP >= 2)
#include <emmintrin.h>
#define PROGPU_NATIVE_STROKE_INTRINSICS_SSE2 1
#endif

namespace progpu::native::semantic_path_stroke {

enum class result {
    success,
    invalid,
    capacity_exceeded
};

struct style {
    progpu_native_affine_2d transform{};
    float thickness{};
    float miter_limit{1.0F};
    double dash_offset{};
    std::uint32_t start_cap{};
    std::uint32_t end_cap{};
    std::uint32_t dash_cap{};
    std::uint32_t line_join{};
    std::uint32_t primitive_flags{};
};

// smooth_joins[i] describes the join from segments[i] to segments[i + 1].
// For a closed run, the last entry describes the closing seam. Direct2D's
// FORCE_ROUND_LINE_JOIN flag belongs to the incoming segment instead, so its
// adapter deliberately shifts that flag while constructing semantic runs.

inline progpu_native_point segment_end(
    const progpu_native_path_segment& segment) noexcept {
    return segment.kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC
        ? segment.p2
        : segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC
            ? segment.p3
            : segment.p1;
}

// Exact constancy only: equal endpoints do not make a retracing curve constant.
// Four independent control-coordinate comparisons use intrinsic lanes; a cubic
// has one fixed two-coordinate tail. Arcs retain their analytic representation.
inline bool is_constant_segment(const progpu_native_path_segment& segment) noexcept {
    if (!std::isfinite(segment.p0.x) || !std::isfinite(segment.p0.y) ||
        (segment.kind != PROGPU_NATIVE_PATH_SEGMENT_LINE &&
         segment.kind != PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC &&
         segment.kind != PROGPU_NATIVE_PATH_SEGMENT_CUBIC)) return false;
    const auto second = segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE ? segment.p1 : segment.p2;
    const std::array<float, 4U> coordinates{segment.p1.x, segment.p1.y, second.x, second.y};
    const std::array<float, 4U> anchor{segment.p0.x, segment.p0.y, segment.p0.x, segment.p0.y};
    bool equal = false;
#if defined(PROGPU_NATIVE_STROKE_INTRINSICS_NEON)
    equal = vminvq_u32(vceqq_f32(vld1q_f32(coordinates.data()), vld1q_f32(anchor.data()))) != 0U;
#elif defined(PROGPU_NATIVE_STROKE_INTRINSICS_SSE2)
    equal = _mm_movemask_ps(_mm_cmpeq_ps(_mm_loadu_ps(coordinates.data()), _mm_loadu_ps(anchor.data()))) == 15;
#else
    equal = coordinates[0U] == anchor[0U] && coordinates[1U] == anchor[1U] &&
        coordinates[2U] == anchor[2U] && coordinates[3U] == anchor[3U];
#endif
    return equal && (segment.kind != PROGPU_NATIVE_PATH_SEGMENT_CUBIC ||
        (segment.p3.x == segment.p0.x && segment.p3.y == segment.p0.y));
}

inline bool has_mixed_constant_segments(std::span<const progpu_native_path_segment> segments) noexcept {
    bool constant = false, moving = false;
    for (const auto& segment : segments) {
        if (is_constant_segment(segment)) constant = true;
        else moving = true;
        if (constant && moving) return true;
    }
    return false;
}

// Caller-owned compaction: no allocation, O(S) topology work, O(1) state.
// Do not heal discontinuities. Validate connectivity before changing either span.
// Any forced-round join across a removed zero-distance chain remains forced.
// All-constant contours retain one anchor segment for the caller's point-cap policy.
inline result compact_constant_segments(std::span<progpu_native_path_segment> segments,
    std::span<std::uint8_t> smooth_joins, bool closed, std::size_t& count) noexcept {
    if (segments.empty() || segments.size() != smooth_joins.size()) return result::invalid;
    const auto same = [](progpu_native_point first, progpu_native_point second) {
        return first.x == second.x && first.y == second.y;
    };
    for (std::size_t index = 1U; index < segments.size(); ++index)
        if (!same(segment_end(segments[index - 1U]), segments[index].p0)) return result::invalid;
    if (closed && !same(segment_end(segments.back()), segments.front().p0)) return result::invalid;
    std::size_t written = 0U;
    std::uint8_t leading_join = 0U;
    for (std::size_t index = 0U; index < segments.size(); ++index) {
        if (is_constant_segment(segments[index])) {
            if (written != 0U) smooth_joins[written - 1U] |= smooth_joins[index];
            else leading_join |= smooth_joins[index];
            continue;
        }
        segments[written] = segments[index];
        smooth_joins[written] = smooth_joins[index];
        ++written;
    }
    if (written == 0U) written = 1U;
    if (closed) smooth_joins[written - 1U] |= leading_join;
    count = written;
    return result::success;
}

inline bool try_tangent(
    const progpu_native_path_segment& segment,
    bool at_start,
    progpu_native_point& tangent) noexcept {
    const auto subtract = [](progpu_native_point first,
                             progpu_native_point second) noexcept {
        return progpu_native_point{
            first.x - second.x,
            first.y - second.y};
    };
    const auto nonzero = [](progpu_native_point value) noexcept {
        return value.x != 0.0F || value.y != 0.0F;
    };
    switch (segment.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE:
        tangent = subtract(segment.p1, segment.p0);
        return nonzero(tangent);
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
        tangent = at_start
            ? subtract(segment.p1, segment.p0)
            : subtract(segment.p2, segment.p1);
        if (!nonzero(tangent)) {
            tangent = subtract(segment.p2, segment.p0);
        }
        return nonzero(tangent);
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC: {
        const std::array candidates = at_start
            ? std::array{
                  subtract(segment.p1, segment.p0),
                  subtract(segment.p2, segment.p0),
                  subtract(segment.p3, segment.p0)}
            : std::array{
                  subtract(segment.p3, segment.p2),
                  subtract(segment.p3, segment.p1),
                  subtract(segment.p3, segment.p0)};
        for (const auto candidate : candidates) {
            if (nonzero(candidate)) {
                tangent = candidate;
                return true;
            }
        }
        return false;
    }
    case PROGPU_NATIVE_PATH_SEGMENT_ARC: {
        const float theta = std::bit_cast<float>(segment.pad0) +
            (at_start ? 0.0F : std::bit_cast<float>(segment.pad1));
        const float direction = std::bit_cast<float>(segment.pad1) < 0.0F
            ? -1.0F
            : 1.0F;
        const float rotation = std::bit_cast<float>(segment.pad2);
        const float cosine_rotation = std::cos(rotation);
        const float sine_rotation = std::sin(rotation);
        const progpu_native_point axis_x{
            segment.p3.x * cosine_rotation,
            segment.p3.x * sine_rotation};
        const progpu_native_point axis_y{
            -segment.p3.y * sine_rotation,
            segment.p3.y * cosine_rotation};
        tangent = {
            direction * (-axis_x.x * std::sin(theta) +
                axis_y.x * std::cos(theta)),
            direction * (-axis_x.y * std::sin(theta) +
                axis_y.y * std::cos(theta))};
        return nonzero(tangent);
    }
    default:
        return false;
    }
}

inline bool make_primitive(
    const progpu_native_path_segment& segment,
    const style& stroke,
    progpu_native_geometry_primitive& primitive) noexcept {
    primitive = {};
    primitive.flags = stroke.primitive_flags;
    primitive.stroke_thickness = stroke.thickness;
    primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
    primitive.transform = stroke.transform;
    switch (segment.kind) {
    case PROGPU_NATIVE_PATH_SEGMENT_LINE:
        primitive.kind = PROGPU_NATIVE_GEOMETRY_LINE;
        primitive.p0 = segment.p0;
        primitive.p1 = segment.p1;
        return true;
    case PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC:
        primitive.kind = PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER;
        primitive.p0 = segment.p0;
        primitive.p1 = segment.p1;
        primitive.p2 = segment.p2;
        return true;
    case PROGPU_NATIVE_PATH_SEGMENT_CUBIC:
        primitive.kind = PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER;
        primitive.p0 = segment.p0;
        primitive.p1 = segment.p1;
        primitive.p2 = segment.p2;
        primitive.p3 = segment.p3;
        return true;
    case PROGPU_NATIVE_PATH_SEGMENT_ARC: {
        primitive.kind = PROGPU_NATIVE_GEOMETRY_ARC;
        primitive.p0 = segment.p2;
        const float rotation = std::bit_cast<float>(segment.pad2);
        const float cosine_rotation = std::cos(rotation);
        const float sine_rotation = std::sin(rotation);
        primitive.p1 = {
            segment.p3.x * cosine_rotation,
            segment.p3.x * sine_rotation};
        primitive.p2 = {
            -segment.p3.y * sine_rotation,
            segment.p3.y * cosine_rotation};
        primitive.p3 = {
            std::bit_cast<float>(segment.pad0),
            std::bit_cast<float>(segment.pad1)};
        return true;
    }
    default:
        return false;
    }
}

inline result compile(
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> smooth_joins,
    bool closed,
    std::span<const double> dash_intervals,
    const style& stroke,
    std::uint32_t brush_index,
    mil::curve_dash::run_buffer& dash_scratch,
    std::vector<progpu_native_geometry_primitive>& primitives,
    std::vector<std::uint32_t>& brushes) {
    constexpr std::uint32_t allowed_primitive_flags =
        PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED |
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE;
    const bool finite_transform =
        std::isfinite(stroke.transform.m11) &&
        std::isfinite(stroke.transform.m12) &&
        std::isfinite(stroke.transform.m21) &&
        std::isfinite(stroke.transform.m22) &&
        std::isfinite(stroke.transform.m31) &&
        std::isfinite(stroke.transform.m32);
    const bool hairline = (stroke.primitive_flags &
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE) != 0U;
    const bool fixed_device = (stroke.primitive_flags &
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE) != 0U;
    if (segments.empty() || smooth_joins.size() != segments.size() ||
        !std::isfinite(stroke.thickness) ||
        (hairline && stroke.thickness != 0.0F) ||
        (!hairline && stroke.thickness <= 0.0F) ||
        !std::isfinite(stroke.miter_limit) || stroke.miter_limit < 1.0F ||
        !std::isfinite(stroke.dash_offset) || !finite_transform ||
        stroke.start_cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
        stroke.end_cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
        stroke.dash_cap > PROGPU_NATIVE_STROKE_CAP_TRIANGLE ||
        stroke.line_join > PROGPU_NATIVE_STROKE_JOIN_ROUND ||
        (stroke.primitive_flags & ~allowed_primitive_flags) != 0U ||
        (hairline && fixed_device)) {
        return result::invalid;
    }
    const std::size_t primitive_start = primitives.size();
    const std::size_t brush_start = brushes.size();
    const auto rollback = [&](result value) noexcept {
        primitives.resize(primitive_start);
        brushes.resize(brush_start);
        return value;
    };
    try {
        std::vector<progpu_native_path_segment> normalized_segments;
        std::vector<std::uint8_t> normalized_joins;
        if (has_mixed_constant_segments(segments)) {
            normalized_segments.assign(segments.begin(), segments.end());
            normalized_joins.assign(smooth_joins.begin(), smooth_joins.end());
            std::size_t count = 0U;
            if (compact_constant_segments(normalized_segments, normalized_joins, closed, count) != result::success)
                return rollback(result::invalid);
            segments = std::span<const progpu_native_path_segment>(normalized_segments).first(count);
            smooth_joins = std::span<const std::uint8_t>(normalized_joins).first(count);
        }
        const auto append_cap = [&](
            const progpu_native_path_segment& segment,
            std::uint32_t cap,
            bool at_start) {
            if (cap == PROGPU_NATIVE_STROKE_CAP_FLAT) {
                return true;
            }
            progpu_native_point tangent{};
            if (!try_tangent(segment, at_start, tangent)) {
                return false;
            }
            progpu_native_geometry_primitive primitive{};
            primitive.kind = PROGPU_NATIVE_GEOMETRY_PATH_CAP;
            primitive.flags = stroke.primitive_flags |
                (cap << PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT);
            primitive.p0 = at_start ? segment.p0 : segment_end(segment);
            primitive.p1 = tangent;
            primitive.p2.x = at_start ? 1.0F : 0.0F;
            primitive.stroke_thickness = stroke.thickness;
            primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
            primitive.transform = stroke.transform;
            primitives.push_back(primitive);
            brushes.push_back(brush_index);
            return true;
        };
        const auto append_join = [&](
            const progpu_native_path_segment& incoming,
            const progpu_native_path_segment& outgoing,
            bool smooth_join) {
            const auto join_point = segment_end(incoming);
            if (join_point.x != outgoing.p0.x ||
                join_point.y != outgoing.p0.y) {
                return false;
            }
            progpu_native_point incoming_tangent{};
            progpu_native_point outgoing_tangent{};
            if (!try_tangent(incoming, false, incoming_tangent) ||
                !try_tangent(outgoing, true, outgoing_tangent)) {
                return false;
            }
            progpu_native_geometry_primitive primitive{};
            primitive.kind = PROGPU_NATIVE_GEOMETRY_PATH_JOIN;
            primitive.flags = stroke.primitive_flags |
                ((smooth_join
                    ? static_cast<std::uint32_t>(
                        PROGPU_NATIVE_STROKE_JOIN_ROUND)
                    : stroke.line_join) <<
                    PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT);
            primitive.p0 = join_point;
            primitive.p1 = incoming_tangent;
            primitive.p2 = outgoing_tangent;
            primitive.p3.x = stroke.miter_limit;
            primitive.stroke_thickness = stroke.thickness;
            primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
            primitive.transform = stroke.transform;
            primitives.push_back(primitive);
            brushes.push_back(brush_index);
            return true;
        };
        const auto append_run = [&](
            std::span<const progpu_native_path_segment> run_segments,
            std::span<const std::uint8_t> run_smooth_joins,
            bool run_closed,
            bool closing_smooth_join,
            std::uint32_t run_start_cap,
            std::uint32_t run_end_cap) {
            if (run_segments.empty() ||
                run_smooth_joins.size() + 1U != run_segments.size()) {
                return false;
            }
            if (!run_closed && !append_cap(
                    run_segments.front(), run_start_cap, true)) {
                return false;
            }
            for (std::size_t index = 0U;
                 index < run_segments.size();
                 ++index) {
                if (index != 0U && !append_join(
                        run_segments[index - 1U],
                        run_segments[index],
                        run_smooth_joins[index - 1U] != 0U)) {
                    return false;
                }
                progpu_native_geometry_primitive primitive{};
                if (!make_primitive(run_segments[index], stroke, primitive)) {
                    return false;
                }
                primitives.push_back(primitive);
                brushes.push_back(brush_index);
            }
            if (run_closed && !append_join(
                    run_segments.back(),
                    run_segments.front(),
                    closing_smooth_join)) {
                return false;
            }
            return run_closed || append_cap(
                run_segments.back(), run_end_cap, false);
        };

        if (!dash_intervals.empty()) {
            const auto dash_result = mil::curve_dash::try_create_runs(
                segments,
                smooth_joins,
                closed,
                dash_intervals,
                stroke.dash_offset,
                hairline ? 1.0F : stroke.thickness,
                dash_scratch,
                hairline || fixed_device ? &stroke.transform : nullptr);
            if (dash_result ==
                mil::curve_dash::result::capacity_exceeded) {
                return rollback(result::capacity_exceeded);
            }
            if (dash_result != mil::curve_dash::result::success) {
                return rollback(result::invalid);
            }
            for (const auto& run : dash_scratch.runs) {
                const std::uint32_t run_start_cap =
                    run.starts_at_source_start
                    ? stroke.start_cap
                    : stroke.dash_cap;
                const std::uint32_t run_end_cap = run.ends_at_source_end
                    ? stroke.end_cap
                    : stroke.dash_cap;
                if (!append_run(
                        dash_scratch.segments_for(run),
                        dash_scratch.smooth_joins_for(run),
                        run.closed,
                        run.closing_smooth_join,
                        run_start_cap,
                        run_end_cap)) {
                    return rollback(result::invalid);
                }
            }
            if (dash_scratch.terminal_visible_point) {
                progpu_native_point tangent{};
                if (!try_tangent(segments.back(), false, tangent)) {
                    return rollback(result::invalid);
                }
                const progpu_native_point endpoint =
                    segment_end(segments.back());
                progpu_native_path_segment terminal_start{};
                terminal_start.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                terminal_start.p0 = endpoint;
                terminal_start.p1 = {
                    endpoint.x + tangent.x,
                    endpoint.y + tangent.y};
                progpu_native_path_segment terminal_end{};
                terminal_end.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                terminal_end.p0 = {
                    endpoint.x - tangent.x,
                    endpoint.y - tangent.y};
                terminal_end.p1 = endpoint;
                if (!append_cap(
                        terminal_start, stroke.dash_cap, true) ||
                    !append_cap(
                        terminal_end, stroke.end_cap, false)) {
                    return rollback(result::invalid);
                }
            }
            return result::success;
        }
        if (!append_run(
                segments,
                smooth_joins.first(smooth_joins.size() - 1U),
                closed,
                smooth_joins.back() != 0U,
                stroke.start_cap,
                stroke.end_cap)) {
            return rollback(result::invalid);
        }
        return result::success;
    } catch (const std::bad_alloc&) {
        return rollback(result::capacity_exceeded);
    } catch (...) {
        return rollback(result::invalid);
    }
}

} // namespace progpu::native::semantic_path_stroke

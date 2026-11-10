#pragma once

#include "progpu_native.h"

#include <cstdint>
#include <span>
#include <vector>

namespace progpu::native::geometry {

/* Arc-length measure of the contours of a path in the canonical segment layout
 * (progpu_native_path_segment), with the contour semantics of the managed
 * SKPathMeasure and of Skia's contour measure: every figure with a positive
 * length is one contour, in order; a closed figure (or every figure with
 * force_closed) measures its closing line. Curves keep their kind: lengths come
 * from chords of a subdivision until a chord is within 0.5 / resolution_scale of
 * its curve (the curvature tests of Skia's contour measure, so lengths agree
 * with Skia-based backends), and segments between two distances are the exact
 * pieces of the original curves (quadratics, cubics, arcs, rational quadratics
 * and rational cubics), not flattened lines. Computation is in double. */
class path_measure final {
public:
    path_measure() = default;

    /* Measures the figures (query-figure layout: start, first_segment,
     * segment_count, flags bit 0 closed; the figures partition the segments in
     * order). Returns INVALID_ARGUMENT for non-finite input, unknown kinds,
     * non-positive weights or a partition gap; may throw std::bad_alloc.
     * The previous content is replaced only on success. */
    [[nodiscard]] progpu_native_status build(
        std::span<const progpu_native_geometry_query_figure> figures,
        std::span<const progpu_native_path_segment> segments,
        bool force_closed, float resolution_scale);

    [[nodiscard]] std::uint32_t contour_count() const noexcept
    {
        return static_cast<std::uint32_t>(contours_.size());
    }

    [[nodiscard]] float length(std::uint32_t contour) const noexcept;
    [[nodiscard]] bool closed(std::uint32_t contour) const noexcept;

    /* Position and unit tangent at a distance along the contour (pinned to
     * [0, length]); a degenerate tangent is zero. False for a contour index out
     * of range. */
    [[nodiscard]] bool position_tangent(std::uint32_t contour, float distance,
        progpu_native_point* position, progpu_native_point* tangent) const noexcept;

    /* Appends the piece of the contour between two distances (start pinned to
     * zero, stop to the length) to output: the exact sub-segments, a single
     * zero-length line for an empty piece. False (nothing appended) when the
     * pinned start is after the pinned stop or a distance is not a number. May
     * throw std::bad_alloc. */
    [[nodiscard]] bool segment(std::uint32_t contour, float start_distance,
        float stop_distance, std::vector<progpu_native_path_segment>& output) const;

private:
    struct sample final {
        double distance;
        double t;
        std::uint32_t segment;
    };

    struct contour final {
        std::uint32_t first_segment;
        std::uint32_t segment_count;
        std::uint32_t first_sample;
        std::uint32_t sample_count;
        double length;
        bool closed;
    };

    [[nodiscard]] std::uint32_t locate(const contour& value, double distance,
        double& t) const noexcept;

    std::vector<progpu_native_path_segment> segments_;
    std::vector<sample> samples_;
    std::vector<contour> contours_;
};

} // namespace progpu::native::geometry

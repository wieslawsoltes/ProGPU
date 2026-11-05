#pragma once

#include "progpu_native.h"
#include "progpu_native_geometry.hpp"

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <span>
#include <utility>
#include <vector>

/* The neutral path core of the Geometry module: recorded path data, plain C++
 * sinks and collectors, and the device-independent path, Boolean and stroke
 * operations over the data. The Direct2D path geometry (COM) and the
 * progpu_native_geometry_* C ABI are clients; neither this header nor its
 * implementation depends on Direct2D, COM or a device.
 *
 * Moved from src/Direct2D/progpu_native_direct2d_path.cpp; the algorithms,
 * kernels and numerics are unchanged. Every operation here mirrors the body of
 * the corresponding path geometry method (GetBounds, GetWidenedBounds,
 * StrokeContainsPoint, FillContainsPoint, Simplify, Tessellate, Outline,
 * ComputeArea, ComputeLength, ComputePointAtLength, Widen), including its
 * argument and state validation, and returns the same status values. */

namespace progpu::native::geometry {

enum class path_state : std::uint32_t {
    fresh,
    open,
    closed,
    failed
};

enum class segment_kind : std::uint8_t {
    line,
    cubic,
    quadratic,
    arc
};

struct stored_segment final {
    segment_kind kind = segment_kind::line;
    path_segment flags = path_segment::none;
    point_2f end{};
    point_2f control1{};
    point_2f control2{};
    arc_segment_f arc{};
};

struct stored_figure final {
    point_2f start{};
    figure_begin begin = figure_begin::filled;
    figure_end end = figure_end::open;
    std::uint32_t first_segment = 0U;
    std::uint32_t segment_count = 0U;
};

/* A recorded path. Recording (path_recorder) happens while state is open;
 * the operations below read a closed path only. */
struct path_data final {
    mutable std::mutex mutex;
    std::vector<stored_figure> figures;
    std::vector<stored_segment> segments;
    std::atomic<path_state> state{path_state::fresh};
    fill_mode mode = fill_mode::alternate;
    path_segment current_flags = path_segment::none;
    std::uint32_t public_segment_count = 0U;
    status recording_failure = status_code::ok;
    bool figure_open = false;
};

/* Receiver of simplified geometry: lines and cubic Beziers in figures. The
 * method contract is the one of a Direct2D simplified geometry sink. */
class simplified_sink {
public:
    virtual ~simplified_sink() = default;

    virtual void set_fill_mode(fill_mode value) noexcept = 0;
    virtual void set_segment_flags(path_segment value) noexcept = 0;
    virtual void begin_figure(point_2f start, figure_begin begin) noexcept = 0;
    virtual void add_lines(
        const point_2f* points,
        std::uint32_t point_count) noexcept = 0;
    virtual void add_beziers(
        const cubic_bezier_segment_f* beziers,
        std::uint32_t bezier_count) noexcept = 0;
    virtual void end_figure(figure_end end) noexcept = 0;
    [[nodiscard]] virtual status close() noexcept = 0;
};

/* Receiver of a full path: adds single lines, quadratic Beziers and arcs. The
 * method contract is the one of a Direct2D geometry sink. */
class path_sink : public simplified_sink {
public:
    virtual void add_line(point_2f point) noexcept = 0;
    virtual void add_bezier(const cubic_bezier_segment_f* bezier) noexcept = 0;
    virtual void add_quadratic_bezier(
        const quadratic_bezier_segment_f* bezier) noexcept = 0;
    virtual void add_quadratic_beziers(
        const quadratic_bezier_segment_f* beziers,
        std::uint32_t bezier_count) noexcept = 0;
    virtual void add_arc(const arc_segment_f* arc) noexcept = 0;
};

/* Records a path into path_data, which must be in the open state. Validation
 * failures are latched in path_data::recording_failure and returned by
 * close(), which closes (or fails) the path. Destroying the recorder of a
 * still-open path fails the path. */
class path_recorder final : public path_sink {
public:
    explicit path_recorder(std::shared_ptr<path_data> data) noexcept;
    ~path_recorder() override;

    path_recorder(const path_recorder&) = delete;
    path_recorder& operator=(const path_recorder&) = delete;

    void set_fill_mode(fill_mode value) noexcept override;
    void set_segment_flags(path_segment value) noexcept override;
    void begin_figure(point_2f start, figure_begin begin) noexcept override;
    void add_lines(
        const point_2f* points,
        std::uint32_t point_count) noexcept override;
    void add_beziers(
        const cubic_bezier_segment_f* beziers,
        std::uint32_t bezier_count) noexcept override;
    void end_figure(figure_end end) noexcept override;
    [[nodiscard]] status close() noexcept override;
    void add_line(point_2f point) noexcept override;
    void add_bezier(const cubic_bezier_segment_f* bezier) noexcept override;
    void add_quadratic_bezier(
        const quadratic_bezier_segment_f* bezier) noexcept override;
    void add_quadratic_beziers(
        const quadratic_bezier_segment_f* beziers,
        std::uint32_t bezier_count) noexcept override;
    void add_arc(const arc_segment_f* arc) noexcept override;

    [[nodiscard]] const std::shared_ptr<path_data>& data() const noexcept
    {
        return data_;
    }

private:
    [[nodiscard]] bool can_record() const noexcept;
    void fail(status value) noexcept;
    [[nodiscard]] bool append(const stored_segment& segment) noexcept;

    std::shared_ptr<path_data> data_;
};

/* Collects the single filled polygon of a simplified line stream. */
class single_polygon_collector final : public simplified_sink {
public:
    void set_fill_mode(fill_mode value) noexcept override;
    void set_segment_flags(path_segment value) noexcept override;
    void begin_figure(point_2f start, figure_begin begin) noexcept override;
    void add_lines(
        const point_2f* points,
        std::uint32_t point_count) noexcept override;
    void add_beziers(
        const cubic_bezier_segment_f* beziers,
        std::uint32_t bezier_count) noexcept override;
    void end_figure(figure_end end) noexcept override;
    [[nodiscard]] status close() noexcept override;

    [[nodiscard]] status result() const noexcept;
    [[nodiscard]] const std::vector<point_2f>& points() const noexcept
    {
        return points_;
    }

private:
    void fail(status value) noexcept;

    std::vector<point_2f> points_;
    status status_ = status_code::ok;
    bool figure_open_ = false;
    bool capture_ = false;
    bool filled_figure_seen_ = false;
    bool supported_ = true;
};

/* Collects the closed filled polygons of a simplified line stream (Outline
 * and CombineWithGeometry output). */
class polygon_contours_collector final : public simplified_sink {
public:
    void set_fill_mode(fill_mode value) noexcept override;
    void set_segment_flags(path_segment value) noexcept override;
    void begin_figure(point_2f start, figure_begin begin) noexcept override;
    void add_lines(
        const point_2f* points,
        std::uint32_t point_count) noexcept override;
    void add_beziers(
        const cubic_bezier_segment_f* beziers,
        std::uint32_t bezier_count) noexcept override;
    void end_figure(figure_end end) noexcept override;
    [[nodiscard]] status close() noexcept override;

    [[nodiscard]] status result() const noexcept;
    [[nodiscard]] std::vector<std::vector<point_2f>> take_contours() noexcept
    {
        return std::move(contours_);
    }

private:
    void fail(status value) noexcept;

    std::vector<std::vector<point_2f>> contours_;
    status status_ = status_code::ok;
    bool figure_open_ = false;
    bool capture_ = false;
};

/* Synchronous Widen output collector: no retained point/segment arrays and no
 * second path recording. Lines use the shared SIMD bounds kernel; cubic roots
 * use the same analytic evaluator as ordinary path GetBounds. Current-point
 * and figure state make segment traversal sequential. */
class widened_bounds_collector final : public simplified_sink {
public:
    void set_fill_mode(fill_mode value) noexcept override;
    void set_segment_flags(path_segment value) noexcept override;
    void begin_figure(point_2f point, figure_begin begin) noexcept override;
    void add_lines(
        const point_2f* points,
        std::uint32_t count) noexcept override;
    void add_beziers(
        const cubic_bezier_segment_f* curves,
        std::uint32_t count) noexcept override;
    void end_figure(figure_end end) noexcept override;
    [[nodiscard]] status close() noexcept override;

    [[nodiscard]] rectangle_edges_f bounds() const noexcept { return bounds_; }
    [[nodiscard]] bool has_outline() const noexcept { return has_outline_; }

private:
    static void unite(rectangle_edges_f& a, const rectangle_edges_f& b) noexcept;

    status result_ = status_code::ok;
    rectangle_edges_f bounds_{}, figure_bounds_{};
    point_2f current_{};
    bool open_{}, has_segments_{}, has_outline_{};
};

/* Operations over a recorded path. Each returns wrong_state for a path that is
 * not closed, after the output pointer checks of the original method. */

[[nodiscard]] bool path_closed(const path_data& data) noexcept;

[[nodiscard]] status path_bounds(
    const path_data& data,
    const matrix_3x2_f* world_transform,
    rectangle_edges_f* bounds) noexcept;

/* style may be null (default flat solid stroke). */
[[nodiscard]] status path_widened_bounds(
    const path_data& data,
    float stroke_width,
    const stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    rectangle_edges_f* bounds) noexcept;

[[nodiscard]] status path_stroke_contains_point(
    const path_data& data,
    point_2f point,
    float stroke_width,
    const stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::int32_t* contains) noexcept;

[[nodiscard]] status path_fill_contains_point(
    const path_data& data,
    point_2f point,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::int32_t* contains) noexcept;

/* lines_only selects the lines simplification (cubics_and_lines otherwise). */
[[nodiscard]] status path_simplify(
    const path_data& data,
    bool lines_only,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    simplified_sink* sink) noexcept;

/* Triangles of the outline; triangles changes only on success. */
[[nodiscard]] status path_tessellate(
    const path_data& data,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::vector<triangle_f>& triangles) noexcept;

[[nodiscard]] status path_outline(
    const path_data& data,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    simplified_sink* sink) noexcept;

/* path_outline into a polygon_contours_collector. Output changes only on
 * success. */
[[nodiscard]] status path_outline_contours(
    const path_data& data,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    std::vector<std::vector<point_2f>>& contours) noexcept;

[[nodiscard]] status path_compute_area(
    const path_data& data,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    float* area) noexcept;

[[nodiscard]] status path_compute_length(
    const path_data& data,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    float* length) noexcept;

[[nodiscard]] status path_compute_point_at_length(
    const path_data& data,
    float length,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    point_2f* point,
    point_2f* unit_tangent) noexcept;

[[nodiscard]] status path_widen(
    const path_data& data,
    float stroke_width,
    const stroke_style* style,
    const matrix_3x2_f* world_transform,
    float flattening_tolerance,
    simplified_sink* sink) noexcept;

/* The Boolean combination of two operands' outline contours (the body of
 * CombineWithGeometry after both outlines), emitted as closed line figures. */
[[nodiscard]] status combine_outline_contours(
    std::vector<std::vector<point_2f>> first,
    std::vector<std::vector<point_2f>> second,
    combine_mode mode,
    simplified_sink& sink) noexcept;

/* CombineWithGeometry with both operands recorded paths. */
[[nodiscard]] status path_combine(
    const path_data& first,
    const path_data& second,
    combine_mode mode,
    const matrix_3x2_f* second_transform,
    float flattening_tolerance,
    simplified_sink* sink) noexcept;

// Original ProGPU CompareWithGeometry topology, shared by COM and synchronous
// fill queries. Callers normalize each operand once; empty-fill policy belongs
// to the calling contract. Ordered arrangement/stitching retains its shared
// SIMD edge kernels and bounded workspace, not a bounds or point surrogate.
// May throw std::bad_alloc.
[[nodiscard]] status compare_normalized_fill_contours(
    const std::vector<std::vector<point_2f>>& first,
    const std::vector<std::vector<point_2f>>& second,
    geometry_relation* relation);

/* CompareWithGeometry with both operands recorded paths. */
[[nodiscard]] status path_compare(
    const path_data& first,
    const path_data& second,
    const matrix_3x2_f* second_transform,
    float flattening_tolerance,
    geometry_relation* relation) noexcept;

/* Canonical segment transport. */

/* Emits canonical native fill or stroke segments as figures into sink (the
 * figure/segment conversion of the native fill and stroke geometries). Full
 * ellipses split into two endpoint arcs; no flattening is introduced here.
 * Fill: a segment that does not start at the previous end begins a new
 * figure, and returning to the start closes the figure. Stroke: one contour,
 * joins describe segment i -> i+1 including the closing seam, and
 * disconnected segments are rejected. Arguments are validated by
 * valid_native_path_arguments; the sink is not closed. */
[[nodiscard]] status write_native_path(
    std::span<const progpu_native_path_segment> segments,
    fill_mode mode,
    std::span<const std::uint8_t> smooth_joins,
    bool filled,
    bool closed,
    path_sink& sink) noexcept;

[[nodiscard]] bool valid_native_path_arguments(
    std::span<const progpu_native_path_segment> segments,
    fill_mode mode,
    std::span<const std::uint8_t> smooth_joins,
    bool filled) noexcept;

/* Complete figure/segment query transport, including source gaps and explicit
 * incoming smooth joins. Arguments are validated by
 * valid_native_query_arguments; the sink is not closed. */
[[nodiscard]] status write_native_query_path(
    std::span<const progpu_native_geometry_query_figure> figures,
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> flags,
    path_sink& sink) noexcept;

[[nodiscard]] bool valid_native_query_arguments(
    std::span<const progpu_native_geometry_query_figure> figures,
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> flags) noexcept;

/* Closed recorded paths of the canonical transports; output changes only on
 * success. */
[[nodiscard]] status build_native_fill_path(
    std::span<const progpu_native_path_segment> segments,
    fill_mode mode,
    std::shared_ptr<path_data>& path) noexcept;

[[nodiscard]] status build_native_stroke_path(
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> smooth_joins,
    bool closed,
    std::shared_ptr<path_data>& path) noexcept;

[[nodiscard]] status build_native_query_path(
    std::span<const progpu_native_geometry_query_figure> figures,
    std::span<const progpu_native_path_segment> segments,
    std::span<const std::uint8_t> flags,
    std::shared_ptr<path_data>& path) noexcept;

/* Composite queries of the C ABI and the Direct2D bridge. */

// Bounds of emitted stroke coverage only, unlike path_widened_bounds which may
// include original path extents. Does not materialize a second path. Empty
// output is explicit; both outputs change only on success.
[[nodiscard]] status widened_outline_bounds(
    const path_data& source,
    float width,
    const stroke_style* style,
    const matrix_3x2_f* transform,
    float tolerance,
    rectangle_edges_f& bounds,
    bool& has_outline) noexcept;

// Exports a closed recorded outline (for example Widen output recorded by a
// path_recorder) as canonical line and cubic segments of closed filled
// figures. Output changes only on success.
[[nodiscard]] status export_outline_segments(
    const path_data& outline,
    float tolerance,
    std::vector<progpu_native_path_segment>& segments,
    fill_mode& mode) noexcept;

// Retained filled stroke outline for downstream deformation. Preserve curves
// and closed contours; output changes only on success. No pixel work or a
// second flattening pass is performed by this exporter.
[[nodiscard]] status widened_outline_segments(
    const path_data& source,
    float width,
    const stroke_style* style,
    const matrix_3x2_f* transform,
    float tolerance,
    std::vector<progpu_native_path_segment>& segments,
    fill_mode& mode) noexcept;

// Returns the actual filled boundary. Output changes only on success;
// tolerance is in the path's coordinates.
[[nodiscard]] status extract_outline_contours(
    const path_data& source,
    float tolerance,
    std::vector<std::vector<point_2f>>& contours) noexcept;

// Device-independent boolean boundary of two canonical fill operands, the
// same implementation as Direct2D and MIL combined strokes. Output changes
// only on success.
[[nodiscard]] status combine_native_fill_contours(
    std::span<const progpu_native_path_segment> first, fill_mode first_fill,
    std::span<const progpu_native_path_segment> second, fill_mode second_fill,
    combine_mode mode, float tolerance,
    std::vector<std::vector<point_2f>>& contours) noexcept;

// Same normalized-boundary relation as CompareWithGeometry, but an empty
// filled operand is disjoint. Equal nonempty coverage is is_contained.
[[nodiscard]] status compare_native_fill_contours(
    std::span<const progpu_native_path_segment> first, fill_mode first_fill,
    std::span<const progpu_native_path_segment> second, fill_mode second_fill,
    float tolerance, geometry_relation& relation) noexcept;

} // namespace progpu::native::geometry

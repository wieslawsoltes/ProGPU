#include "progpu_native_direct2d_prepared_glyphs.hpp"
#include "progpu_native_direct2d_path.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <map>
#include <numeric>
#include <queue>

#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#elif defined(__wasm_simd128__)
#include <wasm_simd128.h>
#endif

namespace progpu::native::direct2d {
namespace {
using namespace compat;
constexpr std::size_t maximum_vertices = 1U << 20U;

bool equal(point_2f a, point_2f b) noexcept { return a.x == b.x && a.y == b.y; }
point_2f point(progpu_native_point p) noexcept { return {p.x, p.y}; }
point_2f end(const progpu_native_path_segment& segment) noexcept {
    return point(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE ? segment.p1 :
        segment.kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC ? segment.p2 : segment.p3);
}
bool finite(point_2f p) noexcept { return std::isfinite(p.x) && std::isfinite(p.y); }
void include(rectangle_f& bounds, point_2f p) noexcept {
    bounds.left = std::min(bounds.left, p.x); bounds.top = std::min(bounds.top, p.y);
    bounds.right = std::max(bounds.right, p.x); bounds.bottom = std::max(bounds.bottom, p.y);
}

// Two independent coordinates use native SIMD. The quotient is binary32, as
// in the observed source cache; rounding is halfway away from zero. SSE2 widens
// before adding .5 so a value immediately below a tie cannot round into it.
bool quantize(point_2f& p, point_2f origin, float unit) noexcept {
    alignas(16) float values[4]{(p.x - origin.x) / unit, (p.y - origin.y) / unit, 0, 0};
    if (!std::isfinite(values[0]) || !std::isfinite(values[1]) || values[0] < 0 || values[1] < 0 ||
        values[0] > 4194304.F || values[1] > 4194304.F) return false;
#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
    vst1q_f32(values, vrndaq_f32(vld1q_f32(values)));
#elif defined(__SSE2__) || defined(_M_X64)
    const auto rounded = _mm_cvttpd_epi32(_mm_add_pd(_mm_cvtps_pd(_mm_load_ps(values)), _mm_set1_pd(.5)));
    _mm_store_ps(values, _mm_cvtepi32_ps(rounded));
#elif defined(__wasm_simd128__)
    const auto wide = wasm_f64x2_promote_low_f32x4(wasm_v128_load(values));
    wasm_v128_store(values, wasm_f32x4_demote_f64x2_zero(wasm_f64x2_floor(
        wasm_f64x2_add(wide, wasm_f64x2_splat(.5)))));
#else
    values[0] = std::round(values[0]); values[1] = std::round(values[1]);
#endif
    p = {origin.x + values[0] * unit, origin.y + values[1] * unit};
    return finite(p);
}

// The canonical ProGPU path Simplify owns subdivision. This sink only retains
// its ordered line endpoints; it never substitutes control-point quantization.
class contour_sink final : public simplified_geometry_sink {
public:
    std::vector<point_2f> points;
    com::result status = com::ok;
    com::result PROGPU_NATIVE_COM_CALL QueryInterface(const com::guid&, void** out) noexcept override {
        if (!out) return com::pointer_error;
        *out = nullptr; return com::no_interface;
    }
    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef() noexcept override { return 1U; }
    com::reference_count_value PROGPU_NATIVE_COM_CALL Release() noexcept override { return 1U; }
    void PROGPU_NATIVE_COM_CALL SetFillMode(fill_mode) noexcept override {}
    void PROGPU_NATIVE_COM_CALL SetSegmentFlags(path_segment) noexcept override {}
    void PROGPU_NATIVE_COM_CALL BeginFigure(point_2f p, figure_begin begin) noexcept override {
        if (begun_ || begin != figure_begin::filled) { status = com::invalid_argument; return; }
        begun_ = true; append(p);
    }
    void PROGPU_NATIVE_COM_CALL AddLines(const point_2f* p, std::uint32_t n) noexcept override {
        if (!begun_ || closed_ || (n && !p)) { status = com::invalid_argument; return; }
        for (std::uint32_t i = 0; i < n && com::succeeded(status); ++i) append(p[i]);
    }
    void PROGPU_NATIVE_COM_CALL AddBeziers(const bezier_segment*, std::uint32_t) noexcept override {
        status = com::invalid_argument;
    }
    void PROGPU_NATIVE_COM_CALL EndFigure(figure_end value) noexcept override {
        if (!begun_ || closed_ || value != figure_end::closed) status = com::invalid_argument;
        closed_ = true;
    }
    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override {
        return com::failed(status) ? status : begun_ && closed_ ? com::ok : com::invalid_argument;
    }
private:
    bool begun_ = false, closed_ = false;
    void append(point_2f p) noexcept {
        if (com::failed(status)) return;
        if (!finite(p)) { status = com::invalid_argument; return; }
        if (points.size() == maximum_vertices) { status = com::out_of_memory; return; }
        try { points.push_back(p); }
        catch (...) { status = com::out_of_memory; }
    }
};

struct contour final {
    com::pointer<path_geometry> path;
    rectangle_f hull{}, target_bounds{};
};

// A sweep over disjoint active Y intervals detects the first strict overlap.
// The active intervals are disjoint until rejection, so only two neighbours
// need testing. O(C log C) time/O(C) storage, including long thin contours.
bool disjoint(std::span<const contour> contours) {
    std::vector<std::size_t> order(contours.size());
    std::iota(order.begin(), order.end(), 0U);
    std::sort(order.begin(), order.end(), [&](auto a, auto b) {
        return contours[a].target_bounds.left < contours[b].target_bounds.left;
    });
    using key = std::pair<float, std::size_t>;
    std::map<key, float> vertical;
    std::priority_queue<key, std::vector<key>, std::greater<key>> right_edges;
    for (auto i : order) {
        const auto box = contours[i].target_bounds;
        while (!right_edges.empty() && right_edges.top().first <= box.left) {
            const auto retired = right_edges.top().second;
            vertical.erase({contours[retired].target_bounds.top, retired}); right_edges.pop();
        }
        if (box.right <= box.left || box.bottom <= box.top) continue;
        const auto after = vertical.lower_bound({box.top, 0U});
        if (after != vertical.end() && after->first.first < box.bottom) return false;
        if (after != vertical.begin() && std::prev(after)->second > box.top) return false;
        vertical.emplace(key{box.top, i}, box.bottom); right_edges.emplace(box.right, i);
    }
    return true;
}

// Original ProGPU-owned diagnostic provenance: glyph-cache-policy222-227,
// independent_alias_gpu.hpp. Project the point and coverage direction together
// with SIMD, normalise in physical pixels, then return target DIPs. Translation
// applies only to the point. Paint coordinates use that same target DIP frame.
bool vertex(point_2f p, point_2f direction, float coverage, const matrix_3x2_f& physical,
    point_2f dpi, progpu_native_scene_mesh_vertex& output) noexcept {
    alignas(16) float x[]{p.x, p.x, direction.x, direction.x};
    alignas(16) float y[]{p.y, p.y, direction.y, direction.y};
    alignas(16) float a[]{physical.m11, physical.m12, physical.m11, physical.m12};
    alignas(16) float b[]{physical.m21, physical.m22, physical.m21, physical.m22};
    alignas(16) float t[]{physical.m31, physical.m32, 0, 0}, result[4];
#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
    vst1q_f32(result, vaddq_f32(vaddq_f32(vmulq_f32(vld1q_f32(x), vld1q_f32(a)),
        vmulq_f32(vld1q_f32(y), vld1q_f32(b))), vld1q_f32(t)));
#elif defined(__SSE2__) || defined(_M_X64)
    _mm_store_ps(result, _mm_add_ps(_mm_add_ps(_mm_mul_ps(_mm_load_ps(x), _mm_load_ps(a)),
        _mm_mul_ps(_mm_load_ps(y), _mm_load_ps(b))), _mm_load_ps(t)));
#elif defined(__wasm_simd128__)
    wasm_v128_store(result, wasm_f32x4_add(wasm_f32x4_add(wasm_f32x4_mul(wasm_v128_load(x), wasm_v128_load(a)),
        wasm_f32x4_mul(wasm_v128_load(y), wasm_v128_load(b))), wasm_v128_load(t)));
#else
    for (std::size_t lane = 0; lane < 4; ++lane) result[lane] = x[lane] * a[lane] + y[lane] * b[lane] + t[lane];
#endif
    if (direction.x != 0 || direction.y != 0) {
        const float length = std::sqrt(result[2] * result[2] + result[3] * result[3]);
        if (!std::isfinite(length) || length == 0) return false;
        result[0] += result[2] / length * .5F; result[1] += result[3] / length * .5F;
    }
    const progpu_native_point target{result[0] / dpi.x, result[1] / dpi.y};
    if (!std::isfinite(target.x) || !std::isfinite(target.y)) return false;
    output = {target, target, {1, 1, 1, coverage}};
    return true;
}
} // namespace

com::result prepared_original_glyph_run::prepare_coverage(factory* owner,
    prepared_original_glyph_coverage& output) const noexcept {
    if (!owner || !request_ || occurrences_.size() != request_->glyphs.count()) return com::invalid_argument;
    const auto& original = *request_;
    const auto& target = original.target;
    if (target.antialias != text_antialias_mode::aliased && target.antialias != text_antialias_mode::grayscale)
        return com::false_result;
    const point_2f dpi{target.dpi_x / 96.F, target.dpi_y / 96.F};
    if (!finite(dpi) || dpi.x <= 0 || dpi.y <= 0) return com::invalid_argument;
    const auto& m = target.transform;
    const matrix_3x2_f physical{m.m11 * dpi.x, m.m12 * dpi.y, m.m21 * dpi.x,
        m.m22 * dpi.y, m.m31 * dpi.x, m.m32 * dpi.y};
    if (!core::valid_transform(&physical)) return com::false_result;
    // Exact singular rejection is separate from the source geometry's own
    // acceptance. No epsilon, inverse, nominal DPI or allocation extent enters.
    if (static_cast<double>(physical.m11) * physical.m22 ==
        static_cast<double>(physical.m12) * physical.m21) return com::false_result;
    const float a = physical.m11 * physical.m11 + physical.m12 * physical.m12;
    const float b = physical.m21 * physical.m21 + physical.m22 * physical.m22;
    const float c = physical.m11 * physical.m21 + physical.m12 * physical.m22;
    const float extent = original.em_size * std::sqrt((a + b + std::sqrt((a-b)*(a-b) + 4*c*c)) * .5F);
    if (!std::isfinite(extent) || extent <= 0) return com::false_result;
    float canonical = 75.F;
    while (canonical < extent) { canonical *= 2; if (!std::isfinite(canonical)) return com::false_result; }
    while (canonical * .5F >= extent) canonical *= .5F;
    const float ratio = original.em_size / canonical, unit = ratio * .125F, tolerance = ratio * .25F;
    if (!std::isfinite(ratio) || unit <= 0 || tolerance <= 0) return com::false_result;
    try {
        std::vector<contour> contours;
        std::size_t next = 0U;
        for (const auto& occurrence : occurrences_) {
            if (occurrence.first_segment != next || occurrence.segment_count > segments_.size() - next)
                return com::invalid_argument;
            const auto stop = next + occurrence.segment_count;
            while (next < stop) {
                const auto first = next;
                const auto start = point(segments_[first].p0);
                point_2f previous = start;
                rectangle_f hull{start.x, start.y, start.x, start.y};
                do {
                    const auto& segment = segments_[next];
                    if (segment.kind > PROGPU_NATIVE_PATH_SEGMENT_CUBIC || !equal(point(segment.p0), previous))
                        return com::false_result;
                    include(hull, point(segment.p1));
                    if (segment.kind != PROGPU_NATIVE_PATH_SEGMENT_LINE) include(hull, point(segment.p2));
                    if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) include(hull, point(segment.p3));
                    previous = end(segment); ++next;
                } while (next < stop && !equal(previous, start));
                if (!equal(previous, start)) return com::false_result;
                contour item; item.hull = hull;
                auto status = compat::detail::create_native_fill_geometry(owner,
                    std::span(segments_).subspan(first, next-first), fill_mode::winding, item.path.put());
                if (com::failed(status)) return status;
                status = item.path->GetBounds(&physical, &item.target_bounds);
                if (com::failed(status)) return status;
                contours.push_back(std::move(item));
            }
        }
        if (next != segments_.size()) return com::invalid_argument;
        if (!disjoint(contours)) return com::false_result;
        prepared_original_glyph_coverage candidate;
        const bool aliased = target.antialias == text_antialias_mode::aliased;
        rectangle_f bounds{};
        bool have_bounds = false;
        for (const auto& contour : contours) {
            contour_sink sink;
            auto status = contour.path->Simplify(geometry_simplification_option::lines, nullptr, tolerance, &sink);
            if (com::succeeded(status)) status = sink.Close();
            if (com::failed(status)) return status;
            auto& points = sink.points;
            for (auto& p : points) if (!quantize(p, {contour.hull.left, contour.hull.top}, unit)) return com::false_result;
            points.erase(std::unique(points.begin(), points.end(), equal), points.end());
            if (points.size() > 1U && equal(points.front(), points.back())) points.pop_back();
            if (points.size() < 3U) return com::false_result;
            double direction = 0;
            for (std::size_t i = 0; i < points.size(); ++i) {
                const auto p = points[i], q = points[(i+1)%points.size()], r = points[(i+2)%points.size()];
                const double cross = (double(q.x)-p.x)*(double(r.y)-q.y) - (double(q.y)-p.y)*(double(r.x)-q.x);
                if (cross != 0) {
                    if (direction != 0 && (cross > 0) != (direction > 0)) return com::false_result;
                    direction = cross;
                }
            }
            if (direction == 0) return com::false_result;
            const auto n = points.size();
            // Consistent local turns alone also accept multiply wound stars.
            // Count one complete tangent rotation, rejecting exact reversals.
            unsigned rotations = 0U;
            const auto upper = [](point_2f v) { return v.y > 0 || (v.y == 0 && v.x >= 0); };
            for (std::size_t i = 0; i < n; ++i) {
                const auto p = points[i], q = points[(i+1)%n], r = points[(i+2)%n];
                const point_2f first{q.x-p.x, q.y-p.y}, second{r.x-q.x, r.y-q.y};
                const double cross = double(first.x)*second.y - double(first.y)*second.x;
                if (cross == 0 && double(first.x)*second.x + double(first.y)*second.y <= 0)
                    return com::false_result;
                if (direction > 0 ? (!upper(first) && upper(second)) : (upper(first) && !upper(second))) ++rotations;
            }
            if (rotations != 1U) return com::false_result;
            const auto required = (n-2U)*3U + (aliased ? 0U : n*9U);
            if (required > maximum_vertices - candidate.vertices.size()) return com::out_of_memory;
            std::vector<point_2f> normals(n);
            std::vector<progpu_native_scene_mesh_vertex> inner(n), before(aliased ? 0U : n), after(aliased ? 0U : n);
            if (!aliased) for (std::size_t i = 0; i < n; ++i) {
                const auto p = points[i], q = points[(i+1)%n];
                const float dx = q.x-p.x, dy = q.y-p.y, length = std::sqrt(dx*dx+dy*dy);
                if (!std::isfinite(length) || length == 0) return com::false_result;
                const float sign = direction > 0 ? 1.F : -1.F;
                normals[i] = {dy/length*sign, -dx/length*sign};
            }
            for (std::size_t i = 0; i < n; ++i) {
                const auto prev = normals[(i+n-1)%n], next_normal = normals[i];
                if (!vertex(points[i], aliased ? point_2f{} : point_2f{-prev.x-next_normal.x, -prev.y-next_normal.y},
                    1, physical, dpi, inner[i])) return com::false_result;
                if (!aliased && (!vertex(points[i], prev, 0, physical, dpi, before[i]) ||
                    !vertex(points[i], next_normal, 0, physical, dpi, after[i]))) return com::false_result;
            }
            // A subpixel contour can collapse or reverse its inset edges.
            // Such coverage needs a separate topology contract, not overlapping
            // fan triangles. Keep the original general path without an epsilon.
            if (!aliased) for (std::size_t i = 0; i < n; ++i) {
                const auto j = (i+1)%n;
                const double dx = double(points[j].x)-points[i].x, dy = double(points[j].y)-points[i].y;
                const double x = dx*physical.m11 + dy*physical.m21, y = dx*physical.m12 + dy*physical.m22;
                const double ix = (double(inner[j].position.x)-inner[i].position.x)*dpi.x;
                const double iy = (double(inner[j].position.y)-inner[i].position.y)*dpi.y;
                if (ix*x + iy*y <= 0) return com::false_result;
            }
            progpu_native_scene_vertex_mesh mesh{};
            mesh.struct_size = sizeof(mesh); mesh.flags = PROGPU_NATIVE_VERTEX_MESH_EDGE_ALIASED;
            mesh.topology = PROGPU_NATIVE_VERTEX_MESH_TRIANGLES;
            // Mesh colors use the existing SkBlendMode-compatible wire values,
            // not progpu_native_blend_mode's layer-composite numbering.
            mesh.color_blend_mode = 5U; // Brush source-in vertex coverage.
            mesh.vertex_offset = static_cast<std::uint32_t>(candidate.vertices.size());
            mesh.vertex_count = static_cast<std::uint32_t>(required);
            mesh.transform = {1, 0, 0, 1, 0, 0};
            const auto needed = candidate.vertices.size() + required;
            if (needed > candidate.vertices.capacity()) candidate.vertices.reserve(std::max(needed,
                std::min(maximum_vertices, std::max(std::size_t{64U}, candidate.vertices.capacity()*2U))));
            const auto triangle = [&](auto p, auto q, auto r) {
                candidate.vertices.push_back(p); candidate.vertices.push_back(q); candidate.vertices.push_back(r);
            };
            for (std::size_t i = 1; i+1 < n; ++i) triangle(inner[0], inner[i], inner[i+1]);
            if (!aliased) for (std::size_t i = 0; i < n; ++i) {
                const auto j = (i+1)%n;
                triangle(inner[i], after[i], inner[j]); triangle(after[i], before[j], inner[j]);
                triangle(inner[i], before[i], after[i]);
            }
            candidate.meshes.push_back(mesh);
        }
        for (const auto& item : candidate.vertices) {
            const auto p = point(item.position);
            if (!have_bounds) { bounds = {p.x, p.y, p.x, p.y}; have_bounds = true; }
            else include(bounds, p);
        }
        candidate.bounds = {bounds.left, bounds.top, bounds.right-bounds.left, bounds.bottom-bounds.top};
        if (!std::isfinite(candidate.bounds.width) || !std::isfinite(candidate.bounds.height)) return com::false_result;
        output = std::move(candidate);
        return com::ok;
    } catch (const std::bad_alloc&) { return com::out_of_memory; }
    catch (...) { return com::invalid_argument; }
}
} // namespace progpu::native::direct2d

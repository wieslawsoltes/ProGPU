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
// Separation in either original-control or physical bounds is sufficient:
// the common physical mapping is already proven exactly invertible. Emitted
// AA fringes remain separate draws and may overlap, as in the original source.
bool disjoint(std::span<const contour> contours, rectangle_f contour::*member) {
    std::vector<std::size_t> order(contours.size());
    std::iota(order.begin(), order.end(), 0U);
    std::sort(order.begin(), order.end(), [&](auto a, auto b) {
        return (contours[a].*member).left < (contours[b].*member).left;
    });
    using key = std::pair<float, std::size_t>;
    std::map<key, float> vertical;
    std::priority_queue<key, std::vector<key>, std::greater<key>> right_edges;
    for (auto i : order) {
        const auto box = contours[i].*member;
        while (!right_edges.empty() && right_edges.top().first <= box.left) {
            const auto retired = right_edges.top().second;
            vertical.erase({(contours[retired].*member).top, retired}); right_edges.pop();
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
// with SIMD and normalise in physical pixels. Retain physical raster positions
// beside target-DIP paint coordinates; translation applies only to the point.
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
    output = {{result[0], result[1]}, target, {1, 1, 1, coverage}};
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
        // Original grayscale rendering switches to whole-path rasterization
        // when physical contour bounds overlap, even if the outlines remain
        // separated under a shear. Aliased separated contours retain their
        // triangle coverage; AA fringes do not establish this policy boundary.
        if (!disjoint(contours, &contour::target_bounds) &&
            (target.antialias != text_antialias_mode::aliased || !disjoint(contours, &contour::hull)))
            return com::false_result;
        prepared_original_glyph_coverage candidate;
        candidate.frame = {sizeof(candidate.frame), 1U, dpi.x, dpi.y, target.pixels.width, target.pixels.height, 0U, 0U};
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
                const double ix = (double(inner[j].position.x)-inner[i].position.x);
                const double iy = (double(inner[j].position.y)-inner[i].position.y);
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
                // Canonical contour orientation owns the edge-strip diagonal.
                // A shear makes (x,y,coverage) nonplanar across this quad, so
                // choosing a diagonal from input traversal changes pixels.
                // Reversed original contours must retain the same triangles.
                if (direction < 0) {
                    triangle(inner[i], after[i], before[j]); triangle(inner[i], before[j], inner[j]);
                } else {
                    triangle(inner[i], after[i], inner[j]); triangle(after[i], before[j], inner[j]);
                }
                triangle(inner[i], before[i], after[i]);
            }
            candidate.meshes.push_back(mesh);
        }
        for (const auto& item : candidate.vertices) {
            const auto p = point(item.texture_coordinate);
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

namespace {
// Original target transform arithmetic uses independent SIMD float lanes. The
// physical coordinate is classified once after actual per-axis DPI projection.
// Range checks precede conversion; ceil retains negative coordinates correctly.
bool source_path_pair(progpu_native_point& p,progpu_native_point& q,
    const compat::matrix_3x2_f& m,const progpu_native_scene_source_coverage_frame& frame) noexcept {
    alignas(16) float v[]{p.x,p.y,q.x,q.y};
    alignas(16) const float a[]{m.m11,m.m12,m.m11,m.m12},b[]{m.m21,m.m22,m.m21,m.m22};
    alignas(16) const float c[]{m.m31,m.m32,m.m31,m.m32},scale[]{frame.dpi_scale_x,frame.dpi_scale_y,frame.dpi_scale_x,frame.dpi_scale_y};
#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
    const auto input=vld1q_f32(v);
    const auto x=vcombine_f32(vdup_lane_f32(vget_low_f32(input),0),vdup_lane_f32(vget_high_f32(input),0));
    const auto y=vcombine_f32(vdup_lane_f32(vget_low_f32(input),1),vdup_lane_f32(vget_high_f32(input),1));
    vst1q_f32(v,vmulq_f32(vaddq_f32(vaddq_f32(vmulq_f32(x,vld1q_f32(a)),vmulq_f32(y,vld1q_f32(b))),vld1q_f32(c)),vld1q_f32(scale)));
#elif defined(__SSE2__) || defined(_M_X64)
    const auto input=_mm_load_ps(v);
    const auto x=_mm_shuffle_ps(input,input,_MM_SHUFFLE(2,2,0,0)),y=_mm_shuffle_ps(input,input,_MM_SHUFFLE(3,3,1,1));
    _mm_store_ps(v,_mm_mul_ps(_mm_add_ps(_mm_add_ps(_mm_mul_ps(x,_mm_load_ps(a)),_mm_mul_ps(y,_mm_load_ps(b))),_mm_load_ps(c)),_mm_load_ps(scale)));
#elif defined(__wasm_simd128__)
    const auto input=wasm_v128_load(v),x=wasm_i32x4_shuffle(input,input,0,0,2,2),y=wasm_i32x4_shuffle(input,input,1,1,3,3);
    wasm_v128_store(v,wasm_f32x4_mul(wasm_f32x4_add(wasm_f32x4_add(wasm_f32x4_mul(x,wasm_v128_load(a)),wasm_f32x4_mul(y,wasm_v128_load(b))),wasm_v128_load(c)),wasm_v128_load(scale)));
#else
    const float x[]{p.x,p.x,q.x,q.x},y[]{p.y,p.y,q.y,q.y};
    for(unsigned lane=0;lane<4;++lane)v[lane]=(x[lane]*a[lane]+y[lane]*b[lane]+c[lane])*scale[lane];
#endif
    for(float value:v)if(!std::isfinite(value)||std::abs(value)>16384.F)return false;
#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
    vst1q_f32(v,vmulq_n_f32(vrndpq_f32(vmulq_n_f32(vld1q_f32(v),16.F)),.0625F));
#elif defined(__SSE2__) || defined(_M_X64)
    const auto values=_mm_mul_ps(_mm_load_ps(v),_mm_set1_ps(16.F));
    const auto truncated=_mm_cvtepi32_ps(_mm_cvttps_epi32(values));
    _mm_store_ps(v,_mm_mul_ps(_mm_add_ps(truncated,_mm_and_ps(_mm_cmplt_ps(truncated,values),_mm_set1_ps(1.F))),_mm_set1_ps(.0625F)));
#elif defined(__wasm_simd128__)
    wasm_v128_store(v,wasm_f32x4_mul(wasm_f32x4_ceil(wasm_f32x4_mul(wasm_v128_load(v),wasm_f32x4_splat(16.F))),wasm_f32x4_splat(.0625F)));
#else
    for(float& value:v)value=std::ceil(value*16.F)*.0625F;
#endif
    p={v[0],v[1]};q={v[2],v[3]};return true;
}
using source_axis_pair=std::array<double,2>;
source_axis_pair source_weighted_points(const std::array<source_axis_pair,4>& p,
    const std::array<double,4>& weights) noexcept {
    source_axis_pair result{};
#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
    auto v=vdupq_n_f64(0.);
    for(unsigned i=0;i<4;++i)v=vaddq_f64(v,vmulq_n_f64(vld1q_f64(p[i].data()),weights[i]));
    vst1q_f64(result.data(),v);
#elif defined(__SSE2__) || defined(_M_X64)
    auto v=_mm_setzero_pd();
    for(unsigned i=0;i<4;++i)v=_mm_add_pd(v,_mm_mul_pd(_mm_loadu_pd(p[i].data()),_mm_set1_pd(weights[i])));
    _mm_storeu_pd(result.data(),v);
#elif defined(__wasm_simd128__)
    auto v=wasm_f64x2_splat(0.);
    for(unsigned i=0;i<4;++i)v=wasm_f64x2_add(v,wasm_f64x2_mul(wasm_v128_load(p[i].data()),wasm_f64x2_splat(weights[i])));
    wasm_v128_store(result.data(),v);
#else
    for(unsigned i=0;i<4;++i)for(unsigned a=0;a<2;++a)result[a]+=p[i][a]*weights[i];
#endif
    return result;
}
progpu_native_point source_curve_point(source_axis_pair p) noexcept {
#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
    vst1q_f64(p.data(),vmulq_n_f64(vrndmq_f64(vaddq_f64(vmulq_n_f64(vld1q_f64(p.data()),16.),vdupq_n_f64(.5))),.0625));
#elif defined(__SSE2__) || defined(_M_X64)
    const auto v=_mm_add_pd(_mm_mul_pd(_mm_loadu_pd(p.data()),_mm_set1_pd(16.)),_mm_set1_pd(.5));
    const auto truncated=_mm_cvtepi32_pd(_mm_cvttpd_epi32(v));
    _mm_storeu_pd(p.data(),_mm_mul_pd(_mm_sub_pd(truncated,_mm_and_pd(_mm_cmpgt_pd(truncated,v),_mm_set1_pd(1.))),_mm_set1_pd(.0625)));
#elif defined(__wasm_simd128__)
    wasm_v128_store(p.data(),wasm_f64x2_mul(wasm_f64x2_floor(wasm_f64x2_add(wasm_f64x2_mul(wasm_v128_load(p.data()),wasm_f64x2_splat(16.)),wasm_f64x2_splat(.5))),wasm_f64x2_splat(.0625)));
#else
    for(double& v:p)v=std::floor(v*16.+.5)*.0625;
#endif
    return {static_cast<float>(p[0]),static_cast<float>(p[1])};
}
}

com::result prepare_original_path_coverage(std::span<const progpu_native_path_segment> source,
    const compat::matrix_3x2_f& transform,const progpu_native_scene_source_coverage_frame& frame,
    std::uint32_t fill_rule,prepared_original_path_coverage& output) noexcept {
    if(source.empty()||source.size()>1048576U||fill_rule>1U||frame.struct_size!=sizeof(frame)||frame.version!=1U||frame.flags||frame.reserved||
        frame.pixel_width==0U||frame.pixel_height==0U||frame.pixel_width>16384U||frame.pixel_height>16384U||
        !std::isfinite(frame.dpi_scale_x)||frame.dpi_scale_x<=0.F||!std::isfinite(frame.dpi_scale_y)||frame.dpi_scale_y<=0.F)return com::false_result;
    for(float value:{transform.m11,transform.m12,transform.m21,transform.m22,transform.m31,transform.m32})if(!std::isfinite(value))return com::false_result;
    if(double(transform.m11)*transform.m22-double(transform.m12)*transform.m21==0.)return com::false_result;
    try {
        prepared_original_path_coverage candidate;
        candidate.segments.reserve(std::min<std::size_t>(1048576U,source.size()*4U));
        auto& path=candidate.path;
        path.min_x=path.min_y=16384.F;path.max_x=path.max_y=-16384.F;
        path.transform={1,0,0,1,0,0};path.color={1,1,1,1};path.fill_rule=fill_rule;path.sample_grid=8U;
        const auto append=[&](progpu_native_point a,progpu_native_point b){
            if(candidate.segments.size()==1048576U)return false;
            candidate.segments.push_back({a,b,{},{},PROGPU_NATIVE_PATH_SEGMENT_LINE,0U,0U,0U});
            for(auto p:{a,b}){path.min_x=std::min(path.min_x,p.x);path.min_y=std::min(path.min_y,p.y);path.max_x=std::max(path.max_x,p.x);path.max_y=std::max(path.max_y,p.y);}
            return true;
        };
        bool open = false;
        progpu_native_point contour_start{}, contour_end{};
        const auto same_point = [](auto a, auto b) { return a.x == b.x && a.y == b.y; };
        for(auto segment:source) {
            if(segment.kind!=PROGPU_NATIVE_PATH_SEGMENT_LINE&&segment.kind!=PROGPU_NATIVE_PATH_SEGMENT_CUBIC)return com::false_result;
            // Validate original topology before any physical classification.
            // Two disconnected endpoints must not become a repaired contour
            // merely because they occupy the same quantized cell.
            if (segment.pad0 || segment.pad1 || segment.pad2) return com::false_result;
            if (!open) { contour_start = segment.p0; open = true; }
            else if (!same_point(segment.p0, contour_end)) return com::false_result;
            contour_end = segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE ? segment.p1 : segment.p3;
            if (same_point(contour_end, contour_start)) open = false;
            if(!source_path_pair(segment.p0,segment.p1,transform,frame))return com::false_result;
            if(segment.kind==PROGPU_NATIVE_PATH_SEGMENT_LINE){if(!append(segment.p0,segment.p1))return com::false_result;continue;}
            if(!source_path_pair(segment.p2,segment.p3,transform,frame))return com::false_result;
            const std::array<source_axis_pair,4> p{{{segment.p0.x,segment.p0.y},{segment.p1.x,segment.p1.y},{segment.p2.x,segment.p2.y},{segment.p3.x,segment.p3.y}}};
            const auto start=source_weighted_points(p,{6.,-12.,6.,0.}),end=source_weighted_points(p,{0.,6.,-12.,6.});
            const std::array<source_axis_pair,4> derivative{start,end,source_axis_pair{},source_axis_pair{}};
            // The ordered adaptive walk is data-dependent. SIMD computes both
            // original axes; dyadic interval selection remains scalar and exact.
            const auto fits=[&](double t,double h){
                const auto a=source_weighted_points(derivative,{1.-t,t,0.,0.});
                const auto b=source_weighted_points(derivative,{1.-t-h,t+h,0.,0.});
                return std::max({std::abs(a[0]),std::abs(a[1]),std::abs(b[0]),std::abs(b[1])})*h*h<=1.5;
            };
            double t=0.,h=1.;auto previous=segment.p0;
            while(t<1.){
                while(2.*h<=1.-t&&std::fmod(t,2.*h)==0.&&fits(t,2.*h))h*=2.;
                while(h>1.-t||!fits(t,h)){h*=.5;if(h<1./1048576.)return com::false_result;}
                t+=h;const double u=1.-t;
                const auto next=source_curve_point(source_weighted_points(p,{u*u*u,3.*u*u*t,3.*u*t*t,t*t*t}));
                if(!append(previous,next))return com::false_result;previous=next;
            }
        }
        if(open||path.min_x>=path.max_x||path.min_y>=path.max_y||
            std::ceil(path.max_x)-std::floor(path.min_x)+2.F>8192.F||std::ceil(path.max_y)-std::floor(path.min_y)+2.F>8192.F)return com::false_result;
        path.segment_count=candidate.segments.size();output=std::move(candidate);return com::ok;
    }catch(const std::bad_alloc&){return com::out_of_memory;}catch(...){return com::invalid_argument;}
}
} // namespace progpu::native::direct2d

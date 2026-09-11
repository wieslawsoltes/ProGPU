#include "progpu_native_scene_builder_internal.hpp"
#include "progpu_native_hit_testing.hpp"
#include "progpu_native_geometry_base.hpp"
#include "progpu_native_geometry_stroke.hpp"
#include "progpu_native_semantic_image.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>
#include <numbers>

#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

namespace progpu::native {
namespace {

// Canonical hit-test WGSL follows ProGPU.Vector.FillRule (EvenOdd=0,
// Nonzero=1), the reverse of the semantic scene path enum.
constexpr std::uint32_t hit_nonzero_fill_rule = 1U;

// Shared four-coordinate transform for bounds corners and polynomial clip controls.
bool transform_hit_coordinates(std::array<float, 4U>& x, std::array<float, 4U>& y,
    const progpu_native_affine_2d& transform) noexcept {
    if (!is_finite(transform)) return false;
#if defined(__aarch64__) || defined(_M_ARM64)
    const auto xs = vld1q_f32(x.data()), ys = vld1q_f32(y.data());
    const auto world_x = vaddq_f32(vaddq_f32(vmulq_n_f32(xs, transform.m11),
        vmulq_n_f32(ys, transform.m21)), vdupq_n_f32(transform.m31));
    const auto world_y = vaddq_f32(vaddq_f32(vmulq_n_f32(xs, transform.m12),
        vmulq_n_f32(ys, transform.m22)), vdupq_n_f32(transform.m32));
    vst1q_f32(x.data(), world_x); vst1q_f32(y.data(), world_y);
#elif defined(__SSE2__) || defined(_M_X64)
    const auto xs = _mm_loadu_ps(x.data()), ys = _mm_loadu_ps(y.data());
    const auto world_x = _mm_add_ps(_mm_add_ps(_mm_mul_ps(xs, _mm_set1_ps(transform.m11)),
        _mm_mul_ps(ys, _mm_set1_ps(transform.m21))), _mm_set1_ps(transform.m31));
    const auto world_y = _mm_add_ps(_mm_add_ps(_mm_mul_ps(xs, _mm_set1_ps(transform.m12)),
        _mm_mul_ps(ys, _mm_set1_ps(transform.m22))), _mm_set1_ps(transform.m32));
    _mm_storeu_ps(x.data(), world_x); _mm_storeu_ps(y.data(), world_y);
#else
    // No unqualified whole-scene scalar preprocessing on other architectures.
    return false;
#endif
    // Fixed four-lane reduction/finite validation, not a whole-buffer scalar path.
    for (std::size_t i = 0; i < 4U; ++i)
        if (!std::isfinite(x[i]) || !std::isfinite(y[i])) return false;
    return true;
}

// Native counterpart of ProGPU.Vector/GpuHitTesting.cs primitive encoding.
// Three independent affine rows, with translation added only to the origin row.
// Preserve compose_affine's multiply/add order without a whole-scene scalar loop.
bool compose_input_frame(const progpu_native_affine_2d& first,
    const progpu_native_affine_2d& second, progpu_native_affine_2d& result) noexcept {
    [[maybe_unused]] const std::array<float, 4U> x{first.m11, first.m21, first.m31, 0.0F};
    [[maybe_unused]] const std::array<float, 4U> y{first.m12, first.m22, first.m32, 0.0F};
    [[maybe_unused]] const std::array<float, 4U> tx{0.0F, 0.0F, second.m31, 0.0F};
    [[maybe_unused]] const std::array<float, 4U> ty{0.0F, 0.0F, second.m32, 0.0F};
    std::array<float, 4U> out_x{}, out_y{};
#if defined(__aarch64__) || defined(_M_ARM64)
    const auto xs = vld1q_f32(x.data()), ys = vld1q_f32(y.data());
    vst1q_f32(out_x.data(), vaddq_f32(vaddq_f32(vmulq_n_f32(xs, second.m11),
        vmulq_n_f32(ys, second.m21)), vld1q_f32(tx.data())));
    vst1q_f32(out_y.data(), vaddq_f32(vaddq_f32(vmulq_n_f32(xs, second.m12),
        vmulq_n_f32(ys, second.m22)), vld1q_f32(ty.data())));
#elif defined(__SSE2__) || defined(_M_X64)
    const auto xs = _mm_loadu_ps(x.data()), ys = _mm_loadu_ps(y.data());
    _mm_storeu_ps(out_x.data(), _mm_add_ps(_mm_add_ps(_mm_mul_ps(xs, _mm_set1_ps(second.m11)),
        _mm_mul_ps(ys, _mm_set1_ps(second.m21))), _mm_loadu_ps(tx.data())));
    _mm_storeu_ps(out_y.data(), _mm_add_ps(_mm_add_ps(_mm_mul_ps(xs, _mm_set1_ps(second.m12)),
        _mm_mul_ps(ys, _mm_set1_ps(second.m22))), _mm_loadu_ps(ty.data())));
#else
    return false;
#endif
    result = {out_x[0], out_y[0], out_x[1], out_y[1], out_x[2], out_y[2]};
    return is_finite(result);
}

// Bounds are broad phase only; local analytic/path data retains exact coverage.
bool place_primitive(progpu_native_hit_test_primitive& output,
    progpu_native_point minimum, progpu_native_point maximum,
    const progpu_native_affine_2d& transform) noexcept {
    if (!is_finite(transform)) return false;
    const double determinant = double{transform.m11} * transform.m22 -
        double{transform.m12} * transform.m21;
    if (!std::isfinite(determinant) || determinant == 0.0) return false;
    const double inverse = 1.0 / determinant;
    const double a = transform.m22 * inverse, b = -transform.m12 * inverse;
    const double c = -transform.m21 * inverse, d = transform.m11 * inverse;
    output.inverse_transform0 = {static_cast<float>(a), static_cast<float>(c),
        static_cast<float>(-(transform.m31 * a + transform.m32 * c)), 0.0F};
    output.inverse_transform1 = {static_cast<float>(b), static_cast<float>(d),
        static_cast<float>(-(transform.m31 * b + transform.m32 * d)), 0.0F};
    if (!std::isfinite(output.inverse_transform0.x) || !std::isfinite(output.inverse_transform0.y) ||
        !std::isfinite(output.inverse_transform0.z) || !std::isfinite(output.inverse_transform1.x) ||
        !std::isfinite(output.inverse_transform1.y) || !std::isfinite(output.inverse_transform1.z)) return false;
    std::array<float, 4U> x{minimum.x, maximum.x, maximum.x, minimum.x};
    std::array<float, 4U> y{minimum.y, minimum.y, maximum.y, maximum.y};
    if (!transform_hit_coordinates(x, y, transform)) return false;
    output.bounds_min = {*std::min_element(x.begin(), x.end()), *std::min_element(y.begin(), y.end())};
    output.bounds_max = {*std::max_element(x.begin(), x.end()), *std::max_element(y.begin(), y.end())};
    return true;
}

template<class T>
T read_record(std::span<const std::byte> bytes, std::size_t index = 0U) noexcept {
    T result{};
    std::memcpy(&result, bytes.data() + index * sizeof(T), sizeof(T));
    return result;
}

// Original ProGPU GpuHitTesting.CreateLineStrokeHitTestData encoding. Independent
// x/y subtraction and squaring use intrinsic lanes; length is one fixed reduction.
bool line_hit_data(progpu_native_point start, progpu_native_point end,
    progpu_native_float_4& data) noexcept {
    [[maybe_unused]] std::array<float, 4U> a{start.x, start.y, 0.0F, 0.0F};
    [[maybe_unused]] std::array<float, 4U> b{end.x, end.y, 0.0F, 0.0F};
    std::array<float, 4U> delta{}, square{};
#if defined(__aarch64__) || defined(_M_ARM64)
    const auto difference = vsubq_f32(vld1q_f32(b.data()), vld1q_f32(a.data()));
    vst1q_f32(delta.data(), difference);
    vst1q_f32(square.data(), vmulq_f32(difference, difference));
#elif defined(__SSE2__) || defined(_M_X64)
    const auto difference = _mm_sub_ps(_mm_loadu_ps(b.data()), _mm_loadu_ps(a.data()));
    _mm_storeu_ps(delta.data(), difference);
    _mm_storeu_ps(square.data(), _mm_mul_ps(difference, difference));
#else
    return false;
#endif
    const float length = std::sqrt(square[0U] + square[1U]);
    // The canonical query shader's degenerate-line branch treats nonflat caps
    // as a disk. Do not apply it to source directed point caps or tiny lines.
    if (!std::isfinite(length) || length <= 0.0001F) return false;
    data = {delta[0U] / length, delta[1U] / length, length, 0.0F};
    return true;
}

// Four independent half-plane limits. This is source clip metadata, never a
// raster fallback; it shares the producer's NEON/SSE2 admission policy.
progpu_native_image_rect intersect_hit_clips(progpu_native_image_rect a,
    progpu_native_image_rect b) noexcept {
    [[maybe_unused]] const std::array<float, 4U> first{-a.x, -a.y, a.x + a.width, a.y + a.height};
    [[maybe_unused]] const std::array<float, 4U> second{-b.x, -b.y, b.x + b.width, b.y + b.height};
    const float invalid = std::numeric_limits<float>::quiet_NaN();
    for (std::size_t i = 0U; i < 4U; ++i)
        if (!std::isfinite(first[i]) || !std::isfinite(second[i])) return {invalid, invalid, invalid, invalid};
    std::array<float, 4U> clipped{};
#if defined(__aarch64__) || defined(_M_ARM64)
    vst1q_f32(clipped.data(), vminq_f32(vld1q_f32(first.data()), vld1q_f32(second.data())));
#elif defined(__SSE2__) || defined(_M_X64)
    _mm_storeu_ps(clipped.data(), _mm_min_ps(_mm_loadu_ps(first.data()), _mm_loadu_ps(second.data())));
#else
    return {invalid, invalid, invalid, invalid};
#endif
    return {-clipped[0], -clipped[1], std::max(0.0F, clipped[2] + clipped[0]),
        std::max(0.0F, clipped[3] + clipped[1])};
}

} // namespace

bool semantic_scene_builder::set_hit_test_owner(std::optional<std::int32_t> owner) noexcept {
    auto& boundaries = implementation_->hit_test_owners;
    if ((boundaries.empty() && !owner) || (!boundaries.empty() && boundaries.back().owner == owner))
        return true;
    try {
        const auto first = implementation_->commands.size();
        if (!boundaries.empty() && boundaries.back().first_command == first)
            boundaries.back().owner = owner;
        else
            boundaries.push_back({first, owner});
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    }
}

// O(C + P + S + P*D) time and O(R + P + S + D) storage for commands C,
// resources R, hit primitives P, copied segments S and quadtree depth D (default 8).
// Directly consumes builder-owned typed resources; no serialize/parse round trip.
bool semantic_scene_builder::add_recorded_hit_test_index(std::uint32_t& resource_index,
    scene_hit_test_opacity_mode opacity_mode) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (opacity_mode != scene_hit_test_opacity_mode::rendered_visibility &&
        opacity_mode != scene_hit_test_opacity_mode::source_geometry)
        return implementation_->fail(scene_build_error::invalid_argument);
    const bool source_geometry = opacity_mode == scene_hit_test_opacity_mode::source_geometry;
    const std::uint32_t input_state_flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT |
        (source_geometry ? static_cast<std::uint32_t>(PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET |
            PROGPU_NATIVE_SCENE_STATE_MASK) : 0U);
    if (implementation_->stack_depth != 0U)
        return implementation_->fail(scene_build_error::unbalanced_stack);
    try {
        std::vector<progpu_native_hit_test_primitive> primitives;
        std::vector<progpu_native_path_segment> segments;
        std::array<std::uint32_t, PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> stack{};
        std::array<bool, PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> layer_stack{};
        std::array<std::uint32_t, PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> clip_scope_stack{};
        std::vector<progpu_native_image_rect> layer_clips(1U); // scope zero has no layer clip
        std::uint32_t layer_clip_scope = 0U;
        // Ordinary noncached input must not allocate a frame table.
        std::vector<progpu_native_affine_2d> input_frames;
        std::uint32_t input_frame = 0U;
        std::array<std::uint32_t, PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> frame_stack{};
        const auto map_rectangle = [&](progpu_native_image_rect& rectangle) {
            if (input_frame == 0U) return true;
            const auto& transform = input_frames[input_frame];
            // Axis-preserving rectangles retain exact rectangle clipping. Other
            // frames need composed polygon clips, not their enclosing AABB.
            if (!((transform.m12 == 0.0F && transform.m21 == 0.0F) ||
                (transform.m11 == 0.0F && transform.m22 == 0.0F))) return false;
            if (rectangle.width < 0.0F || rectangle.height < 0.0F) return false;
            progpu_native_hit_test_primitive bounds{};
            if (!place_primitive(bounds, {rectangle.x, rectangle.y},
                {rectangle.x + rectangle.width, rectangle.y + rectangle.height}, transform)) return false;
            rectangle = {bounds.bounds_min.x, bounds.bounds_min.y,
                bounds.bounds_max.x - bounds.bounds_min.x, bounds.bounds_max.y - bounds.bounds_min.y};
            return std::isfinite(rectangle.width) && std::isfinite(rectangle.height);
        };
        const auto map_state = [&](progpu_native_scene_state& state) {
            if (input_frame == 0U) return true;
            return compose_input_frame(state.transform, input_frames[input_frame], state.transform) &&
                ((state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) == 0U || map_rectangle(state.clip_rect));
        };
        std::size_t depth = 0U, boundary = 0U, glyph_bounds_index = 0U, rectangle_scope_index = 0U;
        std::size_t source_layer_index = 0U;
        std::uint32_t current_state = PROGPU_NATIVE_SCENE_NO_INDEX;
        std::uint32_t query_participation = 0U;
        std::array<std::uint32_t, PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> query_stack{};
        std::optional<std::int32_t> owner;
        constexpr std::size_t exact_float_integer_limit = 1U << 24U;
        // Rectangular clip payloads are reused by state identity, not per primitive.
        struct clip_entry { std::uint32_t scope{}, frame{}; std::optional<std::uint32_t> start; };
        std::vector<clip_entry> clip_starts(implementation_->resources.size() + 1U);
        struct vector_clip_entry {
            bool loaded{};
            std::uint32_t frame{};
            std::uint32_t start{}, count{}, rule{};
            progpu_native_point minimum{}, maximum{};
        };
        std::vector<vector_clip_entry> vector_clips;
        const auto load_vector_clip = [&](std::uint32_t index) -> const vector_clip_entry* {
            if (index >= implementation_->resources.size()) return nullptr;
            if (vector_clips.empty()) vector_clips.resize(implementation_->resources.size());
            auto& cached = vector_clips[index];
            if (cached.loaded && cached.frame == input_frame) return &cached;
            const auto& resource = implementation_->resources[index];
            if (!resource.source_geometry_clip || resource.record.kind != PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK ||
                resource.payload.size() != sizeof(progpu_native_scene_layer_vector_mask)) return nullptr;
            const auto mask = read_record<progpu_native_scene_layer_vector_mask>(resource.payload);
            if (mask.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN || mask.opacity != 1.0F ||
                mask.path_count != 1U || mask.boolean_node_count != 0U) return nullptr;
            const auto path = read_record<progpu_native_scene_clip_path>(resource.auxiliary);
            if (path.boolean_node_count != 0U || path.operation != PROGPU_NATIVE_CLIP_INTERSECT ||
                path.segment_count == 0U || path.segment_count > exact_float_integer_limit ||
                segments.size() > exact_float_integer_limit - path.segment_count) return nullptr;
            progpu_native_hit_test_primitive bounds{};
            auto transform = path.transform;
            if (input_frame != 0U && !compose_input_frame(transform, input_frames[input_frame], transform)) return nullptr;
            if (!place_primitive(bounds, {path.min_x, path.min_y}, {path.max_x, path.max_y}, transform)) return nullptr;
            cached.minimum = bounds.bounds_min; cached.maximum = bounds.bounds_max;
            cached.start = static_cast<std::uint32_t>(segments.size());
            cached.count = static_cast<std::uint32_t>(path.segment_count);
            cached.rule = path.fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD ? 0U : hit_nonzero_fill_rule;
            const auto path_data = std::span<const std::byte>(resource.auxiliary).subspan(sizeof(progpu_native_scene_clip_path));
            for (std::size_t j = 0U; j < path.segment_count; ++j) {
                auto segment = read_record<progpu_native_path_segment>(path_data, path.segment_offset + j);
                if (segment.kind != PROGPU_NATIVE_PATH_SEGMENT_LINE &&
                    segment.kind != PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC && segment.kind != PROGPU_NATIVE_PATH_SEGMENT_CUBIC)
                    return nullptr;
                std::array<float, 4U> x{segment.p0.x, segment.p1.x, segment.p2.x, segment.p3.x};
                std::array<float, 4U> y{segment.p0.y, segment.p1.y, segment.p2.y, segment.p3.y};
                if (!transform_hit_coordinates(x, y, transform)) return nullptr;
                segment.p0 = {x[0], y[0]}; segment.p1 = {x[1], y[1]};
                segment.p2 = {x[2], y[2]}; segment.p3 = {x[3], y[3]};
                segments.push_back(segment);
            }
            cached.loaded = true;
            cached.frame = input_frame;
            return &cached;
        };
        const auto append = [&](progpu_native_hit_test_primitive primitive,
                                const progpu_native_scene_state& state, std::uint32_t state_index) {
            if (primitives.size() >= exact_float_integer_limit) return false;
            primitive.id = *owner;
            primitive.z_index = static_cast<float>(primitives.size());
            primitive.flags = PROGPU_NATIVE_HIT_TEST_VISIBLE | PROGPU_NATIVE_HIT_TEST_VISIBLE_TO_INPUT | query_participation;
            const vector_clip_entry* vector_clip = nullptr;
            if ((state.flags & PROGPU_NATIVE_SCENE_STATE_MASK) != 0U) {
                vector_clip = load_vector_clip(state.mask_resource_index);
                if (vector_clip == nullptr) return false;
                primitive.bounds_min.x = std::max(primitive.bounds_min.x, vector_clip->minimum.x);
                primitive.bounds_min.y = std::max(primitive.bounds_min.y, vector_clip->minimum.y);
                primitive.bounds_max.x = std::min(primitive.bounds_max.x, vector_clip->maximum.x);
                primitive.bounds_max.y = std::min(primitive.bounds_max.y, vector_clip->maximum.y);
                if (primitive.bounds_min.x > primitive.bounds_max.x || primitive.bounds_min.y > primitive.bounds_max.y)
                    return true;
                primitive.clip_start_segment = vector_clip->start;
                primitive.clip_segment_count = vector_clip->count;
                primitive.clip_fill_rule = vector_clip->rule;
                primitive.clip_flags = 1U;
            }
            const bool state_clip = (state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U;
            if (state_clip || layer_clip_scope != 0U) {
                const auto clip = !state_clip ? layer_clips[layer_clip_scope] : layer_clip_scope == 0U
                    ? state.clip_rect : intersect_hit_clips(state.clip_rect, layer_clips[layer_clip_scope]);
                if (clip.width <= 0.0F || clip.height <= 0.0F) return true;
                const float right = clip.x + clip.width, bottom = clip.y + clip.height;
                if (!std::isfinite(right) || !std::isfinite(bottom)) return false;
                if (vector_clip != nullptr) {
                    // A containing rectangle is redundant. Nonredundant intersections
                    // need composed clip topology; never overwrite the actual path.
                    if (clip.x > vector_clip->minimum.x || clip.y > vector_clip->minimum.y ||
                        right < vector_clip->maximum.x || bottom < vector_clip->maximum.y) return false;
                    primitives.push_back(primitive);
                    return true;
                }
                primitive.bounds_min.x = std::max(primitive.bounds_min.x, clip.x);
                primitive.bounds_min.y = std::max(primitive.bounds_min.y, clip.y);
                primitive.bounds_max.x = std::min(primitive.bounds_max.x, right);
                primitive.bounds_max.y = std::min(primitive.bounds_max.y, bottom);
                if (primitive.bounds_min.x > primitive.bounds_max.x || primitive.bounds_min.y > primitive.bounds_max.y)
                    return true;
                auto& cached = clip_starts[state_index == PROGPU_NATIVE_SCENE_NO_INDEX
                    ? implementation_->resources.size() : state_index];
                if (cached.scope != layer_clip_scope || cached.frame != input_frame) {
                    cached.scope = layer_clip_scope;
                    cached.frame = input_frame;
                    cached.start.reset();
                }
                auto& start = cached.start;
                if (!start) {
                    if (segments.size() > exact_float_integer_limit - 4U) return false;
                    start = static_cast<std::uint32_t>(segments.size());
                    const std::array points{progpu_native_point{clip.x, clip.y},
                        progpu_native_point{right, clip.y}, progpu_native_point{right, bottom},
                        progpu_native_point{clip.x, bottom}};
                    for (std::size_t i = 0U; i < 4U; ++i) {
                        progpu_native_path_segment edge{};
                        edge.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                        edge.p0 = points[i]; edge.p1 = points[(i + 1U) % 4U];
                        segments.push_back(edge);
                    }
                }
                primitive.clip_start_segment = *start;
                primitive.clip_segment_count = 4U;
                primitive.clip_flags = 1U;
                primitive.clip_fill_rule = hit_nonzero_fill_rule;
            }
            primitives.push_back(primitive);
            return true;
        };
        const auto unsupported = [&] { return implementation_->fail(scene_build_error::unsupported_hit_test); };
        // Both analytic draws and canonical MIL full-ellipse arc strokes use the
        // existing managed/native ellipse query contract, not a bounds-only hit.
        const auto append_ellipse = [&](progpu_native_point minimum, progpu_native_point maximum,
            float thickness, const progpu_native_affine_2d& transform,
            const progpu_native_scene_state& state, std::uint32_t state_index) {
            progpu_native_hit_test_primitive hit{};
            hit.kind = thickness > 0.0F ? PROGPU_NATIVE_HIT_TEST_ELLIPSE_STROKE : PROGPU_NATIVE_HIT_TEST_ELLIPSE_FILL;
            hit.data0 = {minimum.x, minimum.y, maximum.x, maximum.y};
            hit.data1 = {thickness, 0.0F, 0.0F, 0.0F};
            const float rx = (maximum.x - minimum.x) * 0.5F;
            const float ry = (maximum.y - minimum.y) * 0.5F;
            hit.data2 = {(minimum.x + maximum.x) * 0.5F, (minimum.y + maximum.y) * 0.5F,
                std::abs(rx) > 0.0001F ? 1.0F / rx : 0.0F,
                std::abs(ry) > 0.0001F ? 1.0F / ry : 0.0F};
            const float padding = thickness * 0.5F;
            return place_primitive(hit, {minimum.x - padding, minimum.y - padding},
                {maximum.x + padding, maximum.y + padding}, transform) && append(hit, state, state_index);
        };
        const auto append_line = [&](progpu_native_point first, progpu_native_point last,
            float width, std::uint32_t start_cap, std::uint32_t end_cap,
            const progpu_native_affine_2d& transform, const progpu_native_scene_state& state,
            std::uint32_t state_index) {
            progpu_native_hit_test_primitive hit{};
            hit.kind = PROGPU_NATIVE_HIT_TEST_LINE_STROKE;
            hit.data0 = {first.x, first.y, last.x, last.y};
            hit.data1 = {width, 0.0F, static_cast<float>(start_cap), static_cast<float>(end_cap)};
            if (!line_hit_data(first, last, hit.data2)) return false;
            float padding = width * 0.5F;
            // Broad phase must include diagonal square-cap corners.
            if (start_cap == PROGPU_NATIVE_STROKE_CAP_SQUARE || end_cap == PROGPU_NATIVE_STROKE_CAP_SQUARE)
                padding *= std::sqrt(2.0F);
            return place_primitive(hit,
                {std::min(first.x, last.x) - padding, std::min(first.y, last.y) - padding},
                {std::max(first.x, last.x) + padding, std::max(first.y, last.y) + padding}, transform) &&
                append(hit, state, state_index);
        };
        const auto append_join_triangle = [&](const stroke_triangle& triangle,
            const progpu_native_affine_2d& transform, const progpu_native_scene_state& state,
            std::uint32_t state_index) {
            if (!is_finite(triangle.p0) || !is_finite(triangle.p1) || !is_finite(triangle.p2) ||
                segments.size() > exact_float_integer_limit - 3U) return false;
            const progpu_native_point minimum{std::min({triangle.p0.x, triangle.p1.x, triangle.p2.x}),
                std::min({triangle.p0.y, triangle.p1.y, triangle.p2.y})};
            const progpu_native_point maximum{std::max({triangle.p0.x, triangle.p1.x, triangle.p2.x}),
                std::max({triangle.p0.y, triangle.p1.y, triangle.p2.y})};
            progpu_native_hit_test_primitive hit{};
            hit.kind = PROGPU_NATIVE_HIT_TEST_PATH_FILL;
            hit.data0 = {minimum.x, minimum.y, maximum.x, maximum.y};
            hit.data1 = {static_cast<float>(segments.size()), 3.0F, 1.0F, 0.0F};
            if (!place_primitive(hit, minimum, maximum, transform)) return false;
            const std::array points{triangle.p0, triangle.p1, triangle.p2};
            for (std::size_t edge = 0U; edge < 3U; ++edge) {
                progpu_native_path_segment segment{};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                segment.p0 = points[edge]; segment.p1 = points[(edge + 1U) % 3U];
                segments.push_back(segment);
            }
            return append(hit, state, state_index);
        };
        const auto append_rectangle = [&](const progpu_native_image_rect& rectangle,
                                          const progpu_native_affine_2d& transform,
                                          const progpu_native_scene_state& state,
                                          std::uint32_t state_index) {
            if ((rectangle.width == 0.0F || rectangle.height == 0.0F) &&
                query_participation != PROGPU_NATIVE_HIT_TEST_POINT_ONLY) return true;
            progpu_native_hit_test_primitive hit{};
            hit.kind = PROGPU_NATIVE_HIT_TEST_RECTANGLE_FILL;
            hit.data0 = {rectangle.x, rectangle.y,
                rectangle.x + rectangle.width, rectangle.y + rectangle.height};
            return place_primitive(hit, {hit.data0.x, hit.data0.y}, {hit.data0.z, hit.data0.w}, transform) &&
                append(hit, state, state_index);
        };
        for (std::size_t i = 0U; i < implementation_->commands.size(); ++i) {
            while (boundary < implementation_->hit_test_owners.size() &&
                implementation_->hit_test_owners[boundary].first_command <= i)
                owner = implementation_->hit_test_owners[boundary++].owner;
            const auto& command = implementation_->commands[i];
            const auto kind = command.record.kind;
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE) {
                if (depth == stack.size()) return unsupported();
                clip_scope_stack[depth] = layer_clip_scope;
                frame_stack[depth] = input_frame;
                query_stack[depth] = query_participation;
                const auto& scopes = implementation_->hit_rectangle_scopes;
                while (rectangle_scope_index < scopes.size() &&
                    scopes[rectangle_scope_index].first_command < i) ++rectangle_scope_index;
                if (rectangle_scope_index < scopes.size() && scopes[rectangle_scope_index].first_command == i) {
                    const auto& scope = scopes[rectangle_scope_index++];
                    if (!owner || scope.last_command <= i ||
                        scope.last_command >= implementation_->commands.size() ||
                        implementation_->commands[scope.last_command].record.kind !=
                            PROGPU_NATIVE_SCENE_COMMAND_RESTORE) return unsupported();
                    const auto state_index = command.record.state_index == PROGPU_NATIVE_SCENE_NO_INDEX
                        ? current_state : command.record.state_index;
                    auto state = state_index == PROGPU_NATIVE_SCENE_NO_INDEX ? identity_state() :
                        read_record<progpu_native_scene_state>(implementation_->resources[state_index].payload);
                    if ((state.flags & ~input_state_flags) != 0U || !map_state(state)) return unsupported();
                    if (scope.point_only) {
                        if (!scope.empty_point_region && query_participation != PROGPU_NATIVE_HIT_TEST_REGION_ONLY) {
                            query_participation = PROGPU_NATIVE_HIT_TEST_POINT_ONLY;
                            if (!append_rectangle(scope.local_bounds, state.transform, state, state_index)) return unsupported();
                        }
                        query_participation = PROGPU_NATIVE_HIT_TEST_REGION_ONLY;
                    } else {
                        if ((source_geometry || state.opacity > 0.0001F) &&
                            !append_rectangle(scope.local_bounds, state.transform, state, state_index)) return unsupported();
                        // Image scopes replace both query kinds and skip their
                        // nested rendering. Point-only scopes keep region draws.
                        i = scope.last_command;
                        continue;
                    }
                }
                if (depth == stack.size()) return unsupported();
                layer_stack[depth] = false;
                stack[depth++] = current_state;
                if (command.record.state_index != PROGPU_NATIVE_SCENE_NO_INDEX)
                    current_state = command.record.state_index;
                continue;
            }
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_RESTORE) {
                if (depth == 0U || layer_stack[depth - 1U]) return unsupported();
                current_state = stack[--depth];
                layer_clip_scope = clip_scope_stack[depth];
                input_frame = frame_stack[depth];
                query_participation = query_stack[depth];
                continue;
            }
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
                const auto& layers = implementation_->source_geometry_hit_layers;
                while (source_layer_index < layers.size() && layers[source_layer_index].command_index < i)
                    ++source_layer_index;
                // Only explicitly admitted source layers preserve input geometry.
                // Their storage/effect padding is never a geometric clip.
                if (source_layer_index == layers.size() || layers[source_layer_index].command_index != i ||
                    depth == stack.size()) return unsupported();
                const auto& input_layer = layers[source_layer_index++];
                layer_stack[depth] = true;
                clip_scope_stack[depth] = layer_clip_scope;
                frame_stack[depth] = input_frame;
                query_stack[depth] = query_participation;
                const auto layer = read_record<progpu_native_scene_layer>(command.payload);
                if ((layer.flags & PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE) != 0U || input_layer.changes_frame) {
                    const auto composite = read_record<progpu_native_scene_state>(
                        implementation_->resources[layer.reserved0].payload);
                    if ((composite.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U) {
                        auto clip = composite.clip_rect;
                        if (!map_rectangle(clip)) return unsupported();
                        if (layer_clip_scope != 0U) clip = intersect_hit_clips(clip, layer_clips[layer_clip_scope]);
                        if (!std::isfinite(clip.x + clip.width) || !std::isfinite(clip.y + clip.height))
                            return unsupported();
                        layer_clip_scope = static_cast<std::uint32_t>(layer_clips.size());
                        layer_clips.push_back(clip);
                    }
                }
                if (input_layer.changes_frame) {
                    if (input_frames.empty()) input_frames.push_back(identity_transform());
                    progpu_native_affine_2d transform{};
                    if (!compose_input_frame(input_layer.content_to_parent, input_frames[input_frame], transform))
                        return unsupported();
                    input_frame = static_cast<std::uint32_t>(input_frames.size());
                    input_frames.push_back(transform);
                }
                stack[depth++] = current_state;
                continue;
            }
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
                if (depth == 0U || !layer_stack[depth - 1U]) return unsupported();
                current_state = stack[--depth];
                layer_clip_scope = clip_scope_stack[depth];
                input_frame = frame_stack[depth];
                query_participation = query_stack[depth];
                continue;
            }
            if (!owner) continue;
            const auto state_index = command.record.state_index == PROGPU_NATIVE_SCENE_NO_INDEX
                ? current_state : command.record.state_index;
            auto state = state_index == PROGPU_NATIVE_SCENE_NO_INDEX ? identity_state() :
                read_record<progpu_native_scene_state>(implementation_->resources[state_index].payload);
            if ((state.flags & ~input_state_flags) != 0U || !map_state(state)) return unsupported();
            if (!source_geometry && state.opacity <= 0.0001F) continue;
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN) {
                while (glyph_bounds_index < implementation_->glyph_hit_bounds.size() &&
                    implementation_->glyph_hit_bounds[glyph_bounds_index].command_index < i) ++glyph_bounds_index;
                if (glyph_bounds_index == implementation_->glyph_hit_bounds.size() ||
                    implementation_->glyph_hit_bounds[glyph_bounds_index].command_index != i) return unsupported();
                // Source hit semantics are the run's actual ink rectangle, not
                // per-glyph outline holes, raster padding or estimated advances.
                if (!append_rectangle(implementation_->glyph_hit_bounds[glyph_bounds_index].local_bounds,
                    state.transform, state, state_index)) return unsupported();
                continue;
            }
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) {
                const auto source = read_record<progpu_native_scene_image_draw>(command.payload);
                if ((source.flags & PROGPU_NATIVE_SCENE_IMAGE_EFFECT) != 0U) return unsupported();
                const auto& resource = implementation_->resources[command.record.resource_index];
                auto record = command.record;
                record.payload_offset = 0U;
                record.payload_size = static_cast<std::uint32_t>(command.payload.size());
                semantic::semantic_image_options options{};
                const std::uint32_t bytes_per_pixel = resource.r8_image ? 1U : 4U;
                const std::uint64_t pixel_bytes = std::uint64_t{source.row_bytes} * (source.image_height - 1U) +
                    std::uint64_t{source.image_width} * bytes_per_pixel;
                if (!semantic::validate_image_draw_payload(command.payload.data(), record, source,
                    pixel_bytes, options, bytes_per_pixel)) return unsupported();
                const auto transform = compose_affine(source.transform, state.transform);
                if (options.patch_count == 0U) {
                    if (!append_rectangle(source.destination_rect, transform, state, state_index)) return unsupported();
                } else {
                    const std::span patch_bytes(options.patch_bytes,
                        static_cast<std::size_t>(options.patch_count) * sizeof(progpu_native_scene_image_patch));
                    for (std::size_t j = 0U; j < options.patch_count; ++j) {
                        const auto patch = read_record<progpu_native_scene_image_patch>(patch_bytes, j);
                        if (!append_rectangle(patch.destination_rect,
                            compose_affine(patch.transform, transform), state, state_index)) return unsupported();
                    }
                }
                continue;
            }
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) {
                const auto& resource = implementation_->resources[command.record.resource_index];
                for (std::size_t j = 0U; j < resource.payload.size() / sizeof(progpu_native_geometry_primitive); ++j) {
                    const auto source = read_record<progpu_native_geometry_primitive>(resource.payload, j);
                    if (source.kind == PROGPU_NATIVE_GEOMETRY_ARC) {
                        // EllipseGeometry emits this canonical full sweep. Partial,
                        // skew-basis and device-width arcs need their own contract.
                        if ((source.flags & ~PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) != 0U ||
                            source.p1.x <= 0.0F || source.p2.y <= 0.0F ||
                            source.p1.y != 0.0F || source.p2.x != 0.0F || source.p3.x != 0.0F ||
                            source.p3.y != std::numbers::pi_v<float> * 2.0F) return unsupported();
                        if (source.stroke_thickness <= 0.0F) continue;
                        if (!append_ellipse({source.p0.x - source.p1.x, source.p0.y - source.p2.y},
                            {source.p0.x + source.p1.x, source.p0.y + source.p2.y}, source.stroke_thickness,
                            compose_affine(source.transform, state.transform), state, state_index)) return unsupported();
                        continue;
                    }
                    constexpr std::uint32_t allowed = PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED |
                        PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK | PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK;
                    if (source.kind == PROGPU_NATIVE_GEOMETRY_PATH_JOIN) {
                        if ((source.flags & ~(PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED |
                            PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK)) != 0U) return unsupported();
                        const auto transform = compose_affine(source.transform, state.transform);
                        float maximum_scale{}, minimum_scale{};
                        if (!try_get_stroke_scales(transform, maximum_scale, minimum_scale)) return unsupported();
                        const bool affine_outline = requires_affine_stroke_geometry(transform);
                        std::array<stroke_triangle, 8U> joins{};
                        // Use the renderer's connected-path join construction,
                        // including its conformal versus local-affine domain.
                        const auto count = create_join_triangles(joins,
                            (source.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >> PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT,
                            affine_outline ? source.stroke_thickness : source.stroke_thickness * maximum_scale,
                            source.p3.x, affine_outline ? source.p0 : transformed_point(transform, source.p0),
                            affine_outline ? source.p1 : transformed_direction(transform, source.p1),
                            affine_outline ? source.p2 : transformed_direction(transform, source.p2));
                        for (std::size_t k = 0U; k < count; ++k)
                            if (!append_join_triangle(joins[k], affine_outline ? transform : identity_transform(),
                                state, state_index)) return unsupported();
                        continue;
                    }
                    if (source.kind == PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER ||
                        source.kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER) {
                        if ((source.flags & ~allowed) != 0U) return unsupported();
                        if (source.stroke_thickness <= 0.0F) continue;
                        if (segments.size() >= exact_float_integer_limit) return unsupported();
                        const bool cubic = source.kind == PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER;
                        progpu_native_path_segment segment{};
                        segment.kind = cubic ? PROGPU_NATIVE_PATH_SEGMENT_CUBIC : PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC;
                        segment.p0 = source.p0; segment.p1 = source.p1;
                        segment.p2 = source.p2; segment.p3 = cubic ? source.p3 : progpu_native_point{};
                        // Control-hull bounds are conservative pruning only. The
                        // shared PathStroke shader queries the retained curve.
                        progpu_native_point minimum{std::min({source.p0.x, source.p1.x, source.p2.x}),
                            std::min({source.p0.y, source.p1.y, source.p2.y})};
                        progpu_native_point maximum{std::max({source.p0.x, source.p1.x, source.p2.x}),
                            std::max({source.p0.y, source.p1.y, source.p2.y})};
                        if (cubic) {
                            minimum.x = std::min(minimum.x, source.p3.x); minimum.y = std::min(minimum.y, source.p3.y);
                            maximum.x = std::max(maximum.x, source.p3.x); maximum.y = std::max(maximum.y, source.p3.y);
                        }
                        progpu_native_hit_test_primitive hit{};
                        hit.kind = PROGPU_NATIVE_HIT_TEST_PATH_STROKE;
                        hit.data0 = {minimum.x, minimum.y, maximum.x, maximum.y};
                        hit.data1 = {static_cast<float>(segments.size()), 1.0F, source.stroke_thickness, 0.0F};
                        hit.data2 = {static_cast<float>((source.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >> PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT),
                            static_cast<float>((source.flags & PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK) >> PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT), 0.0F, 0.0F};
                        // A diagonal square-cap corner can extend sqrt(2) radii
                        // along an axis; the curve body still uses exact segments.
                        const bool square_cap = hit.data2.x == static_cast<float>(PROGPU_NATIVE_STROKE_CAP_SQUARE) ||
                            hit.data2.y == static_cast<float>(PROGPU_NATIVE_STROKE_CAP_SQUARE);
                        const float padding = source.stroke_thickness * 0.5F * (square_cap ? std::numbers::sqrt2_v<float> : 1.0F);
                        if (!place_primitive(hit, {minimum.x - padding, minimum.y - padding},
                            {maximum.x + padding, maximum.y + padding}, compose_affine(source.transform, state.transform))) return unsupported();
                        segments.push_back(segment);
                        if (!append(hit, state, state_index)) return unsupported();
                        continue;
                    }
                    if (source.kind != PROGPU_NATIVE_GEOMETRY_LINE || (source.flags & ~allowed) != 0U)
                        return unsupported();
                    if (source.stroke_thickness <= 0.0F) continue;
                    const auto start_cap = (source.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) >> PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
                    const auto end_cap = (source.flags & PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK) >> PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT;
                    if (!append_line(source.p0, source.p1, source.stroke_thickness, start_cap, end_cap,
                        compose_affine(source.transform, state.transform), state, state_index)) return unsupported();
                }
                continue;
            }
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH) {
                const auto& resource = implementation_->resources[command.record.resource_index];
                for (std::size_t j = 0U; j < resource.payload.size() / sizeof(progpu_native_scene_stroke); ++j) {
                    const auto source = read_record<progpu_native_scene_stroke>(resource.payload, j);
                    constexpr std::uint32_t allowed = PROGPU_NATIVE_POLYLINE_FLAG_EDGE_ALIASED |
                        PROGPU_NATIVE_POLYLINE_FLAG_CLOSED | PROGPU_NATIVE_POLYLINE_FLAG_WPF_JOIN_SEMANTICS;
                    const bool closed = (source.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U;
                    if (source.kind != PROGPU_NATIVE_SCENE_STROKE_POLYLINE || source.dash_interval_count != 0U ||
                        (source.flags & ~allowed) != 0U || source.point_count < (closed ? 3U : 2U) ||
                        source.stroke_thickness <= 0.0001F) return unsupported();
                    const auto transform = compose_affine(source.transform, state.transform);
                    float maximum_scale{}, minimum_scale{};
                    if (!try_get_stroke_scales(transform, maximum_scale, minimum_scale)) return unsupported();
                    const bool affine_outline = requires_affine_stroke_geometry(transform);
                    const auto join_transform = affine_outline ? transform : identity_transform();
                    const auto point = [&](std::size_t index) {
                        return read_record<progpu_native_point>(resource.auxiliary, source.point_offset + index);
                    };
                    const bool wpf_joins = (source.flags & PROGPU_NATIVE_POLYLINE_FLAG_WPF_JOIN_SEMANTICS) != 0U;
                    // Same traversal and join construction as append_polyline.
                    // Line bodies with endpoint caps plus real join triangles form a union under
                    // one owner; canonical query output deduplicates that owner.
                    const auto edge_count = closed ? source.point_count : source.point_count - 1U;
                    for (std::size_t edge = 0U; edge < edge_count; ++edge) {
                        const auto first = point(edge), corner = point((edge + 1U) % source.point_count);
                        const auto start_cap = !closed && edge == 0U ? source.start_cap :
                            static_cast<std::uint32_t>(PROGPU_NATIVE_STROKE_CAP_FLAT);
                        const auto end_cap = !closed && edge + 1U == edge_count ? source.end_cap :
                            static_cast<std::uint32_t>(PROGPU_NATIVE_STROKE_CAP_FLAT);
                        if (!append_line(first, corner, source.stroke_thickness, start_cap,
                            end_cap, transform, state, state_index)) return unsupported();
                        if (!closed && edge + 1U == edge_count) continue;
                        const auto last = point((edge + 2U) % source.point_count);
                        std::array<stroke_triangle, 8U> joins{};
                        const progpu_native_point incoming{corner.x - first.x, corner.y - first.y};
                        const progpu_native_point outgoing{last.x - corner.x, last.y - corner.y};
                        // Match append_polyline's local-affine versus world-conformal
                        // join domain, including its scale-sensitive miter threshold.
                        const auto count = create_join_triangles(joins, source.line_join,
                            affine_outline ? source.stroke_thickness : source.stroke_thickness * maximum_scale,
                            source.miter_limit, affine_outline ? corner : transformed_point(transform, corner),
                            affine_outline ? incoming : transformed_direction(transform, incoming),
                            affine_outline ? outgoing : transformed_direction(transform, outgoing), wpf_joins);
                        for (std::size_t k = 0U; k < count; ++k)
                            if (!append_join_triangle(joins[k], join_transform, state, state_index)) return unsupported();
                    }
                }
                continue;
            }
            if (kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC &&
                kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) return unsupported();
            const auto& resource = implementation_->resources[command.record.resource_index];
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
                for (std::size_t j = 0U; j < resource.payload.size() / sizeof(progpu_native_analytic_primitive); ++j) {
                    const auto source = read_record<progpu_native_analytic_primitive>(resource.payload, j);
                    if ((source.flags & ~PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) != 0U) return unsupported();
                    if (source.width <= 0.0F || source.height <= 0.0F) return unsupported();
                    const bool ellipse = source.kind == PROGPU_NATIVE_PRIMITIVE_ELLIPSE;
                    if (ellipse) {
                        if (!append_ellipse({source.x, source.y}, {source.x + source.width, source.y + source.height},
                            source.stroke_thickness, compose_affine(source.transform, state.transform), state, state_index))
                            return unsupported();
                        continue;
                    }
                    progpu_native_hit_test_primitive hit{};
                    const bool stroke = source.stroke_thickness > 0.0F;
                    const float radius = source.kind == PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE ?
                        std::clamp(source.corner_radius, 0.0F, std::min(source.width, source.height) * 0.5F) : 0.0F;
                    hit.kind = stroke ? PROGPU_NATIVE_HIT_TEST_RECTANGLE_STROKE : PROGPU_NATIVE_HIT_TEST_RECTANGLE_FILL;
                    hit.data0 = {source.x, source.y, source.x + source.width, source.y + source.height};
                    hit.data1 = {radius, radius, source.stroke_thickness, 0.0F};
                    const float padding = source.stroke_thickness * 0.5F;
                    if (!place_primitive(hit, {source.x - padding, source.y - padding},
                        {hit.data0.z + padding, hit.data0.w + padding}, compose_affine(source.transform, state.transform)) ||
                        !append(hit, state, state_index)) return unsupported();
                }
            } else if (kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) {
                for (std::size_t j = 0U; j < resource.payload.size() / sizeof(progpu_native_scene_path_fill); ++j) {
                    const auto source = read_record<progpu_native_scene_path_fill>(resource.payload, j);
                    if (source.boolean_node_count != 0U || source.segment_count == 0U ||
                        source.segment_count > exact_float_integer_limit || segments.size() > exact_float_integer_limit - source.segment_count)
                        return unsupported();
                    progpu_native_hit_test_primitive hit{};
                    hit.kind = PROGPU_NATIVE_HIT_TEST_PATH_FILL;
                    hit.data0 = {source.min_x, source.min_y, source.max_x, source.max_y};
                    hit.data1 = {static_cast<float>(segments.size()), static_cast<float>(source.segment_count),
                        source.fill_rule == PROGPU_NATIVE_FILL_RULE_EVEN_ODD ? 0.0F : 1.0F, 0.0F};
                    if (!place_primitive(hit, {source.min_x, source.min_y}, {source.max_x, source.max_y},
                            compose_affine(source.transform, state.transform))) return unsupported();
                    const auto start = segments.size();
                    segments.resize(start + static_cast<std::size_t>(source.segment_count));
                    std::memcpy(segments.data() + start,
                        resource.auxiliary.data() + source.segment_offset * sizeof(progpu_native_path_segment),
                        static_cast<std::size_t>(source.segment_count) * sizeof(progpu_native_path_segment));
                    if (!append(hit, state, state_index)) return unsupported();
                }
            } else {
                return unsupported();
            }
        }
        hit_testing::hit_test_index index;
        hit_testing::hit_test_build_error error{};
        if (!hit_testing::try_build_hit_test_index(primitives, segments, {}, index, error))
            return implementation_->fail(error == hit_testing::hit_test_build_error::out_of_memory ?
                scene_build_error::out_of_memory : scene_build_error::capacity_exceeded);
        return add_hit_test_index(index.primitives(), index.nodes(), index.primitive_indices(), index.path_segments(), resource_index);
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

} // namespace progpu::native

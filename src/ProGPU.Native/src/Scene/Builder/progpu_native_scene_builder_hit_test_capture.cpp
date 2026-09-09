#include "progpu_native_scene_builder_internal.hpp"
#include "progpu_native_hit_testing.hpp"
#include "progpu_native_geometry_base.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <new>

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

// Native counterpart of ProGPU.Vector/GpuHitTesting.cs primitive encoding.
// Four independent corner lanes use NEON/SSE2, without alignment assumptions.
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
bool semantic_scene_builder::add_recorded_hit_test_index(std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (implementation_->stack_depth != 0U)
        return implementation_->fail(scene_build_error::unbalanced_stack);
    try {
        std::vector<progpu_native_hit_test_primitive> primitives;
        std::vector<progpu_native_path_segment> segments;
        std::array<std::uint32_t, PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> stack{};
        std::size_t depth = 0U, boundary = 0U;
        std::uint32_t current_state = PROGPU_NATIVE_SCENE_NO_INDEX;
        std::optional<std::int32_t> owner;
        constexpr std::size_t exact_float_integer_limit = 1U << 24U;
        // Rectangular clip payloads are reused by state identity, not per primitive.
        std::vector<std::optional<std::uint32_t>> clip_starts(implementation_->resources.size());
        const auto append = [&](progpu_native_hit_test_primitive primitive,
                                const progpu_native_scene_state& state, std::uint32_t state_index) {
            if (primitives.size() >= exact_float_integer_limit) return false;
            primitive.id = *owner;
            primitive.z_index = static_cast<float>(primitives.size());
            primitive.flags = PROGPU_NATIVE_HIT_TEST_VISIBLE | PROGPU_NATIVE_HIT_TEST_VISIBLE_TO_INPUT;
            if ((state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U) {
                const auto& clip = state.clip_rect;
                if (clip.width <= 0.0F || clip.height <= 0.0F) return true;
                const float right = clip.x + clip.width, bottom = clip.y + clip.height;
                if (!std::isfinite(right) || !std::isfinite(bottom)) return false;
                primitive.bounds_min.x = std::max(primitive.bounds_min.x, clip.x);
                primitive.bounds_min.y = std::max(primitive.bounds_min.y, clip.y);
                primitive.bounds_max.x = std::min(primitive.bounds_max.x, right);
                primitive.bounds_max.y = std::min(primitive.bounds_max.y, bottom);
                if (primitive.bounds_min.x > primitive.bounds_max.x || primitive.bounds_min.y > primitive.bounds_max.y)
                    return true;
                auto& start = clip_starts[state_index];
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
        for (std::size_t i = 0U; i < implementation_->commands.size(); ++i) {
            while (boundary < implementation_->hit_test_owners.size() &&
                implementation_->hit_test_owners[boundary].first_command <= i)
                owner = implementation_->hit_test_owners[boundary++].owner;
            const auto& command = implementation_->commands[i];
            const auto kind = command.record.kind;
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE) {
                if (depth == stack.size()) return unsupported();
                stack[depth++] = current_state;
                if (command.record.state_index != PROGPU_NATIVE_SCENE_NO_INDEX)
                    current_state = command.record.state_index;
                continue;
            }
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_RESTORE) {
                if (depth == 0U) return unsupported();
                current_state = stack[--depth];
                continue;
            }
            // Effects/cache/mask isolation must not turn into bounds-only hits.
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER || kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER)
                return unsupported();
            if (!owner) continue;
            const auto state_index = command.record.state_index == PROGPU_NATIVE_SCENE_NO_INDEX
                ? current_state : command.record.state_index;
            const auto state = state_index == PROGPU_NATIVE_SCENE_NO_INDEX ? identity_state() :
                read_record<progpu_native_scene_state>(implementation_->resources[state_index].payload);
            if ((state.flags & ~PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U) return unsupported();
            if (state.opacity <= 0.0001F) continue;
            if (kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC &&
                kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH) return unsupported();
            const auto& resource = implementation_->resources[command.record.resource_index];
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
                for (std::size_t j = 0U; j < resource.payload.size() / sizeof(progpu_native_analytic_primitive); ++j) {
                    const auto source = read_record<progpu_native_analytic_primitive>(resource.payload, j);
                    if ((source.flags & ~PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED) != 0U) return unsupported();
                    if (source.width <= 0.0F || source.height <= 0.0F) return unsupported();
                    progpu_native_hit_test_primitive hit{};
                    const bool ellipse = source.kind == PROGPU_NATIVE_PRIMITIVE_ELLIPSE;
                    const bool stroke = source.stroke_thickness > 0.0F;
                    const float radius = source.kind == PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE ?
                        std::clamp(source.corner_radius, 0.0F, std::min(source.width, source.height) * 0.5F) : 0.0F;
                    hit.kind = ellipse ? (stroke ? PROGPU_NATIVE_HIT_TEST_ELLIPSE_STROKE : PROGPU_NATIVE_HIT_TEST_ELLIPSE_FILL) :
                        (stroke ? PROGPU_NATIVE_HIT_TEST_RECTANGLE_STROKE : PROGPU_NATIVE_HIT_TEST_RECTANGLE_FILL);
                    hit.data0 = {source.x, source.y, source.x + source.width, source.y + source.height};
                    hit.data1 = ellipse ? progpu_native_float_4{source.stroke_thickness, 0.0F, 0.0F, 0.0F} :
                        progpu_native_float_4{radius, radius, source.stroke_thickness, 0.0F};
                    if (ellipse) {
                        const float rx = (hit.data0.z - hit.data0.x) * 0.5F;
                        const float ry = (hit.data0.w - hit.data0.y) * 0.5F;
                        hit.data2 = {(hit.data0.x + hit.data0.z) * 0.5F, (hit.data0.y + hit.data0.w) * 0.5F,
                            std::abs(rx) > 0.0001F ? 1.0F / rx : 0.0F,
                            std::abs(ry) > 0.0001F ? 1.0F / ry : 0.0F};
                    }
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

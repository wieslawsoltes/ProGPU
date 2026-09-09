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
        (source_geometry ? static_cast<std::uint32_t>(PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET) : 0U);
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
        std::size_t depth = 0U, boundary = 0U, glyph_bounds_index = 0U, rectangle_scope_index = 0U;
        std::size_t source_layer_index = 0U;
        std::uint32_t current_state = PROGPU_NATIVE_SCENE_NO_INDEX;
        std::optional<std::int32_t> owner;
        constexpr std::size_t exact_float_integer_limit = 1U << 24U;
        // Rectangular clip payloads are reused by state identity, not per primitive.
        struct clip_entry { std::uint32_t scope{}; std::optional<std::uint32_t> start; };
        std::vector<clip_entry> clip_starts(implementation_->resources.size() + 1U);
        const auto append = [&](progpu_native_hit_test_primitive primitive,
                                const progpu_native_scene_state& state, std::uint32_t state_index) {
            if (primitives.size() >= exact_float_integer_limit) return false;
            primitive.id = *owner;
            primitive.z_index = static_cast<float>(primitives.size());
            primitive.flags = PROGPU_NATIVE_HIT_TEST_VISIBLE | PROGPU_NATIVE_HIT_TEST_VISIBLE_TO_INPUT;
            const bool state_clip = (state.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U;
            if (state_clip || layer_clip_scope != 0U) {
                const auto clip = !state_clip ? layer_clips[layer_clip_scope] : layer_clip_scope == 0U
                    ? state.clip_rect : intersect_hit_clips(state.clip_rect, layer_clips[layer_clip_scope]);
                if (clip.width <= 0.0F || clip.height <= 0.0F) return true;
                const float right = clip.x + clip.width, bottom = clip.y + clip.height;
                if (!std::isfinite(right) || !std::isfinite(bottom)) return false;
                primitive.bounds_min.x = std::max(primitive.bounds_min.x, clip.x);
                primitive.bounds_min.y = std::max(primitive.bounds_min.y, clip.y);
                primitive.bounds_max.x = std::min(primitive.bounds_max.x, right);
                primitive.bounds_max.y = std::min(primitive.bounds_max.y, bottom);
                if (primitive.bounds_min.x > primitive.bounds_max.x || primitive.bounds_min.y > primitive.bounds_max.y)
                    return true;
                auto& cached = clip_starts[state_index == PROGPU_NATIVE_SCENE_NO_INDEX
                    ? implementation_->resources.size() : state_index];
                if (cached.scope != layer_clip_scope) {
                    cached.scope = layer_clip_scope;
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
            if (rectangle.width == 0.0F || rectangle.height == 0.0F) return true;
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
                    const auto state = state_index == PROGPU_NATIVE_SCENE_NO_INDEX ? identity_state() :
                        read_record<progpu_native_scene_state>(implementation_->resources[state_index].payload);
                    if ((state.flags & ~input_state_flags) != 0U) return unsupported();
                    if ((source_geometry || state.opacity > 0.0001F) &&
                        !append_rectangle(scope.local_bounds, state.transform, state, state_index)) return unsupported();
                    // Builder restore pairs this exact balanced scope. Its
                    // internal rendering, including nested masks/layers, is not
                    // the source operation's input geometry. Outer state stays.
                    i = scope.last_command;
                    continue;
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
                continue;
            }
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
                const auto& layers = implementation_->source_geometry_hit_layers;
                while (source_layer_index < layers.size() && layers[source_layer_index] < i)
                    ++source_layer_index;
                // Only explicitly admitted source layers preserve input geometry.
                // Their storage/effect padding is never a geometric clip.
                if (source_layer_index == layers.size() || layers[source_layer_index] != i ||
                    depth == stack.size()) return unsupported();
                ++source_layer_index;
                layer_stack[depth] = true;
                clip_scope_stack[depth] = layer_clip_scope;
                const auto layer = read_record<progpu_native_scene_layer>(command.payload);
                if ((layer.flags & PROGPU_NATIVE_SCENE_LAYER_COMPOSITE_STATE) != 0U) {
                    const auto composite = read_record<progpu_native_scene_state>(
                        implementation_->resources[layer.reserved0].payload);
                    if ((composite.flags & PROGPU_NATIVE_SCENE_STATE_CLIP_RECT) != 0U) {
                        const auto clip = layer_clip_scope == 0U ? composite.clip_rect :
                            intersect_hit_clips(composite.clip_rect, layer_clips[layer_clip_scope]);
                        if (!std::isfinite(clip.x + clip.width) || !std::isfinite(clip.y + clip.height))
                            return unsupported();
                        layer_clip_scope = static_cast<std::uint32_t>(layer_clips.size());
                        layer_clips.push_back(clip);
                    }
                }
                stack[depth++] = current_state;
                continue;
            }
            if (kind == PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
                if (depth == 0U || !layer_stack[depth - 1U]) return unsupported();
                current_state = stack[--depth];
                layer_clip_scope = clip_scope_stack[depth];
                continue;
            }
            if (!owner) continue;
            const auto state_index = command.record.state_index == PROGPU_NATIVE_SCENE_NO_INDEX
                ? current_state : command.record.state_index;
            const auto state = state_index == PROGPU_NATIVE_SCENE_NO_INDEX ? identity_state() :
                read_record<progpu_native_scene_state>(implementation_->resources[state_index].payload);
            if ((state.flags & ~input_state_flags) != 0U) return unsupported();
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
                    constexpr std::uint32_t allowed = PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED |
                        PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK | PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK;
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
                    if (source.kind != PROGPU_NATIVE_SCENE_STROKE_POLYLINE || source.dash_interval_count != 0U ||
                        (source.flags & ~allowed) != 0U || (source.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) == 0U ||
                        source.point_count < 3U || source.stroke_thickness <= 0.0001F) return unsupported();
                    const auto transform = compose_affine(source.transform, state.transform);
                    float maximum_scale{}, minimum_scale{};
                    if (!try_get_stroke_scales(transform, maximum_scale, minimum_scale)) return unsupported();
                    const bool affine_outline = requires_affine_stroke_geometry(transform);
                    const auto join_transform = affine_outline ? transform : identity_transform();
                    const auto point = [&](std::size_t index) {
                        return read_record<progpu_native_point>(resource.auxiliary, source.point_offset + index);
                    };
                    const bool wpf_joins = (source.flags & PROGPU_NATIVE_POLYLINE_FLAG_WPF_JOIN_SEMANTICS) != 0U;
                    // Same closed traversal and join construction as append_polyline.
                    // Flat line bodies plus real join triangles form a union under
                    // one owner; canonical query output deduplicates that owner.
                    for (std::size_t edge = 0U; edge < source.point_count; ++edge) {
                        const auto first = point(edge), corner = point((edge + 1U) % source.point_count);
                        const auto last = point((edge + 2U) % source.point_count);
                        if (!append_line(first, corner, source.stroke_thickness, PROGPU_NATIVE_STROKE_CAP_FLAT,
                            PROGPU_NATIVE_STROKE_CAP_FLAT, transform, state, state_index)) return unsupported();
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

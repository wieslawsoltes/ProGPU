#pragma once

#include "progpu_native_shader_capture_frame.hpp"

#include <array>
#include <bit>
#include <cmath>
#include <cstdint>

namespace progpu::native::shader_effect {

inline float source_quotient(float numerator, float denominator) noexcept {
    volatile float result = numerator / denominator;
    return result;
}

// A row-vector diagonal 4x4 with an independent homogeneous coordinate.
// Do not collapse w into 1: the original full cofactor inverse can retain a
// nonunit w even though the original input was an ordinary affine transform.
struct axis_matrix final {
    float x{1.0F}, y{1.0F}, z{1.0F}, w{1.0F};
    float tx{}, ty{};
    // Used only by the additive affine-frame arithmetic. Legacy axis helpers
    // and their exact operation order remain independent.
    float xy{}, yx{};
};

inline bool finite_axis_matrix(const axis_matrix& matrix) noexcept {
    return std::isfinite(matrix.x) && std::isfinite(matrix.y) &&
        std::isfinite(matrix.z) && std::isfinite(matrix.w) &&
        std::isfinite(matrix.tx) && std::isfinite(matrix.ty);
}

inline axis_matrix multiply_axis_matrix(const axis_matrix& local, const axis_matrix& parent) noexcept {
    return {source_product(local.x, parent.x), source_product(local.y, parent.y),
        source_product(local.z, parent.z), source_product(local.w, parent.w),
        source_sum(source_product(local.tx, parent.x), source_product(local.w, parent.tx)),
        source_sum(source_product(local.ty, parent.y), source_product(local.w, parent.ty))};
}

// Independent sparse cofactor reduction. This is full 4x4 arithmetic, not six
// independent reciprocal shortcuts or an identity residual repair. Original SDK
// bit-pattern controls are authored separately; source authoring is not proof
// that an unexecuted provider/SDK comparison has passed.
inline bool inverse_axis_matrix(const axis_matrix& matrix, axis_matrix& output) noexcept {
    if (!finite_axis_matrix(matrix)) return false;
    const float xy = source_product(matrix.x, matrix.y);
    const float zw = source_product(matrix.z, matrix.w);
    const float determinant = source_product(xy, zw);
    if (!std::isfinite(determinant) || determinant == 0.0F) return false;
    const float reciprocal = source_quotient(1.0F, determinant);
    const float yz = source_product(matrix.y, matrix.z);
    const float xz = source_product(matrix.x, matrix.z);
    const axis_matrix candidate{
        source_product(source_product(matrix.y, zw), reciprocal),
        source_product(source_product(matrix.x, zw), reciprocal),
        source_product(source_product(xy, matrix.w), reciprocal),
        source_product(source_product(xy, matrix.z), reciprocal),
        source_product(-source_product(yz, matrix.tx), reciprocal),
        source_product(-source_product(xz, matrix.ty), reciprocal)};
    if (!finite_axis_matrix(candidate)) return false;
    output = candidate;
    return true;
}

struct sample_lattice final {
    std::int32_t x{}, y{};
    std::uint32_t width{}, height{};
    bool operator==(const sample_lattice&) const noexcept = default;
};

// Original per-push float history is supplied by the actual source traversal,
// not reconstructed from an arbitrary generic scene transform.
struct sample_frame_request final {
    float local_left{}, local_top{}, local_right{}, local_bottom{};
    axis_matrix source_to_device;
};

struct sample_frame final {
    axis_matrix source_scale;
    axis_matrix inverse_scale;
    axis_matrix residual;
    sample_lattice capture;
    axis_matrix capture_to_device;
    axis_matrix unit_to_device;
    axis_matrix device_to_unit;
    sample_lattice output;
    bool affine{};
};

struct sample_projection final {
    axis_matrix unit_to_clip;
    float reciprocal_width{}, reciprocal_height{};
};

// Original target-dependent projection is composed AFTER the retained unit
// quad. Keep each original float operation; projecting with the effect's small
// output extent and cancelling it later would change this operation history.
inline bool project_sample_frame(const sample_frame& frame, const sample_lattice& target,
    sample_projection& output) noexcept {
    if (target.width == 0U || target.height == 0U) return false;
    sample_projection candidate{};
    candidate.reciprocal_width = source_quotient(1.0F, static_cast<float>(target.width));
    candidate.reciprocal_height = source_quotient(1.0F, static_cast<float>(target.height));
    axis_matrix viewport{};
    viewport.x = source_product(2.0F, candidate.reciprocal_width);
    viewport.y = source_product(-2.0F, candidate.reciprocal_height);
    viewport.tx = -source_sum(1.0F, candidate.reciprocal_width);
    viewport.ty = source_sum(1.0F, candidate.reciprocal_height);
    const axis_matrix target_origin{1.0F, 1.0F, 1.0F, 1.0F,
        -static_cast<float>(target.x), -static_cast<float>(target.y)};
    candidate.unit_to_clip = multiply_axis_matrix(
        multiply_axis_matrix(frame.unit_to_device, target_origin), viewport);
    if (!finite_axis_matrix(candidate.unit_to_clip)) return false;
    output = candidate;
    return true;
}

inline bool bounded_sample_lattice(double left, double top, double right, double bottom,
    sample_lattice& output) noexcept {
    constexpr double exact_integer_limit = 1U << 24U;
    if (!std::isfinite(left) || !std::isfinite(top) || !std::isfinite(right) || !std::isfinite(bottom) ||
        left != std::floor(left) || top != std::floor(top) || right != std::floor(right) || bottom != std::floor(bottom) ||
        left < -exact_integer_limit || top < -exact_integer_limit || right > exact_integer_limit || bottom > exact_integer_limit ||
        right <= left || bottom <= top || right - left > 16'384.0 || bottom - top > 16'384.0) return false;
    output = {static_cast<std::int32_t>(left), static_cast<std::int32_t>(top),
        static_cast<std::uint32_t>(right - left), static_cast<std::uint32_t>(bottom - top)};
    return true;
}

// Capture and output are independent lattices. No target clip participates in
// allocation or UV normalization, and no effect expression is evaluated here.
// Fixed arithmetic, no allocation and no hidden expansion of the existing
// per-axis resource budget. Publication is atomic on every failure.
inline bool create_sample_frame(const sample_frame_request& request, sample_frame& output) noexcept {
    const auto& world = request.source_to_device;
    if (!finite_axis_matrix(world) || world.x <= 0.0F || world.y <= 0.0F || world.z != 1.0F || world.w != 1.0F ||
        !std::isfinite(request.local_left) || !std::isfinite(request.local_top) ||
        !std::isfinite(request.local_right) || !std::isfinite(request.local_bottom) ||
        request.local_right <= request.local_left || request.local_bottom <= request.local_top) return false;
    sample_frame candidate{};
    candidate.source_scale.x = std::sqrt(source_product(world.x, world.x));
    candidate.source_scale.y = std::sqrt(source_product(world.y, world.y));
    if (candidate.source_scale.x <= 0.0F || candidate.source_scale.y <= 0.0F ||
        !inverse_axis_matrix(candidate.source_scale, candidate.inverse_scale)) return false;
    candidate.residual = multiply_axis_matrix(candidate.inverse_scale, world);
    if (!finite_axis_matrix(candidate.residual) || candidate.residual.x <= 0.0F ||
        candidate.residual.y <= 0.0F || candidate.residual.w <= 0.0F) return false;
    if (!bounded_sample_lattice(
        std::floor(source_product(request.local_left, candidate.source_scale.x)),
        std::floor(source_product(request.local_top, candidate.source_scale.y)),
        std::ceil(source_product(request.local_right, candidate.source_scale.x)),
        std::ceil(source_product(request.local_bottom, candidate.source_scale.y)), candidate.capture)) return false;
    const axis_matrix origin{1.0F, 1.0F, 1.0F, 1.0F,
        static_cast<float>(candidate.capture.x), static_cast<float>(candidate.capture.y)};
    candidate.capture_to_device = multiply_axis_matrix(origin, candidate.residual);
    const axis_matrix extent{static_cast<float>(candidate.capture.width), static_cast<float>(candidate.capture.height)};
    candidate.unit_to_device = multiply_axis_matrix(extent, candidate.capture_to_device);
    if (!inverse_axis_matrix(candidate.unit_to_device, candidate.device_to_unit)) return false;
    const auto& quad = candidate.unit_to_device;
    const float left = source_quotient(quad.tx, quad.w), top = source_quotient(quad.ty, quad.w);
    const float right = source_quotient(source_sum(quad.x, quad.tx), quad.w);
    const float bottom = source_quotient(source_sum(quad.y, quad.ty), quad.w);
    if (!bounded_sample_lattice(std::floor(left), std::floor(top), std::ceil(right), std::ceil(bottom), candidate.output))
        return false;
    output = candidate;
    return true;
}

inline sample_frame_request sample_request(const progpu_native_scene_shader_sample_frame& frame) noexcept {
    return {frame.local_left, frame.local_top, frame.local_right, frame.local_bottom,
        {frame.source_scale_x, frame.source_scale_y, 1.0F, 1.0F, frame.source_offset_x, frame.source_offset_y}};
}

// The wire owns redundant derived values deliberately: validation proves that
// resource import did not silently mix an allocation from another generation.
inline bool complete_sample_frame(progpu_native_scene_shader_sample_frame& frame) noexcept {
    sample_frame derived{};
    if (!create_sample_frame(sample_request(frame), derived)) return false;
    auto candidate = frame;
    candidate.capture_x = derived.capture.x; candidate.capture_y = derived.capture.y;
    candidate.capture_width = derived.capture.width; candidate.capture_height = derived.capture.height;
    candidate.output_x = derived.output.x; candidate.output_y = derived.output.y;
    candidate.output_width = derived.output.width; candidate.output_height = derived.output.height;
    candidate.quad_x = derived.unit_to_device.x; candidate.quad_y = derived.unit_to_device.y;
    candidate.quad_z = derived.unit_to_device.z; candidate.quad_w = derived.unit_to_device.w;
    candidate.quad_offset_x = derived.unit_to_device.tx; candidate.quad_offset_y = derived.unit_to_device.ty;
    frame = candidate;
    return true;
}

inline bool validate_sample_frame(const progpu_native_scene_shader_sample_frame& frame) noexcept {
    if (!std::isfinite(frame.source_dpi_x) || !std::isfinite(frame.source_dpi_y) ||
        frame.source_dpi_x <= 0.0 || frame.source_dpi_y <= 0.0 || frame.reserved != 0U ||
        frame.clip_antialias > 1U || !std::isfinite(frame.clip_left) || !std::isfinite(frame.clip_top) ||
        !std::isfinite(frame.clip_right) || !std::isfinite(frame.clip_bottom) ||
        frame.clip_right < frame.clip_left || frame.clip_bottom < frame.clip_top) return false;
    if (frame.clip_left != std::floor(frame.clip_left) ||
        frame.clip_top != std::floor(frame.clip_top) || frame.clip_right != std::floor(frame.clip_right) ||
        frame.clip_bottom != std::floor(frame.clip_bottom)) return false;
    auto expected = frame;
    if (!complete_sample_frame(expected)) return false;
    const auto same = [](float left, float right) {
        return std::bit_cast<std::uint32_t>(left) == std::bit_cast<std::uint32_t>(right);
    };
    return frame.capture_x == expected.capture_x && frame.capture_y == expected.capture_y &&
        frame.capture_width == expected.capture_width && frame.capture_height == expected.capture_height &&
        frame.output_x == expected.output_x && frame.output_y == expected.output_y &&
        frame.output_width == expected.output_width && frame.output_height == expected.output_height &&
        same(frame.quad_x, expected.quad_x) && same(frame.quad_y, expected.quad_y) &&
        same(frame.quad_z, expected.quad_z) && same(frame.quad_w, expected.quad_w) &&
        same(frame.quad_offset_x, expected.quad_offset_x) && same(frame.quad_offset_y, expected.quad_offset_y);
}

} // namespace progpu::native::shader_effect

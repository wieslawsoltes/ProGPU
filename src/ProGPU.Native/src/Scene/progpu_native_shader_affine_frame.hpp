#pragma once

#include "progpu_native_shader_sample_frame.hpp"
#include <algorithm>
#include <limits>

namespace progpu::native::shader_effect {

// Clean-room sparse 2D homogeneous matrix algebra. Values are row-vector
// coefficients, each operation publishes one float; no aggregate double
// reconstruction, fused multiply-add, angle approximation or identity repair.
inline bool finite_affine_matrix(const axis_matrix& value) noexcept {
    return finite_axis_matrix(value) && std::isfinite(value.xy) && std::isfinite(value.yx);
}

inline axis_matrix multiply_affine_matrix(const axis_matrix& a, const axis_matrix& b) noexcept {
    return {
        source_sum(source_product(a.x, b.x), source_product(a.xy, b.yx)),
        source_sum(source_product(a.yx, b.xy), source_product(a.y, b.y)),
        source_product(a.z, b.z), source_product(a.w, b.w),
        source_sum(source_sum(source_product(a.tx, b.x), source_product(a.ty, b.yx)), source_product(a.w, b.tx)),
        source_sum(source_sum(source_product(a.tx, b.xy), source_product(a.ty, b.y)), source_product(a.w, b.ty)),
        source_sum(source_product(a.x, b.xy), source_product(a.xy, b.y)),
        source_sum(source_product(a.yx, b.x), source_product(a.y, b.yx))};
}

inline bool inverse_affine_matrix(const axis_matrix& value, axis_matrix& output) noexcept {
    if (!finite_affine_matrix(value)) return false;
    const float determinant_xy = source_sum(source_product(value.x, value.y), -source_product(value.xy, value.yx));
    const float zw = source_product(value.z, value.w);
    const float determinant = source_product(determinant_xy, zw);
    if (!std::isfinite(determinant) || determinant == 0.0F) return false;
    const float reciprocal = source_quotient(1.0F, determinant);
    const axis_matrix candidate{
        source_product(source_product(value.y, zw), reciprocal),
        source_product(source_product(value.x, zw), reciprocal),
        source_product(source_product(determinant_xy, value.w), reciprocal),
        source_product(source_product(determinant_xy, value.z), reciprocal),
        source_product(source_product(source_sum(source_product(value.yx, value.ty), -source_product(value.y, value.tx)), value.z), reciprocal),
        source_product(source_product(source_sum(source_product(value.xy, value.tx), -source_product(value.x, value.ty)), value.z), reciprocal),
        source_product(-source_product(value.xy, zw), reciprocal),
        source_product(-source_product(value.yx, zw), reciprocal)};
    if (!finite_affine_matrix(candidate)) return false;
    output = candidate;
    return true;
}

inline std::array<float, 2U> affine_point(const axis_matrix& matrix, float x, float y) noexcept {
    return {source_sum(source_sum(source_product(x, matrix.x), source_product(y, matrix.yx)), matrix.tx),
        source_sum(source_sum(source_product(x, matrix.xy), source_product(y, matrix.y)), matrix.ty)};
}

inline std::array<float, 4U> affine_bounds(const axis_matrix& matrix,
    const std::array<float, 4U>& bounds) noexcept {
    const std::array corners{affine_point(matrix, bounds[0], bounds[1]),
        affine_point(matrix, bounds[2], bounds[1]), affine_point(matrix, bounds[0], bounds[3]),
        affine_point(matrix, bounds[2], bounds[3])};
    auto result = std::array{corners[0][0], corners[0][1], corners[0][0], corners[0][1]};
    for (const auto& point : corners) {
        if (!std::isfinite(point[0]) || !std::isfinite(point[1])) {
            result.fill(std::numeric_limits<float>::quiet_NaN());
            return result;
        }
        result[0] = std::min(result[0], point[0]); result[1] = std::min(result[1], point[1]);
        result[2] = std::max(result[2], point[0]); result[3] = std::max(result[3], point[1]);
    }
    return result;
}

inline bool create_affine_sample_frame(const sample_frame_request& request, sample_frame& output) noexcept {
    const auto& world = request.source_to_device;
    if (!finite_affine_matrix(world) || world.z != 1.0F || world.w != 1.0F ||
        !std::isfinite(request.local_left) || !std::isfinite(request.local_top) ||
        !std::isfinite(request.local_right) || !std::isfinite(request.local_bottom) ||
        request.local_right <= request.local_left || request.local_bottom <= request.local_top) return false;
    sample_frame candidate{};
    candidate.affine = true;
    candidate.source_scale.x = std::sqrt(source_sum(source_product(world.x, world.x), source_product(world.xy, world.xy)));
    candidate.source_scale.y = std::sqrt(source_sum(source_product(world.yx, world.yx), source_product(world.y, world.y)));
    if (candidate.source_scale.x <= 0.0F || candidate.source_scale.y <= 0.0F ||
        !inverse_axis_matrix(candidate.source_scale, candidate.inverse_scale)) return false;
    candidate.residual = multiply_affine_matrix(candidate.inverse_scale, world);
    if (!finite_affine_matrix(candidate.residual) || candidate.residual.w <= 0.0F) return false;
    if (!bounded_sample_lattice(std::floor(source_product(request.local_left, candidate.source_scale.x)),
            std::floor(source_product(request.local_top, candidate.source_scale.y)),
            std::ceil(source_product(request.local_right, candidate.source_scale.x)),
            std::ceil(source_product(request.local_bottom, candidate.source_scale.y)), candidate.capture)) return false;
    const axis_matrix origin{1, 1, 1, 1, static_cast<float>(candidate.capture.x), static_cast<float>(candidate.capture.y)};
    candidate.capture_to_device = multiply_affine_matrix(origin, candidate.residual);
    const axis_matrix extent{static_cast<float>(candidate.capture.width), static_cast<float>(candidate.capture.height)};
    candidate.unit_to_device = multiply_affine_matrix(extent, candidate.capture_to_device);
    if (!inverse_affine_matrix(candidate.unit_to_device, candidate.device_to_unit)) return false;
    auto bounds = affine_bounds(candidate.unit_to_device, {0, 0, 1, 1});
    for (auto& value : bounds) value = source_quotient(value, candidate.unit_to_device.w);
    if (!bounded_sample_lattice(std::floor(bounds[0]), std::floor(bounds[1]),
            std::ceil(bounds[2]), std::ceil(bounds[3]), candidate.output)) return false;
    output = candidate;
    return true;
}

inline bool project_affine_sample_frame(const sample_frame& frame, const sample_lattice& target,
    sample_projection& output) noexcept {
    if (target.width == 0U || target.height == 0U) return false;
    sample_projection candidate{};
    candidate.reciprocal_width = source_quotient(1, static_cast<float>(target.width));
    candidate.reciprocal_height = source_quotient(1, static_cast<float>(target.height));
    axis_matrix viewport{};
    viewport.x = source_product(2, candidate.reciprocal_width);
    viewport.y = source_product(-2, candidate.reciprocal_height);
    viewport.tx = -source_sum(1, candidate.reciprocal_width);
    viewport.ty = source_sum(1, candidate.reciprocal_height);
    const axis_matrix origin{1, 1, 1, 1, -static_cast<float>(target.x), -static_cast<float>(target.y)};
    candidate.unit_to_clip = multiply_affine_matrix(multiply_affine_matrix(frame.unit_to_device, origin), viewport);
    if (!finite_affine_matrix(candidate.unit_to_clip)) return false;
    output = candidate;
    return true;
}

inline sample_frame_request sample_request(const progpu_native_scene_shader_affine_frame& frame) noexcept {
    auto result = sample_request(frame.placement);
    result.source_to_device.xy = frame.source_m12;
    result.source_to_device.yx = frame.source_m21;
    return result;
}

inline bool complete_affine_frame(progpu_native_scene_shader_affine_frame& frame) noexcept {
    sample_frame derived{};
    if (!create_affine_sample_frame(sample_request(frame), derived)) return false;
    auto candidate = frame;
    auto& p = candidate.placement;
    p.capture_x = derived.capture.x; p.capture_y = derived.capture.y;
    p.capture_width = derived.capture.width; p.capture_height = derived.capture.height;
    p.output_x = derived.output.x; p.output_y = derived.output.y;
    p.output_width = derived.output.width; p.output_height = derived.output.height;
    p.quad_x = derived.unit_to_device.x; p.quad_y = derived.unit_to_device.y;
    p.quad_z = derived.unit_to_device.z; p.quad_w = derived.unit_to_device.w;
    p.quad_offset_x = derived.unit_to_device.tx; p.quad_offset_y = derived.unit_to_device.ty;
    candidate.quad_m12 = derived.unit_to_device.xy; candidate.quad_m21 = derived.unit_to_device.yx;
    frame = candidate;
    return true;
}

inline bool validate_affine_frame(const progpu_native_scene_shader_affine_frame& frame) noexcept {
    const auto& p = frame.placement;
    if (!std::isfinite(p.source_dpi_x) || !std::isfinite(p.source_dpi_y) || p.source_dpi_x <= 0 || p.source_dpi_y <= 0 ||
        p.reserved != 0U || p.clip_antialias > 1U || !std::isfinite(p.clip_left) || !std::isfinite(p.clip_top) ||
        !std::isfinite(p.clip_right) || !std::isfinite(p.clip_bottom) || p.clip_right < p.clip_left || p.clip_bottom < p.clip_top ||
        p.clip_left != std::floor(p.clip_left) || p.clip_top != std::floor(p.clip_top) ||
        p.clip_right != std::floor(p.clip_right) || p.clip_bottom != std::floor(p.clip_bottom)) return false;
    auto expected = frame;
    if (!complete_affine_frame(expected)) return false;
    const auto& e = expected.placement;
    const auto same = [](float a, float b) { return std::bit_cast<std::uint32_t>(a) == std::bit_cast<std::uint32_t>(b); };
    return p.capture_x == e.capture_x && p.capture_y == e.capture_y && p.capture_width == e.capture_width &&
        p.capture_height == e.capture_height && p.output_x == e.output_x && p.output_y == e.output_y &&
        p.output_width == e.output_width && p.output_height == e.output_height && same(p.quad_x,e.quad_x) &&
        same(p.quad_y,e.quad_y) && same(p.quad_z,e.quad_z) && same(p.quad_w,e.quad_w) &&
        same(p.quad_offset_x,e.quad_offset_x) && same(p.quad_offset_y,e.quad_offset_y) &&
        same(frame.quad_m12,expected.quad_m12) && same(frame.quad_m21,expected.quad_m21);
}
} // namespace progpu::native::shader_effect

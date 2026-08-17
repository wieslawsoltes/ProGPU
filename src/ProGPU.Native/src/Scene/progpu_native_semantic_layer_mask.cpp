#include "progpu_native_semantic_layer_mask.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstring>
#include <limits>

namespace progpu::native::semantic {
namespace {

bool valid_transform(const progpu_native_affine_2d& transform) noexcept {
    const double determinant =
        static_cast<double>(transform.m11) * transform.m22 -
        static_cast<double>(transform.m12) * transform.m21;
    if (!std::isfinite(transform.m11) ||
        !std::isfinite(transform.m12) ||
        !std::isfinite(transform.m21) ||
        !std::isfinite(transform.m22) ||
        !std::isfinite(transform.m31) ||
        !std::isfinite(transform.m32) ||
        !std::isfinite(determinant) || std::abs(determinant) <= 0.000001) {
        return false;
    }
    const double inverse = 1.0 / determinant;
    const std::array<double, 6U> values{
        transform.m22 * inverse,
        -transform.m12 * inverse,
        -transform.m21 * inverse,
        transform.m11 * inverse,
        (static_cast<double>(transform.m21) * transform.m32 -
            static_cast<double>(transform.m22) * transform.m31) * inverse,
        (static_cast<double>(transform.m12) * transform.m31 -
            static_cast<double>(transform.m11) * transform.m32) * inverse};
    return std::ranges::all_of(values, [](double value) noexcept {
        return std::isfinite(value) &&
            value >= -std::numeric_limits<float>::max() &&
            value <= std::numeric_limits<float>::max();
    });
}

bool valid_bounds(const progpu_native_image_rect& bounds) noexcept {
    return std::isfinite(bounds.x) && std::isfinite(bounds.y) &&
        std::isfinite(bounds.width) && std::isfinite(bounds.height) &&
        bounds.width > 0.0F && bounds.height > 0.0F;
}

bool valid_analytic(const progpu_native_scene_layer_mask& mask) noexcept {
    const auto radius = [](float value) noexcept {
        return std::isfinite(value) && value >= 0.0F;
    };
    return mask.struct_size == sizeof(mask) &&
        mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE &&
        mask.flags == 0U && mask.reserved == 0U &&
        mask.reserved0 == 0U && mask.reserved1 == 0U &&
        mask.reserved2 == 0U && valid_bounds(mask.bounds) &&
        valid_transform(mask.transform) &&
        std::ranges::all_of(mask.corner_radii_x, radius) &&
        std::ranges::all_of(mask.corner_radii_y, radius) &&
        std::isfinite(mask.opacity) && mask.opacity >= 0.0F &&
        mask.opacity <= 1.0F;
}

bool valid_coverage(
    const progpu_native_scene_layer_coverage_mask& mask,
    std::uint32_t auxiliary_size) noexcept {
    const std::uint64_t required = mask.height == 0U
        ? 0U
        : static_cast<std::uint64_t>(mask.row_bytes) *
                (mask.height - 1U) +
            mask.width;
    return mask.struct_size == sizeof(mask) &&
        mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP &&
        mask.flags == 0U && mask.reserved0 == 0U &&
        mask.reserved1 == 0U && mask.width > 0U &&
        mask.width <= 16384U && mask.height > 0U &&
        mask.height <= 16384U && mask.row_bytes >= mask.width &&
        (mask.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST ||
            mask.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR) &&
        required == auxiliary_size && valid_bounds(mask.bounds) &&
        valid_transform(mask.transform) && std::isfinite(mask.opacity) &&
        mask.opacity >= 0.0F && mask.opacity <= 1.0F;
}

bool valid_chain(const progpu_native_scene_layer_mask_chain& chain) noexcept {
    if (chain.struct_size != sizeof(chain) ||
        chain.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN ||
        chain.flags != 0U || chain.mask_count < 2U ||
        chain.mask_count > PROGPU_NATIVE_SCENE_MAX_ANALYTIC_MASKS) {
        return false;
    }
    for (std::uint32_t index = 0U;
         index < PROGPU_NATIVE_SCENE_MAX_ANALYTIC_MASKS;
         ++index) {
        if (index < chain.mask_count) {
            if (!valid_analytic(chain.masks[index])) {
                return false;
            }
        } else {
            const progpu_native_scene_layer_mask zero{};
            if (std::memcmp(&chain.masks[index], &zero, sizeof(zero)) != 0) {
                return false;
            }
        }
    }
    return true;
}

bool valid_vector_segment(
    const progpu_native_path_segment& segment) noexcept {
    const bool arc = segment.kind == PROGPU_NATIVE_PATH_SEGMENT_ARC;
    return segment.kind <= PROGPU_NATIVE_PATH_SEGMENT_ARC &&
        std::isfinite(segment.p0.x) && std::isfinite(segment.p0.y) &&
        std::isfinite(segment.p1.x) && std::isfinite(segment.p1.y) &&
        std::isfinite(segment.p2.x) && std::isfinite(segment.p2.y) &&
        std::isfinite(segment.p3.x) && std::isfinite(segment.p3.y) &&
        (arc
            ? segment.p3.x > 0.0F && segment.p3.y > 0.0F &&
                std::isfinite(std::bit_cast<float>(segment.pad0)) &&
                std::isfinite(std::bit_cast<float>(segment.pad1)) &&
                std::isfinite(std::bit_cast<float>(segment.pad2))
            : segment.pad0 == 0U && segment.pad1 == 0U &&
                segment.pad2 == 0U);
}

bool valid_vector_path(
    const progpu_native_scene_clip_path& path,
    std::uint32_t segment_count) noexcept {
    return path.segment_count > 0U &&
        path.segment_offset <= segment_count &&
        path.segment_count <= segment_count - path.segment_offset &&
        std::isfinite(path.min_x) && std::isfinite(path.min_y) &&
        std::isfinite(path.max_x) && std::isfinite(path.max_y) &&
        path.max_x > path.min_x && path.max_y > path.min_y &&
        valid_transform(path.transform) &&
        path.fill_rule <= PROGPU_NATIVE_FILL_RULE_EVEN_ODD &&
        (path.sample_grid == 4U || path.sample_grid == 8U) &&
        path.operation <= PROGPU_NATIVE_CLIP_DIFFERENCE &&
        path.reserved == 0U;
}

bool valid_vector(
    const progpu_native_scene_layer_vector_mask& mask,
    const progpu_native_scene_clip_path* paths,
    const progpu_native_path_segment* segments,
    std::uint32_t auxiliary_size) noexcept {
    if (mask.struct_size != sizeof(mask) ||
        mask.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
        mask.flags != 0U || mask.path_count == 0U ||
        mask.path_count > 64U || mask.segment_count == 0U ||
        !std::isfinite(mask.opacity) || mask.opacity < 0.0F ||
        mask.opacity > 1.0F || mask.reserved0 != 0U ||
        mask.reserved1 != 0U || paths == nullptr || segments == nullptr) {
        return false;
    }
    const std::uint64_t path_bytes =
        static_cast<std::uint64_t>(mask.path_count) * sizeof(*paths);
    const std::uint64_t segment_bytes =
        static_cast<std::uint64_t>(mask.segment_count) * sizeof(*segments);
    if (path_bytes + segment_bytes != auxiliary_size) {
        return false;
    }
    for (std::uint32_t index = 0U; index < mask.path_count; ++index) {
        if (!valid_vector_path(paths[index], mask.segment_count)) {
            return false;
        }
    }
    for (std::uint32_t index = 0U; index < mask.segment_count; ++index) {
        if (!valid_vector_segment(segments[index])) {
            return false;
        }
    }
    return true;
}

} // namespace

bool is_valid_semantic_layer_mask(
    const progpu_native_scene_layer_mask& mask) noexcept {
    return valid_analytic(mask);
}

bool is_valid_semantic_layer_coverage_mask(
    const progpu_native_scene_layer_coverage_mask& mask,
    std::uint64_t auxiliary_size) noexcept {
    return auxiliary_size <= std::numeric_limits<std::uint32_t>::max() &&
        valid_coverage(mask, static_cast<std::uint32_t>(auxiliary_size));
}

bool is_valid_semantic_layer_mask_chain(
    const progpu_native_scene_layer_mask_chain& chain) noexcept {
    return valid_chain(chain);
}

bool is_valid_semantic_layer_vector_mask(
    const progpu_native_scene_layer_vector_mask& mask,
    std::span<const progpu_native_scene_clip_path> paths,
    std::span<const progpu_native_path_segment> segments) noexcept {
    if (paths.size() != mask.path_count ||
        segments.size() != mask.segment_count) {
        return false;
    }
    const std::uint64_t auxiliary_size =
        static_cast<std::uint64_t>(paths.size_bytes()) +
        segments.size_bytes();
    return auxiliary_size <= std::numeric_limits<std::uint32_t>::max() &&
        valid_vector(
            mask,
            paths.data(),
            segments.data(),
            static_cast<std::uint32_t>(auxiliary_size));
}

bool validate_layer_mask_resource(
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    std::uint32_t& error_offset,
    semantic_layer_mask* parsed) noexcept {
    error_offset = resource.payload_offset;
    if (bytes == nullptr || resource.payload_size < sizeof(std::uint32_t) * 2U) {
        return false;
    }
    std::uint32_t kind = 0U;
    std::memcpy(&kind, bytes + resource.payload_offset + sizeof(std::uint32_t),
        sizeof(kind));
    semantic_layer_mask result{};
    result.kind = kind;
    if (kind == PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE) {
        if (resource.payload_size != sizeof(result.analytic) ||
            resource.auxiliary_size != 0U) {
            return false;
        }
        std::memcpy(&result.analytic, bytes + resource.payload_offset,
            sizeof(result.analytic));
        if (!valid_analytic(result.analytic)) {
            return false;
        }
    } else if (kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP) {
        if (resource.payload_size != sizeof(result.coverage) ||
            resource.auxiliary_size == 0U) {
            return false;
        }
        std::memcpy(&result.coverage, bytes + resource.payload_offset,
            sizeof(result.coverage));
        if (!valid_coverage(result.coverage, resource.auxiliary_size)) {
            return false;
        }
    } else if (kind == PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN) {
        if (resource.payload_size != sizeof(result.chain) ||
            resource.auxiliary_size != 0U) {
            return false;
        }
        std::memcpy(&result.chain, bytes + resource.payload_offset,
            sizeof(result.chain));
        if (!valid_chain(result.chain)) {
            return false;
        }
    } else if (kind == PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN) {
        if (resource.payload_size != sizeof(result.vector) ||
            resource.auxiliary_size == 0U) {
            return false;
        }
        std::memcpy(&result.vector, bytes + resource.payload_offset,
            sizeof(result.vector));
        const std::uint64_t path_bytes =
            static_cast<std::uint64_t>(result.vector.path_count) *
                sizeof(progpu_native_scene_clip_path);
        if (path_bytes > resource.auxiliary_size) {
            return false;
        }
        result.vector_paths =
            reinterpret_cast<const progpu_native_scene_clip_path*>(
                bytes + resource.auxiliary_offset);
        result.vector_segments =
            reinterpret_cast<const progpu_native_path_segment*>(
                bytes + resource.auxiliary_offset + path_bytes);
        if (!valid_vector(
                result.vector,
                result.vector_paths,
                result.vector_segments,
                resource.auxiliary_size)) {
            return false;
        }
    } else {
        return false;
    }
    if (parsed != nullptr) {
        *parsed = result;
    }
    return true;
}

} // namespace progpu::native::semantic

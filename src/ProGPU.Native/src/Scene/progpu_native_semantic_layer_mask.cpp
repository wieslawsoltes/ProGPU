#include "progpu_native_semantic_layer_mask.hpp"
#include "progpu_native_semantic_brush.hpp"
#include "progpu_native_path_boolean_validation.hpp"
#include "progpu_native_geometry_stroke.hpp"

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

bool valid_nested_scene_header(
    const std::byte* stream,
    std::uint32_t stream_size) noexcept {
    if (stream == nullptr ||
        stream_size < sizeof(progpu_native_scene_header) ||
        stream_size > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES) {
        return false;
    }
    progpu_native_scene_header header{};
    std::memcpy(&header, stream, sizeof(header));
    return header.struct_size >= sizeof(header) &&
        header.magic == PROGPU_NATIVE_SCENE_STREAM_MAGIC &&
        header.stream_version == PROGPU_NATIVE_SCENE_STREAM_VERSION &&
        header.endian_marker == PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER &&
        header.flags == 0U && header.total_size == stream_size &&
        header.scene_id != 0U && header.generation != 0U &&
        header.reserved0 == 0U && header.reserved1 == 0U;
}

bool valid_picture_mask(
    const progpu_native_scene_layer_picture_mask& mask,
    const std::byte* streams,
    std::uint32_t stream_bytes) noexcept {
    return mask.struct_size == sizeof(mask) &&
        mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE &&
        mask.flags == 0U && mask.reserved0 == 0U &&
        mask.reserved1 == 0U && mask.stream_size != 0U &&
        mask.stream_offset <= stream_bytes &&
        mask.stream_size <= stream_bytes - mask.stream_offset &&
        valid_bounds(mask.bounds) && valid_transform(mask.transform) &&
        std::isfinite(mask.opacity) && mask.opacity >= 0.0F &&
        mask.opacity <= 1.0F &&
        valid_nested_scene_header(
            streams + mask.stream_offset,
            mask.stream_size);
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
    const bool rational = segment.kind ==
        PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_QUADRATIC;
    const float rational_weight = std::bit_cast<float>(segment.pad0);
    const double rational_scale = std::fmax(1.0, std::fmax(
        std::fmax(std::abs(segment.p0.x), std::abs(segment.p0.y)),
        std::fmax(
            std::fmax(std::abs(segment.p1.x), std::abs(segment.p1.y)),
            std::fmax(std::abs(segment.p2.x), std::abs(segment.p2.y)))));
    const double rational_weight_limit =
        std::numeric_limits<float>::max() / (4.0 * rational_scale);
    return segment.kind <= PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_QUADRATIC &&
        std::isfinite(segment.p0.x) && std::isfinite(segment.p0.y) &&
        std::isfinite(segment.p1.x) && std::isfinite(segment.p1.y) &&
        std::isfinite(segment.p2.x) && std::isfinite(segment.p2.y) &&
        std::isfinite(segment.p3.x) && std::isfinite(segment.p3.y) &&
        (arc
            ? segment.p3.x > 0.0F && segment.p3.y > 0.0F &&
                std::isfinite(std::bit_cast<float>(segment.pad0)) &&
                std::isfinite(std::bit_cast<float>(segment.pad1)) &&
                std::isfinite(std::bit_cast<float>(segment.pad2))
            : rational
                ? segment.p3.x == 0.0F && segment.p3.y == 0.0F &&
                    std::isfinite(rational_weight) &&
                    rational_weight > 0.0F &&
                    rational_weight <= rational_weight_limit &&
                    segment.pad1 == 0U &&
                    segment.pad2 == 0U
                : segment.pad0 == 0U && segment.pad1 == 0U &&
                    segment.pad2 == 0U);
}

bool valid_vector_path(
    const progpu_native_scene_clip_path& path,
    std::uint32_t segment_count,
    const progpu_native_scene_path_boolean_node* boolean_nodes,
    std::uint32_t boolean_node_count) noexcept {
    if (!(path.segment_count > 0U &&
        path.segment_offset <= segment_count &&
        path.segment_count <= segment_count - path.segment_offset &&
        std::isfinite(path.min_x) && std::isfinite(path.min_y) &&
        std::isfinite(path.max_x) && std::isfinite(path.max_y) &&
        path.max_x > path.min_x && path.max_y > path.min_y &&
        valid_transform(path.transform) &&
        path.fill_rule <= PROGPU_NATIVE_FILL_RULE_EVEN_ODD &&
        (path.sample_grid == 4U || path.sample_grid == 8U) &&
        path.operation <= PROGPU_NATIVE_CLIP_DIFFERENCE &&
        path.reserved == 0U)) {
        return false;
    }
    return path_boolean::validate(
        path,
        boolean_nodes,
        boolean_node_count);
}

bool valid_vector(
    const progpu_native_scene_layer_vector_mask& mask,
    const progpu_native_scene_clip_path* paths,
    const progpu_native_path_segment* segments,
    const progpu_native_scene_path_boolean_node* boolean_nodes,
    std::uint32_t auxiliary_size) noexcept {
    if (mask.struct_size != sizeof(mask) ||
        mask.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
        mask.flags != 0U || mask.path_count == 0U ||
        mask.path_count > 64U || mask.segment_count == 0U ||
        !std::isfinite(mask.opacity) || mask.opacity < 0.0F ||
        mask.opacity > 1.0F || mask.reserved1 != 0U ||
        mask.boolean_node_count > 64U * 63U || paths == nullptr ||
        segments == nullptr ||
        (mask.boolean_node_count != 0U && boolean_nodes == nullptr)) {
        return false;
    }
    const std::uint64_t path_bytes =
        static_cast<std::uint64_t>(mask.path_count) * sizeof(*paths);
    const std::uint64_t segment_bytes =
        static_cast<std::uint64_t>(mask.segment_count) * sizeof(*segments);
    const std::uint64_t boolean_node_bytes =
        static_cast<std::uint64_t>(mask.boolean_node_count) *
            sizeof(*boolean_nodes);
    if (path_bytes + segment_bytes + boolean_node_bytes != auxiliary_size) {
        return false;
    }
    std::uint64_t expected_boolean_node_offset = 0U;
    for (std::uint32_t index = 0U; index < mask.path_count; ++index) {
        if (!valid_vector_path(
                paths[index],
                mask.segment_count,
                boolean_nodes,
                mask.boolean_node_count) ||
            (paths[index].boolean_node_count != 0U &&
                paths[index].boolean_node_offset !=
                    expected_boolean_node_offset)) {
            return false;
        }
        expected_boolean_node_offset += paths[index].boolean_node_count;
    }
    if (expected_boolean_node_offset != mask.boolean_node_count) {
        return false;
    }
    for (std::uint32_t index = 0U; index < mask.segment_count; ++index) {
        if (!valid_vector_segment(segments[index])) {
            return false;
        }
    }
    return true;
}

bool valid_brush_mask(
    const progpu_native_scene_layer_brush_mask& mask,
    const progpu_native_scene_gradient_stop* stops,
    std::uint32_t stop_count,
    std::uint32_t auxiliary_size) noexcept {
    const std::uint64_t expected_bytes =
        static_cast<std::uint64_t>(stop_count) *
            sizeof(progpu_native_scene_gradient_stop);
    if (mask.struct_size != sizeof(mask) ||
        mask.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH ||
        mask.flags != 0U || mask.gradient_stop_count != stop_count ||
        semantic_brush_stored_stop_count(mask.brush) != stop_count ||
        expected_bytes != auxiliary_size ||
        stop_count > PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS ||
        !valid_bounds(mask.bounds) || !valid_transform(mask.transform) ||
        !std::isfinite(mask.opacity) || mask.opacity < 0.0F ||
        mask.opacity > 1.0F || mask.reserved0 != 0U ||
        mask.brush.stop_offset != 0U ||
        (stop_count != 0U && stops == nullptr)) {
        return false;
    }
    return is_valid_semantic_brush(
        mask.brush,
        std::span<const progpu_native_scene_gradient_stop>(
            stops,
            stop_count));
}

bool valid_composite_brush(
    const progpu_native_scene_layer_brush_mask& mask,
    const progpu_native_scene_gradient_stop* stops,
    std::uint32_t stop_count) noexcept {
    const std::uint32_t stored_stop_count =
        semantic_brush_stored_stop_count(mask.brush);
    return mask.struct_size == sizeof(mask) &&
        mask.kind == PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH &&
        mask.flags == 0U && mask.gradient_stop_count == stored_stop_count &&
        valid_bounds(mask.bounds) && valid_transform(mask.transform) &&
        std::isfinite(mask.opacity) && mask.opacity >= 0.0F &&
        mask.opacity <= 1.0F && mask.reserved0 == 0U &&
        mask.brush.stop_offset <= stop_count &&
        stored_stop_count <= stop_count - mask.brush.stop_offset &&
        is_valid_semantic_brush(
            mask.brush,
            std::span<const progpu_native_scene_gradient_stop>(
                stops,
                stop_count));
}

bool valid_geometry_mask(
    const progpu_native_scene_layer_geometry_mask& mask,
    const progpu_native_geometry_primitive* primitives,
    std::uint32_t primitive_count,
    const progpu_native_scene_gradient_stop* stops,
    std::uint32_t stop_count) noexcept {
    const std::uint32_t stored_stop_count =
        semantic_brush_stored_stop_count(mask.brush);
    if (mask.struct_size != sizeof(mask) ||
        mask.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY ||
        mask.flags != 0U || mask.reserved0 != 0U ||
        mask.reserved1 != 0U || mask.reserved2 != 0U ||
        mask.primitive_count == 0U ||
        mask.primitive_offset > primitive_count ||
        mask.primitive_count > primitive_count - mask.primitive_offset ||
        mask.gradient_stop_count != stored_stop_count ||
        !valid_bounds(mask.bounds) || !valid_transform(mask.transform) ||
        !std::isfinite(mask.opacity) || mask.opacity < 0.0F ||
        mask.opacity > 1.0F || mask.brush.stop_offset > stop_count ||
        stored_stop_count > stop_count - mask.brush.stop_offset ||
        primitives == nullptr ||
        !is_valid_semantic_brush(
            mask.brush,
            std::span<const progpu_native_scene_gradient_stop>(
                stops,
                stop_count))) {
        return false;
    }
    for (std::uint32_t index = 0U; index < mask.primitive_count; ++index) {
        if (!::progpu::native::is_valid_geometry_primitive(
                primitives[mask.primitive_offset + index])) {
            return false;
        }
    }
    return true;
}

bool valid_composite(
    const progpu_native_scene_layer_composite_mask& mask,
    const progpu_native_scene_layer_brush_mask* brushes,
    const progpu_native_scene_layer_geometry_mask* geometry_masks,
    const progpu_native_geometry_primitive* geometry_primitives,
    const progpu_native_scene_layer_picture_mask* picture_masks,
    const std::byte* picture_streams,
    const progpu_native_scene_clip_path* paths,
    const progpu_native_path_segment* segments,
    const progpu_native_scene_path_boolean_node* boolean_nodes,
    const progpu_native_scene_gradient_stop* stops,
    std::uint32_t auxiliary_size) noexcept {
    const std::uint32_t vector_component = mask.path_count == 0U ? 0U : 1U;
    const std::uint64_t brush_bytes =
        static_cast<std::uint64_t>(mask.brush_mask_count) * sizeof(*brushes);
    const std::uint64_t geometry_mask_bytes =
        static_cast<std::uint64_t>(mask.geometry_mask_count) *
            sizeof(*geometry_masks);
    const std::uint64_t geometry_primitive_bytes =
        static_cast<std::uint64_t>(mask.geometry_primitive_count) *
            sizeof(*geometry_primitives);
    const std::uint64_t picture_mask_bytes =
        static_cast<std::uint64_t>(mask.picture_mask_count) *
            sizeof(*picture_masks);
    const std::uint64_t picture_stream_bytes = mask.picture_stream_bytes;
    const std::uint64_t path_bytes =
        static_cast<std::uint64_t>(mask.path_count) * sizeof(*paths);
    const std::uint64_t segment_bytes =
        static_cast<std::uint64_t>(mask.segment_count) * sizeof(*segments);
    const std::uint64_t boolean_bytes =
        static_cast<std::uint64_t>(mask.boolean_node_count) *
            sizeof(*boolean_nodes);
    const std::uint64_t stop_bytes =
        static_cast<std::uint64_t>(mask.gradient_stop_count) * sizeof(*stops);
    constexpr std::uint32_t legacy_composite_size = offsetof(
        progpu_native_scene_layer_composite_mask,
        picture_mask_count);
    const bool legacy = mask.struct_size == legacy_composite_size &&
        mask.picture_mask_count == 0U && mask.picture_stream_bytes == 0U &&
        mask.reserved0 == 0U && mask.reserved1 == 0U;
    if ((!legacy && mask.struct_size != sizeof(mask)) ||
        mask.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE ||
        mask.flags != 0U || mask.reserved0 != 0U || mask.reserved1 != 0U ||
        mask.component_count != mask.brush_mask_count +
            mask.geometry_mask_count + mask.picture_mask_count +
            vector_component ||
        mask.component_count < 2U || mask.component_count > 64U ||
        (mask.brush_mask_count == 0U && mask.geometry_mask_count == 0U &&
            mask.picture_mask_count == 0U) ||
        (mask.brush_mask_count != 0U && brushes == nullptr) ||
        (mask.geometry_mask_count != 0U && geometry_masks == nullptr) ||
        (mask.geometry_mask_count == 0U) !=
            (mask.geometry_primitive_count == 0U) ||
        (mask.geometry_primitive_count != 0U &&
            geometry_primitives == nullptr) ||
        (mask.picture_mask_count == 0U) !=
            (mask.picture_stream_bytes == 0U) ||
        (mask.picture_mask_count != 0U &&
            (picture_masks == nullptr || picture_streams == nullptr)) ||
        mask.gradient_stop_count > PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS ||
        !std::isfinite(mask.opacity) || mask.opacity < 0.0F ||
        mask.opacity > 1.0F ||
        (mask.path_count == 0U &&
            (mask.segment_count != 0U || mask.boolean_node_count != 0U)) ||
        (mask.path_count != 0U &&
            (mask.path_count > 64U || mask.segment_count == 0U ||
                paths == nullptr || segments == nullptr ||
                (mask.boolean_node_count != 0U && boolean_nodes == nullptr))) ||
        brush_bytes + geometry_mask_bytes + geometry_primitive_bytes +
                picture_mask_bytes + picture_stream_bytes + path_bytes +
                segment_bytes + boolean_bytes + stop_bytes != auxiliary_size ||
        (mask.gradient_stop_count != 0U && stops == nullptr)) {
        return false;
    }
    for (std::uint32_t index = 0U; index < mask.brush_mask_count; ++index) {
        if (!valid_composite_brush(
                brushes[index], stops, mask.gradient_stop_count)) {
            return false;
        }
    }
    std::uint32_t expected_primitive_offset = 0U;
    for (std::uint32_t index = 0U; index < mask.geometry_mask_count; ++index) {
        if (geometry_masks[index].primitive_offset !=
                expected_primitive_offset ||
            !valid_geometry_mask(
                geometry_masks[index],
                geometry_primitives,
                mask.geometry_primitive_count,
                stops,
                mask.gradient_stop_count)) {
            return false;
        }
        expected_primitive_offset += geometry_masks[index].primitive_count;
    }
    if (expected_primitive_offset != mask.geometry_primitive_count) {
        return false;
    }
    std::uint32_t expected_stream_offset = 0U;
    for (std::uint32_t index = 0U; index < mask.picture_mask_count; ++index) {
        if (picture_masks[index].stream_offset != expected_stream_offset ||
            !valid_picture_mask(
                picture_masks[index],
                picture_streams,
                mask.picture_stream_bytes)) {
            return false;
        }
        expected_stream_offset += picture_masks[index].stream_size;
    }
    if (expected_stream_offset != mask.picture_stream_bytes) {
        return false;
    }
    if (mask.path_count != 0U) {
        const progpu_native_scene_layer_vector_mask vector{
            sizeof(progpu_native_scene_layer_vector_mask),
            PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN,
            0U,
            mask.path_count,
            mask.segment_count,
            1.0F,
            mask.boolean_node_count,
            0U};
        if (!valid_vector(
                vector,
                paths,
                segments,
                boolean_nodes,
                static_cast<std::uint32_t>(
                    path_bytes + segment_bytes + boolean_bytes))) {
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
    std::span<const progpu_native_path_segment> segments,
    std::span<const progpu_native_scene_path_boolean_node> boolean_nodes) noexcept {
    if (paths.size() != mask.path_count ||
        segments.size() != mask.segment_count ||
        boolean_nodes.size() != mask.boolean_node_count) {
        return false;
    }
    const std::uint64_t auxiliary_size =
        static_cast<std::uint64_t>(paths.size_bytes()) +
        segments.size_bytes() + boolean_nodes.size_bytes();
    return auxiliary_size <= std::numeric_limits<std::uint32_t>::max() &&
        valid_vector(
            mask,
            paths.data(),
            segments.data(),
            boolean_nodes.data(),
            static_cast<std::uint32_t>(auxiliary_size));
}

bool is_valid_semantic_layer_brush_mask(
    const progpu_native_scene_layer_brush_mask& mask,
    std::span<const progpu_native_scene_gradient_stop> stops) noexcept {
    const std::uint64_t auxiliary_size = stops.size_bytes();
    return stops.size() <= std::numeric_limits<std::uint32_t>::max() &&
        auxiliary_size <= std::numeric_limits<std::uint32_t>::max() &&
        valid_brush_mask(
            mask,
            stops.data(),
            static_cast<std::uint32_t>(stops.size()),
            static_cast<std::uint32_t>(auxiliary_size));
}

bool is_valid_semantic_layer_geometry_mask(
    const progpu_native_scene_layer_geometry_mask& mask,
    std::span<const progpu_native_geometry_primitive> primitives,
    std::span<const progpu_native_scene_gradient_stop> stops) noexcept {
    return primitives.size() <= std::numeric_limits<std::uint32_t>::max() &&
        stops.size() <= std::numeric_limits<std::uint32_t>::max() &&
        mask.primitive_offset == 0U &&
        mask.primitive_count == primitives.size() &&
        valid_geometry_mask(
            mask,
            primitives.data(),
            static_cast<std::uint32_t>(primitives.size()),
            stops.data(),
            static_cast<std::uint32_t>(stops.size()));
}

bool is_valid_semantic_layer_picture_mask(
    const progpu_native_scene_layer_picture_mask& mask,
    std::span<const std::byte> nested_scene) noexcept {
    return nested_scene.size() <=
            std::numeric_limits<std::uint32_t>::max() &&
        mask.stream_offset == 0U &&
        mask.stream_size == nested_scene.size() &&
        valid_picture_mask(
            mask,
            nested_scene.data(),
            static_cast<std::uint32_t>(nested_scene.size()));
}

bool is_valid_semantic_layer_composite_mask(
    const progpu_native_scene_layer_composite_mask& mask,
    std::span<const progpu_native_scene_layer_brush_mask> brushes,
    std::span<const progpu_native_scene_layer_geometry_mask> geometry_masks,
    std::span<const progpu_native_geometry_primitive> geometry_primitives,
    std::span<const progpu_native_scene_layer_picture_mask> picture_masks,
    std::span<const std::byte> picture_streams,
    std::span<const progpu_native_scene_clip_path> paths,
    std::span<const progpu_native_path_segment> segments,
    std::span<const progpu_native_scene_path_boolean_node> boolean_nodes,
    std::span<const progpu_native_scene_gradient_stop> stops) noexcept {
    const std::uint64_t size = brushes.size_bytes() +
        geometry_masks.size_bytes() + geometry_primitives.size_bytes() +
        picture_masks.size_bytes() + picture_streams.size_bytes() +
        paths.size_bytes() + segments.size_bytes() +
        boolean_nodes.size_bytes() + stops.size_bytes();
    return size <= std::numeric_limits<std::uint32_t>::max() &&
        valid_composite(
            mask,
            brushes.data(),
            geometry_masks.data(),
            geometry_primitives.data(),
            picture_masks.data(),
            picture_streams.data(),
            paths.data(),
            segments.data(),
            boolean_nodes.data(),
            stops.data(),
            static_cast<std::uint32_t>(size));
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
        const std::uint64_t segment_bytes =
            static_cast<std::uint64_t>(result.vector.segment_count) *
                sizeof(progpu_native_path_segment);
        if (path_bytes + segment_bytes > resource.auxiliary_size) {
            return false;
        }
        result.vector_boolean_nodes =
            reinterpret_cast<const progpu_native_scene_path_boolean_node*>(
                bytes + resource.auxiliary_offset + path_bytes + segment_bytes);
        if (!valid_vector(
                result.vector,
                result.vector_paths,
                result.vector_segments,
                result.vector_boolean_nodes,
                resource.auxiliary_size)) {
            return false;
        }
    } else if (kind == PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH) {
        if (resource.payload_size != sizeof(result.brush) ||
            resource.auxiliary_size %
                sizeof(progpu_native_scene_gradient_stop) != 0U) {
            return false;
        }
        std::memcpy(&result.brush, bytes + resource.payload_offset,
            sizeof(result.brush));
        const std::uint32_t stop_count = resource.auxiliary_size /
            sizeof(progpu_native_scene_gradient_stop);
        result.brush_stops = stop_count == 0U
            ? nullptr
            : reinterpret_cast<const progpu_native_scene_gradient_stop*>(
                bytes + resource.auxiliary_offset);
        if (!valid_brush_mask(
                result.brush,
                result.brush_stops,
                stop_count,
                resource.auxiliary_size)) {
            return false;
        }
    } else if (kind == PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY) {
        if (resource.payload_size != sizeof(result.geometry)) {
            return false;
        }
        std::memcpy(&result.geometry, bytes + resource.payload_offset,
            sizeof(result.geometry));
        const std::uint64_t primitive_bytes =
            static_cast<std::uint64_t>(result.geometry.primitive_count) *
                sizeof(progpu_native_geometry_primitive);
        const std::uint64_t stop_bytes =
            static_cast<std::uint64_t>(result.geometry.gradient_stop_count) *
                sizeof(progpu_native_scene_gradient_stop);
        if (primitive_bytes + stop_bytes != resource.auxiliary_size) {
            return false;
        }
        const auto* auxiliary = bytes + resource.auxiliary_offset;
        result.composite_geometry_primitives = reinterpret_cast<
            const progpu_native_geometry_primitive*>(auxiliary);
        result.brush_stops = reinterpret_cast<
            const progpu_native_scene_gradient_stop*>(
                auxiliary + primitive_bytes);
        if (result.geometry.primitive_offset != 0U ||
            !valid_geometry_mask(
                result.geometry,
                result.composite_geometry_primitives,
                result.geometry.primitive_count,
                result.brush_stops,
                result.geometry.gradient_stop_count)) {
            return false;
        }
    } else if (kind == PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE) {
        if (resource.payload_size != sizeof(result.picture) ||
            resource.auxiliary_size == 0U) {
            return false;
        }
        std::memcpy(
            &result.picture,
            bytes + resource.payload_offset,
            sizeof(result.picture));
        if (result.picture.stream_offset != 0U ||
            result.picture.stream_size != resource.auxiliary_size ||
            !valid_picture_mask(
                result.picture,
                bytes + resource.auxiliary_offset,
                resource.auxiliary_size)) {
            return false;
        }
        result.composite_picture_streams =
            bytes + resource.auxiliary_offset;
    } else if (kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE) {
        constexpr std::uint32_t legacy_composite_size = offsetof(
            progpu_native_scene_layer_composite_mask,
            picture_mask_count);
        if (resource.payload_size != sizeof(result.composite) &&
            resource.payload_size != legacy_composite_size) {
            return false;
        }
        std::memcpy(&result.composite, bytes + resource.payload_offset,
            resource.payload_size);
        const std::uint64_t brush_bytes =
            static_cast<std::uint64_t>(result.composite.brush_mask_count) *
                sizeof(progpu_native_scene_layer_brush_mask);
        const std::uint64_t geometry_mask_bytes =
            static_cast<std::uint64_t>(result.composite.geometry_mask_count) *
                sizeof(progpu_native_scene_layer_geometry_mask);
        const std::uint64_t geometry_primitive_bytes =
            static_cast<std::uint64_t>(
                result.composite.geometry_primitive_count) *
                sizeof(progpu_native_geometry_primitive);
        const std::uint64_t picture_mask_bytes =
            static_cast<std::uint64_t>(
                result.composite.picture_mask_count) *
                sizeof(progpu_native_scene_layer_picture_mask);
        const std::uint64_t picture_stream_bytes =
            result.composite.picture_stream_bytes;
        const std::uint64_t path_bytes =
            static_cast<std::uint64_t>(result.composite.path_count) *
                sizeof(progpu_native_scene_clip_path);
        const std::uint64_t segment_bytes =
            static_cast<std::uint64_t>(result.composite.segment_count) *
                sizeof(progpu_native_path_segment);
        const std::uint64_t boolean_bytes =
            static_cast<std::uint64_t>(result.composite.boolean_node_count) *
                sizeof(progpu_native_scene_path_boolean_node);
        const std::uint64_t stop_bytes =
            static_cast<std::uint64_t>(result.composite.gradient_stop_count) *
                sizeof(progpu_native_scene_gradient_stop);
        if (brush_bytes + geometry_mask_bytes + geometry_primitive_bytes +
                picture_mask_bytes + picture_stream_bytes + path_bytes +
                segment_bytes + boolean_bytes + stop_bytes !=
            resource.auxiliary_size) {
            return false;
        }
        const auto* auxiliary = bytes + resource.auxiliary_offset;
        result.composite_brushes = reinterpret_cast<
            const progpu_native_scene_layer_brush_mask*>(auxiliary);
        result.composite_geometry_masks = reinterpret_cast<
            const progpu_native_scene_layer_geometry_mask*>(
                auxiliary + brush_bytes);
        result.composite_geometry_primitives = reinterpret_cast<
            const progpu_native_geometry_primitive*>(
                auxiliary + brush_bytes + geometry_mask_bytes);
        const std::uint64_t picture_mask_offset = brush_bytes +
            geometry_mask_bytes + geometry_primitive_bytes;
        result.composite_picture_masks = reinterpret_cast<
            const progpu_native_scene_layer_picture_mask*>(
                auxiliary + picture_mask_offset);
        result.composite_picture_streams =
            auxiliary + picture_mask_offset + picture_mask_bytes;
        const std::uint64_t path_offset = brush_bytes + geometry_mask_bytes +
            geometry_primitive_bytes + picture_mask_bytes +
            picture_stream_bytes;
        result.composite_paths = reinterpret_cast<
            const progpu_native_scene_clip_path*>(auxiliary + path_offset);
        result.composite_segments = reinterpret_cast<
            const progpu_native_path_segment*>(
                auxiliary + path_offset + path_bytes);
        result.composite_boolean_nodes = reinterpret_cast<
            const progpu_native_scene_path_boolean_node*>(
                auxiliary + path_offset + path_bytes + segment_bytes);
        result.composite_stops = reinterpret_cast<
            const progpu_native_scene_gradient_stop*>(
                auxiliary + path_offset + path_bytes + segment_bytes +
                    boolean_bytes);
        if (!valid_composite(
                result.composite,
                result.composite_brushes,
                result.composite_geometry_masks,
                result.composite_geometry_primitives,
                result.composite_picture_masks,
                result.composite_picture_streams,
                result.composite_paths,
                result.composite_segments,
                result.composite_boolean_nodes,
                result.composite_stops,
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

#include "progpu_native_semantic_layer_mask_tests.hpp"

#include "progpu_native.h"
#include "progpu_native_scene.hpp"
#include "progpu_native_semantic_layer_mask.hpp"

#include <array>
#include <bit>
#include <cstddef>
#include <cstring>
#include <limits>

namespace progpu::native::tests {

bool semantic_layer_coverage_mask_is_exact_and_bounded() {
    static_assert(sizeof(progpu_native_scene_layer_coverage_mask) == 80U);
    static_assert(sizeof(progpu_native_scene_layer_mask_chain) == 432U);
    static_assert(sizeof(progpu_native_scene_layer_vector_mask) == 32U);
    static_assert(sizeof(progpu_native_scene_layer_brush_mask) == 320U);
    static_assert(sizeof(progpu_native_scene_layer_geometry_mask) == 336U);
    static_assert(sizeof(progpu_native_scene_layer_picture_mask) == 72U);
    static_assert(sizeof(progpu_native_scene_layer_composite_mask) == 64U);
    static_assert(sizeof(progpu_native_scene_clip_path) == 88U);
    static_assert(sizeof(progpu_native_scene_path_boolean_node) == 48U);
    static_assert(PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_QUADRATIC == 4U);
    static_assert(PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_CUBIC == 5U);
    constexpr std::size_t coverage_size = 16U;
    std::array<std::byte,
        sizeof(progpu_native_scene_layer_coverage_mask) + coverage_size>
        bytes{};
    progpu_native_scene_layer_coverage_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP;
    mask.width = 4U;
    mask.height = 4U;
    mask.row_bytes = 4U;
    mask.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
    mask.bounds = {0.0F, 0.0F, 4.0F, 4.0F};
    mask.transform = {1.0F, 0.25F, -0.125F, 1.0F, 3.0F, 5.0F};
    mask.opacity = 0.75F;
    std::memcpy(bytes.data(), &mask, sizeof(mask));
    progpu_native_scene_resource resource{};
    resource.payload_size = sizeof(mask);
    resource.auxiliary_offset = sizeof(mask);
    resource.auxiliary_size = coverage_size;
    std::uint32_t error_offset = 0U;
    semantic::semantic_layer_mask parsed{};
    if (!semantic::validate_layer_mask_resource(
            bytes.data(), resource, error_offset, &parsed) ||
        parsed.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP ||
        parsed.coverage.opacity != mask.opacity) {
        return false;
    }

    resource.auxiliary_size = coverage_size - 1U;
    if (semantic::validate_layer_mask_resource(
            bytes.data(), resource, error_offset)) {
        return false;
    }
    resource.auxiliary_size = coverage_size;
    mask.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
    std::memcpy(bytes.data(), &mask, sizeof(mask));
    if (semantic::validate_layer_mask_resource(
            bytes.data(), resource, error_offset)) {
        return false;
    }
    mask.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    mask.transform.m11 = std::numeric_limits<float>::quiet_NaN();
    std::memcpy(bytes.data(), &mask, sizeof(mask));
    if (semantic::validate_layer_mask_resource(
            bytes.data(), resource, error_offset)) {
        return false;
    }

    progpu_native_scene_layer_mask_chain chain{};
    chain.struct_size = sizeof(chain);
    chain.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN;
    chain.mask_count = 2U;
    for (std::uint32_t index = 0U; index < chain.mask_count; ++index) {
        auto& analytic = chain.masks[index];
        analytic.struct_size = sizeof(analytic);
        analytic.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
        analytic.bounds = {
            static_cast<float>(index),
            static_cast<float>(index),
            8.0F,
            8.0F};
        analytic.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
        analytic.opacity = 1.0F;
    }
    std::array<std::byte, sizeof(chain)> chain_bytes{};
    std::memcpy(chain_bytes.data(), &chain, sizeof(chain));
    resource.payload_size = sizeof(chain);
    resource.auxiliary_offset = 0U;
    resource.auxiliary_size = 0U;
    if (!semantic::validate_layer_mask_resource(
            chain_bytes.data(), resource, error_offset, &parsed) ||
        parsed.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN ||
        parsed.chain.mask_count != 2U) {
        return false;
    }
    chain.mask_count = 1U;
    std::memcpy(chain_bytes.data(), &chain, sizeof(chain));
    if (semantic::validate_layer_mask_resource(
            chain_bytes.data(), resource, error_offset)) {
        return false;
    }
    chain.mask_count = 2U;
    chain.masks[3] = chain.masks[0];
    std::memcpy(chain_bytes.data(), &chain, sizeof(chain));
    if (semantic::validate_layer_mask_resource(
            chain_bytes.data(), resource, error_offset)) {
        return false;
    }

    constexpr std::size_t vector_auxiliary_size =
        sizeof(progpu_native_scene_clip_path) +
        sizeof(progpu_native_path_segment);
    std::array<std::byte,
        sizeof(progpu_native_scene_layer_vector_mask) +
            vector_auxiliary_size> vector_bytes{};
    progpu_native_scene_layer_vector_mask vector_mask{};
    vector_mask.struct_size = sizeof(vector_mask);
    vector_mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN;
    vector_mask.path_count = 1U;
    vector_mask.segment_count = 1U;
    vector_mask.opacity = 0.8F;
    progpu_native_scene_clip_path vector_path{};
    vector_path.segment_count = 1U;
    vector_path.min_x = 0.0F;
    vector_path.min_y = 0.0F;
    vector_path.max_x = 12.0F;
    vector_path.max_y = 8.0F;
    vector_path.transform = {1.0F, 0.2F, -0.1F, 1.0F, 3.0F, 4.0F};
    vector_path.fill_rule = PROGPU_NATIVE_FILL_RULE_EVEN_ODD;
    vector_path.sample_grid = 8U;
    vector_path.operation = PROGPU_NATIVE_CLIP_INTERSECT;
    progpu_native_path_segment vector_segment{};
    vector_segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
    vector_segment.p0 = {0.0F, 0.0F};
    vector_segment.p1 = {12.0F, 8.0F};
    std::memcpy(vector_bytes.data(), &vector_mask, sizeof(vector_mask));
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask),
        &vector_path,
        sizeof(vector_path));
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask) + sizeof(vector_path),
        &vector_segment,
        sizeof(vector_segment));
    resource.payload_offset = 0U;
    resource.payload_size = sizeof(vector_mask);
    resource.auxiliary_offset = sizeof(vector_mask);
    resource.auxiliary_size = vector_auxiliary_size;
    if (!semantic::validate_layer_mask_resource(
            vector_bytes.data(), resource, error_offset, &parsed) ||
        parsed.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
        parsed.vector.path_count != 1U ||
        parsed.vector_paths[0].sample_grid != 8U ||
        parsed.vector_segments[0].kind !=
            PROGPU_NATIVE_PATH_SEGMENT_LINE) {
        return false;
    }
    vector_segment.kind = PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_QUADRATIC;
    vector_segment.p0 = {0.0F, 0.0F};
    vector_segment.p1 = {6.0F, 12.0F};
    vector_segment.p2 = {12.0F, 0.0F};
    vector_segment.p3 = {0.0F, 0.0F};
    vector_segment.pad0 = std::bit_cast<std::uint32_t>(0.5F);
    vector_segment.pad1 = 0U;
    vector_segment.pad2 = 0U;
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask) + sizeof(vector_path),
        &vector_segment,
        sizeof(vector_segment));
    if (!semantic::validate_layer_mask_resource(
            vector_bytes.data(), resource, error_offset, &parsed) ||
        parsed.vector_segments[0].kind !=
            PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_QUADRATIC) {
        return false;
    }
    vector_segment.p3 = {1.0F, 0.0F};
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask) + sizeof(vector_path),
        &vector_segment,
        sizeof(vector_segment));
    if (semantic::validate_layer_mask_resource(
            vector_bytes.data(), resource, error_offset)) {
        return false;
    }
    vector_segment.p3 = {0.0F, 0.0F};
    vector_segment.pad0 = std::bit_cast<std::uint32_t>(
        std::numeric_limits<float>::max());
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask) + sizeof(vector_path),
        &vector_segment,
        sizeof(vector_segment));
    if (semantic::validate_layer_mask_resource(
            vector_bytes.data(), resource, error_offset)) {
        return false;
    }
    vector_segment.kind = PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_CUBIC;
    vector_segment.p0 = {0.0F, 0.0F};
    vector_segment.p1 = {0.0F, 12.0F};
    vector_segment.p2 = {12.0F, 12.0F};
    vector_segment.p3 = {12.0F, 0.0F};
    vector_segment.pad0 = std::bit_cast<std::uint32_t>(0.5F);
    vector_segment.pad1 = std::bit_cast<std::uint32_t>(1.5F);
    vector_segment.pad2 = 0U;
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask) + sizeof(vector_path),
        &vector_segment,
        sizeof(vector_segment));
    if (!semantic::validate_layer_mask_resource(
            vector_bytes.data(), resource, error_offset, &parsed) ||
        parsed.vector_segments[0].kind !=
            PROGPU_NATIVE_PATH_SEGMENT_RATIONAL_CUBIC) {
        return false;
    }
    vector_segment.pad2 = 1U;
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask) + sizeof(vector_path),
        &vector_segment,
        sizeof(vector_segment));
    if (semantic::validate_layer_mask_resource(
            vector_bytes.data(), resource, error_offset)) {
        return false;
    }
    vector_segment = {};
    vector_segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
    vector_segment.p0 = {0.0F, 0.0F};
    vector_segment.p1 = {12.0F, 8.0F};
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask) + sizeof(vector_path),
        &vector_segment,
        sizeof(vector_segment));
    vector_path.reserved = 1U;
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask),
        &vector_path,
        sizeof(vector_path));
    if (semantic::validate_layer_mask_resource(
            vector_bytes.data(), resource, error_offset)) {
        return false;
    }

    constexpr std::size_t boolean_auxiliary_size =
        sizeof(progpu_native_scene_clip_path) +
        2U * sizeof(progpu_native_path_segment) +
        3U * sizeof(progpu_native_scene_path_boolean_node);
    std::array<std::byte,
        sizeof(progpu_native_scene_layer_vector_mask) +
            boolean_auxiliary_size> boolean_bytes{};
    vector_mask.segment_count = 2U;
    vector_mask.boolean_node_count = 3U;
    vector_path = {};
    vector_path.segment_count = 2U;
    vector_path.boolean_node_count = 3U;
    vector_path.min_x = 0.0F;
    vector_path.min_y = 0.0F;
    vector_path.max_x = 12.0F;
    vector_path.max_y = 8.0F;
    vector_path.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    vector_path.sample_grid = 4U;
    std::array<progpu_native_path_segment, 2U> boolean_segments{
        vector_segment, vector_segment};
    std::array<progpu_native_scene_path_boolean_node, 3U> nodes{};
    nodes[0].segment_count = 1U;
    nodes[0].max_x = 12.0F;
    nodes[0].max_y = 8.0F;
    nodes[0].kind = PROGPU_NATIVE_PATH_BOOLEAN_LEAF;
    nodes[1] = nodes[0];
    nodes[1].segment_offset = 1U;
    nodes[2].kind = PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE;
    std::memcpy(boolean_bytes.data(), &vector_mask, sizeof(vector_mask));
    std::size_t offset = sizeof(vector_mask);
    std::memcpy(boolean_bytes.data() + offset, &vector_path, sizeof(vector_path));
    offset += sizeof(vector_path);
    std::memcpy(boolean_bytes.data() + offset,
        boolean_segments.data(), sizeof(boolean_segments));
    offset += sizeof(boolean_segments);
    std::memcpy(boolean_bytes.data() + offset, nodes.data(), sizeof(nodes));
    resource.auxiliary_size = boolean_auxiliary_size;
    if (!semantic::validate_layer_mask_resource(
            boolean_bytes.data(), resource, error_offset, &parsed) ||
        parsed.vector.boolean_node_count != 3U ||
        parsed.vector_boolean_nodes[2].kind !=
            PROGPU_NATIVE_PATH_BOOLEAN_DIFFERENCE) {
        return false;
    }
    nodes[2].kind = PROGPU_NATIVE_PATH_BOOLEAN_LEAF;
    std::memcpy(boolean_bytes.data() + offset, nodes.data(), sizeof(nodes));
    if (semantic::validate_layer_mask_resource(
            boolean_bytes.data(), resource, error_offset)) {
        return false;
    }

    constexpr std::size_t brush_stop_count = 2U;
    constexpr std::size_t brush_auxiliary_size =
        brush_stop_count * sizeof(progpu_native_scene_gradient_stop);
    std::array<std::byte,
        sizeof(progpu_native_scene_layer_brush_mask) +
            brush_auxiliary_size> brush_bytes{};
    progpu_native_scene_layer_brush_mask brush_mask{};
    brush_mask.struct_size = sizeof(brush_mask);
    brush_mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH;
    brush_mask.gradient_stop_count = brush_stop_count;
    brush_mask.bounds = {1.0F, 2.0F, 40.0F, 20.0F};
    brush_mask.transform = {1.0F, 0.25F, -0.1F, 1.0F, 3.0F, 4.0F};
    brush_mask.opacity = 0.75F;
    brush_mask.brush.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    brush_mask.brush.opacity = 0.8F;
    brush_mask.brush.start_point = {1.0F, 2.0F};
    brush_mask.brush.end_point = {41.0F, 2.0F};
    brush_mask.brush.stop_count = brush_stop_count;
    brush_mask.brush.coordinate_transform0[0] = 1.0F;
    brush_mask.brush.coordinate_transform1[1] = 1.0F;
    std::array<progpu_native_scene_gradient_stop, brush_stop_count>
        brush_stops{};
    brush_stops[0].color = {1.0F, 1.0F, 1.0F, 1.0F};
    brush_stops[1].color = {0.0F, 0.0F, 0.0F, 0.0F};
    brush_stops[1].offset = 1.0F;
    std::memcpy(brush_bytes.data(), &brush_mask, sizeof(brush_mask));
    std::memcpy(
        brush_bytes.data() + sizeof(brush_mask),
        brush_stops.data(),
        sizeof(brush_stops));
    resource.payload_size = sizeof(brush_mask);
    resource.auxiliary_offset = sizeof(brush_mask);
    resource.auxiliary_size = brush_auxiliary_size;
    if (!semantic::validate_layer_mask_resource(
            brush_bytes.data(), resource, error_offset, &parsed) ||
        parsed.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH ||
        parsed.brush.gradient_stop_count != brush_stop_count ||
        parsed.brush_stops[1].offset != 1.0F) {
        return false;
    }

    auto solid_brush_mask = brush_mask;
    solid_brush_mask.gradient_stop_count = 0U;
    solid_brush_mask.brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
    solid_brush_mask.brush.stop_count = 0U;
    solid_brush_mask.brush.colors[0] = {1.0F, 1.0F, 1.0F, 0.5F};
    std::memcpy(
        brush_bytes.data(),
        &solid_brush_mask,
        sizeof(solid_brush_mask));
    resource.auxiliary_size = 0U;
    if (!semantic::validate_layer_mask_resource(
            brush_bytes.data(), resource, error_offset, &parsed) ||
        parsed.brush.gradient_stop_count != 0U ||
        parsed.brush.brush.type != PROGPU_NATIVE_SCENE_BRUSH_SOLID ||
        parsed.brush_stops != nullptr) {
        return false;
    }

    brush_mask.brush.stop_offset = 1U;
    std::memcpy(brush_bytes.data(), &brush_mask, sizeof(brush_mask));
    resource.auxiliary_size = brush_auxiliary_size;
    if (semantic::validate_layer_mask_resource(
            brush_bytes.data(), resource, error_offset)) {
        return false;
    }

    constexpr std::size_t geometry_auxiliary_size =
        sizeof(progpu_native_geometry_primitive);
    std::array<std::byte,
        sizeof(progpu_native_scene_layer_geometry_mask) +
            geometry_auxiliary_size> geometry_bytes{};
    progpu_native_scene_layer_geometry_mask geometry_mask{};
    geometry_mask.struct_size = sizeof(geometry_mask);
    geometry_mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY;
    geometry_mask.primitive_count = 1U;
    geometry_mask.bounds = {0.0F, 0.0F, 20.0F, 20.0F};
    geometry_mask.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    geometry_mask.opacity = 1.0F;
    geometry_mask.brush = solid_brush_mask.brush;
    progpu_native_geometry_primitive geometry_primitive{};
    geometry_primitive.kind = PROGPU_NATIVE_GEOMETRY_LINE;
    geometry_primitive.p0 = {2.0F, 10.0F};
    geometry_primitive.p1 = {18.0F, 10.0F};
    geometry_primitive.stroke_thickness = 4.0F;
    geometry_primitive.color = {1.0F, 1.0F, 1.0F, 0.5F};
    geometry_primitive.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    std::memcpy(geometry_bytes.data(), &geometry_mask, sizeof(geometry_mask));
    std::memcpy(
        geometry_bytes.data() + sizeof(geometry_mask),
        &geometry_primitive,
        sizeof(geometry_primitive));
    resource.payload_size = sizeof(geometry_mask);
    resource.auxiliary_offset = sizeof(geometry_mask);
    resource.auxiliary_size = geometry_auxiliary_size;
    if (!semantic::validate_layer_mask_resource(
            geometry_bytes.data(), resource, error_offset, &parsed) ||
        parsed.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY ||
        parsed.geometry.primitive_count != 1U ||
        parsed.composite_geometry_primitives[0].kind !=
            PROGPU_NATIVE_GEOMETRY_LINE) {
        return false;
    }
    geometry_mask.primitive_offset = 1U;
    std::memcpy(geometry_bytes.data(), &geometry_mask, sizeof(geometry_mask));
    if (semantic::validate_layer_mask_resource(
            geometry_bytes.data(), resource, error_offset)) {
        return false;
    }

    progpu_native_scene_header nested_header{};
    nested_header.struct_size = sizeof(nested_header);
    nested_header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    nested_header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    nested_header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    nested_header.total_size = sizeof(nested_header);
    nested_header.scene_id = 41U;
    nested_header.generation = 7U;
    nested_header.command_offset = sizeof(nested_header);
    nested_header.command_stride = sizeof(progpu_native_scene_command);
    nested_header.resource_offset = sizeof(nested_header);
    nested_header.resource_stride = sizeof(progpu_native_scene_resource);
    nested_header.arena_offset = sizeof(nested_header);
    std::array<std::byte, sizeof(nested_header)> nested_bytes{};
    std::memcpy(
        nested_bytes.data(), &nested_header, sizeof(nested_header));
    progpu_native_scene_layer_picture_mask picture_mask{};
    picture_mask.struct_size = sizeof(picture_mask);
    picture_mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE;
    picture_mask.stream_size = sizeof(nested_header);
    picture_mask.bounds = {0.0F, 0.0F, 20.0F, 12.0F};
    picture_mask.transform = {1.0F, 0.0F, 0.0F, 1.0F, 3.0F, 4.0F};
    picture_mask.opacity = 0.75F;
    std::array<std::byte,
        sizeof(picture_mask) + sizeof(nested_header)> picture_bytes{};
    std::memcpy(
        picture_bytes.data(), &picture_mask, sizeof(picture_mask));
    std::memcpy(
        picture_bytes.data() + sizeof(picture_mask),
        nested_bytes.data(),
        nested_bytes.size());
    resource.payload_size = sizeof(picture_mask);
    resource.auxiliary_offset = sizeof(picture_mask);
    resource.auxiliary_size = sizeof(nested_header);
    if (!semantic::validate_layer_mask_resource(
            picture_bytes.data(), resource, error_offset, &parsed) ||
        parsed.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE ||
        parsed.picture.stream_size != sizeof(nested_header)) {
        return false;
    }

    constexpr std::size_t outer_resource_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::size_t outer_payload_offset = outer_resource_offset +
        sizeof(progpu_native_scene_resource);
    constexpr std::size_t outer_auxiliary_offset = outer_payload_offset +
        sizeof(progpu_native_scene_layer_picture_mask);
    constexpr std::size_t outer_size = outer_auxiliary_offset +
        sizeof(progpu_native_scene_header);
    std::array<std::byte, outer_size> outer_bytes{};
    progpu_native_scene_header outer_header{};
    outer_header.struct_size = sizeof(outer_header);
    outer_header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    outer_header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    outer_header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    outer_header.total_size = outer_size;
    outer_header.scene_id = 40U;
    outer_header.generation = 7U;
    outer_header.command_offset = sizeof(outer_header);
    outer_header.command_stride = sizeof(progpu_native_scene_command);
    outer_header.resource_offset = outer_resource_offset;
    outer_header.resource_count = 1U;
    outer_header.resource_stride = sizeof(progpu_native_scene_resource);
    outer_header.arena_offset = outer_payload_offset;
    outer_header.arena_size = outer_size - outer_payload_offset;
    progpu_native_scene_resource outer_resource{};
    outer_resource.struct_size = sizeof(outer_resource);
    outer_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK;
    outer_resource.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    outer_resource.resource_id = 1U;
    outer_resource.generation = 7U;
    outer_resource.payload_offset = outer_payload_offset;
    outer_resource.payload_size = sizeof(picture_mask);
    outer_resource.auxiliary_offset = outer_auxiliary_offset;
    outer_resource.auxiliary_size = sizeof(nested_header);
    std::memcpy(outer_bytes.data(), &outer_header, sizeof(outer_header));
    std::memcpy(
        outer_bytes.data() + outer_resource_offset,
        &outer_resource,
        sizeof(outer_resource));
    std::memcpy(
        outer_bytes.data() + outer_payload_offset,
        &picture_mask,
        sizeof(picture_mask));
    std::memcpy(
        outer_bytes.data() + outer_auxiliary_offset,
        &nested_header,
        sizeof(nested_header));
    if (scene::validate(outer_bytes.data(), outer_bytes.size()).status !=
        PROGPU_NATIVE_STATUS_SUCCESS) {
        return false;
    }
    nested_header.command_stride = 0U;
    std::memcpy(
        outer_bytes.data() + outer_auxiliary_offset,
        &nested_header,
        sizeof(nested_header));
    if (!semantic::validate_layer_mask_resource(
            outer_bytes.data(), outer_resource, error_offset) ||
        scene::validate(outer_bytes.data(), outer_bytes.size()).status ==
            PROGPU_NATIVE_STATUS_SUCCESS) {
        return false;
    }

    constexpr std::size_t composite_brush_count = 2U;
    constexpr std::size_t composite_auxiliary_size =
        composite_brush_count * sizeof(progpu_native_scene_layer_brush_mask);
    std::array<std::byte,
        sizeof(progpu_native_scene_layer_composite_mask) +
            composite_auxiliary_size> composite_bytes{};
    progpu_native_scene_layer_composite_mask composite{};
    composite.struct_size = sizeof(composite);
    composite.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE;
    composite.component_count = composite_brush_count;
    composite.brush_mask_count = composite_brush_count;
    composite.opacity = 1.0F;
    std::array<progpu_native_scene_layer_brush_mask, composite_brush_count>
        composite_brushes{solid_brush_mask, solid_brush_mask};
    std::memcpy(composite_bytes.data(), &composite, sizeof(composite));
    std::memcpy(
        composite_bytes.data() + sizeof(composite),
        composite_brushes.data(),
        sizeof(composite_brushes));
    resource.payload_size = sizeof(composite);
    resource.auxiliary_offset = sizeof(composite);
    resource.auxiliary_size = composite_auxiliary_size;
    if (!semantic::validate_layer_mask_resource(
            composite_bytes.data(), resource, error_offset, &parsed) ||
        parsed.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE ||
        parsed.composite.component_count != composite_brush_count ||
        parsed.composite_brushes[1].brush.type !=
            PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
        return false;
    }
    constexpr std::uint32_t legacy_composite_size = offsetof(
        progpu_native_scene_layer_composite_mask,
        picture_mask_count);
    auto legacy_composite = composite;
    legacy_composite.struct_size = legacy_composite_size;
    std::memcpy(
        composite_bytes.data(),
        &legacy_composite,
        legacy_composite_size);
    resource.payload_size = legacy_composite_size;
    if (!semantic::validate_layer_mask_resource(
            composite_bytes.data(), resource, error_offset, &parsed) ||
        parsed.composite.picture_mask_count != 0U) {
        return false;
    }
    resource.payload_size = sizeof(composite);
    composite.component_count = 1U;
    std::memcpy(composite_bytes.data(), &composite, sizeof(composite));
    return !semantic::validate_layer_mask_resource(
        composite_bytes.data(), resource, error_offset);
}

} // namespace progpu::native::tests

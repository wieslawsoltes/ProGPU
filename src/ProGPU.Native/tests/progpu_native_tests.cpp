#include "progpu_native.h"
#include "progpu_native_geometry.hpp"

#include <cmath>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <vector>

namespace {

void require(
    bool condition,
    const char* expression,
    const char* file,
    int line) {
    if (condition) {
        return;
    }
    std::cerr << file << ':' << line
              << ": requirement failed: " << expression << '\n';
    std::abort();
}

#define PROGPU_REQUIRE(condition) \
    require((condition), #condition, __FILE__, __LINE__)

bool nearly_equal(float left, float right) {
    return std::abs(left - right) <= 0.00001F;
}

void fixed_stroke_topology_masks_match_reference_classification() {
    using progpu::native::stroke_triangle;
    std::array<stroke_triangle, 8U> triangles{};
    const progpu_native_point center{13.25F, -7.5F};
    const progpu_native_point direction{3.0F, 4.0F};
    for (std::uint32_t cap = PROGPU_NATIVE_STROKE_CAP_SQUARE;
         cap <= PROGPU_NATIVE_STROKE_CAP_TRIANGLE;
         ++cap) {
        const std::size_t count = progpu::native::create_cap_triangles(
            triangles, cap, 5.5F, center, direction, false);
        progpu_native_point normalized{};
        PROGPU_REQUIRE(progpu::native::try_normalize(
            direction, {}, normalized));
        for (std::size_t index = 0U; index < count; ++index) {
            std::uint32_t expected_exterior = 0U;
            std::uint32_t expected_owned = 0U;
            std::uint32_t actual_exterior = 0U;
            std::uint32_t actual_owned = 0U;
            progpu::native::classify_triangle_edges(
                triangles.data(), count, index, true, center, normalized,
                expected_exterior, expected_owned);
            progpu::native::classify_cap_triangle_edges(
                cap, count, index, actual_exterior, actual_owned);
            PROGPU_REQUIRE(actual_exterior == expected_exterior);
            PROGPU_REQUIRE(actual_owned == expected_owned);
        }
    }

    const progpu_native_point incoming{1.0F, 0.0F};
    const progpu_native_point outgoing{0.35F, 0.94F};
    for (std::uint32_t join = PROGPU_NATIVE_STROKE_JOIN_MITER;
         join <= PROGPU_NATIVE_STROKE_JOIN_ROUND;
         ++join) {
        const std::size_t count = progpu::native::create_join_triangles(
            triangles, join, 5.5F, 4.0F, center, incoming, outgoing);
        for (std::size_t index = 0U; index < count; ++index) {
            std::uint32_t expected_exterior = 0U;
            std::uint32_t expected_owned = 0U;
            std::uint32_t actual_exterior = 0U;
            std::uint32_t actual_owned = 0U;
            progpu::native::classify_triangle_edges(
                triangles.data(), count, index, false, {}, {},
                expected_exterior, expected_owned);
            progpu::native::classify_join_triangle_edges(
                join, count, index, actual_exterior, actual_owned);
            PROGPU_REQUIRE(actual_exterior == expected_exterior);
            PROGPU_REQUIRE(actual_owned == expected_owned);
        }
    }
}

void api_contract_is_versioned() {
    PROGPU_REQUIRE(
        progpu_native_get_abi_version() == PROGPU_NATIVE_ABI_VERSION);

    progpu_native_engine_info too_small{};
    too_small.struct_size = sizeof(too_small) - 1U;
    PROGPU_REQUIRE(progpu_native_get_info(&too_small) == 0U);

    progpu_native_engine_info info{};
    info.struct_size = sizeof(info);
    PROGPU_REQUIRE(progpu_native_get_info(&info) == 1U);
    PROGPU_REQUIRE(info.abi_version == PROGPU_NATIVE_ABI_VERSION);
    PROGPU_REQUIRE(info.backend_abi ==
        PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SHARED_VECTOR_SHADER) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_INDEXED_ANALYTIC_BATCH) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_INDEXED_GEOMETRY_BATCH) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_DEVICE_STROKES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_BEZIER_STROKES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_STROKE_CAPS) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_CONNECTED_STROKES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SPLINE_STROKES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_DASHED_STROKES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_PATH_FILL_ATLAS) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_POSITIONED_GLYPH_ATLAS) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_RESIZABLE_ATLASES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_RETAINED_RGBA_IMAGE) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_RGBA_VIEW) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_IMAGE_MASK) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_EXPLICIT_QUEUE_TIMELINE) != 0U);
    PROGPU_REQUIRE(sizeof(progpu_native_glyph_outline) == 40U);
    PROGPU_REQUIRE(sizeof(progpu_native_positioned_glyph) == 64U);
    PROGPU_REQUIRE(sizeof(progpu_native_glyph_frame) == 96U);
    PROGPU_REQUIRE(sizeof(progpu_native_glyph_frame_metrics) == 80U);
    PROGPU_REQUIRE(sizeof(progpu_native_image_rect) == 16U);
    PROGPU_REQUIRE(sizeof(progpu_native_image_frame) == 200U);
    PROGPU_REQUIRE(sizeof(progpu_native_image_frame_metrics) == 72U);
    PROGPU_REQUIRE(std::strstr(info.name, "ProGPU C++") != nullptr);
}

void geometry_batch_encodes_direct_and_affine_lines() {
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    progpu_native_geometry_primitive direct{
        PROGPU_NATIVE_GEOMETRY_LINE,
        PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED,
        {1.0F, 2.0F},
        {5.0F, 2.0F},
        {},
        {},
        3.0F,
        0.0F,
        {0.1F, 0.2F, 0.3F, 0.4F},
        {0.0F, 2.0F, -2.0F, 0.0F, 5.0F, 7.0F}
    };
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        direct,
        2.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 4U && indices.size() == 6U);
    PROGPU_REQUIRE(indices[3] == 1U && indices[4] == 3U && indices[5] == 2U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[0], 1.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[1], 9.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].stroke_thickness, 6.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 1003.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].brush_index, 2.0F));

    vertices.clear();
    indices.clear();
    direct.flags = 0U;
    direct.transform = {2.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    direct.stroke_thickness = 4.0F;
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        direct,
        3.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 4U && indices.size() == 6U);
    PROGPU_REQUIRE(indices[3] == 0U && indices[4] == 2U && indices[5] == 3U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[0], 0.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[1], -1.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[0], 2.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[1], 4.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[2], 10.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[3], 4.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[0], 10.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[1], 0.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].corner_radius, 2.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].stroke_thickness, 0.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 14.0F));
}

void geometry_batch_encodes_device_strokes_and_fills() {
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    progpu_native_geometry_primitive hairline{
        PROGPU_NATIVE_GEOMETRY_LINE,
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE,
        {1.0F, 2.0F},
        {5.0F, 6.0F},
        {},
        {},
        0.0F,
        0.0F,
        {1.0F, 0.0F, 0.0F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}
    };
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        hairline,
        0.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(nearly_equal(vertices[0].stroke_thickness, -1.0F));

    vertices.clear();
    indices.clear();
    hairline.flags = PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE;
    hairline.stroke_thickness = 2.5F;
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        hairline,
        0.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(nearly_equal(vertices[0].stroke_thickness, -3.5F));

    vertices.clear();
    indices.clear();
    progpu_native_geometry_primitive triangle{
        PROGPU_NATIVE_GEOMETRY_TRIANGLE,
        0U,
        {1.0F, 2.0F},
        {5.0F, 2.0F},
        {3.0F, 7.0F},
        {},
        0.0F,
        0.0F,
        {0.0F, 1.0F, 0.0F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 4.0F, 8.0F}
    };
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        triangle,
        4.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 3U && indices.size() == 3U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[0], 5.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[1], 10.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 7.0F));
}

void geometry_batch_encodes_gpu_and_affine_bezier_strokes() {
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    progpu_native_geometry_primitive quadratic{
        PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER,
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE,
        {1.0F, 2.0F},
        {3.0F, 8.0F},
        {9.0F, 4.0F},
        {},
        2.5F,
        0.0F,
        {0.1F, 0.2F, 0.3F, 0.8F},
        {2.0F, 0.0F, 0.0F, 2.0F, 5.0F, 7.0F}
    };
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        quadratic,
        3.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 50U && indices.size() == 144U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[0], 7.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[1], 11.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[0], 11.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[1], 23.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[0], 23.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[1], 15.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].stroke_thickness, -3.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 5.0F));
    PROGPU_REQUIRE(indices[0] == 0U && indices[143] == 48U);

    vertices.clear();
    indices.clear();
    progpu_native_geometry_primitive cubic{
        PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER,
        0U,
        {0.0F, 0.0F},
        {10.0F, 30.0F},
        {20.0F, -20.0F},
        {40.0F, 0.0F},
        4.0F,
        0.0F,
        {0.8F, 0.4F, 0.2F, 1.0F},
        {2.0F, 0.25F, 0.5F, 1.0F, 3.0F, 5.0F}
    };
    std::size_t vertex_capacity = 0U;
    std::size_t index_capacity = 0U;
    PROGPU_REQUIRE(progpu::native::geometry_primitive_capacity(
        cubic,
        vertex_capacity,
        index_capacity));
    PROGPU_REQUIRE(vertex_capacity >= 96U && vertex_capacity <= 4096U);
    PROGPU_REQUIRE(index_capacity * 2U == vertex_capacity * 3U);
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        cubic,
        4.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(!vertices.empty() && !indices.empty());
    PROGPU_REQUIRE(vertices.size() <= vertex_capacity);
    PROGPU_REQUIRE(indices.size() <= index_capacity);
    PROGPU_REQUIRE(nearly_equal(vertices[0].brush_index, 4.0F));
    PROGPU_REQUIRE(vertices[0].shape_type == 16.0F);
    PROGPU_REQUIRE(vertices[vertices.size() - 1U].shape_type == 17.0F);
}

void geometry_batch_preserves_cap_order_and_space() {
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    progpu_native_geometry_primitive hairline{
        PROGPU_NATIVE_GEOMETRY_LINE,
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
            (PROGPU_NATIVE_STROKE_CAP_ROUND <<
                PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) |
            (PROGPU_NATIVE_STROKE_CAP_TRIANGLE <<
                PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT),
        {1.0F, 2.0F},
        {5.0F, 6.0F},
        {},
        {},
        0.0F,
        0.0F,
        {0.3F, 0.5F, 0.7F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}
    };
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        hairline,
        2.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 12U && indices.size() == 18U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 22.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[0], 2.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[1], 1.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[0], 0.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[4].shape_type, 3.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].shape_type, 22.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].color[0], 3.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].color[1], 0.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].shape_size[0], 8.0F));

    vertices.clear();
    indices.clear();
    hairline.flags =
        (PROGPU_NATIVE_STROKE_CAP_ROUND <<
            PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) |
        (PROGPU_NATIVE_STROKE_CAP_ROUND <<
            PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT);
    hairline.stroke_thickness = 4.0F;
    hairline.transform = {2.0F, 0.25F, 0.5F, 1.0F, 3.0F, 5.0F};
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        hairline,
        3.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 12U && indices.size() == 18U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 24.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[4].shape_type, 14.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].shape_type, 24.0F));
}

void invalid_geometry_flags_fail_without_partial_append() {
    progpu_native_geometry_primitive primitive{
        PROGPU_NATIVE_GEOMETRY_LINE,
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
            PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE,
        {0.0F, 0.0F},
        {1.0F, 1.0F},
        {},
        {},
        1.0F,
        0.0F,
        {1.0F, 1.0F, 1.0F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}
    };
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    PROGPU_REQUIRE(!progpu::native::append_geometry_primitive(
        primitive,
        0.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.empty() && indices.empty());
}

void connected_strokes_encode_caps_joins_and_closed_contours() {
    const progpu_native_point points[] = {
        {2.0F, 3.0F},
        {12.0F, 3.0F},
        {12.0F, 13.0F},
        {22.0F, 13.0F}
    };
    progpu_native_polyline open{
        0U,
        4U,
        {0.2F, 0.4F, 0.8F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
        0.0F,
        6.0F,
        PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE |
            (PROGPU_NATIVE_STROKE_CAP_ROUND <<
                PROGPU_NATIVE_POLYLINE_START_CAP_SHIFT) |
            (PROGPU_NATIVE_STROKE_CAP_TRIANGLE <<
                PROGPU_NATIVE_POLYLINE_END_CAP_SHIFT) |
            (PROGPU_NATIVE_STROKE_JOIN_ROUND <<
                PROGPU_NATIVE_POLYLINE_JOIN_SHIFT),
        0U
    };
    std::size_t vertex_capacity = 0U;
    std::size_t index_capacity = 0U;
    PROGPU_REQUIRE(progpu::native::polyline_capacity(
        open,
        vertex_capacity,
        index_capacity));
    PROGPU_REQUIRE(vertex_capacity == 140U);
    PROGPU_REQUIRE(index_capacity == 210U);

    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    PROGPU_REQUIRE(progpu::native::append_polyline(
        open,
        points,
        5.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 28U);
    PROGPU_REQUIRE(indices.size() == 42U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 22.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[4].shape_type, 3.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].shape_type, 23.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[12].shape_type, 3.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[16].shape_type, 23.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[20].shape_type, 3.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[24].shape_type, 22.0F));

    vertices.clear();
    indices.clear();
    open.point_count = 3U;
    open.stroke_thickness = 4.0F;
    open.flags = PROGPU_NATIVE_POLYLINE_FLAG_CLOSED |
        (PROGPU_NATIVE_STROKE_JOIN_BEVEL <<
            PROGPU_NATIVE_POLYLINE_JOIN_SHIFT);
    open.transform = {2.0F, 0.25F, 0.5F, 1.0F, 3.0F, 5.0F};
    PROGPU_REQUIRE(progpu::native::append_polyline(
        open,
        points,
        6.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 24U);
    PROGPU_REQUIRE(indices.size() == 36U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 14.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[4].shape_type, 13.0F));
}

void dashed_strokes_preserve_pattern_space_caps_and_closed_seams() {
    const double intervals[] = {2.0, 2.0};
    const progpu_native_dash_style flat_style{
        0U,
        2U,
        0.0,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        0U
    };
    const progpu_native_point line[] = {
        {0.0F, 0.0F},
        {10.0F, 0.0F}
    };
    progpu_native_polyline stroke{
        0U,
        2U,
        {0.2F, 0.7F, 0.4F, 1.0F},
        {2.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
        1.0F,
        4.0F,
        PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE,
        1U
    };
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    PROGPU_REQUIRE(progpu::native::append_polyline(
        stroke,
        line,
        2.0F,
        vertices,
        indices,
        &flat_style,
        1U,
        intervals,
        2U));
    PROGPU_REQUIRE(vertices.size() == 20U);
    PROGPU_REQUIRE(indices.size() == 30U);

    vertices.clear();
    indices.clear();
    stroke.flags = 0U;
    PROGPU_REQUIRE(progpu::native::append_polyline(
        stroke,
        line,
        2.0F,
        vertices,
        indices,
        &flat_style,
        1U,
        intervals,
        2U));
    PROGPU_REQUIRE(vertices.size() == 12U);
    PROGPU_REQUIRE(indices.size() == 18U);

    const progpu_native_dash_style round_style{
        0U,
        2U,
        0.0,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        0U
    };
    vertices.clear();
    indices.clear();
    stroke.flags = PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE;
    stroke.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    PROGPU_REQUIRE(progpu::native::append_polyline(
        stroke,
        line,
        2.0F,
        vertices,
        indices,
        &round_style,
        1U,
        intervals,
        2U));
    PROGPU_REQUIRE(vertices.size() == 28U);
    PROGPU_REQUIRE(indices.size() == 42U);

    const double closed_intervals[] = {100.0, 1.0};
    const progpu_native_point square[] = {
        {0.0F, 0.0F},
        {5.0F, 0.0F},
        {5.0F, 5.0F},
        {0.0F, 5.0F}
    };
    stroke.point_count = 4U;
    stroke.flags = PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE |
        PROGPU_NATIVE_POLYLINE_FLAG_CLOSED |
        (PROGPU_NATIVE_STROKE_JOIN_BEVEL <<
            PROGPU_NATIVE_POLYLINE_JOIN_SHIFT);
    vertices.clear();
    indices.clear();
    PROGPU_REQUIRE(progpu::native::append_polyline(
        stroke,
        square,
        2.0F,
        vertices,
        indices,
        &round_style,
        1U,
        closed_intervals,
        2U));
    PROGPU_REQUIRE(vertices.size() == 32U);
    PROGPU_REQUIRE(indices.size() == 48U);

    const double odd_intervals[] = {2.0, 1.0, 3.0};
    const progpu_native_dash_style odd_style{
        0U,
        3U,
        -2.0,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        0U
    };
    progpu::native::dash_pattern_state pattern{};
    stroke.point_count = 2U;
    stroke.flags = PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE;
    PROGPU_REQUIRE(progpu::native::try_create_dash_pattern(
        stroke,
        &odd_style,
        1U,
        odd_intervals,
        3U,
        pattern));
    PROGPU_REQUIRE(pattern.effective_count == 6U);
    PROGPU_REQUIRE(pattern.index < pattern.effective_count);
    PROGPU_REQUIRE(pattern.distance >= 0.0F);
}

void splines_evaluate_adaptively_without_retained_graphs() {
    const progpu_native_point points[] = {
        {0.0F, 0.0F},
        {10.0F, 0.0F},
        {10.0F, 10.0F}
    };
    const double knots[] = {0.0, 0.0, 1.0, 2.0, 2.0};
    progpu_native_spline spline{};
    spline.stroke.point_count = 3U;
    spline.stroke.color = {0.3F, 0.6F, 0.9F, 1.0F};
    spline.stroke.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    spline.stroke.stroke_thickness = 2.0F;
    spline.stroke.miter_limit = 4.0F;
    spline.knot_count = 5U;
    spline.degree = 1U;

    std::size_t segment_count = 0U;
    std::size_t vertex_capacity = 0U;
    std::size_t index_capacity = 0U;
    PROGPU_REQUIRE(progpu::native::spline_capacity(
        spline,
        points,
        knots,
        segment_count,
        vertex_capacity,
        index_capacity));
    PROGPU_REQUIRE(segment_count == 10U);

    std::vector<progpu::native::spline_homogeneous_point> work;
    progpu_native_point evaluated{};
    PROGPU_REQUIRE(progpu::native::try_evaluate_spline_point(
        spline,
        points,
        knots,
        nullptr,
        1.0,
        work,
        evaluated));
    PROGPU_REQUIRE(nearly_equal(evaluated.x, 10.0F));
    PROGPU_REQUIRE(nearly_equal(evaluated.y, 0.0F));

    std::array<progpu_native_point, 101U> sampled{};
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    work.reserve(2U);
    PROGPU_REQUIRE(progpu::native::append_spline(
        spline,
        points,
        knots,
        nullptr,
        segment_count,
        8.0F,
        sampled,
        work,
        vertices,
        indices));
    PROGPU_REQUIRE(nearly_equal(sampled.front().x, 0.0F));
    PROGPU_REQUIRE(nearly_equal(sampled.front().y, 0.0F));
    PROGPU_REQUIRE(nearly_equal(sampled[segment_count].x, 10.0F));
    PROGPU_REQUIRE(nearly_equal(sampled[segment_count].y, 10.0F));
    PROGPU_REQUIRE(!vertices.empty() && !indices.empty());

    spline.stroke.transform = {10.0F, 0.0F, 0.0F, 10.0F, 0.0F, 0.0F};
    PROGPU_REQUIRE(progpu::native::try_get_spline_segment_count(
        spline,
        points,
        segment_count));
    PROGPU_REQUIRE(segment_count == 50U);

    const progpu_native_point rational_points[] = {
        {1.0F, 0.0F},
        {1.0F, 1.0F},
        {0.0F, 1.0F}
    };
    const double rational_knots[] = {0.0, 0.0, 0.0, 1.0, 1.0, 1.0};
    const double rational_weights[] = {
        1.0,
        0.7071067811865476,
        1.0
    };
    spline.stroke.point_count = 3U;
    spline.knot_count = 6U;
    spline.weight_count = 3U;
    spline.degree = 2U;
    PROGPU_REQUIRE(progpu::native::try_evaluate_spline_point(
        spline,
        rational_points,
        rational_knots,
        rational_weights,
        0.5,
        work,
        evaluated));
    PROGPU_REQUIRE(std::abs(evaluated.x - 0.70710677F) <= 0.00001F);
    PROGPU_REQUIRE(std::abs(evaluated.y - 0.70710677F) <= 0.00001F);

    spline.knot_count = 2U;
    PROGPU_REQUIRE(progpu::native::spline_capacity(
        spline,
        rational_points,
        rational_knots,
        segment_count,
        vertex_capacity,
        index_capacity));
    PROGPU_REQUIRE(segment_count == 2U);
}

void indexed_analytic_batch_preserves_affine_local_coordinates() {
    progpu_native_analytic_primitive primitive{
        PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE,
        0U,
        10.0F,
        20.0F,
        100.0F,
        50.0F,
        12.0F,
        4.0F,
        {0.2F, 0.4F, 0.6F, 0.8F},
        {2.0F, 0.25F, -0.5F, 1.5F, 7.0F, 11.0F}
    };
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    PROGPU_REQUIRE(progpu::native::append_analytic_primitive(
        primitive,
        1.5F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 4U);
    PROGPU_REQUIRE(indices.size() == 6U);
    PROGPU_REQUIRE(indices[0] == 0U && indices[5] == 3U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[0], -53.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[1], -28.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].corner_radius, 12.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].stroke_thickness, 4.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 2.0F));

    const float local_x = 6.5F;
    const float local_y = 16.5F;
    PROGPU_REQUIRE(nearly_equal(
        vertices[0].position[0],
        local_x * 2.0F + local_y * -0.5F + 7.0F));
    PROGPU_REQUIRE(nearly_equal(
        vertices[0].position[1],
        local_x * 0.25F + local_y * 1.5F + 11.0F));
}

void singular_analytic_transform_fails_closed() {
    progpu_native_affine_2d singular{1.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F};
    float minimum_scale = 0.0F;
    PROGPU_REQUIRE(!progpu::native::try_get_minimum_scale(
        singular,
        minimum_scale));
}

void rectangle_batch_matches_vector_vertex_abi() {
    progpu_native_rect rectangle{
        10.0F,
        20.0F,
        100.0F,
        50.0F,
        {0.25F, 0.5F, 0.75F, 1.0F}
    };
    std::vector<progpu::native::vector_vertex> vertices;
    PROGPU_REQUIRE(
        progpu::native::append_solid_rect(rectangle, 1.5F, vertices));
    PROGPU_REQUIRE(vertices.size() == 6U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[0], 8.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[1], 18.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[2].position[0], 111.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[2].position[1], 71.5F));
    PROGPU_REQUIRE(nearly_equal(
        vertices[0].texture_coordinate[0], -51.5F));
    PROGPU_REQUIRE(nearly_equal(
        vertices[0].texture_coordinate[1], -26.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[0], 100.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[1], 50.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[2], 0.75F));
    PROGPU_REQUIRE(std::memcmp(&vertices[0], &vertices[3],
        sizeof(progpu::native::vector_vertex)) == 0);
}

void invalid_rectangles_fail_without_partial_append() {
    progpu_native_rect rectangle{
        0.0F,
        0.0F,
        -1.0F,
        10.0F,
        {1.0F, 1.0F, 1.0F, 1.0F}
    };
    std::vector<progpu::native::vector_vertex> vertices;
    PROGPU_REQUIRE(
        !progpu::native::append_solid_rect(rectangle, 1.5F, vertices));
    PROGPU_REQUIRE(vertices.empty());
    rectangle.width = 1.0F;
    rectangle.color.a = std::nanf("");
    PROGPU_REQUIRE(
        !progpu::native::append_solid_rect(rectangle, 1.5F, vertices));
    PROGPU_REQUIRE(vertices.empty());
}

} // namespace

int main() {
    api_contract_is_versioned();
    fixed_stroke_topology_masks_match_reference_classification();
    rectangle_batch_matches_vector_vertex_abi();
    indexed_analytic_batch_preserves_affine_local_coordinates();
    singular_analytic_transform_fails_closed();
    geometry_batch_encodes_direct_and_affine_lines();
    geometry_batch_encodes_device_strokes_and_fills();
    geometry_batch_encodes_gpu_and_affine_bezier_strokes();
    geometry_batch_preserves_cap_order_and_space();
    connected_strokes_encode_caps_joins_and_closed_contours();
    dashed_strokes_preserve_pattern_space_caps_and_closed_seams();
    splines_evaluate_adaptively_without_retained_graphs();
    invalid_geometry_flags_fail_without_partial_append();
    invalid_rectangles_fail_without_partial_append();
    std::cout << "ProGPU native CPU/ABI tests passed.\n";
    return 0;
}

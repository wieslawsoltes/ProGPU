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
    rectangle_batch_matches_vector_vertex_abi();
    indexed_analytic_batch_preserves_affine_local_coordinates();
    singular_analytic_transform_fails_closed();
    geometry_batch_encodes_direct_and_affine_lines();
    geometry_batch_encodes_device_strokes_and_fills();
    invalid_geometry_flags_fail_without_partial_append();
    invalid_rectangles_fail_without_partial_append();
    std::cout << "ProGPU native CPU/ABI tests passed.\n";
    return 0;
}

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
    PROGPU_REQUIRE(std::strstr(info.name, "ProGPU C++") != nullptr);
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
    invalid_rectangles_fail_without_partial_append();
    std::cout << "ProGPU native CPU/ABI tests passed.\n";
    return 0;
}

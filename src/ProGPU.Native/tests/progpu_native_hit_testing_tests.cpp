#include "progpu_native_hit_testing.hpp"

#include <array>
#include <cstdint>
#include <cstdlib>

namespace {

void require(bool condition) {
    if (!condition) {
        std::abort();
    }
}

progpu_native_hit_test_primitive primitive(
    float minimum_x,
    float minimum_y,
    float maximum_x,
    float maximum_y,
    std::int32_t id) {
    progpu_native_hit_test_primitive value{};
    value.bounds_min = {minimum_x, minimum_y};
    value.bounds_max = {maximum_x, maximum_y};
    value.kind = PROGPU_NATIVE_HIT_TEST_RECTANGLE_FILL;
    value.flags = PROGPU_NATIVE_HIT_TEST_VISIBLE |
        PROGPU_NATIVE_HIT_TEST_VISIBLE_TO_INPUT;
    value.id = id;
    return value;
}

} // namespace

static_assert(sizeof(progpu_native_hit_test_primitive) == 128U);
static_assert(sizeof(progpu_native_hit_test_node) == 32U);
static_assert(sizeof(progpu_native_hit_test_query) == 40U);
static_assert(sizeof(progpu_native_hit_test_result) == 32U);

int main() {
    using namespace progpu::native::hit_testing;
    const std::array primitives{
        primitive(0.0F, 0.0F, 10.0F, 10.0F, 10),
        primitive(90.0F, 90.0F, 100.0F, 100.0F, 20),
        primitive(0.0F, 90.0F, 10.0F, 100.0F, 30),
        primitive(90.0F, 0.0F, 100.0F, 10.0F, 40)};
    hit_test_index index;
    hit_test_build_error error{};
    require(try_build_hit_test_index(
        primitives,
        {},
        {.maximum_depth = 4U, .maximum_primitives_per_node = 1U},
        index,
        error));
    require(error == hit_test_build_error::none);
    require(index.primitives().size() == 4U);
    require(index.nodes().size() == 5U);
    require(index.primitive_indices().size() == 4U);
    require(index.nodes()[0U].first_child == 1U);
    require(index.nodes()[0U].child_count == 4U);
    require(index.nodes()[0U].primitive_count == 0U);
    require(index.primitive_indices()[0U] == 0U);
    require(index.primitive_indices()[1U] == 3U);
    require(index.primitive_indices()[2U] == 2U);
    require(index.primitive_indices()[3U] == 1U);

    const std::array invalid{
        primitive(2.0F, 0.0F, 1.0F, 1.0F, 1)};
    hit_test_index unchanged;
    require(!try_build_hit_test_index(
        invalid, {}, {}, unchanged, error));
    require(error == hit_test_build_error::invalid_argument);
    require(!try_build_hit_test_index(
        primitives,
        {},
        {.maximum_depth = 65U, .maximum_primitives_per_node = 1U},
        unchanged,
        error));
    require(error == hit_test_build_error::invalid_argument);
    return 0;
}

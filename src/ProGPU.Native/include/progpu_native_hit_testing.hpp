#ifndef PROGPU_NATIVE_HIT_TESTING_HPP
#define PROGPU_NATIVE_HIT_TESTING_HPP

#include "progpu_native.h"

#include <cstdint>
#include <span>
#include <vector>

namespace progpu::native::hit_testing {

enum class hit_test_build_error : std::uint32_t {
    none = 0U,
    invalid_argument,
    capacity_exceeded,
    out_of_memory
};

struct hit_test_build_options final {
    std::uint32_t maximum_depth = 8U;
    std::uint32_t maximum_primitives_per_node = 32U;
};

/* Immutable retained broad-phase index shared by CPU differential tests and
 * the WebGPU backend. Build cost is O(N * D) worst-case for N primitives and
 * bounded depth D; stable queries reuse these owned arrays. */
class hit_test_index final {
public:
    [[nodiscard]] std::span<const progpu_native_hit_test_primitive>
    primitives() const noexcept;
    [[nodiscard]] std::span<const progpu_native_hit_test_node>
    nodes() const noexcept;
    [[nodiscard]] std::span<const std::uint32_t>
    primitive_indices() const noexcept;
    [[nodiscard]] std::span<const progpu_native_path_segment>
    path_segments() const noexcept;

private:
    friend bool try_build_hit_test_index(
        std::span<const progpu_native_hit_test_primitive>,
        std::span<const progpu_native_path_segment>,
        hit_test_build_options,
        hit_test_index&,
        hit_test_build_error&) noexcept;

    std::vector<progpu_native_hit_test_primitive> primitives_;
    std::vector<progpu_native_hit_test_node> nodes_;
    std::vector<std::uint32_t> primitive_indices_;
    std::vector<progpu_native_path_segment> path_segments_;
};

[[nodiscard]] bool try_build_hit_test_index(
    std::span<const progpu_native_hit_test_primitive> primitives,
    std::span<const progpu_native_path_segment> path_segments,
    hit_test_build_options options,
    hit_test_index& output,
    hit_test_build_error& error) noexcept;

} // namespace progpu::native::hit_testing

#endif

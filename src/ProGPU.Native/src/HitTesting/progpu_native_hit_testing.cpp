#include "progpu_native_hit_testing.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <limits>
#include <new>
#include <stdexcept>
#include <utility>
#include <vector>

namespace progpu::native::hit_testing {
namespace {

constexpr std::size_t maximum_preallocated_node_capacity = 65'536U;
constexpr std::uint32_t maximum_supported_depth = 64U;

struct point_bounds final {
    progpu_native_point minimum{};
    progpu_native_point maximum{};
};

[[nodiscard]] bool finite_point(progpu_native_point point) noexcept {
    return std::isfinite(point.x) && std::isfinite(point.y);
}

[[nodiscard]] bool valid_bounds(
    progpu_native_point minimum,
    progpu_native_point maximum) noexcept {
    return finite_point(minimum) && finite_point(maximum) &&
        minimum.x <= maximum.x && minimum.y <= maximum.y;
}

[[nodiscard]] int find_containing_child(
    progpu_native_point primitive_minimum,
    progpu_native_point primitive_maximum,
    progpu_native_point center) noexcept {
    const auto fits_left = primitive_maximum.x <= center.x;
    const auto fits_right = primitive_minimum.x >= center.x;
    const auto fits_top = primitive_maximum.y <= center.y;
    const auto fits_bottom = primitive_minimum.y >= center.y;
    if (fits_top) {
        if (fits_left) {
            return 0;
        }
        if (fits_right) {
            return 1;
        }
    }
    if (fits_bottom) {
        if (fits_left) {
            return 2;
        }
        if (fits_right) {
            return 3;
        }
    }
    return -1;
}

[[nodiscard]] point_bounds child_bounds(
    int index,
    progpu_native_point minimum,
    progpu_native_point maximum,
    progpu_native_point center) noexcept {
    switch (index) {
    case 0:
        return {minimum, center};
    case 1:
        return {{center.x, minimum.y}, {maximum.x, center.y}};
    case 2:
        return {{minimum.x, center.y}, {center.x, maximum.y}};
    default:
        return {center, maximum};
    }
}

class index_builder final {
public:
    index_builder(
        std::span<const progpu_native_hit_test_primitive> primitives,
        hit_test_build_options options,
        std::vector<progpu_native_hit_test_node>& nodes,
        std::vector<std::uint32_t>& indices)
        : primitives_(primitives),
          options_(options),
          nodes_(nodes),
          indices_(indices) {
    }

    void build(progpu_native_point minimum, progpu_native_point maximum) {
        std::vector<std::uint32_t> root(primitives_.size());
        for (std::size_t index = 0U; index < root.size(); ++index) {
            root[index] = static_cast<std::uint32_t>(index);
        }
        nodes_.push_back({});
        fill_node(0U, minimum, maximum, root, 0U);
    }

private:
    void write_leaf(
        std::size_t node_index,
        progpu_native_point minimum,
        progpu_native_point maximum,
        std::span<const std::uint32_t> primitive_indices) {
        const auto first = static_cast<std::uint32_t>(indices_.size());
        indices_.insert(
            indices_.end(), primitive_indices.begin(), primitive_indices.end());
        nodes_[node_index] = {
            minimum,
            maximum,
            0U,
            0U,
            first,
            static_cast<std::uint32_t>(primitive_indices.size())};
    }

    void fill_node(
        std::size_t node_index,
        progpu_native_point minimum,
        progpu_native_point maximum,
        std::span<const std::uint32_t> primitive_indices,
        std::uint32_t depth) {
        if (depth >= options_.maximum_depth ||
            primitive_indices.size() <=
                options_.maximum_primitives_per_node ||
            (minimum.x == maximum.x && minimum.y == maximum.y)) {
            write_leaf(node_index, minimum, maximum, primitive_indices);
            return;
        }

        const progpu_native_point center{
            (minimum.x + maximum.x) * 0.5F,
            (minimum.y + maximum.y) * 0.5F};
        std::vector<std::uint32_t> retained;
        std::array<std::vector<std::uint32_t>, 4U> children;
        retained.reserve(primitive_indices.size());
        for (const auto primitive_index : primitive_indices) {
            const auto& primitive = primitives_[primitive_index];
            const auto child = find_containing_child(
                primitive.bounds_min, primitive.bounds_max, center);
            if (child < 0) {
                retained.push_back(primitive_index);
            } else {
                children[static_cast<std::size_t>(child)].push_back(
                    primitive_index);
            }
        }

        const auto child_count = static_cast<std::uint32_t>(std::count_if(
            children.begin(), children.end(),
            [](const auto& child) { return !child.empty(); }));
        const auto only_child = std::find_if(
            children.begin(), children.end(),
            [](const auto& child) { return !child.empty(); });
        if (child_count == 0U ||
            (child_count == 1U && retained.empty() &&
             only_child->size() == primitive_indices.size())) {
            write_leaf(node_index, minimum, maximum, primitive_indices);
            return;
        }

        const auto first_primitive =
            static_cast<std::uint32_t>(indices_.size());
        indices_.insert(indices_.end(), retained.begin(), retained.end());
        const auto first_child = static_cast<std::uint32_t>(nodes_.size());
        std::array<std::size_t, 4U> child_node_indices{
            std::numeric_limits<std::size_t>::max(),
            std::numeric_limits<std::size_t>::max(),
            std::numeric_limits<std::size_t>::max(),
            std::numeric_limits<std::size_t>::max()};
        for (std::size_t child = 0U; child < children.size(); ++child) {
            if (!children[child].empty()) {
                child_node_indices[child] = nodes_.size();
                nodes_.push_back({});
            }
        }
        nodes_[node_index] = {
            minimum,
            maximum,
            first_child,
            child_count,
            first_primitive,
            static_cast<std::uint32_t>(retained.size())};

        for (std::size_t child = 0U; child < children.size(); ++child) {
            if (children[child].empty()) {
                continue;
            }
            const auto bounds = child_bounds(
                static_cast<int>(child), minimum, maximum, center);
            fill_node(
                child_node_indices[child],
                bounds.minimum,
                bounds.maximum,
                children[child],
                depth + 1U);
        }
    }

    std::span<const progpu_native_hit_test_primitive> primitives_;
    hit_test_build_options options_;
    std::vector<progpu_native_hit_test_node>& nodes_;
    std::vector<std::uint32_t>& indices_;
};

[[nodiscard]] std::size_t estimate_node_capacity(
    std::size_t primitive_count,
    std::uint32_t maximum_primitives_per_node) noexcept {
    const auto divisor = static_cast<std::size_t>(
        maximum_primitives_per_node);
    const auto leaf_estimate = std::max<std::size_t>(
        1U, (primitive_count + divisor - 1U) / divisor);
    const auto estimated = 1ULL +
        static_cast<unsigned long long>(leaf_estimate) * 2ULL;
    const auto maximum_reasonable = std::min(
        static_cast<unsigned long long>(primitive_count) * 2ULL + 1ULL,
        static_cast<unsigned long long>(maximum_preallocated_node_capacity));
    return static_cast<std::size_t>(std::clamp(
        estimated, 1ULL, maximum_reasonable));
}

} // namespace

std::span<const progpu_native_hit_test_primitive>
hit_test_index::primitives() const noexcept {
    return primitives_;
}

std::span<const progpu_native_hit_test_node>
hit_test_index::nodes() const noexcept {
    return nodes_;
}

std::span<const std::uint32_t>
hit_test_index::primitive_indices() const noexcept {
    return primitive_indices_;
}

std::span<const progpu_native_path_segment>
hit_test_index::path_segments() const noexcept {
    return path_segments_;
}

bool try_build_hit_test_index(
    std::span<const progpu_native_hit_test_primitive> primitives,
    std::span<const progpu_native_path_segment> path_segments,
    hit_test_build_options options,
    hit_test_index& output,
    hit_test_build_error& error) noexcept {
    error = hit_test_build_error::none;
    if (options.maximum_primitives_per_node == 0U ||
        options.maximum_depth > maximum_supported_depth ||
        primitives.size() > std::numeric_limits<std::uint32_t>::max() ||
        path_segments.size() > std::numeric_limits<std::uint32_t>::max()) {
        error = hit_test_build_error::invalid_argument;
        return false;
    }
    for (const auto& primitive : primitives) {
        if (!valid_bounds(primitive.bounds_min, primitive.bounds_max)) {
            error = hit_test_build_error::invalid_argument;
            return false;
        }
    }

    try {
        hit_test_index candidate;
        candidate.primitives_.assign(primitives.begin(), primitives.end());
        candidate.path_segments_.assign(
            path_segments.begin(), path_segments.end());
        if (primitives.empty()) {
            candidate.nodes_.push_back({});
        } else {
            auto minimum = primitives.front().bounds_min;
            auto maximum = primitives.front().bounds_max;
            for (std::size_t index = 1U; index < primitives.size(); ++index) {
                minimum.x = std::min(minimum.x, primitives[index].bounds_min.x);
                minimum.y = std::min(minimum.y, primitives[index].bounds_min.y);
                maximum.x = std::max(maximum.x, primitives[index].bounds_max.x);
                maximum.y = std::max(maximum.y, primitives[index].bounds_max.y);
            }
            candidate.nodes_.reserve(estimate_node_capacity(
                primitives.size(), options.maximum_primitives_per_node));
            candidate.primitive_indices_.reserve(primitives.size());
            index_builder builder(
                primitives,
                options,
                candidate.nodes_,
                candidate.primitive_indices_);
            builder.build(minimum, maximum);
        }
        output = std::move(candidate);
        return true;
    } catch (const std::bad_alloc&) {
        error = hit_test_build_error::out_of_memory;
        return false;
    } catch (const std::length_error&) {
        error = hit_test_build_error::capacity_exceeded;
        return false;
    }
}

} // namespace progpu::native::hit_testing

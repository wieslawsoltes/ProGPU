#include "progpu_native_scene_builder_internal.hpp"
#include "progpu_native_hit_testing_validation.hpp"
#include "progpu_native_semantic_validation.hpp"

#include <cstring>
#include <limits>
#include <new>
#include <utility>

namespace progpu::native {
bool semantic_scene_builder::add_hit_test_index(
    std::span<const progpu_native_hit_test_primitive> primitives,
    std::span<const progpu_native_hit_test_node> nodes,
    std::span<const std::uint32_t> primitive_indices,
    std::span<const progpu_native_path_segment> path_segments,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (nodes.empty() || primitive_indices.size() != primitives.size() ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES ||
        primitives.size() > std::numeric_limits<std::uint32_t>::max() ||
        nodes.size() > std::numeric_limits<std::uint32_t>::max() ||
        primitive_indices.size() >
            std::numeric_limits<std::uint32_t>::max() ||
        path_segments.size() > std::numeric_limits<std::uint32_t>::max()) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    for (const auto& primitive : primitives) {
        if (!hit_testing::valid_hit_test_primitive(
                primitive, path_segments.size())) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    for (const auto& node : nodes) {
        if (!hit_testing::valid_hit_test_node(
                node, nodes.size(), primitive_indices.size())) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    for (const auto primitive_index : primitive_indices) {
        if (primitive_index >= primitives.size()) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }
    for (const auto& segment : path_segments) {
        if (!semantic::is_valid_semantic_segment(segment, true)) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
    }

    hit_testing::hit_test_page_layout layout{};
    if (!hit_testing::try_get_hit_test_page_layout(
            primitives.size(),
            nodes.size(),
            primitive_indices.size(),
            path_segments.size(),
            layout) ||
        layout.auxiliary_size > std::numeric_limits<std::size_t>::max()) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }

    try {
        std::vector<std::uint8_t> primitive_seen(
            primitives.size(), 0U);
        std::vector<std::uint8_t> node_parent_count(nodes.size(), 0U);
        for (std::size_t node_index = 0U;
             node_index < nodes.size();
             ++node_index) {
            const auto& node = nodes[node_index];
            if (node.child_count != 0U &&
                node.first_child <= node_index) {
                return implementation_->fail(
                    scene_build_error::invalid_argument);
            }
            for (std::uint32_t child = 0U;
                 child < node.child_count;
                 ++child) {
                const auto child_index = node.first_child + child;
                if (++node_parent_count[child_index] != 1U) {
                    return implementation_->fail(
                        scene_build_error::invalid_argument);
                }
                const auto& child_node = nodes[child_index];
                if (child_node.bounds_min.x < node.bounds_min.x ||
                    child_node.bounds_min.y < node.bounds_min.y ||
                    child_node.bounds_max.x > node.bounds_max.x ||
                    child_node.bounds_max.y > node.bounds_max.y) {
                    return implementation_->fail(
                        scene_build_error::invalid_argument);
                }
            }
            for (std::uint32_t slot = 0U;
                 slot < node.primitive_count;
                 ++slot) {
                const auto primitive_index =
                    primitive_indices[node.first_primitive + slot];
                if (++primitive_seen[primitive_index] != 1U) {
                    return implementation_->fail(
                        scene_build_error::invalid_argument);
                }
                const auto& primitive = primitives[primitive_index];
                if (primitive.bounds_min.x < node.bounds_min.x ||
                    primitive.bounds_min.y < node.bounds_min.y ||
                    primitive.bounds_max.x > node.bounds_max.x ||
                    primitive.bounds_max.y > node.bounds_max.y) {
                    return implementation_->fail(
                        scene_build_error::invalid_argument);
                }
            }
        }
        for (std::size_t index = 1U; index < nodes.size(); ++index) {
            if (node_parent_count[index] != 1U) {
                return implementation_->fail(
                    scene_build_error::invalid_argument);
            }
        }
        for (const auto seen : primitive_seen) {
            if (seen != 1U) {
                return implementation_->fail(
                    scene_build_error::invalid_argument);
            }
        }

        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_HIT_TEST_INDEX;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        const progpu_native_scene_hit_test_index page{
            sizeof(progpu_native_scene_hit_test_index),
            0U,
            static_cast<std::uint32_t>(primitives.size()),
            static_cast<std::uint32_t>(nodes.size()),
            static_cast<std::uint32_t>(primitive_indices.size()),
            static_cast<std::uint32_t>(path_segments.size()),
            static_cast<std::uint32_t>(layout.primitive_offset),
            static_cast<std::uint32_t>(layout.node_offset),
            static_cast<std::uint32_t>(layout.primitive_index_offset),
            static_cast<std::uint32_t>(layout.path_segment_offset)};
        resource.payload = scene_builder_detail::copy_bytes(
            std::span<const progpu_native_scene_hit_test_index>(&page, 1U));
        resource.auxiliary.resize(
            static_cast<std::size_t>(layout.auxiliary_size));
        if (!primitives.empty()) {
            std::memcpy(
                resource.auxiliary.data() +
                    static_cast<std::size_t>(layout.primitive_offset),
                primitives.data(),
                primitives.size_bytes());
        }
        std::memcpy(
            resource.auxiliary.data() +
                static_cast<std::size_t>(layout.node_offset),
            nodes.data(),
            nodes.size_bytes());
        if (!primitive_indices.empty()) {
            std::memcpy(
                resource.auxiliary.data() +
                    static_cast<std::size_t>(
                        layout.primitive_index_offset),
                primitive_indices.data(),
                primitive_indices.size_bytes());
        }
        if (!path_segments.empty()) {
            std::memcpy(
                resource.auxiliary.data() +
                    static_cast<std::size_t>(layout.path_segment_offset),
                path_segments.data(),
                path_segments.size_bytes());
        }
        resource_index = static_cast<std::uint32_t>(
            implementation_->resources.size());
        implementation_->resources.push_back(std::move(resource));
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

} // namespace progpu::native

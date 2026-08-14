#ifndef PROGPU_NATIVE_SCENE_BUILDER_HPP
#define PROGPU_NATIVE_SCENE_BUILDER_HPP

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <vector>

namespace progpu::native {

enum class scene_build_error : std::uint32_t {
    none = 0U,
    invalid_argument,
    invalid_state,
    unbalanced_stack,
    capacity_exceeded,
    out_of_memory
};

struct scene_build_metrics final {
    std::uint32_t command_count = 0U;
    std::uint32_t resource_count = 0U;
    std::uint32_t brush_count = 0U;
    std::uint32_t maximum_stack_depth = 0U;
    std::uint64_t arena_bytes = 0U;
    std::uint64_t stream_bytes = 0U;
};

/*
 * Standalone retained-scene recorder/compiler for native C++ clients.
 * Recording is O(C + R + P) in commands, resources, and payload bytes.
 * build() performs one deterministic O(C + R + P) pointer-free serialization.
 * Stable replay owns no builder work: callers retain the resulting byte vector
 * and submit one update only when its generation changes.
 */
class semantic_scene_builder final {
public:
    explicit semantic_scene_builder(
        std::uint64_t scene_id,
        std::uint64_t generation = 1U);
    ~semantic_scene_builder();

    semantic_scene_builder(semantic_scene_builder&&) noexcept;
    semantic_scene_builder& operator=(semantic_scene_builder&&) noexcept;
    semantic_scene_builder(const semantic_scene_builder&) = delete;
    semantic_scene_builder& operator=(const semantic_scene_builder&) = delete;

    bool reserve(
        std::uint32_t command_count,
        std::uint32_t resource_count,
        std::uint64_t arena_bytes) noexcept;
    bool reset(std::uint64_t scene_id, std::uint64_t generation) noexcept;

    bool add_solid_brush(
        progpu_native_color color,
        float opacity,
        std::uint32_t& brush_index) noexcept;
    bool add_state(
        const progpu_native_scene_state& state,
        std::uint32_t& resource_index) noexcept;

    bool save(
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;
    bool restore() noexcept;

    bool draw_analytic(
        std::span<const progpu_native_analytic_primitive> primitives,
        std::span<const std::uint32_t> brush_indices,
        progpu_native_image_rect bounds,
        std::uint32_t state_resource_index =
            PROGPU_NATIVE_SCENE_NO_INDEX) noexcept;

    bool build(
        std::vector<std::byte>& stream,
        scene_build_metrics* metrics = nullptr) const noexcept;

    scene_build_error last_error() const noexcept;
    std::uint64_t scene_id() const noexcept;
    std::uint64_t generation() const noexcept;

    static progpu_native_affine_2d identity_transform() noexcept;
    static progpu_native_scene_state identity_state() noexcept;

private:
    struct implementation;
    std::unique_ptr<implementation> implementation_;
};

} // namespace progpu::native

#endif

#pragma once

#include "progpu_native.h"
#include "progpu_native_scene_builder.hpp"

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace progpu::native::samples {

enum class motion_mark_segment_kind : std::uint8_t {
    line,
    quadratic,
    cubic
};

struct motion_mark_scene_metrics final {
    std::uint32_t element_count = 0U;
    std::uint32_t group_count = 0U;
    std::uint32_t primitive_count = 0U;
    std::uint32_t brush_count = 0U;
    std::uint32_t command_count = 0U;
    std::uint32_t resource_count = 0U;
    std::uint64_t stream_bytes = 0U;
    std::uint64_t generation = 0U;
};

/*
 * Pure C++20 port of the ProGPU-owned managed MotionMark sample in
 * src/ProGPU.Samples/Views/MotionMarkShowcaseVisual.cs. The element topology,
 * fixed 60 Hz split cadence, grid mapping, grouping, palettes, and style-at-
 * group-end contract are preserved. One changed scene is serialized into one
 * retained geometry batch; stable frames reuse the immutable byte stream.
 *
 * Rebuild: O(N + G) time and O(N + G) retained storage for N segments and G
 * groups. Stable replay is O(1) sample-side work with no scene serialization.
 */
class motion_mark_scene final {
public:
    explicit motion_mark_scene(
        std::uint32_t element_count = 1000U,
        std::uint32_t seed = 0x50A7C0DEU);

    bool resize(float width, float height) noexcept;
    bool set_element_count(std::uint32_t count) noexcept;
    bool set_color_mode(std::uint32_t mode) noexcept;
    bool regenerate(std::uint32_t seed) noexcept;
    bool advance(float delta_seconds) noexcept;
    void invalidate() noexcept;

    bool compile(
        std::vector<std::byte>& stream,
        motion_mark_scene_metrics& metrics) noexcept;

    bool dirty() const noexcept;
    std::uint64_t generation() const noexcept;
    std::uint32_t element_count() const noexcept;
    std::uint32_t group_count() const noexcept;
    std::span<const progpu_native_geometry_primitive> primitives() const
        noexcept;

private:
    struct grid_point final {
        std::int32_t x = 0;
        std::int32_t y = 0;
    };

    struct element final {
        motion_mark_segment_kind kind = motion_mark_segment_kind::line;
        grid_point start{};
        grid_point control1{};
        grid_point control2{};
        grid_point end{};
        progpu_native_color color{};
        float width = 1.0F;
        bool split = false;
    };

    std::uint32_t next_random() noexcept;
    float next_unit() noexcept;
    grid_point random_point(grid_point point) noexcept;
    progpu_native_color random_color() noexcept;
    element create_element(grid_point& current) noexcept;
    progpu_native_point map(grid_point point) const noexcept;
    progpu_native_point incoming_tangent(const element& value) const noexcept;
    progpu_native_point outgoing_tangent(const element& value) const noexcept;
    void rebuild_elements(std::uint32_t count) noexcept;
    void rebuild_primitives() noexcept;
    void mark_dirty() noexcept;

    static constexpr std::uint64_t scene_id_ = 0x4D4F54494F4E4D4BULL;
    semantic_scene_builder builder_{scene_id_, 1U};
    std::vector<element> elements_{};
    std::vector<progpu_native_geometry_primitive> primitives_{};
    std::vector<std::uint32_t> brush_indices_{};
    std::uint32_t random_state_ = 0x50A7C0DEU;
    std::uint32_t color_mode_ = 0U;
    std::uint32_t group_count_ = 0U;
    std::uint64_t generation_ = 1U;
    float width_ = 960.0F;
    float height_ = 540.0F;
    float grid_scale_ = 1.0F;
    float grid_offset_x_ = 0.0F;
    float grid_offset_y_ = 0.0F;
    float animation_budget_ = 0.0F;
    float split_toggle_budget_ = 0.0F;
    bool dirty_ = true;
};

} // namespace progpu::native::samples

#include "progpu_native_scene_builder_tests.hpp"

#include "progpu_native_scene.hpp"
#include "progpu_native_scene_builder.hpp"

#include <array>
#include <cstring>
#include <vector>

namespace progpu::native::tests {
namespace {

template<typename T>
T read(const std::vector<std::byte>& bytes, std::uint32_t offset) noexcept {
    T value{};
    std::memcpy(&value, bytes.data() + offset, sizeof(value));
    return value;
}

} // namespace

bool semantic_scene_builder_is_deterministic_and_valid() {
    semantic_scene_builder builder(701U, 4U);
    if (!builder.reserve(5U, 5U, 2048U)) {
        return false;
    }
    std::uint32_t blue = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t amber = PROGPU_NATIVE_SCENE_NO_INDEX;
    std::uint32_t duplicate_blue = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_solid_brush(
            {0.1F, 0.3F, 0.9F, 1.0F}, 1.0F, blue) ||
        !builder.add_solid_brush(
            {1.0F, 0.5F, 0.1F, 1.0F}, 0.75F, amber) ||
        !builder.add_solid_brush(
            {0.1F, 0.3F, 0.9F, 1.0F}, 1.0F, duplicate_blue) ||
        blue != duplicate_blue || blue == amber) {
        return false;
    }

    auto state = semantic_scene_builder::identity_state();
    state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
    state.transform = {1.0F, 0.0F, 0.0F, 1.0F, 4.0F, 6.0F};
    state.opacity = 0.8F;
    state.clip_rect = {8.0F, 10.0F, 80.0F, 60.0F};
    std::uint32_t state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_state(state, state_index) || !builder.save(state_index)) {
        return false;
    }

    const auto identity = semantic_scene_builder::identity_transform();
    const std::array primitives{
        progpu_native_analytic_primitive{
            PROGPU_NATIVE_PRIMITIVE_RECTANGLE,
            0U,
            10.0F,
            12.0F,
            32.0F,
            24.0F,
            0.0F,
            0.0F,
            {1.0F, 1.0F, 1.0F, 1.0F},
            identity},
        progpu_native_analytic_primitive{
            PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE,
            0U,
            48.0F,
            18.0F,
            40.0F,
            30.0F,
            6.0F,
            0.0F,
            {1.0F, 1.0F, 1.0F, 1.0F},
            identity}};
    const std::array brush_indices{blue, amber};
    if (!builder.draw_analytic(
            primitives,
            brush_indices,
            {10.0F, 12.0F, 78.0F, 36.0F})) {
        return false;
    }
    progpu_native_geometry_primitive line{};
    line.kind = PROGPU_NATIVE_GEOMETRY_LINE;
    line.p0 = {12.0F, 54.0F};
    line.p1 = {86.0F, 54.0F};
    line.stroke_thickness = 3.0F;
    line.color = {1.0F, 1.0F, 1.0F, 1.0F};
    line.transform = identity;
    if (!builder.draw_geometry(
            std::span<const progpu_native_geometry_primitive>(&line, 1U),
            std::span<const std::uint32_t>(&amber, 1U),
            {10.0F, 50.0F, 80.0F, 8.0F})) {
        return false;
    }
    const std::array stroke_points{
        progpu_native_point{12.0F, 64.0F},
        progpu_native_point{48.0F, 76.0F},
        progpu_native_point{86.0F, 62.0F}};
    const std::array stroke_doubles{2.0, 1.0};
    progpu_native_scene_stroke stroke{};
    stroke.struct_size = sizeof(stroke);
    stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
    stroke.point_count = stroke_points.size();
    stroke.dash_interval_count = stroke_doubles.size();
    stroke.color = {1.0F, 1.0F, 1.0F, 1.0F};
    stroke.transform = identity;
    stroke.stroke_thickness = 3.0F;
    stroke.miter_limit = 10.0F;
    stroke.start_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
    stroke.end_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
    stroke.line_join = PROGPU_NATIVE_STROKE_JOIN_ROUND;
    stroke.dash_cap = PROGPU_NATIVE_STROKE_CAP_SQUARE;
    if (!builder.draw_strokes(
            std::span<const progpu_native_scene_stroke>(&stroke, 1U),
            stroke_points,
            stroke_doubles,
            std::span<const std::uint32_t>(&blue, 1U),
            {10.0F, 60.0F, 80.0F, 20.0F}) ||
        !builder.restore()) {
        return false;
    }

    std::vector<std::byte> first;
    std::vector<std::byte> second;
    scene_build_metrics metrics{};
    if (!builder.build(first, &metrics) || !builder.build(second) ||
        first != second || metrics.command_count != 5U ||
        metrics.resource_count != 5U || metrics.brush_count != 2U ||
        metrics.maximum_stack_depth != 1U || metrics.arena_bytes == 0U) {
        return false;
    }
    const auto validated = scene::validate(first.data(), first.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.header.scene_id != 701U ||
        validated.header.generation != 4U ||
        validated.draw_count != 3U ||
        validated.maximum_stack_depth != 1U) {
        return false;
    }

    const auto brush_resource = read<progpu_native_scene_resource>(
        first,
        validated.header.resource_offset);
    const auto state_resource = read<progpu_native_scene_resource>(
        first,
        validated.header.resource_offset +
            sizeof(progpu_native_scene_resource));
    const auto analytic_resource = read<progpu_native_scene_resource>(
        first,
        validated.header.resource_offset +
            2U * sizeof(progpu_native_scene_resource));
    const auto geometry_resource = read<progpu_native_scene_resource>(
        first,
        validated.header.resource_offset +
            3U * sizeof(progpu_native_scene_resource));
    const auto stroke_resource = read<progpu_native_scene_resource>(
        first,
        validated.header.resource_offset +
            4U * sizeof(progpu_native_scene_resource));
    return brush_resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE &&
        brush_resource.payload_size ==
            2U * sizeof(progpu_native_scene_brush) &&
        state_resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE &&
        analytic_resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH &&
        analytic_resource.payload_size == sizeof(primitives) &&
        geometry_resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH &&
        geometry_resource.payload_size == sizeof(line) &&
        stroke_resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH &&
        stroke_resource.payload_size == sizeof(stroke) &&
        stroke_resource.auxiliary_size ==
            sizeof(stroke_points) + sizeof(stroke_doubles);
}

bool semantic_scene_builder_rejects_invalid_state() {
    semantic_scene_builder builder(702U, 1U);
    auto state = semantic_scene_builder::identity_state();
    state.opacity = 2.0F;
    std::uint32_t index = 0U;
    if (builder.add_state(state, index) ||
        builder.last_error() != scene_build_error::invalid_argument ||
        builder.restore() ||
        builder.last_error() != scene_build_error::unbalanced_stack) {
        return false;
    }
    state.opacity = 1.0F;
    if (!builder.add_state(state, index) || !builder.save(index)) {
        return false;
    }
    std::vector<std::byte> stream{std::byte{0x5a}};
    return !builder.build(stream) &&
        builder.last_error() == scene_build_error::unbalanced_stack &&
        stream == std::vector<std::byte>{std::byte{0x5a}};
}

} // namespace progpu::native::tests

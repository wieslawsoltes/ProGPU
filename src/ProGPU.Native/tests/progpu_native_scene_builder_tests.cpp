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
    if (!builder.reserve(3U, 3U, 1024U)) {
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
            {10.0F, 12.0F, 78.0F, 36.0F}) ||
        !builder.restore()) {
        return false;
    }

    std::vector<std::byte> first;
    std::vector<std::byte> second;
    scene_build_metrics metrics{};
    if (!builder.build(first, &metrics) || !builder.build(second) ||
        first != second || metrics.command_count != 3U ||
        metrics.resource_count != 3U || metrics.brush_count != 2U ||
        metrics.maximum_stack_depth != 1U || metrics.arena_bytes == 0U) {
        return false;
    }
    const auto validated = scene::validate(first.data(), first.size());
    if (validated.status != PROGPU_NATIVE_STATUS_SUCCESS ||
        validated.header.scene_id != 701U ||
        validated.header.generation != 4U ||
        validated.draw_count != 1U ||
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
    return brush_resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE &&
        brush_resource.payload_size ==
            2U * sizeof(progpu_native_scene_brush) &&
        state_resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE &&
        analytic_resource.kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH &&
        analytic_resource.payload_size == sizeof(primitives);
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

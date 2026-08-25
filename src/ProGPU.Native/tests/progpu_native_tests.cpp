#include "progpu_native.h"
#include "progpu_native_geometry.hpp"
#include "progpu_native_scene.hpp"

#include <cstddef>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <limits>
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

template<typename T>
void write_scene_record(
    std::vector<std::byte>& stream,
    std::size_t offset,
    const T& value) {
    PROGPU_REQUIRE(offset + sizeof(T) <= stream.size());
    std::memcpy(stream.data() + offset, &value, sizeof(T));
}

std::vector<std::byte> create_valid_mixed_scene(
    std::uint64_t scene_id = 41U,
    std::uint64_t generation = 7U) {
    constexpr std::uint32_t command_count = 8U;
    constexpr std::uint32_t resource_count = 5U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    constexpr std::uint32_t arena_size = 96U;
    constexpr std::uint32_t total_size = arena_offset + arena_size;
    std::vector<std::byte> stream(total_size);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = total_size;
    header.scene_id = scene_id;
    header.generation = generation;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = resource_count;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = arena_size;
    write_scene_record(stream, 0U, header);

    for (std::uint32_t index = 0U; index < resource_count; ++index) {
        progpu_native_scene_resource resource{};
        resource.struct_size = sizeof(resource);
        resource.kind =
            PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH + index;
        resource.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.resource_id = 100U + index;
        resource.generation = 20U + index;
        resource.payload_offset = arena_offset + index * 8U;
        resource.payload_size = index == 4U
            ? sizeof(progpu_native_scene_state)
            : 8U;
        write_scene_record(
            stream,
            resource_offset +
                index * sizeof(progpu_native_scene_resource),
            resource);
        if (index == 4U) {
            progpu_native_scene_state state{};
            state.struct_size = sizeof(state);
            state.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
            state.opacity = 0.75F;
            write_scene_record(stream, resource.payload_offset, state);
        } else {
            stream[resource.payload_offset] =
                static_cast<std::byte>(resource.kind);
        }
    }

    const std::array<std::uint32_t, command_count> kinds{
        PROGPU_NATIVE_SCENE_COMMAND_SAVE,
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
        PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER,
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH,
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN,
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE,
        PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER,
        PROGPU_NATIVE_SCENE_COMMAND_RESTORE
    };
    for (std::uint32_t index = 0U; index < command_count; ++index) {
        progpu_native_scene_command command{};
        command.struct_size = sizeof(command);
        command.kind = kinds[index];
        command.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        command.command_id = 1000U + index;
        command.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        command.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        command.bounds_x = 1.0F;
        command.bounds_y = 2.0F;
        command.bounds_width = 300.0F;
        command.bounds_height = 200.0F;
        if (command.kind >= PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
            command.resource_index = command.kind -
                PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC;
        }
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_SAVE) {
            command.state_index = 4U;
        }
        write_scene_record(
            stream,
            command_offset + index * sizeof(progpu_native_scene_command),
            command);
    }
    return stream;
}

std::vector<std::byte> create_nested_scene(
    std::uint32_t depth,
    bool layers = false) {
    const std::uint32_t command_count = depth * 2U;
    const std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    const std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    const std::uint32_t arena_size = layers
        ? depth * sizeof(progpu_native_scene_layer)
        : 0U;
    const std::uint32_t total_size = resource_offset + arena_size;
    std::vector<std::byte> stream(total_size);
    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = total_size;
    header.scene_id = 55U;
    header.generation = 1U;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = resource_offset;
    header.arena_size = arena_size;
    write_scene_record(stream, 0U, header);
    for (std::uint32_t index = 0U; index < command_count; ++index) {
        progpu_native_scene_command command{};
        command.struct_size = sizeof(command);
        command.kind = index < depth
            ? (layers
                ? PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER
                : PROGPU_NATIVE_SCENE_COMMAND_SAVE)
            : (layers
                ? PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER
                : PROGPU_NATIVE_SCENE_COMMAND_RESTORE);
        command.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        command.command_id = index + 1U;
        command.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        command.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (layers && index < depth) {
            command.payload_offset = resource_offset +
                index * sizeof(progpu_native_scene_layer);
            command.payload_size = sizeof(progpu_native_scene_layer);
            progpu_native_scene_layer layer{};
            layer.struct_size = sizeof(layer);
            layer.flags = PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
            layer.opacity = 1.0F;
            layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
            layer.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            write_scene_record(stream, command.payload_offset, layer);
        }
        write_scene_record(
            stream,
            command_offset + index * sizeof(progpu_native_scene_command),
            command);
    }
    return stream;
}

std::vector<std::byte> create_layer_descriptor_scene(
    const progpu_native_scene_layer& layer) {
    constexpr std::uint32_t command_count = 2U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset;
    constexpr std::uint32_t total_size = arena_offset +
        sizeof(progpu_native_scene_layer);
    std::vector<std::byte> stream(total_size);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = total_size;
    header.scene_id = 56U;
    header.generation = 1U;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = sizeof(progpu_native_scene_layer);
    write_scene_record(stream, 0U, header);

    progpu_native_scene_command push{};
    push.struct_size = sizeof(push);
    push.kind = PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER;
    push.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    push.command_id = 1U;
    push.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    push.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    push.payload_offset = arena_offset;
    push.payload_size = sizeof(layer);
    write_scene_record(stream, command_offset, push);

    progpu_native_scene_command pop{};
    pop.struct_size = sizeof(pop);
    pop.kind = PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER;
    pop.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    pop.command_id = 2U;
    pop.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    pop.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    write_scene_record(
        stream,
        command_offset + sizeof(progpu_native_scene_command),
        pop);
    write_scene_record(stream, arena_offset, layer);
    return stream;
}

std::vector<std::byte> create_local_cache_layer_scene(
    const progpu_native_scene_layer& layer,
    const progpu_native_scene_state& composite_state) {
    constexpr std::uint32_t command_count = 2U;
    constexpr std::uint32_t resource_count = 1U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    constexpr std::uint32_t state_offset = arena_offset;
    constexpr std::uint32_t layer_offset = state_offset +
        sizeof(progpu_native_scene_state);
    constexpr std::uint32_t total_size = layer_offset +
        sizeof(progpu_native_scene_layer);
    std::vector<std::byte> stream(total_size);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = total_size;
    header.scene_id = 57U;
    header.generation = 1U;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = resource_count;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = total_size - arena_offset;
    write_scene_record(stream, 0U, header);

    progpu_native_scene_resource state_resource{};
    state_resource.struct_size = sizeof(state_resource);
    state_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_STATE;
    state_resource.resource_id = 1U;
    state_resource.generation = 1U;
    state_resource.payload_offset = state_offset;
    state_resource.payload_size = sizeof(composite_state);
    write_scene_record(stream, resource_offset, state_resource);
    write_scene_record(stream, state_offset, composite_state);
    write_scene_record(stream, layer_offset, layer);

    progpu_native_scene_command push{};
    push.struct_size = sizeof(push);
    push.kind = PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER;
    push.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    push.command_id = 1U;
    push.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    push.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    push.payload_offset = layer_offset;
    push.payload_size = sizeof(layer);
    write_scene_record(stream, command_offset, push);

    progpu_native_scene_command pop{};
    pop.struct_size = sizeof(pop);
    pop.kind = PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER;
    pop.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    pop.command_id = 2U;
    pop.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    pop.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    write_scene_record(
        stream,
        command_offset + sizeof(progpu_native_scene_command),
        pop);
    return stream;
}

std::vector<std::byte> create_typed_layer_resource_scene() {
    constexpr std::uint32_t command_count = 2U;
    constexpr std::uint32_t resource_count = 2U;
    constexpr std::uint32_t command_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t resource_offset = command_offset +
        command_count * sizeof(progpu_native_scene_command);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    constexpr std::uint32_t mask_offset = arena_offset;
    constexpr std::uint32_t chain_offset = mask_offset +
        sizeof(progpu_native_scene_layer_mask);
    constexpr std::uint32_t effects_offset = chain_offset +
        sizeof(progpu_native_scene_effect_chain);
    constexpr std::uint32_t layer_offset = effects_offset +
        sizeof(progpu_native_group_effect);
    constexpr std::uint32_t total_size = layer_offset +
        sizeof(progpu_native_scene_layer);
    std::vector<std::byte> stream(total_size);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = total_size;
    header.scene_id = 57U;
    header.generation = 1U;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = resource_count;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = total_size - arena_offset;
    write_scene_record(stream, 0U, header);

    progpu_native_scene_resource mask_resource{};
    mask_resource.struct_size = sizeof(mask_resource);
    mask_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK;
    mask_resource.resource_id = 1U;
    mask_resource.generation = 1U;
    mask_resource.payload_offset = mask_offset;
    mask_resource.payload_size = sizeof(progpu_native_scene_layer_mask);
    write_scene_record(stream, resource_offset, mask_resource);

    progpu_native_scene_resource effect_resource{};
    effect_resource.struct_size = sizeof(effect_resource);
    effect_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_EFFECT_CHAIN;
    effect_resource.resource_id = 2U;
    effect_resource.generation = 1U;
    effect_resource.payload_offset = chain_offset;
    effect_resource.payload_size = sizeof(progpu_native_scene_effect_chain);
    effect_resource.auxiliary_offset = effects_offset;
    effect_resource.auxiliary_size = sizeof(progpu_native_group_effect);
    write_scene_record(
        stream,
        resource_offset + sizeof(progpu_native_scene_resource),
        effect_resource);

    progpu_native_scene_layer_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
    mask.bounds = {4.0F, 4.0F, 24.0F, 16.0F};
    mask.transform = {1.0F, 0.0F, 0.0F, 1.0F, 2.0F, 3.0F};
    mask.corner_radii_x[0] = 3.0F;
    mask.corner_radii_x[1] = 4.0F;
    mask.corner_radii_x[2] = 5.0F;
    mask.corner_radii_x[3] = 6.0F;
    mask.corner_radii_y[0] = 6.0F;
    mask.corner_radii_y[1] = 5.0F;
    mask.corner_radii_y[2] = 4.0F;
    mask.corner_radii_y[3] = 3.0F;
    mask.opacity = 0.75F;
    write_scene_record(stream, mask_offset, mask);

    progpu_native_scene_effect_chain chain{};
    chain.struct_size = sizeof(chain);
    chain.effect_count = 1U;
    chain.revision = 9U;
    write_scene_record(stream, chain_offset, chain);

    progpu_native_group_effect effect{};
    effect.struct_size = sizeof(effect);
    effect.kind = PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR;
    effect.revision = 3U;
    effect.sigma_x = 2.0F;
    effect.sigma_y = 1.5F;
    write_scene_record(stream, effects_offset, effect);

    progpu_native_scene_layer layer{};
    layer.struct_size = sizeof(layer);
    layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
        PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
    layer.bounds = {0.0F, 0.0F, 32.0F, 24.0F};
    layer.opacity = 0.5F;
    layer.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
    layer.mask_resource_index = 0U;
    layer.effect_resource_index = 1U;
    layer.content_revision = 11U;
    layer.composite_revision = 12U;
    write_scene_record(stream, layer_offset, layer);

    progpu_native_scene_command push{};
    push.struct_size = sizeof(push);
    push.kind = PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER;
    push.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    push.command_id = 1U;
    push.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    push.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    push.payload_offset = layer_offset;
    push.payload_size = sizeof(layer);
    write_scene_record(stream, command_offset, push);

    progpu_native_scene_command pop{};
    pop.struct_size = sizeof(pop);
    pop.kind = PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER;
    pop.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    pop.command_id = 2U;
    pop.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    pop.resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    write_scene_record(
        stream,
        command_offset + sizeof(progpu_native_scene_command),
        pop);
    return stream;
}

void fixed_stroke_topology_masks_match_reference_classification() {
    using progpu::native::stroke_triangle;
    std::array<stroke_triangle, 8U> triangles{};
    const progpu_native_point center{13.25F, -7.5F};
    const progpu_native_point direction{3.0F, 4.0F};
    for (std::uint32_t cap = PROGPU_NATIVE_STROKE_CAP_SQUARE;
         cap <= PROGPU_NATIVE_STROKE_CAP_TRIANGLE;
         ++cap) {
        const std::size_t count = progpu::native::create_cap_triangles(
            triangles, cap, 5.5F, center, direction, false);
        progpu_native_point normalized{};
        PROGPU_REQUIRE(progpu::native::try_normalize(
            direction, {}, normalized));
        for (std::size_t index = 0U; index < count; ++index) {
            std::uint32_t expected_exterior = 0U;
            std::uint32_t expected_owned = 0U;
            std::uint32_t actual_exterior = 0U;
            std::uint32_t actual_owned = 0U;
            progpu::native::classify_triangle_edges(
                triangles.data(), count, index, true, center, normalized,
                expected_exterior, expected_owned);
            progpu::native::classify_cap_triangle_edges(
                cap, count, index, actual_exterior, actual_owned);
            PROGPU_REQUIRE(actual_exterior == expected_exterior);
            PROGPU_REQUIRE(actual_owned == expected_owned);
        }
    }

    const progpu_native_point incoming{1.0F, 0.0F};
    const progpu_native_point outgoing{0.35F, 0.94F};
    for (std::uint32_t join = PROGPU_NATIVE_STROKE_JOIN_MITER;
         join <= PROGPU_NATIVE_STROKE_JOIN_ROUND;
         ++join) {
        const std::size_t count = progpu::native::create_join_triangles(
            triangles, join, 5.5F, 4.0F, center, incoming, outgoing);
        for (std::size_t index = 0U; index < count; ++index) {
            std::uint32_t expected_exterior = 0U;
            std::uint32_t expected_owned = 0U;
            std::uint32_t actual_exterior = 0U;
            std::uint32_t actual_owned = 0U;
            progpu::native::classify_triangle_edges(
                triangles.data(), count, index, false, {}, {},
                expected_exterior, expected_owned);
            progpu::native::classify_join_triangle_edges(
                join, count, index, actual_exterior, actual_owned);
            PROGPU_REQUIRE(actual_exterior == expected_exterior);
            PROGPU_REQUIRE(actual_owned == expected_owned);
        }
    }
}

void api_contract_is_versioned() {
    PROGPU_REQUIRE(
        progpu_native_get_abi_version() == PROGPU_NATIVE_ABI_VERSION);
    progpu_native_hit_test_query query{};
    std::uint64_t request_token = 0U;
    PROGPU_REQUIRE(progpu_native_engine_begin_hit_test(
        nullptr,
        &query,
        &request_token) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    progpu_native_hit_test_result summary{};
    std::uint32_t result_count = 0U;
    std::uint8_t complete = 0U;
    PROGPU_REQUIRE(progpu_native_engine_poll_hit_test(
        nullptr,
        1U,
        nullptr,
        0U,
        &result_count,
        &summary,
        &complete) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);

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
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_BEZIER_STROKES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_STROKE_CAPS) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_CONNECTED_STROKES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SPLINE_STROKES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_DASHED_STROKES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_PATH_FILL_ATLAS) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_POSITIONED_GLYPH_ATLAS) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_RESIZABLE_ATLASES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_RETAINED_RGBA_IMAGE) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_RGBA_VIEW) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_EXTERNAL_IMAGE_MASK) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_EXPLICIT_QUEUE_TIMELINE) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_FRAME_DRAW_STATE) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_GROUP_OPACITY) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_COMMON_GROUP_MASK) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_ANALYTIC_ROUNDED_GROUP_MASK) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_RETAINED_VECTOR_CLIP_CHAIN) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_GROUP_GAUSSIAN_BLUR) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_GROUP_DROP_SHADOW) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_BOUNDED_GROUP_EFFECT_CHAIN) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_GROUP_BLEND_MODES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_SCENE_SNAPSHOTS) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_SCENE_RENDERING) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_RETAINED_BRUSHES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_RETAINED_TEXT_STYLES) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_COLOR_GLYPH_ATLAS) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_DEVICE_LOSS_RECREATION) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_GEOMETRY_BATCH) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_POINT_BATCH) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_VERTEX_MESH) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_STROKE_BATCH) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_IMAGE_PATCH_BATCH) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_IMAGE_MIPMAP_SAMPLING) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_IMAGE_FRAME_MIPMAP_SAMPLING) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_SEMANTIC_VECTOR_CLIP_MASK) != 0U);
    PROGPU_REQUIRE((info.capabilities &
        PROGPU_NATIVE_CAPABILITY_RETAINED_GPU_HIT_TESTING) != 0U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_header) == 80U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_resource) == 48U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_command) == 64U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_metrics) == 64U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_image_draw) == 88U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_image_patch_batch) == 16U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_image_patch) == 88U);
    PROGPU_REQUIRE(
        sizeof(progpu_native_scene_image_sampling_options) == 16U);
    PROGPU_REQUIRE(
        sizeof(progpu_native_scene_image_color_matrix) == 96U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_state) == 64U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_layer) == 64U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_layer_mask) == 104U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_layer_mask_chain) == 432U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_effect_chain) == 16U);
    PROGPU_REQUIRE(sizeof(progpu_native_group_effect) == 56U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_path_fill) == 96U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_stroke) == 160U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_glyph_outline) == 40U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_frame) == 80U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_brush) == 256U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_gradient_stop) == 32U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_draw_brushes) == 16U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_text_style) == 32U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_color_glyph_bitmap) == 48U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_glyph_draw) == 24U);
    PROGPU_REQUIRE(sizeof(progpu_native_scene_frame_metrics) == 104U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_frame_metrics,
        brush_upload_bytes) == 72U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_frame_metrics,
        gradient_stop_upload_bytes) == 80U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_frame_metrics,
        text_style_upload_bytes) == 88U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_frame_metrics,
        color_glyph_upload_bytes) == 96U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_image_draw,
        source_rect) == 24U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_image_draw,
        transform) == 56U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_state,
        transform) == 8U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_state,
        opacity) == 32U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_state,
        clip_rect) == 40U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_layer,
        bounds) == 8U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_layer,
        opacity) == 24U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_layer,
        mask_resource_index) == 32U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_layer,
        content_revision) == 40U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_layer_mask,
        bounds) == 16U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_layer_mask,
        transform) == 32U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_layer_mask,
        opacity) == 88U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_path_fill,
        color) == 48U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_glyph_outline,
        raster_scale) == 32U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_frame,
        target_view) == 16U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_frame,
        scene_id) == 40U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_frame,
        flags) == 56U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_scene_frame,
        damage_height) == 72U);
    PROGPU_REQUIRE(sizeof(progpu_native_glyph_outline) == 40U);
    PROGPU_REQUIRE(sizeof(progpu_native_positioned_glyph) == 64U);
    PROGPU_REQUIRE(sizeof(progpu_native_clip_path) == 88U);
    PROGPU_REQUIRE(sizeof(progpu_native_clip_chain) == 56U);
    PROGPU_REQUIRE(sizeof(progpu_native_path_boolean_node) == 48U);
    PROGPU_REQUIRE(sizeof(progpu_native_group_mask) == 152U);
    PROGPU_REQUIRE(offsetof(progpu_native_group_mask, external_view) == 16U);
    PROGPU_REQUIRE(offsetof(progpu_native_group_mask, destination_rect) == 48U);
    PROGPU_REQUIRE(offsetof(progpu_native_group_mask, transform) == 80U);
    PROGPU_REQUIRE(offsetof(progpu_native_group_mask, opacity) == 136U);
    PROGPU_REQUIRE(offsetof(progpu_native_group_mask, clip_chain) == 144U);
    PROGPU_REQUIRE(sizeof(progpu_native_group_effect) == 56U);
    PROGPU_REQUIRE(offsetof(progpu_native_group_effect, sigma_x) == 16U);
    PROGPU_REQUIRE(offsetof(progpu_native_group_effect, sigma_y) == 20U);
    PROGPU_REQUIRE(offsetof(progpu_native_group_effect, offset_x) == 32U);
    PROGPU_REQUIRE(offsetof(progpu_native_group_effect, color_a) == 52U);
    PROGPU_REQUIRE(sizeof(progpu_native_group_effect_chain) == 24U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_group_effect_chain,
        effects) == 16U);
    PROGPU_REQUIRE(sizeof(progpu_native_draw_state) == 72U);
    PROGPU_REQUIRE(offsetof(progpu_native_draw_state, group_mask) == 40U);
    PROGPU_REQUIRE(offsetof(progpu_native_draw_state, group_effect) == 48U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_draw_state,
        group_effect_chain) == 56U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_draw_state,
        group_blend_mode) == 64U);
    PROGPU_REQUIRE(sizeof(progpu_native_layer_metrics) == 200U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_layer_metrics,
        mask_kind) == 56U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_layer_metrics,
        mask_uniform_upload_bytes) == 72U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_layer_metrics,
        clip_path_count) == 80U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_layer_metrics,
        clip_path_upload_bytes) == 96U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_layer_metrics,
        effect_kind) == 120U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_layer_metrics,
        effect_uniform_upload_bytes) == 136U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_layer_metrics,
        effect_texture_bytes) == 144U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_layer_metrics,
        effect_count) == 152U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_layer_metrics,
        blend_mode) == 168U);
    PROGPU_REQUIRE(offsetof(
        progpu_native_layer_metrics,
        blend_source_texture_bytes) == 192U);
    PROGPU_REQUIRE(sizeof(progpu_native_glyph_frame) == 104U);
    PROGPU_REQUIRE(sizeof(progpu_native_glyph_frame_metrics) == 80U);
    PROGPU_REQUIRE(sizeof(progpu_native_image_rect) == 16U);
    PROGPU_REQUIRE(sizeof(progpu_native_image_frame) == 224U);
    PROGPU_REQUIRE(offsetof(progpu_native_image_frame, draw_state) == 200U);
    PROGPU_REQUIRE(offsetof(progpu_native_image_frame, cubic_b) == 208U);
    PROGPU_REQUIRE(offsetof(progpu_native_image_frame, cubic_c) == 212U);
    PROGPU_REQUIRE(offsetof(progpu_native_image_frame, max_anisotropy) == 216U);
    PROGPU_REQUIRE(offsetof(progpu_native_image_frame, reserved3) == 220U);
    PROGPU_REQUIRE(sizeof(progpu_native_image_frame_metrics) == 72U);
    PROGPU_REQUIRE(std::strstr(info.name, "ProGPU C++") != nullptr);
}

void semantic_scene_stream_validates_mixed_order_and_stack() {
    const auto stream = create_valid_mixed_scene();
    progpu_native_scene_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_NONE);
    PROGPU_REQUIRE(metrics.scene_id == 41U);
    PROGPU_REQUIRE(metrics.generation == 7U);
    PROGPU_REQUIRE(metrics.command_count == 8U);
    PROGPU_REQUIRE(metrics.resource_count == 5U);
    PROGPU_REQUIRE(metrics.draw_count == 4U);
    PROGPU_REQUIRE(metrics.maximum_stack_depth == 2U);
    PROGPU_REQUIRE(metrics.snapshot_bytes == stream.size());
    PROGPU_REQUIRE(metrics.payload_bytes == 96U);
}

void semantic_scene_static_guideline_resource_validates() {
    constexpr std::uint32_t resource_count = 2U;
    constexpr std::uint32_t resource_offset =
        sizeof(progpu_native_scene_header);
    constexpr std::uint32_t arena_offset = resource_offset +
        resource_count * sizeof(progpu_native_scene_resource);
    constexpr std::uint32_t guideline_size =
        sizeof(progpu_native_scene_guideline_set) + sizeof(double);
    constexpr std::uint32_t arena_size =
        guideline_size + sizeof(progpu_native_scene_state);
    std::vector<std::byte> stream(arena_offset + arena_size);

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = static_cast<std::uint32_t>(stream.size());
    header.scene_id = 42U;
    header.generation = 8U;
    header.command_offset = sizeof(header);
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = resource_count;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = arena_size;
    write_scene_record(stream, 0U, header);

    progpu_native_scene_resource guideline_resource{};
    guideline_resource.struct_size = sizeof(guideline_resource);
    guideline_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET;
    guideline_resource.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    guideline_resource.resource_id = 1U;
    guideline_resource.generation = 1U;
    guideline_resource.payload_offset = arena_offset;
    guideline_resource.payload_size = guideline_size;
    write_scene_record(stream, resource_offset, guideline_resource);

    progpu_native_scene_guideline_set guidelines{};
    guidelines.struct_size = sizeof(guidelines);
    guidelines.guideline_x_count = 1U;
    write_scene_record(stream, arena_offset, guidelines);
    const double guideline_x = 8.25;
    write_scene_record(
        stream,
        arena_offset + sizeof(guidelines),
        guideline_x);

    progpu_native_scene_resource state_resource{};
    state_resource.struct_size = sizeof(state_resource);
    state_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_STATE;
    state_resource.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
    state_resource.resource_id = 2U;
    state_resource.generation = 1U;
    state_resource.payload_offset = arena_offset + guideline_size;
    state_resource.payload_size = sizeof(progpu_native_scene_state);
    write_scene_record(
        stream,
        resource_offset + sizeof(progpu_native_scene_resource),
        state_resource);

    progpu_native_scene_state state{};
    state.struct_size = sizeof(state);
    state.flags = PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET;
    state.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    state.opacity = 1.0F;
    write_scene_record(stream, state_resource.payload_offset, state);

    progpu_native_scene_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_NONE);
    PROGPU_REQUIRE(metrics.resource_count == resource_count);
    PROGPU_REQUIRE(metrics.payload_bytes == arena_size);
}

void semantic_scene_stream_rejects_malformed_updates_transactionally() {
    auto stream = create_valid_mixed_scene();
    auto header = progpu::native::scene::validate(
        stream.data(), stream.size()).header;
    progpu_native_scene_metrics metrics{};
    metrics.struct_size = sizeof(metrics);

    auto command = progpu_native_scene_command{};
    std::memcpy(
        &command,
        stream.data() + header.command_offset,
        sizeof(command));
    command.kind = 0x7fffffffU;
    write_scene_record(stream, header.command_offset, command);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_UNSUPPORTED);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_UNSUPPORTED);

    stream = create_valid_mixed_scene();
    const std::size_t final_offset = header.command_offset +
        7U * header.command_stride;
    std::memcpy(&command, stream.data() + final_offset, sizeof(command));
    command.kind = PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER;
    write_scene_record(stream, final_offset, command);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_STACK);

    stream = create_valid_mixed_scene();
    const std::size_t second_offset = header.command_offset +
        header.command_stride;
    std::memcpy(&command, stream.data() + second_offset, sizeof(command));
    command.command_id = 1000U;
    write_scene_record(stream, second_offset, command);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_ID);

    stream = create_valid_mixed_scene();
    std::memcpy(&command, stream.data() + second_offset, sizeof(command));
    command.resource_index = 1U;
    write_scene_record(stream, second_offset, command);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_RECORD);

    stream = create_valid_mixed_scene();
    const std::size_t state_resource_offset = header.resource_offset +
        4U * header.resource_stride;
    progpu_native_scene_resource state_resource{};
    std::memcpy(
        &state_resource,
        stream.data() + state_resource_offset,
        sizeof(state_resource));
    progpu_native_scene_state state{};
    std::memcpy(
        &state,
        stream.data() + state_resource.payload_offset,
        sizeof(state));
    state.opacity = std::numeric_limits<float>::quiet_NaN();
    write_scene_record(stream, state_resource.payload_offset, state);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_VALUE);

    stream = create_valid_mixed_scene();
    std::memcpy(
        &state,
        stream.data() + state_resource.payload_offset,
        sizeof(state));
    state.clip_rect = {1.0F, 2.0F, 3.0F, 4.0F};
    write_scene_record(stream, state_resource.payload_offset, state);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_VALUE);

    stream = create_valid_mixed_scene();
    std::memcpy(
        &state,
        stream.data() + state_resource.payload_offset,
        sizeof(state));
    state.flags = PROGPU_NATIVE_SCENE_STATE_MASK;
    state.mask_resource_index = 0U;
    write_scene_record(stream, state_resource.payload_offset, state);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_RECORD);

    stream = create_valid_mixed_scene();
    std::memcpy(
        &state,
        stream.data() + state_resource.payload_offset,
        sizeof(state));
    state.mask_resource_index = 1U;
    write_scene_record(stream, state_resource.payload_offset, state);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_VALUE);

    stream = create_valid_mixed_scene();
    std::memcpy(&command, stream.data() + final_offset, sizeof(command));
    command.state_index = 4U;
    write_scene_record(stream, final_offset, command);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_RECORD);
}

void semantic_scene_resource_generations_are_monotonic() {
    const auto previous = create_valid_mixed_scene(41U, 7U);
    auto next = create_valid_mixed_scene(41U, 8U);
    const auto header = progpu::native::scene::validate(
        next.data(), next.size()).header;
    progpu_native_scene_resource resource{};
    const std::size_t resource_offset = header.resource_offset +
        2U * header.resource_stride;
    std::memcpy(&resource, next.data() + resource_offset, sizeof(resource));
    resource.generation = 1U;
    write_scene_record(next, resource_offset, resource);
    std::uint32_t error_offset = 0U;
    PROGPU_REQUIRE(!progpu::native::scene::generations_do_not_regress(
        previous.data(),
        progpu::native::scene::validate(
            previous.data(), previous.size()).header,
        next.data(),
        header,
        error_offset));
    PROGPU_REQUIRE(error_offset == resource_offset);

    auto immutable_next = previous;
    auto immutable_header = progpu::native::scene::validate(
        immutable_next.data(), immutable_next.size()).header;
    immutable_header.generation = 8U;
    write_scene_record(immutable_next, 0U, immutable_header);
    error_offset = 0U;
    PROGPU_REQUIRE(progpu::native::scene::generations_do_not_regress(
        previous.data(),
        progpu::native::scene::validate(
            previous.data(), previous.size()).header,
        immutable_next.data(),
        immutable_header,
        error_offset));

    const std::size_t immutable_resource_offset =
        immutable_header.resource_offset +
        2U * immutable_header.resource_stride;
    progpu_native_scene_resource immutable_resource{};
    std::memcpy(
        &immutable_resource,
        immutable_next.data() + immutable_resource_offset,
        sizeof(immutable_resource));
    PROGPU_REQUIRE(immutable_resource.payload_size != 0U);
    immutable_next[immutable_resource.payload_offset] ^= std::byte{0x01};
    PROGPU_REQUIRE(!progpu::native::scene::generations_do_not_regress(
        previous.data(),
        progpu::native::scene::validate(
            previous.data(), previous.size()).header,
        immutable_next.data(),
        immutable_header,
        error_offset));
    PROGPU_REQUIRE(error_offset == immutable_resource_offset);
}

void semantic_scene_layer_descriptors_are_exact_and_canonical() {
    progpu_native_scene_layer layer{};
    layer.struct_size = sizeof(layer);
    layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
        PROGPU_NATIVE_SCENE_LAYER_BACKDROP |
        PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
    layer.bounds = {2.0F, 3.0F, 40.0F, 50.0F};
    layer.opacity = 0.5F;
    layer.blend_mode = PROGPU_NATIVE_BLEND_OVERLAY;
    layer.mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    layer.effect_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    layer.content_revision = 7U;
    layer.composite_revision = 9U;

    auto stream = create_layer_descriptor_scene(layer);
    progpu_native_scene_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    PROGPU_REQUIRE(metrics.maximum_stack_depth == 1U);
    PROGPU_REQUIRE(metrics.payload_bytes == sizeof(layer));

    const auto rejects_value = [](progpu_native_scene_layer invalid) {
        const auto invalid_stream = create_layer_descriptor_scene(invalid);
        progpu_native_scene_metrics invalid_metrics{};
        invalid_metrics.struct_size = sizeof(invalid_metrics);
        PROGPU_REQUIRE(progpu_native_scene_validate(
            invalid_stream.data(),
            invalid_stream.size(),
            &invalid_metrics) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        PROGPU_REQUIRE(invalid_metrics.validation_error ==
            PROGPU_NATIVE_SCENE_VALIDATION_VALUE);
    };

    auto invalid = layer;
    --invalid.struct_size;
    rejects_value(invalid);
    invalid = layer;
    invalid.flags |= 1U << 31U;
    rejects_value(invalid);
    invalid = layer;
    invalid.flags = 0U;
    rejects_value(invalid);
    invalid = layer;
    invalid.bounds.width = -1.0F;
    rejects_value(invalid);
    invalid = layer;
    invalid.opacity = std::numeric_limits<float>::quiet_NaN();
    rejects_value(invalid);
    invalid = layer;
    invalid.opacity = 1.01F;
    rejects_value(invalid);
    invalid = layer;
    invalid.blend_mode = PROGPU_NATIVE_BLEND_MODULATE + 1U;
    rejects_value(invalid);
    invalid = layer;
    invalid.mask_resource_index = 0U;
    auto invalid_stream = create_layer_descriptor_scene(invalid);
    progpu_native_scene_metrics invalid_metrics{};
    invalid_metrics.struct_size = sizeof(invalid_metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        invalid_stream.data(),
        invalid_stream.size(),
        &invalid_metrics) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(invalid_metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_RECORD);
    invalid = layer;
    invalid.effect_resource_index = 0U;
    invalid_stream = create_layer_descriptor_scene(invalid);
    invalid_metrics = {};
    invalid_metrics.struct_size = sizeof(invalid_metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        invalid_stream.data(),
        invalid_stream.size(),
        &invalid_metrics) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(invalid_metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_RECORD);
    invalid = layer;
    invalid.reserved0 = 1U;
    rejects_value(invalid);

    auto cached = layer;
    cached.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
        PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT;
    cached.opacity = 1.0F;
    cached.blend_mode = PROGPU_NATIVE_BLEND_SRC_OVER;
    stream = create_layer_descriptor_scene(cached);
    metrics = {};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    invalid = cached;
    invalid.content_revision = 0U;
    rejects_value(invalid);
    invalid = cached;
    invalid.composite_revision = 0U;
    rejects_value(invalid);
    invalid = cached;
    invalid.flags |= PROGPU_NATIVE_SCENE_LAYER_BACKDROP;
    rejects_value(invalid);

    progpu_native_scene_state composite_state{};
    composite_state.struct_size = sizeof(composite_state);
    composite_state.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    composite_state.opacity = 1.0F;
    composite_state.transform.m31 = 12.0F;
    composite_state.transform.m32 = 8.0F;
    auto local_cached = cached;
    local_cached.flags |=
        PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE;
    local_cached.bounds = {0.0F, 0.0F, 20.0F, 15.0F};
    local_cached.reserved0 = 0U;
    stream = create_local_cache_layer_scene(
        local_cached, composite_state);
    metrics = {};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    auto nearest_local_cached = local_cached;
    nearest_local_cached.flags |=
        PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST;
    stream = create_local_cache_layer_scene(
        nearest_local_cached, composite_state);
    metrics = {};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    auto invalid_nearest = cached;
    invalid_nearest.flags |= PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST;
    rejects_value(invalid_nearest);

    composite_state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
    composite_state.clip_rect = {16.0F, 10.0F, 8.0F, 6.0F};
    stream = create_local_cache_layer_scene(
        local_cached, composite_state);
    metrics = {};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS);

    auto invalid_local = local_cached;
    invalid_local.bounds.x = 1.0F;
    rejects_value(invalid_local);
    invalid_local = local_cached;
    invalid_local.blend_mode = PROGPU_NATIVE_BLEND_OVERLAY;
    rejects_value(invalid_local);
    invalid_local = local_cached;
    invalid_local.reserved0 = PROGPU_NATIVE_SCENE_NO_INDEX;
    auto invalid_local_stream = create_layer_descriptor_scene(invalid_local);
    progpu_native_scene_metrics invalid_local_metrics{};
    invalid_local_metrics.struct_size = sizeof(invalid_local_metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        invalid_local_stream.data(),
        invalid_local_stream.size(),
        &invalid_local_metrics) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(invalid_local_metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_RECORD);

    stream = create_layer_descriptor_scene(layer);
    progpu_native_scene_header header{};
    std::memcpy(&header, stream.data(), sizeof(header));
    progpu_native_scene_command push{};
    std::memcpy(
        &push,
        stream.data() + header.command_offset,
        sizeof(push));
    --push.payload_size;
    write_scene_record(stream, header.command_offset, push);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_VALUE);
}

void semantic_scene_layer_resources_are_typed_and_canonical() {
    auto stream = create_typed_layer_resource_scene();
    progpu_native_scene_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        stream.data(), stream.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    PROGPU_REQUIRE(metrics.resource_count == 2U);
    PROGPU_REQUIRE(metrics.maximum_stack_depth == 1U);
    PROGPU_REQUIRE(metrics.payload_bytes ==
        sizeof(progpu_native_scene_layer_mask) +
        sizeof(progpu_native_scene_effect_chain) +
        sizeof(progpu_native_group_effect) +
        sizeof(progpu_native_scene_layer));

    progpu_native_scene_header header{};
    std::memcpy(&header, stream.data(), sizeof(header));
    progpu_native_scene_resource mask_resource{};
    std::memcpy(
        &mask_resource,
        stream.data() + header.resource_offset,
        sizeof(mask_resource));
    progpu_native_scene_resource effect_resource{};
    std::memcpy(
        &effect_resource,
        stream.data() + header.resource_offset + header.resource_stride,
        sizeof(effect_resource));
    progpu_native_scene_command push{};
    std::memcpy(
        &push,
        stream.data() + header.command_offset,
        sizeof(push));

    const auto rejects = [](
        const std::vector<std::byte>& invalid_stream,
        std::uint32_t expected_error) {
        progpu_native_scene_metrics invalid_metrics{};
        invalid_metrics.struct_size = sizeof(invalid_metrics);
        PROGPU_REQUIRE(progpu_native_scene_validate(
            invalid_stream.data(),
            invalid_stream.size(),
            &invalid_metrics) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
        PROGPU_REQUIRE(invalid_metrics.validation_error == expected_error);
    };

    auto invalid_stream = stream;
    progpu_native_scene_layer_mask mask{};
    std::memcpy(
        &mask,
        invalid_stream.data() + mask_resource.payload_offset,
        sizeof(mask));
    mask.flags = 1U;
    write_scene_record(invalid_stream, mask_resource.payload_offset, mask);
    rejects(invalid_stream, PROGPU_NATIVE_SCENE_VALIDATION_VALUE);

    invalid_stream = stream;
    std::memcpy(
        &mask,
        invalid_stream.data() + mask_resource.payload_offset,
        sizeof(mask));
    mask.transform.m22 = 0.0F;
    write_scene_record(invalid_stream, mask_resource.payload_offset, mask);
    rejects(invalid_stream, PROGPU_NATIVE_SCENE_VALIDATION_VALUE);

    invalid_stream = stream;
    std::memcpy(
        &mask,
        invalid_stream.data() + mask_resource.payload_offset,
        sizeof(mask));
    mask.transform.m11 = 1.0e-39F;
    mask.transform.m22 = 3.0e34F;
    write_scene_record(invalid_stream, mask_resource.payload_offset, mask);
    rejects(invalid_stream, PROGPU_NATIVE_SCENE_VALIDATION_VALUE);

    invalid_stream = stream;
    std::memcpy(
        &mask,
        invalid_stream.data() + mask_resource.payload_offset,
        sizeof(mask));
    mask.opacity = 1.01F;
    write_scene_record(invalid_stream, mask_resource.payload_offset, mask);
    rejects(invalid_stream, PROGPU_NATIVE_SCENE_VALIDATION_VALUE);

    invalid_stream = stream;
    --effect_resource.auxiliary_size;
    write_scene_record(
        invalid_stream,
        header.resource_offset + header.resource_stride,
        effect_resource);
    rejects(invalid_stream, PROGPU_NATIVE_SCENE_VALIDATION_VALUE);

    invalid_stream = stream;
    progpu_native_group_effect effect{};
    std::memcpy(
        &effect,
        invalid_stream.data() + effect_resource.auxiliary_offset,
        sizeof(effect));
    effect.offset_x = 1.0F;
    write_scene_record(
        invalid_stream,
        effect_resource.auxiliary_offset,
        effect);
    rejects(invalid_stream, PROGPU_NATIVE_SCENE_VALIDATION_VALUE);

    invalid_stream = stream;
    progpu_native_scene_layer layer{};
    std::memcpy(
        &layer,
        invalid_stream.data() + push.payload_offset,
        sizeof(layer));
    layer.mask_resource_index = 1U;
    layer.effect_resource_index = 0U;
    write_scene_record(invalid_stream, push.payload_offset, layer);
    rejects(invalid_stream, PROGPU_NATIVE_SCENE_VALIDATION_RECORD);
}

void semantic_scene_stack_depth_is_bounded_exactly() {
    const auto maximum = create_nested_scene(
        PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH);
    progpu_native_scene_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        maximum.data(), maximum.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    PROGPU_REQUIRE(metrics.maximum_stack_depth ==
        PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH);

    const auto excessive = create_nested_scene(
        PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH + 1U);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        excessive.data(), excessive.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_STACK);

    const auto maximum_layers = create_nested_scene(
        PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS,
        true);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        maximum_layers.data(), maximum_layers.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_SUCCESS);

    const auto excessive_layers = create_nested_scene(
        PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS + 1U,
        true);
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(progpu_native_scene_validate(
        excessive_layers.data(), excessive_layers.size(), &metrics) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    PROGPU_REQUIRE(metrics.validation_error ==
        PROGPU_NATIVE_SCENE_VALIDATION_STACK);
}

void semantic_scene_validation_is_deterministic_under_mutation() {
    auto stream = create_valid_mixed_scene();
    std::uint32_t random = 0x9e3779b9U;
    for (std::uint32_t iteration = 0U; iteration < 5000U; ++iteration) {
        random = random * 1664525U + 1013904223U;
        const std::size_t offset = random % stream.size();
        const std::byte original = stream[offset];
        const std::byte mutation = static_cast<std::byte>(
            1U << ((random >> 24U) & 7U));
        stream[offset] ^= mutation;
        const auto first = progpu::native::scene::validate(
            stream.data(), stream.size());
        const auto second = progpu::native::scene::validate(
            stream.data(), stream.size());
        PROGPU_REQUIRE(first.status == second.status);
        PROGPU_REQUIRE(first.error == second.error);
        PROGPU_REQUIRE(first.error_offset == second.error_offset);
        stream[offset] = original;
    }
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

void geometry_batch_encodes_gpu_and_affine_bezier_strokes() {
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    progpu_native_geometry_primitive quadratic{
        PROGPU_NATIVE_GEOMETRY_QUADRATIC_BEZIER,
        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE,
        {1.0F, 2.0F},
        {3.0F, 8.0F},
        {9.0F, 4.0F},
        {},
        2.5F,
        0.0F,
        {0.1F, 0.2F, 0.3F, 0.8F},
        {2.0F, 0.0F, 0.0F, 2.0F, 5.0F, 7.0F}
    };
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        quadratic,
        3.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 50U && indices.size() == 144U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[0], 7.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[1], 11.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[0], 11.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[1], 23.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[0], 23.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[1], 15.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].stroke_thickness, -3.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 5.0F));
    PROGPU_REQUIRE(indices[0] == 0U && indices[143] == 48U);

    vertices.clear();
    indices.clear();
    progpu_native_geometry_primitive cubic{
        PROGPU_NATIVE_GEOMETRY_CUBIC_BEZIER,
        0U,
        {0.0F, 0.0F},
        {10.0F, 30.0F},
        {20.0F, -20.0F},
        {40.0F, 0.0F},
        4.0F,
        0.0F,
        {0.8F, 0.4F, 0.2F, 1.0F},
        {2.0F, 0.25F, 0.5F, 1.0F, 3.0F, 5.0F}
    };
    std::size_t vertex_capacity = 0U;
    std::size_t index_capacity = 0U;
    PROGPU_REQUIRE(progpu::native::geometry_primitive_capacity(
        cubic,
        vertex_capacity,
        index_capacity));
    PROGPU_REQUIRE(vertex_capacity >= 96U && vertex_capacity <= 4096U);
    PROGPU_REQUIRE(index_capacity * 2U == vertex_capacity * 3U);
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        cubic,
        4.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(!vertices.empty() && !indices.empty());
    PROGPU_REQUIRE(vertices.size() <= vertex_capacity);
    PROGPU_REQUIRE(indices.size() <= index_capacity);
    PROGPU_REQUIRE(nearly_equal(vertices[0].brush_index, 4.0F));
    PROGPU_REQUIRE(vertices[0].shape_type == 16.0F);
    PROGPU_REQUIRE(vertices[vertices.size() - 1U].shape_type == 17.0F);
}

void geometry_batch_encodes_exact_path_arcs_caps_and_joins() {
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    const progpu_native_geometry_primitive arc{
        PROGPU_NATIVE_GEOMETRY_ARC,
        0U,
        {20.0F, 18.0F},
        {11.27214F, 4.11477F},
        {-2.40028F, 6.57661F},
        {0.25F, std::numbers::pi_v<float>},
        3.0F,
        0.0F,
        {1.0F, 1.0F, 1.0F, 1.0F},
        {2.0F, 0.0F, 0.0F, 2.0F, 5.0F, 7.0F}
    };
    std::size_t vertex_capacity = 0U;
    std::size_t index_capacity = 0U;
    PROGPU_REQUIRE(progpu::native::geometry_primitive_capacity(
        arc,
        vertex_capacity,
        index_capacity));
    PROGPU_REQUIRE(vertex_capacity == 4U && index_capacity == 6U);
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        arc,
        2.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 4U && indices.size() == 6U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 12.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[0], 45.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[1], 43.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].stroke_thickness, 6.0F));

    vertices.clear();
    indices.clear();
    const progpu_native_geometry_primitive cap{
        PROGPU_NATIVE_GEOMETRY_PATH_CAP,
        PROGPU_NATIVE_STROKE_CAP_ROUND <<
            PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT,
        {10.0F, 12.0F},
        {1.0F, 0.0F},
        {1.0F, 0.0F},
        {},
        4.0F,
        0.0F,
        {1.0F, 1.0F, 1.0F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}
    };
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        cap,
        3.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(!vertices.empty() && !indices.empty());

    vertices.clear();
    indices.clear();
    const progpu_native_geometry_primitive join{
        PROGPU_NATIVE_GEOMETRY_PATH_JOIN,
        PROGPU_NATIVE_STROKE_JOIN_ROUND <<
            PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT,
        {10.0F, 12.0F},
        {1.0F, 0.0F},
        {0.0F, 1.0F},
        {4.0F, 0.0F},
        4.0F,
        0.0F,
        {1.0F, 1.0F, 1.0F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}
    };
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        join,
        4.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(!vertices.empty() && !indices.empty());
}

void geometry_batch_encodes_periodic_dot_grid_as_one_quad() {
    const progpu_native_geometry_primitive grid{
        PROGPU_NATIVE_GEOMETRY_DOT_GRID,
        PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED,
        {4.0F, 6.0F},
        {40.0F, 30.0F},
        {2.0F, 3.0F},
        {8.0F, 1.5F},
        0.0F,
        0.0F,
        {0.8F, 0.9F, 0.2F, 1.0F},
        {2.0F, 0.0F, 0.0F, 3.0F, 5.0F, 7.0F}
    };
    std::size_t vertex_capacity = 0U;
    std::size_t index_capacity = 0U;
    PROGPU_REQUIRE(progpu::native::geometry_primitive_capacity(
        grid,
        vertex_capacity,
        index_capacity));
    PROGPU_REQUIRE(vertex_capacity == 4U && index_capacity == 6U);

    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        grid,
        3.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 4U && indices.size() == 6U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[0], 13.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[1], 25.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[2].position[0], 93.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[2].position[1], 115.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[0], 4.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[1], 6.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[0], 8.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[1], 1.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].corner_radius, 2.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].stroke_thickness, 3.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 1021.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].brush_index, 3.0F));
    PROGPU_REQUIRE(indices[0] == 0U && indices[5] == 3U);

    auto invalid = grid;
    invalid.p3.x = 0.0F;
    vertices.clear();
    indices.clear();
    PROGPU_REQUIRE(!progpu::native::append_geometry_primitive(
        invalid,
        3.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.empty() && indices.empty());
}

void semantic_point_batch_compiles_compact_retained_points() {
    const std::array points{
        progpu_native_point{10.0F, 20.0F},
        progpu_native_point{30.0F, 40.0F}
    };
    const progpu_native_scene_point_batch batch{
        sizeof(progpu_native_scene_point_batch),
        PROGPU_NATIVE_POINT_BATCH_ROUND,
        0U,
        static_cast<std::uint32_t>(points.size()),
        2.0F,
        0.0F,
        {0.2F, 0.4F, 0.6F, 0.8F},
        {2.0F, 0.0F, 0.0F, 3.0F, 5.0F, 7.0F}
    };
    std::size_t vertex_capacity = 0U;
    std::size_t index_capacity = 0U;
    PROGPU_REQUIRE(progpu::native::point_batch_capacity(
        batch,
        points.size(),
        vertex_capacity,
        index_capacity));
    PROGPU_REQUIRE(vertex_capacity == 8U && index_capacity == 12U);

    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    PROGPU_REQUIRE(progpu::native::append_point_batch(
        batch,
        points.data(),
        points.size(),
        3.0F,
        true,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 8U && indices.size() == 12U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[0], 18.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[1], 56.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[0], -3.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[1], -3.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[0], 10.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[1], 20.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[0], 4.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 1.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].brush_index, 3.0F));
    PROGPU_REQUIRE(indices[6] == 4U && indices[11] == 7U);

    auto hairline = batch;
    hairline.flags = PROGPU_NATIVE_POINT_BATCH_EDGE_ALIASED |
        PROGPU_NATIVE_POINT_BATCH_ROUND |
        PROGPU_NATIVE_POINT_BATCH_HAIRLINE;
    hairline.point_count = 1U;
    hairline.radius = 0.5F;
    vertices.clear();
    indices.clear();
    PROGPU_REQUIRE(progpu::native::append_point_batch(
        hairline,
        points.data(),
        points.size(),
        1.0F,
        false,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 4U && indices.size() == 6U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[0], 25.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[1], 67.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[3].position[0], 25.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[3].position[1], 67.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[0], -0.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 1020.0F));

    auto fixed = batch;
    fixed.flags = PROGPU_NATIVE_POINT_BATCH_ROUND |
        PROGPU_NATIVE_POINT_BATCH_FIXED_DEVICE_RADIUS;
    fixed.point_count = 1U;
    vertices.clear();
    indices.clear();
    PROGPU_REQUIRE(progpu::native::append_point_batch(
        fixed,
        points.data(),
        points.size(),
        1.0F,
        false,
        vertices,
        indices));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[0], 21.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[1], 63.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[2].position[0], 28.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[2].position[1], 70.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[0], -3.5F));

    fixed.flags |= PROGPU_NATIVE_POINT_BATCH_HAIRLINE;
    fixed.radius = 0.5F;
    PROGPU_REQUIRE(!progpu::native::point_batch_capacity(
        fixed,
        points.size(),
        vertex_capacity,
        index_capacity));

    auto invalid_points = points;
    invalid_points[1].x = std::numeric_limits<float>::infinity();
    vertices.clear();
    indices.clear();
    PROGPU_REQUIRE(!progpu::native::append_point_batch(
        batch,
        invalid_points.data(),
        invalid_points.size(),
        0.0F,
        false,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.empty() && indices.empty());
}

void semantic_stroke_batch_preserves_retained_stroke_contracts() {
    const std::array points{
        progpu_native_point{0.0F, 0.0F},
        progpu_native_point{10.0F, 0.0F},
        progpu_native_point{10.0F, 10.0F},
        progpu_native_point{10.0F, 0.0F},
        progpu_native_point{10.0F, 10.0F},
        progpu_native_point{0.0F, 10.0F}
    };
    const std::array doubles{
        2.0, 2.0,
        0.0, 0.0, 0.0, 1.0, 1.0, 1.0,
        1.0, 0.7071067811865476, 1.0
    };
    std::array<progpu_native_scene_stroke, 2U> strokes{};
    auto& polyline = strokes[0];
    polyline.struct_size = sizeof(polyline);
    polyline.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
    polyline.flags = PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE;
    polyline.point_count = 3U;
    polyline.dash_interval_count = 2U;
    polyline.color = {1.0F, 0.25F, 0.5F, 1.0F};
    polyline.transform = {1.0F, 0.0F, 0.0F, 1.0F, 2.0F, 3.0F};
    polyline.stroke_thickness = 4.0F;
    polyline.miter_limit = 10.0F;
    polyline.start_cap = PROGPU_NATIVE_STROKE_CAP_ROUND;
    polyline.end_cap = PROGPU_NATIVE_STROKE_CAP_TRIANGLE;
    polyline.line_join = PROGPU_NATIVE_STROKE_JOIN_ROUND;
    polyline.dash_cap = PROGPU_NATIVE_STROKE_CAP_SQUARE;

    auto& spline = strokes[1];
    spline.struct_size = sizeof(spline);
    spline.kind = PROGPU_NATIVE_SCENE_STROKE_SPLINE;
    spline.flags = PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE;
    spline.degree = 2U;
    spline.point_offset = 3U;
    spline.point_count = 3U;
    spline.knot_offset = 2U;
    spline.knot_count = 6U;
    spline.weight_offset = 8U;
    spline.weight_count = 3U;
    spline.dash_interval_offset = 11U;
    spline.color = {0.25F, 1.0F, 0.5F, 1.0F};
    spline.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    spline.stroke_thickness = 0.0F;
    spline.miter_limit = 10.0F;
    spline.start_cap = PROGPU_NATIVE_STROKE_CAP_FLAT;
    spline.end_cap = PROGPU_NATIVE_STROKE_CAP_FLAT;
    spline.line_join = PROGPU_NATIVE_STROKE_JOIN_MITER;
    spline.dash_cap = PROGPU_NATIVE_STROKE_CAP_FLAT;

    std::size_t point_count = 0U;
    std::size_t double_count = 0U;
    PROGPU_REQUIRE(progpu::native::semantic_stroke_resource_layout(
        strokes.data(),
        strokes.size(),
        sizeof(points) + sizeof(doubles),
        point_count,
        double_count));
    PROGPU_REQUIRE(point_count == points.size());
    PROGPU_REQUIRE(double_count == doubles.size());

    std::array<progpu_native_point, 101U> sampled_points{};
    std::vector<progpu::native::spline_homogeneous_point> work;
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    PROGPU_REQUIRE(progpu::native::append_semantic_stroke(
        polyline,
        points.data(),
        doubles.data(),
        doubles.size(),
        7.0F,
        sampled_points,
        work,
        vertices,
        indices));
    PROGPU_REQUIRE(!vertices.empty() && !indices.empty());
    PROGPU_REQUIRE(nearly_equal(vertices.front().brush_index, 7.0F));
    const auto polyline_vertex_count = vertices.size();
    PROGPU_REQUIRE(progpu::native::append_semantic_stroke(
        spline,
        points.data() + spline.point_offset,
        doubles.data(),
        doubles.size(),
        8.0F,
        sampled_points,
        work,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() > polyline_vertex_count);
    PROGPU_REQUIRE(nearly_equal(vertices.back().brush_index, 8.0F));

    auto collapsed = polyline;
    collapsed.transform = {0.0F, 0.0F, 0.0F, 1.0F, 454.0F, 130.5F};
    std::size_t collapsed_vertex_count = 1U;
    std::size_t collapsed_index_count = 1U;
    PROGPU_REQUIRE(progpu::native::semantic_stroke_capacity(
        collapsed,
        points.data(),
        doubles.data(),
        doubles.size(),
        sampled_points,
        work,
        collapsed_vertex_count,
        collapsed_index_count));
    PROGPU_REQUIRE(collapsed_vertex_count == 0U);
    PROGPU_REQUIRE(collapsed_index_count == 0U);
    const auto retained_vertex_count = vertices.size();
    const auto retained_index_count = indices.size();
    PROGPU_REQUIRE(progpu::native::append_semantic_stroke(
        collapsed,
        points.data(),
        doubles.data(),
        doubles.size(),
        9.0F,
        sampled_points,
        work,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == retained_vertex_count);
    PROGPU_REQUIRE(indices.size() == retained_index_count);

    auto invalid_layout = strokes;
    invalid_layout[1].point_offset = 4U;
    point_count = 17U;
    double_count = 19U;
    PROGPU_REQUIRE(!progpu::native::semantic_stroke_resource_layout(
        invalid_layout.data(),
        invalid_layout.size(),
        sizeof(points) + sizeof(doubles),
        point_count,
        double_count));
    PROGPU_REQUIRE(point_count == 0U && double_count == 0U);

    auto overflowing_layout = strokes;
    overflowing_layout[0].point_count =
        std::numeric_limits<std::uint64_t>::max();
    overflowing_layout[1].point_offset =
        std::numeric_limits<std::uint64_t>::max();
    PROGPU_REQUIRE(!progpu::native::semantic_stroke_resource_layout(
        overflowing_layout.data(),
        overflowing_layout.size(),
        sizeof(points) + sizeof(doubles),
        point_count,
        double_count));

    auto invalid_doubles = doubles;
    invalid_doubles[0] = -1.0;
    vertices.clear();
    indices.clear();
    PROGPU_REQUIRE(!progpu::native::append_semantic_stroke(
        polyline,
        points.data(),
        invalid_doubles.data(),
        invalid_doubles.size(),
        0.0F,
        sampled_points,
        work,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.empty() && indices.empty());
}

void semantic_vertex_mesh_preserves_topology_color_and_coordinates() {
    const std::array source_vertices{
        progpu_native_scene_mesh_vertex{
            {0.0F, 0.0F}, {0.1F, 0.2F}, {1.0F, 0.5F, 0.25F, 0.5F}},
        progpu_native_scene_mesh_vertex{
            {10.0F, 0.0F}, {0.9F, 0.2F}, {0.2F, 1.0F, 0.4F, 1.0F}},
        progpu_native_scene_mesh_vertex{
            {0.0F, 10.0F}, {0.1F, 0.8F}, {0.4F, 0.2F, 1.0F, 0.75F}},
        progpu_native_scene_mesh_vertex{
            {10.0F, 10.0F}, {0.9F, 0.8F}, {1.0F, 1.0F, 1.0F, 1.0F}}
    };
    const progpu_native_scene_vertex_mesh mesh{
        sizeof(progpu_native_scene_vertex_mesh),
        PROGPU_NATIVE_VERTEX_MESH_EDGE_ALIASED,
        PROGPU_NATIVE_VERTEX_MESH_TRIANGLE_STRIP,
        21U,
        0U,
        static_cast<std::uint32_t>(source_vertices.size()),
        0U,
        0U,
        {2.0F, 0.0F, 0.0F, 3.0F, 5.0F, 7.0F},
        {0U, 0U}
    };
    std::size_t resource_vertex_count = 0U;
    std::size_t resource_index_count = 0U;
    PROGPU_REQUIRE(progpu::native::vertex_mesh_resource_layout(
        &mesh,
        1U,
        sizeof(source_vertices),
        resource_vertex_count,
        resource_index_count));
    PROGPU_REQUIRE(resource_vertex_count == 4U);
    PROGPU_REQUIRE(resource_index_count == 0U);

    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    PROGPU_REQUIRE(progpu::native::append_vertex_mesh(
        mesh,
        source_vertices.data(),
        source_vertices.size(),
        nullptr,
        0U,
        0.65F,
        3.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 4U && indices.size() == 6U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[0], 5.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].position[1], 7.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[0], 0.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[1], 0.25F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[2], 0.125F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[3], 0.5F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].texture_coordinate[0], 0.1F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].corner_radius, 21.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].stroke_thickness, 0.65F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 1018.0F));
    PROGPU_REQUIRE(indices[0] == 0U && indices[1] == 1U &&
        indices[2] == 2U && indices[3] == 2U &&
        indices[4] == 1U && indices[5] == 3U);

    const std::array<std::uint16_t, 4U> fan_indices{0U, 1U, 8U, 3U};
    auto indexed = mesh;
    indexed.topology = PROGPU_NATIVE_VERTEX_MESH_TRIANGLE_FAN;
    indexed.flags = 0U;
    indexed.index_count = static_cast<std::uint32_t>(fan_indices.size());
    vertices.clear();
    indices.clear();
    PROGPU_REQUIRE(progpu::native::append_vertex_mesh(
        indexed,
        source_vertices.data(),
        source_vertices.size(),
        fan_indices.data(),
        fan_indices.size(),
        1.0F,
        0.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 4U);
    PROGPU_REQUIRE(indices.empty());

    auto invalid_vertices = source_vertices;
    invalid_vertices[3].texture_coordinate.x =
        std::numeric_limits<float>::quiet_NaN();
    vertices.clear();
    PROGPU_REQUIRE(!progpu::native::append_vertex_mesh(
        mesh,
        invalid_vertices.data(),
        invalid_vertices.size(),
        nullptr,
        0U,
        1.0F,
        0.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.empty());
}

void geometry_batch_preserves_cap_order_and_space() {
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    progpu_native_geometry_primitive hairline{
        PROGPU_NATIVE_GEOMETRY_LINE,
        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE |
            (PROGPU_NATIVE_STROKE_CAP_ROUND <<
                PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) |
            (PROGPU_NATIVE_STROKE_CAP_TRIANGLE <<
                PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT),
        {1.0F, 2.0F},
        {5.0F, 6.0F},
        {},
        {},
        0.0F,
        0.0F,
        {0.3F, 0.5F, 0.7F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F}
    };
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        hairline,
        2.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 12U && indices.size() == 18U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 22.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[0], 2.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].color[1], 1.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_size[0], 0.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[4].shape_type, 3.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].shape_type, 22.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].color[0], 3.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].color[1], 0.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].shape_size[0], 8.0F));

    vertices.clear();
    indices.clear();
    hairline.flags =
        (PROGPU_NATIVE_STROKE_CAP_ROUND <<
            PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT) |
        (PROGPU_NATIVE_STROKE_CAP_ROUND <<
            PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT);
    hairline.stroke_thickness = 4.0F;
    hairline.transform = {2.0F, 0.25F, 0.5F, 1.0F, 3.0F, 5.0F};
    PROGPU_REQUIRE(progpu::native::append_geometry_primitive(
        hairline,
        3.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 12U && indices.size() == 18U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 24.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[4].shape_type, 14.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].shape_type, 24.0F));
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

void connected_strokes_encode_caps_joins_and_closed_contours() {
    const progpu_native_point points[] = {
        {2.0F, 3.0F},
        {12.0F, 3.0F},
        {12.0F, 13.0F},
        {22.0F, 13.0F}
    };
    progpu_native_polyline open{
        0U,
        4U,
        {0.2F, 0.4F, 0.8F, 1.0F},
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
        0.0F,
        6.0F,
        PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE |
            (PROGPU_NATIVE_STROKE_CAP_ROUND <<
                PROGPU_NATIVE_POLYLINE_START_CAP_SHIFT) |
            (PROGPU_NATIVE_STROKE_CAP_TRIANGLE <<
                PROGPU_NATIVE_POLYLINE_END_CAP_SHIFT) |
            (PROGPU_NATIVE_STROKE_JOIN_ROUND <<
                PROGPU_NATIVE_POLYLINE_JOIN_SHIFT),
        0U
    };
    std::size_t vertex_capacity = 0U;
    std::size_t index_capacity = 0U;
    PROGPU_REQUIRE(progpu::native::polyline_capacity(
        open,
        vertex_capacity,
        index_capacity));
    PROGPU_REQUIRE(vertex_capacity == 140U);
    PROGPU_REQUIRE(index_capacity == 210U);

    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    PROGPU_REQUIRE(progpu::native::append_polyline(
        open,
        points,
        5.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 28U);
    PROGPU_REQUIRE(indices.size() == 42U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 22.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[4].shape_type, 3.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[8].shape_type, 23.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[12].shape_type, 3.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[16].shape_type, 23.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[20].shape_type, 3.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[24].shape_type, 22.0F));

    vertices.clear();
    indices.clear();
    open.point_count = 3U;
    open.stroke_thickness = 4.0F;
    open.flags = PROGPU_NATIVE_POLYLINE_FLAG_CLOSED |
        (PROGPU_NATIVE_STROKE_JOIN_BEVEL <<
            PROGPU_NATIVE_POLYLINE_JOIN_SHIFT);
    open.transform = {2.0F, 0.25F, 0.5F, 1.0F, 3.0F, 5.0F};
    PROGPU_REQUIRE(progpu::native::append_polyline(
        open,
        points,
        6.0F,
        vertices,
        indices));
    PROGPU_REQUIRE(vertices.size() == 24U);
    PROGPU_REQUIRE(indices.size() == 36U);
    PROGPU_REQUIRE(nearly_equal(vertices[0].shape_type, 14.0F));
    PROGPU_REQUIRE(nearly_equal(vertices[4].shape_type, 13.0F));
}

void dashed_strokes_preserve_pattern_space_caps_and_closed_seams() {
    const double intervals[] = {2.0, 2.0};
    const progpu_native_dash_style flat_style{
        0U,
        2U,
        0.0,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        0U
    };
    const progpu_native_point line[] = {
        {0.0F, 0.0F},
        {10.0F, 0.0F}
    };
    progpu_native_polyline stroke{
        0U,
        2U,
        {0.2F, 0.7F, 0.4F, 1.0F},
        {2.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
        1.0F,
        4.0F,
        PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE,
        1U
    };
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    PROGPU_REQUIRE(progpu::native::append_polyline(
        stroke,
        line,
        2.0F,
        vertices,
        indices,
        &flat_style,
        1U,
        intervals,
        2U));
    PROGPU_REQUIRE(vertices.size() == 20U);
    PROGPU_REQUIRE(indices.size() == 30U);

    vertices.clear();
    indices.clear();
    stroke.flags = 0U;
    PROGPU_REQUIRE(progpu::native::append_polyline(
        stroke,
        line,
        2.0F,
        vertices,
        indices,
        &flat_style,
        1U,
        intervals,
        2U));
    PROGPU_REQUIRE(vertices.size() == 12U);
    PROGPU_REQUIRE(indices.size() == 18U);

    const progpu_native_dash_style round_style{
        0U,
        2U,
        0.0,
        PROGPU_NATIVE_STROKE_CAP_ROUND,
        0U
    };
    vertices.clear();
    indices.clear();
    stroke.flags = PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE;
    stroke.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    PROGPU_REQUIRE(progpu::native::append_polyline(
        stroke,
        line,
        2.0F,
        vertices,
        indices,
        &round_style,
        1U,
        intervals,
        2U));
    PROGPU_REQUIRE(vertices.size() == 28U);
    PROGPU_REQUIRE(indices.size() == 42U);

    const double closed_intervals[] = {100.0, 1.0};
    const progpu_native_point square[] = {
        {0.0F, 0.0F},
        {5.0F, 0.0F},
        {5.0F, 5.0F},
        {0.0F, 5.0F}
    };
    stroke.point_count = 4U;
    stroke.flags = PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE |
        PROGPU_NATIVE_POLYLINE_FLAG_CLOSED |
        (PROGPU_NATIVE_STROKE_JOIN_BEVEL <<
            PROGPU_NATIVE_POLYLINE_JOIN_SHIFT);
    vertices.clear();
    indices.clear();
    PROGPU_REQUIRE(progpu::native::append_polyline(
        stroke,
        square,
        2.0F,
        vertices,
        indices,
        &round_style,
        1U,
        closed_intervals,
        2U));
    PROGPU_REQUIRE(vertices.size() == 32U);
    PROGPU_REQUIRE(indices.size() == 48U);

    const double odd_intervals[] = {2.0, 1.0, 3.0};
    const progpu_native_dash_style odd_style{
        0U,
        3U,
        -2.0,
        PROGPU_NATIVE_STROKE_CAP_FLAT,
        0U
    };
    progpu::native::dash_pattern_state pattern{};
    stroke.point_count = 2U;
    stroke.flags = PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE;
    PROGPU_REQUIRE(progpu::native::try_create_dash_pattern(
        stroke,
        &odd_style,
        1U,
        odd_intervals,
        3U,
        pattern));
    PROGPU_REQUIRE(pattern.effective_count == 6U);
    PROGPU_REQUIRE(pattern.index < pattern.effective_count);
    PROGPU_REQUIRE(pattern.distance >= 0.0F);
}

void splines_evaluate_adaptively_without_retained_graphs() {
    const progpu_native_point points[] = {
        {0.0F, 0.0F},
        {10.0F, 0.0F},
        {10.0F, 10.0F}
    };
    const double knots[] = {0.0, 0.0, 1.0, 2.0, 2.0};
    progpu_native_spline spline{};
    spline.stroke.point_count = 3U;
    spline.stroke.color = {0.3F, 0.6F, 0.9F, 1.0F};
    spline.stroke.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    spline.stroke.stroke_thickness = 2.0F;
    spline.stroke.miter_limit = 4.0F;
    spline.knot_count = 5U;
    spline.degree = 1U;

    std::size_t segment_count = 0U;
    std::size_t vertex_capacity = 0U;
    std::size_t index_capacity = 0U;
    PROGPU_REQUIRE(progpu::native::spline_capacity(
        spline,
        points,
        knots,
        segment_count,
        vertex_capacity,
        index_capacity));
    PROGPU_REQUIRE(segment_count == 10U);

    std::vector<progpu::native::spline_homogeneous_point> work;
    progpu_native_point evaluated{};
    PROGPU_REQUIRE(progpu::native::try_evaluate_spline_point(
        spline,
        points,
        knots,
        nullptr,
        1.0,
        work,
        evaluated));
    PROGPU_REQUIRE(nearly_equal(evaluated.x, 10.0F));
    PROGPU_REQUIRE(nearly_equal(evaluated.y, 0.0F));

    std::array<progpu_native_point, 101U> sampled{};
    std::vector<progpu::native::vector_vertex> vertices;
    std::vector<std::uint32_t> indices;
    work.reserve(2U);
    PROGPU_REQUIRE(progpu::native::append_spline(
        spline,
        points,
        knots,
        nullptr,
        segment_count,
        8.0F,
        sampled,
        work,
        vertices,
        indices));
    PROGPU_REQUIRE(nearly_equal(sampled.front().x, 0.0F));
    PROGPU_REQUIRE(nearly_equal(sampled.front().y, 0.0F));
    PROGPU_REQUIRE(nearly_equal(sampled[segment_count].x, 10.0F));
    PROGPU_REQUIRE(nearly_equal(sampled[segment_count].y, 10.0F));
    PROGPU_REQUIRE(!vertices.empty() && !indices.empty());

    spline.stroke.transform = {10.0F, 0.0F, 0.0F, 10.0F, 0.0F, 0.0F};
    PROGPU_REQUIRE(progpu::native::try_get_spline_segment_count(
        spline,
        points,
        segment_count));
    PROGPU_REQUIRE(segment_count == 50U);

    const progpu_native_point rational_points[] = {
        {1.0F, 0.0F},
        {1.0F, 1.0F},
        {0.0F, 1.0F}
    };
    const double rational_knots[] = {0.0, 0.0, 0.0, 1.0, 1.0, 1.0};
    const double rational_weights[] = {
        1.0,
        0.7071067811865476,
        1.0
    };
    spline.stroke.point_count = 3U;
    spline.knot_count = 6U;
    spline.weight_count = 3U;
    spline.degree = 2U;
    PROGPU_REQUIRE(progpu::native::try_evaluate_spline_point(
        spline,
        rational_points,
        rational_knots,
        rational_weights,
        0.5,
        work,
        evaluated));
    PROGPU_REQUIRE(std::abs(evaluated.x - 0.70710677F) <= 0.00001F);
    PROGPU_REQUIRE(std::abs(evaluated.y - 0.70710677F) <= 0.00001F);

    spline.knot_count = 2U;
    PROGPU_REQUIRE(progpu::native::spline_capacity(
        spline,
        rational_points,
        rational_knots,
        segment_count,
        vertex_capacity,
        index_capacity));
    PROGPU_REQUIRE(segment_count == 2U);
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

void affine_composition_matches_sequential_row_vector_transforms() {
    const progpu_native_affine_2d first{
        2.0F, 0.25F, -0.5F, 1.5F, 7.0F, 11.0F};
    const progpu_native_affine_2d second{
        0.75F, -0.2F, 0.3F, 1.25F, -4.0F, 5.0F};
    const auto composed = progpu::native::compose_affine(first, second);
    float intermediate_x = 0.0F;
    float intermediate_y = 0.0F;
    float expected_x = 0.0F;
    float expected_y = 0.0F;
    float actual_x = 0.0F;
    float actual_y = 0.0F;
    progpu::native::transform_point(
        first, 3.5F, -2.0F, intermediate_x, intermediate_y);
    progpu::native::transform_point(
        second, intermediate_x, intermediate_y, expected_x, expected_y);
    progpu::native::transform_point(
        composed, 3.5F, -2.0F, actual_x, actual_y);
    PROGPU_REQUIRE(nearly_equal(actual_x, expected_x));
    PROGPU_REQUIRE(nearly_equal(actual_y, expected_y));

    progpu::native::transform_vector(
        first, 3.5F, -2.0F, intermediate_x, intermediate_y);
    progpu::native::transform_vector(
        second, intermediate_x, intermediate_y, expected_x, expected_y);
    progpu::native::transform_vector(
        composed, 3.5F, -2.0F, actual_x, actual_y);
    PROGPU_REQUIRE(nearly_equal(actual_x, expected_x));
    PROGPU_REQUIRE(nearly_equal(actual_y, expected_y));
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
    semantic_scene_stream_validates_mixed_order_and_stack();
    semantic_scene_static_guideline_resource_validates();
    semantic_scene_stream_rejects_malformed_updates_transactionally();
    semantic_scene_resource_generations_are_monotonic();
    semantic_scene_layer_descriptors_are_exact_and_canonical();
    semantic_scene_layer_resources_are_typed_and_canonical();
    semantic_scene_stack_depth_is_bounded_exactly();
    semantic_scene_validation_is_deterministic_under_mutation();
    fixed_stroke_topology_masks_match_reference_classification();
    rectangle_batch_matches_vector_vertex_abi();
    indexed_analytic_batch_preserves_affine_local_coordinates();
    singular_analytic_transform_fails_closed();
    affine_composition_matches_sequential_row_vector_transforms();
    geometry_batch_encodes_direct_and_affine_lines();
    geometry_batch_encodes_device_strokes_and_fills();
    geometry_batch_encodes_gpu_and_affine_bezier_strokes();
    geometry_batch_encodes_exact_path_arcs_caps_and_joins();
    geometry_batch_encodes_periodic_dot_grid_as_one_quad();
    semantic_point_batch_compiles_compact_retained_points();
    semantic_vertex_mesh_preserves_topology_color_and_coordinates();
    semantic_stroke_batch_preserves_retained_stroke_contracts();
    geometry_batch_preserves_cap_order_and_space();
    connected_strokes_encode_caps_joins_and_closed_contours();
    dashed_strokes_preserve_pattern_space_caps_and_closed_seams();
    splines_evaluate_adaptively_without_retained_graphs();
    invalid_geometry_flags_fail_without_partial_append();
    invalid_rectangles_fail_without_partial_append();
    std::cout << "ProGPU native CPU/ABI tests passed.\n";
    return 0;
}

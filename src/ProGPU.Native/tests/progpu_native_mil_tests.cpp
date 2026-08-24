#include "progpu_native_mil.hpp"
#include "progpu_native_mil.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <vector>

namespace {

using progpu::native::mil::batch_metrics;
using progpu::native::mil::channel;
using progpu::native::mil::command;
using progpu::native::mil::status;

void require(bool condition, const char* expression, int line) {
    if (condition) {
        return;
    }
    std::cerr << "line " << line << ": requirement failed: "
              << expression << '\n';
    std::abort();
}

#define PROGPU_REQUIRE(condition) require((condition), #condition, __LINE__)

template<typename T>
void append_value(std::vector<std::byte>& bytes, const T& value) {
    const auto previous = bytes.size();
    bytes.resize(previous + sizeof(T));
    std::memcpy(bytes.data() + previous, &value, sizeof(T));
}

template<typename T>
T read_value(const std::vector<std::byte>& bytes, std::size_t offset) {
    T value{};
    PROGPU_REQUIRE(offset <= bytes.size());
    PROGPU_REQUIRE(sizeof(T) <= bytes.size() - offset);
    std::memcpy(&value, bytes.data() + offset, sizeof(T));
    return value;
}

template<typename... T>
void append_command(
    std::vector<std::byte>& batch,
    command kind,
    const T&... fields) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(kind));
    (append_value(packet, fields), ...);
    const auto item_size = static_cast<std::uint32_t>(
        (packet.size() + sizeof(std::uint32_t) + 3U) & ~std::size_t{3U});
    append_value(batch, item_size);
    batch.insert(batch.end(), packet.begin(), packet.end());
    batch.resize(batch.size() + item_size - sizeof(std::uint32_t) - packet.size());
}

void append_create(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    std::uint32_t type) {
    append_command(batch, command::channel_create_resource, handle, type);
}

void append_render_data(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    const std::vector<std::byte>& render_data) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(command::render_data));
    append_value(packet, handle);
    append_value(packet, static_cast<std::uint32_t>(render_data.size()));
    packet.insert(packet.end(), render_data.begin(), render_data.end());
    const auto item_size = static_cast<std::uint32_t>(
        (packet.size() + sizeof(std::uint32_t) + 3U) & ~std::size_t{3U});
    append_value(batch, item_size);
    batch.insert(batch.end(), packet.begin(), packet.end());
    batch.resize(batch.size() + item_size - sizeof(std::uint32_t) - packet.size());
}

void append_dash_style(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    double offset,
    std::uint32_t offset_animations,
    std::span<const double> intervals) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(command::dash_style));
    append_value(packet, handle);
    append_value(packet, offset);
    append_value(packet, offset_animations);
    append_value(
        packet,
        static_cast<std::uint32_t>(intervals.size_bytes()));
    for (const double interval : intervals) {
        append_value(packet, interval);
    }
    append_value(
        batch,
        static_cast<std::uint32_t>(packet.size() + sizeof(std::uint32_t)));
    batch.insert(batch.end(), packet.begin(), packet.end());
}

bool channel_retains_visual_target_graph() {
    constexpr std::uint32_t visual_type = 39U;
    constexpr std::uint32_t render_data_type = 43U;
    constexpr std::uint32_t target_type = 47U;
    std::vector<std::byte> batch;
    append_create(batch, 1U, visual_type);
    append_create(batch, 2U, visual_type);
    append_create(batch, 3U, render_data_type);
    append_create(batch, 4U, target_type);
    append_command(batch, command::visual_create, 1U);
    append_command(batch, command::visual_create, 2U);
    append_command(batch, command::visual_set_offset, 1U, 12.5, -3.0);
    append_command(batch, command::visual_set_alpha, 1U, 0.625);
    append_command(batch, command::visual_set_content, 1U, 3U);
    append_command(batch, command::visual_insert_child_at, 1U, 2U, 0U);

    const std::array<std::byte, 8> render_data{
        std::byte{8}, std::byte{0}, std::byte{0}, std::byte{0},
        std::byte{0x40}, std::byte{0}, std::byte{0}, std::byte{0}};
    std::vector<std::byte> render_packet;
    append_value(render_packet, static_cast<std::uint32_t>(command::render_data));
    append_value(render_packet, 3U);
    append_value(render_packet, static_cast<std::uint32_t>(render_data.size()));
    render_packet.insert(
        render_packet.end(), render_data.begin(), render_data.end());
    append_value(batch, static_cast<std::uint32_t>(render_packet.size() + 4U));
    batch.insert(batch.end(), render_packet.begin(), render_packet.end());

    append_command(
        batch,
        command::generic_target_create,
        4U,
        std::uint64_t{0U},
        std::uint64_t{0U},
        640U,
        480U,
        0U);
    append_command(batch, command::target_set_root, 4U, 1U);
    append_command(
        batch,
        command::target_set_clear_color,
        4U,
        0.1F,
        0.2F,
        0.3F,
        1.0F);
    append_command(batch, command::target_set_flags, 4U, 7U);

    channel state;
    batch_metrics metrics{};
    PROGPU_REQUIRE(state.apply(batch, &metrics) == status::success);
    PROGPU_REQUIRE(metrics.command_count == 15U);
    PROGPU_REQUIRE(metrics.supported_command_count == 15U);
    PROGPU_REQUIRE(metrics.created_resource_count == 4U);
    PROGPU_REQUIRE(state.resource_count() == 4U);
    PROGPU_REQUIRE(state.resource_generation(1U) == 6U);

    progpu::native::mil::visual_snapshot visual{};
    PROGPU_REQUIRE(state.try_get_visual(1U, visual));
    PROGPU_REQUIRE(visual.offset_x == 12.5);
    PROGPU_REQUIRE(visual.offset_y == -3.0);
    PROGPU_REQUIRE(visual.opacity == 0.625);
    PROGPU_REQUIRE(visual.content_handle == 3U);
    PROGPU_REQUIRE(visual.child_count == 1U);
    std::uint32_t child = 0U;
    PROGPU_REQUIRE(state.try_get_visual_child(1U, 0U, child));
    PROGPU_REQUIRE(child == 2U);

    progpu::native::mil::target_snapshot target{};
    PROGPU_REQUIRE(state.try_get_target(4U, target));
    PROGPU_REQUIRE(target.root_handle == 1U);
    PROGPU_REQUIRE(target.clear_red == 0.1F);
    PROGPU_REQUIRE(target.clear_green == 0.2F);
    PROGPU_REQUIRE(target.clear_blue == 0.3F);
    PROGPU_REQUIRE(target.clear_alpha == 1.0F);
    PROGPU_REQUIRE(target.flags == 7U);
    return true;
}

bool failed_batches_roll_back() {
    channel state;
    std::vector<std::byte> seed;
    append_create(seed, 1U, 39U);
    append_command(seed, command::visual_create, 1U);
    PROGPU_REQUIRE(state.apply(seed) == status::success);
    const auto generation = state.resource_generation(1U);

    std::vector<std::byte> invalid;
    append_command(invalid, command::visual_set_alpha, 1U, 0.25);
    append_command(invalid, command::visual_insert_child_at, 1U, 99U, 0U);
    PROGPU_REQUIRE(state.apply(invalid) == status::invalid_handle);
    progpu::native::mil::visual_snapshot snapshot{};
    PROGPU_REQUIRE(state.try_get_visual(1U, snapshot));
    PROGPU_REQUIRE(snapshot.opacity == 1.0);
    PROGPU_REQUIRE(state.resource_generation(1U) == generation);
    return true;
}

bool invalid_visual_graphs_fail_closed() {
    channel state;
    std::vector<std::byte> seed;
    for (std::uint32_t handle = 1U; handle <= 3U; ++handle) {
        append_create(seed, handle, 39U);
        append_command(seed, command::visual_create, handle);
    }
    append_command(seed, command::visual_insert_child_at, 1U, 2U, 0U);
    PROGPU_REQUIRE(state.apply(seed) == status::success);

    std::vector<std::byte> cycle;
    append_command(cycle, command::visual_insert_child_at, 2U, 1U, 0U);
    PROGPU_REQUIRE(state.apply(cycle) == status::invalid_graph);

    std::vector<std::byte> second_parent;
    append_command(
        second_parent, command::visual_insert_child_at, 3U, 2U, 0U);
    PROGPU_REQUIRE(state.apply(second_parent) == status::invalid_graph);
    progpu::native::mil::visual_snapshot root{};
    progpu::native::mil::visual_snapshot second{};
    PROGPU_REQUIRE(state.try_get_visual(1U, root));
    PROGPU_REQUIRE(state.try_get_visual(3U, second));
    PROGPU_REQUIRE(root.child_count == 1U);
    PROGPU_REQUIRE(second.child_count == 0U);
    return true;
}

bool solid_rectangle_compiles_to_semantic_scene() {
    constexpr std::uint32_t visual_type = 39U;
    constexpr std::uint32_t render_data_type = 43U;
    constexpr std::uint32_t target_type = 47U;
    constexpr std::uint32_t solid_brush_type = 75U;
    constexpr std::uint32_t root = 1U;
    constexpr std::uint32_t child = 2U;
    constexpr std::uint32_t content = 3U;
    constexpr std::uint32_t target = 4U;
    constexpr std::uint32_t brush = 5U;

    std::vector<std::byte> batch;
    append_create(batch, root, visual_type);
    append_create(batch, child, visual_type);
    append_create(batch, content, render_data_type);
    append_create(batch, target, target_type);
    append_create(batch, brush, solid_brush_type);
    append_command(batch, command::visual_create, root);
    append_command(batch, command::visual_create, child);
    append_command(batch, command::visual_set_offset, root, 10.0, 20.0);
    append_command(batch, command::visual_set_alpha, root, 0.8);
    append_command(batch, command::visual_set_offset, child, 3.0, 4.0);
    append_command(batch, command::visual_set_alpha, child, 0.5);
    append_command(batch, command::visual_set_content, child, content);
    append_command(batch, command::visual_insert_child_at, root, child, 0U);

    const progpu_native_color color{0.25F, 0.5F, 0.75F, 0.9F};
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        0.75,
        color,
        0U,
        0U,
        0U,
        0U);
    std::vector<std::byte> nested;
    append_command(nested, command::push_opacity, 0.5);
    append_command(
        nested,
        command::draw_rectangle,
        2.0,
        6.0,
        30.0,
        40.0,
        brush,
        0U);
    append_command(
        nested,
        command::draw_ellipse,
        5.0,
        9.0,
        7.0,
        11.0,
        brush,
        0U);
    append_command(
        nested,
        command::draw_rounded_rectangle,
        1.0,
        3.0,
        20.0,
        30.0,
        4.0,
        4.0,
        brush,
        0U);
    append_command(nested, command::pop);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        640U,
        480U,
        0U);
    append_command(batch, command::target_set_root, target, root);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 9001U, 7U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.visual_count == 2U);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.ellipse_count == 1U);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.brush_count == 1U);
    PROGPU_REQUIRE(metrics.maximum_visual_depth == 2U);
    PROGPU_REQUIRE(metrics.stream_bytes == stream.size());

    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    PROGPU_REQUIRE(header.scene_id == 9001U);
    PROGPU_REQUIRE(header.generation == 7U);
    PROGPU_REQUIRE(header.command_count == 9U);
    PROGPU_REQUIRE(header.resource_count == 7U);

    bool found_child_state = false;
    bool found_nested_opacity_state = false;
    bool found_rectangle = false;
    bool found_ellipse = false;
    bool found_rounded_rectangle = false;
    bool found_brush = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            const auto scene_state = read_value<progpu_native_scene_state>(
                stream, record.payload_offset);
            if (scene_state.transform.m31 == 13.0F &&
                scene_state.transform.m32 == 24.0F) {
                if (scene_state.opacity == 0.4F) {
                    found_child_state = true;
                } else if (scene_state.opacity == 0.2F) {
                    found_nested_opacity_state = true;
                }
            }
        } else if (
            record.kind == PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH) {
            const auto primitive =
                read_value<progpu_native_analytic_primitive>(
                    stream, record.payload_offset);
            if (primitive.kind == PROGPU_NATIVE_PRIMITIVE_RECTANGLE) {
                PROGPU_REQUIRE(primitive.x == 2.0F);
                PROGPU_REQUIRE(primitive.y == 6.0F);
                PROGPU_REQUIRE(primitive.width == 30.0F);
                PROGPU_REQUIRE(primitive.height == 40.0F);
                found_rectangle = true;
            } else if (primitive.kind == PROGPU_NATIVE_PRIMITIVE_ELLIPSE) {
                PROGPU_REQUIRE(primitive.x == -2.0F);
                PROGPU_REQUIRE(primitive.y == -2.0F);
                PROGPU_REQUIRE(primitive.width == 14.0F);
                PROGPU_REQUIRE(primitive.height == 22.0F);
                found_ellipse = true;
            } else if (
                primitive.kind ==
                PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE) {
                PROGPU_REQUIRE(primitive.x == 1.0F);
                PROGPU_REQUIRE(primitive.y == 3.0F);
                PROGPU_REQUIRE(primitive.width == 20.0F);
                PROGPU_REQUIRE(primitive.height == 30.0F);
                PROGPU_REQUIRE(primitive.corner_radius == 4.0F);
                found_rounded_rectangle = true;
            } else {
                PROGPU_REQUIRE(false);
            }
        } else if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            const auto scene_brush = read_value<progpu_native_scene_brush>(
                stream, record.payload_offset);
            PROGPU_REQUIRE(scene_brush.opacity == 0.75F);
            PROGPU_REQUIRE(scene_brush.colors[0].r == color.r);
            PROGPU_REQUIRE(scene_brush.colors[0].g == color.g);
            PROGPU_REQUIRE(scene_brush.colors[0].b == color.b);
            PROGPU_REQUIRE(scene_brush.colors[0].a == color.a);
            found_brush = true;
        }
    }
    PROGPU_REQUIRE(found_child_state);
    PROGPU_REQUIRE(found_nested_opacity_state);
    PROGPU_REQUIRE(found_rectangle);
    PROGPU_REQUIRE(found_ellipse);
    PROGPU_REQUIRE(found_rounded_rectangle);
    PROGPU_REQUIRE(found_brush);

    progpu_native_mil_channel* native_channel = nullptr;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_create(&native_channel) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_apply(
            native_channel, batch.data(), batch.size(), nullptr) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    progpu_native_mil_scene_metrics abi_metrics{};
    abi_metrics.struct_size = sizeof(abi_metrics);
    std::size_t required_bytes = 0U;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene(
            native_channel,
            target,
            9001U,
            7U,
            nullptr,
            0U,
            &required_bytes,
            &abi_metrics) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(required_bytes == stream.size());
    PROGPU_REQUIRE(abi_metrics.visual_count == 2U);
    PROGPU_REQUIRE(abi_metrics.rectangle_count == 1U);
    PROGPU_REQUIRE(abi_metrics.ellipse_count == 1U);
    PROGPU_REQUIRE(abi_metrics.rounded_rectangle_count == 1U);
    alignas(progpu_native_mil_scene_metrics)
        std::array<std::byte, sizeof(progpu_native_mil_scene_metrics)>
            legacy_metrics_storage{};
    legacy_metrics_storage.fill(std::byte{0x5a});
    constexpr std::uint32_t legacy_metrics_size = 32U;
    std::memcpy(
        legacy_metrics_storage.data(),
        &legacy_metrics_size,
        sizeof(legacy_metrics_size));
    std::size_t legacy_required_bytes = 0U;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene(
            native_channel,
            target,
            9001U,
            7U,
            nullptr,
            0U,
            &legacy_required_bytes,
            reinterpret_cast<progpu_native_mil_scene_metrics*>(
                legacy_metrics_storage.data())) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(legacy_required_bytes == stream.size());
    for (std::size_t index = legacy_metrics_size;
         index < legacy_metrics_storage.size();
         ++index) {
        PROGPU_REQUIRE(legacy_metrics_storage[index] == std::byte{0x5a});
    }
    std::vector<std::byte> abi_stream(required_bytes);
    std::size_t written_bytes = 0U;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene(
            native_channel,
            target,
            9001U,
            7U,
            abi_stream.data(),
            abi_stream.size() - 1U,
            &written_bytes,
            nullptr) == PROGPU_NATIVE_MIL_STATUS_CAPACITY_EXCEEDED);
    PROGPU_REQUIRE(written_bytes == required_bytes);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_build_scene(
            native_channel,
            target,
            9001U,
            7U,
            abi_stream.data(),
            abi_stream.size(),
            &written_bytes,
            nullptr) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(abi_stream == stream);
    progpu_native_mil_channel_destroy(native_channel);
    return true;
}

bool matrix_transform_scopes_compile_to_semantic_state() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t visual_transform = 5U;
    constexpr std::uint32_t scope_transform = 6U;
    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, visual_transform, 66U);
    append_create(batch, scope_transform, 66U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_offset, visual, 10.0, 20.0);
    append_command(
        batch,
        command::matrix_transform,
        visual_transform,
        2.0,
        0.0,
        0.0,
        2.0,
        1.0,
        2.0,
        0U);
    append_command(
        batch,
        command::matrix_transform,
        scope_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        3.0,
        4.0,
        0U);
    append_command(
        batch,
        command::visual_set_transform,
        visual,
        visual_transform);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{1.0F, 0.5F, 0.25F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::push_transform,
        scope_transform,
        0U);
    append_command(nested, command::push_opacity, 0.5);
    append_command(
        nested,
        command::draw_rectangle,
        1.0,
        2.0,
        3.0,
        4.0,
        brush,
        0U);
    append_command(nested, command::pop);
    append_command(nested, command::pop);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 7001U, 3U, stream) == status::success);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    PROGPU_REQUIRE(header.command_count == 7U);
    PROGPU_REQUIRE(header.resource_count == 5U);

    bool found_visual_state = false;
    bool found_transform_state = false;
    bool found_opacity_state = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind == PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            const auto scene_state = read_value<progpu_native_scene_state>(
                stream,
                record.payload_offset);
            if (scene_state.transform.m11 == 2.0F &&
                scene_state.transform.m22 == 2.0F &&
                scene_state.transform.m31 == 11.0F &&
                scene_state.transform.m32 == 22.0F &&
                scene_state.opacity == 1.0F) {
                found_visual_state = true;
            }
            if (scene_state.transform.m11 == 2.0F &&
                scene_state.transform.m22 == 2.0F &&
                scene_state.transform.m31 == 17.0F &&
                scene_state.transform.m32 == 30.0F) {
                if (scene_state.opacity == 1.0F) {
                    found_transform_state = true;
                } else if (scene_state.opacity == 0.5F) {
                    found_opacity_state = true;
                }
            }
        }
    }
    bool found_transformed_bounds = false;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
            PROGPU_REQUIRE(record.bounds_x == 19.0F);
            PROGPU_REQUIRE(record.bounds_y == 34.0F);
            PROGPU_REQUIRE(record.bounds_width == 6.0F);
            PROGPU_REQUIRE(record.bounds_height == 8.0F);
            found_transformed_bounds = true;
        }
    }
    PROGPU_REQUIRE(found_visual_state);
    PROGPU_REQUIRE(found_transform_state);
    PROGPU_REQUIRE(found_opacity_state);
    PROGPU_REQUIRE(found_transformed_bounds);

    const auto transform_generation =
        state.resource_generation(scope_transform);
    std::vector<std::byte> animated_update;
    append_command(
        animated_update,
        command::matrix_transform,
        scope_transform,
        1.0,
        0.0,
        0.0,
        1.0,
        99.0,
        99.0,
        1U);
    PROGPU_REQUIRE(
        state.apply(animated_update) == status::unsupported_command);
    PROGPU_REQUIRE(
        state.resource_generation(scope_transform) == transform_generation);

    std::vector<std::byte> wrong_type;
    append_command(
        wrong_type,
        command::visual_set_transform,
        visual,
        brush);
    PROGPU_REQUIRE(state.apply(wrong_type) == status::invalid_handle);
    return true;
}

bool solid_pen_line_compiles_to_geometry_scene() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    constexpr std::uint32_t brush = 4U;
    constexpr std::uint32_t pen = 5U;
    constexpr std::uint32_t transform = 6U;
    constexpr std::uint32_t dash = 7U;
    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_create(batch, brush, 75U);
    append_create(batch, pen, 85U);
    append_create(batch, transform, 66U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_offset, visual, 10.0, 20.0);
    append_command(
        batch,
        command::matrix_transform,
        transform,
        2.0,
        0.0,
        0.0,
        2.0,
        0.0,
        0.0,
        0U);
    append_command(
        batch,
        command::visual_set_transform,
        visual,
        transform);
    append_command(batch, command::visual_set_content, visual, content);
    append_command(
        batch,
        command::solid_color_brush,
        brush,
        1.0,
        progpu_native_color{0.25F, 0.5F, 0.75F, 1.0F},
        0U,
        0U,
        0U,
        0U);
    append_command(
        batch,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        0U,
        1U,
        2U,
        1U,
        0U,
        0U);
    std::vector<std::byte> nested;
    append_command(
        nested,
        command::draw_line,
        1.0,
        2.0,
        5.0,
        8.0,
        pen,
        0U);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        64U,
        64U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    progpu::native::mil::scene_metrics metrics{};
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 1U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.line_count == 1U);
    PROGPU_REQUIRE(metrics.brush_count == 1U);
    const auto header = read_value<progpu_native_scene_header>(stream, 0U);
    PROGPU_REQUIRE(header.command_count == 3U);
    PROGPU_REQUIRE(header.resource_count == 3U);

    bool found_line = false;
    for (std::uint32_t index = 0U; index < header.resource_count; ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        const auto primitive =
            read_value<progpu_native_geometry_primitive>(
                stream,
                record.payload_offset);
        PROGPU_REQUIRE(primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE);
        PROGPU_REQUIRE(primitive.p0.x == 1.0F);
        PROGPU_REQUIRE(primitive.p0.y == 2.0F);
        PROGPU_REQUIRE(primitive.p1.x == 5.0F);
        PROGPU_REQUIRE(primitive.p1.y == 8.0F);
        PROGPU_REQUIRE(primitive.stroke_thickness == 2.0F);
        PROGPU_REQUIRE(
            (primitive.flags & PROGPU_NATIVE_PRIMITIVE_START_CAP_MASK) ==
            (1U << PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT));
        PROGPU_REQUIRE(
            (primitive.flags & PROGPU_NATIVE_PRIMITIVE_END_CAP_MASK) ==
            (2U << PROGPU_NATIVE_PRIMITIVE_END_CAP_SHIFT));
        found_line = true;
    }
    PROGPU_REQUIRE(found_line);

    bool found_bounds = false;
    for (std::uint32_t index = 0U; index < header.command_count; ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) {
            PROGPU_REQUIRE(std::abs(record.bounds_x - 9.226499F) < 0.0001F);
            PROGPU_REQUIRE(std::abs(record.bounds_y - 21.2265F) < 0.0001F);
            PROGPU_REQUIRE(
                std::abs(record.bounds_width - 12.773501F) < 0.0001F);
            PROGPU_REQUIRE(
                std::abs(record.bounds_height - 16.7735F) < 0.0001F);
            found_bounds = true;
        }
    }
    PROGPU_REQUIRE(found_bounds);

    const auto pen_generation = state.resource_generation(pen);
    std::vector<std::byte> animated_pen;
    append_command(
        animated_pen,
        command::pen,
        pen,
        3.0,
        10.0,
        brush,
        1U,
        0U,
        0U,
        1U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(animated_pen) == status::unsupported_command);
    PROGPU_REQUIRE(state.resource_generation(pen) == pen_generation);

    std::vector<std::byte> dashed_pen;
    append_create(dashed_pen, dash, 84U);
    const std::array dash_intervals{2.0, 1.0};
    append_dash_style(dashed_pen, dash, 0.5, 0U, dash_intervals);
    append_command(
        dashed_pen,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        0U,
        0U,
        0U,
        1U,
        0U,
        dash);
    PROGPU_REQUIRE(state.apply(dashed_pen) == status::success);
    PROGPU_REQUIRE(state.resource_generation(pen) == pen_generation + 1U);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 2U, stream, &metrics) ==
        status::success);
    const auto dashed_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_dashed_line = false;
    for (std::uint32_t index = 0U;
        index < dashed_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            dashed_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            continue;
        }
        const auto stroke = read_value<progpu_native_scene_stroke>(
            stream,
            record.payload_offset);
        PROGPU_REQUIRE(stroke.kind == PROGPU_NATIVE_SCENE_STROKE_POLYLINE);
        PROGPU_REQUIRE(stroke.point_count == 2U);
        PROGPU_REQUIRE(stroke.dash_interval_count == 2U);
        PROGPU_REQUIRE(stroke.dash_offset == 0.5);
        PROGPU_REQUIRE(stroke.dash_cap == 1U);
        PROGPU_REQUIRE(
            read_value<double>(stream, record.auxiliary_offset + 16U) ==
            2.0);
        PROGPU_REQUIRE(
            read_value<double>(stream, record.auxiliary_offset + 24U) ==
            1.0);
        found_dashed_line = true;
    }
    PROGPU_REQUIRE(found_dashed_line);

    const auto dash_generation = state.resource_generation(dash);
    std::vector<std::byte> animated_dash;
    append_dash_style(animated_dash, dash, 0.0, 99U, dash_intervals);
    PROGPU_REQUIRE(
        state.apply(animated_dash) == status::unsupported_command);
    PROGPU_REQUIRE(state.resource_generation(dash) == dash_generation);

    std::vector<std::byte> invalid_dash;
    const std::array invalid_intervals{2.0, -1.0};
    append_dash_style(invalid_dash, dash, 0.0, 0U, invalid_intervals);
    PROGPU_REQUIRE(state.apply(invalid_dash) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(dash) == dash_generation);

    std::vector<std::byte> delete_referenced_dash;
    append_command(
        delete_referenced_dash,
        command::channel_delete_resource,
        dash,
        84U);
    PROGPU_REQUIRE(
        state.apply(delete_referenced_dash) == status::invalid_graph);

    std::vector<std::byte> rectangle_batch;
    std::vector<std::byte> dashed_rectangle;
    append_command(
        dashed_rectangle,
        command::draw_rectangle,
        1.0,
        2.0,
        4.0,
        6.0,
        brush,
        pen);
    append_render_data(rectangle_batch, content, dashed_rectangle);
    PROGPU_REQUIRE(state.apply(rectangle_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 3U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.line_count == 0U);
    const auto rectangle_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_closed_rectangle = false;
    for (std::uint32_t index = 0U;
        index < rectangle_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            rectangle_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            continue;
        }
        const auto stroke = read_value<progpu_native_scene_stroke>(
            stream,
            record.payload_offset);
        PROGPU_REQUIRE(
            (stroke.flags & PROGPU_NATIVE_POLYLINE_FLAG_CLOSED) != 0U);
        PROGPU_REQUIRE(stroke.point_count == 4U);
        PROGPU_REQUIRE(stroke.dash_interval_count == 2U);
        found_closed_rectangle = true;
    }
    PROGPU_REQUIRE(found_closed_rectangle);
    bool found_rectangle_stroke_bounds = false;
    for (std::uint32_t index = 0U;
        index < rectangle_header.command_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            rectangle_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH) {
            continue;
        }
        PROGPU_REQUIRE(record.bounds_x == 10.0F);
        PROGPU_REQUIRE(record.bounds_y == 22.0F);
        PROGPU_REQUIRE(record.bounds_width == 12.0F);
        PROGPU_REQUIRE(record.bounds_height == 16.0F);
        found_rectangle_stroke_bounds = true;
    }
    PROGPU_REQUIRE(found_rectangle_stroke_bounds);

    constexpr std::uint32_t solid_pen = 8U;
    std::vector<std::byte> solid_pen_batch;
    append_create(solid_pen_batch, solid_pen, 85U);
    append_command(
        solid_pen_batch,
        command::pen,
        solid_pen,
        2.0,
        10.0,
        brush,
        0U,
        0U,
        0U,
        1U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(solid_pen_batch) == status::success);
    std::vector<std::byte> ellipse_batch;
    std::vector<std::byte> ellipse;
    append_command(
        ellipse,
        command::draw_ellipse,
        3.0,
        4.0,
        2.0,
        1.0,
        brush,
        solid_pen);
    append_render_data(ellipse_batch, content, ellipse);
    PROGPU_REQUIRE(state.apply(ellipse_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 4U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.ellipse_count == 1U);
    const auto ellipse_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_ellipse_arc = false;
    for (std::uint32_t index = 0U;
        index < ellipse_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            ellipse_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        const auto primitive =
            read_value<progpu_native_geometry_primitive>(
                stream,
                record.payload_offset);
        if (primitive.kind != PROGPU_NATIVE_GEOMETRY_ARC) {
            continue;
        }
        PROGPU_REQUIRE(primitive.p0.x == 3.0F);
        PROGPU_REQUIRE(primitive.p0.y == 4.0F);
        PROGPU_REQUIRE(primitive.p1.x == 2.0F);
        PROGPU_REQUIRE(primitive.p2.y == 1.0F);
        PROGPU_REQUIRE(primitive.stroke_thickness == 2.0F);
        found_ellipse_arc = true;
    }
    PROGPU_REQUIRE(found_ellipse_arc);
    bool found_ellipse_stroke_bounds = false;
    for (std::uint32_t index = 0U;
        index < ellipse_header.command_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            ellipse_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) {
            continue;
        }
        PROGPU_REQUIRE(record.bounds_x == 10.0F);
        PROGPU_REQUIRE(record.bounds_y == 24.0F);
        PROGPU_REQUIRE(record.bounds_width == 12.0F);
        PROGPU_REQUIRE(record.bounds_height == 8.0F);
        found_ellipse_stroke_bounds = true;
    }
    PROGPU_REQUIRE(found_ellipse_stroke_bounds);

    std::vector<std::byte> rounded_batch;
    std::vector<std::byte> rounded;
    append_command(
        rounded,
        command::draw_rounded_rectangle,
        2.0,
        3.0,
        8.0,
        6.0,
        2.0,
        2.0,
        0U,
        solid_pen);
    append_render_data(rounded_batch, content, rounded);
    PROGPU_REQUIRE(state.apply(rounded_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 5U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    const auto rounded_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_rounded_stroke = false;
    for (std::uint32_t index = 0U;
        index < rounded_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            rounded_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH) {
            continue;
        }
        const auto primitive =
            read_value<progpu_native_analytic_primitive>(
                stream,
                record.payload_offset);
        if (primitive.kind !=
            PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE) {
            continue;
        }
        PROGPU_REQUIRE(primitive.x == 2.0F);
        PROGPU_REQUIRE(primitive.y == 3.0F);
        PROGPU_REQUIRE(primitive.width == 8.0F);
        PROGPU_REQUIRE(primitive.height == 6.0F);
        PROGPU_REQUIRE(primitive.corner_radius == 2.0F);
        PROGPU_REQUIRE(primitive.stroke_thickness == 2.0F);
        found_rounded_stroke = true;
    }
    PROGPU_REQUIRE(found_rounded_stroke);
    bool found_rounded_stroke_bounds = false;
    for (std::uint32_t index = 0U;
        index < rounded_header.command_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_command>(
            stream,
            rounded_header.command_offset +
                index * sizeof(progpu_native_scene_command));
        if (record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC) {
            continue;
        }
        PROGPU_REQUIRE(record.bounds_x == 12.0F);
        PROGPU_REQUIRE(record.bounds_y == 24.0F);
        PROGPU_REQUIRE(record.bounds_width == 20.0F);
        PROGPU_REQUIRE(record.bounds_height == 16.0F);
        found_rounded_stroke_bounds = true;
    }
    PROGPU_REQUIRE(found_rounded_stroke_bounds);

    std::vector<std::byte> dashed_ellipse_batch;
    std::vector<std::byte> dashed_ellipse;
    append_command(
        dashed_ellipse,
        command::draw_ellipse,
        3.0,
        4.0,
        2.0,
        1.0,
        brush,
        pen);
    append_render_data(dashed_ellipse_batch, content, dashed_ellipse);
    PROGPU_REQUIRE(state.apply(dashed_ellipse_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 6U, stream, &metrics) ==
        status::unsupported_command);

    std::vector<std::byte> dashed_rounded_batch;
    std::vector<std::byte> dashed_rounded;
    append_command(
        dashed_rounded,
        command::draw_rounded_rectangle,
        2.0,
        3.0,
        8.0,
        6.0,
        2.0,
        2.0,
        0U,
        pen);
    append_render_data(dashed_rounded_batch, content, dashed_rounded);
    PROGPU_REQUIRE(state.apply(dashed_rounded_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 7U, stream, &metrics) ==
        status::unsupported_command);

    constexpr std::uint32_t line_geometry = 9U;
    std::vector<std::byte> geometry_batch;
    append_create(geometry_batch, line_geometry, 68U);
    append_command(
        geometry_batch,
        command::line_geometry,
        line_geometry,
        1.0,
        2.0,
        5.0,
        8.0,
        transform,
        0U,
        0U);
    std::vector<std::byte> geometry_draw;
    append_command(
        geometry_draw,
        command::draw_geometry,
        brush,
        solid_pen,
        line_geometry,
        0U);
    append_render_data(geometry_batch, content, geometry_draw);
    PROGPU_REQUIRE(state.apply(geometry_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 8U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.line_count == 1U);
    const auto geometry_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    bool found_transformed_line_geometry = false;
    for (std::uint32_t index = 0U;
        index < geometry_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            geometry_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            continue;
        }
        const auto primitive = read_value<progpu_native_geometry_primitive>(
            stream,
            record.payload_offset);
        PROGPU_REQUIRE(primitive.kind == PROGPU_NATIVE_GEOMETRY_LINE);
        PROGPU_REQUIRE(primitive.transform.m11 == 2.0F);
        PROGPU_REQUIRE(primitive.transform.m22 == 2.0F);
        found_transformed_line_geometry = true;
    }
    PROGPU_REQUIRE(found_transformed_line_geometry);

    const auto geometry_generation =
        state.resource_generation(line_geometry);
    std::vector<std::byte> animated_geometry;
    append_command(
        animated_geometry,
        command::line_geometry,
        line_geometry,
        1.0,
        2.0,
        5.0,
        8.0,
        transform,
        1U,
        0U);
    PROGPU_REQUIRE(
        state.apply(animated_geometry) == status::unsupported_command);
    PROGPU_REQUIRE(
        state.resource_generation(line_geometry) == geometry_generation);

    constexpr std::uint32_t rectangle_geometry = 10U;
    constexpr std::uint32_t ellipse_geometry = 11U;
    std::vector<std::byte> primitive_geometry_batch;
    append_create(primitive_geometry_batch, rectangle_geometry, 69U);
    append_create(primitive_geometry_batch, ellipse_geometry, 70U);
    append_command(
        primitive_geometry_batch,
        command::rectangle_geometry,
        rectangle_geometry,
        2.0,
        2.0,
        3.0,
        4.0,
        12.0,
        8.0,
        transform,
        0U,
        0U,
        0U);
    append_command(
        primitive_geometry_batch,
        command::ellipse_geometry,
        ellipse_geometry,
        4.0,
        3.0,
        9.0,
        8.0,
        transform,
        0U,
        0U,
        0U);
    std::vector<std::byte> primitive_geometry_draw;
    append_command(
        primitive_geometry_draw,
        command::draw_geometry,
        brush,
        solid_pen,
        rectangle_geometry,
        0U);
    append_command(
        primitive_geometry_draw,
        command::draw_geometry,
        brush,
        solid_pen,
        ellipse_geometry,
        0U);
    append_render_data(
        primitive_geometry_batch,
        content,
        primitive_geometry_draw);
    PROGPU_REQUIRE(
        state.apply(primitive_geometry_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 9U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.rounded_rectangle_count == 1U);
    PROGPU_REQUIRE(metrics.ellipse_count == 1U);
    const auto primitive_geometry_header =
        read_value<progpu_native_scene_header>(stream, 0U);
    std::uint32_t transformed_analytic_count = 0U;
    for (std::uint32_t index = 0U;
        index < primitive_geometry_header.resource_count;
        ++index) {
        const auto record = read_value<progpu_native_scene_resource>(
            stream,
            primitive_geometry_header.resource_offset +
                index * sizeof(progpu_native_scene_resource));
        if (record.kind != PROGPU_NATIVE_SCENE_RESOURCE_ANALYTIC_BATCH) {
            continue;
        }
        const auto primitive = read_value<progpu_native_analytic_primitive>(
            stream,
            record.payload_offset);
        if (primitive.kind != PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE &&
            primitive.kind != PROGPU_NATIVE_PRIMITIVE_ELLIPSE) {
            continue;
        }
        PROGPU_REQUIRE(primitive.transform.m11 == 2.0F);
        PROGPU_REQUIRE(primitive.transform.m22 == 2.0F);
        ++transformed_analytic_count;
    }
    PROGPU_REQUIRE(transformed_analytic_count >= 3U);

    const auto rectangle_generation =
        state.resource_generation(rectangle_geometry);
    std::vector<std::byte> animated_rectangle_geometry;
    append_command(
        animated_rectangle_geometry,
        command::rectangle_geometry,
        rectangle_geometry,
        2.0,
        2.0,
        3.0,
        4.0,
        12.0,
        8.0,
        transform,
        1U,
        0U,
        0U);
    PROGPU_REQUIRE(
        state.apply(animated_rectangle_geometry) ==
        status::unsupported_command);
    PROGPU_REQUIRE(
        state.resource_generation(rectangle_geometry) ==
        rectangle_generation);

    std::vector<std::byte> invalid_cap;
    append_command(
        invalid_cap,
        command::pen,
        pen,
        2.0,
        10.0,
        brush,
        0U,
        4U,
        0U,
        1U,
        0U,
        0U);
    PROGPU_REQUIRE(state.apply(invalid_cap) == status::malformed_batch);
    PROGPU_REQUIRE(state.resource_generation(pen) == pen_generation + 1U);

    std::vector<std::byte> null_pen_batch;
    std::vector<std::byte> null_pen;
    append_command(
        null_pen,
        command::draw_line,
        1.0,
        2.0,
        5.0,
        8.0,
        0U,
        0U);
    append_render_data(null_pen_batch, content, null_pen);
    PROGPU_REQUIRE(state.apply(null_pen_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 2U, stream, &metrics) ==
        status::success);
    PROGPU_REQUIRE(metrics.line_count == 0U);

    std::vector<std::byte> missing_pen_batch;
    std::vector<std::byte> missing_pen;
    append_command(
        missing_pen,
        command::draw_line,
        1.0,
        2.0,
        5.0,
        8.0,
        99U,
        0U);
    append_render_data(missing_pen_batch, content, missing_pen);
    PROGPU_REQUIRE(state.apply(missing_pen_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 7002U, 3U, stream, &metrics) ==
        status::invalid_handle);
    return true;
}

bool render_data_scope_errors_fail_closed() {
    constexpr std::uint32_t visual = 1U;
    constexpr std::uint32_t content = 2U;
    constexpr std::uint32_t target = 3U;
    std::vector<std::byte> batch;
    append_create(batch, visual, 39U);
    append_create(batch, content, 43U);
    append_create(batch, target, 47U);
    append_command(batch, command::visual_create, visual);
    append_command(batch, command::visual_set_content, visual, content);
    std::vector<std::byte> nested;
    append_command(nested, command::push_opacity, 0.5);
    append_render_data(batch, content, nested);
    append_command(
        batch,
        command::generic_target_create,
        target,
        std::uint64_t{0U},
        std::uint64_t{0U},
        16U,
        16U,
        0U);
    append_command(batch, command::target_set_root, target, visual);

    channel state;
    PROGPU_REQUIRE(state.apply(batch) == status::success);
    std::vector<std::byte> stream;
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 1U, stream) ==
        status::invalid_graph);

    std::vector<std::byte> pop_batch;
    std::vector<std::byte> unmatched_pop;
    append_command(unmatched_pop, command::pop);
    append_render_data(pop_batch, content, unmatched_pop);
    PROGPU_REQUIRE(state.apply(pop_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 2U, stream) ==
        status::invalid_graph);

    std::vector<std::byte> unequal_batch;
    std::vector<std::byte> unequal_radius;
    append_command(
        unequal_radius,
        command::draw_rounded_rectangle,
        0.0,
        0.0,
        10.0,
        10.0,
        2.0,
        3.0,
        visual,
        0U);
    append_render_data(unequal_batch, content, unequal_radius);
    PROGPU_REQUIRE(state.apply(unequal_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 3U, stream) ==
        status::unsupported_command);

    std::vector<std::byte> null_transform_batch;
    std::vector<std::byte> null_transform;
    append_command(
        null_transform,
        command::push_transform,
        0U,
        0U);
    append_command(null_transform, command::pop);
    append_render_data(null_transform_batch, content, null_transform);
    PROGPU_REQUIRE(state.apply(null_transform_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 4U, stream) == status::success);

    std::vector<std::byte> missing_transform_batch;
    std::vector<std::byte> missing_transform;
    append_command(
        missing_transform,
        command::push_transform,
        99U,
        0U);
    append_command(missing_transform, command::pop);
    append_render_data(
        missing_transform_batch,
        content,
        missing_transform);
    PROGPU_REQUIRE(state.apply(missing_transform_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 5U, stream) ==
        status::invalid_handle);

    std::vector<std::byte> nonzero_padding_batch;
    std::vector<std::byte> nonzero_padding;
    append_command(
        nonzero_padding,
        command::push_transform,
        1U,
        1U);
    append_command(nonzero_padding, command::pop);
    append_render_data(
        nonzero_padding_batch,
        content,
        nonzero_padding);
    PROGPU_REQUIRE(state.apply(nonzero_padding_batch) == status::success);
    PROGPU_REQUIRE(
        state.build_scene(target, 1U, 6U, stream) ==
        status::malformed_batch);
    return true;
}

bool malformed_and_unsupported_packets_fail_closed() {
    channel state;
    const std::array malformed{
        std::byte{7}, std::byte{0}, std::byte{0}, std::byte{0},
        std::byte{1}, std::byte{0}, std::byte{0}, std::byte{0}};
    PROGPU_REQUIRE(state.apply(malformed) == status::malformed_batch);

    std::vector<std::byte> unknown;
    append_command(unknown, static_cast<command>(0x8eU));
    PROGPU_REQUIRE(state.apply(unknown) == status::unknown_command);

    std::vector<std::byte> unsupported;
    append_command(unsupported, command::draw_rectangle);
    batch_metrics metrics{};
    PROGPU_REQUIRE(
        state.apply(unsupported, &metrics) == status::unsupported_command);
    PROGPU_REQUIRE(metrics.unsupported_command_count == 1U);
    PROGPU_REQUIRE(state.resource_count() == 0U);
    return true;
}

bool c_abi_is_typed_and_size_versioned() {
    progpu_native_mil_channel* native_channel = nullptr;
    PROGPU_REQUIRE(
        progpu_native_mil_channel_create(&native_channel) ==
        PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(native_channel != nullptr);

    std::vector<std::byte> batch;
    append_create(batch, 17U, 39U);
    append_command(batch, command::visual_create, 17U);
    append_command(batch, command::visual_set_offset, 17U, 2.0, 4.0);
    progpu_native_mil_batch_metrics metrics{};
    metrics.struct_size = sizeof(metrics);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_apply(
            native_channel,
            batch.data(),
            batch.size(),
            &metrics) == PROGPU_NATIVE_MIL_STATUS_SUCCESS);
    PROGPU_REQUIRE(metrics.command_count == 3U);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_get_resource_count(native_channel) == 1U);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_get_resource_type(native_channel, 17U) ==
        39U);
    progpu_native_mil_visual_snapshot snapshot{};
    snapshot.struct_size = sizeof(snapshot);
    PROGPU_REQUIRE(
        progpu_native_mil_channel_get_visual(
            native_channel, 17U, &snapshot) == 1U);
    PROGPU_REQUIRE(snapshot.offset_x == 2.0);
    PROGPU_REQUIRE(snapshot.offset_y == 4.0);
    progpu_native_mil_channel_destroy(native_channel);
    return true;
}

} // namespace

int main() {
    PROGPU_REQUIRE(channel_retains_visual_target_graph());
    PROGPU_REQUIRE(failed_batches_roll_back());
    PROGPU_REQUIRE(invalid_visual_graphs_fail_closed());
    PROGPU_REQUIRE(solid_rectangle_compiles_to_semantic_scene());
    PROGPU_REQUIRE(matrix_transform_scopes_compile_to_semantic_state());
    PROGPU_REQUIRE(solid_pen_line_compiles_to_geometry_scene());
    PROGPU_REQUIRE(render_data_scope_errors_fail_closed());
    PROGPU_REQUIRE(malformed_and_unsupported_packets_fail_closed());
    PROGPU_REQUIRE(c_abi_is_typed_and_size_versioned());
    return 0;
}

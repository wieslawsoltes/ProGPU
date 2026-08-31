#include "progpu_native_mil.h"
#include "progpu_native_mil.hpp"

#include <algorithm>
#include <cstddef>
#include <cstring>
#include <memory>
#include <new>
#include <span>

struct progpu_native_mil_channel {
    progpu::native::mil::channel state;
};

namespace {

progpu_native_mil_status to_abi(
    progpu::native::mil::status value) noexcept {
    return static_cast<progpu_native_mil_status>(value);
}

void write_scene_metrics(
    progpu_native_mil_scene_metrics* destination,
    std::size_t caller_size,
    const progpu::native::mil::scene_metrics& source) noexcept {
    if (destination == nullptr) {
        return;
    }
    std::memset(
        destination,
        0,
        std::min(caller_size, sizeof(*destination)));
    destination->struct_size = sizeof(*destination);
    destination->visual_count = source.visual_count;
    destination->rectangle_count = source.rectangle_count;
    destination->brush_count = source.brush_count;
    destination->maximum_visual_depth = source.maximum_visual_depth;
    destination->ellipse_count = source.ellipse_count;
    destination->stream_bytes = source.stream_bytes;
    if (caller_size >=
        offsetof(
            progpu_native_mil_scene_metrics,
            rounded_rectangle_count) + sizeof(std::uint32_t)) {
        destination->rounded_rectangle_count =
            source.rounded_rectangle_count;
    }
    if (caller_size >=
        offsetof(progpu_native_mil_scene_metrics, line_count) +
            sizeof(std::uint32_t)) {
        destination->line_count = source.line_count;
    }
}

static_assert(sizeof(progpu_native_mil_scene_build_request) == 64U);
static_assert(sizeof(progpu_native_mil_scene_build_result) == 32U);

} // namespace

extern "C" {

progpu_native_mil_status progpu_native_mil_channel_create(
    progpu_native_mil_channel** channel) {
    if (channel == nullptr) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    *channel = nullptr;
    try {
        auto result = std::make_unique<progpu_native_mil_channel>();
        *channel = result.release();
        return PROGPU_NATIVE_MIL_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    } catch (...) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
}

void progpu_native_mil_channel_destroy(
    progpu_native_mil_channel* channel) {
    delete channel;
}

progpu_native_mil_status progpu_native_mil_channel_apply(
    progpu_native_mil_channel* channel,
    const void* batch,
    size_t batch_size,
    progpu_native_mil_batch_metrics* metrics) {
    if (channel == nullptr || (batch == nullptr && batch_size != 0U) ||
        (metrics != nullptr &&
         metrics->struct_size < sizeof(progpu_native_mil_batch_metrics))) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    progpu::native::mil::batch_metrics native_metrics{};
    const auto result = channel->state.apply(
        std::span<const std::byte>{
            static_cast<const std::byte*>(batch), batch_size},
        metrics == nullptr ? nullptr : &native_metrics);
    if (metrics != nullptr) {
        *metrics = {};
        metrics->struct_size = sizeof(*metrics);
        metrics->command_count = native_metrics.command_count;
        metrics->supported_command_count =
            native_metrics.supported_command_count;
        metrics->unsupported_command_count =
            native_metrics.unsupported_command_count;
        metrics->created_resource_count =
            native_metrics.created_resource_count;
        metrics->deleted_resource_count =
            native_metrics.deleted_resource_count;
        metrics->updated_resource_count =
            native_metrics.updated_resource_count;
        metrics->total_bytes = native_metrics.total_bytes;
    }
    return to_abi(result);
}

progpu_native_mil_status
progpu_native_mil_channel_set_bitmap_source_rgba8(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height,
    uint32_t row_bytes,
    const void* pixels,
    size_t pixel_size) {
    if (channel == nullptr || (pixels == nullptr && pixel_size != 0U)) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_bitmap_source_rgba8(
        handle,
        width,
        height,
        row_bytes,
        std::span<const std::byte>{
            static_cast<const std::byte*>(pixels), pixel_size}));
}

progpu_native_mil_status
progpu_native_mil_channel_set_bitmap_source_external_image(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height) {
    if (channel == nullptr) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_bitmap_source_external_image(
        handle, width, height));
}

progpu_native_mil_status
progpu_native_mil_channel_set_double_buffered_bitmap_rgba8(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height,
    uint32_t row_bytes,
    const void* pixels,
    size_t pixel_size) {
    if (channel == nullptr || (pixels == nullptr && pixel_size != 0U)) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_double_buffered_bitmap_rgba8(
        handle,
        width,
        height,
        row_bytes,
        std::span<const std::byte>{
            static_cast<const std::byte*>(pixels), pixel_size}));
}

progpu_native_mil_status
progpu_native_mil_channel_set_double_buffered_bitmap_external_image(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height) {
    if (channel == nullptr) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(
        channel->state.set_double_buffered_bitmap_external_image(
            handle, width, height));
}

progpu_native_mil_status
progpu_native_mil_channel_set_media_player_external_image(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height) {
    if (channel == nullptr) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_media_player_external_image(
        handle, width, height));
}

progpu_native_mil_status
progpu_native_mil_channel_set_d3d_image_external_image(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t width,
    uint32_t height,
    uint64_t content_version) {
    if (channel == nullptr) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_d3d_image_external_image(
        handle, width, height, content_version));
}

progpu_native_mil_status
progpu_native_mil_channel_set_drawing_image_bounds(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    double x,
    double y,
    double width,
    double height) {
    if (channel == nullptr) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_drawing_image_bounds(
        handle, x, y, width, height));
}

progpu_native_mil_status
progpu_native_mil_channel_set_drawing_group_bounds(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    double x,
    double y,
    double width,
    double height) {
    if (channel == nullptr) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_drawing_group_bounds(
        handle, x, y, width, height));
}

progpu_native_mil_status
progpu_native_mil_channel_set_visual_cache_bounds(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    double x,
    double y,
    double width,
    double height) {
    if (channel == nullptr) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_visual_cache_bounds(
        handle, x, y, width, height));
}

progpu_native_mil_status
progpu_native_mil_channel_set_viewport3d_scene(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    const progpu_native_scene_camera_3d* camera,
    progpu_native_image_rect viewport,
    const progpu_native_scene_mesh_3d* meshes,
    size_t mesh_count,
    const progpu_native_scene_mesh_3d_vertex* vertices,
    size_t vertex_count,
    const uint32_t* indices,
    size_t index_count) {
    if (channel == nullptr || camera == nullptr || meshes == nullptr ||
        vertices == nullptr || indices == nullptr || mesh_count == 0U ||
        vertex_count == 0U || index_count == 0U) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_viewport3d_scene(
        handle,
        *camera,
        viewport,
        std::span<const progpu_native_scene_mesh_3d>{meshes, mesh_count},
        std::span<const progpu_native_scene_mesh_3d_vertex>{
            vertices, vertex_count},
        std::span<const std::uint32_t>{indices, index_count}));
}

progpu_native_mil_status
progpu_native_mil_channel_set_viewport3d_scene_lights(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    const progpu_native_scene_camera_3d* camera,
    progpu_native_image_rect viewport,
    const progpu_native_scene_mesh_3d* meshes,
    size_t mesh_count,
    const progpu_native_scene_mesh_3d_vertex* vertices,
    size_t vertex_count,
    const uint32_t* indices,
    size_t index_count,
    const progpu_native_scene_light_3d* lights,
    size_t light_count) {
    if (channel == nullptr || camera == nullptr || meshes == nullptr ||
        vertices == nullptr || indices == nullptr || lights == nullptr ||
        mesh_count == 0U || vertex_count == 0U || index_count == 0U ||
        light_count == 0U) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_viewport3d_scene(
        handle,
        *camera,
        viewport,
        std::span<const progpu_native_scene_mesh_3d>{meshes, mesh_count},
        std::span<const progpu_native_scene_mesh_3d_vertex>{
            vertices, vertex_count},
        std::span<const std::uint32_t>{indices, index_count},
        std::span<const progpu_native_scene_light_3d>{lights, light_count}));
}

progpu_native_mil_status
progpu_native_mil_channel_set_viewport3d_scene_materials(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    const progpu_native_scene_camera_3d* camera,
    progpu_native_image_rect viewport,
    const progpu_native_scene_mesh_3d* meshes,
    size_t mesh_count,
    const progpu_native_scene_mesh_3d_vertex* vertices,
    size_t vertex_count,
    const uint32_t* indices,
    size_t index_count,
    const progpu_native_scene_light_3d* lights,
    size_t light_count,
    const progpu_native_scene_brush* materials,
    size_t material_count,
    const progpu_native_scene_gradient_stop* gradient_stops,
    size_t gradient_stop_count) {
    if (channel == nullptr || camera == nullptr || meshes == nullptr ||
        vertices == nullptr || indices == nullptr || materials == nullptr ||
        mesh_count == 0U || vertex_count == 0U || index_count == 0U ||
        material_count != mesh_count ||
        (light_count != 0U && lights == nullptr) ||
        (gradient_stop_count != 0U && gradient_stops == nullptr)) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_viewport3d_scene(
        handle,
        *camera,
        viewport,
        std::span<const progpu_native_scene_mesh_3d>{meshes, mesh_count},
        std::span<const progpu_native_scene_mesh_3d_vertex>{
            vertices, vertex_count},
        std::span<const std::uint32_t>{indices, index_count},
        std::span<const progpu_native_scene_light_3d>{lights, light_count},
        std::span<const progpu_native_scene_brush>{
            materials, material_count},
        std::span<const progpu_native_scene_gradient_stop>{
            gradient_stops, gradient_stop_count}));
}

progpu_native_mil_status
progpu_native_mil_channel_set_glyph_run_font_sfnt(
    progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t face_index,
    uint32_t style_simulations,
    const void* font_data,
    size_t font_size) {
    if (channel == nullptr || font_data == nullptr || font_size == 0U) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    return to_abi(channel->state.set_glyph_run_font_sfnt(
        handle,
        face_index,
        style_simulations,
        std::span<const std::byte>{
            static_cast<const std::byte*>(font_data), font_size}));
}

size_t progpu_native_mil_channel_get_resource_count(
    const progpu_native_mil_channel* channel) {
    return channel == nullptr ? 0U : channel->state.resource_count();
}

uint8_t progpu_native_mil_channel_has_resource(
    const progpu_native_mil_channel* channel,
    uint32_t handle) {
    return channel != nullptr && channel->state.has_resource(handle) ? 1U : 0U;
}

uint32_t progpu_native_mil_channel_get_resource_type(
    const progpu_native_mil_channel* channel,
    uint32_t handle) {
    return channel == nullptr ? 0U : channel->state.resource_type(handle);
}

uint64_t progpu_native_mil_channel_get_resource_generation(
    const progpu_native_mil_channel* channel,
    uint32_t handle) {
    return channel == nullptr
        ? 0U
        : channel->state.resource_generation(handle);
}

uint8_t progpu_native_mil_channel_get_visual(
    const progpu_native_mil_channel* channel,
    uint32_t handle,
    progpu_native_mil_visual_snapshot* snapshot) {
    if (channel == nullptr || snapshot == nullptr ||
        snapshot->struct_size < sizeof(*snapshot)) {
        return 0U;
    }
    progpu::native::mil::visual_snapshot source{};
    if (!channel->state.try_get_visual(handle, source)) {
        return 0U;
    }
    *snapshot = {};
    snapshot->struct_size = sizeof(*snapshot);
    snapshot->handle = source.handle;
    snapshot->offset_x = source.offset_x;
    snapshot->offset_y = source.offset_y;
    snapshot->opacity = source.opacity;
    snapshot->content_handle = source.content_handle;
    snapshot->child_count = source.child_count;
    return 1U;
}

uint8_t progpu_native_mil_channel_get_visual_child(
    const progpu_native_mil_channel* channel,
    uint32_t handle,
    uint32_t index,
    uint32_t* child_handle) {
    if (channel == nullptr || child_handle == nullptr) {
        return 0U;
    }
    return channel->state.try_get_visual_child(
        handle, index, *child_handle) ? 1U : 0U;
}

uint8_t progpu_native_mil_channel_get_target(
    const progpu_native_mil_channel* channel,
    uint32_t handle,
    progpu_native_mil_target_snapshot* snapshot) {
    if (channel == nullptr || snapshot == nullptr ||
        snapshot->struct_size < sizeof(*snapshot)) {
        return 0U;
    }
    progpu::native::mil::target_snapshot source{};
    if (!channel->state.try_get_target(handle, source)) {
        return 0U;
    }
    *snapshot = {};
    snapshot->struct_size = sizeof(*snapshot);
    snapshot->handle = source.handle;
    snapshot->root_handle = source.root_handle;
    snapshot->clear_red = source.clear_red;
    snapshot->clear_green = source.clear_green;
    snapshot->clear_blue = source.clear_blue;
    snapshot->clear_alpha = source.clear_alpha;
    snapshot->flags = source.flags;
    return 1U;
}

progpu_native_mil_status progpu_native_mil_channel_build_scene(
    const progpu_native_mil_channel* channel,
    uint32_t target_handle,
    uint64_t scene_id,
    uint64_t generation,
    void* destination,
    size_t destination_size,
    size_t* bytes_written,
    progpu_native_mil_scene_metrics* metrics) {
    constexpr std::size_t metrics_v1_size =
        offsetof(progpu_native_mil_scene_metrics, stream_bytes) +
        sizeof(std::uint64_t);
    const std::size_t caller_metrics_size =
        metrics == nullptr ? 0U : metrics->struct_size;
    if (channel == nullptr || bytes_written == nullptr ||
        (destination == nullptr && destination_size != 0U) ||
        (metrics != nullptr && caller_metrics_size < metrics_v1_size)) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }
    *bytes_written = 0U;
    progpu::native::mil::scene_metrics native_metrics{};
    std::vector<std::byte> stream;
    const auto result = channel->state.build_scene(
        target_handle,
        scene_id,
        generation,
        stream,
        metrics == nullptr ? nullptr : &native_metrics);
    write_scene_metrics(metrics, caller_metrics_size, native_metrics);
    if (result != progpu::native::mil::status::success) {
        return to_abi(result);
    }
    *bytes_written = stream.size();
    if (destination == nullptr && destination_size == 0U) {
        return PROGPU_NATIVE_MIL_STATUS_SUCCESS;
    }
    if (destination_size < stream.size()) {
        return PROGPU_NATIVE_MIL_STATUS_CAPACITY_EXCEEDED;
    }
    if (!stream.empty()) {
        std::memcpy(destination, stream.data(), stream.size());
    }
    return PROGPU_NATIVE_MIL_STATUS_SUCCESS;
}

progpu_native_mil_status
progpu_native_mil_channel_build_scene_with_request(
    progpu_native_mil_channel* channel,
    const progpu_native_mil_scene_build_request* request,
    void* destination,
    size_t destination_size,
    size_t* bytes_written,
    progpu_native_mil_scene_metrics* metrics,
    progpu_native_mil_scene_build_result* build_result) {
    constexpr std::size_t metrics_v1_size =
        offsetof(progpu_native_mil_scene_metrics, stream_bytes) +
        sizeof(std::uint64_t);
    const std::size_t caller_metrics_size =
        metrics == nullptr ? 0U : metrics->struct_size;
    const std::size_t caller_result_size =
        build_result == nullptr ? 0U : build_result->struct_size;
    if (channel == nullptr || request == nullptr ||
        request->struct_size < sizeof(*request) ||
        request->reserved0 != 0U || bytes_written == nullptr ||
        build_result == nullptr ||
        caller_result_size < sizeof(*build_result) ||
        (destination == nullptr && destination_size != 0U) ||
        (metrics != nullptr && caller_metrics_size < metrics_v1_size)) {
        return PROGPU_NATIVE_MIL_STATUS_INVALID_ARGUMENT;
    }

    *bytes_written = 0U;
    std::memset(
        build_result,
        0,
        std::min(caller_result_size, sizeof(*build_result)));
    build_result->struct_size = sizeof(*build_result);

    const progpu::native::mil::scene_build_request native_request{
        static_cast<progpu::native::mil::scene_build_request_flags>(
            request->flags),
        request->target_handle,
        request->scene_id,
        request->generation,
        request->dpi_scale_x,
        request->dpi_scale_y,
        request->monotonic_time_nanoseconds,
        request->request_serial};
    progpu::native::mil::scene_metrics native_metrics{};
    progpu::native::mil::scene_build_result native_result{};
    std::span<const std::byte> stream;
    const auto result = channel->state.build_scene(
        native_request,
        stream,
        metrics == nullptr ? nullptr : &native_metrics,
        &native_result);
    write_scene_metrics(metrics, caller_metrics_size, native_metrics);
    if (result != progpu::native::mil::status::success) {
        return to_abi(result);
    }

    build_result->flags = static_cast<std::uint32_t>(native_result.flags);
    build_result->request_serial = native_result.request_serial;
    build_result->next_due_time_nanoseconds =
        native_result.next_due_time_nanoseconds;
    build_result->stream_bytes = native_result.stream_bytes;
    *bytes_written = stream.size();
    if (destination == nullptr && destination_size == 0U) {
        return PROGPU_NATIVE_MIL_STATUS_SUCCESS;
    }
    if (destination_size < stream.size()) {
        return PROGPU_NATIVE_MIL_STATUS_CAPACITY_EXCEEDED;
    }
    if (!stream.empty()) {
        std::memcpy(destination, stream.data(), stream.size());
    }
    return PROGPU_NATIVE_MIL_STATUS_SUCCESS;
}

} // extern "C"

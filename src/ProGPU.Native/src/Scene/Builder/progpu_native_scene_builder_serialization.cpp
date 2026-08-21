#include "progpu_native_scene_builder.hpp"
#include "progpu_native_scene_builder_internal.hpp"

#include <algorithm>
#include <cstring>
#include <limits>
#include <new>

namespace progpu::native {

namespace {

constexpr std::uint64_t align8(std::uint64_t value) noexcept {
    return (value + 7U) & ~std::uint64_t{7U};
}

} // namespace

bool semantic_scene_builder::try_measure_stream(
    std::uint32_t& command_offset,
    std::uint32_t& resource_offset,
    std::uint32_t& arena_offset,
    std::uint32_t& total_size) const noexcept {
    command_offset = 0U;
    resource_offset = 0U;
    arena_offset = 0U;
    total_size = 0U;
    if (implementation_->scene_id == 0U ||
        implementation_->generation == 0U ||
        implementation_->stack_depth != 0U) {
        implementation_->error = implementation_->stack_depth == 0U
            ? scene_build_error::invalid_state
            : scene_build_error::unbalanced_stack;
        return false;
    }
    std::uint64_t previous_resource_id = 0U;
    for (const auto& resource : implementation_->resources) {
        if (resource.record.resource_id <= previous_resource_id ||
            resource.record.generation == 0U) {
            implementation_->error = scene_build_error::invalid_state;
            return false;
        }
        previous_resource_id = resource.record.resource_id;
    }
    if (implementation_->commands.size() > PROGPU_NATIVE_SCENE_MAX_COMMANDS ||
        implementation_->resources.size() > PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }

    const std::uint64_t command_bytes =
        implementation_->commands.size() *
        sizeof(progpu_native_scene_command);
    const std::uint64_t resource_bytes =
        implementation_->resources.size() *
        sizeof(progpu_native_scene_resource);
    const std::uint64_t measured_command_offset =
        align8(sizeof(progpu_native_scene_header));
    const std::uint64_t measured_resource_offset =
        align8(measured_command_offset + command_bytes);
    const std::uint64_t measured_arena_offset =
        align8(measured_resource_offset + resource_bytes);
    std::uint64_t cursor = measured_arena_offset;
    const auto append_size = [&cursor](std::size_t byte_count) noexcept {
        if (byte_count == 0U) {
            return true;
        }
        cursor = align8(cursor);
        return cursor <= PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES &&
            byte_count <= PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES - cursor &&
            (cursor += byte_count) <=
                std::numeric_limits<std::uint32_t>::max();
    };
    for (const auto& source : implementation_->resources) {
        const std::size_t payload_size = source.brush_table
            ? implementation_->brushes.size() *
                sizeof(progpu_native_scene_brush)
            : source.text_style_table
                ? implementation_->text_styles.size() *
                    sizeof(progpu_native_scene_text_style)
                : source.payload.size();
        const std::size_t auxiliary_size = source.brush_table
            ? implementation_->gradient_stops.size() *
                sizeof(progpu_native_scene_gradient_stop)
            : source.auxiliary.size();
        if (payload_size > std::numeric_limits<std::uint32_t>::max() ||
            auxiliary_size > std::numeric_limits<std::uint32_t>::max() ||
            !append_size(payload_size) || !append_size(auxiliary_size)) {
            return implementation_->fail(scene_build_error::capacity_exceeded);
        }
    }
    for (const auto& command : implementation_->commands) {
        if (command.payload.size() >
                std::numeric_limits<std::uint32_t>::max() ||
            !append_size(command.payload.size())) {
            return implementation_->fail(scene_build_error::capacity_exceeded);
        }
    }
    if (measured_command_offset > std::numeric_limits<std::uint32_t>::max() ||
        measured_resource_offset > std::numeric_limits<std::uint32_t>::max() ||
        measured_arena_offset > std::numeric_limits<std::uint32_t>::max() ||
        cursor > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES ||
        cursor > std::numeric_limits<std::uint32_t>::max()) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }

    command_offset = static_cast<std::uint32_t>(measured_command_offset);
    resource_offset = static_cast<std::uint32_t>(measured_resource_offset);
    arena_offset = static_cast<std::uint32_t>(measured_arena_offset);
    total_size = static_cast<std::uint32_t>(cursor);
    implementation_->error = scene_build_error::none;
    return true;
}

std::size_t semantic_scene_builder::required_stream_size() const noexcept {
    std::uint32_t command_offset = 0U;
    std::uint32_t resource_offset = 0U;
    std::uint32_t arena_offset = 0U;
    std::uint32_t total_size = 0U;
    return try_measure_stream(
        command_offset,
        resource_offset,
        arena_offset,
        total_size)
        ? total_size
        : 0U;
}

bool semantic_scene_builder::build_into(
    std::span<std::byte> destination,
    std::size_t& bytes_written,
    scene_build_metrics* metrics) const noexcept {
    bytes_written = 0U;
    if (metrics != nullptr) {
        *metrics = {};
    }
    std::uint32_t command_offset = 0U;
    std::uint32_t resource_offset = 0U;
    std::uint32_t arena_offset = 0U;
    std::uint32_t total_size = 0U;
    if (!try_measure_stream(
            command_offset,
            resource_offset,
            arena_offset,
            total_size)) {
        return false;
    }
    if (destination.size() < total_size) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }

    std::fill_n(destination.data(), total_size, std::byte{});
    std::uint64_t cursor = arena_offset;
    const auto append = [&](std::span<const std::byte> bytes,
                            std::uint32_t& offset) noexcept {
        if (bytes.empty()) {
            offset = 0U;
            return true;
        }
        cursor = align8(cursor);
        if (cursor > total_size || bytes.size() > total_size - cursor) {
            return false;
        }
        offset = static_cast<std::uint32_t>(cursor);
        std::memcpy(
            destination.data() + static_cast<std::size_t>(cursor),
            bytes.data(),
            bytes.size());
        cursor += bytes.size();
        return true;
    };

    const std::uint32_t resource_count = static_cast<std::uint32_t>(
        implementation_->resources.size());
    for (std::uint32_t index = 0U; index < resource_count; ++index) {
        const auto& source = implementation_->resources[index];
        auto resource = source.record;
        std::span<const std::byte> payload{
            source.payload.data(), source.payload.size()};
        std::span<const std::byte> auxiliary{
            source.auxiliary.data(), source.auxiliary.size()};
        if (source.brush_table) {
            payload = std::as_bytes(
                std::span<const progpu_native_scene_brush>{
                    implementation_->brushes.data(),
                    implementation_->brushes.size()});
            auxiliary = std::as_bytes(
                std::span<const progpu_native_scene_gradient_stop>{
                    implementation_->gradient_stops.data(),
                    implementation_->gradient_stops.size()});
        } else if (source.text_style_table) {
            payload = std::as_bytes(
                std::span<const progpu_native_scene_text_style>{
                    implementation_->text_styles.data(),
                    implementation_->text_styles.size()});
        }
        if (!append(payload, resource.payload_offset) ||
            !append(auxiliary, resource.auxiliary_offset)) {
            return implementation_->fail(scene_build_error::invalid_state);
        }
        resource.payload_size = static_cast<std::uint32_t>(payload.size());
        resource.auxiliary_size = static_cast<std::uint32_t>(auxiliary.size());
        std::memcpy(
            destination.data() + resource_offset +
                index * sizeof(resource),
            &resource,
            sizeof(resource));
    }

    const std::uint32_t command_count = static_cast<std::uint32_t>(
        implementation_->commands.size());
    for (std::uint32_t index = 0U; index < command_count; ++index) {
        const auto& source = implementation_->commands[index];
        auto command = source.record;
        const std::span<const std::byte> payload{
            source.payload.data(), source.payload.size()};
        if (!append(payload, command.payload_offset)) {
            return implementation_->fail(scene_build_error::invalid_state);
        }
        command.payload_size = static_cast<std::uint32_t>(payload.size());
        std::memcpy(
            destination.data() + command_offset +
                index * sizeof(command),
            &command,
            sizeof(command));
    }
    if (cursor != total_size) {
        return implementation_->fail(scene_build_error::invalid_state);
    }

    progpu_native_scene_header header{};
    header.struct_size = sizeof(header);
    header.magic = PROGPU_NATIVE_SCENE_STREAM_MAGIC;
    header.stream_version = PROGPU_NATIVE_SCENE_STREAM_VERSION;
    header.endian_marker = PROGPU_NATIVE_SCENE_STREAM_ENDIAN_MARKER;
    header.total_size = total_size;
    header.scene_id = implementation_->scene_id;
    header.generation = implementation_->generation;
    header.command_offset = command_offset;
    header.command_count = command_count;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.resource_offset = resource_offset;
    header.resource_count = resource_count;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.arena_offset = arena_offset;
    header.arena_size = total_size - arena_offset;
    std::memcpy(destination.data(), &header, sizeof(header));

    if (metrics != nullptr) {
        metrics->command_count = command_count;
        metrics->resource_count = resource_count;
        metrics->brush_count = static_cast<std::uint32_t>(
            implementation_->brushes.size());
        metrics->text_style_count = static_cast<std::uint32_t>(
            implementation_->text_styles.size());
        metrics->maximum_stack_depth = implementation_->maximum_stack_depth;
        metrics->arena_bytes = header.arena_size;
        metrics->stream_bytes = header.total_size;
    }
    bytes_written = total_size;
    implementation_->error = scene_build_error::none;
    return true;
}

bool semantic_scene_builder::build(
    std::vector<std::byte>& stream,
    scene_build_metrics* metrics) const noexcept {
    if (metrics != nullptr) {
        *metrics = {};
    }
    std::uint32_t command_offset = 0U;
    std::uint32_t resource_offset = 0U;
    std::uint32_t arena_offset = 0U;
    std::uint32_t total_size = 0U;
    if (!try_measure_stream(
            command_offset,
            resource_offset,
            arena_offset,
            total_size)) {
        return false;
    }
    try {
        std::vector<std::byte> built;
        const auto maximum_stream_size = static_cast<std::uint64_t>(
            PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES);
        const std::uint64_t reserved_arena_size =
            implementation_->arena_reserve >
                    maximum_stream_size - arena_offset
                ? maximum_stream_size
                : arena_offset + implementation_->arena_reserve;
        built.reserve(static_cast<std::size_t>(std::max<std::uint64_t>(
            total_size,
            reserved_arena_size)));
        built.resize(total_size);
        std::size_t bytes_written = 0U;
        if (!build_into(built, bytes_written, metrics) ||
            bytes_written != total_size) {
            return false;
        }
        stream.swap(built);
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

} // namespace progpu::native

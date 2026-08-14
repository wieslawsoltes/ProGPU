#include "progpu_native_scene_builder_internal.hpp"

#include "progpu_native_semantic_validation.hpp"

#include <cstring>
#include <limits>
#include <new>
#include <utility>

namespace progpu::native {
using scene_builder_detail::finite_rect;

bool semantic_scene_builder::add_rgba8_image(
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t row_bytes,
    std::span<const std::byte> pixels,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    const std::uint64_t minimum_row_bytes =
        static_cast<std::uint64_t>(width) * 4U;
    const std::uint64_t required_bytes = height == 0U
        ? 0U
        : static_cast<std::uint64_t>(row_bytes) * (height - 1U) +
            minimum_row_bytes;
    if (width == 0U || height == 0U || width > 16384U || height > 16384U ||
        row_bytes < minimum_row_bytes || required_bytes != pixels.size() ||
        required_bytes > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES ||
        implementation_->resources.size() >=
            PROGPU_NATIVE_SCENE_MAX_RESOURCES) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    try {
        implementation_->resources.reserve(
            implementation_->resources.size() + 1U);
        implementation::resource_entry resource{};
        resource.record.struct_size = sizeof(resource.record);
        resource.record.kind = PROGPU_NATIVE_SCENE_RESOURCE_IMAGE;
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload.assign(pixels.begin(), pixels.end());
        resource.rgba8_image = true;
        resource.image_width = width;
        resource.image_height = height;
        resource.image_row_bytes = row_bytes;
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

bool semantic_scene_builder::draw_image(
    std::uint32_t image_resource_index,
    const progpu_native_scene_image_draw& source,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index,
    const progpu_native_scene_image_sampling_options* sampling_options,
    const progpu_native_scene_image_color_matrix* color_matrix) noexcept {
    if (image_resource_index >= implementation_->resources.size() ||
        !implementation_->valid_state_index(state_resource_index) ||
        !finite_rect(bounds) ||
        implementation_->commands.size() >=
            PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    const auto& resource = implementation_->resources[image_resource_index];
    progpu_native_scene_image_draw image = source;
    image.struct_size = sizeof(image);
    image.reserved = 0U;
    const bool wants_sampling =
        image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
    const bool wants_matrix =
        (image.flags & PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX) != 0U;
    if (!resource.rgba8_image ||
        image.image_width != resource.image_width ||
        image.image_height != resource.image_height ||
        image.row_bytes != resource.image_row_bytes ||
        !semantic::is_valid_semantic_image(image, resource.payload.size()) ||
        wants_sampling != (sampling_options != nullptr) ||
        wants_matrix != (color_matrix != nullptr) ||
        (sampling_options != nullptr &&
            !semantic::is_valid_semantic_image_sampling_options(
                *sampling_options)) ||
        (color_matrix != nullptr &&
            !semantic::is_valid_semantic_image_color_matrix(*color_matrix))) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    const std::uint64_t payload_size = sizeof(image) +
        (sampling_options == nullptr ? 0U : sizeof(*sampling_options)) +
        (color_matrix == nullptr ? 0U : sizeof(*color_matrix));
    if (payload_size > std::numeric_limits<std::uint32_t>::max()) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }
    try {
        implementation_->commands.reserve(
            implementation_->commands.size() + 1U);
        implementation::command_entry command{};
        command.record.struct_size = sizeof(command.record);
        command.record.kind = PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE;
        command.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED;
        command.record.command_id = implementation_->commands.size() + 1U;
        command.record.state_index = state_resource_index;
        command.record.resource_index = image_resource_index;
        command.record.bounds_x = bounds.x;
        command.record.bounds_y = bounds.y;
        command.record.bounds_width = bounds.width;
        command.record.bounds_height = bounds.height;
        command.payload.resize(static_cast<std::size_t>(payload_size));
        std::size_t offset = 0U;
        std::memcpy(command.payload.data(), &image, sizeof(image));
        offset += sizeof(image);
        if (sampling_options != nullptr) {
            std::memcpy(
                command.payload.data() + offset,
                sampling_options,
                sizeof(*sampling_options));
            offset += sizeof(*sampling_options);
        }
        if (color_matrix != nullptr) {
            std::memcpy(
                command.payload.data() + offset,
                color_matrix,
                sizeof(*color_matrix));
        }
        implementation_->commands.push_back(std::move(command));
        implementation_->error = scene_build_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return implementation_->fail(scene_build_error::out_of_memory);
    } catch (...) {
        return implementation_->fail(scene_build_error::invalid_state);
    }
}

} // namespace progpu::native

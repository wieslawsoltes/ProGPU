#include "progpu_native_scene_builder_internal.hpp"

#include "progpu_native_semantic_image.hpp"
#include "progpu_native_semantic_validation.hpp"

#include <algorithm>
#include <cstring>
#include <limits>
#include <new>
#include <utility>

namespace progpu::native {
using scene_builder_detail::finite_rect;

namespace {

bool same_float(float left, float right) noexcept {
    return std::bit_cast<std::uint32_t>(left) ==
        std::bit_cast<std::uint32_t>(right);
}

bool same_transform(
    const progpu_native_affine_2d& left,
    const progpu_native_affine_2d& right) noexcept {
    return same_float(left.m11, right.m11) &&
        same_float(left.m12, right.m12) &&
        same_float(left.m21, right.m21) &&
        same_float(left.m22, right.m22) &&
        same_float(left.m31, right.m31) &&
        same_float(left.m32, right.m32);
}

bool can_share_image_patch_batch(
    const progpu_native_scene_image_draw& left,
    const progpu_native_scene_image_draw& right) noexcept {
    constexpr std::uint32_t patch_flag =
        PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH;
    constexpr std::uint32_t excluded_flags =
        PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX |
        PROGPU_NATIVE_SCENE_IMAGE_EFFECT;
    return (left.flags & excluded_flags) == 0U &&
        (right.flags & (excluded_flags | patch_flag)) == 0U &&
        left.image_width == right.image_width &&
        left.image_height == right.image_height &&
        left.row_bytes == right.row_bytes &&
        left.sampling == right.sampling &&
        left.max_anisotropy == right.max_anisotropy &&
        (left.flags & ~patch_flag) == right.flags &&
        same_float(left.opacity, right.opacity) &&
        same_transform(left.transform, right.transform);
}

bool same_sampling_options(
    const progpu_native_scene_image_sampling_options* left,
    const progpu_native_scene_image_sampling_options* right) noexcept {
    if (left == nullptr || right == nullptr) {
        return left == right;
    }
    return left->struct_size == right->struct_size &&
        left->flags == right->flags &&
        same_float(left->cubic_b, right->cubic_b) &&
        same_float(left->cubic_c, right->cubic_c);
}

progpu_native_scene_image_patch make_texture_patch(
    const progpu_native_scene_image_draw& image) noexcept {
    progpu_native_scene_image_patch patch{};
    patch.struct_size = sizeof(patch);
    patch.kind = PROGPU_NATIVE_SCENE_IMAGE_PATCH_TEXTURE;
    patch.source_rect = image.source_rect;
    patch.destination_rect = image.destination_rect;
    patch.transform = semantic_scene_builder::identity_transform();
    return patch;
}

progpu_native_image_rect union_bounds(
    progpu_native_image_rect left,
    progpu_native_image_rect right) noexcept {
    const float x = std::min(left.x, right.x);
    const float y = std::min(left.y, right.y);
    const float right_edge = std::max(
        left.x + left.width,
        right.x + right.width);
    const float bottom_edge = std::max(
        left.y + left.height,
        right.y + right.height);
    return {x, y, right_edge - x, bottom_edge - y};
}

} // namespace

bool semantic_scene_builder::implementation::try_merge_image_draw(
    std::uint32_t image_resource_index,
    const progpu_native_scene_image_draw& image,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index,
    const progpu_native_scene_image_sampling_options* sampling_options) {
    if (commands.empty()) {
        return false;
    }
    auto& previous = commands.back();
    if (previous.record.kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE ||
        previous.record.resource_index != image_resource_index ||
        previous.record.state_index != state_resource_index ||
        previous.payload.size() < sizeof(progpu_native_scene_image_draw)) {
        return false;
    }

    progpu_native_scene_image_draw previous_image{};
    std::memcpy(
        &previous_image,
        previous.payload.data(),
        sizeof(previous_image));
    if (!can_share_image_patch_batch(previous_image, image)) {
        return false;
    }
    const bool cubic = image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
    const std::size_t suffix_offset = sizeof(previous_image);
    progpu_native_scene_image_sampling_options previous_sampling{};
    const auto* previous_sampling_pointer =
        cubic ? &previous_sampling : nullptr;
    if (cubic) {
        if (previous.payload.size() <
            suffix_offset + sizeof(previous_sampling)) {
            return false;
        }
        std::memcpy(
            &previous_sampling,
            previous.payload.data() + suffix_offset,
            sizeof(previous_sampling));
    }
    if (!same_sampling_options(
            previous_sampling_pointer,
            sampling_options)) {
        return false;
    }

    const std::size_t batch_offset = suffix_offset +
        (cubic ? sizeof(previous_sampling) : 0U);
    const auto current_patch = make_texture_patch(image);
    if ((previous_image.flags & PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH) != 0U) {
        if (previous.payload.size() <
            batch_offset + sizeof(progpu_native_scene_image_patch_batch)) {
            return false;
        }
        progpu_native_scene_image_patch_batch batch{};
        std::memcpy(
            &batch,
            previous.payload.data() + batch_offset,
            sizeof(batch));
        if (batch.patch_count == 0U ||
            batch.patch_count >= PROGPU_NATIVE_SCENE_MAX_IMAGE_PATCHES) {
            return false;
        }
        const std::size_t expected_size = batch_offset + sizeof(batch) +
            static_cast<std::size_t>(batch.patch_count) *
                sizeof(progpu_native_scene_image_patch);
        if (previous.payload.size() != expected_size) {
            return false;
        }
        previous.payload.resize(
            expected_size + sizeof(progpu_native_scene_image_patch));
        std::memcpy(
            previous.payload.data() + expected_size,
            &current_patch,
            sizeof(current_patch));
        ++batch.patch_count;
        std::memcpy(
            previous.payload.data() + batch_offset,
            &batch,
            sizeof(batch));
    } else {
        const auto first_patch = make_texture_patch(previous_image);
        previous_image.flags |= PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH;
        const progpu_native_scene_image_patch_batch batch{
            sizeof(progpu_native_scene_image_patch_batch), 0U, 2U, 0U};
        std::vector<std::byte> payload(
            batch_offset + sizeof(batch) +
            2U * sizeof(progpu_native_scene_image_patch));
        std::size_t offset = 0U;
        const auto append = [&](const void* value, std::size_t size) {
            std::memcpy(payload.data() + offset, value, size);
            offset += size;
        };
        append(&previous_image, sizeof(previous_image));
        if (cubic) {
            append(&previous_sampling, sizeof(previous_sampling));
        }
        append(&batch, sizeof(batch));
        append(&first_patch, sizeof(first_patch));
        append(&current_patch, sizeof(current_patch));
        previous.payload = std::move(payload);
    }
    previous.record.payload_size =
        static_cast<std::uint32_t>(previous.payload.size());
    const auto merged_bounds = union_bounds(
        {previous.record.bounds_x,
            previous.record.bounds_y,
            previous.record.bounds_width,
            previous.record.bounds_height},
        bounds);
    previous.record.bounds_x = merged_bounds.x;
    previous.record.bounds_y = merged_bounds.y;
    previous.record.bounds_width = merged_bounds.width;
    previous.record.bounds_height = merged_bounds.height;
    return true;
}

bool semantic_scene_builder::add_rgba8_image(
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t row_bytes,
    std::span<const std::byte> pixels,
    std::uint32_t& resource_index) noexcept {
    return add_32bit_image(
        width, height, row_bytes, pixels, false, resource_index);
}

bool semantic_scene_builder::add_bgra8_image(
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t row_bytes,
    std::span<const std::byte> pixels,
    std::uint32_t& resource_index) noexcept {
    return add_32bit_image(
        width, height, row_bytes, pixels, true, resource_index);
}

bool semantic_scene_builder::add_32bit_image(
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t row_bytes,
    std::span<const std::byte> pixels,
    bool bgra8,
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
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
            (bgra8 ? PROGPU_NATIVE_SCENE_IMAGE_BGRA8 : 0U);
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.payload.assign(pixels.begin(), pixels.end());
        resource.rgba8_image = !bgra8;
        resource.bgra8_image = bgra8;
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

bool semantic_scene_builder::add_external_image(
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t& resource_index) noexcept {
    resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    if (width == 0U || height == 0U || width > 16384U || height > 16384U ||
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
        resource.record.flags = PROGPU_NATIVE_SCENE_RECORD_REQUIRED |
            PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE;
        resource.record.resource_id = implementation_->resources.size() + 1U;
        resource.record.generation = implementation_->generation;
        resource.image_width = width;
        resource.image_height = height;
        resource.image_row_bytes = width * 4U;
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

bool semantic_scene_builder::update_rgba8_image(
    std::uint32_t resource_index,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t row_bytes,
    std::span<const std::byte> pixels,
    std::uint64_t resource_generation) noexcept {
    return update_32bit_image(
        resource_index,
        width,
        height,
        row_bytes,
        pixels,
        resource_generation,
        false);
}

bool semantic_scene_builder::update_bgra8_image(
    std::uint32_t resource_index,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t row_bytes,
    std::span<const std::byte> pixels,
    std::uint64_t resource_generation) noexcept {
    return update_32bit_image(
        resource_index,
        width,
        height,
        row_bytes,
        pixels,
        resource_generation,
        true);
}

bool semantic_scene_builder::update_32bit_image(
    std::uint32_t resource_index,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t row_bytes,
    std::span<const std::byte> pixels,
    std::uint64_t resource_generation,
    bool bgra8) noexcept {
    if (resource_index >= implementation_->resources.size()) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    auto& resource = implementation_->resources[resource_index];
    if ((bgra8 ? !resource.bgra8_image : !resource.rgba8_image) ||
        width != resource.image_width ||
        height != resource.image_height ||
        row_bytes != resource.image_row_bytes ||
        pixels.size() != resource.payload.size() ||
        resource_generation <= resource.record.generation) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    try {
        std::vector<std::byte> next(pixels.begin(), pixels.end());
        resource.payload.swap(next);
        resource.record.generation = resource_generation;
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
    const progpu_native_scene_image_color_matrix* color_matrix,
    const progpu_native_scene_image_effect* effect) noexcept {
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
    if ((image.flags & PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH) != 0U) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    const bool wants_sampling =
        image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
    const bool wants_matrix =
        (image.flags & PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX) != 0U;
    const bool wants_effect =
        (image.flags & PROGPU_NATIVE_SCENE_IMAGE_EFFECT) != 0U;
    const bool external_image =
        (resource.record.flags & PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) != 0U;
    const std::uint64_t validation_bytes = external_image
        ? static_cast<std::uint64_t>(image.row_bytes) *
                (image.image_height - 1U) +
            static_cast<std::uint64_t>(image.image_width) * 4U
        : resource.payload.size();
    if ((!resource.rgba8_image && !resource.bgra8_image && !external_image) ||
        image.image_width != resource.image_width ||
        image.image_height != resource.image_height ||
        image.row_bytes != resource.image_row_bytes ||
        !semantic::is_valid_semantic_image(image, validation_bytes) ||
        wants_sampling != (sampling_options != nullptr) ||
        wants_matrix != (color_matrix != nullptr) ||
        wants_effect != (effect != nullptr) ||
        (sampling_options != nullptr &&
            !semantic::is_valid_semantic_image_sampling_options(
                *sampling_options)) ||
        (color_matrix != nullptr &&
            !semantic::is_valid_semantic_image_color_matrix(*color_matrix)) ||
        (effect != nullptr &&
            !semantic::is_valid_semantic_image_effect(*effect))) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    const std::uint64_t payload_size = sizeof(image) +
        (sampling_options == nullptr ? 0U : sizeof(*sampling_options)) +
        (color_matrix == nullptr ? 0U : sizeof(*color_matrix)) +
        (effect == nullptr ? 0U : sizeof(*effect));
    if (payload_size > std::numeric_limits<std::uint32_t>::max()) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }
    try {
        if (implementation_->try_merge_image_draw(
                image_resource_index,
                image,
                bounds,
                state_resource_index,
                sampling_options)) {
            implementation_->error = scene_build_error::none;
            return true;
        }
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
            offset += sizeof(*color_matrix);
        }
        if (effect != nullptr) {
            std::memcpy(
                command.payload.data() + offset,
                effect,
                sizeof(*effect));
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

bool semantic_scene_builder::draw_image_patches(
    std::uint32_t image_resource_index,
    const progpu_native_scene_image_draw& source,
    std::span<const progpu_native_scene_image_patch> patches,
    progpu_native_image_rect bounds,
    std::uint32_t state_resource_index,
    const progpu_native_scene_image_sampling_options* sampling_options,
    const progpu_native_scene_image_color_matrix* color_matrix,
    const progpu_native_scene_image_effect* effect) noexcept {
    if (image_resource_index >= implementation_->resources.size() ||
        !implementation_->valid_state_index(state_resource_index) ||
        !finite_rect(bounds) || patches.empty() ||
        patches.size() > PROGPU_NATIVE_SCENE_MAX_IMAGE_PATCHES ||
        implementation_->commands.size() >=
            PROGPU_NATIVE_SCENE_MAX_COMMANDS) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    const auto& resource = implementation_->resources[image_resource_index];
    progpu_native_scene_image_draw image = source;
    image.struct_size = sizeof(image);
    image.flags |= PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH;
    const bool wants_sampling =
        image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
    const bool wants_matrix =
        (image.flags & PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX) != 0U;
    const bool wants_effect =
        (image.flags & PROGPU_NATIVE_SCENE_IMAGE_EFFECT) != 0U;
    const bool external_image =
        (resource.record.flags & PROGPU_NATIVE_SCENE_EXTERNAL_IMAGE) != 0U;
    const std::uint64_t validation_bytes = external_image
        ? static_cast<std::uint64_t>(image.row_bytes) *
                (image.image_height - 1U) +
            static_cast<std::uint64_t>(image.image_width) * 4U
        : resource.payload.size();
    if ((!resource.rgba8_image && !resource.bgra8_image && !external_image) ||
        image.image_width != resource.image_width ||
        image.image_height != resource.image_height ||
        image.row_bytes != resource.image_row_bytes ||
        !semantic::is_valid_semantic_image(image, validation_bytes) ||
        wants_sampling != (sampling_options != nullptr) ||
        wants_matrix != (color_matrix != nullptr) ||
        wants_effect != (effect != nullptr) ||
        (sampling_options != nullptr &&
            !semantic::is_valid_semantic_image_sampling_options(
                *sampling_options)) ||
        (color_matrix != nullptr &&
            !semantic::is_valid_semantic_image_color_matrix(*color_matrix)) ||
        (effect != nullptr &&
            !semantic::is_valid_semantic_image_effect(*effect))) {
        return implementation_->fail(scene_build_error::invalid_argument);
    }
    const std::uint64_t payload_size = sizeof(image) +
        (sampling_options == nullptr ? 0U : sizeof(*sampling_options)) +
        (color_matrix == nullptr ? 0U : sizeof(*color_matrix)) +
        (effect == nullptr ? 0U : sizeof(*effect)) +
        sizeof(progpu_native_scene_image_patch_batch) + patches.size_bytes();
    if (payload_size > std::numeric_limits<std::uint32_t>::max()) {
        return implementation_->fail(scene_build_error::capacity_exceeded);
    }
    try {
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
        command.record.payload_size = static_cast<std::uint32_t>(payload_size);
        command.payload.resize(static_cast<std::size_t>(payload_size));
        std::size_t offset = 0U;
        const auto append = [&](const void* value, std::size_t size) {
            std::memcpy(command.payload.data() + offset, value, size);
            offset += size;
        };
        append(&image, sizeof(image));
        if (sampling_options != nullptr) {
            append(sampling_options, sizeof(*sampling_options));
        }
        if (color_matrix != nullptr) {
            append(color_matrix, sizeof(*color_matrix));
        }
        if (effect != nullptr) {
            append(effect, sizeof(*effect));
        }
        const progpu_native_scene_image_patch_batch batch{
            sizeof(progpu_native_scene_image_patch_batch),
            0U,
            static_cast<std::uint32_t>(patches.size()),
            0U};
        append(&batch, sizeof(batch));
        append(patches.data(), patches.size_bytes());

        semantic::semantic_image_options parsed{};
        command.record.payload_offset = 0U;
        if (offset != command.payload.size() ||
            !semantic::validate_image_draw_payload(
                command.payload.data(),
                command.record,
                image,
                validation_bytes,
                parsed) ||
            parsed.patch_count != patches.size()) {
            return implementation_->fail(scene_build_error::invalid_argument);
        }
        command.record.payload_offset = 0U;
        implementation_->commands.reserve(
            implementation_->commands.size() + 1U);
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

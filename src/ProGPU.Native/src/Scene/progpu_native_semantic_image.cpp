#include "progpu_native_semantic_image.hpp"

#include "progpu_native_semantic_validation.hpp"

#include <cmath>
#include <cstring>

namespace progpu::native::semantic {

static_assert(sizeof(progpu_native_scene_image_sampling_options) == 16U);
static_assert(sizeof(progpu_native_scene_image_color_matrix) == 96U);
static_assert(sizeof(progpu_native_scene_image_patch_batch) == 16U);
static_assert(sizeof(progpu_native_scene_image_patch) == 88U);

namespace {

bool valid_rect(const progpu_native_image_rect& rect, bool positive) noexcept {
    return std::isfinite(rect.x) && std::isfinite(rect.y) &&
        std::isfinite(rect.width) && std::isfinite(rect.height) &&
        (!positive || (rect.width > 0.0F && rect.height > 0.0F));
}

bool valid_transform(const progpu_native_affine_2d& value) noexcept {
    return std::isfinite(value.m11) && std::isfinite(value.m12) &&
        std::isfinite(value.m21) && std::isfinite(value.m22) &&
        std::isfinite(value.m31) && std::isfinite(value.m32);
}

bool valid_patch(
    const progpu_native_scene_image_patch& patch,
    const progpu_native_scene_image_draw& image) noexcept {
    const bool samples_texture =
        patch.kind != PROGPU_NATIVE_SCENE_IMAGE_PATCH_FIXED_COLOR;
    return patch.struct_size == sizeof(patch) && patch.flags == 0U &&
        patch.kind <= PROGPU_NATIVE_SCENE_IMAGE_PATCH_ATLAS_COLOR &&
        patch.color_blend_mode <= 28U && valid_rect(patch.source_rect, false) &&
        valid_rect(patch.destination_rect, true) &&
        valid_transform(patch.transform) &&
        std::isfinite(patch.color[0]) && std::isfinite(patch.color[1]) &&
        std::isfinite(patch.color[2]) && std::isfinite(patch.color[3]) &&
        (!samples_texture ||
            (patch.source_rect.x >= 0.0F && patch.source_rect.y >= 0.0F &&
                patch.source_rect.width > 0.0F &&
                patch.source_rect.height > 0.0F &&
                patch.source_rect.x + patch.source_rect.width <=
                    static_cast<float>(image.image_width) &&
                patch.source_rect.y + patch.source_rect.height <=
                    static_cast<float>(image.image_height)));
}

} // namespace

bool validate_image_draw_payload(
    const std::byte* bytes,
    const progpu_native_scene_command& command,
    const progpu_native_scene_image_draw& image,
    std::uint64_t pixel_bytes,
    semantic_image_options& options, std::uint32_t bytes_per_pixel) noexcept {
    options = {};
    if (bytes == nullptr || image.struct_size != sizeof(image) ||
        !is_valid_semantic_image(image, pixel_bytes, bytes_per_pixel)) {
        return false;
    }
    std::uint64_t cursor = command.payload_offset + sizeof(image);
    std::uint64_t expected_size = sizeof(image);
    if (image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC) {
        expected_size += sizeof(progpu_native_scene_image_sampling_options);
        if (command.payload_size < expected_size) {
            return false;
        }
        progpu_native_scene_image_sampling_options source{};
        std::memcpy(&source, bytes + cursor, sizeof(source));
        if (!is_valid_semantic_image_sampling_options(source)) {
            return false;
        }
        options.cubic_b = source.cubic_b;
        options.cubic_c = source.cubic_c;
        cursor += sizeof(source);
    }

    if ((image.flags & PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX) != 0U) {
        expected_size += sizeof(progpu_native_scene_image_color_matrix);
        if (command.payload_size < expected_size) {
            return false;
        }
        std::memcpy(&options.color_matrix, bytes + cursor,
            sizeof(options.color_matrix));
        const auto& matrix = options.color_matrix;
        if (!is_valid_semantic_image_color_matrix(matrix)) {
            return false;
        }
        options.has_color_matrix = true;
        options.luminance_to_alpha =
            (matrix.flags &
                PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX_LUMINANCE_TO_ALPHA) !=
            0U;
        cursor += sizeof(matrix);
    }
    if ((image.flags & PROGPU_NATIVE_SCENE_IMAGE_EFFECT) != 0U) {
        expected_size += sizeof(progpu_native_scene_image_effect);
        if (command.payload_size < expected_size) {
            return false;
        }
        std::memcpy(&options.effect, bytes + cursor, sizeof(options.effect));
        if (!is_valid_semantic_image_effect(options.effect)) {
            return false;
        }
        options.has_effect = true;
        cursor += sizeof(options.effect);
    }
    if ((image.flags & PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH) != 0U) {
        expected_size += sizeof(progpu_native_scene_image_patch_batch);
        if (command.payload_size < expected_size) {
            return false;
        }
        progpu_native_scene_image_patch_batch batch{};
        std::memcpy(&batch, bytes + cursor, sizeof(batch));
        if (batch.struct_size != sizeof(batch) || batch.flags != 0U ||
            batch.reserved != 0U || batch.patch_count == 0U ||
            batch.patch_count > PROGPU_NATIVE_SCENE_MAX_IMAGE_PATCHES ||
            batch.patch_count >
                (command.payload_size - expected_size) /
                    sizeof(progpu_native_scene_image_patch)) {
            return false;
        }
        cursor += sizeof(batch);
        expected_size += static_cast<std::uint64_t>(batch.patch_count) *
            sizeof(progpu_native_scene_image_patch);
        options.patch_bytes = bytes + cursor;
        options.patch_count = batch.patch_count;
        for (std::uint32_t index = 0U; index < batch.patch_count; ++index) {
            progpu_native_scene_image_patch patch{};
            std::memcpy(
                &patch,
                options.patch_bytes +
                    static_cast<std::size_t>(index) * sizeof(patch),
                sizeof(patch));
            if (!valid_patch(patch, image)) {
                return false;
            }
        }
    }
    return command.payload_size == expected_size;
}

void resolve_image_vertex_color(
    const progpu_native_scene_image_draw& image,
    bool has_effect,
    float (&color)[4]) noexcept {
    const bool premultiplied_source = has_effect ||
        (image.flags & PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED) != 0U;
    const bool ignore_source_alpha =
        (image.flags & PROGPU_NATIVE_SCENE_IMAGE_SOURCE_ALPHA_IGNORE) != 0U;
    color[0] = premultiplied_source ? image.opacity : 1.0F;
    color[1] = premultiplied_source ? 1.0F : 0.0F;
    color[2] = ignore_source_alpha
        ? -1.0F
        : (premultiplied_source ? image.opacity : 1.0F);
    color[3] = image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC
        ? -image.opacity
        : image.opacity;
}

void resolve_image_patch_vertex_attributes(
    const progpu_native_scene_image_draw& image,
    const progpu_native_scene_image_patch& patch,
    bool has_effect,
    float (&color)[4],
    float& patch_kind,
    float& color_blend_mode,
    float& patch_opacity) noexcept {
    const bool source_premultiplied =
        (image.flags & PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED) != 0U;
    color_blend_mode = 0.0F;
    patch_opacity = 1.0F;
    if (patch.kind == PROGPU_NATIVE_SCENE_IMAGE_PATCH_FIXED_COLOR) {
        const float alpha = patch.color[3] * image.opacity;
        color[0] = source_premultiplied ? patch.color[0] * alpha : patch.color[0];
        color[1] = source_premultiplied ? patch.color[1] * alpha : patch.color[1];
        color[2] = source_premultiplied ? patch.color[2] * alpha : patch.color[2];
        color[3] = alpha;
        patch_kind = source_premultiplied ? 2.0F : 1.0F;
        return;
    }
    if (patch.kind == PROGPU_NATIVE_SCENE_IMAGE_PATCH_ATLAS_COLOR) {
        const float alpha = patch.color[3];
        color[0] = patch.color[0] * alpha;
        color[1] = patch.color[1] * alpha;
        color[2] = patch.color[2] * alpha;
        color[3] = alpha;
        patch_kind = source_premultiplied ? 4.0F : 3.0F;
        color_blend_mode = static_cast<float>(patch.color_blend_mode);
        patch_opacity = image.sampling == PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC
            ? -image.opacity
            : image.opacity;
        return;
    }
    resolve_image_vertex_color(image, has_effect, color);
    patch_kind = 0.0F;
}

} // namespace progpu::native::semantic

#include "progpu_native_semantic_image_tests.hpp"

#include "progpu_native.h"
#include "progpu_native_semantic_image.hpp"
#include "progpu_native_semantic_validation.hpp"

#include <array>
#include <cstddef>
#include <cstring>
#include <limits>

namespace progpu::native::tests {

bool semantic_image_sampling_payload_is_exact_and_bounded() {
    static_assert(sizeof(progpu_native_scene_image_sampling_options) == 16U);
    static_assert(sizeof(progpu_native_scene_image_color_matrix) == 96U);
    static_assert(sizeof(progpu_native_scene_image_effect) == 304U);
    static_assert(sizeof(progpu_native_scene_image_patch_batch) == 16U);
    static_assert(sizeof(progpu_native_scene_image_patch) == 88U);
    struct sampler_case final {
        std::uint32_t sampling;
        bool mag_linear;
        bool min_linear;
        bool mip_linear;
    };
    constexpr std::array<sampler_case, 11U> sampler_cases{{
        {PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST, false, false, false},
        {PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR, true, true, false},
        {PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC, true, true, false},
        {PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR_MIPMAP, true, true, true},
        {PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_LINEAR_MIP_NEAREST,
            true, true, false},
        {PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_NEAREST_MIP_LINEAR,
            true, false, true},
        {PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_NEAREST_MIP_NEAREST,
            true, false, false},
        {PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_LINEAR_MIP_LINEAR,
            false, true, true},
        {PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_LINEAR_MIP_NEAREST,
            false, true, false},
        {PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_NEAREST_MIP_LINEAR,
            false, false, true},
        {PROGPU_NATIVE_IMAGE_SAMPLING_FANT, true, true, false}
    }};
    for (const auto& expected : sampler_cases) {
        semantic::semantic_image_sampler_options actual{};
        if (!semantic::resolve_semantic_image_sampler_options(
                expected.sampling, 1U, actual) ||
            actual.mag_linear != expected.mag_linear ||
            actual.min_linear != expected.min_linear ||
            actual.mip_linear != expected.mip_linear ||
            actual.max_anisotropy != 1U) {
            return false;
        }
    }
    semantic::semantic_image_sampler_options anisotropic{};
    if (!semantic::resolve_semantic_image_sampler_options(
            PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR_MIPMAP,
            0U,
            anisotropic) ||
        anisotropic.max_anisotropy != 1U ||
        !semantic::resolve_semantic_image_sampler_options(
            PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR_MIPMAP,
            16U,
            anisotropic) ||
        anisotropic.max_anisotropy != 16U ||
        semantic::resolve_semantic_image_sampler_options(
            PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR,
            2U,
            anisotropic) ||
        semantic::resolve_semantic_image_sampler_options(
            PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR_MIPMAP,
            17U,
            anisotropic) ||
        semantic::resolve_semantic_image_sampler_options(
            11U,
            1U,
            anisotropic)) {
        return false;
    }
    constexpr auto base_size = sizeof(progpu_native_scene_image_draw);
    std::array<std::byte,
        base_size + sizeof(progpu_native_scene_image_sampling_options) +
            sizeof(progpu_native_scene_image_color_matrix) +
            sizeof(progpu_native_scene_image_effect)> bytes{};
    progpu_native_scene_image_draw image{};
    image.struct_size = sizeof(image);
    image.image_width = 2U;
    image.image_height = 2U;
    image.row_bytes = 8U;
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
    image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    image.destination_rect = {1.0F, 2.0F, 8.0F, 8.0F};
    image.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    image.opacity = 1.0F;
    std::memcpy(bytes.data(), &image, sizeof(image));

    progpu_native_scene_image_sampling_options source{};
    source.struct_size = sizeof(source);
    source.cubic_b = 1.0F / 3.0F;
    source.cubic_c = 1.0F / 3.0F;
    std::memcpy(bytes.data() + base_size, &source, sizeof(source));

    progpu_native_scene_command command{};
    command.payload_size = base_size + sizeof(source);
    semantic::semantic_image_options parsed{};
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed) ||
        parsed.cubic_b != source.cubic_b ||
        parsed.cubic_c != source.cubic_c) {
        return false;
    }

    command.payload_size = base_size;
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    command.payload_size = base_size + sizeof(source);
    source.cubic_b = std::numeric_limits<float>::quiet_NaN();
    std::memcpy(bytes.data() + base_size, &source, sizeof(source));
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }

    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
    command.payload_size = base_size;
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }

    image.flags =
        static_cast<std::uint32_t>(PROGPU_NATIVE_IMAGE_ADDRESS_REPEAT) <<
        PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_SHIFT;
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    image.flags =
        (static_cast<std::uint32_t>(PROGPU_NATIVE_IMAGE_ADDRESS_REPEAT) <<
            PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_SHIFT) |
        (static_cast<std::uint32_t>(
            PROGPU_NATIVE_IMAGE_ADDRESS_MIRROR_REPEAT) <<
            PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_V_SHIFT) |
        PROGPU_NATIVE_SCENE_IMAGE_EXTENDED_SOURCE_RECT;
    image.source_rect = {-2.0F, -1.0F, 8.0F, 6.0F};
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_MASK |
        PROGPU_NATIVE_SCENE_IMAGE_EXTENDED_SOURCE_RECT;
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH |
        PROGPU_NATIVE_SCENE_IMAGE_EXTENDED_SOURCE_RECT;
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    image.flags = 0U;
    image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_ALPHA_IGNORE;
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    image.flags = 0U;

    image.flags = PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX;
    progpu_native_scene_image_color_matrix matrix{};
    matrix.struct_size = sizeof(matrix);
    matrix.red[0] = 1.0F;
    matrix.green[1] = 1.0F;
    matrix.blue[2] = 1.0F;
    matrix.alpha[3] = 1.0F;
    std::memcpy(bytes.data() + base_size, &matrix, sizeof(matrix));
    command.payload_size = base_size + sizeof(matrix);
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed) ||
        !parsed.has_color_matrix || parsed.color_matrix.red[0] != 1.0F) {
        return false;
    }
    matrix.flags =
        PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX_LUMINANCE_TO_ALPHA;
    std::memcpy(bytes.data() + base_size, &matrix, sizeof(matrix));
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed) ||
        !parsed.luminance_to_alpha) {
        return false;
    }
    matrix.flags = 1U << 31U;
    std::memcpy(bytes.data() + base_size, &matrix, sizeof(matrix));
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    matrix.flags = 0U;
    matrix.offset[0] = std::numeric_limits<float>::infinity();
    std::memcpy(bytes.data() + base_size, &matrix, sizeof(matrix));
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }

    image.flags = PROGPU_NATIVE_SCENE_IMAGE_EFFECT;
    progpu_native_scene_image_effect effect{};
    effect.effects0[1] = 1.0F;
    effect.effects0[2] = 1.0F;
    effect.effects1[3] = 1.0F;
    effect.texture0[0] = 2.0F;
    effect.texture0[1] = 2.0F;
    effect.struct_size = sizeof(effect);
    std::memcpy(bytes.data() + base_size, &effect, sizeof(effect));
    command.payload_size = base_size + sizeof(effect);
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed) ||
        !parsed.has_effect || parsed.effect.effects0[1] != 1.0F) {
        return false;
    }
    effect.effects1[2] = 32.0F;
    std::memcpy(bytes.data() + base_size, &effect, sizeof(effect));
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed) ||
        parsed.effect.effects1[2] != 32.0F) {
        return false;
    }
    effect.effects1[2] = 32.01F;
    std::memcpy(bytes.data() + base_size, &effect, sizeof(effect));
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    effect.effects1[2] = 0.0F;
    effect.effects1[3] = 0.0F;
    effect.flags0[0] = 1.0F;
    std::memcpy(bytes.data() + base_size, &effect, sizeof(effect));
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed) ||
        parsed.effect.flags0[0] != 1.0F) {
        return false;
    }
    effect.flags =
        PROGPU_NATIVE_SCENE_IMAGE_EFFECT_UNFILTERABLE_PLANAR;
    std::memcpy(bytes.data() + base_size, &effect, sizeof(effect));
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    effect.flags0[0] = 0.0F;
    std::memcpy(bytes.data() + base_size, &effect, sizeof(effect));
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    effect.flags = 0U;
    effect.flags0[0] = 0.5F;
    std::memcpy(bytes.data() + base_size, &effect, sizeof(effect));
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }
    effect.flags0[0] = 0.0F;
    effect.spherical0[0] = 0.5F;
    std::memcpy(bytes.data() + base_size, &effect, sizeof(effect));
    if (semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed)) {
        return false;
    }

    float color[4]{};
    image.flags = 0U;
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
    image.opacity = 0.25F;
    semantic::resolve_image_vertex_color(image, false, color);
    if (color[0] != 1.0F || color[1] != 0.0F || color[2] != 1.0F ||
        color[3] != 0.25F) {
        return false;
    }
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED;
    semantic::resolve_image_vertex_color(image, false, color);
    if (color[0] != 0.25F || color[1] != 1.0F || color[2] != 0.25F ||
        color[3] != 0.25F) {
        return false;
    }
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED |
        PROGPU_NATIVE_SCENE_IMAGE_SOURCE_ALPHA_IGNORE;
    semantic::resolve_image_vertex_color(image, false, color);
    if (color[0] != 0.25F || color[1] != 1.0F || color[2] != -1.0F ||
        color[3] != 0.25F) {
        return false;
    }
    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
    semantic::resolve_image_vertex_color(image, false, color);
    if (color[3] != -0.25F) {
        return false;
    }

    image.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
    image.opacity = 0.5F;
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED |
        PROGPU_NATIVE_SCENE_IMAGE_PATCH_BATCH;
    const progpu_native_scene_image_patch_batch batch{
        sizeof(progpu_native_scene_image_patch_batch), 0U, 3U, 0U};
    std::array<progpu_native_scene_image_patch, 3U> patches{};
    for (auto& patch : patches) {
        patch.struct_size = sizeof(patch);
        patch.source_rect = {0.0F, 0.0F, 1.0F, 1.0F};
        patch.destination_rect = {1.0F, 2.0F, 3.0F, 4.0F};
        patch.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    }
    patches[0].kind = PROGPU_NATIVE_SCENE_IMAGE_PATCH_TEXTURE;
    patches[1].kind = PROGPU_NATIVE_SCENE_IMAGE_PATCH_FIXED_COLOR;
    patches[1].color[0] = 0.5F;
    patches[1].color[1] = 0.25F;
    patches[1].color[2] = 0.75F;
    patches[1].color[3] = 0.5F;
    patches[2].kind = PROGPU_NATIVE_SCENE_IMAGE_PATCH_ATLAS_COLOR;
    patches[2].color_blend_mode = 24U;
    patches[2].color[0] = 0.5F;
    patches[2].color[1] = 0.25F;
    patches[2].color[2] = 0.75F;
    patches[2].color[3] = 0.5F;
    std::memcpy(bytes.data() + base_size, &batch, sizeof(batch));
    std::memcpy(
        bytes.data() + base_size + sizeof(batch),
        patches.data(),
        sizeof(patches));
    command.payload_size = base_size + sizeof(batch) + sizeof(patches);
    if (!semantic::validate_image_draw_payload(
            bytes.data(), command, image, 16U, parsed) ||
        parsed.patch_count != patches.size() || parsed.patch_bytes == nullptr) {
        return false;
    }

    float patch_kind = 0.0F;
    float blend_mode = 0.0F;
    float patch_opacity = 0.0F;
    semantic::resolve_image_patch_vertex_attributes(
        image,
        patches[1],
        false,
        color,
        patch_kind,
        blend_mode,
        patch_opacity);
    if (color[0] != 0.125F || color[1] != 0.0625F ||
        color[2] != 0.1875F || color[3] != 0.25F || patch_kind != 2.0F ||
        blend_mode != 0.0F || patch_opacity != 1.0F) {
        return false;
    }
    semantic::resolve_image_patch_vertex_attributes(
        image,
        patches[2],
        false,
        color,
        patch_kind,
        blend_mode,
        patch_opacity);
    if (color[0] != 0.25F || color[1] != 0.125F || color[2] != 0.375F ||
        color[3] != 0.5F || patch_kind != 4.0F || blend_mode != 24.0F ||
        patch_opacity != 0.5F) {
        return false;
    }

    patches[2].flags = 1U;
    std::memcpy(
        bytes.data() + base_size + sizeof(batch),
        patches.data(),
        sizeof(patches));
    return !semantic::validate_image_draw_payload(
        bytes.data(), command, image, 16U, parsed);
}

} // namespace progpu::native::tests

#include <array>
#include <cstddef>
#include <utility>

import progpu.native.scene_builder;

int main() {
    progpu::native::semantic_scene_builder builder(9001U, 3U);
    progpu::native::progpu_native_scene_brush gradient{};
    gradient.type =
        progpu::native::PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    gradient.opacity = 1.0F;
    gradient.end_point = {1.0F, 0.0F};
    gradient.stop_count = 2U;
    gradient.coordinate_transform0[0] = 1.0F;
    gradient.coordinate_transform1[1] = 1.0F;
    const progpu::native::progpu_native_scene_gradient_stop stops[2]{
        {{0.0F, 0.0F, 0.0F, 1.0F}, 0.0F, 0U, 0U, 0U},
        {{1.0F, 1.0F, 1.0F, 1.0F}, 1.0F, 0U, 0U, 0U}};
    unsigned int brush = progpu::native::PROGPU_NATIVE_SCENE_NO_INDEX;
    unsigned int image_index = progpu::native::PROGPU_NATIVE_SCENE_NO_INDEX;
    progpu::native::progpu_native_scene_image_draw image{};
    image.image_width = 2U;
    image.image_height = 2U;
    image.row_bytes = 8U;
    image.sampling = progpu::native::PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR_MIPMAP;
    image.max_anisotropy = 8U;
    image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    image.destination_rect = {0.0F, 0.0F, 8.0F, 8.0F};
    image.transform = builder.identity_transform();
    image.opacity = 1.0F;
    progpu::native::progpu_native_scene_image_patch patch{};
    patch.struct_size = sizeof(patch);
    patch.kind = progpu::native::PROGPU_NATIVE_SCENE_IMAGE_PATCH_TEXTURE;
    patch.source_rect = image.source_rect;
    patch.destination_rect = image.destination_rect;
    patch.transform = builder.identity_transform();
    if (builder.scene_id() != 9001U || builder.generation() != 3U ||
        !builder.add_brush(gradient, stops, brush) || brush != 0U ||
        !builder.add_external_image(2U, 2U, image_index) ||
        !builder.draw_image_patches(
            image_index,
            image,
            {&patch, 1U},
            {0.0F, 0.0F, 8.0F, 8.0F}) ||
        !builder.advance_generation(4U) || builder.generation() != 4U) {
        return 1;
    }
    progpu::native::progpu_native_scene_tile_composite tile{};
    tile.struct_size = sizeof(tile);
    tile.address_u = progpu::native::PROGPU_NATIVE_IMAGE_ADDRESS_REPEAT;
    tile.address_v = progpu::native::PROGPU_NATIVE_IMAGE_ADDRESS_MIRROR_REPEAT;
    tile.output_width = 32.0F;
    tile.output_height = 16.0F;
    tile.m11 = 0.125F;
    tile.m22 = 0.25F;
    unsigned int tile_index = progpu::native::PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!builder.add_tile_composite(tile, tile_index)) return 1;
    progpu::native::semantic_scene_builder guideline_source(9002U, 1U);
    const std::array coordinates{2.25};
    const std::array offsets{-0.125};
    unsigned int source_guidelines = progpu::native::PROGPU_NATIVE_SCENE_NO_INDEX;
    unsigned int copied_guidelines = progpu::native::PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!guideline_source.add_guideline_set_with_offsets(coordinates, {}, offsets, {}, source_guidelines) ||
        !builder.copy_guideline_set_from(guideline_source, source_guidelines, copied_guidelines) ||
        copied_guidelines == progpu::native::PROGPU_NATIVE_SCENE_NO_INDEX) return 1;
    progpu::native::progpu_native_point displacement{};
    if (!builder.try_uniform_guideline_translation(copied_guidelines, 2.0F, displacement) ||
        displacement.x != -0.0625F || displacement.y != 0.0F) return 1;
    std::array<std::byte, 4096U> stream{};
    const std::array<std::byte, 4U> coverage{
        std::byte{0}, std::byte{64}, std::byte{128}, std::byte{255}};
    unsigned int r8_index = progpu::native::PROGPU_NATIVE_SCENE_NO_INDEX;
    image.row_bytes = 2U;
    image.sampling = progpu::native::PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
    image.max_anisotropy = 1U;
    if (!builder.add_r8_image(2U, 2U, 2U, coverage, r8_index) ||
        !builder.draw_image(r8_index, image, image.destination_rect)) return 1;
    progpu::native::semantic_scene_builder child(9003U, 1U);
    unsigned int child_image = progpu::native::PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!child.add_r8_image(2U, 2U, 2U, coverage, child_image) ||
        !child.draw_image(child_image, image, image.destination_rect)) return 1;
    std::array<std::byte, 1024U> child_stream{};
    std::size_t child_written = 0U;
    if (!child.build_into(child_stream, child_written)) return 1;
    progpu::native::progpu_native_scene_picture_image picture{};
    picture.struct_size = sizeof(picture);
    picture.width = picture.height = 2U;
    picture.dpi_scale = 2.0F;
    picture.clear_color = {0.25F, 0.5F, 1.0F, 0.75F};
    unsigned int picture_index = progpu::native::PROGPU_NATIVE_SCENE_NO_INDEX;
    image.row_bytes = 8U;
    image.flags = progpu::native::PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED;
    if (!builder.add_picture_image(picture, {child_stream.data(), child_written}, picture_index) ||
        !builder.draw_image(picture_index, image, image.destination_rect)) return 1;
    const auto before_copy_size = builder.required_stream_size();
    std::array<std::byte, 4096U> before_copy{};
    std::array<std::byte, 4096U> after_failed_copy{};
    std::size_t before_copy_written = 0U;
    std::size_t after_failed_copy_written = 0U;
    if (!builder.build_into(before_copy, before_copy_written)) return 1;
    image.row_bytes = 2U;
    image.sampling = progpu::native::PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    image.flags = progpu::native::PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX;
    // Missing matrix fails after upload/layer append; transaction restores both.
    if (builder.copy_image_from_memory(image, progpu::native::PROGPU_NATIVE_SCENE_IMAGE_R8, coverage) ||
        builder.required_stream_size() != before_copy_size ||
        !builder.build_into(after_failed_copy, after_failed_copy_written) ||
        after_failed_copy_written != before_copy_written || before_copy != after_failed_copy) return 1;
    image.flags = 0U;
    if (!builder.copy_image_from_memory(image, progpu::native::PROGPU_NATIVE_SCENE_IMAGE_R8, coverage)) return 1;
    progpu::native::semantic_scene_builder staged_image(9004U);
    unsigned int staged_index = progpu::native::PROGPU_NATIVE_SCENE_NO_INDEX;
    if (!staged_image.add_r8_image(2U, 2U, 2U, coverage, staged_index) ||
        !builder.copy_image_from_builder(std::move(staged_image), staged_index, image)) return 1;
    if (!builder.build_into(before_copy, before_copy_written)) return 1;
    progpu::native::semantic_scene_builder rejected_image(9005U);
    if (!rejected_image.add_r8_image(2U, 2U, 2U, coverage, staged_index)) return 1;
    image.flags = progpu::native::PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX;
    if (builder.copy_image_from_builder(std::move(rejected_image), staged_index, image) ||
        !builder.build_into(after_failed_copy, after_failed_copy_written) ||
        after_failed_copy_written != before_copy_written || before_copy != after_failed_copy) return 1;
    const std::size_t required_size = builder.required_stream_size();
    std::size_t bytes_written = 0U;
    return required_size > 0U && required_size <= stream.size() &&
            builder.build_into(stream, bytes_written) &&
            bytes_written == required_size
        ? 0
        : 1;
}

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
    return builder.scene_id() == 9001U && builder.generation() == 3U &&
            builder.add_brush(gradient, stops, brush) && brush == 0U &&
            builder.add_external_image(2U, 2U, image_index) &&
            builder.draw_image_patches(
                image_index,
                image,
                {&patch, 1U},
                {0.0F, 0.0F, 8.0F, 8.0F}) &&
            builder.advance_generation(4U) && builder.generation() == 4U
        ? 0
        : 1;
}

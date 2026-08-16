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
    return builder.scene_id() == 9001U && builder.generation() == 3U &&
            builder.add_brush(gradient, stops, brush) && brush == 0U &&
            builder.advance_generation(4U) && builder.generation() == 4U
        ? 0
        : 1;
}

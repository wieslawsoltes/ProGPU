import progpu.native.scene_builder;

int main() {
    progpu::native::semantic_scene_builder builder(9001U, 3U);
    return builder.scene_id() == 9001U && builder.generation() == 3U
        ? 0
        : 1;
}

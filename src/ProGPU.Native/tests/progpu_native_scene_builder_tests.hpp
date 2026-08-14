#ifndef PROGPU_NATIVE_SCENE_BUILDER_TESTS_HPP
#define PROGPU_NATIVE_SCENE_BUILDER_TESTS_HPP

namespace progpu::native::tests {

bool semantic_scene_builder_is_deterministic_and_valid();
bool semantic_scene_builder_rejects_invalid_state();
bool semantic_scene_builder_reuses_retained_images();
bool semantic_scene_builder_records_styled_glyph_runs();
bool semantic_scene_builder_records_color_bitmap_glyphs();
bool semantic_scene_builder_records_layers_masks_and_effects();

} // namespace progpu::native::tests

#endif

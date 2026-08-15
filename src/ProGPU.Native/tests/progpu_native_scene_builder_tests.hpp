#ifndef PROGPU_NATIVE_SCENE_BUILDER_TESTS_HPP
#define PROGPU_NATIVE_SCENE_BUILDER_TESTS_HPP

namespace progpu::native::tests {

bool semantic_scene_builder_is_deterministic_and_valid();
bool semantic_scene_builder_rejects_invalid_state();
bool semantic_scene_builder_reuses_retained_images();
bool semantic_scene_builder_updates_retained_images_transactionally();
bool semantic_scene_builder_records_styled_glyph_runs();
bool semantic_scene_builder_records_native_shaped_runs();
bool semantic_scene_builder_records_color_bitmap_glyphs();
bool semantic_scene_builder_records_layers_masks_and_effects();
bool semantic_scene_builder_preserves_stable_resource_identities();
bool semantic_scene_builder_records_retained_3d_families();
bool semantic_scene_content_hashes_isolate_image_updates();

} // namespace progpu::native::tests

#endif

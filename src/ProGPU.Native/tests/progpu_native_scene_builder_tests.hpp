#ifndef PROGPU_NATIVE_SCENE_BUILDER_TESTS_HPP
#define PROGPU_NATIVE_SCENE_BUILDER_TESTS_HPP

namespace progpu::native::tests {

bool semantic_scene_builder_is_deterministic_and_valid();
bool semantic_scene_builder_rejects_invalid_state();
bool semantic_scene_builder_reuses_retained_images();

} // namespace progpu::native::tests

#endif

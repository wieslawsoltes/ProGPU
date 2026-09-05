module;

#include "progpu_native_scene_builder.hpp"

export module progpu.native.scene_builder;

export namespace progpu::native {

using ::progpu::native::scene_build_error;
using ::progpu::native::scene_build_metrics;
using ::progpu::native::shaped_text_scene_options;
using ::progpu::native::semantic_scene_builder;

using ::progpu_native_point_3d;
using ::progpu_native_image_rect;
using ::progpu_native_scene_image_draw;
using ::progpu_native_scene_image_patch;
using ::progpu_native_scene_image_patch_batch;
using ::progpu_native_scene_image_sampling_options;
using ::progpu_native_scene_image_color_matrix;
using ::progpu_native_scene_image_effect;
using ::progpu_native_scene_brush;
using ::progpu_native_scene_gradient_stop;
using ::progpu_native_scene_brush_kind;
using ::PROGPU_NATIVE_SCENE_BRUSH_SOLID;
using ::PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
using ::PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT;
using ::PROGPU_NATIVE_SCENE_BRUSH_HATCH_PATTERN;
using ::PROGPU_NATIVE_SCENE_BRUSH_CROSS_HATCH;
using ::PROGPU_NATIVE_SCENE_BRUSH_TWO_POINT_CONICAL_GRADIENT;
using ::PROGPU_NATIVE_SCENE_BRUSH_SWEEP_GRADIENT;
using ::PROGPU_NATIVE_SCENE_BRUSH_PERLIN_NOISE;
using ::PROGPU_NATIVE_SCENE_BRUSH_HATCH_PATTERN_SET;
using ::PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
using ::PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
using ::PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
using ::PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR_MIPMAP;
using ::PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_LINEAR_MIP_NEAREST;
using ::PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_NEAREST_MIP_LINEAR;
using ::PROGPU_NATIVE_IMAGE_SAMPLING_MAG_LINEAR_MIN_NEAREST_MIP_NEAREST;
using ::PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_LINEAR_MIP_LINEAR;
using ::PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_LINEAR_MIP_NEAREST;
using ::PROGPU_NATIVE_IMAGE_SAMPLING_MAG_NEAREST_MIN_NEAREST_MIP_LINEAR;
using ::PROGPU_NATIVE_SCENE_IMAGE_PATCH_TEXTURE;
using ::PROGPU_NATIVE_SCENE_IMAGE_PATCH_FIXED_COLOR;
using ::PROGPU_NATIVE_SCENE_IMAGE_PATCH_ATLAS_COLOR;
using ::PROGPU_NATIVE_SCENE_NO_INDEX;
using ::progpu_native_float_4;
using ::progpu_native_matrix_4x4;
using ::progpu_native_hit_test_primitive;
using ::progpu_native_hit_test_node;
using ::progpu_native_scene_hit_test_index;
using ::progpu_native_scene_camera_3d;
using ::progpu_native_scene_line_3d;
using ::progpu_native_scene_mesh_3d_vertex;
using ::progpu_native_scene_mesh_3d;

} // namespace progpu::native
